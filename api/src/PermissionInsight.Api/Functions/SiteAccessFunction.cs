using System.Net;
using System.Web;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using PermissionInsight.Api.Downstream;
using PermissionInsight.Api.Http;
using PermissionInsight.Api.Security;

namespace PermissionInsight.Api.Functions;

/// <summary>
/// Who has access at the site itself.
/// </summary>
/// <remarks>
/// This is the widest scope the tool reports, and until now it was the one
/// scope it never showed. A grant here reaches every library and every item
/// that still inherits, which on a typical site is nearly everything — so a
/// tenant-wide claim granted at the site is a larger finding than anything the
/// item scan can turn up, and it was invisible.
///
/// <para><b>How it is read.</b> SharePoint's own <c>/_api/web/roleassignments</c>
/// refuses an app-only <c>Sites.Read.All</c> token, and Graph has no "web
/// permissions" route. So the site's assignments are read indirectly: a list
/// that inherits has, by definition, exactly the site's permissions, so
/// reading such a list's permissions reads the site's. The list used is named
/// in the response rather than hidden, because a derived answer that does not
/// say what it was derived from is not auditable.</para>
///
/// <para>When every list on the site has unique permissions there is nothing
/// to derive from, and the answer is "undetermined" rather than "empty". That
/// distinction is the whole point: this application has been wrong in that
/// exact way four times, and an empty access list reads as "nobody has
/// access", which is the most dangerous thing it could say.</para>
/// </remarks>
public sealed class SiteAccessFunction
{
    private readonly SiteContextProvider _sites;
    private readonly SharePointClient _sharePoint;
    private readonly GraphClient _graph;
    private readonly ILogger<SiteAccessFunction> _logger;

    public SiteAccessFunction(
        SiteContextProvider sites,
        SharePointClient sharePoint,
        GraphClient graph,
        ILogger<SiteAccessFunction> logger)
    {
        _sites = sites;
        _sharePoint = sharePoint;
        _graph = graph;
        _logger = logger;
    }

