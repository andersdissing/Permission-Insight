using System.Text;
using System.Web;

namespace PermissionInsight.Api.Auditing;

/// <summary>
/// Decides which query string values may be written to the audit log.
///
/// The audit trail has to answer "who looked at which site, and when". It does
/// not have to answer "which documents were in it". Those are different
/// questions with different blast radii: a scan of one library sends one
/// request per broken item, each carrying that item's server-relative path, and
/// logging all of them would deposit a browsable index of an organisation's
/// most exposed documents into Log Analytics. That index would then sit outside
/// the app role — readable by anyone holding Reader on the workspace, which is
/// a different and wider population than the group that gates this tool — and
/// it would contradict what the application tells the user, which is that
/// paths are held in their own browser.
///
/// So the rule is an allow-list of names, not a deny-list of the paths known
/// today. Values not named here are replaced rather than dropped, because the
/// fact that a parameter was sent is itself worth auditing. A new parameter is
/// redacted until somebody decides otherwise, which is the safe direction for
/// this to fail in.
/// </summary>
public static class AuditQuery
{
    public const string RedactedValue = "[redacted]";

    /// <summary>
    /// Names whose values identify <em>what was looked at</em> rather than
    /// <em>what was in it</em>. Identifiers and search terms: opaque GUIDs,
    /// numeric ids, and the search box contents, which for a site or person
    /// search is the target and is the whole point of the entry.
    /// </summary>
    private static readonly HashSet<string> Auditable = new(StringComparer.OrdinalIgnoreCase)
    {
        "q",
        "siteId",
        "listId",
        "itemId",
        "itemGuid",
        "linkGuid",
        "groupId",
    };

    /// <summary>
    /// Rewrites a raw query string into the form that may be logged. Parameter
    /// order and names are preserved so the entry still reads like the request.
    /// </summary>
    public static string Redact(string? query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return string.Empty;
        }

        var parsed = HttpUtility.ParseQueryString(query);
        if (parsed.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder("?");

        for (var i = 0; i < parsed.Count; i++)
        {
            var name = parsed.GetKey(i);

            if (builder.Length > 1)
            {
                builder.Append('&');
            }

            // A valueless parameter parses with a null key. Keep it visible
            // without pretending to know what it was called.
            if (name is null)
            {
                builder.Append(RedactedValue);
                continue;
            }

            builder.Append(Uri.EscapeDataString(name)).Append('=');

            builder.Append(Auditable.Contains(name)
                ? Uri.EscapeDataString(parsed[i] ?? string.Empty)
                : RedactedValue);
        }

        return builder.ToString();
    }
}
