using System.Net;
using System.Web;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using PermissionInsight.Api.Downstream;
using PermissionInsight.Api.Http;
using PermissionInsight.Api.Security;

namespace PermissionInsight.Api.Functions;

/// <summary>
/// The picker behind "Search for people or Entra ID group".
/// </summary>
/// <remarks>
/// Only the lookup lives here. Working out what a principal can reach happens
/// in the browser against the cached scan, because the scan already recorded
/// every principal named on every item and asking SharePoint again would add
/// nothing.
///
/// This once called getusereffectivepermissions, which resolved the whole
/// group chain server-side. That endpoint refuses an app-only read-only token,
/// so the chain is walked one level at a time instead. See docs/adr/0012.
/// </remarks>
public sealed class PeopleFunction
{
    private const int PickerResults = 15;

    private readonly GraphClient _graph;

    public PeopleFunction(GraphClient graph) => _graph = graph;

    /// <summary>Users and Entra groups together, because a permission is as likely to be held by one as the other.</summary>
    [Function("people")]
    public async Task<HttpResponseData> Search(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "people")] HttpRequestData request,
        FunctionContext context)
    {
        _ = context.GetCaller();

        var query = (HttpUtility.ParseQueryString(request.Url.Query)["q"] ?? string.Empty).Trim();

        if (query.Length < 2)
        {
            return await request.ProblemAsync(
                HttpStatusCode.BadRequest,
                "query_too_short",
                "Search for at least 2 characters.").ConfigureAwait(false);
        }

        try
        {
            var people = await _graph
                .SearchPeopleAsync(query, PickerResults, context.CancellationToken)
                .ConfigureAwait(false);

            return await request.OkAsync(new
            {
                results = people.Select(person => new
                {
                    id = person.Id,
                    displayName = person.DisplayName,
                    mail = person.Mail,
                    userPrincipalName = person.UserPrincipalName,
                    kind = person.Kind,
                }),
            }).ConfigureAwait(false);
        }
        catch (DownstreamException failure)
        {
            if (failure.IsThrottled)
            {
                var throttled = await request.ProblemAsync(
                    HttpStatusCode.TooManyRequests,
                    "throttled",
                    "Microsoft Graph is throttling this tenant. Try again shortly.").ConfigureAwait(false);

                if (failure.RetryAfter is { } retryAfter)
                {
                    throttled.Headers.Add("Retry-After", retryAfter);
                }

                return throttled;
            }

            var message = failure.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "Searching people and groups needs User.ReadBasic.All and GroupMember.Read.All on the backend's managed identity. See docs/runbook-deployment.md."
                : "Microsoft Graph could not be reached.";

            return await request.ProblemAsync(
                HttpStatusCode.BadGateway,
                "downstream_error",
                message,
                new Dictionary<string, object?> { ["downstreamStatus"] = (int)failure.Status }).ConfigureAwait(false);
        }
    }
}
