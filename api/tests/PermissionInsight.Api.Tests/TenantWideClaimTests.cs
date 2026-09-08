using PermissionInsight.Api.Downstream;
using PermissionInsight.Api.Functions;
using Xunit;

namespace PermissionInsight.Api.Tests;

/// <summary>
/// Recognising the grants that reach everybody.
///
/// The failure mode these guard against is not a crash. It is the tool
/// scanning a Danish or German tenant, finding a grant to the whole
/// organisation, and reporting nothing — because the principal was called
/// something the code did not recognise. That looks exactly like a clean site.
/// </summary>
public sealed class TenantWideClaimTests
{
    [Theory]
    // The tenant id is appended, so the claim is never a fixed string. These
    // are placeholders: a real tenant id must not be committed, and the whole
    // point of the match is that the value is irrelevant.
    [InlineData("c:0-.f|rolemanager|spo-grid-all-users/11111111-2222-3333-4444-555555555555")]
    [InlineData("c:0-.f|rolemanager|spo-grid-all-users/00000000-0000-0000-0000-000000000000")]
    // Observed without the tenant segment on some tenants.
    [InlineData("c:0-.f|rolemanager|spo-grid-all-users")]
    // Casing is not guaranteed by anything.
    [InlineData("C:0-.F|RoleManager|SPO-GRID-ALL-USERS/11111111-2222-3333-4444-555555555555")]
    public void Everyone_except_external_users_is_recognised_whatever_tenant_it_came_from(string claim)
    {
        var principal = TenantWideClaim.Classify(claim);

        Assert.NotNull(principal);
        Assert.Equal(TenantWideClaim.EveryoneExceptExternal, principal!.Code);
        Assert.False(principal.IncludesExternal);
    }

    [Fact]
    public void Everyone_includes_external_users_and_says_so()
    {
        // The difference that changes how urgent the finding is: this one
        // reaches guests, the other does not.
        var principal = TenantWideClaim.Classify("c:0(.s|true");

        Assert.NotNull(principal);
        Assert.Equal(TenantWideClaim.Everyone, principal!.Code);
        Assert.True(principal.IncludesExternal);
    }

    [Fact]
    public void All_windows_users_is_recognised()
    {
        var principal = TenantWideClaim.Classify("c:0!.s|windows");

        Assert.NotNull(principal);
        Assert.Equal(TenantWideClaim.AllWindowsUsers, principal!.Code);
    }

    [Theory]
    // Danish, German, French, Spanish. The tenant renders the display name in
    // its own language and the tool must not depend on any of them.
    [InlineData("Alle undtagen eksterne brugere")]
    [InlineData("Jeder außer externen Benutzern")]
    [InlineData("Tout le monde sauf les utilisateurs externes")]
    [InlineData("Todos excepto los usuarios externos")]
    [InlineData("Everyone except external users")]
    public void A_display_name_alone_never_classifies_anything(string displayName)
    {
        // Stated as a test because it is the whole design. If a future change
        // starts matching display names, English tenants would keep working and
        // every other tenant would silently under-report — the failure nobody
        // notices. Classification takes claims only.
        Assert.Null(TenantWideClaim.Classify(displayName));
    }

    [Theory]
    [InlineData("i:0#.f|membership|alpha@contoso.com")]
    [InlineData("c:0o.c|federateddirectoryclaimprovider|11111111-1111-1111-1111-111111111111")]
    [InlineData("alpha@contoso.com")]
    [InlineData("")]
    [InlineData(null)]
    public void An_ordinary_principal_is_not_flagged(string? login)
    {
        Assert.Null(TenantWideClaim.Classify(login));
    }

    [Fact]
    public void The_claim_is_found_whichever_identifier_carries_it()
    {
        // Which field holds the claim depends on the identity facet Graph
        // chose, so every identifier is offered rather than one being guessed.
        var principal = TenantWideClaim.Classify(null, "c:0-.f|rolemanager|spo-grid-all-users/abc");

        Assert.NotNull(principal);
        Assert.Equal(TenantWideClaim.EveryoneExceptExternal, principal!.Code);
    }

    [Fact]
    public void The_label_is_the_tools_own_and_not_the_tenants()
    {
        // So a report reads the same whatever language the tenant runs in.
        var principal = TenantWideClaim.Classify("c:0-.f|rolemanager|spo-grid-all-users/abc");

        Assert.Equal("Everyone except external users", principal!.Label);
    }
}

/// <summary>
/// Deriving the site's own permissions from a list that inherits.
/// </summary>
public sealed class SiteAccessDerivationTests
{
    [Fact]
    public void The_default_document_library_is_tried_first_because_a_reader_can_check_it_by_hand()
    {
        var lists = new[]
        {
            List("1", "Site Assets", inherits: true),
            List("2", "Documents", inherits: true, isDefault: true),
        };

        Assert.Equal("2", SiteAccessFunction.Candidates(lists).First().Id);
    }

    [Fact]
    public void A_list_SharePoint_said_nothing_about_is_still_tried()
    {
        // The one that matters. /_api/web/lists frequently omits
        // HasUniqueRoleAssignments, so requiring it meant that on a real tenant
        // every list read as unknown and the site's permissions were never read
        // at all. Graph's own inheritedFrom marker works without the flag.
        var lists = new[] { List("1", "Documents", inherits: null, isDefault: true) };

        Assert.Equal("1", SiteAccessFunction.Candidates(lists).First().Id);
    }

    [Fact]
    public void A_list_known_to_inherit_outranks_one_SharePoint_said_nothing_about()
    {
        var lists = new[]
        {
            List("1", "Documents", inherits: null, isDefault: true),
            List("2", "Site Pages", inherits: true),
        };

        Assert.Equal("2", SiteAccessFunction.Candidates(lists).First().Id);
    }

    [Fact]
    public void A_list_with_unique_permissions_is_tried_last_rather_than_refused()
    {
        // It can only contribute if it happens to carry an inherited entry, so
        // it is worth asking — after everything else.
        var lists = new[]
        {
            List("1", "SmallLib", inherits: false),
            List("2", "Documents", inherits: null),
        };

        Assert.Equal(new[] { "2", "1" }, SiteAccessFunction.Candidates(lists).Select(list => list.Id));
    }

    [Fact]
    public void A_hidden_list_is_a_last_resort_rather_than_a_refusal()
    {
        var lists = new[]
        {
            List("1", "Form Templates", inherits: true, hidden: true),
            List("2", "Site Pages", inherits: true),
        };

        Assert.Equal("2", SiteAccessFunction.Candidates(lists).First().Id);
    }

    [Fact]
    public void No_more_than_four_lists_are_tried_because_each_one_costs_a_call()
    {
        var lists = Enumerable.Range(1, 20)
            .Select(i => List(i.ToString(), $"List {i}", inherits: null))
            .ToArray();

        Assert.Equal(4, SiteAccessFunction.Candidates(lists).Count());
    }

    private static ListSummary List(
        string id,
        string title,
        bool? inherits,
        bool isDefault = false,
        bool hidden = false) =>
        new(
            Id: id,
            Title: title,
            ItemCount: 0,
            Hidden: hidden,
            BaseTemplate: 101,
            IsDocumentLibrary: true,
            IsDefaultDocumentLibrary: isDefault,
            HasUniqueRoleAssignments: inherits is null ? null : !inherits);
}
