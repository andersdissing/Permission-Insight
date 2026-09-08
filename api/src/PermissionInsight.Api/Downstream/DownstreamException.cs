using System.Net;
using System.Text.Json;

namespace PermissionInsight.Api.Downstream;

/// <summary>
/// A downstream service refused or failed. Carries enough to answer the browser
/// honestly, including <c>Retry-After</c>, which is passed through rather than
/// absorbed: the SPA runs the scan loop and needs to show the backoff itself.
/// </summary>
public sealed class DownstreamException : Exception
{
    private DownstreamException(string service, HttpStatusCode status, string? retryAfter, string? code)
        : base($"{service} returned {(int)status} {status}.")
    {
        Service = service;
        Status = status;
        RetryAfter = retryAfter;
        Code = code;
    }

    public string Service { get; }

    public HttpStatusCode Status { get; }

    public string? RetryAfter { get; }

    /// <summary>
    /// The downstream error <em>code</em> — <c>accessDenied</c>,
    /// <c>itemNotFound</c>, <c>System.UnauthorizedAccessException</c> — and
    /// deliberately not the message beside it.
    ///
    /// The message is what distinguishes one permission failure from another
    /// for a human, but SharePoint writes the resource into it: "List
    /// 'Personalesager' does not exist at site with URL '…'". Logging that puts
    /// list and document names into Application Insights, where they are
    /// readable by anyone holding Reader on the workspace — a wider and
    /// differently governed population than the app role this API is gated on.
    /// The code alone answers the question a log is consulted for, which is
    /// whether the identity is missing a grant or the item is simply gone.
    /// </summary>
    public string? Code { get; }

    public bool IsThrottled => Status == HttpStatusCode.TooManyRequests;

    public static async Task<DownstreamException> FromAsync(
        HttpResponseMessage response,
        string service,
        CancellationToken cancellationToken)
    {
        string? code = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            code = ExtractCode(body);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A missing error body must not replace the status code with an
            // exception about reading it.
        }

        var retryAfter = response.Headers.TryGetValues("Retry-After", out var values)
            ? values.FirstOrDefault()
            : null;

        return new DownstreamException(service, response.StatusCode, retryAfter, code);
    }

    /// <summary>
    /// Reads the code out of either error shape: Graph's <c>error.code</c> or
    /// SharePoint REST's <c>odata.error.code</c>. Returns null rather than
    /// falling back to the raw body, because a body that does not parse is
    /// exactly the case where its contents are unknown.
    /// </summary>
    internal static string? ExtractCode(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!document.RootElement.TryGetProperty("error", out var error) &&
                !document.RootElement.TryGetProperty("odata.error", out error))
            {
                return null;
            }

            if (error.ValueKind != JsonValueKind.Object ||
                !error.TryGetProperty("code", out var value) ||
                value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var code = value.GetString();

            // Codes are short identifiers. A long one is not a code, whatever
            // the property is called, so it does not get logged.
            return code is { Length: > 0 and <= 200 } ? code : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
