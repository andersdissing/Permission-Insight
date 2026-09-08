# Phase 1 — Deployment, sign-in and site search

The milestone that proves the hard parts. When this phase passes, the
infrastructure deploys, Entra gates access correctly, the backend holds an
application token, and SharePoint answers. Every later phase is a screen on top
of machinery that already works.

Build nothing from phases 2 to 5 here. Resist adding the tabs.

Read `01-architecture.md` and `02-permissions.md` first.

## 1. Infrastructure

Bicep, one deployment, both Azure and Entra resources.

**Azure**

- Static hosting for the SPA: storage account with static website enabled, or
  Static Web App. See the open decision in `01-architecture.md`.
- Function App for the backend API.
- User-assigned managed identity, assigned to the Function App.
- Application Insights, so the audit log has somewhere to go.

No Key Vault and no client secret. If a task in this phase seems to need one,
that is a signal something has been misunderstood — reread the Credentials
section of `01-architecture.md`.

**Entra, via the Microsoft Graph Bicep extension**

- Application registration with:
  - SPA redirect URI for the static site origin.
  - An app role `PermissionInsight.Use`, member type `User`.
  - ~~Delegated permission `Files.ReadWrite`, not consented at this stage.~~
    Removed with OneDrive export; see [`adr/0005`](adr/0005-onedrive-export-deferred.md).
    The registration now requests nothing on any other resource.
  - No application permissions. They belong to the managed identity.
- Service principal with `appRoleAssignmentRequired` set to `true`.
- Security group `sg-permission-insight-users`.
- App role assignment binding the group to `PermissionInsight.Use`.
- App role assignments granting the **managed identity's** service principal
  `Sites.Read.All` and `User.ReadBasic.All` on Microsoft Graph, and
  `Sites.Read.All` on the SharePoint resource.

Configure the extension with dynamic types in `bicepconfig.json`. Check the
current version in the Microsoft Artifact Registry rather than copying a version
string from documentation.

**Admin consent.** Attempt it in Bicep with `Microsoft.Graph/appRoleAssignedTo`
against the Microsoft Graph service principal. If the deploying identity lacks
the rights, the deployment must emit the consent URL as an output rather than
failing quietly:

```
https://login.microsoftonline.com/{tenantId}/adminconsent?client_id={clientId}
```

The deployment script prints it with one line of instruction. Document in the
runbook that the deploying identity needs Privileged Role Administrator or
Global Administrator, and is therefore not an ordinary pipeline identity.

## 2. Backend API

One function app. Two endpoints in this phase.

### `GET /api/me`

Returns the signed-in user's display name, mail and object id, taken from the
validated token. No Graph call. Used by the top bar and to confirm end to end
that token validation works.

### `GET /api/sites?q={query}`

Searches sites by title and URL across the tenant and returns at most 20
results, each `{ id, title, url, webUrl }`.

Use Microsoft Graph `/sites?search={query}`, which matches on title and URL.
`VERIFY` the result shape and whether URL fragments match as expected; if
searching by URL path proves unreliable, fall back to SharePoint search with
`contentclass:STS_Site` and note the change here.

Reject queries shorter than two characters with 400.

### Shared middleware, applied to every route

1. Validate the bearer token: signature, issuer, audience, expiry.
2. Require `PermissionInsight.Use` in the `roles` claim. Reject with 403 and a
   body that names the missing role, so a misconfigured assignment is diagnosable
   without reading logs.
3. Write an audit entry: user object id, timestamp, route, query parameters.
4. For proxy routes, validate the HTTP method against a read-only allow-list
   before forwarding.

Point 2 is the whole access control model. It must not be possible to register a
handler that bypasses it — enforce by construction, for example a single router
that wraps every handler, rather than by remembering to add a decorator.

## 3. Frontend

### Sign-in

Automatic on load. No sign-in button, no anonymous state. Use MSAL popup flow
for the reasons in `01-architecture.md`.

If sign-in succeeds but the token lacks the app role, show a full-page message
naming the group a user needs to join and who to ask. Do not show an empty
application.

### Top bar

