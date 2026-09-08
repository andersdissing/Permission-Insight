using System.Net;
using Microsoft.Azure.Functions.Worker.Http;

namespace PermissionInsight.Api.Http;

internal static class Responses
{
    /// <summary>
    /// Applied to every response this API produces.
    /// </summary>
    /// <remarks>
    /// <c>no-store</c> is the one that earns its place. Every body here is
    /// permission data for a named site — who can reach which document — and
    /// without a directive nothing stops a proxy or a browser's back-forward
    /// cache holding it after the user has moved on or signed out.
    ///
    /// The rest are cheap and unconditional. This API returns only JSON, so
    /// there is nothing for a sniffing browser to reinterpret and nothing that
    /// belongs in a frame.
    /// </remarks>
    private static void Harden(HttpResponseData response)
    {
        response.Headers.Add("Cache-Control", "no-store, no-cache, must-revalidate");
        response.Headers.Add("Pragma", "no-cache");
        response.Headers.Add("X-Content-Type-Options", "nosniff");
        response.Headers.Add("X-Frame-Options", "DENY");
        response.Headers.Add("Referrer-Policy", "no-referrer");
    }

    public static async Task<HttpResponseData> JsonAsync<T>(
        this HttpRequestData request,
        HttpStatusCode status,
        T body)
    {
        var response = request.CreateResponse();
        await response.WriteAsJsonAsync(body).ConfigureAwait(false);

        // WriteAsJsonAsync sets 200 itself, so the real status goes on after it.
        // Setting it first is silently overwritten, which turns a 403 into a
        // 200 carrying an error body.
        response.StatusCode = status;
        Harden(response);
        return response;
    }

    public static Task<HttpResponseData> OkAsync<T>(this HttpRequestData request, T body) =>
        request.JsonAsync(HttpStatusCode.OK, body);

    /// <summary>
    /// A refusal the caller can act on without reading server logs. The error
    /// code is stable and machine readable; the message is written for the
    /// person who has to fix it.
    /// </summary>
    public static async Task<HttpResponseData> ProblemAsync(
        this HttpRequestData request,
        HttpStatusCode status,
        string error,
        string message,
        IReadOnlyDictionary<string, object?>? details = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["error"] = error,
            ["message"] = message,
        };

        if (details is not null)
        {
            foreach (var (key, value) in details)
            {
                body[key] = value;
            }
        }

        var response = request.CreateResponse();
        await response.WriteAsJsonAsync(body).ConfigureAwait(false);
        response.StatusCode = status;
        Harden(response);
        return response;
    }
}
