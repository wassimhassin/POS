using System.Net;
using System.Net.Http.Json;
using Npgsql;
using Pos.Core.Gateway;
using Pos.Gateway.Tests;
using Pos.Sync;

namespace Pos.Cloud.Tests;

/// <summary>
/// Organization A: business A1 (branches "A1 Centre", "A1 Nord") and business A2 (branch "A2 Plage", with its own
/// catalog prices). Organization B: business B1 (branch "B1 Port"). Each branch has its own real gateway.
/// </summary>
public sealed class MultiTenantTests : IAsyncLifetime
{
    private readonly CloudProcess _cloud = new();
    private readonly List<BranchUnderTest> _branches = [];
    private Guid _orgA;
    private Guid _orgB;
    private Guid _a1;
    private Guid _a2;
    private Guid _b1;

    public async Task InitializeAsync()
    {
        await _cloud.StartAsync();
        _orgA = _cloud.CreateOrganization("Groupe A");
        _orgB = _cloud.CreateOrganization("Groupe B");
        _a1 = _cloud.CreateBusiness(_orgA, "A1 Café");
        _a2 = _cloud.CreateBusiness(_orgA, "A2 Restaurant");
        _b1 = _cloud.CreateBusiness(_orgB, "B1 Brasserie");
    }

    public async Task DisposeAsync()
    {
        foreach (var branch in _branches)
        {
            await branch.DisposeAsync();
        }

        await _cloud.DisposeAsync();
    }

    private async Task<BranchUnderTest> BranchAsync(Guid organization, Guid business, string name)
    {
        var branch = await BranchUnderTest.CreateAsync(_cloud, organization, business, name);
        _branches.Add(branch);
        return branch;
    }

