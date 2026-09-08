using System.Net;
using System.Text;
using PermissionInsight.Api.Downstream;
using Xunit;

namespace PermissionInsight.Api.Tests;

public sealed class GraphSiteSearchTests
{
    [Fact]
    public async Task A_site_is_projected_to_a_title_and_a_server_relative_url()
    {
        var graph = GraphReturning("""
            {
              "value": [
                {
                  "id": "contoso.sharepoint.com,1111,2222",
                  "name": "finance",
                  "displayName": "Finance",
                  "webUrl": "https://contoso.sharepoint.com/sites/finance"
                }
              ]
            }
            """);

        var result = await graph.SearchSitesAsync("finance", 20, CancellationToken.None);

        var site = Assert.Single(result.Results);
        Assert.Equal("contoso.sharepoint.com,1111,2222", site.Id);
        Assert.Equal("Finance", site.Title);
        // The picker shows this line in monospace, so it is the path and not
        // the whole address.
        Assert.Equal("/sites/finance", site.Url);
        Assert.Equal("https://contoso.sharepoint.com/sites/finance", site.WebUrl);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task A_site_with_no_display_name_falls_back_to_its_name()
    {
        var graph = GraphReturning("""
            {
              "value": [
                { "id": "a", "name": "archive", "webUrl": "https://contoso.sharepoint.com/sites/archive" }
              ]
            }
            """);

        var result = await graph.SearchSitesAsync("archive", 20, CancellationToken.None);

        Assert.Equal("archive", Assert.Single(result.Results).Title);
    }

    [Fact]
    public async Task A_site_with_no_id_is_dropped_rather_than_shown_as_an_unclickable_row()
    {
        var graph = GraphReturning("""
            {
              "value": [
                { "webUrl": "https://contoso.sharepoint.com/sites/broken" },
                { "id": "b", "displayName": "Fine", "webUrl": "https://contoso.sharepoint.com/sites/fine" }
              ]
            }
            """);

        var result = await graph.SearchSitesAsync("s", 20, CancellationToken.None);

        Assert.Equal("Fine", Assert.Single(result.Results).Title);
    }

    [Fact]
    public async Task More_matches_than_the_ceiling_are_reported_as_truncated()
    {
        var sites = Enumerable.Range(0, 25).Select(index => $$"""
            { "id": "id-{{index}}", "displayName": "Site {{index}}", "webUrl": "https://contoso.sharepoint.com/sites/s{{index}}" }
            """);

        var graph = GraphReturning($$"""{ "value": [ {{string.Join(",", sites)}} ] }""");

        var result = await graph.SearchSitesAsync("site", 20, CancellationToken.None);

        Assert.Equal(20, result.Results.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task A_further_page_counts_as_truncated_even_when_this_page_fits()
    {
        var graph = GraphReturning("""
            {
              "@odata.nextLink": "https://graph.microsoft.com/v1.0/sites?search=s&$skiptoken=x",
              "value": [
                { "id": "a", "displayName": "One", "webUrl": "https://contoso.sharepoint.com/sites/one" }
              ]
            }
            """);

        var result = await graph.SearchSitesAsync("s", 20, CancellationToken.None);

        Assert.Single(result.Results);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task No_matches_is_an_empty_result_rather_than_a_failure()
    {
        var graph = GraphReturning("""{ "value": [] }""");

        var result = await graph.SearchSitesAsync("nothing", 20, CancellationToken.None);

        Assert.Empty(result.Results);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task A_refusal_from_graph_carries_its_status_through()
    {
        var graph = GraphFailing(HttpStatusCode.Forbidden, """{ "error": { "code": "accessDenied" } }""");

        var failure = await Assert.ThrowsAsync<DownstreamException>(
            () => graph.SearchSitesAsync("finance", 20, CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, failure.Status);
        Assert.Equal("accessDenied", failure.Code);
        Assert.False(failure.IsThrottled);
    }

    [Fact]
    public async Task Throttling_keeps_its_retry_after_so_the_browser_can_show_the_backoff()
    {
        var graph = GraphFailing(HttpStatusCode.TooManyRequests, "{}", retryAfterSeconds: 42);

        var failure = await Assert.ThrowsAsync<DownstreamException>(
            () => graph.SearchSitesAsync("finance", 20, CancellationToken.None));

        Assert.True(failure.IsThrottled);
        Assert.Equal("42", failure.RetryAfter);
    }

    [Fact]
    public async Task The_search_term_is_escaped_into_the_query()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{ "value": [] }"""));
        var graph = new GraphClient(new ReadOnlyHttpClient(new HttpClient(handler), new StubTokenProvider()));

        await graph.SearchSitesAsync("sales & marketing", 20, CancellationToken.None);

        // AbsoluteUri rather than ToString: ToString is the display form and
        // unescapes spaces, so it would hide whether the term was escaped at
        // all. A site called "Sales & marketing" is otherwise a truncated query.
        Assert.Equal(
            "https://graph.microsoft.com/v1.0/sites?search=sales%20%26%20marketing",
            handler.LastRequest!.RequestUri!.AbsoluteUri);
    }

    private static GraphClient GraphReturning(string json) =>
        new(new ReadOnlyHttpClient(
            new HttpClient(new StubHandler(_ => Json(HttpStatusCode.OK, json))),
            new StubTokenProvider()));

    private static GraphClient GraphFailing(HttpStatusCode status, string json, int? retryAfterSeconds = null) =>
        new(new ReadOnlyHttpClient(
            new HttpClient(new StubHandler(_ =>
            {
                var response = Json(status, json);
                if (retryAfterSeconds is { } seconds)
                {
                    response.Headers.Add("Retry-After", seconds.ToString());
                }

                return response;
            })),
            new StubTokenProvider()));

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private sealed class StubTokenProvider : IDownstreamTokenProvider
    {
        public ValueTask<string> GetTokenAsync(string scope, CancellationToken cancellationToken) =>
            ValueTask.FromResult("stub-token");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(respond(request));
        }
    }
}
