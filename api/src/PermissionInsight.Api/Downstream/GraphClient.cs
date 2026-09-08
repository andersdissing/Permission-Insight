using System.Text.Json;

namespace PermissionInsight.Api.Downstream;

/// <summary>
/// Microsoft Graph, reached as the application identity. Read only, because
/// every call goes through <see cref="ReadOnlyHttpClient"/>.
/// </summary>
public sealed class GraphClient
{
    public const string Scope = "https://graph.microsoft.com/.default";

    private const string BaseUrl = "https://graph.microsoft.com/v1.0";
    private const string ServiceName = "Microsoft Graph";

    private readonly ReadOnlyHttpClient _client;

    public GraphClient(ReadOnlyHttpClient client) => _client = client;

    /// <summary>
    /// Searches sites by title and URL across the whole tenant.
    /// </summary>
    /// <remarks>
    /// The query carries <c>search</c> and nothing else. <c>$select</c> and
    /// <c>$top</c> are deliberately not sent: their behaviour alongside
    /// <c>search</c> on this endpoint is one of the items phase 1 flags
    /// VERIFY, and guessing at an API shape is the failure this project
    /// specifically forbids. Trimming and the result ceiling are applied here
    /// instead, which costs a slightly larger response and nothing else.
    /// Run scripts/probe-verify.ps1 against a test tenant before changing it.
    ///
    /// Results are not security trimmed, because the token is application
    /// rather than delegated. A site the signed-in user cannot open still
    /// appears, which is the entire point of the permission model.
    /// </remarks>
    public async Task<SiteSearchResult> SearchSitesAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        var uri = new Uri($"{BaseUrl}/sites?search={Uri.EscapeDataString(query)}");

        using var response = await _client.GetAsync(uri, Scope, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await DownstreamException.FromAsync(response, ServiceName, cancellationToken).ConfigureAwait(false);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var sites = new List<SiteSummary>(limit);
        var matched = 0;

        if (document.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in value.EnumerateArray())
            {
                matched++;
                if (sites.Count >= limit)
                {
                    continue;
                }

                var site = ToSummary(element);
                if (site is not null)
                {
                    sites.Add(site);
                }
            }
        }

        var hasMorePages = document.RootElement.TryGetProperty("@odata.nextLink", out _);

