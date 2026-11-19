using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Pos.Cloud.Identity;
using Pos.Sync;

namespace Pos.Cloud;

/// <summary>
/// OpenAPI description of the public cloud contract (first-party generator). Only what a client needs: paths,
/// request/response shapes, error envelope and the authentication schemes. No servers (no host or listen address),
/// no examples with credentials, no internal entities. Debug-only fault injection has no endpoint, so it cannot appear.
/// </summary>
internal static class CloudOpenApi
{
    public const string DocumentName = "v1";
    public const string Route = "/openapi/{documentName}.json";
    private const string GatewayScheme = "PosGateway";
    private const string ReportScheme = "ReportKey";
    private const string SessionScheme = "Session";

    public static void Configure(OpenApiOptions options)
    {
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info = new OpenApiInfo
            {
                Title = "PosSaaS Cloud API",
                Version = DocumentName,
                Description = "Branch gateway synchronization and read-only reports. Every error body is the envelope "
                    + "{ \"code\", \"message\" }: clients act on `code`; `message` is human-readable (French). "
                    + "Out-of-scope and non-existent resources are both 404. Cloud users authenticate with a session cookie "
                    + "(/v1/auth, /v1/me); gateways and report keys are separate schemes.",
            };
            document.Servers = [];
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal)
            {
                [GatewayScheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    In = ParameterLocation.Header,
                    Name = "Authorization",
                    Description = "`PosGateway {gatewayId}:{unixSeconds}:{signature}`: ECDSA P-256 signature by the registered "
                        + "gateway key over method, path and query, timestamp and body SHA-256; valid ±5 minutes.",
                },
                [ReportScheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "posrk",
                    Description = "Read-only report key scoped to an organization, a business or a branch.",
                },
                [SessionScheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    In = ParameterLocation.Cookie,
                    Name = SessionService.CookieName,
                    Description = "Cloud user session (HttpOnly, Secure, SameSite=Strict cookie set by login). Unsafe requests from a "
                        + "browser must come from the configured dashboard origin. 403 mfa_required: finish or enrol the second factor.",
                },
            };
            return Task.CompletedTask;
        });

        options.AddOperationTransformer((operation, context, _) =>
        {
            var metadata = context.Description.ActionDescriptor.EndpointMetadata;
            if (metadata.OfType<IAllowAnonymous>().Any())
            {
                return Task.CompletedTask;
            }

            var policy = metadata.OfType<IAuthorizeData>().Select(a => a.Policy).LastOrDefault(p => p is not null);
            var scheme = policy switch
            {
                CloudApi.ReportPolicy => ReportScheme,
                CloudApi.GatewayPolicy => GatewayScheme,
                AuthApi.UserPolicy or AuthApi.EnrollingPolicy or AuthApi.ChallengePolicy or AuthApi.AnySessionPolicy => SessionScheme,
                _ => GatewayScheme, // fallback policy (gateway or report credentials)
            };
            operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(scheme, context.Document)] = [] }];
            return Task.CompletedTask;
        });

        // The strict wire format: object schemas reject unknown members, matching the server's deserializer.
        options.AddSchemaTransformer((schema, context, _) =>
        {
            if (context.JsonTypeInfo.Kind == System.Text.Json.Serialization.Metadata.JsonTypeInfoKind.Object
                && context.JsonTypeInfo.Type.Namespace is { } ns && (ns == typeof(SyncBatch).Namespace || ns == typeof(AuthStatus).Namespace))
            {
                schema.AdditionalPropertiesAllowed = false;
            }

            return Task.CompletedTask;
        });
    }
}
