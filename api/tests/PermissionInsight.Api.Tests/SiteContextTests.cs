using PermissionInsight.Api.Downstream;
using PermissionInsight.Api.Functions;
using Xunit;

namespace PermissionInsight.Api.Tests;

public sealed class SiteContextTests
{
    private static SiteContext Site(params DriveLocation[] drives) =>
        new("site-id", "Finance", "https://contoso.sharepoint.com/sites/finance", drives);

    [Fact]
    public void An_item_is_located_in_its_library()
    {
        var site = Site(new DriveLocation("drive-1", "/sites/finance/Shared Documents"));

        var located = site.Locate("/sites/finance/Shared Documents/Budget/2026.xlsx");

        Assert.NotNull(located);
        Assert.Equal("drive-1", located.Value.DriveId);
        Assert.Equal("Budget/2026.xlsx", located.Value.PathInDrive);
    }

    [Fact]
    public void The_library_root_itself_locates_with_an_empty_path()
    {
        var site = Site(new DriveLocation("drive-1", "/sites/finance/Shared Documents"));

        var located = site.Locate("/sites/finance/Shared Documents");

        Assert.NotNull(located);
        Assert.Equal(string.Empty, located.Value.PathInDrive);
    }

    [Fact]
    public void The_longest_matching_library_wins()
    {
        // "Documents" must not claim an item belonging to "Documents archive".
        var site = Site(
            new DriveLocation("drive-1", "/sites/finance/Documents"),
            new DriveLocation("drive-2", "/sites/finance/Documents archive"));

        var located = site.Locate("/sites/finance/Documents archive/old.docx");

        Assert.NotNull(located);
        Assert.Equal("drive-2", located.Value.DriveId);
        Assert.Equal("old.docx", located.Value.PathInDrive);
    }

    [Fact]
    public void A_similar_prefix_that_is_not_a_folder_boundary_does_not_match()
    {
        var site = Site(new DriveLocation("drive-1", "/sites/finance/Documents"));

        Assert.Null(site.Locate("/sites/finance/DocumentsOld/file.docx"));
    }

    [Fact]
    public void A_path_outside_every_library_does_not_locate()
    {
        var site = Site(new DriveLocation("drive-1", "/sites/finance/Shared Documents"));

        Assert.Null(site.Locate("/sites/finance/Lists/Tasks/3_.000"));
    }

    [Fact]
    public void Path_segments_are_escaped_individually_so_separators_survive()
    {
        Assert.Equal("Budget%202026/Q1%20%26%20Q2/plan.xlsx", GraphClient.EscapePath("Budget 2026/Q1 & Q2/plan.xlsx"));
    }
}

public sealed class LinkMatchingTests
{
    private const string LinkGuid = "3a9d7c11-55b2-4f6a-8e21-9c0b4d7f2e58";

    private static LinkPermission Permission(string id, string scope = "organization") =>
        new(id, scope, "read", [], null, false);

    [Fact]
    public void A_permission_whose_id_is_the_link_guid_matches()
    {
        var matched = SharingLinksFunction.MatchLink(
            [Permission("other"), Permission(LinkGuid)],
            LinkGuid);

        Assert.Equal(LinkGuid, matched?.LinkId);
    }

    [Fact]
    public void A_permission_id_that_embeds_the_link_guid_matches()
    {
        var matched = SharingLinksFunction.MatchLink(
            [Permission($"u!aHR0cHM6{LinkGuid}")],
            LinkGuid);

        Assert.NotNull(matched);
    }

    [Fact]
    public void A_single_link_on_the_item_needs_no_identification()
    {
        var matched = SharingLinksFunction.MatchLink([Permission("opaque-id")], LinkGuid);

        Assert.Equal("opaque-id", matched?.LinkId);
    }

    [Fact]
    public void Several_unidentifiable_links_resolve_to_none_rather_than_the_wrong_one()
    {
        // Showing the wrong scope against a path is worse than showing no
        // scope. The tab exists to answer who can reach a file.
        var matched = SharingLinksFunction.MatchLink(
            [Permission("opaque-a", "anonymous"), Permission("opaque-b", "users")],
            LinkGuid);

        Assert.Null(matched);
    }

    [Fact]
    public void An_item_with_no_links_resolves_to_none()
    {
        Assert.Null(SharingLinksFunction.MatchLink([], LinkGuid));
    }
}
