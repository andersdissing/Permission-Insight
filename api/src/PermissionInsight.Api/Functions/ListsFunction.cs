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
/// The three calls behind the lists and libraries tab.
///
/// The browser drives the scan: it pages the items itself and asks about each
/// broken one in turn, because progress, cancellation and caching all belong to
/// the session rather than to the server. The backend stays a token holder and
/// a read-only proxy.
/// </summary>
public sealed class ListsFunction
{
    /// <summary>
    /// Excluded by title as well as by the Hidden flag. These exist in every
    /// site, none of them is what a governance team came to look at, and a
    /// toggle in the tab header brings them back.
    /// </summary>
    private static readonly HashSet<string> SystemListTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Form Templates",
        "Site Assets",
        "Style Library",
        "Preservation Hold Library",
        "Master Page Gallery",
        "User Information List",
        "Web Part Gallery",
        "Workflow History",
    };

    private readonly SiteContextProvider _sites;
    private readonly SharePointClient _sharePoint;
    private readonly GraphClient _graph;
    private readonly ApiOptions _options;
    private readonly ILogger<ListsFunction> _logger;

    public ListsFunction(
        SiteContextProvider sites,
        SharePointClient sharePoint,
        GraphClient graph,
        ApiOptions options,
        ILogger<ListsFunction> logger)
    {
        _sites = sites;
        _sharePoint = sharePoint;
        _graph = graph;
        _options = options;
        _logger = logger;
    }

    [Function("lists")]
    public async Task<HttpResponseData> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "lists")] HttpRequestData request,
        FunctionContext context)
    {
        var caller = context.GetCaller();
        var query = HttpUtility.ParseQueryString(request.Url.Query);

        try
        {
            var site = await ResolveSiteAsync(query["siteId"], context.CancellationToken).ConfigureAwait(false);
            if (site is null)
            {
                return await NoSuchSite(request).ConfigureAwait(false);
            }

            var lists = await _sharePoint.GetListsAsync(site.WebUrl, context.CancellationToken).ConfigureAwait(false);

            return await request.OkAsync(new
            {
                siteTitle = site.Title,
                siteWebUrl = site.WebUrl,
                lists = lists.Select(list => new
                {
                    id = list.Id,
                    title = list.Title,
                    itemCount = list.ItemCount,
                    isDocumentLibrary = list.IsDocumentLibrary,
                    isDefaultDocumentLibrary = list.IsDefaultDocumentLibrary,
                    // Null means SharePoint did not report it, which the
                    // screen shows as unknown rather than as inheriting.
                    hasUniqueRoleAssignments = list.HasUniqueRoleAssignments,
                    // Hidden and system lists are returned but flagged, so the
                    // "System lists" toggle needs no second round trip.
                    isSystem = list.Hidden || SystemListTitles.Contains(list.Title),
                }),
            }).ConfigureAwait(false);
        }
        catch (DownstreamException failure)
        {
            return await Failure(request, caller, failure).ConfigureAwait(false);
        }
    }

    /// <summary>Phase A of a scan: one page of items, flat, every depth.</summary>
    [Function("list-items")]
    public async Task<HttpResponseData> Items(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "lists/items")] HttpRequestData request,
        FunctionContext context)
    {
        var caller = context.GetCaller();
        var query = HttpUtility.ParseQueryString(request.Url.Query);

        var listId = query["listId"];
        if (!Guid.TryParse(listId, out _))
        {
            return await BadRequest(request, "listId must be a GUID.").ConfigureAwait(false);
        }

        try
        {
            var site = await ResolveSiteAsync(query["siteId"], context.CancellationToken).ConfigureAwait(false);
            if (site is null)
            {
                return await NoSuchSite(request).ConfigureAwait(false);
            }

            var page = await _sharePoint
                .GetListItemsPageAsync(site.WebUrl, listId, query["skipToken"], context.CancellationToken)
                .ConfigureAwait(false);

            return await request.OkAsync(new
            {
                items = page.Items.Select(item => new
                {
                    id = item.Id,
                    broken = item.HasUniqueRoleAssignments,
                    path = item.FileRef,
                    name = item.FileLeafRef,
                    isFolder = item.FileSystemObjectType == 1,
                }),
                nextToken = page.NextToken,
                // False means the identity can read content but not
                // permissions, so every item looks as though it inherits. The
                // browser must refuse to report a clean library rather than
                // pass that on.
                permissionDataAvailable = page.PermissionFlagPresent,
            }).ConfigureAwait(false);
        }
        catch (DownstreamException failure)
        {
            return await Failure(request, caller, failure).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Access granted on the list or library itself, which reaches everything
    /// inside that inherits.
    /// </summary>
    [Function("list-access")]
    public async Task<HttpResponseData> ListAccess(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "lists/access")] HttpRequestData request,
        FunctionContext context)
    {
        var caller = context.GetCaller();
        var query = HttpUtility.ParseQueryString(request.Url.Query);

        var listId = query["listId"];
        if (!Guid.TryParse(listId, out _))
        {
            return await BadRequest(request, "listId must be a GUID.").ConfigureAwait(false);
        }

        // Declared outside the try because the catch reports against it, but
        // resolved inside: site resolution is itself a Graph call, and a
        // failure there has to reach the same handler as any other.
        SiteContext? site = null;

        try
        {
            site = await ResolveSiteAsync(query["siteId"], context.CancellationToken).ConfigureAwait(false);
            if (site is null)
            {
                return await NoSuchSite(request).ConfigureAwait(false);
            }

            var access = await _graph
                .GetListAccessAsync(site.SiteId, listId, context.CancellationToken)
                .ConfigureAwait(false);

            return await request.OkAsync(new
            {
                listId,
                assignments = access.Select(entry => new
                {
                    principalId = 0,
                    principalName = entry.PrincipalName,
                    principalType = PrincipalTypeOf(entry.PrincipalKind),
                    loginName = entry.LoginName ?? string.Empty,
                    principalRef = entry.PrincipalRef,
                    roles = entry.Roles,
                    source = entry.Source,
                    linkScope = entry.LinkScope,
                    expiry = entry.Expiry,
                    // True when it comes from the site rather than the library.
                    inherited = entry.Inherited,
                    tenantWide = TenantWideOf(entry),
                }),
            }).ConfigureAwait(false);
        }
        catch (DownstreamException failure)
        {
            return await Failure(request, caller, failure, site).ConfigureAwait(false);
        }
    }

    /// <summary>Phase B of a scan: the assignments on one item that stopped inheriting.</summary>
    [Function("item-permissions")]
    public async Task<HttpResponseData> Permissions(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "lists/permissions")] HttpRequestData request,
        FunctionContext context)
    {
        var caller = context.GetCaller();
        var query = HttpUtility.ParseQueryString(request.Url.Query);

        var listId = query["listId"];
        if (!Guid.TryParse(listId, out _) || !int.TryParse(query["itemId"], out var itemId))
        {
            return await BadRequest(request, "listId must be a GUID and itemId a number.").ConfigureAwait(false);
        }

        SiteContext? site = null;

        try
        {
            site = await ResolveSiteAsync(query["siteId"], context.CancellationToken).ConfigureAwait(false);
            if (site is null)
            {
                return await NoSuchSite(request).ConfigureAwait(false);
            }

            // Graph, not SharePoint. SharePoint's roleassignments endpoint
            // refuses an app-only Sites.Read.All token because it needs
            // EnumeratePermissions, which lives in Full Control. Graph answers
            // the same question with the permission we already hold, and
            // reaches every list type rather than document libraries only.
            // See docs/adr/0011.
            var access = await _graph
                .GetItemAccessAsync(site.SiteId, listId, itemId, context.CancellationToken)
                .ConfigureAwait(false);

            return await request.OkAsync(new
            {
                itemId,
                assignments = access
                    // Inherited entries belong to the ancestor they came from.
                    // Listing them here would make one broken folder look like
                    // a hundred.
                    .Where(entry => !entry.Inherited)
                    .Select(entry => new
                    {
                        principalId = 0,
                        principalName = entry.PrincipalName,
                        principalType = PrincipalTypeOf(entry.PrincipalKind),
                        loginName = entry.LoginName ?? string.Empty,
                        // What a deep link needs, and what expanding an Entra
                        // group is addressed by.
                        principalRef = entry.PrincipalRef,
                        roles = entry.Roles,
                        source = entry.Source,
                        linkScope = entry.LinkScope,
                        expiry = entry.Expiry,
                        tenantWide = TenantWideOf(entry),
                    }),
            }).ConfigureAwait(false);
        }
        catch (DownstreamException failure)
        {
            // The site is passed here and nowhere else in this class: reading
            // role assignments is the call that needs a site grant, so this is
            // the only refusal that means "not onboarded" rather than "broken".
            return await Failure(request, caller, failure, site).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The tenant-wide claim behind a row, flattened for the wire. Non-null
    /// means this single assignment reaches the whole organisation, which the
    /// tree and the detail panel both style differently.
    /// </summary>
    private static object? TenantWideOf(ItemAccess entry) => entry.TenantWide is { } wide
        ? new
        {
            code = wide.Code,
            label = wide.Label,
            description = wide.Description,
            includesExternal = wide.IncludesExternal,
        }
        : null;

    /// <summary>Maps Graph's identity kinds onto SharePoint's principal type numbers, which the front end already understands.</summary>
    private static int PrincipalTypeOf(string kind) => kind switch
    {
        "User" => 1,
        "EntraGroup" => 4,
        "SharePointGroup" => 8,
        _ => 0,
    };

    private async Task<SiteContext?> ResolveSiteAsync(string? siteId, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(siteId)
            ? null
            : await _sites.GetAsync(siteId, cancellationToken).ConfigureAwait(false);

    private static Task<HttpResponseData> NoSuchSite(HttpRequestData request) =>
        request.ProblemAsync(HttpStatusCode.NotFound, "site_not_found", "That site doesn't exist.");

    private static Task<HttpResponseData> BadRequest(HttpRequestData request, string message) =>
        request.ProblemAsync(HttpStatusCode.BadRequest, "invalid_request", message);

    private async Task<HttpResponseData> Failure(
        HttpRequestData request,
        CallerContext caller,
        DownstreamException failure) => await Failure(request, caller, failure, site: null).ConfigureAwait(false);

    private async Task<HttpResponseData> Failure(
        HttpRequestData request,
        CallerContext caller,
        DownstreamException failure,
        SiteContext? site)
    {
        _logger.LogError(
            "List scan failed for {ActorObjectId}: {Service} returned {Status} {DownstreamCode}",
            caller.ObjectId,
            failure.Service,
            (int)failure.Status,
            failure.Code ?? "-");

        // Throttling and a permission failure look alike from the browser and
        // only one of them is worth retrying, so they are answered differently.
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

        // Reading permissions is the part that needs a site grant. A refusal
        // almost always means this site has not been onboarded, which is a
        // missing step rather than a fault.
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
