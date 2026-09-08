using System.Net;
using System.Text;
using PermissionInsight.Api.Configuration;
using PermissionInsight.Api.Downstream;
using Xunit;

namespace PermissionInsight.Api.Tests;

public sealed class SharePointClientTests
{
    private const string Root = "https://contoso.sharepoint.com";
    private const string SiteUrl = "https://contoso.sharepoint.com/sites/finance";
    private const string ItemGuid = "8f1c6b2e-1f4a-4c0e-9a3d-2b7e5c9d1a44";
    private const string LinkGuid = "3a9d7c11-55b2-4f6a-8e21-9c0b4d7f2e58";

    [Fact]
    public async Task Site_groups_are_filtered_to_sharing_links()
    {
        var handler = new RouteHandler();
        handler.Respond("sitegroups", Json($$"""
            {
              "value": [
                { "Id": 3, "Title": "Finance Owners" },
                { "Id": 8, "Title": "SharingLinks.{{ItemGuid}}.Flexible.{{LinkGuid}}" },
                { "Id": 9, "Title": "Excel Services Viewers" }
              ]
            }
            """));

        var links = await ClientOver(handler).GetSharingLinkGroupsAsync(SiteUrl, CancellationToken.None);

        var link = Assert.Single(links);
        Assert.Equal(8, link.GroupId);
        Assert.Equal(ItemGuid, link.ItemGuid);
    }

    [Fact]
    public async Task Every_page_is_followed_before_the_total_is_reported()
    {
        // The total is the first thing the tab shows. A half-read page would
        // be a wrong number rather than a slow one.
        var handler = new RouteHandler();

        // Registered most specific first: the continuation URL also contains
        // "sitegroups".
        handler.Respond("skiptoken=page2", Json($$"""
            {
              "value": [ { "Id": 2, "Title": "SharingLinks.{{ItemGuid}}.OrganizationView.{{LinkGuid}}" } ]
            }
            """));

        handler.Respond("sitegroups?", Json($$"""
            {
              "odata.nextLink": "https://contoso.sharepoint.com/sites/finance/_api/web/sitegroups?$skiptoken=page2",
              "value": [ { "Id": 1, "Title": "SharingLinks.{{ItemGuid}}.Flexible.{{LinkGuid}}" } ]
            }
            """));

        var links = await ClientOver(handler).GetSharingLinkGroupsAsync(SiteUrl, CancellationToken.None);

        Assert.Equal(2, links.Count);
    }

    [Fact]
    public async Task An_item_resolves_as_a_file()
    {
        var handler = new RouteHandler();
        handler.Respond("GetFileById", Json("""{ "ServerRelativeUrl": "/sites/finance/Shared Documents/a.docx" }"""));

        var item = await ClientOver(handler).ResolveItemAsync(SiteUrl, ItemGuid, CancellationToken.None);

        Assert.Equal("/sites/finance/Shared Documents/a.docx", item?.Path);
        Assert.Equal("file", item?.ItemType);
    }

    [Fact]
    public async Task A_guid_that_is_not_a_file_is_tried_as_a_folder()
    {
        var handler = new RouteHandler();
        handler.Respond("GetFileById", new HttpResponseMessage(HttpStatusCode.NotFound));
        handler.Respond("GetFolderById", Json("""{ "ServerRelativeUrl": "/sites/finance/Shared Documents/Budget" }"""));

        var item = await ClientOver(handler).ResolveItemAsync(SiteUrl, ItemGuid, CancellationToken.None);

        Assert.Equal("folder", item?.ItemType);
    }

    [Fact]
    public async Task An_item_that_is_neither_is_the_orphaned_case_and_not_an_error()
    {
        var handler = new RouteHandler();
        handler.Respond("GetFileById", new HttpResponseMessage(HttpStatusCode.NotFound));
        handler.Respond("GetFolderById", new HttpResponseMessage(HttpStatusCode.NotFound));

        var item = await ClientOver(handler).ResolveItemAsync(SiteUrl, ItemGuid, CancellationToken.None);

        Assert.Null(item);
    }

    [Fact]
    public async Task Group_members_keep_the_external_flag()
    {
        var handler = new RouteHandler();
        handler.Respond("users", Json("""
            {
              "value": [
                { "Title": "Jens Hansen", "Email": "jens@contoso.com", "LoginName": "i:0#.f|membership|jens@contoso.com" },
                { "Title": "Outside Person", "Email": "out@partner.com", "LoginName": "i:0#.f|membership|out_partner.com#ext#@contoso.onmicrosoft.com" }
              ]
            }
            """));

        var members = await ClientOver(handler).GetGroupMembersAsync(SiteUrl, 8, CancellationToken.None);

        Assert.Equal(2, members.Count);
        Assert.False(members[0].External);
        Assert.True(members[1].External);
    }

    [Theory]
    [InlineData("https://evil.example.com/sites/finance")]
    [InlineData("https://contoso.sharepoint.com.evil.example.com/sites/x")]
    [InlineData("http://contoso.sharepoint.com/sites/finance")]
    public async Task An_address_outside_the_tenant_is_refused(string siteUrl)
    {
        // This should be unreachable, because site URLs come from Graph rather
        // than from the browser. It is checked anyway: the alternative is a
        // component that will send an application token holding tenant-wide
        // read to whatever host it is handed.
        var client = ClientOver(new RouteHandler());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetSharingLinkGroupsAsync(siteUrl, CancellationToken.None));
    }

    [Fact]
    public void The_token_audience_comes_from_configuration_not_from_the_caller()
    {
        Assert.Equal($"{Root}/.default", ClientOver(new RouteHandler()).Scope);
    }

    private static SharePointClient ClientOver(RouteHandler handler) =>
        new(
            new ReadOnlyHttpClient(new HttpClient(handler), new StubTokenProvider()),
            new ApiOptions
            {
                TenantId = "00000000-0000-0000-0000-000000000000",
                ApiClientId = "00000000-0000-0000-0000-000000000000",
                ApiIdentifierUri = "api://contoso.onmicrosoft.com/permission-insight",
                SharePointRootUrl = Root,
                Authority = "https://login.microsoftonline.com",
                ManagedIdentityClientId = "00000000-0000-0000-0000-000000000000",
            });

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubTokenProvider : IDownstreamTokenProvider
    {
        public ValueTask<string> GetTokenAsync(string scope, CancellationToken cancellationToken) =>
            ValueTask.FromResult("stub-token");
    }

    private sealed class RouteHandler : HttpMessageHandler
    {
        private readonly List<(string Fragment, Func<HttpResponseMessage> Respond)> _routes = [];

        public void Respond(string fragment, HttpResponseMessage response) =>
            _routes.Add((fragment, () => response));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = Uri.UnescapeDataString(request.RequestUri!.AbsoluteUri);

            foreach (var (fragment, respond) in _routes)
            {
                if (url.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(respond());
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
