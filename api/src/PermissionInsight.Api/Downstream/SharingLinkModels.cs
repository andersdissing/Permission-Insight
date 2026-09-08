namespace PermissionInsight.Api.Downstream;

/// <summary>
/// A hidden site group created by a sharing link, parsed from its title.
///
/// Every sharing link creates one of these, named
/// <c>SharingLinks.{itemGuid}.{kind}.{linkGuid}</c>. Site groups are
/// site-collection scoped, so one call to /_api/web/sitegroups returns every
/// link in the site including its subsites, which is what makes the total
/// appear in well under a second.
/// </summary>
/// <param name="Kind">
/// The kind segment of the group name. It proves a link exists but says
/// nothing useful about its scope: modern SharePoint almost always writes
/// Flexible, and the real scope lives in the permission object.
/// </param>
public sealed record SharingLinkGroup(
    int GroupId,
    string GroupTitle,
    string ItemGuid,
    string Kind,
    string LinkGuid)
{
    private const string Prefix = "SharingLinks.";

    /// <summary>
    /// Parses a site group title, or returns null if it is not a sharing link
    /// group. Malformed titles are dropped rather than guessed at: a row that
    /// cannot be resolved to an item is worse than one fewer row.
    /// </summary>
    public static SharingLinkGroup? TryParse(int groupId, string? title)
    {
        if (title is null || !title.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        // GUIDs contain hyphens, never dots, so splitting on dots is safe.
        var parts = title.Split('.');
        if (parts.Length < 4)
        {
            return null;
        }

        var itemGuid = parts[1];
        var kind = parts[2];
        var linkGuid = parts[3];

        if (!Guid.TryParse(itemGuid, out _) || kind.Length == 0 || linkGuid.Length == 0)
        {
            return null;
        }

        return new SharingLinkGroup(groupId, title, itemGuid, kind, linkGuid);
    }
}

public sealed record SharingLinkGroups(IReadOnlyList<SharingLinkGroup> Links, string SiteWebUrl);

/// <param name="External">True when the recipient is outside the tenant. Drives the danger tint.</param>
public sealed record Recipient(string Name, string? Email, bool External);

/// <summary>A direct member of a SharePoint site group.</summary>
/// <param name="IsEntraGroup">
/// The chain stops here. SharePoint will name the group but the application
/// cannot see inside it, because <c>GroupMember.Read.All</c> is deliberately
/// not requested — see docs/02-permissions.md. A row like this must say so
/// rather than render as empty: an empty expansion reads as "nobody has
/// access", which would be the tool giving a wrong answer to the exact
/// question it exists to answer.
/// </param>
public sealed record GroupMember(
    string Name,
    string? Email,
    bool External,
    int PrincipalType,
    bool IsEntraGroup)
{
    public Recipient AsRecipient() => new(Name, Email, External);
}

/// <param name="Scope">anyone, organization, users or existingAccess, from the permission object.</param>
/// <param name="Role">read or write.</param>
public sealed record LinkPermission(
    string LinkId,
    string Scope,
    string Role,
    IReadOnlyList<Recipient> Recipients,
    DateTimeOffset? Expiry,
    bool HasPassword);

public enum SharingLinkStatus
{
    /// <summary>The item exists and its permissions were read.</summary>
    Resolved,

    /// <summary>
    /// The item is gone but the group remains. Not an error and not to be
    /// discarded: an orphaned group still holding an external guest is a
    /// finding in its own right, and one Microsoft's own sharing reports do
    /// not show.
    /// </summary>
    Orphaned,

    /// <summary>
    /// The item resolved but its permission object did not. The path is worth
    /// showing; the scope is not guessed at.
    /// </summary>
    ScopeUnavailable,

    /// <summary>
    /// SharePoint refused to say whether the item still exists, so this is
    /// neither resolved nor orphaned.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Orphaned"/> on purpose. Orphaned asserts the
    /// item is gone, which is a finding; this one asserts nothing. Collapsing
    /// the two would report deleted items that are sitting there intact.
    /// </remarks>
    ItemUnreadable,
}

/// <summary>
/// Why a link's audience could not be determined.
/// </summary>
/// <remarks>
/// Two quite different situations arrive at the same badge, and the reader
/// cannot act on either without knowing which. Stable codes rather than
/// sentences, so the wording lives with the screen that shows it.
/// </remarks>
public static class ScopeUnavailableReason
{
    /// <summary>The item is not in a document library, so there is no drive to read link permissions through.</summary>
    public const string OutsideLibrary = "outside-library";

    /// <summary>The item's permissions were read, but none could be tied to this particular link.</summary>
    public const string LinkNotMatched = "link-not-matched";
}

/// <param name="MembersUnavailable">
/// True when the link's group members could not be read. Group membership is
/// the one thing in this application that genuinely needs rights beyond
/// <c>Sites.Read.All</c>, so it degrades on its own rather than failing the
/// row: everything else about the link is still worth showing.
/// </param>
/// <param name="ScopeReason">
/// Set only alongside <see cref="SharingLinkStatus.ScopeUnavailable"/>. See
/// <see cref="ScopeUnavailableReason"/>.
/// </param>
public sealed record SharingLinkDetail(
    string ItemGuid,
    string LinkGuid,
    SharingLinkStatus Status,
    string? Path,
    string ItemType,
    LinkPermission? Permission,
    IReadOnlyList<Recipient> Members,
    bool MembersUnavailable = false,
    string? ScopeReason = null);
