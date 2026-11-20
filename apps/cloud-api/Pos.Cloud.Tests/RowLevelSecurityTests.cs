using System.Net;
using System.Text.Json;
using Npgsql;
using Pos.Sync;

namespace Pos.Cloud.Tests;

/// <summary>Two tenants with real data: a registered gateway that synced orders, a report key, platform audit rows.</summary>
public sealed class RlsFixture : IAsyncLifetime
{
    public CloudProcess Cloud { get; } = new();

    public Tenant A { get; private set; } = null!;

    public Tenant B { get; private set; } = null!;

    public sealed class Tenant(Guid organization, Guid business, Guid branch, string reportKey, RawGatewayClient gateway)
    {
        public Guid Organization => organization;
        public Guid Business => business;
        public Guid Branch => branch;
        public string ReportKey => reportKey;
        public RawGatewayClient Gateway => gateway;
        public long Sequence { get; set; }
        public int Orders { get; set; }
    }

    public async Task InitializeAsync()
    {
        await Cloud.StartAsync();
        A = await TenantAsync("Alpha", 3);
        B = await TenantAsync("Beta", 2);
    }

    private async Task<Tenant> TenantAsync(string name, int orders)
    {
        var organization = Cloud.CreateOrganization(name);
        var business = Cloud.CreateBusiness(organization, $"{name} Café");
        var branch = Cloud.CreateBranch(business, $"{name} Centre");
        var tenant = new Tenant(organization, business, branch, Cloud.ReportKey(organization), await RawGatewayClient.RegisterAsync(Cloud, branch));
        await SyncAsync(tenant, orders);
        return tenant;
    }

    public static async Task SyncAsync(Tenant tenant, int orders)
    {
        var events = Enumerable.Range(1, orders).Select(i => TestEvents.Order(tenant.Sequence + i, tenant.Orders + i)).ToArray();
        var result = await tenant.Gateway.SendOkAsync(events);
        Assert.Null(result.Rejected);
        tenant.Sequence += orders;
        tenant.Orders += orders;
    }

    public async Task DisposeAsync()
    {
        A?.Gateway.Dispose();
        B?.Gateway.Dispose();
        await Cloud.DisposeAsync();
    }
}

/// <summary>Phase 2.1E (doc 15): PostgreSQL row-level security, tenant context, pool hygiene, privileges and config validation.</summary>
public sealed class RowLevelSecurityTests(RlsFixture fixture) : IClassFixture<RlsFixture>
{
    /// <summary>Every tenant-owned table (an independent copy of the server's list: a new table must be added in both).</summary>
    private static readonly string[] TenantTables =
    [
        "organizations", "businesses", "branches", "gateway_credentials", "provisioning_codes", "branch_sync_states", "sync_events",
        "orders", "order_lines", "branch_products", "report_keys", "organization_memberships", "membership_invitations",
    ];

    private const string InsufficientPrivilege = "42501";

    private CloudProcess Cloud => fixture.Cloud;

    private RlsFixture.Tenant A => fixture.A;

    private RlsFixture.Tenant B => fixture.B;

    // ---------------------------------------------------------------- helpers (as the runtime role pos_app)

    private NpgsqlConnection App(string extra = "") => new(PostgresCluster.Instance.AppConnection(Cloud.Database) + extra);

    private static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql, NpgsqlTransaction? tx = null, params object[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx);
        for (var i = 0; i < parameters.Length; i++)
        {
            command.Parameters.AddWithValue($"p{i}", parameters[i]);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync() ?? 0L, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, string sql, NpgsqlTransaction? tx = null, params object[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx);
        for (var i = 0; i < parameters.Length; i++)
        {
            command.Parameters.AddWithValue($"p{i}", parameters[i]);
        }

        return await command.ExecuteNonQueryAsync();
    }

