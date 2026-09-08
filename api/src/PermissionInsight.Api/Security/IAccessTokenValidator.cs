using System.Security.Claims;

namespace PermissionInsight.Api.Security;

/// <summary>
/// Cryptographic validation of a bearer token: signature, issuer, audience and
/// lifetime, and nothing else. Every decision about what the token permits
/// lives in <see cref="RequestGate"/>, so that the policy can be tested without
/// a signing key.
/// </summary>
public interface IAccessTokenValidator
{
    Task<TokenValidationOutcome> ValidateAsync(string token, CancellationToken cancellationToken);
}

public sealed record TokenValidationOutcome(ClaimsPrincipal? Principal, string? FailureReason)
{
    public bool IsValid => Principal is not null;

    public static TokenValidationOutcome Valid(ClaimsPrincipal principal) => new(principal, null);

    public static TokenValidationOutcome Invalid(string reason) => new(null, reason);
}
