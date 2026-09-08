using System.Net.Http.Headers;
using PermissionInsight.Api.Security;

namespace PermissionInsight.Api.Downstream;

/// <summary>
/// The only place an outbound request to SharePoint or Microsoft Graph is
/// built, and the only place the application identity's token is attached.
///
/// Every request is checked against <see cref="HttpMethodPolicy"/> before it is
/// sent. The check is here rather than in the callers because a convention that
/// lives in review comments is not enforcement: a future feature that needed to
/// write would have to widen the allow-list, which is a visible change to a
/// file whose whole purpose is to be read.
/// </summary>
public sealed class ReadOnlyHttpClient
{
    private readonly HttpClient _http;
    private readonly IDownstreamTokenProvider _tokens;

    public ReadOnlyHttpClient(HttpClient http, IDownstreamTokenProvider tokens)
    {
        _http = http;
        _tokens = tokens;
    }

    /// <summary>
    /// Sends a read request as the application identity.
    /// </summary>
    /// <exception cref="ReadOnlyViolationException">
    /// If the method is not on the allow-list. This is a programming error
    /// rather than a runtime condition, so it throws instead of returning a
    /// failure the caller might ignore.
    /// </exception>
    public async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        string scope,
        CancellationToken cancellationToken)
    {
        if (!HttpMethodPolicy.IsAllowedDownstream(request.Method.Method))
        {
            throw new ReadOnlyViolationException(request.Method.Method, request.RequestUri);
        }

        var token = await _tokens.GetTokenAsync(scope, cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Throttling is passed back to the browser rather than retried here.
        // The SPA orchestrates scans and has to show the backoff in its own
        // progress text; a silent server-side retry would freeze a bar that
        // looks stalled, which is the failure mode phase 3 calls out.
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<HttpResponseMessage> GetAsync(Uri uri, string scope, CancellationToken cancellationToken) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), scope, cancellationToken);
}

/// <summary>
/// Thrown when something tries to send a method that is not on the read-only
/// allow-list. Reaching this means the application is about to do the one thing
/// it promises never to do.
/// </summary>
public sealed class ReadOnlyViolationException : InvalidOperationException
{
    public ReadOnlyViolationException(string method, Uri? uri)
        : base($"Permission Insight never writes to SharePoint. Refused to send {method} to {uri?.GetLeftPart(UriPartial.Path) ?? "an unknown address"}. Allowed downstream methods: {string.Join(", ", HttpMethodPolicy.DownstreamMethods)}.")
    {
        Method = method;
        RequestUri = uri;
    }

    public string Method { get; }

    public Uri? RequestUri { get; }
}