Present on every screen. Left: a lock icon and the product name "Permission
insight". Right, in order: the "Save scans" label with its toggle, the
information icon, the clear button, a hairline divider, the user avatar with
initials, and a sign-out icon button.

In this phase the toggle and clear button are rendered but inert. They are wired
up in phase 3, when there is something to save. Build them now so the layout is
settled.

### Site picker

The only screen in this phase.

- Heading "Select a site". Sub-line: "Search for the site you want to check for
  broken inheritance and sharing links."
- A search input with a leading magnifier and a clear affordance. Debounce 300ms,
  minimum two characters.
- Below the input, a "Recently used" row of up to three chips read from
  IndexedDB. Chips hold only title and URL. Clicking one selects that site.
- Results: a bordered list, one row per site, showing title on the first line
  and the server-relative URL in a monospace muted second line. The whole row is
  the click target, with a chevron at the trailing edge. No buttons per row.
- Above the list, a count: "3 results".
- Below the list, a persistent line: "Searching across all sites in your
  organization."

Do not show item counts here. Library and item sizes belong to phase 3, where
they inform a decision.

**Empty search state.** With no query, the results area shows the recently used
sites rather than nothing. A first-time user with no history gets an empty state
inviting them to search.

**Result ceiling.** Cap at 20 rows. Beyond that, show "More than 20 sites match.
Refine your search." Pagination is not worth building for an audience that
already knows which site it wants.

**No results.** "No site matches that search." Do not suggest the user lacks
access — with application permissions, a missing result means the site does not
exist or the query is wrong.

### Selecting a site

Store `{siteId, title, url}` at the front of `recentSites`, cap at ten, and
route to the site screen. In this phase the site screen is a placeholder showing
the selected site in the framed search box. The tabs arrive in phase 2.

## Acceptance

1. `az deployment` completes from a clean subscription and produces the static
   site URL, the API URL and, if consent was not granted automatically, the
   consent URL.
2. A user in `sg-permission-insight-users` opens the site URL and is signed in
   without clicking anything.
3. A user **not** in the group is refused by Entra at sign-in, not by the
   application after sign-in. Verify that `appRoleAssignmentRequired` is doing
   the work by checking the Entra sign-in log.
4. Calling `/api/sites` with a valid token that lacks the role returns 403.
   Calling it with no token returns 401. Test both with a raw HTTP client, not
   through the SPA.
5. Searching a partial site title returns matching sites. Searching a fragment
   of a site URL returns the same site.
6. A site that the signed-in user has **no** SharePoint access to still appears
   in results. This is the test that proves application permissions are in
   effect rather than delegated ones, and it is the single most important
   assertion in this phase.
7. Any write method against the proxy is rejected before it reaches SharePoint.
8. The audit log contains one entry per search with user, timestamp and query.
9. Recently used chips survive a page reload.
10. The deployed resource group contains no Key Vault and the function app
    settings contain no secret. Grep the whole repository and the deployed
    configuration for `clientSecret` and find nothing.
11. The backend obtains its Graph token from the managed identity endpoint. Kill
    the function app's identity assignment and confirm the API fails, proving no
    second credential path exists.

## Verify before phase 3

`scripts/probe-verify.ps1` runs these checks and prints the answers. Run it
**app-only** with `-ClientId`, `-Tenant` and `-CertificateThumbprint`: a
delegated connection tells you what each endpoint returns but not whether
app-only `Sites.Read.All` is sufficient, which is the question that decides the
permission model.

Record the answers here:

- Does `Sites.Read.All` permit reading
  `/_api/web/lists(guid'...')/items(N)/roleassignments`?
- Does it permit `/_api/web/getusereffectivepermissions(@u)`?

~~If either fails, phase 3 and phase 5 need `Sites.FullControl.All`, which
requires renewed security approval.~~ Finding this out now is much cheaper than
finding it out in phase 5.

> **No escalation.** `Sites.FullControl.All` is ruled out. If either check
> fails, phase 3 reports that inheritance is broken without saying who has
> access, and phase 5 does not work as designed. See
> [`adr/0009`](adr/0009-no-full-control-escalation.md).
