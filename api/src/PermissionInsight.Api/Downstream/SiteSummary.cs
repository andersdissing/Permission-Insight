namespace PermissionInsight.Api.Downstream;

/// <summary>One row in the site picker.</summary>
/// <param name="Id">Graph site id, the composite the rest of the API takes.</param>
/// <param name="Title">Site title, in whatever language it exists in.</param>
/// <param name="Url">Server-relative URL, which is what the picker shows.</param>
/// <param name="WebUrl">Absolute URL, for linking out to SharePoint.</param>
public sealed record SiteSummary(string Id, string Title, string Url, string WebUrl);

/// <param name="Results">At most the requested number of sites.</param>
/// <param name="Truncated">More sites matched than were returned, so the query needs refining.</param>
public sealed record SiteSearchResult(IReadOnlyList<SiteSummary> Results, bool Truncated);

/// <summary>A document library, and the server-relative path it occupies.</summary>
public sealed record DriveLocation(string DriveId, string ServerRelativePath);

/// <summary>One row in the picker: a person or a group.</summary>
/// <param name="Kind">"user" or "group". Decides the icon and how a match is recognised in a scan.</param>
public sealed record Person(
    string Id,
    string DisplayName,
    string Mail,
    string UserPrincipalName,
    string Kind);

/// <summary>
/// What the backend needs to know about a site to resolve items in it: where
/// it lives, and which library each path belongs to.
/// </summary>
public sealed record SiteContext(
    string SiteId,
    string Title,
    string WebUrl,
    IReadOnlyList<DriveLocation> Drives)
{
    /// <summary>
    /// Turns a server-relative item path into the drive that holds it and the
    /// path within that drive.
    /// </summary>
    /// <remarks>
    /// Longest prefix wins, so a library called "Documents" does not claim an
    /// item that belongs to "Documents archive".
    /// </remarks>
    public (string DriveId, string PathInDrive)? Locate(string serverRelativePath)
    {
        DriveLocation? best = null;

        foreach (var drive in Drives)
        {
            var isMatch =
                serverRelativePath.Equals(drive.ServerRelativePath, StringComparison.OrdinalIgnoreCase) ||
                serverRelativePath.StartsWith($"{drive.ServerRelativePath}/", StringComparison.OrdinalIgnoreCase);

            if (isMatch && (best is null || drive.ServerRelativePath.Length > best.ServerRelativePath.Length))
            {
                best = drive;
            }
        }

        if (best is null)
        {
            return null;
        }

        var remainder = serverRelativePath[best.ServerRelativePath.Length..].TrimStart('/');
        return (best.DriveId, remainder);
    }
}