    private static Task<int> EnterAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid organization) =>
        ExecuteAsync(connection, "SELECT app_enter_tenant(@p0)", tx, organization);

    /// <summary>Runs one statement in its own tenant transaction and returns the database error it raised.</summary>
    private async Task<PostgresException> RefusedInTenantAsync(Guid organization, string sql, params object[] parameters)
    {
        await using var connection = App();
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await EnterAsync(connection, tx, organization);
        return await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, sql, tx, parameters));
    }

    private long OwnerCount(string table, Guid? organization = null) => table switch
    {
        "organizations" => Cloud.Count(organization is null ? "SELECT COUNT(*) FROM organizations" : "SELECT COUNT(*) FROM organizations WHERE id = @p0", [.. Args(organization)]),
        "order_lines" => Cloud.Count(organization is null ? "SELECT COUNT(*) FROM order_lines"
            : "SELECT COUNT(*) FROM order_lines l JOIN orders o ON o.branch_id = l.branch_id AND o.id = l.order_id WHERE o.organization_id = @p0", [.. Args(organization)]),
        _ => Cloud.Count(organization is null ? $"SELECT COUNT(*) FROM {table}" : $"SELECT COUNT(*) FROM {table} WHERE organization_id = @p0", [.. Args(organization)]),
    };

    private static object[] Args(Guid? organization) => organization is { } o ? [o] : [];

    private Guid OwnerGuid(string sql, params object[] parameters)
    {
        using var connection = new NpgsqlConnection(PostgresCluster.Instance.AdminConnection(Cloud.Database));
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        for (var i = 0; i < parameters.Length; i++)
        {
            command.Parameters.AddWithValue($"p{i}", parameters[i]);
        }

        return (Guid)command.ExecuteScalar()!;
    }

    // ---------------------------------------------------------------- tests

    [Fact]
    public async Task Without_a_tenant_context_the_runtime_role_reads_and_writes_no_tenant_row()
    {
        await using var connection = App();
        await connection.OpenAsync();
        foreach (var table in TenantTables)
        {
            Assert.Equal(0, await ScalarAsync(connection, $"SELECT COUNT(*) FROM {table}"));
        }

        // The data is there (as the owner): it is only invisible.
        foreach (var table in new[] { "organizations", "businesses", "branches", "gateway_credentials", "provisioning_codes", "branch_sync_states", "sync_events", "orders", "order_lines", "report_keys" })
        {
            Assert.True(OwnerCount(table) > 0, table);
        }

        // Organization rows of the security log too; account rows (no organization) remain the service's.
        Assert.True(Cloud.Count("SELECT COUNT(*) FROM auth_audit_events WHERE organization_id IS NOT NULL") > 0);
        Assert.Equal(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM auth_audit_events WHERE organization_id IS NOT NULL"));

        // Writes: nothing to update or delete, and an insert is refused outright.
        var before = OwnerCount("orders");
        Assert.Equal(0, await ExecuteAsync(connection, "UPDATE branches SET name = 'pwned'"));
        Assert.Equal(0, await ExecuteAsync(connection, "DELETE FROM orders"));
        Assert.Equal(0, await ExecuteAsync(connection, "DELETE FROM report_keys"));
        var insert = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection,
            "INSERT INTO businesses (organization_id, id, name, created_at) VALUES (@p0, gen_random_uuid(), 'intruder', now())", null, A.Organization));
        Assert.Equal(InsufficientPrivilege, insert.SqlState);
        var audit = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection,
            "INSERT INTO auth_audit_events (id, occurred_at, event, organization_id) VALUES (gen_random_uuid(), now(), 'forged', @p0)", null, A.Organization));
        Assert.Equal(InsufficientPrivilege, audit.SqlState);
        Assert.Equal(before, OwnerCount("orders"));
        Assert.Equal(0, Cloud.Count("SELECT COUNT(*) FROM branches WHERE name = 'pwned'"));
    }

    [Fact]
    public async Task Inside_a_tenant_other_tenants_rows_cannot_be_selected_inserted_updated_or_deleted()
    {
        await using var connection = App();
        await connection.OpenAsync();
        await using (var tx = await connection.BeginTransactionAsync())
        {
            await EnterAsync(connection, tx, A.Organization);

            // SELECT: exactly the tenant's own rows, in every table, even when asking for the other tenant explicitly.
            foreach (var table in TenantTables)
            {
                Assert.Equal(OwnerCount(table, A.Organization), await ScalarAsync(connection, $"SELECT COUNT(*) FROM {table}", tx));
            }

            Assert.Equal(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM branches WHERE organization_id = @p0", tx, B.Organization));
            Assert.Equal(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM order_lines WHERE branch_id = @p0", tx, B.Branch));

            // UPDATE / DELETE aimed at the other tenant touch nothing.
            Assert.Equal(0, await ExecuteAsync(connection, "UPDATE branches SET name = 'pwned' WHERE organization_id = @p0", tx, B.Organization));
            Assert.Equal(0, await ExecuteAsync(connection, "UPDATE orders SET label = 'pwned' WHERE branch_id = @p0", tx, B.Branch));
            Assert.Equal(0, await ExecuteAsync(connection, "DELETE FROM sync_events WHERE organization_id = @p0", tx, B.Organization));
            Assert.Equal(0, await ExecuteAsync(connection, "DELETE FROM order_lines WHERE branch_id = @p0", tx, B.Branch));
            Assert.Equal(0, await ExecuteAsync(connection, "DELETE FROM report_keys WHERE organization_id = @p0", tx, B.Organization));
            await tx.RollbackAsync();
        }

        // INSERT into the other tenant, or moving an own row there, is refused by the policy's WITH CHECK.
        Assert.Equal(InsufficientPrivilege, (await RefusedInTenantAsync(A.Organization,
            "INSERT INTO report_keys (id, organization_id, name, secret_sha256, created_at) VALUES (gen_random_uuid(), @p0, 'x', repeat('0', 64), now())", B.Organization)).SqlState);
        Assert.Equal(InsufficientPrivilege, (await RefusedInTenantAsync(A.Organization,
            "UPDATE report_keys SET organization_id = @p0", B.Organization)).SqlState);
        Assert.Equal(InsufficientPrivilege, (await RefusedInTenantAsync(A.Organization,
            "INSERT INTO organizations (id, name, created_at) VALUES (@p0, 'x', now())", Guid.NewGuid())).SqlState);
        // A line attached to the other tenant's (existing) order: its order is invisible here, so the line is refused.
        var otherOrder = OwnerGuid("SELECT id FROM orders WHERE branch_id = @p0 LIMIT 1", B.Branch);
        Assert.Equal(InsufficientPrivilege, (await RefusedInTenantAsync(A.Organization,
            "INSERT INTO order_lines (branch_id, order_id, product_id, line_no, name, unit_price_minor, quantity, line_total_minor) "
            + "VALUES (@p0, @p1, 'p-intruder', 99, 'x', 1, 1, 1)", B.Branch, otherOrder)).SqlState);

        // B is untouched (as the owner sees it).
        Assert.Equal(0, Cloud.Count("SELECT COUNT(*) FROM branches WHERE name = 'pwned'"));
        Assert.Equal(B.Sequence, Cloud.Count("SELECT COUNT(*) FROM sync_events WHERE organization_id = @p0", B.Organization));
        Assert.Equal(0, Cloud.Count("SELECT COUNT(*) FROM order_lines WHERE product_id = 'p-intruder'"));
        Assert.Equal(1, Cloud.Count("SELECT COUNT(*) FROM report_keys WHERE organization_id = @p0", B.Organization));
    }

    [Fact]
    public async Task A_missing_forged_stale_or_session_level_context_grants_nothing()
    {
        await using var connection = App();
        await connection.OpenAsync();

        // Session-level values (as a careless SET would leave them) are never honoured: the context is bound to one transaction.
        await ExecuteAsync(connection, "SELECT set_config('app.organization_id', @p0, false)", null, A.Organization.ToString());
        Assert.Equal(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM organizations"));
        await ExecuteAsync(connection, "SELECT set_config('app.tenant_txn', '1', false)");
        await using (var tx = await connection.BeginTransactionAsync())
        {
            Assert.Equal(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM organizations", tx));
        }

        // Entering a tenant outside a transaction block lasts one statement.
        await ExecuteAsync(connection, "SELECT app_enter_tenant(@p0)", null, A.Organization);
        Assert.Equal(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM organizations"));

        // A context that is not an organization id fails closed (error), it never widens.
        await using (var tx = await connection.BeginTransactionAsync())
        {
            await ExecuteAsync(connection, """
                SELECT set_config('app.organization_id', 'not-a-uuid', true),
                       set_config('app.tenant_txn', ((extract(epoch FROM transaction_timestamp()) * 1000000)::bigint)::text, true)
                """, tx);
            Assert.Equal("22P02", (await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync(connection, "SELECT COUNT(*) FROM organizations", tx))).SqlState);
        }

        // No organization, or a second organization in the same transaction: refused by the database.
        await using (var tx = await connection.BeginTransactionAsync())
        {
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, "SELECT app_enter_tenant(NULL)", tx))).SqlState);
        }

        await using (var tx = await connection.BeginTransactionAsync())
        {
            await EnterAsync(connection, tx, A.Organization);
            await EnterAsync(connection, tx, A.Organization); // re-entering the same one is harmless
            Assert.Equal(InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => EnterAsync(connection, tx, B.Organization))).SqlState);
        }

        // An organization that does not exist simply has no rows.
        await using (var tx = await connection.BeginTransactionAsync())
        {
            await EnterAsync(connection, tx, Guid.NewGuid());
            foreach (var table in TenantTables)
            {
                Assert.Equal(0, await ScalarAsync(connection, $"SELECT COUNT(*) FROM {table}", tx));
            }
        }
    }

    [Fact]
    public async Task A_pooled_connection_never_carries_a_tenant_into_the_next_use()
    {
        // One physical connection, reused every time: committed, rolled back and failed transactions all leave it clean.
        const string pool = ";Pooling=true;Minimum Pool Size=0;Maximum Pool Size=1;Application Name=rls-pool-probe";
        var backends = new HashSet<long>();
        for (var i = 0; i < 12; i++)
        {
            var tenant = i % 2 == 0 ? A : B;
            await using (var connection = App(pool))
            {
                await connection.OpenAsync();
                backends.Add(await ScalarAsync(connection, "SELECT pg_backend_pid()"));
                await using var tx = await connection.BeginTransactionAsync();
                await EnterAsync(connection, tx, tenant.Organization);
                Assert.Equal(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM organizations WHERE id = @p0", tx, tenant.Organization));
                switch (i % 3)
                {
                    case 0:
                        await tx.CommitAsync();
                        break;
                    case 1:
                        await tx.RollbackAsync();
                        break;
                    default:
                        await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync(connection, "SELECT 1 / 0", tx));
                        break; // disposed with the failed transaction still open
                }
            }

            await using (var next = App(pool))
            {
                await next.OpenAsync();
                Assert.Equal(0, await ScalarAsync(next, "SELECT COUNT(*) FROM organizations"));
                await using var tx = await next.BeginTransactionAsync();
                Assert.Equal(0, await ScalarAsync(next, "SELECT COUNT(*) FROM organizations", tx));
                Assert.Equal(0, await ScalarAsync(next, "SELECT COUNT(*) FROM sync_events", tx));
            }
        }

        Assert.Single(backends);
    }

    [Fact]
    public async Task Concurrent_requests_of_different_organizations_stay_isolated()
    {
        // Real traffic: both gateways sync while both report keys read, all at once, through the server's own pool.
        using var reportA = Cloud.Http(A.ReportKey);
        using var reportB = Cloud.Http(B.ReportKey);
        var work = new List<Task>
        {
            RlsFixture.SyncAsync(A, 5),
            RlsFixture.SyncAsync(B, 4),
        };
        for (var i = 0; i < 30; i++)
        {
            work.Add(ExpectOwnAsync(reportA, A));
            work.Add(ExpectOwnAsync(reportB, B));
            work.Add(ExpectHiddenAsync(reportA, B));
            work.Add(ExpectHiddenAsync(reportB, A));
        }

        await Task.WhenAll(work);
        Assert.Equal(A.Orders, (await Cloud.OrdersAsync(A.ReportKey, A.Branch)).Count);
        Assert.Equal(B.Orders, (await Cloud.OrdersAsync(B.ReportKey, B.Branch)).Count);
        Assert.Equal(0, Cloud.Count("SELECT COUNT(*) FROM sync_events e JOIN branches b ON b.id = e.branch_id WHERE b.organization_id <> e.organization_id"));
        Assert.Equal(A.Sequence, Cloud.Count("SELECT last_sequence FROM branch_sync_states WHERE branch_id = @p0", A.Branch));
        Assert.Equal(B.Sequence, Cloud.Count("SELECT last_sequence FROM branch_sync_states WHERE branch_id = @p0", B.Branch));

        // Raw: many interleaved tenant transactions over a small shared pool; each sees exactly its tenant.
        const string pool = ";Pooling=true;Maximum Pool Size=3;Application Name=rls-concurrency-probe";
        await Task.WhenAll(Enumerable.Range(0, 6).Select(worker => Task.Run(async () =>
        {
            for (var i = 0; i < 25; i++)
            {
                var tenant = (worker + i) % 2 == 0 ? A : B;
                await using var connection = App(pool);
                await connection.OpenAsync();
                await using (var tx = await connection.BeginTransactionAsync())
                {
                    await EnterAsync(connection, tx, tenant.Organization);
                    Assert.Equal(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM organizations", tx));
                    Assert.Equal(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM organizations WHERE id = @p0", tx, tenant.Organization));
                    Assert.Equal(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM orders WHERE organization_id <> @p0", tx, tenant.Organization));
                    await tx.CommitAsync();
                }

                Assert.Equal(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM orders"));
            }
        })));
    }

    private static async Task ExpectOwnAsync(HttpClient report, RlsFixture.Tenant tenant)
    {
        using var response = await report.GetAsync($"v1/reports/organizations/{tenant.Organization:D}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(tenant.Organization.ToString("D"), json.RootElement.GetProperty("id").GetString());
    }

    private static async Task ExpectHiddenAsync(HttpClient report, RlsFixture.Tenant other)
    {
        foreach (var path in new[] { $"v1/reports/organizations/{other.Organization:D}", $"v1/reports/branches/{other.Branch:D}/orders", $"v1/reports/businesses/{other.Business:D}" })
        {
            using var response = await report.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task The_runtime_role_cannot_bypass_or_alter_row_level_security_and_does_not_own_the_schema()
    {
        await using (var connection = App())
        {
            await connection.OpenAsync();
            foreach (var sql in new[]
            {
                "ALTER TABLE orders DISABLE ROW LEVEL SECURITY",
                "DROP POLICY tenant_isolation ON orders",
                "CREATE POLICY open_door ON orders USING (true)",
                "CREATE OR REPLACE FUNCTION app_current_organization() RETURNS uuid LANGUAGE sql AS 'SELECT NULL::uuid'",
                "ALTER FUNCTION app_enter_tenant(uuid) OWNER TO pos_app",
                "ALTER ROLE pos_app BYPASSRLS",
                "SET ROLE postgres",
            })
            {
                var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, sql));
                Assert.True(error.SqlState == InsufficientPrivilege, $"{sql}: {error.SqlState} {error.MessageText}");
            }

            // Turning row security off does not bypass it: the query is refused instead.
            await ExecuteAsync(connection, "SET row_security = off");
            Assert.Equal(InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync(connection, "SELECT COUNT(*) FROM orders"))).SqlState);
        }

        // Catalog facts, as the owner: not superuser, no BYPASSRLS, owns nothing; every tenant table has RLS on.
        Assert.Equal(0, Cloud.Count("SELECT COUNT(*) FROM pg_roles WHERE rolname = 'pos_app' AND (rolsuper OR rolbypassrls OR rolcreaterole OR rolcreatedb)"));
        Assert.Equal(0, Cloud.Count("SELECT COUNT(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public' AND c.relowner = 'pos_app'::regrole"));
        Assert.Equal(0, Cloud.Count("SELECT COUNT(*) FROM pg_proc WHERE proowner = 'pos_app'::regrole"));
        Assert.Equal(TenantTables.Length + 1, Cloud.Count(
            "SELECT COUNT(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public' AND c.relrowsecurity AND c.relname = ANY(@p0)",
            (object)TenantTables.Append("auth_audit_events").ToArray()));

        // The functions belong to the schema owner (migrations); only the lookups are SECURITY DEFINER; PUBLIC may call none.
        Assert.Equal(9, Cloud.Count("SELECT COUNT(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'public' AND p.proname LIKE 'app\\_%' AND p.proowner = (SELECT relowner FROM pg_class WHERE oid = 'public.orders'::regclass)"));
        Assert.Equal(7, Cloud.Count("SELECT COUNT(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'public' AND p.proname LIKE 'app\\_%' AND p.prosecdef"));
        Assert.Equal(0, Cloud.Count("SELECT COUNT(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'public' AND p.proname LIKE 'app\\_%' AND (p.proacl IS NULL OR EXISTS (SELECT 1 FROM aclexplode(p.proacl) a WHERE a.grantee = 0))"));
        Assert.Equal(1, Cloud.Count("SELECT COUNT(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" LIKE '%_CloudRowLevelSecurity'"));
    }

    [Fact]
    public void Check_config_detects_disabled_weakened_or_bypassed_row_level_security()
    {
        string[] checkConfig = ["check-config", .. Cloud.RunArgs().Skip(1)];
        var database = Cloud.Database;
        void Owner(string sql) => PostgresCluster.Instance.Execute(database, sql);
        (int, string) Check(IReadOnlyDictionary<string, string?>? environment = null) => Cloud.AdminRaw(environment ?? new Dictionary<string, string?>(), checkConfig);
        void AssertAccepted()
        {
            var (exitCode, output) = Check();
            Assert.True(exitCode == 0, output);
        }

        void AssertRefused(string expected, IReadOnlyDictionary<string, string?>? environment = null)
        {
            var (exitCode, output) = Check(environment);
            Assert.True(exitCode == 2 && output.Contains(expected, StringComparison.Ordinal), $"{exitCode}: {output}");
        }

        AssertAccepted();

        Owner("ALTER TABLE orders DISABLE ROW LEVEL SECURITY;");
        try
        {
            AssertRefused("sécurité par ligne absente ou modifiée sur orders");
        }
        finally
        {
            Owner("ALTER TABLE orders ENABLE ROW LEVEL SECURITY;");
        }

        Owner("CREATE POLICY open_door ON branches USING (true);"); // permissive policies add up: this one would open everything
        try
        {
            AssertRefused("branches");
        }
        finally
        {
            Owner("DROP POLICY open_door ON branches;");
        }

        Owner("REVOKE EXECUTE ON FUNCTION app_enter_tenant(uuid) FROM pos_app;");
        try
        {
            AssertRefused("app_enter_tenant(uuid)");
        }
        finally
        {
            Owner("GRANT EXECUTE ON FUNCTION app_enter_tenant(uuid) TO pos_app;");
        }

        Owner("CREATE TABLE stray_notes (id int); GRANT SELECT, INSERT ON stray_notes TO pos_app;");
        try
        {
            AssertRefused("stray_notes");
        }
        finally
        {
            Owner("DROP TABLE stray_notes;");
        }

        Owner("ALTER TABLE branch_products OWNER TO pos_app;");
        try
        {
            AssertRefused("possède");
        }
        finally
        {
            // Ownership transfer folds the role's explicit grants into the owner's: restore them too.
            Owner("ALTER TABLE branch_products OWNER TO postgres; GRANT SELECT, INSERT, UPDATE, DELETE ON branch_products TO pos_app;");
        }

        Owner($"ALTER DATABASE {database} SET app.organization_id = '{A.Organization:D}';");
        try
        {
            AssertRefused("contexte d'organisation est prédéfini");
        }
        finally
        {
            Owner($"ALTER DATABASE {database} RESET app.organization_id;");
        }

        // A runtime role with BYPASSRLS (a dedicated probe role: the shared pos_app is never altered).
        var probe = $"rls_probe_{Guid.NewGuid():N}"[..24];
        var password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
        Owner($"""
            CREATE ROLE {probe} LOGIN PASSWORD '{password}' BYPASSRLS;
            GRANT CONNECT ON DATABASE {database} TO {probe};
            GRANT USAGE ON SCHEMA public TO {probe};
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {probe};
            REVOKE INSERT, UPDATE, DELETE ON "__EFMigrationsHistory" FROM {probe};
            REVOKE UPDATE, DELETE ON auth_audit_events FROM {probe};
            GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public TO {probe};
            """);
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(PostgresCluster.Instance.AppConnection(database)) { Username = probe, Password = password, Pooling = false };
            AssertRefused("BYPASSRLS", new Dictionary<string, string?> { ["POS_CLOUD_DB"] = connection.ConnectionString });
        }
        finally
        {
            Owner($"DROP OWNED BY {probe};");
            PostgresCluster.Instance.Execute("postgres", $"DROP ROLE {probe};");
        }

        AssertAccepted();
    }

    [Fact]
    public async Task Platform_operations_need_a_privileged_role_and_every_tenant_administration_is_audited()
    {
        // The runtime role cannot provision: it sees no tenant data outside a tenant transaction.
        var name = $"Refused {Guid.NewGuid():N}";
        var (exitCode, output) = Cloud.AdminRaw(new Dictionary<string, string?> { ["POS_CLOUD_ADMIN_DB"] = PostgresCluster.Instance.AppConnection(Cloud.Database) },
            "org-create", "--name", name);
        Assert.True(exitCode == 2 && output.Contains("opération de plateforme refusée", StringComparison.Ordinal), $"{exitCode}: {output}");
        Assert.Equal(0, Cloud.Count("SELECT COUNT(*) FROM organizations WHERE name = @p0", name));

        // Setup of A was audited, per organization, with ids only.
        foreach (var auditEvent in new[] { "platform_organization_created", "platform_business_created", "platform_branch_created", "platform_gateway_code_issued", "platform_report_key_created", "gateway_registered" })
        {
            Assert.True(Cloud.Count("SELECT COUNT(*) FROM auth_audit_events WHERE event = @p0 AND organization_id = @p1", auditEvent, A.Organization) == 1, auditEvent);
        }

        Assert.Equal(1, Cloud.Count("SELECT COUNT(*) FROM auth_audit_events WHERE event = 'gateway_registered' AND organization_id = @p0 AND details = @p1",
            A.Organization, $"gateway={A.Gateway.GatewayId};branch={A.Branch:D};replaced=0"));
        Assert.Equal(0, Cloud.Count("SELECT COUNT(*) FROM auth_audit_events WHERE details LIKE '%posrk%' OR details LIKE '%@%' OR details LIKE '%code=%'"));

        // Revocation by the operator: audited in the gateway's organization; the gateway is fenced at once.
        var branch = Cloud.CreateBranch(A.Business, $"Annexe {Guid.NewGuid():N}"[..16]);
        using var gateway = await RawGatewayClient.RegisterAsync(Cloud, branch);
        Assert.Contains("revoked=1", Cloud.Admin("gateway-revoke", "--gateway", gateway.GatewayId), StringComparison.Ordinal);
        Assert.Equal(1, Cloud.Count("SELECT COUNT(*) FROM auth_audit_events WHERE event = 'platform_gateway_revoked' AND organization_id = @p0 AND details LIKE @p1",
            A.Organization, $"gateway={gateway.GatewayId};%"));
        var (status, body) = await gateway.SendAsync(TestEvents.Order(1, 1));
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains(CloudErrorCodes.GatewayRevoked, body, StringComparison.Ordinal);

        // The listing is a platform view across tenants; the service never offers one.
        Assert.Contains(gateway.GatewayId, Cloud.Admin("gateways"), StringComparison.Ordinal);
    }
}
