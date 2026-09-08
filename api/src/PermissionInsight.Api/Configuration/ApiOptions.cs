using Microsoft.Extensions.Configuration;

namespace PermissionInsight.Api.Configuration;

/// <summary>
/// Everything the backend cannot start without. Reading configuration here and
/// failing at startup means a half-configured deployment is obvious straight
/// away, rather than surfacing as a confusing 401 on the first request.
/// </summary>
public sealed class ApiOptions
{
    /// <summary>Directory that issues the tokens users sign in with.</summary>
    public required string TenantId { get; init; }

    /// <summary>Application id of the registration users sign in against. Entra puts this in the audience of a v2 token.</summary>
    public required string ApiClientId { get; init; }

    /// <summary>The registration's identifier URI. Accepted as an audience alongside the client id; see docs/adr/0003.</summary>
    public required string ApiIdentifierUri { get; init; }

    /// <summary>Root of the SharePoint tenant, for example https://contoso.sharepoint.com.</summary>
    public required string SharePointRootUrl { get; init; }

    /// <summary>Entra login endpoint, so sovereign clouds are a configuration change rather than a code change.</summary>
    public required string Authority { get; init; }

    /// <summary>
    /// Client id of the user-assigned managed identity the backend calls Graph
    /// and SharePoint as. Required outside development: there is deliberately
    /// no second credential path.
    /// </summary>
    public string? ManagedIdentityClientId { get; init; }

    public bool IsDevelopment { get; init; }

    public string TokenIssuer => $"{Authority.TrimEnd('/')}/{TenantId}/v2.0";

    public string OpenIdConfigurationUrl =>
        $"{Authority.TrimEnd('/')}/{TenantId}/v2.0/.well-known/openid-configuration";

    public static ApiOptions FromConfiguration(IConfiguration configuration)
    {
        var environment = configuration["AZURE_FUNCTIONS_ENVIRONMENT"];
        var isDevelopment = string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase);

        var options = new ApiOptions
        {
            TenantId = Required(configuration, "TenantId"),
            ApiClientId = Required(configuration, "ApiClientId"),
            ApiIdentifierUri = Required(configuration, "ApiIdentifierUri"),
            SharePointRootUrl = Required(configuration, "SharePointRootUrl").TrimEnd('/'),
            Authority = configuration["Authority"] ?? "https://login.microsoftonline.com",
            ManagedIdentityClientId = configuration["ManagedIdentityClientId"],
            IsDevelopment = isDevelopment,
        };

        Validate(options);
        return options;
    }

    private static void Validate(ApiOptions options)
    {
        if (!Guid.TryParse(options.TenantId, out _))
        {
            throw new InvalidOperationException("TenantId must be a GUID.");
        }

        if (!Guid.TryParse(options.ApiClientId, out _))
        {
            throw new InvalidOperationException("ApiClientId must be a GUID.");
        }

        if (!Uri.TryCreate(options.SharePointRootUrl, UriKind.Absolute, out var sharePoint) ||
            sharePoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("SharePointRootUrl must be an absolute https URL.");
        }

        // Without this the app would silently fall back to the development
        // credential chain in a deployed environment, which is the one thing
        // acceptance test 11 in phase 1 exists to rule out.
        if (!options.IsDevelopment && string.IsNullOrWhiteSpace(options.ManagedIdentityClientId))
        {
            throw new InvalidOperationException(
                "ManagedIdentityClientId is required outside development. The backend has no other way to reach Graph or SharePoint, and must not have one.");
        }
    }

    private static string Required(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Application setting '{key}' is missing.")
            : value;
    }
}