    [Function("site-access")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sites/access")] HttpRequestData request,
        FunctionContext context)
    {
        var caller = context.GetCaller();
        var query = HttpUtility.ParseQueryString(request.Url.Query);

        SiteContext? site = null;

        try
        {
            site = await ResolveAsync(query["siteId"], context.CancellationToken).ConfigureAwait(false);
            if (site is null)
            {
                return await request.ProblemAsync(
                    HttpStatusCode.NotFound,
                    "site_not_found",
                    "That site doesn't exist.").ConfigureAwait(false);
            }

            var lists = await _sharePoint
                .GetListsAsync(site.WebUrl, context.CancellationToken)
                .ConfigureAwait(false);

            // Several candidates rather than one. The first attempt used only
            // lists SharePoint flagged as inheriting, which turned out to be
            // unusable: /_api/web/lists does not reliably return
            // HasUniqueRoleAssignments, so on a real tenant every list read as
            // "unknown" and the site's permissions were never derived at all.
            ListSummary? source = null;
            IReadOnlyList<ItemAccess> inherited = [];

            foreach (var candidate in Candidates(lists))
            {
                var access = await _graph
                    .GetListAccessAsync(site.SiteId, candidate.Id, context.CancellationToken)
                    .ConfigureAwait(false);

                // Two ways to recognise the site's own assignments, because
                // only one of them is always available. When SharePoint did say
                // the list inherits, every entry on it is the site's. Otherwise
                // fall back on Graph's own inheritedFrom marker, which needs no
                // flag from SharePoint at all.
                var entries = candidate.HasUniqueRoleAssignments == false
                    ? access
                    : access.Where(entry => entry.Inherited).ToArray();

                if (entries.Count > 0)
                {
                    source = candidate;
                    inherited = entries;
                    break;
                }
            }

            if (source is null)
            {
                return await request.OkAsync(new
                {
                    siteTitle = site.Title,
                    siteWebUrl = site.WebUrl,
                    determined = false,
                    // Plain language, and explicit about the consequence. The
                    // reader needs to know what is missing from the screen, not
                    // how the derivation works.
                    reason =
                        "Permission Insight couldn't read the permissions set on the site itself. " +
                        "It reads them through a list that inherits from the site, and no list here reported any inherited permission. " +
                        "Anything granted at the site — including a grant to the whole organisation — wouldn't be listed below.",
                    derivedFrom = (object?)null,
                    assignments = Array.Empty<object>(),
                }).ConfigureAwait(false);
            }

            return await request.OkAsync(new
            {
                siteTitle = site.Title,
                siteWebUrl = site.WebUrl,
                determined = true,
                // Named so the derivation can be checked rather than trusted.
                derivedFrom = new { listId = source.Id, listTitle = source.Title },
                assignments = inherited
                    // Limited Access is granted automatically so a recipient can
                    // navigate to something shared deeper in. It is noise here as
                    // everywhere else.
                    .Where(entry => !entry.Roles.Contains(RoleAssignmentSummary.LimitedAccess))
                    .Select(entry => new
                    {
                        principalName = entry.PrincipalName,
                        principalType = PrincipalTypeOf(entry.PrincipalKind),
                        loginName = entry.LoginName ?? string.Empty,
                        principalRef = entry.PrincipalRef,
                        roles = entry.Roles,
                        source = entry.Source,
                        linkScope = entry.LinkScope,
                        expiry = entry.Expiry,
                        // The reason this endpoint exists. Non-null means this
                        // one row reaches the whole organisation.
                        tenantWide = entry.TenantWide is { } wide
                            ? new
                            {
                                code = wide.Code,
                                label = wide.Label,
                                description = wide.Description,
                                includesExternal = wide.IncludesExternal,
                            }
                            : null,
                    }),
            }).ConfigureAwait(false);
        }
        catch (DownstreamException failure)
        {
            _logger.LogError(
                "Site access failed for {ActorObjectId}: {Service} returned {Status} {DownstreamCode}",
                caller.ObjectId,
                failure.Service,
                (int)failure.Status,
                failure.Code ?? "-");

            if (failure.IsThrottled)
            {
                var throttled = await request.ProblemAsync(
                    HttpStatusCode.TooManyRequests,
                    "throttled",
                    "SharePoint is throttling this tenant. Try again shortly.").ConfigureAwait(false);

                if (failure.RetryAfter is { } retry)
                {
                    throttled.Headers.Add("Retry-After", retry);
                }

                return throttled;
            }

            return await request.ProblemAsync(
                HttpStatusCode.BadGateway,
                "downstream_error",
                $"{failure.Service} could not be reached.",
                new Dictionary<string, object?> { ["downstreamStatus"] = (int)failure.Status })
                .ConfigureAwait(false);
        }
    }

    /// <summary>How many lists to try before giving up. Each one costs a call.</summary>
    private const int MaxCandidates = 4;

    /// <summary>
    /// Lists worth asking, best first.
    /// </summary>
    /// <remarks>
    /// Ordered rather than filtered. A list SharePoint said inherits is the
    /// strongest answer, but the flag is frequently absent from the lists
    /// collection, so a list it said nothing about is the next best thing —
    /// Graph's inheritedFrom marker still identifies the site's entries on it.
    /// A list known to have unique permissions goes last: it can only
    /// contribute if it happens to carry an inherited entry anyway.
    ///
    /// Within each group the default document library comes first, because it
    /// is the one a reader can most easily check by hand against SharePoint.
    /// </remarks>
    internal static IEnumerable<ListSummary> Candidates(IReadOnlyList<ListSummary> lists) =>
        lists
            .OrderBy(list => list.HasUniqueRoleAssignments switch
            {
                false => 0,
                null => 1,
                true => 2,
            })
            .ThenBy(list => list.IsDefaultDocumentLibrary ? 0 : 1)
            .ThenBy(list => list.IsDocumentLibrary ? 0 : 1)
            .ThenBy(list => list.Hidden ? 1 : 0)
            .Take(MaxCandidates);

    private static int PrincipalTypeOf(string kind) => kind switch
    {
        "User" => 1,
        "EntraGroup" => 4,
        "SharePointGroup" => 8,
        _ => 0,
    };

    private async Task<SiteContext?> ResolveAsync(string? siteId, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(siteId)
            ? null
            : await _sites.GetAsync(siteId, cancellationToken).ConfigureAwait(false);
}
