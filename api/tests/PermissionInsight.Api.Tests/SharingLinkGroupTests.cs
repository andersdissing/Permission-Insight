using PermissionInsight.Api.Downstream;
using Xunit;

namespace PermissionInsight.Api.Tests;

public sealed class SharingLinkGroupTests
{
    private const string ItemGuid = "8f1c6b2e-1f4a-4c0e-9a3d-2b7e5c9d1a44";
    private const string LinkGuid = "3a9d7c11-55b2-4f6a-8e21-9c0b4d7f2e58";

    [Fact]
    public void A_sharing_link_group_yields_the_item_the_kind_and_the_link()
    {
        var group = SharingLinkGroup.TryParse(42, $"SharingLinks.{ItemGuid}.Flexible.{LinkGuid}");

        Assert.NotNull(group);
        Assert.Equal(42, group.GroupId);
        Assert.Equal(ItemGuid, group.ItemGuid);
        Assert.Equal("Flexible", group.Kind);
        Assert.Equal(LinkGuid, group.LinkGuid);
    }

    [Theory]
    [InlineData("Flexible")]
    [InlineData("OrganizationView")]
    [InlineData("AnonymousEdit")]
    public void Every_kind_parses(string kind)
    {
        Assert.NotNull(SharingLinkGroup.TryParse(1, $"SharingLinks.{ItemGuid}.{kind}.{LinkGuid}"));
    }

    [Theory]
    [InlineData("Team Site Members")]
    [InlineData("Excel Services Viewers")]
    [InlineData("SharingLinksAreNotThis")]
    [InlineData("")]
    [InlineData(null)]
    public void An_ordinary_site_group_is_not_a_sharing_link(string? title)
    {
        Assert.Null(SharingLinkGroup.TryParse(1, title));
    }

    [Theory]
    [InlineData("SharingLinks.")]
    [InlineData("SharingLinks.not-a-guid.Flexible.abc")]
    [InlineData("SharingLinks.8f1c6b2e-1f4a-4c0e-9a3d-2b7e5c9d1a44.Flexible")]
    public void A_malformed_title_is_dropped_rather_than_guessed_at(string title)
    {
        // A row holding a GUID that resolves to nothing is worse than one
        // fewer row: it would sit unresolved forever and count towards the
        // total the user is trying to work through.
        Assert.Null(SharingLinkGroup.TryParse(1, title));
    }

    [Fact]
    public void Extra_segments_do_not_break_the_parse()
    {
        var group = SharingLinkGroup.TryParse(7, $"SharingLinks.{ItemGuid}.Flexible.{LinkGuid}.suffix");

        Assert.NotNull(group);
        Assert.Equal(LinkGuid, group.LinkGuid);
    }

    [Theory]
    [InlineData("i:0#.f|membership|guest_contoso.com#ext#@fabrikam.onmicrosoft.com", null, true)]
    [InlineData("i:0#.f|membership|jens@contoso.com", "jens@contoso.com", false)]
    [InlineData(null, "guest#ext#@contoso.com", true)]
    [InlineData(null, "jens@contoso.com", false)]
    [InlineData(null, null, false)]
    public void A_guest_is_recognised_from_the_login_name(string? loginName, string? email, bool expected)
    {
        Assert.Equal(expected, SharePointClient.IsExternal(loginName, email));
    }
}
