using System.Net;

namespace PermissionInsight.Api.Downstream;

/// <summary>
/// Turns SharePoint's refusal of a read into something a person can act on.
///
/// <para><b>Narrower than it used to be.</b> This was written when the tool
/// read permissions through SharePoint REST, which app-only
/// <c>Sites.Read.All</c> cannot do, and every refusal genuinely meant the site
/// needed a grant. adr/0011 moved permission reads to Microsoft Graph, which
/// answers them on the permission the application already holds — so a refusal
/// now means one specific SharePoint endpoint said no, not that the tool is
/// locked out of the site.</para>
///
/// <para>The message must therefore not tell anyone to grant Full Control.
/// That contradicts the rule in CLAUDE.md, and it contradicts the screen next
/// to it: the lists and libraries tab reads the same site's permissions
/// through Graph without any grant at all. The only read left that needs more
/// is SharePoint group membership — see adr/0013.</para>
/// </summary>
public static class SiteOnboarding
{
    public const string ErrorCode = "site_not_onboarded";

    /// <summary>
    /// Whether a downstream failure looks like a missing site grant rather
    /// than something broken.
    /// </summary>
    public static bool LooksLikeMissingGrant(DownstreamException failure) =>
        failure.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    public static string Message(string siteTitle) =>
        $"SharePoint refused one read on {siteTitle}, so part of this row is missing. " +
        "Permissions elsewhere in the tool are read through Microsoft Graph and are unaffected — " +
        "the lists and libraries tab still works on this site. " +
        "Reading the membership of a SharePoint group is the one thing that needs rights beyond read-only, " +
        "and it's only needed to show who still holds a link whose file has been deleted.";

    /// <summary>
    /// The exact command an administrator runs. Filled in with the real
    /// identity and site so it can be pasted rather than assembled, because a
    /// half-remembered command is how the wrong app gets granted access.
    /// </summary>
    public static string GrantCommand(string siteWebUrl, string managedIdentityClientId) =>
        $"./scripts/grant-site.ps1 -SiteUrl {siteWebUrl} -AppId {managedIdentityClientId}";

    public static Dictionary<string, object?> Details(
        string siteWebUrl,
        string managedIdentityClientId,
        HttpStatusCode downstreamStatus) => new()
    {
        ["downstreamStatus"] = (int)downstreamStatus,
        ["siteWebUrl"] = siteWebUrl,
        ["grantCommand"] = GrantCommand(siteWebUrl, managedIdentityClientId),
    };
}
