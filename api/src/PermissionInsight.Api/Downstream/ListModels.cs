namespace PermissionInsight.Api.Downstream;

/// <param name="ItemCount">
/// The denominator for the progress bar and the input to the size badge, which
/// is why it is fetched here rather than per scan.
///
/// Null when SharePoint did not return it. That matters more than it looks:
/// zero means the list is empty and therefore has nothing to scan, so
/// collapsing "didn't say" into zero would quietly skip a list that might hold
/// thousands of items. Same reasoning as
/// <see cref="HasUniqueRoleAssignments"/>.
/// </param>
/// <param name="HasUniqueRoleAssignments">
/// Describes the list object only. A library that inherits can still contain
/// thousands of broken items, so this is a finding about the library, never an
/// answer about its contents.
///
/// Null when SharePoint did not return the property at all, which is a
/// different answer from false and must not be flattened into it: "this
/// library inherits" and "we could not tell" look identical on screen unless
/// the distinction is kept here.
/// </param>
public sealed record ListSummary(
    string Id,
    string Title,
    int? ItemCount,
    bool Hidden,
    int BaseTemplate,
    bool IsDocumentLibrary,
    bool IsDefaultDocumentLibrary,
    bool? HasUniqueRoleAssignments);

/// <param name="FileSystemObjectType">0 for a file or list item, 1 for a folder.</param>
public sealed record ListItemSummary(
    int Id,
    bool HasUniqueRoleAssignments,
    string? FileRef,
    string? FileLeafRef,
    int FileSystemObjectType);

/// <param name="NextToken">
/// The opaque <c>$skiptoken</c> from SharePoint's own next link, not the link
/// itself. The browser hands it back and the backend rebuilds the URL, so a
/// caller can never steer an application token holding tenant-wide read at an
/// address of its choosing.
/// </param>
/// <param name="PermissionFlagPresent">
/// Whether SharePoint actually returned <c>HasUniqueRoleAssignments</c>.
///
/// This exists because the alternative is the worst answer this tool can give.
/// An identity that may read content but not permissions gets items back with
/// the flag missing, and treating missing as false reports "everything
/// inherits" for a library full of findings — confident, wrong, and
/// indistinguishable from a clean site. When this is false the scan is blind
/// and must say so rather than report zero.
/// </param>
public sealed record ListItemPage(
    IReadOnlyList<ListItemSummary> Items,
    string? NextToken,
    bool PermissionFlagPresent);

/// <param name="PrincipalType">1 user, 2 distribution list, 4 security group, 8 SharePoint group.</param>
public sealed record PrincipalSummary(int Id, string Title, string LoginName, int PrincipalType);

/// <summary>
/// One principal's access to one item, as Graph reports it.
/// </summary>
/// <param name="PrincipalKind">
/// User, SharePointGroup, EntraGroup or Unknown. An EntraGroup is named and
/// not opened: the application holds no permission to look inside one, and a
/// row that rendered empty would read as "nobody has access".
/// </param>
/// <param name="Inherited">
/// True when the permission came from an ancestor rather than this item.
/// Inherited entries are not findings here; they belong to the ancestor.
/// </param>
/// <param name="PrincipalRef">
/// The principal's own identifier: a SharePoint group's numeric id, or a
/// directory object id for an Entra group or user. Kept separate from the
/// login name because it is what a deep link into SharePoint or Entra needs,
/// and what expanding a group is addressed by.
/// </param>
/// <param name="TenantWide">
/// Set when the principal is a claim that covers the whole organisation at
/// once — "Everyone", "Everyone except external users". Null for an ordinary
/// user or group. Detected from the claim rather than the display name, which
/// is localised; see <see cref="TenantWideClaim"/>.
/// </param>
public sealed record ItemAccess(
    string PrincipalName,
    string PrincipalKind,
    string? LoginName,
    string? PrincipalRef,
    IReadOnlyList<string> Roles,
    string Source,
    string? LinkScope,
    DateTimeOffset? Expiry,
    bool Inherited,
    TenantWidePrincipal? TenantWide = null);

/// <param name="Source">
/// "Sharing link" or "Direct grant". The distinction drives the labels in the
/// tree and matters for remediation: a direct grant never expires and is
/// rarely documented.
/// </param>
public sealed record RoleAssignmentSummary(
    PrincipalSummary Member,
    IReadOnlyList<string> Roles,
    string Source)
{
    public const string SharingLinkSource = "Sharing link";
    public const string DirectGrantSource = "Direct grant";

    /// <summary>
    /// SharePoint grants this automatically up the tree so a recipient can
    /// navigate to a shared item. It is noise, and leaving it in makes every
    /// library look far worse than it is.
    /// </summary>
    public const string LimitedAccess = "Limited Access";
}
