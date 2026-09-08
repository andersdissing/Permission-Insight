using PermissionInsight.Api.Downstream;
using Xunit;

namespace PermissionInsight.Api.Tests;

/// <summary>
/// Paging a library. These matter more than they look: when this goes wrong it
/// goes wrong silently, and the scan reports a confident number that is far
/// too small.
/// </summary>
public sealed class SkipTokenTests
{
    [Fact]
    public void The_paging_token_is_taken_from_the_next_link_and_not_the_link_itself()
    {
        // The browser hands the token back and the backend rebuilds the URL, so
        // a caller can never steer an application token that can read every
        // site in the tenant at an address of its own choosing.
        var token = SharePointClient.ExtractSkipToken(
            "https://contoso.sharepoint.com/sites/finance/_api/web/lists(guid'abc')/items?$skiptoken=Paged%3dTRUE%26p_ID%3d1000&$top=1000");

        Assert.Equal("Paged=TRUE&p_ID=1000", token);
    }

    [Fact]
    public void A_token_at_the_end_of_the_link_is_read_whole()
    {
        var token = SharePointClient.ExtractSkipToken(
            "https://contoso.sharepoint.com/_api/web/lists(guid'abc')/items?$top=1000&$skiptoken=Paged%3dTRUE%26p_ID%3d2000");

        Assert.Equal("Paged=TRUE&p_ID=2000", token);
    }

    [Fact]
    public void A_percent_encoded_dollar_still_yields_the_token()
    {
        // SharePoint returns the next link with %24skiptoken rather than
        // $skiptoken. Matching on the literal found nothing, so paging stopped
        // after one page and a 12,000 item library reported exactly 1,000
        // items — including none of the sharing links beyond the first page.
        var token = SharePointClient.ExtractSkipToken(
            "https://contoso.sharepoint.com/_api/web/lists(guid'abc')/items?%24top=1000&%24skiptoken=Paged%3dTRUE%26p_ID%3d1000");

        Assert.Equal("Paged=TRUE&p_ID=1000", token);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://contoso.sharepoint.com/_api/web/lists(guid'abc')/items?$top=1000")]
    public void The_last_page_yields_no_token(string? nextLink)
    {
        Assert.Null(SharePointClient.ExtractSkipToken(nextLink));
    }

    [Fact]
    public void A_relative_next_link_does_not_throw()
    {
        Assert.Null(SharePointClient.ExtractSkipToken("/_api/web/lists/items?$skiptoken=x"));
    }
}
