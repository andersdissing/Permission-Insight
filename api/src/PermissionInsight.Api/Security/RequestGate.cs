using System.Net;
using System.Security.Claims;

namespace PermissionInsight.Api.Security;

/// <summary>
/// The access control model, in one testable place.
///
/// Signing in to Permission Insight is equivalent to tenant-wide read access,
/// because the identity behind every downstream call holds Sites.Read.All. So
/// this check is not a convenience over the front end's route guard, it is the
/// only thing standing between a signed-in employee and every site in the
/// organisation. The SPA's route guard is cosmetic.
///
/// <see cref="AuthorizationMiddleware"/> is a thin adapter over this class, and
/// it runs before every function invocation. There is no way to register a
/// handler that skips it.
/// </summary>
public sealed class RequestGate
{
    public const string RequiredRole = "PermissionInsight.Use";

    /// <summary>The delegated scope the front end asks for. Its presence also proves the token is not app-only.</summary>
    public const string RequiredScope = "access_as_user";

    private const string BearerPrefix = "Bearer ";

    private readonly IAccessTokenValidator _validator;

    public RequestGate(IAccessTokenValidator validator) => _validator = validator;

    public async Task<GateDecision> EvaluateAsync(
        string method,
        string? authorizationHeader,
        CancellationToken cancellationToken)
    {
        // Method first. A refused method is refused whatever token it carries,
        // so a valid token never buys a method this API does not perform.
        //
        // Note this is the inbound list, which admits POST. It says nothing
        // about what may be sent to SharePoint: that is a separate list, and
        // no inbound method is ever forwarded downstream.
        if (!HttpMethodPolicy.IsAllowedInbound(method))
        {
            return GateDecision.Deny(
                HttpStatusCode.MethodNotAllowed,
                "method_not_allowed",
                $"This API accepts {string.Join(", ", HttpMethodPolicy.InboundMethods.Where(m => m != "OPTIONS"))}.");
        }

        if (string.IsNullOrWhiteSpace(authorizationHeader))
        {
            return GateDecision.Deny(
                HttpStatusCode.Unauthorized,
                "missing_token",
                "This request carried no bearer token.");
        }

        if (!authorizationHeader.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return GateDecision.Deny(
                HttpStatusCode.Unauthorized,
                "invalid_authorization_header",
                "The Authorization header must be a bearer token.");
        }

        var token = authorizationHeader[BearerPrefix.Length..].Trim();
        if (token.Length == 0)
        {
            return GateDecision.Deny(
                HttpStatusCode.Unauthorized,
                "missing_token",
                "The Authorization header carried no token.");
        }

        var outcome = await _validator.ValidateAsync(token, cancellationToken).ConfigureAwait(false);
        if (outcome.Principal is not { } principal)
        {
            return GateDecision.Deny(
                HttpStatusCode.Unauthorized,
                "invalid_token",
                "The bearer token was rejected.",
                new Dictionary<string, object?> { ["reason"] = outcome.FailureReason });
        }

        return Authorize(principal);
    }

    /// <summary>
    /// Everything decided from claims, after the signature has been checked.
    /// Separated so the policy can be tested without minting real tokens.
    /// </summary>
    internal static GateDecision Authorize(ClaimsPrincipal principal)
    {
        // Only v2 tokens. A v1 token has a different issuer and a different
        // claim shape, and accepting both would mean two sets of rules.
        var version = principal.FindFirst("ver")?.Value;
        if (version != "2.0")
        {
            return GateDecision.Deny(
                HttpStatusCode.Unauthorized,
                "unsupported_token_version",
                "Only version 2.0 tokens are accepted.");
        }

        // A delegated token carries scp. An app-only token does not, and must
        // never reach this API: the app role is assignable to users only, and
        // an application arriving with tenant-wide read is exactly the shape of
        // access this gate exists to refuse.
        var scopes = (principal.FindFirst("scp")?.Value ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (!scopes.Contains(RequiredScope, StringComparer.Ordinal))
        {
            return GateDecision.Deny(
                HttpStatusCode.Unauthorized,
                "invalid_scope",
                $"The token must be a delegated token carrying the {RequiredScope} scope.");
        }

        // Read the object id before the role check rather than after. A caller
        // refused the role is precisely the caller worth naming in the audit
        // log — a misconfiguration or somebody probing — and by this point the
        // token's signature and issuer have been verified, so the claim is as
        // trustworthy as it is for a caller who passes.
        var objectId = principal.FindFirst("oid")?.Value;
        var identified = string.IsNullOrWhiteSpace(objectId) ? null : objectId;

        var roles = principal.FindAll("roles").Select(claim => claim.Value).ToArray();
        if (!roles.Contains(RequiredRole, StringComparer.Ordinal))
        {
            // Name the role and list what did arrive. A missing group
            // assignment is then diagnosable from the response alone, without
            // anyone going to read the logs.
            return GateDecision.Deny(
                HttpStatusCode.Forbidden,
                "missing_role",
                $"This account is not assigned the {RequiredRole} role. Ask an administrator to add you to the group that holds it.",
                new Dictionary<string, object?>
                {
                    ["requiredRole"] = RequiredRole,
                    ["rolesPresent"] = roles,
                },
                identified);
        }

        if (string.IsNullOrWhiteSpace(objectId))
        {
            // Every action is logged against the object id. Without one there
            // is no audit trail, so the request cannot be served.
            return GateDecision.Deny(
                HttpStatusCode.Unauthorized,
                "invalid_token",
                "The token carries no object id, so the request cannot be audited.");
        }

        var caller = new CallerContext(
            ObjectId: objectId,
            DisplayName: principal.FindFirst("name")?.Value ?? objectId,
            Mail: principal.FindFirst("preferred_username")?.Value,
            TenantId: principal.FindFirst("tid")?.Value ?? string.Empty);

        return GateDecision.Allow(caller);
    }
}

public abstract record GateDecision
{
    public sealed record Allowed(CallerContext Caller) : GateDecision;

    /// <param name="ObjectId">
    /// Who was refused, when the token was valid enough to say. Null for a
    /// refusal that happened before or during token validation, where there is
    /// genuinely no identity to name.
    /// </param>
    public sealed record Denied(
        HttpStatusCode Status,
        string Error,
        string Message,
        IReadOnlyDictionary<string, object?>? Details,
        string? ObjectId) : GateDecision;

    public static GateDecision Allow(CallerContext caller) => new Allowed(caller);

    public static GateDecision Deny(
        HttpStatusCode status,
        string error,
        string message,
        IReadOnlyDictionary<string, object?>? details = null,
        string? objectId = null) => new Denied(status, error, message, details, objectId);
}
