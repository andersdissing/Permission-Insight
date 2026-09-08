using System.Net;
using System.Web;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using PermissionInsight.Api.Configuration;
using PermissionInsight.Api.Downstream;
using PermissionInsight.Api.Http;
using PermissionInsight.Api.Security;

namespace PermissionInsight.Api.Functions;

/// <summary>
/// The two calls behind the sharing links tab.
///
/// The split matches the two-stage load. One call returns every link in the
/// site collection, which is where the total comes from and is fast enough to
/// need no progress indicator. The other resolves a single link into a path
/// with recipients, and the browser drives those from the top of the list so
/// that progress is visible and cancellable.
/// </summary>
public sealed class SharingLinksFunction
{
    private readonly SiteContextProvider _sites;
    private readonly SharePointClient _sharePoint;
    private readonly GraphClient _graph;
    private readonly ApiOptions _options;
    private readonly ILogger<SharingLinksFunction> _logger;

    public SharingLinksFunction(
        SiteContextProvider sites,
        SharePointClient sharePoint,
        GraphClient graph,
        ApiOptions options,
        ILogger<SharingLinksFunction> logger)
    {
        _sites = sites;
        _sharePoint = sharePoint;
        _graph = graph;
        _options = options;
        _logger = logger;
    }

    /// <summary>Stage one: every sharing link in the site collection, from one paged call.</summary>
    [Function("sharing-links")]
    public async Task<HttpResponseData> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sharing-links")] HttpRequestData request,
        FunctionContext context)
    {
        var caller = context.GetCaller();
        var query = HttpUtility.ParseQueryString(request.Url.Query);
        var siteId = query["siteId"];

        if (string.IsNullOrWhiteSpace(siteId))
        {
            return await BadRequest(request, "siteId is required.").ConfigureAwait(false);
        }

        // Declared outside the try so the failure handler can name the site
        // when it explains that the site has not been onboarded.
        SiteContext? site = null;

        try
        {
            site = await _sites.GetAsync(siteId, context.CancellationToken).ConfigureAwait(false);
            if (site is null)
            {
                return await request.ProblemAsync(
                    HttpStatusCode.NotFound,
                    "site_not_found",
                    "That site doesn't exist.").ConfigureAwait(false);
            }

            var links = await _sharePoint
                .GetSharingLinkGroupsAsync(site.WebUrl, context.CancellationToken)
                .ConfigureAwait(false);

            return await request.OkAsync(new
            {
                siteTitle = site.Title,
                siteWebUrl = site.WebUrl,
                links = links.Select(link => new
                {
                    groupId = link.GroupId,
                    itemGuid = link.ItemGuid,
                    kind = link.Kind,
                    linkGuid = link.LinkGuid,
                }),
            }).ConfigureAwait(false);
        }
        catch (DownstreamException failure)
        {
            return await Failure(request, caller, failure, site).ConfigureAwait(false);
        }
    }

    /// <summary>Stage two: one link, resolved into a path with scope, recipients and expiry.</summary>
    [Function("sharing-link-detail")]
    public async Task<HttpResponseData> Detail(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sharing-links/detail")] HttpRequestData request,
        FunctionContext context)
    {
        var caller = context.GetCaller();
        var query = HttpUtility.ParseQueryString(request.Url.Query);

        var siteId = query["siteId"];
        var itemGuid = query["itemGuid"];
        var linkGuid = query["linkGuid"];

        // Both identifiers are checked as GUIDs, not merely as present. They are
        // interpolated into a SharePoint OData literal — GetFileById('...') —
        // and escaping alone leaves the question of how SharePoint decodes the
        // segment. A GUID cannot carry a quote, so the question does not arise.
        if (string.IsNullOrWhiteSpace(siteId) ||
            !Guid.TryParse(itemGuid, out _) ||
            !Guid.TryParse(linkGuid, out _) ||
            !int.TryParse(query["groupId"], out var groupId))
        {
            return await BadRequest(
                request,
                "siteId is required, itemGuid and linkGuid must be GUIDs, and groupId must be a number.")
                .ConfigureAwait(false);
        }

        // Declared outside the try so the failure handler can name the site
        // when it explains that the site has not been onboarded.
        SiteContext? site = null;

        try
        {
            site = await _sites.GetAsync(siteId, context.CancellationToken).ConfigureAwait(false);
            if (site is null)
            {
                return await request.ProblemAsync(
                    HttpStatusCode.NotFound,
                    "site_not_found",
                    "That site doesn't exist.").ConfigureAwait(false);
            }

            var detail = await ResolveAsync(site, groupId, itemGuid, linkGuid, context.CancellationToken)
                .ConfigureAwait(false);

            return await request.OkAsync(detail).ConfigureAwait(false);
        }
        catch (DownstreamException failure)
        {
            return await Failure(request, caller, failure, site).ConfigureAwait(false);
        }
    }

    private async Task<SharingLinkDetail> ResolveAsync(
        SiteContext site,
        int groupId,
        string itemGuid,
        string linkGuid,
        CancellationToken cancellationToken)
    {
        (string Path, string ItemType)? item;

        try
        {
            item = await _sharePoint
                .ResolveItemAsync(site.WebUrl, itemGuid, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DownstreamException refused) when (SiteOnboarding.LooksLikeMissingGrant(refused))
        {
            // Refused, not absent. Falling through to the orphan branch would
            // read this as "the item was deleted", which is a finding, and
            // reporting a deletion that did not happen is worse than reporting
            // nothing. The row says it could not look.
            return new SharingLinkDetail(
                itemGuid,
                linkGuid,
                SharingLinkStatus.ItemUnreadable,
                null,
                "unknown",
                null,
                []);
        }

        if (item is null)
        {
            // The item is gone but the group survived it. Keep the members:
            // an orphaned group still holding an external guest is a finding
            // in its own right, and one that does not appear in Microsoft's
            // own sharing reports.
            //
            // Membership is the single read that genuinely needs more than
            // Sites.Read.All (adr/0013), so a refusal here degrades this one
            // field instead of failing the row and blaming the whole site.
            try
            {
                var members = await _sharePoint
                    .GetGroupMembersAsync(site.WebUrl, groupId, cancellationToken)
                    .ConfigureAwait(false);

                return new SharingLinkDetail(
                    itemGuid,
                    linkGuid,
                    SharingLinkStatus.Orphaned,
                    null,
                    "unknown",
                    null,
                    [.. members.Select(member => member.AsRecipient())]);
            }
            catch (DownstreamException refused) when (SiteOnboarding.LooksLikeMissingGrant(refused))
            {
                return new SharingLinkDetail(
                    itemGuid,
                    linkGuid,
                    SharingLinkStatus.Orphaned,
                    null,
                    "unknown",
                    null,
                    [],
                    MembersUnavailable: true);
            }
        }

        var (path, itemType) = item.Value;

        var location = site.Locate(path);
        if (location is null)
        {
            // The path is outside every document library, so there is no drive
            // to ask about permissions. Show the path rather than pretending
            // the link does not exist.
            return new SharingLinkDetail(
                itemGuid,
                linkGuid,
                SharingLinkStatus.ScopeUnavailable,
                path,
                itemType,
                null,
                [],
                ScopeReason: ScopeUnavailableReason.OutsideLibrary);
        }

        var permissions = await _graph
            .GetLinkPermissionsAsync(location.Value.DriveId, location.Value.PathInDrive, cancellationToken)
            .ConfigureAwait(false);

        var permission = MatchLink(permissions, linkGuid);

        return permission is null
            ? new SharingLinkDetail(
                itemGuid,
                linkGuid,
                SharingLinkStatus.ScopeUnavailable,
                path,
                itemType,
                null,
                [],
                ScopeReason: ScopeUnavailableReason.LinkNotMatched)
            : new SharingLinkDetail(itemGuid, linkGuid, SharingLinkStatus.Resolved, path, itemType, permission, permission.Recipients);
    }

    /// <summary>
    /// Picks the permission belonging to one sharing link group.
    /// </summary>
    /// <remarks>
    /// The group name ends in the link's GUID and Graph gives each permission
    /// an id, but the two are not documented as the same value, so this tries
    /// the identifications it can justify and then stops rather than guessing:
    ///
    ///   1. The permission id is the link GUID.
    ///   2. The link's share URL contains the GUID.
    ///   3. The item carries exactly one link, so there is nothing to confuse
    ///      it with.
    ///
    /// If none of those hold, the row resolves as ScopeUnavailable. Showing
    /// the wrong scope against a path would be worse than showing none: the
    /// whole tab exists to answer who can reach a file.
    ///
    /// `VERIFY` which branch fires in practice — scripts/probe-verify.ps1
    /// reports it. If branch 1 always wins, 2 and 3 can go.
    /// </remarks>
    internal static LinkPermission? MatchLink(IReadOnlyList<LinkPermission> permissions, string linkGuid)
    {
        var byId = permissions.FirstOrDefault(permission =>
            permission.LinkId.Equals(linkGuid, StringComparison.OrdinalIgnoreCase));

        if (byId is not null)
        {
            return byId;
        }

        var byShareUrl = permissions.FirstOrDefault(permission =>
            permission.LinkId.Contains(linkGuid, StringComparison.OrdinalIgnoreCase));

        if (byShareUrl is not null)
        {
            return byShareUrl;
        }

        return permissions.Count == 1 ? permissions[0] : null;
    }

    private static Task<HttpResponseData> BadRequest(HttpRequestData request, string message) =>
        request.ProblemAsync(HttpStatusCode.BadRequest, "invalid_request", message);

    private async Task<HttpResponseData> Failure(
        HttpRequestData request,
        CallerContext caller,
        DownstreamException failure,
        SiteContext? site = null)
    {
        _logger.LogError(
            "Sharing links failed for {ActorObjectId}: {Service} returned {Status} {DownstreamCode}",
            caller.ObjectId,
            failure.Service,
            (int)failure.Status,
            failure.Code ?? "-");

        if (failure.IsThrottled)
        {
            var throttled = await request.ProblemAsync(
                HttpStatusCode.TooManyRequests,
                "throttled",
                "SharePoint is throttling this tenant. The scan will continue on its own.").ConfigureAwait(false);

            if (failure.RetryAfter is { } retryAfter)
            {
                throttled.Headers.Add("Retry-After", retryAfter);
            }

            return throttled;
        }

        // Every call this function makes reads permission data, so a refusal
        // means the site has not been granted to the tool yet.
        if (site is not null && SiteOnboarding.LooksLikeMissingGrant(failure))
        {
            return await request.ProblemAsync(
                HttpStatusCode.Forbidden,
                SiteOnboarding.ErrorCode,
                SiteOnboarding.Message(site.Title),
                SiteOnboarding.Details(site.WebUrl, _options.ManagedIdentityClientId ?? "the backend identity", failure.Status))
                .ConfigureAwait(false);
        }

        return await request.ProblemAsync(
            HttpStatusCode.BadGateway,
            "downstream_error",
            $"{failure.Service} could not be reached.",
            new Dictionary<string, object?> { ["downstreamStatus"] = (int)failure.Status }).ConfigureAwait(false);
    }
}