        return new SiteSearchResult(sites, Truncated: matched > limit || hasMorePages);
    }

    private static SiteSummary? ToSummary(JsonElement element)
    {
        var id = GetString(element, "id");
        var webUrl = GetString(element, "webUrl");

        // A site with no id or no address cannot be selected, so it is not a
        // result. Silently dropping it beats a row that does nothing on click.
        if (id is null || webUrl is null)
        {
            return null;
        }

        var title = GetString(element, "displayName")
            ?? GetString(element, "name")
            ?? webUrl;

        var serverRelativeUrl = Uri.TryCreate(webUrl, UriKind.Absolute, out var absolute)
            ? absolute.AbsolutePath
            : webUrl;

        return new SiteSummary(id, title, serverRelativeUrl, webUrl);
    }

    /// <summary>
    /// The people picker in Search for person.
    /// </summary>
    /// <remarks>
    /// Backed by User.ReadBasic.All, which returns a name and an address and
    /// nothing else. The alternative — offering only principals already seen
    /// in a scan — needs no permission at all and was rejected: a person with
    /// no access would then not appear, and users would read a missing name as
    /// a failed search rather than as an answer.
    /// </remarks>
    public async Task<IReadOnlyList<Person>> SearchPeopleAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        var term = query.Replace("'", "''", StringComparison.Ordinal);

        // Users and groups together. A permission is as likely to be held by a
        // group as by a person, and making the caller guess which they are
        // looking for is a worse experience than searching both.
        var users = SearchAsync(
            $"{BaseUrl}/users?$filter={Uri.EscapeDataString($"startswith(displayName,'{term}') or startswith(mail,'{term}') or startswith(userPrincipalName,'{term}')")}" +
            $"&$select=id,displayName,mail,userPrincipalName&$top={limit}",
            "user",
            cancellationToken);

        var groups = SearchAsync(
            $"{BaseUrl}/groups?$filter={Uri.EscapeDataString($"startswith(displayName,'{term}') or startswith(mail,'{term}')")}" +
            $"&$select=id,displayName,mail&$top={limit}",
            "group",
            cancellationToken);

        await Task.WhenAll(users, groups).ConfigureAwait(false);

        return [.. (await users.ConfigureAwait(false)).Concat(await groups.ConfigureAwait(false))
            .OrderBy(person => person.DisplayName, StringComparer.CurrentCultureIgnoreCase)];
    }

    private async Task<List<Person>> SearchAsync(string url, string kind, CancellationToken cancellationToken)
    {
        var results = new List<Person>();

        try
        {
            using var document = await GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);

            if (document.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in value.EnumerateArray())
                {
                    var name = GetString(element, "displayName");
                    var id = GetString(element, "id");

                    if (name is null || id is null)
                    {
                        continue;
                    }

                    var address = GetString(element, "mail") ?? GetString(element, "userPrincipalName");

                    results.Add(new Person(id, name, address ?? string.Empty, address ?? id, kind));
                }
            }
        }
        catch (DownstreamException)
        {
            // One half of the search failing should not lose the other. A
            // missing group result is visible as an absence; an error banner
            // over a working user search is not.
        }

        return results;
    }

    /// <summary>
    /// Permissions on a list or library itself, which is where "top level"
    /// access lives.
    /// </summary>
    /// <remarks>
    /// A grant here reaches everything inside that inherits, so it deserves to
    /// be stated separately rather than shown as one row among thousands.
    /// </remarks>
    public async Task<IReadOnlyList<ItemAccess>> GetListAccessAsync(
        string siteId,
        string listId,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(
            $"{BaseUrl}/sites/{Uri.EscapeDataString(siteId)}/lists/{Uri.EscapeDataString(listId)}/permissions");

        using var document = await GetAsync(uri, cancellationToken).ConfigureAwait(false);

        var access = new List<ItemAccess>();

        if (document.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in value.EnumerateArray())
            {
                if (ToItemAccess(element) is { } entry)
                {
                    access.Add(entry);
                }
            }
        }

        return access;
    }

    /// <summary>
    /// Direct members of an Entra group.
    /// </summary>
    /// <remarks>
    /// Backed by GroupMember.Read.All. The specification originally refused
    /// this permission, on the grounds that getusereffectivepermissions
    /// answered the underlying question better by resolving the whole chain
    /// server-side. That endpoint turned out to be unreachable on read-only
    /// permissions, so the chain has to be walked here instead, one level at a
    /// time. See docs/adr/0012.
    ///
    /// Nested groups are returned and marked rather than expanded. One level
    /// is what the panel shows, and a row that says "group" is honest where a
    /// silently flattened list would not be.
    /// </remarks>
    public async Task<IReadOnlyList<GroupMember>> GetEntraGroupMembersAsync(
        string groupId,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(
            $"{BaseUrl}/groups/{Uri.EscapeDataString(groupId)}/members" +
            "?$select=id,displayName,mail,userPrincipalName&$top=100");

        using var document = await GetAsync(uri, cancellationToken).ConfigureAwait(false);

        var members = new List<GroupMember>();

        if (document.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in value.EnumerateArray())
            {
                var name = GetString(element, "displayName");
                var mail = GetString(element, "mail") ?? GetString(element, "userPrincipalName");

                if (name is null && mail is null)
                {
                    continue;
                }

                var isGroup = GetString(element, "@odata.type")?.Contains("group", StringComparison.OrdinalIgnoreCase) == true;

                members.Add(new GroupMember(
                    name ?? mail!,
                    mail,
                    SharePointClient.IsExternal(null, mail),
                    isGroup ? 4 : 1,
                    isGroup));
            }
        }

        return members;
    }

    /// <summary>One site, for its web URL. The rest of the API takes Graph site ids, never a URL from the browser.</summary>
    public async Task<SiteSummary?> GetSiteAsync(string siteId, CancellationToken cancellationToken)
    {
        var uri = new Uri($"{BaseUrl}/sites/{Uri.EscapeDataString(siteId)}?$select=id,name,displayName,webUrl");

        using var document = await GetAsync(uri, cancellationToken).ConfigureAwait(false);
        return ToSummary(document.RootElement);
    }

    /// <summary>
    /// The document libraries in a site, with the server-relative path each one
    /// occupies. Fetched once per site so an item's path can be turned into a
    /// drive and a path within it.
    /// </summary>
    public async Task<IReadOnlyList<DriveLocation>> GetDrivesAsync(
        string siteId,
        CancellationToken cancellationToken)
    {
        var uri = new Uri($"{BaseUrl}/sites/{Uri.EscapeDataString(siteId)}/drives?$select=id,webUrl");

        using var document = await GetAsync(uri, cancellationToken).ConfigureAwait(false);

        var drives = new List<DriveLocation>();

        if (document.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in value.EnumerateArray())
            {
                var id = GetString(element, "id");
                var webUrl = GetString(element, "webUrl");

                if (id is null || webUrl is null || !Uri.TryCreate(webUrl, UriKind.Absolute, out var absolute))
                {
                    continue;
                }

                drives.Add(new DriveLocation(id, Uri.UnescapeDataString(absolute.AbsolutePath).TrimEnd('/')));
            }
        }

        return drives;
    }

    /// <summary>
    /// The permissions on one item, addressed by its path within a drive.
    /// </summary>
    /// <remarks>
    /// This is the call phase 2 leans on: it returns scope, roles, recipients,
    /// expiry and password state together, where the site group's name gives
    /// only the fact that a link exists.
    ///
    /// The item is addressed by path rather than by driveItem id because the
    /// GUID in the sharing link group's name is SharePoint's list item unique
    /// id, which is not a Graph driveItem id. Turning one into the other would
    /// need a lookup per item; the path comes out of the same GetFileById call
    /// that resolves the row anyway, so this costs nothing extra.
    /// </remarks>
    public async Task<IReadOnlyList<LinkPermission>> GetLinkPermissionsAsync(
        string driveId,
        string pathInDrive,
        CancellationToken cancellationToken)
    {
        var drive = $"{BaseUrl}/drives/{Uri.EscapeDataString(driveId)}";

        var uri = pathInDrive.Length == 0
            ? new Uri($"{drive}/root/permissions")
            : new Uri($"{drive}/root:/{EscapePath(pathInDrive)}:/permissions");

        using var document = await GetAsync(uri, cancellationToken).ConfigureAwait(false);

        var permissions = new List<LinkPermission>();

        if (document.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in value.EnumerateArray())
            {
                if (ToLinkPermission(element) is { } permission)
                {
                    permissions.Add(permission);
                }
            }
        }

        return permissions;
    }

    /// <summary>
    /// Everyone who has access to one item, read through Graph rather than
    /// through SharePoint's roleassignments.
    /// </summary>
    /// <remarks>
    /// This is the call that makes the whole product work on read-only
    /// permissions. SharePoint's own <c>roleassignments</c> endpoint refuses an
    /// app-only <c>Sites.Read.All</c> token, because it needs
    /// <c>EnumeratePermissions</c> which lives in Full Control. Graph answers
    /// the same question and documents <c>Sites.Read.All</c> as its *least*
    /// privileged application permission.
    ///
    /// Addressed as a <b>listItem</b> rather than a driveItem on purpose.
    /// Only document library items have driveItems, so the driveItem route
    /// cannot see a generic list at all; the listItem route covers every list
    /// type, and takes the site, list and item ids the scan already holds
    /// instead of needing a path resolved to a drive.
    ///
    /// It also returns more than SharePoint did: <c>inheritedFrom</c> says
    /// whether a permission belongs to this item or came from an ancestor,
    /// which SharePoint made us infer.
    /// </remarks>
    public async Task<IReadOnlyList<ItemAccess>> GetItemAccessAsync(
        string siteId,
        string listId,
        int itemId,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(
            $"{BaseUrl}/sites/{Uri.EscapeDataString(siteId)}" +
            $"/lists/{Uri.EscapeDataString(listId)}" +
            $"/items/{itemId}/permissions");

        using var document = await GetAsync(uri, cancellationToken).ConfigureAwait(false);

        var access = new List<ItemAccess>();

        if (document.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in value.EnumerateArray())
            {
                if (ToItemAccess(element) is { } entry)
                {
                    access.Add(entry);
                }
            }
        }

        return access;
    }

    private static ItemAccess? ToItemAccess(JsonElement element)
    {
        var roles = element.TryGetProperty("roles", out var roleList) && roleList.ValueKind == JsonValueKind.Array
            ? roleList.EnumerateArray().Select(role => role.GetString() ?? string.Empty).Where(role => role.Length > 0).ToArray()
            : [];

        var isLink = element.TryGetProperty("link", out var link) && link.ValueKind == JsonValueKind.Object;

        // Inherited permissions are not findings on this item. They belong to
        // the ancestor they came from, and reporting them here would make one
        // broken folder look like a hundred.
        var inherited = element.TryGetProperty("inheritedFrom", out var from) && from.ValueKind == JsonValueKind.Object;

        var (name, kind, login, reference) = ReadIdentity(element);

        if (name is null && !isLink)
        {
            return null;
        }

        // Classified from the claim, and from every identifier Graph gave —
        // which facet carries it depends on how Graph chose to represent the
        // principal. The display name is deliberately not offered: it is
        // localised, and matching it would mean finding nothing on a tenant
        // that does not run in English.
        var tenantWide = TenantWideClaim.Classify(login, reference);

        return new ItemAccess(
            PrincipalName: name ?? (isLink ? LinkScopeLabel(link) : "Unknown"),
            PrincipalKind: kind,
            LoginName: login,
            PrincipalRef: reference,
            Roles: roles,
            Source: isLink ? RoleAssignmentSummary.SharingLinkSource : RoleAssignmentSummary.DirectGrantSource,
            LinkScope: isLink ? GetString(link, "scope") : null,
            Expiry: element.TryGetProperty("expirationDateTime", out var expires) &&
                    expires.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(expires.GetString(), out var parsed)
                ? parsed
                : null,
            Inherited: inherited,
            TenantWide: tenantWide);
    }

    /// <summary>
    /// Who the permission was granted to, and what kind of principal that is.
    /// </summary>
    /// <remarks>
    /// An Entra group is named and left unopened. The application deliberately
    /// does not hold <c>GroupMember.Read.All</c>, so the chain stops here and
    /// the row has to say so — an expansion that rendered empty would read as
    /// "nobody has access".
    /// </remarks>
    private static (string? Name, string Kind, string? Login, string? Ref) ReadIdentity(JsonElement permission)
    {
        foreach (var property in new[] { "grantedToV2", "grantedTo" })
        {
            if (!permission.TryGetProperty(property, out var identity) || identity.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (identity.TryGetProperty("siteGroup", out var siteGroup) && siteGroup.ValueKind == JsonValueKind.Object)
            {
                return (
                    GetString(siteGroup, "displayName"),
                    "SharePointGroup",
                    GetString(siteGroup, "loginName"),
                    // The numeric site group id, which people.aspx needs.
                    GetString(siteGroup, "id"));
            }

            if (identity.TryGetProperty("group", out var group) && group.ValueKind == JsonValueKind.Object)
            {
                var id = GetString(group, "id");
                return (GetString(group, "displayName"), "EntraGroup", id, id);
            }

            if (identity.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
            {
                return (
                    GetString(user, "displayName") ?? GetString(user, "email"),
                    "User",
                    GetString(user, "email") ?? GetString(user, "id"),
                    GetString(user, "id"));
            }

            if (identity.TryGetProperty("siteUser", out var siteUser) && siteUser.ValueKind == JsonValueKind.Object)
            {
                return (
                    GetString(siteUser, "displayName"),
                    "User",
                    GetString(siteUser, "loginName"),
                    GetString(siteUser, "id"));
            }
        }

        // A sharing link with recipients names them separately.
        if (permission.TryGetProperty("grantedToIdentitiesV2", out var identities) &&
            identities.ValueKind == JsonValueKind.Array)
        {
            var names = identities.EnumerateArray()
                .Select(identity =>
                    identity.TryGetProperty("user", out var user)
                        ? GetString(user, "displayName") ?? GetString(user, "email")
                        : null)
                .Where(name => name is not null)
                .ToArray();

            if (names.Length > 0)
            {
                return (string.Join(", ", names), "User", null, null);
            }
        }

        return (null, "Unknown", null, null);
    }

    private static string LinkScopeLabel(JsonElement link) => GetString(link, "scope") switch
    {
        "anonymous" => "Anyone with the link",
        "organization" => "Anyone in the organization",
        "users" => "Specific people",
        _ => "Sharing link",
    };

    private static LinkPermission? ToLinkPermission(JsonElement element)
    {
        // Only sharing links. A permission with no link facet is a direct
        // grant, which belongs to the library scan rather than this tab.
        if (!element.TryGetProperty("link", out var link) || link.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = GetString(element, "id") ?? string.Empty;
        var scope = GetString(link, "scope") ?? "unknown";
        var role = element.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array
            ? roles.EnumerateArray().FirstOrDefault().GetString() ?? GetString(link, "type") ?? "read"
            : GetString(link, "type") ?? "read";

        var expiry = element.TryGetProperty("expirationDateTime", out var expires) &&
                     expires.ValueKind == JsonValueKind.String &&
                     DateTimeOffset.TryParse(expires.GetString(), out var parsed)
            ? parsed
            : (DateTimeOffset?)null;

        var hasPassword = element.TryGetProperty("hasPassword", out var password) &&
                          password.ValueKind == JsonValueKind.True;

        return new LinkPermission(
            id,
            scope,
            role,
            ReadRecipients(element),
            expiry,
            hasPassword);
    }

    private static IReadOnlyList<Recipient> ReadRecipients(JsonElement permission)
    {
        var recipients = new List<Recipient>();

        if (!permission.TryGetProperty("grantedToIdentitiesV2", out var identities) ||
            identities.ValueKind != JsonValueKind.Array)
        {
            return recipients;
        }

        foreach (var identity in identities.EnumerateArray())
        {
            if (!identity.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = GetString(user, "displayName");
            var email = GetString(user, "email");

            if (name is null && email is null)
            {
                continue;
            }

            recipients.Add(new Recipient(name ?? email!, email, SharePointClient.IsExternal(null, email)));
        }

        return recipients;
    }

    /// <summary>Escapes each segment while leaving the separators alone.</summary>
    internal static string EscapePath(string path) =>
        string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private async Task<JsonDocument> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(uri, Scope, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw await DownstreamException.FromAsync(response, ServiceName, cancellationToken).ConfigureAwait(false);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
