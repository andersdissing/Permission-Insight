using System.Net;
using System.Web;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using PermissionInsight.Api.Downstream;
using PermissionInsight.Api.Http;
using PermissionInsight.Api.Security;

namespace PermissionInsight.Api.Functions;

/// <summary>
/// Direct members of a SharePoint site group, fetched when the user expands a
/// row in the detail panel rather than during the scan. A site has a handful of
/// real groups and most are never expanded, so this is one call on click
/// instead of a call per group on every scan.
/// </summary>
public sealed class GroupsFunction
{
    private readonly SiteContextProvider _sites;
    private readonly SharePointClient _sharePoint;
    private readonly GraphClient _graph;

    public GroupsFunction(SiteContextProvider sites, SharePointClient sharePoint, GraphClient graph)
    {
        _sites = sites;
        _sharePoint = sharePoint;
        _graph = graph;
    }

    /// <summary>
    /// Direct members of an Entra group, through Graph.
    /// </summary>
    /// <remarks>
    /// Separate from the SharePoint site group route because the two are not
    /// interchangeable. SharePoint group membership lives behind an endpoint
    /// that read-only permissions cannot reach, and Graph has no equivalent
    /// for it. An Entra group can be opened; a SharePoint group cannot.
    /// </remarks>
    [Function("entra-group-members")]
    public async Task<HttpResponseData> EntraMembers(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "groups/entra-members")] HttpRequestData request,
        FunctionContext context)
    {
        _ = context.GetCaller();

        var groupId = HttpUtility.ParseQueryString(request.Url.Query)["groupId"];

        if (string.IsNullOrWhiteSpace(groupId) || !Guid.TryParse(groupId, out _))
        {
            return await request.ProblemAsync(
                HttpStatusCode.BadRequest,
                "invalid_request",
                "groupId must be a group object id.").ConfigureAwait(false);
        }

        try
        {
            var members = await _graph
                .GetEntraGroupMembersAsync(groupId, context.CancellationToken)
                .ConfigureAwait(false);

            return await request.OkAsync(new
            {
                groupId,
                members = members.Select(member => new
                {
                    name = member.Name,
                    email = member.Email,
                    external = member.External,
                    principalType = member.PrincipalType,
                    isEntraGroup = member.IsEntraGroup,
                }),
            }).ConfigureAwait(false);
        }
        catch (DownstreamException failure)
        {
            var message = failure.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "Reading group membership needs GroupMember.Read.All on the backend's managed identity. See docs/runbook-deployment.md."
                : "The group's members could not be read.";

            return await request.ProblemAsync(
                HttpStatusCode.BadGateway,
                "downstream_error",
                message,
                new Dictionary<string, object?> { ["downstreamStatus"] = (int)failure.Status }).ConfigureAwait(false);
        }
    }

    [Function("group-members")]
    public async Task<HttpResponseData> Members(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "groups/members")] HttpRequestData request,
        FunctionContext context)
    {
        _ = context.GetCaller();

        var query = HttpUtility.ParseQueryString(request.Url.Query);
        var siteId = query["siteId"];

        if (string.IsNullOrWhiteSpace(siteId) || !int.TryParse(query["groupId"], out var groupId))
        {
            return await request.ProblemAsync(
                HttpStatusCode.BadRequest,
                "invalid_request",
                "siteId and groupId are both required.").ConfigureAwait(false);
        }

        try
        {
            // Inside the try: resolving the site is a Graph call and can fail
            // the same way the member read can.
            var site = await _sites.GetAsync(siteId, context.CancellationToken).ConfigureAwait(false);
            if (site is null)
            {
                return await request.ProblemAsync(
                    HttpStatusCode.NotFound,
                    "site_not_found",
                    "That site doesn't exist.").ConfigureAwait(false);
            }

            var members = await _sharePoint
                .GetGroupMembersAsync(site.WebUrl, groupId, context.CancellationToken)
                .ConfigureAwait(false);

            return await request.OkAsync(new
            {
                groupId,
                members = members.Select(member => new
                {
                    name = member.Name,
                    email = member.Email,
                    external = member.External,
                    principalType = member.PrincipalType,
                    // Where this is true the chain stops and the row must say
                    // so. See docs/02-permissions.md for why the application
                    // cannot look inside.
                    isEntraGroup = member.IsEntraGroup,
                }),
            }).ConfigureAwait(false);
        }
        catch (DownstreamException failure)
        {
            return await request.ProblemAsync(
                HttpStatusCode.BadGateway,
                "downstream_error",
                $"{failure.Service} could not be reached.",
                new Dictionary<string, object?> { ["downstreamStatus"] = (int)failure.Status }).ConfigureAwait(false);
        }
    }
}
