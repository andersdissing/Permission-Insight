using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Web;
using PermissionInsight.Api.Configuration;

namespace PermissionInsight.Api.Downstream;

/// <summary>
/// The SharePoint REST endpoints Microsoft Graph does not expose: site groups,
/// item resolution by GUID, and group membership.
///
/// Read only, because every call goes through <see cref="ReadOnlyHttpClient"/>.
/// </summary>
public sealed class SharePointClient
{
    private const string ServiceName = "SharePoint";

    /// <summary>The endpoint returns 100 per page by default. Ask for more and follow the link.</summary>
    private const int PageSize = 500;

    /// <summary>See GetListItemsPageAsync for why this is 1,000 and not 5,000.</summary>
    private const int ItemPageSize = 1000;

    private readonly ReadOnlyHttpClient _client;
    private readonly ApiOptions _options;

    public SharePointClient(ReadOnlyHttpClient client, ApiOptions options)
    {
        _client = client;
        _options = options;
    }

    /// <summary>
    /// The app-only token audience for SharePoint REST, derived from the
    /// configured tenant root rather than from anything a caller supplied.
    /// </summary>
    public string Scope => $"{_options.SharePointRootUrl}/.default";

    /// <summary>
    /// Every sharing link in a site collection, from one paged call.
    /// </summary>
    public async Task<IReadOnlyList<SharingLinkGroup>> GetSharingLinkGroupsAsync(
        string siteWebUrl,
        CancellationToken cancellationToken)
    {
        var url = $"{Guard(siteWebUrl)}/_api/web/sitegroups?$select=Id,Title&$top={PageSize}";
        var links = new List<SharingLinkGroup>();

        // Pages are followed here rather than in the browser because this is
        // one logical answer: the total is the first thing the tab shows, and
        // a half-read page would be a wrong total rather than a slow one.
        while (url is not null)
        {
            using var document = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);

            if (document.RootElement.TryGetProperty("value", out var groups) &&
                groups.ValueKind == JsonValueKind.Array)
            {
                foreach (var group in groups.EnumerateArray())
                {
                    var id = group.TryGetProperty("Id", out var idValue) && idValue.TryGetInt32(out var parsed)
                        ? parsed
                        : -1;

                    var title = group.TryGetProperty("Title", out var titleValue)
                        ? titleValue.GetString()
                        : null;

                    if (id >= 0 && SharingLinkGroup.TryParse(id, title) is { } link)
                    {
                        links.Add(link);
                    }
                }
            }

            // Guarded again, not just on the first URL. The continuation comes
            // out of a response body, and the token attached to it grants
            // tenant-wide read, so a next link is treated like any other URL
            // this class did not construct.
            url = NextLink(document.RootElement) is { } next ? Guard(next) : null;
        }

