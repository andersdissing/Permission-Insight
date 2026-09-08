namespace PermissionInsight.Api.Downstream;

/// <summary>
/// Recognises the claim principals that grant access to everybody at once.
///
/// These are the highest-value findings the tool can report. A single grant to
/// "Everyone except external users" on a site reaches every employee in the
/// organisation, and unlike a group membership there is nothing to enumerate —
/// no members list gets longer, so nothing looks unusual. It reads on screen as
/// one ordinary row.
///
/// <b>Matching is on the claim token and never on the display name.</b> The
/// display name is localised: on a Danish tenant the same principal is called
/// "Alle undtagen eksterne brugere", on a German one "Jeder außer externen
/// Benutzern". Matching English text would mean the tool silently reports
/// nothing on exactly the tenants it was not developed against — the worst
/// failure shape available, because it looks like a clean result. The claim
/// itself is invariant, and the tenant id inside it is the only part that
/// varies.
///
/// The label the tool shows is its own, taken from this table rather than from
/// SharePoint, so a report reads the same whatever language the tenant is in.
/// </summary>
public static class TenantWideClaim
{
    /// <summary>
    /// Everyone, including guests and external users. The wider of the two and
    /// the one Microsoft hides from modern people pickers by default, so its
    /// presence usually means a legacy grant or a scripted one.
    /// </summary>
    public const string Everyone = "everyone";

    /// <summary>Every licensed user in the tenant. Excludes guests, includes everybody else.</summary>
    public const string EveryoneExceptExternal = "everyone-except-external";

    /// <summary>Every authenticated Windows account, from a hybrid or on-premises configuration.</summary>
    public const string AllWindowsUsers = "all-windows-users";

    /// <summary>
    /// The claim for "Everyone except external users" is
    /// <c>c:0-.f|rolemanager|spo-grid-all-users/{tenantId}</c>. Matched on the
    /// role manager segment rather than the whole string, because the tenant id
    /// is appended and a few tenants have been observed without it.
    /// </summary>
    private const string AllUsersRoleManager = "spo-grid-all-users";

    /// <summary>The claim for "Everyone". A fixed string with nothing tenant specific in it.</summary>
    private const string EveryoneClaim = "c:0(.s|true";

    /// <summary>The claim for "All Users (windows)".</summary>
    private const string WindowsClaim = "c:0!.s|windows";

    /// <summary>
    /// Classifies a principal from its claim, returning null for an ordinary
    /// user or group.
    /// </summary>
    /// <param name="candidates">
    /// Every identifier Graph gave for the principal. Which field carries the
    /// claim varies with the identity facet Graph chooses, so all of them are
    /// offered rather than guessing one.
    /// </param>
    public static TenantWidePrincipal? Classify(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var value = candidate.Trim();

            if (value.Contains(AllUsersRoleManager, StringComparison.OrdinalIgnoreCase))
            {
                return new TenantWidePrincipal(
                    EveryoneExceptExternal,
                    "Everyone except external users",
                    "Every licensed user in the organisation. Guests are excluded.",
                    IncludesExternal: false);
            }

            if (value.StartsWith(EveryoneClaim, StringComparison.OrdinalIgnoreCase))
            {
                return new TenantWidePrincipal(
                    Everyone,
                    "Everyone",
                    "Every user in the organisation, including guests and external users.",
                    IncludesExternal: true);
            }

            if (value.StartsWith(WindowsClaim, StringComparison.OrdinalIgnoreCase))
            {
                return new TenantWidePrincipal(
                    AllWindowsUsers,
                    "All users (Windows)",
                    "Every authenticated Windows account, from a hybrid configuration.",
                    IncludesExternal: false);
            }
        }

        return null;
    }
}

/// <param name="Code">
/// Stable and language independent, so the front end can style a row without
/// matching on text and an export can be filtered without knowing the tenant's
/// language.
/// </param>
/// <param name="Label">
/// What the tool calls this principal. Deliberately the tool's own English
/// label rather than the tenant's localised one; the localised name is carried
/// separately as the principal name, so a report is legible to a reader who
/// does not speak the tenant's language.
/// </param>
/// <param name="IncludesExternal">
/// Whether guests are inside the grant. This is the difference between "the
/// whole company" and "the whole company plus anybody who was ever invited",
/// and it changes the remediation urgency.
/// </param>
public sealed record TenantWidePrincipal(
    string Code,
    string Label,
    string Description,
    bool IncludesExternal);
