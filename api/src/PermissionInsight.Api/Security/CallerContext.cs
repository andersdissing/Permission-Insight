using Microsoft.Azure.Functions.Worker;

namespace PermissionInsight.Api.Security;

/// <summary>
/// The signed-in user, taken from a token that has already been validated and
/// checked for the app role. A handler can only obtain one of these from
/// <see cref="FunctionContextExtensions.GetCaller"/>, which throws if the
/// authorisation middleware did not run.
/// </summary>
/// <remarks>
/// Deliberately does not carry the raw bearer token. Nothing acts on the
/// user's behalf — every downstream call uses the application identity, which
/// cannot write — so holding the token would be retaining a credential for no
/// reason.
/// </remarks>
public sealed record CallerContext(
    string ObjectId,
    string DisplayName,
    string? Mail,
    string TenantId);

public static class FunctionContextExtensions
{
    internal const string CallerKey = "PermissionInsight.Caller";

    /// <summary>
    /// The caller for this invocation.
    /// </summary>
    /// <remarks>
    /// Throws rather than returning null when the middleware has not run. A
    /// handler that somehow became reachable without the app role check fails
    /// closed instead of serving tenant-wide permission data to an unchecked
    /// caller.
    /// </remarks>
    public static CallerContext GetCaller(this FunctionContext context)
    {
        if (context.Items.TryGetValue(CallerKey, out var value) && value is CallerContext caller)
        {
            return caller;
        }

        throw new InvalidOperationException(
            "No caller on this invocation. The authorisation middleware did not run, so the request has not been checked for the PermissionInsight.Use role.");
    }
}