    [Fact]
    public async Task Two_branches_per_business_and_two_businesses_per_organization_stay_isolated()
    {
        var centre = await BranchAsync(_orgA, _a1, "A1 Centre");
        var nord = await BranchAsync(_orgA, _a1, "A1 Nord");
        var plage = await BranchAsync(_orgA, _a2, "A2 Plage");
        var port = await BranchAsync(_orgB, _b1, "B1 Port");

        // A2 has its own local catalog (price and name), published one-way to the cloud.
        plage.ExecuteGatewaySql("UPDATE products SET price_minor = 3000, name = 'Expresso maison' WHERE id = 'p-espresso';");

        var placed = new Dictionary<BranchUnderTest, List<SharedOrder>>();
        foreach (var (branch, count) in new[] { (centre, 3), (nord, 2), (plage, 2), (port, 1) })
        {
            var terminal = await branch.TerminalAsync("Caisse 1");
            placed[branch] = [];
            for (var i = 0; i < count; i++)
            {
                placed[branch].Add(await BranchUnderTest.PlaceAsync(terminal, submit: i != 1));
            }
        }

        await Wait.UntilAsync(() => plage.Gateway.Count("SELECT COUNT(*) FROM sync_outbox WHERE type = 'catalog_published'") == 2, "A2 catalog change published");
        foreach (var (branch, orders) in placed)
        {
            await branch.WaitSyncedAsync(_cloud);
            await branch.AssertCloudMatchesAsync(_cloud, orders); // each branch holds exactly its own orders
            Assert.Equal(1, orders.Min(o => o.Number)); // numbering is per branch: every branch has its own n°1
        }

        Assert.Equal(3000, placed[plage][0].Lines.Single().UnitPriceMinor);
        Assert.Equal(2500, placed[centre][0].Lines.Single().UnitPriceMinor);

        // Branch-scoped key: its branch only.
        await AssertVisible(_cloud.ReportKey(_orgA, _a1, centre.BranchId),
            visible: [$"branches/{centre.BranchId:D}/orders", $"branches/{centre.BranchId:D}"],
            hidden: [$"branches/{nord.BranchId:D}/orders", $"businesses/{_a1:D}", $"organizations/{_orgA:D}", $"branches/{plage.BranchId:D}/catalog"]);

        // Business-scoped key: both branches of A1, nothing of A2 or of organization B.
        var a1Key = _cloud.ReportKey(_orgA, _a1);
        await AssertVisible(a1Key,
            visible: [$"businesses/{_a1:D}", $"branches/{centre.BranchId:D}/orders", $"branches/{nord.BranchId:D}/catalog"],
            hidden: [$"businesses/{_a2:D}", $"branches/{plage.BranchId:D}/orders", $"organizations/{_orgA:D}", $"branches/{port.BranchId:D}/orders"]);
        using (var http = _cloud.Http(a1Key))
        {
            var a1 = (await http.GetFromJsonAsync<BusinessReport>($"v1/reports/businesses/{_a1:D}", SyncProtocol.Json))!;
            Assert.Equal(new[] { centre.BranchId.ToString("D"), nord.BranchId.ToString("D") }.Order(StringComparer.Ordinal), a1.Branches.Select(b => b.Id).Order(StringComparer.Ordinal));
            var centreReport = a1.Branches.Single(b => b.Id == centre.BranchId.ToString("D"));
            Assert.Equal(3, centreReport.OrderCount);
            Assert.Equal(2, centreReport.SubmittedCount);
            Assert.Equal(placed[centre].Where(o => o.Status == "submitted").Sum(o => o.TotalMinor), centreReport.SubmittedTotalMinor);
            Assert.Equal(centre.OutboxCount, centreReport.LastSequence);
        }

        // Organization-scoped key: all of A, nothing of B.
        var orgAKey = _cloud.ReportKey(_orgA);
        await AssertVisible(orgAKey,
            visible: [$"organizations/{_orgA:D}", $"businesses/{_a2:D}", $"branches/{plage.BranchId:D}/catalog"],
            hidden: [$"organizations/{_orgB:D}", $"businesses/{_b1:D}", $"branches/{port.BranchId:D}/orders"]);
        using (var http = _cloud.Http(orgAKey))
        {
            var orgA = (await http.GetFromJsonAsync<OrganizationReport>($"v1/reports/organizations/{_orgA:D}", SyncProtocol.Json))!;
            Assert.Equal(2, orgA.Businesses.Count);
            Assert.Equal(3, orgA.Businesses.Sum(b => b.Branches.Count));
            Assert.Equal(7, orgA.Businesses.Sum(b => b.Branches.Sum(r => r.OrderCount)));

            // Catalogs are per branch: A2's local prices never leak into A1.
            var plageCatalog = (await http.GetFromJsonAsync<ReportCatalog>($"v1/reports/branches/{plage.BranchId:D}/catalog", SyncProtocol.Json))!;
            var centreCatalog = (await http.GetFromJsonAsync<ReportCatalog>($"v1/reports/branches/{centre.BranchId:D}/catalog", SyncProtocol.Json))!;
            Assert.Equal((3000L, "Expresso maison"), plageCatalog.Products.Where(p => p.Id == "p-espresso").Select(p => (p.PriceMinor, p.Name)).Single());
            Assert.Equal((2500L, "Café express"), centreCatalog.Products.Where(p => p.Id == "p-espresso").Select(p => (p.PriceMinor, p.Name)).Single());
        }

        // Organization B sees nothing of A.
        await AssertVisible(_cloud.ReportKey(_orgB),
            visible: [$"organizations/{_orgB:D}", $"branches/{port.BranchId:D}/orders"],
            hidden: [$"organizations/{_orgA:D}", $"businesses/{_a1:D}", $"branches/{centre.BranchId:D}/orders", $"branches/{plage.BranchId:D}/catalog"]);
    }

