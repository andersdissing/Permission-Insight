using System.Net;
using System.Web;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using PermissionInsight.Api.Auditing;
using PermissionInsight.Api.Http;
using PermissionInsight.Api.Security;

namespace PermissionInsight.Api.Functions;

/// <summary>
/// Records the actions the specification wants logged in their own right
/// rather than as one request among many: an export, because that is the last
/// moment data can be recorded before it leaves the application, and a person
/// lookup, because it profiles a named individual.
/// </summary>
/// <remarks>
/// A POST, because it records something. It writes to the audit log and
/// nothing else: the read-only rule concerns what may be sent to SharePoint
/// and Graph, and that list is separate and unchanged. See
/// <see cref="HttpMethodPolicy"/> for the two directions.
/// </remarks>
public sealed class AuditFunction
{
    private static readonly HashSet<string> KnownActions = new(StringComparer.Ordinal)
    {
        "export",
        "person-lookup",
    };

    private readonly AuditLog _audit;

    public AuditFunction(AuditLog audit) => _audit = audit;

    [Function("audit")]
    public async Task<HttpResponseData> Record(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "audit/{action}")] HttpRequestData request,
        FunctionContext context,
        string action)
    {
        var caller = context.GetCaller();

        if (!KnownActions.Contains(action))
        {
            return await request.ProblemAsync(
                HttpStatusCode.BadRequest,
                "unknown_action",
                $"'{action}' is not an auditable action.").ConfigureAwait(false);
        }

        var query = HttpUtility.ParseQueryString(request.Url.Query);

        // The middleware has already recorded who called and with what query.
        // This adds the named action, so an export can be found without
        // knowing the route it happened to arrive on. Both values are caller
        // supplied free text; AuditLog caps them.
        _audit.RecordAction(
            caller,
            action,
            target: query["target"] ?? "-",
            subject: query["subject"]);

        return await request.OkAsync(new { recorded = true }).ConfigureAwait(false);
    }
}