        return links;
    }

    /// <summary>
    /// The server-relative path of the item a sharing link points at.
    /// </summary>
    /// <returns>
    /// Null when the item is gone. Both calls returning 404 is the orphaned
    /// case, which is a finding rather than an error.
    /// </returns>
    public async Task<(string Path, string ItemType)?> ResolveItemAsync(
        string siteWebUrl,
        string itemGuid,
        CancellationToken cancellationToken)
    {
        var site = Guard(siteWebUrl);
        var guid = Uri.EscapeDataString(itemGuid);

        var file = await TryGetServerRelativeUrlAsync(
            $"{site}/_api/web/GetFileById('{guid}')?$select=ServerRelativeUrl",
            cancellationToken).ConfigureAwait(false);

        if (file is not null)
        {
            return (file, "file");
        }

        var folder = await TryGetServerRelativeUrlAsync(
            $"{site}/_api/web/GetFolderById('{guid}')?$select=ServerRelativeUrl",
            cancellationToken).ConfigureAwait(false);

        return folder is not null ? (folder, "folder") : null;
    }

    /// <summary>
    /// Direct members of a site group.
    /// </summary>
    /// <remarks>
    /// Used for orphaned links, where there is no item left to read a
    /// permission object from and the members are the only thing that says who
    /// still holds the link.
    /// </remarks>
    public async Task<IReadOnlyList<GroupMember>> GetGroupMembersAsync(
        string siteWebUrl,
        int groupId,
        CancellationToken cancellationToken)
    {
        var url = $"{Guard(siteWebUrl)}/_api/web/sitegroups({groupId})/users?$select=Title,Email,LoginName,PrincipalType";

        using var document = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);

        var members = new List<GroupMember>();

        if (document.RootElement.TryGetProperty("value", out var users) &&
            users.ValueKind == JsonValueKind.Array)
        {
            foreach (var user in users.EnumerateArray())
            {
                var name = GetString(user, "Title");
                var email = GetString(user, "Email");
                var loginName = GetString(user, "LoginName");

                if (name is null && email is null)
                {
                    continue;
                }

                members.Add(new GroupMember(
                    name ?? email!,
                    email,
                    IsExternal(loginName, email),
                    GetInt(user, "PrincipalType") ?? 0,
                    IsEntraGroup(loginName, GetInt(user, "PrincipalType") ?? 0)));
            }
        }

        return members;
    }

    /// <summary>
    /// Whether a member of a SharePoint group is itself a directory group, in
    /// which case the application cannot see inside it.
    /// </summary>
    /// <remarks>
    /// Recognised by claim provider prefix rather than by principal type
    /// alone: <c>c:0t.c|tenant|</c> is an Entra security group and
    /// <c>c:0o.c|federateddirectoryclaimprovider|</c> a Microsoft 365 group,
    /// and both arrive with principal type 4.
    /// </remarks>
    internal static bool IsEntraGroup(string? loginName, int principalType)
    {
        if (loginName is not null &&
            (loginName.StartsWith("c:0t.c|tenant|", StringComparison.OrdinalIgnoreCase) ||
             loginName.StartsWith("c:0o.c|federateddirectoryclaimprovider|", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // 4 is SecurityGroup. A SharePoint group is 8 and a user is 1, so
        // anything left at 4 came from the directory.
        return principalType == 4;
    }

    /// <summary>
    /// Every list and library in the web, with the counts and flags the tab
    /// needs to decide what to show and what to scan.
    /// </summary>
    /// <remarks>
    /// No <c>$select</c>. IsDefaultDocumentLibrary is not documented as
    /// selectable on every SharePoint build, and a rejected <c>$select</c>
    /// fails the whole call rather than omitting one field. There are a couple
    /// of dozen lists in a site, so asking for everything costs nothing worth
    /// the risk.
    /// </remarks>
    public async Task<IReadOnlyList<ListSummary>> GetListsAsync(
        string siteWebUrl,
        CancellationToken cancellationToken)
    {
        var url = $"{Guard(siteWebUrl)}/_api/web/lists?$top=500";

        using var document = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);

        var lists = new List<ListSummary>();

        if (document.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in value.EnumerateArray())
            {
                var id = GetString(element, "Id");
                var title = GetString(element, "Title");

                if (id is null || title is null)
                {
                    continue;
                }

                var baseTemplate = GetInt(element, "BaseTemplate") ?? 0;

                lists.Add(new ListSummary(
                    Id: id,
                    Title: title,
                    // Null when absent rather than zero. Zero means "nothing to
                    // scan" downstream, and a list SharePoint said nothing
                    // about must not be skipped as though it were empty.
                    ItemCount: GetInt(element, "ItemCount"),
                    Hidden: GetBool(element, "Hidden"),
                    BaseTemplate: baseTemplate,
                    // BaseType 1 is a document library. BaseTemplate 101 is the
                    // ordinary one; other templates share the base type.
                    IsDocumentLibrary: (GetInt(element, "BaseType") ?? 0) == 1,
                    IsDefaultDocumentLibrary: GetBool(element, "IsDefaultDocumentLibrary"),
                    // Null when absent rather than false. A library whose
                    // permission state could not be read must not be reported
                    // as inheriting.
                    HasUniqueRoleAssignments: GetBoolOrNull(element, "HasUniqueRoleAssignments")));
            }
        }

        return await FillInheritanceAsync(siteWebUrl, lists, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fills in <c>HasUniqueRoleAssignments</c>, which the unfiltered lists
    /// collection does not return.
    /// </summary>
    /// <remarks>
    /// A second call, because the first deliberately sends no <c>$select</c>:
    /// <c>IsDefaultDocumentLibrary</c> is not selectable on every SharePoint
    /// build and a rejected <c>$select</c> fails the whole request rather than
    /// omitting one field. Asking separately keeps that risk off the call that
    /// matters.
    ///
    /// It was worth finding. Without this every list reported "permissions
    /// unknown" — the library badge said nothing on any tenant, and site level
    /// access, which is derived through a list that inherits, had no list it
    /// could trust.
    ///
    /// A failure here is not a failure of the tab. The flag returns to null,
    /// which the front end already renders as unknown rather than as
    /// inheriting.
    /// </remarks>
    private async Task<IReadOnlyList<ListSummary>> FillInheritanceAsync(
        string siteWebUrl,
        List<ListSummary> lists,
        CancellationToken cancellationToken)
    {
        if (lists.Count == 0)
        {
            return lists;
        }

        Dictionary<string, bool> flags;

        try
        {
            var url = $"{Guard(siteWebUrl)}/_api/web/lists?$select=Id,HasUniqueRoleAssignments&$top=500";
            using var document = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("value", out var value) ||
                value.ValueKind != JsonValueKind.Array)
            {
                return lists;
            }

            flags = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (var element in value.EnumerateArray())
            {
                if (GetString(element, "Id") is { } id &&
                    GetBoolOrNull(element, "HasUniqueRoleAssignments") is { } unique)
                {
                    flags[id] = unique;
                }
            }
        }
        catch (DownstreamException)
        {
            // The tab still works without it. Reporting "unknown" is the
            // honest answer and is what happened before this call existed.
            return lists;
        }

        for (var i = 0; i < lists.Count; i++)
        {
            if (lists[i].HasUniqueRoleAssignments is null &&
                flags.TryGetValue(lists[i].Id, out var unique))
            {
                lists[i] = lists[i] with { HasUniqueRoleAssignments = unique };
            }
        }

        return lists;
    }

    /// <summary>
    /// One page of list items, flat.
    /// </summary>
    /// <remarks>
    /// The endpoint returns every item at every folder depth, with folders
    /// appearing as items where FileSystemObjectType is 1, so folder depth
    /// never requires recursion.
    ///
    /// HasUniqueRoleAssignments is in the <c>$select</c> deliberately. Loading
    /// it per item afterwards is 10,000 round trips and a throttling incident.
    ///
    /// Page size is 1,000 rather than 5,000 on purpose: at 5,000 a 12,000 item
    /// library reports progress three times, at 1,000 it reports thirteen,
    /// which is still cheap and actually looks like progress.
    /// </remarks>
    public async Task<ListItemPage> GetListItemsPageAsync(
        string siteWebUrl,
        string listId,
        string? skipToken,
        CancellationToken cancellationToken)
    {
        var site = Guard(siteWebUrl);
        var list = Uri.EscapeDataString(listId);

        var url =
            $"{site}/_api/web/lists(guid'{list}')/items" +
            "?$select=ID,HasUniqueRoleAssignments,FileRef,FileLeafRef,FileSystemObjectType" +
            $"&$top={ItemPageSize}";

        if (!string.IsNullOrEmpty(skipToken))
        {
            url += $"&$skiptoken={Uri.EscapeDataString(skipToken)}";
        }

        using var document = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);

        var items = new List<ListItemSummary>(ItemPageSize);

        // An empty page proves nothing either way, so it is not treated as
        // blindness.
        var flagPresent = true;
        var sawAnyItem = false;

        if (document.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in value.EnumerateArray())
            {
                var id = GetInt(element, "ID") ?? GetInt(element, "Id");
                if (id is null)
                {
                    continue;
                }

                if (!sawAnyItem)
                {
                    sawAnyItem = true;
                    flagPresent = element.TryGetProperty("HasUniqueRoleAssignments", out var flag) &&
                                  flag.ValueKind is JsonValueKind.True or JsonValueKind.False;
                }

                items.Add(new ListItemSummary(
                    Id: id.Value,
                    HasUniqueRoleAssignments: GetBool(element, "HasUniqueRoleAssignments"),
                    FileRef: GetString(element, "FileRef"),
                    FileLeafRef: GetString(element, "FileLeafRef"),
                    FileSystemObjectType: GetInt(element, "FileSystemObjectType") ?? 0));
            }
        }

        return new ListItemPage(
            items,
            ExtractSkipToken(NextLink(document.RootElement)),
            PermissionFlagPresent: !sawAnyItem || flagPresent);
    }

    /// <summary>
    /// Pulls the opaque paging token out of SharePoint's next link so the
    /// browser never handles a URL the backend will later call.
    /// </summary>
    internal static string? ExtractSkipToken(string? nextLink)
    {
        if (string.IsNullOrEmpty(nextLink) || !Uri.TryCreate(nextLink, UriKind.Absolute, out var uri))
        {
            return null;
        }

        // Parsed rather than matched on a literal "$skiptoken=". SharePoint
        // percent-encodes the dollar as %24 in the next link, so a substring
        // search finds nothing, paging silently stops after the first page,
        // and a 12,000 item library reports exactly 1,000 items with a
        // straight face.
        var query = HttpUtility.ParseQueryString(uri.Query);

        return query["$skiptoken"] ?? query["%24skiptoken"] ?? query["skiptoken"];
    }

    private static int? GetInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static bool GetBool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Distinguishes "SharePoint said false" from "SharePoint did not say".
    /// The two mean different things wherever a permission is involved.
    /// </summary>
    private static bool? GetBoolOrNull(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.ValueKind == JsonValueKind.True
            : null;

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// A guest arrives with #ext# in the login name, which survives even when
    /// the mail address looks internal.
    /// </summary>
    internal static bool IsExternal(string? loginName, string? email)
    {
        if (loginName is not null &&
            (loginName.Contains("#ext#", StringComparison.OrdinalIgnoreCase) ||
             loginName.Contains("urn:spo:guest", StringComparison.OrdinalIgnoreCase) ||
             loginName.Contains("|guest|", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return email is not null && email.Contains("#ext#", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string?> TryGetServerRelativeUrlAsync(string url, CancellationToken cancellationToken)
    {
        using var request = BuildRequest(url);
        using var response = await _client.SendAsync(request, Scope, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw await DownstreamException.FromAsync(response, ServiceName, cancellationToken).ConfigureAwait(false);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        return document.RootElement.TryGetProperty("ServerRelativeUrl", out var value)
            ? value.GetString()
            : null;
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var request = BuildRequest(url);
        using var response = await _client.SendAsync(request, Scope, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw await DownstreamException.FromAsync(response, ServiceName, cancellationToken).ConfigureAwait(false);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static HttpRequestMessage BuildRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);

        // nometadata keeps the payload small and puts paging under the plain
        // "odata.nextLink" name rather than the verbose "d.__next".
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json")
        {
            Parameters = { new NameValueHeaderValue("odata", "nometadata") },
        });

        return request;
    }

    private static string? NextLink(JsonElement root)
    {
        foreach (var name in new[] { "odata.nextLink", "@odata.nextLink" })
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    /// <summary>
    /// Refuses any address outside the configured SharePoint tenant.
    /// </summary>
    /// <remarks>
    /// Site URLs reach this class from Microsoft Graph rather than from the
    /// browser, so this should never fire. It is here because the alternative
    /// to checking is a component that will forward an application token
    /// holding tenant-wide read to whatever host it is handed, and that is not
    /// a property to leave resting on the call sites staying correct.
    /// </remarks>
    private string Guard(string siteWebUrl)
    {
        var trimmed = siteWebUrl.TrimEnd('/');
        var root = _options.SharePointRootUrl;

        var inTenant =
            trimmed.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith($"{root}/", StringComparison.OrdinalIgnoreCase);

        return inTenant
            ? trimmed
            : throw new InvalidOperationException(
                $"Refused to call '{trimmed}'. Only addresses under {root} are permitted.");
    }
}
