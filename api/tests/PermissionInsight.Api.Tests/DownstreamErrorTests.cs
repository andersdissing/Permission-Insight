using PermissionInsight.Api.Downstream;
using Xunit;

namespace PermissionInsight.Api.Tests;

/// <summary>
/// What a downstream failure is allowed to remember.
///
/// The code answers the question a log is consulted for — is the identity
/// missing a grant, or is the item gone. The message beside it names the
/// resource, and SharePoint writes list and document names into it, so it is
/// deliberately dropped rather than truncated.
/// </summary>
public sealed class DownstreamErrorTests
{
    [Fact]
    public void The_graph_error_code_is_kept()
    {
        var code = DownstreamException.ExtractCode(
            """{"error":{"code":"accessDenied","message":"Access denied to /sites/hr/Personalesager."}}""");

        Assert.Equal("accessDenied", code);
    }

    [Fact]
    public void The_sharepoint_error_code_is_kept()
    {
        var code = DownstreamException.ExtractCode(
            """{"odata.error":{"code":"-2147024891, System.UnauthorizedAccessException","message":{"lang":"en-US","value":"Access denied."}}}""");

        Assert.Equal("-2147024891, System.UnauthorizedAccessException", code);
    }

    [Fact]
    public void The_message_naming_the_resource_is_not_kept()
    {
        // The reason this class exists. SharePoint's 404 spells out the list
        // and the site, and that is precisely what must not reach telemetry.
        const string body =
            """{"error":{"code":"itemNotFound","message":"List 'Personalesager' does not exist at site with URL 'https://contoso.sharepoint.com/sites/hr'."}}""";

        var code = DownstreamException.ExtractCode(body);

        Assert.Equal("itemNotFound", code);
        Assert.DoesNotContain("Personalesager", code!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<html><body>502 Bad Gateway</body></html>")]
    [InlineData("""{"error":"a string, not an object"}""")]
    [InlineData("""{"error":{"message":"no code here"}}""")]
    [InlineData("[1,2,3]")]
    public void An_unrecognised_body_yields_nothing_rather_than_a_fallback(string? body)
    {
        // Falling back to the raw body would reintroduce the leak in exactly
        // the case where nobody knows what the body contains.
        Assert.Null(DownstreamException.ExtractCode(body));
    }

    [Fact]
    public void A_code_long_enough_to_be_prose_is_refused()
    {
        var body = "{\"error\":{\"code\":\"" + new string('x', 400) + "\"}}";

        Assert.Null(DownstreamException.ExtractCode(body));
    }
}
