using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using PermissionInsight.Api.Configuration;

namespace PermissionInsight.Api.Security;

/// <summary>
/// Validates a token against Entra's published signing keys. The configuration
/// manager caches the key set and refreshes it on rollover, so this costs one
/// discovery request per process rather than one per call.
/// </summary>
public sealed class EntraAccessTokenValidator : IAccessTokenValidator
{
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _configurationManager;
    private readonly TokenValidationParameters _parameters;
    private readonly JsonWebTokenHandler _handler;

    public EntraAccessTokenValidator(ApiOptions options, HttpClient httpClient)
    {
        _configurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            options.OpenIdConfigurationUrl,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever(httpClient) { RequireHttps = true });

        _parameters = new TokenValidationParameters
        {
            ValidIssuer = options.TokenIssuer,

            // Entra issues v2 tokens with the client id as the audience. The
            // identifier URI is accepted too, because that is what the front
            // end names when it asks for the scope, and pinning only one of
            // them rejects real tokens. See docs/adr/0003.
            ValidAudiences = [options.ApiClientId, options.ApiIdentifierUri],

            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
            ConfigurationManager = _configurationManager,
        };

        // Keep the raw JWT claim names. Everything downstream reads oid, roles
        // and scp, and the legacy SOAP-era mapping would silently rename them.
        _handler = new JsonWebTokenHandler { MapInboundClaims = false };
    }

    public async Task<TokenValidationOutcome> ValidateAsync(string token, CancellationToken cancellationToken)
    {
        // The handler resolves signing keys through the configuration manager,
        // which honours the cancellation of the refresh but not of the call.
        cancellationToken.ThrowIfCancellationRequested();

        var result = await _handler.ValidateTokenAsync(token, _parameters).ConfigureAwait(false);

        if (!result.IsValid)
        {
            // The exception type is the useful part; the message can carry
            // token contents and is not returned to the caller.
            return TokenValidationOutcome.Invalid(result.Exception?.GetType().Name ?? "TokenInvalid");
        }

        return TokenValidationOutcome.Valid(new ClaimsPrincipal(result.ClaimsIdentity));
    }
}
