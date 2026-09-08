using System.Collections.Concurrent;
using Azure.Core;
using Azure.Identity;
using PermissionInsight.Api.Configuration;

namespace PermissionInsight.Api.Downstream;

public interface IDownstreamTokenProvider
{
    ValueTask<string> GetTokenAsync(string scope, CancellationToken cancellationToken);
}

/// <summary>
/// Acquires the application-only tokens the backend calls Graph and SharePoint
/// with.
///
/// In a deployed environment there is exactly one way to get one: the
/// user-assigned managed identity. No secret, no certificate, no fallback
/// chain. Remove the identity assignment from the function app and every
/// downstream call fails, which is the assertion phase 1 acceptance test 11
/// makes — a second credential path would make that test pass while the
/// architecture had quietly changed.
/// </summary>
public sealed class DownstreamTokenProvider : IDownstreamTokenProvider
{
    private readonly TokenCredential _credential;
    private readonly ConcurrentDictionary<string, AccessToken> _cache = new(StringComparer.Ordinal);

    /// <summary>Renew early, so a token never expires mid-request.</summary>
    private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(5);

    public DownstreamTokenProvider(ApiOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ManagedIdentityClientId))
        {
            _credential = new ManagedIdentityCredential(
                new ManagedIdentityCredentialOptions(
                    ManagedIdentityId.FromUserAssignedClientId(options.ManagedIdentityClientId)));
        }
        else if (options.IsDevelopment)
        {
            // Local development only, and reachable only because ApiOptions
            // refuses to start without a managed identity client id anywhere
            // else. Sign in with az login as an account that holds the same
            // application permissions.
            _credential = new AzureCliCredential();
        }
        else
        {
            throw new InvalidOperationException(
                "No managed identity is configured. The backend has no other way to authenticate downstream, and must not acquire one.");
        }
    }

    public async ValueTask<string> GetTokenAsync(string scope, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(scope, out var cached) && cached.ExpiresOn - RenewBefore > DateTimeOffset.UtcNow)
        {
            return cached.Token;
        }

        var token = await _credential
            .GetTokenAsync(new TokenRequestContext([scope]), cancellationToken)
            .ConfigureAwait(false);

        _cache[scope] = token;
        return token.Token;
    }
}