    [Fact]
    public async Task A_gateway_credential_can_only_write_to_its_own_branch()
    {
        var centre = await BranchAsync(_orgA, _a1, "A1 Centre");
        var terminal = await centre.TerminalAsync("Caisse 1");
        var victim = await BranchUnderTest.PlaceAsync(terminal);
        await centre.WaitSyncedAsync(_cloud);

        // A gateway of another organization sends an event reusing the SAME order id and number. It has no way
        // to designate centre's branch: the event lands in its own branch, centre's order is untouched.
        var otherBranch = _cloud.CreateBranch(_b1, "B1 Port");
        using var intruder = await RawGatewayClient.RegisterAsync(_cloud, otherBranch);
        var forged = TestEvents.Order(1, victim.Number, version: 99, orderId: victim.Id, quantity: 7);
        var result = await intruder.SendOkAsync(forged);
        Assert.Equal(1, result.LastAcceptedSequence);

        var centreOrders = await _cloud.OrdersAsync(_cloud.ReportKey(_orgA), centre.BranchId);
        Assert.Equal(Compare.Normalize(Compare.FromGateway(victim)), Compare.Normalize(Assert.Single(centreOrders).Order));
        Assert.Equal(1, _cloud.Count("SELECT COUNT(*) FROM orders WHERE branch_id = @p0 AND organization_id = @p1", otherBranch, _orgB));
        Assert.Equal(centre.OutboxCount, _cloud.Count("SELECT COUNT(*) FROM sync_events WHERE branch_id = @p0", centre.BranchId));
    }

    [Fact]
    public async Task The_database_itself_rejects_rows_whose_tenant_chain_is_inconsistent()
    {
        var branchA = _cloud.CreateBranch(_a1, "A1 Centre");
        await using var connection = new NpgsqlConnection(PostgresCluster.Instance.AppConnection(_cloud.Database));
        await connection.OpenAsync();

        // A branch of business A1 claimed by organization B, or by business A2: composite foreign keys refuse it. Each
        // insert runs inside the claimed organization's tenant context, so row-level security lets it reach the keys.
        foreach (var (organization, business) in new[] { (_orgB, _a1), (_orgA, _a2) })
        {
            await using var tx = await connection.BeginTransactionAsync();
            await EnterTenantAsync(connection, tx, organization);
            await using var insert = new NpgsqlCommand("""
                INSERT INTO orders (organization_id, business_id, branch_id, id, number, status, version, currency, item_count, total_minor,
                                    created_by_name, updated_by_name, created_at, updated_at, last_sequence)
                VALUES (@o, @b, @br, gen_random_uuid(), 1, 'open', 1, 'TND', 0, 0, 'x', 'x', now(), now(), 1);
                """, connection, tx);
            insert.Parameters.AddWithValue("o", organization);
            insert.Parameters.AddWithValue("b", business);
            insert.Parameters.AddWithValue("br", branchA);
            var error = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        }

        // A business attached to another organization's business key, likewise.
        await using var branchTx = await connection.BeginTransactionAsync();
        await EnterTenantAsync(connection, branchTx, _orgB);
        await using var branchInsert = new NpgsqlCommand(
            "INSERT INTO branches (organization_id, business_id, id, name, created_at) VALUES (@o, @b, gen_random_uuid(), 'x', now());", connection, branchTx);
        branchInsert.Parameters.AddWithValue("o", _orgB);
        branchInsert.Parameters.AddWithValue("b", _a1);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, (await Assert.ThrowsAsync<PostgresException>(() => branchInsert.ExecuteNonQueryAsync())).SqlState);
    }

    private static async Task EnterTenantAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid organization)
    {
        await using var enter = new NpgsqlCommand("SELECT app_enter_tenant(@o)", connection, tx);
        enter.Parameters.AddWithValue("o", organization);
        await enter.ExecuteNonQueryAsync();
    }

    private async Task AssertVisible(string key, string[] visible, string[] hidden)
    {
        using var http = _cloud.Http(key);
        foreach (var path in visible)
        {
            using var response = await http.GetAsync($"v1/reports/{path}");
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path} should be visible: {response.StatusCode}");
        }

        foreach (var path in hidden)
        {
            using var response = await http.GetAsync($"v1/reports/{path}");
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{path} should be hidden (404): {response.StatusCode}");
        }
    }
}
