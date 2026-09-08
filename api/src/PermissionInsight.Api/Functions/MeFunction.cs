using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using PermissionInsight.Api.Http;
using PermissionInsight.Api.Security;

namespace PermissionInsight.Api.Functions;

/// <summary>
/// Who the caller is, read from the token that has already been validated. No
/// Graph call. It exists so the top bar has a name to show and so a deployment
/// can be confirmed end to end without touching SharePoint.
/// </summary>
public sealed class MeFunction
{
    // AuthorizationLevel.Anonymous throughout the API, deliberately. A function
    // key would be a second credential, weaker than the app role check and
    // capable of admitting a request the role check would refuse. The
    // authorisation middleware is the only gate.
    [Function("me")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "me")] HttpRequestData request,
        FunctionContext context)
    {
        var caller = context.GetCaller();

        return await request.OkAsync(new
        {
            objectId = caller.ObjectId,
            displayName = caller.DisplayName,
            mail = caller.Mail,
        });
    }
}
