using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using Pos.Cloud.Identity;
using Pos.Sync;

namespace Pos.Cloud;

/// <summary>The cloud web host: services and pipeline, shared by <c>run</c> and <c>openapi</c>.</summary>
internal static class CloudHost
{
    /// <param name="kestrel">Listener configuration; <c>null</c> = no network at all (a no-op server, for <c>openapi</c>).</param>
    public static WebApplication Build(string connectionString, AuthSettings auth, ILoggerFactory loggerFactory, Action<KestrelServerOptions>? kestrel)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Configuration.Sources.Clear(); // explicit configuration only: nothing can add a plain-HTTP listener
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(loggerFactory);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = SyncProtocol.MaxBodyBytes;
            k.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            kestrel?.Invoke(k);
        });

        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            var strict = SyncProtocol.Json;
            o.SerializerOptions.UnmappedMemberHandling = strict.UnmappedMemberHandling;
            o.SerializerOptions.RespectNullableAnnotations = strict.RespectNullableAnnotations;
            o.SerializerOptions.RespectRequiredConstructorParameters = strict.RespectRequiredConstructorParameters;
            o.SerializerOptions.NumberHandling = strict.NumberHandling;
            o.SerializerOptions.MaxDepth = strict.MaxDepth;
        });
        builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddDbContext<CloudDb>(o => o.UseNpgsql(connectionString));
        builder.Services.AddScoped<SyncService>();
        builder.Services.AddScoped<GatewayRegistration>();

        // Cloud users: Identity (users, password hashing, lockout, security stamp) + server-side sessions.
        builder.Services.AddSingleton(auth);
        builder.Services.AddSingleton<IAuthMailer>(sp => new PickupDirectoryMailer(auth.MailDirectory, sp.GetRequiredService<ILogger<PickupDirectoryMailer>>()));
        builder.Services.AddScoped<SessionService>();
        builder.Services.AddScoped<AuthService>();
        builder.Services.AddIdentityCore<CloudUser>(o =>
            {
                // Length over composition rules (NIST SP 800-63B); upper bound in AuthService (hashing cost).
                o.Password.RequiredLength = 12;
                o.Password.RequiredUniqueChars = 5;
                o.Password.RequireDigit = false;
                o.Password.RequireLowercase = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireNonAlphanumeric = false;
                o.User.RequireUniqueEmail = true;
                o.SignIn.RequireConfirmedEmail = true;
                o.Lockout.AllowedForNewUsers = true;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddEntityFrameworkStores<CloudDb>();

        builder.Services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, GatewaySignatureHandler>(SyncProtocol.GatewayScheme, null)
            .AddScheme<AuthenticationSchemeOptions, ReportKeyHandler>(SyncProtocol.ReportScheme, null)
            .AddScheme<AuthenticationSchemeOptions, SessionHandler>(SessionService.Scheme, null);
        var authorization = builder.Services.AddAuthorizationBuilder();
        AuthApi.AddPolicies(authorization);
        authorization
            .AddPolicy(CloudApi.GatewayPolicy, p => p.AddAuthenticationSchemes(SyncProtocol.GatewayScheme).RequireAuthenticatedUser())
            .AddPolicy(CloudApi.ReportPolicy, p => p.AddAuthenticationSchemes(SyncProtocol.ReportScheme).RequireAuthenticatedUser())
            // Deny by default: an endpoint without an explicit policy or AllowAnonymous requires authentication.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder(SyncProtocol.GatewayScheme, SyncProtocol.ReportScheme).RequireAuthenticatedUser().Build());
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                return ValueTask.CompletedTask; // the body is the status-code page (CloudError envelope)
            };
            // Per IP, on top of the per-account lockout and per-account mail budget.
            o.AddPolicy(AuthApi.AuthRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "?", _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
            o.AddPolicy(AuthApi.MailRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "?", _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
            o.AddPolicy(CloudApi.RegisterRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "?", _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
        });

        builder.Services.AddOpenApi(CloudOpenApi.DocumentName, CloudOpenApi.Configure);
        if (kestrel is null)
        {
            builder.Services.AddSingleton<IServer, NoListenerServer>();
        }

        return builder.Build();
    }

    public static void UsePipeline(WebApplication app, bool exposeOpenApi)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseStatusCodePages(CloudApi.WriteStatusCodeError);
        app.Use(CloudApi.HandleErrors);
        app.Use((http, next) => AuthApi.GuardAsync(http, next, http.RequestServices.GetRequiredService<AuthSettings>()));
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        CloudApi.Map(app);
        if (exposeOpenApi)
        {
            // Public contract only (see CloudOpenApi); opt-in so a production host does not advertise its surface by default.
            app.MapOpenApi(CloudOpenApi.Route).AllowAnonymous();
        }
    }

    /// <summary>The OpenAPI 3.1 document as JSON, generated from endpoint metadata (the host must be started).</summary>
    public static async Task<string> OpenApiJsonAsync(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var provider = app.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>(CloudOpenApi.DocumentName);
        var document = await provider.GetOpenApiDocumentAsync();
        using var stream = new MemoryStream();
        await document.SerializeAsJsonAsync(stream, OpenApiSpecVersion.OpenApi3_1);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray()).ReplaceLineEndings("\n").TrimEnd() + "\n";
    }
}

/// <summary>
/// Lets the host start (which wires endpoints into routing and the API explorer) without opening any socket.
/// Used only to generate the OpenAPI document, as the framework's build-time generator does.
/// </summary>
internal sealed class NoListenerServer : IServer
{
    public IFeatureCollection Features { get; } = new FeatureCollection();

    public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
        where TContext : notnull => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
    }
}
