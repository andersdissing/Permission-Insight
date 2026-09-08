using System.Collections.Concurrent;

namespace PermissionInsight.Api.Downstream;

/// <summary>
/// Resolves a Graph site id into the site's address and its libraries, and
/// remembers the answer for a few minutes.
///
/// Two reasons this exists rather than the browser passing a site URL. It saves
/// two Graph calls on every one of the hundreds of requests a sharing link scan
/// makes; and it means the backend never takes an address from the caller, so
/// there is no way to point an application token that can read every site in
/// the tenant at somewhere it should not go.
/// </summary>
public sealed class SiteContextProvider
{
    /// <summary>
    /// Short enough that a library added mid-session appears without a
    /// restart, long enough to cover a scan of several hundred links.
    /// </summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly GraphClient _graph;
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, SiteContext Context)> _cache = new(StringComparer.Ordinal);

    public SiteContextProvider(GraphClient graph) => _graph = graph;

    /// <returns>Null when no such site exists.</returns>
    public async Task<SiteContext?> GetAsync(string siteId, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(siteId, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
        {
            return cached.Context;
        }

        var site = await _graph.GetSiteAsync(siteId, cancellationToken).ConfigureAwait(false);
        if (site is null)
        {
            return null;
        }

        var drives = await _graph.GetDrivesAsync(siteId, cancellationToken).ConfigureAwait(false);
        var context = new SiteContext(site.Id, site.Title, site.WebUrl.TrimEnd('/'), drives);

        _cache[siteId] = (DateTimeOffset.UtcNow.Add(Lifetime), context);
        return context;
    }
}
