using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using PermissionInsight.Api.Auditing;
using PermissionInsight.Api.Http;

namespace PermissionInsight.Api.Security;

/// <summary>
/// Runs before every function invocation, with no per-function opt-in. This is
/// the "single router that wraps every handler" phase 1 asks for: a handler
/// cannot be registered in a way that skips it, and a handler that somehow ran
/// without it would fail on <see cref="FunctionContextExtensions.GetCaller"/>
/// rather than serve data.
///
/// The middleware itself holds no policy. It adapts the HTTP request into
/// <see cref="RequestGate"/> and turns the answer back into a response.
/// </summary>
public sealed class AuthorizationMiddleware : IFunctionsWorkerMiddleware
{
    private readonly RequestGate _gate;
    private readonly AuditLog _audit;

    public AuthorizationMiddleware(RequestGate gate, AuditLog audit)
    {
        _gate = gate;
        _audit = audit;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var request = await context.GetHttpRequestDataAsync().ConfigureAwait(false);
        if (request is null)
        {
            // Not an HTTP trigger. There are none, and if one is ever added it
            // will have no caller, so any handler reaching for one throws.
            await next(context).ConfigureAwait(false);
            return;
        }

        var action = context.FunctionDefinition.Name;

        // A CORS preflight carries no token and is normally answered by the
        // platform from the function app's own CORS configuration, so it never
        // gets this far. If it does, answer it rather than letting it fall
        // through to the token check, where it would come back as a 401 and
        // send whoever is debugging it looking at the wrong thing.
        if (string.Equals(request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            var preflight = request.CreateResponse();
            preflight.StatusCode = HttpStatusCode.NoContent;
            context.GetInvocationResult().Value = preflight;
            return;
        }

        request.Headers.TryGetValues("Authorization", out var authorization);

        var decision = await _gate.EvaluateAsync(
            request.Method,
            authorization?.FirstOrDefault(),
            context.CancellationToken).ConfigureAwait(false);

        if (decision is GateDecision.Denied denied)
        {
            _audit.RecordDenied(action, request.Method, denied.Error, denied.ObjectId);

            var refusal = await request.ProblemAsync(
                denied.Status,
                denied.Error,
                denied.Message,
                denied.Details).ConfigureAwait(false);

            context.GetInvocationResult().Value = refusal;
            return;
        }

        var caller = ((GateDecision.Allowed)decision).Caller;
        context.Items[FunctionContextExtensions.CallerKey] = caller;

        _audit.RecordRequest(caller, action, request.Url.AbsolutePath, request.Url.Query);

        await next(context).ConfigureAwait(false);
    }
}
