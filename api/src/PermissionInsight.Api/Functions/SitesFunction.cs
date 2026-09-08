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
/// Site search across the whole tenant.
/// </summary>
public sealed class SitesFunction
{
    /// <summary>
    /// Twenty rows, then a line telling the user to refine. Pagination is not
    /// worth building for an audience that already knows which site it wants.
    /// </summary>
    private const int MaxResults = 20;

    private const int MinimumQueryLength = 2;

    private readonly GraphClient _graph;
    private readonly ILogger<SitesFunction> _logger;

    public SitesFunction(GraphClient graph, ILogger<SitesFunction> logger)
    {
        _graph = graph;
        _logger = logger;
    }

    [Function("sites")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sites")] HttpRequestData request,
        FunctionContext context)
    {
        // Throws unless the authorisation middleware ran and admitted the
        // caller. The search itself is already in the audit log by this point.
        var caller = context.GetCaller();

        var query = (HttpUtility.ParseQueryString(request.Url.Query)["q"] ?? string.Empty).Trim();

        if (query.Length < MinimumQueryLength)
        {
            return await request.ProblemAsync(
                HttpStatusCode.BadRequest,
                "query_too_short",
                $"Search for at least {MinimumQueryLength} characters.");
        }

        try
        {
            var result = await _graph
                .SearchSitesAsync(query, MaxResults, context.CancellationToken)
                .ConfigureAwait(false);

            return await request.OkAsync(new
            {
                results = result.Results,
                truncated = result.Truncated,
            });
        }
        catch (DownstreamException failure)
        {
            return await DescribeFailure(request, caller, failure).ConfigureAwait(false);
        }
    }

    private async Task<HttpResponseData> DescribeFailure(
        HttpRequestData request,
        CallerContext caller,
        DownstreamException failure)
    {
        _logger.LogError(
            "Site search failed for {ActorObjectId}: {Service} returned {Status} {DownstreamCode}",
            caller.ObjectId,
            failure.Service,
            (int)failure.Status,
            failure.Code ?? "-");

        if (failure.IsThrottled)
        {
            // Passed through rather than retried, so the browser can show the
            // backoff in its own progress text.
            var throttled = await request.ProblemAsync(
                HttpStatusCode.TooManyRequests,
                "throttled",
                "SharePoint is throttling this tenant. Try again shortly.").ConfigureAwait(false);

            if (failure.RetryAfter is { } retryAfter)
            {
                throttled.Headers.Add("Retry-After", retryAfter);
            }

            return throttled;
        }

        // A 401 or 403 from Graph here almost always means the managed
        // identity never received Sites.Read.All, which is the half of the
        // deployment that needs a privileged account. Say so, because the
        // alternative is an empty result set that looks like a working tool
        // with nothing to find.
        var message = failure.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? "The backend's managed identity was refused by Microsoft Graph. Check that it holds Sites.Read.All; see docs/runbook-deployment.md."
            : "Microsoft Graph could not be reached.";

        return await request.ProblemAsync(
            HttpStatusCode.BadGateway,
            "downstream_error",
            message,
            new Dictionary<string, object?> { ["downstreamStatus"] = (int)failure.Status }).ConfigureAwait(false);
    }
}
