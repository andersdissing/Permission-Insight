# Setup guide

How to install Permission Insight, who has to do it, and how to see what it did.

- [Prerequisites](#prerequisites)
- [Install](#install)
- [Verify the install](#verify-the-install)
- [Permission, access and auditability](#permission-access-and-auditability)
- [Uninstall](#uninstall)

---

## Prerequisites

### Azure subscription

- One Azure subscription you can create resources in.
- One resource group, or rights to create one. Everything lands in it.
- A region. Anything that offers **Flex Consumption** Functions; `westeurope`
  and `eastus` both do.
- No Key Vault, no secret store, no database. The design has none of them, and
  nothing here will ask you for a connection string.

**Rough cost:** a Flex Consumption plan billed per execution, one storage
account, and Log Analytics ingestion. For a governance team of a handful of
people this is small — the audit log is the only thing that grows, and it is one
row per request with a 365-day retention you can lower in `main.bicep`.

### Permissions you need to install it

Two different levels, and only one of them is privileged:

- **Resource group Contributor** — for everything except the app role grants.
  Enough to redeploy the API, the front end, and to change scaling.
- **Privileged Role Administrator** or **Global Administrator** in the Entra
  tenant — for `infra/app-roles.bicep` only.

That second one is not negotiable and not something to hand to a pipeline
identity. Granting an application permission means creating an app role
assignment on the Microsoft Graph service principal, and only those directory
roles can do it. See [`adr/0004`](adr/0004-app-role-grants-are-a-second-deployment.md).

If you are not privileged enough, the deployment **does not fail silently**: it
finishes everything else, prints the exact commands an administrator has to run
with the object ids already filled in, and exits non-zero.

### Tools to install

| Tool | Version | Why |
| --- | --- | --- |
| [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) | current | Deploys the templates, uploads the front end |
| Bicep | current | `az bicep install` |
| [.NET SDK](https://dotnet.microsoft.com/download) | 9 | Builds the backend |
| [Azure Functions Core Tools](https://learn.microsoft.com/azure/azure-functions/functions-run-local) | v4 | Publishes the backend |
| [Node.js](https://nodejs.org) | 20 or later | Builds the front end |
| PowerShell | 7 or later | `scripts/*.ps1` are PowerShell 7 |
| [PnP.PowerShell](https://pnp.github.io/powershell/) | current | **Test tenants only** — provisioning test data |

Check them all at once:

```powershell
az version; az bicep version; dotnet --version; func --version; node --version; $PSVersionTable.PSVersion
```

### Two values you must decide before starting

- **An identifier URI** for the app registration, under a domain the tenant has
  already verified. `api://{tenant}.onmicrosoft.com/permission-insight` works in
  every tenant with no preparation. It cannot be derived automatically — see
  [`adr/0003`](adr/0003-api-identifier-uri-is-a-parameter.md).
- **The SharePoint root URL**, for example `https://contoso.sharepoint.com`.

---

## Install

```powershell
az login
az account set --subscription <subscription>

./scripts/deploy.ps1 `
    -ResourceGroup rg-permission-insight `
    -Location westeurope `
    -ApiIdentifierUri api://contoso.onmicrosoft.com/permission-insight `
    -SharePointRootUrl https://contoso.sharepoint.com
```

That one command runs five steps in order:

- **Azure resources and Entra objects** — `infra/main.bicep`.
- **Static website hosting** — a data-plane setting ARM cannot express, so the
  script makes the call itself.
- **App role grants** — `infra/app-roles.bicep`. The privileged step.
- **Front end** — built, configured from the deployment outputs, uploaded.
- **Backend** — published to the function app.

It prints the site URL at the end. Useful switches:

- `-SkipAppRoles` — skip the privileged step when you already know you cannot do
  it, so you get the instructions without a failed attempt first.
- `-SkipPublish` — infrastructure only, for iterating on Bicep.
- `-NamePrefix` — defaults to `perminsight`.
- `-UsersGroupDisplayName` — defaults to `sg-permission-insight-users`.

### If the app role grants are refused

- The deployment is still usable; only permission grants are missing.
- Sign-in works and **every search returns 502**. That is the expected symptom.
- `/api/sites` says so in its error body rather than returning an empty result,
  which would look like a working tool that found nothing.
- Hand the printed commands to an administrator, then re-run `deploy.ps1`.

### The consent URL it prints

- Optional, and covers only this application's own `access_as_user` scope so
  users meet no consent prompt on first sign-in.
- **It grants the managed identity nothing.** A managed identity has no consent
  screen; its permissions exist only as app role assignments.

---

## Verify the install

- Open the site URL. You are redirected to sign in immediately — there is no
  anonymous state.
- If you are not in the users group you get the access-denied screen naming the
  group to ask for. That is correct behaviour, not a failure.
- Search for a site you know. Results within a second or two.
- Open it. The banner above the tabs states site-level access — green when no
  organisation-wide grant was found, red when there is one.
- Open **Lists and libraries** and scan the default document library.

Failures worth recognising on sight:

- **Every search returns 502** — the app role grants are missing. See above.
- **Sign-in loops, or `timed_out`** — stale MSAL state. The screen offers *Sign
  in again*, which clears it.
- **Every library says "Permissions unknown"** — SharePoint did not return
  `HasUniqueRoleAssignments`. The tool now asks for it separately; if it still
  shows, the second call is being refused.

---

## Permission, access and auditability

### What the application can do

The backend runs as a user-assigned managed identity holding **four application
permissions, all reads**:

| Resource | Permission | For |
| --- | --- | --- |
| Microsoft Graph | `Sites.Read.All` | Sites, lists, items — **and the permission data itself** |
| Microsoft Graph | `User.ReadBasic.All` | The people picker |
| Microsoft Graph | `GroupMember.Read.All` | Expanding an Entra group, one level |
| SharePoint | `Sites.Read.All` | Flat item enumeration, and the site groups collection |

- **No write permission exists anywhere.** Not unused — not granted. "The
  application never writes to SharePoint" is a property of the identity, not a
  promise made by the code.
- **No Full Control**, tenant-wide or per site.
- **No per-site onboarding**, and no administrator in the loop after install.
- `Sites.Selected` was assigned early, granted nothing, and has been removed.

### Why `Sites.Read.All` — the argument to have ready

An administrator asked to approve tenant-wide read is entitled to push back.

**Why it must be tenant-wide:**

- The questions the tool exists to answer are "where in the tenant is something
  exposed" and "what can this person reach". Neither can be answered from a
  subset of sites chosen in advance — a scoped grant would only ever confirm
  what somebody already suspected.
- There is no delegated alternative. Reading as the signed-in user returns only
  what that user can already see, which inverts the tool: the compliance staff
  who most need to audit a site are usually the ones with no access to it.

**Why it does not need to be more** — the part worth recording, because the
project spent a long time believing the opposite:

- SharePoint's own permission endpoints — `roleassignments`,
  `sitegroups({id})/users`, `getusereffectivepermissions` — **refuse an app-only
  `Sites.Read.All` token**. They need `EnumeratePermissions`, which lives in
  Full Control. Verified against a live tenant:
  `403 -2147024891, System.UnauthorizedAccessException`.
- SharePoint offers **no read-only permission that reads permissions**. The
  choice it presents is Full Control or nothing.
- That dead end sent the project down two wrong roads: `Sites.FullControl.All`
  (rejected outright — [`adr/0009`](adr/0009-no-full-control-escalation.md)),
  then per-site `Sites.Selected` grants, which shipped a script before being
  abandoned for putting a named administrator in the loop of every audit
  ([`adr/0010`](adr/0010-hybrid-tenant-read-plus-site-grants.md)).
- Both were unnecessary. **Graph documents `Sites.Read.All` as the
  least-privileged application permission for
  `GET /sites/{id}/lists/{id}/items/{id}/permissions`** — it answers the
  question SharePoint refuses, on the permission already held, and reaches every
  list type rather than document libraries only.
  See [`adr/0011`](adr/0011-permissions-are-read-through-graph.md).

**The one thing still out of reach:** SharePoint group *membership*. The
consequence is narrow and visible — where a sharing link's file has been
deleted, the tool reports the orphaned link but shows *"Members not readable"*
instead of naming who holds it. Stated rather than hidden, because "cannot see"
and "nothing to see" must never look the same on screen.
See [`adr/0013`](adr/0013-sharing-links-derived-from-scans.md).

### How to assign access

Access is a **group membership**. The deployment creates a security group and
assigns it the app role; nobody is added to it automatically, including you.

- Group: `sg-permission-insight-users` (change with `-UsersGroupDisplayName`).
- The app registration has `appRoleAssignmentRequired` on, so Entra refuses
  sign-in to anyone without the role. The backend then re-checks the role on
  **every request** — the front end's route guard is cosmetic.

Add someone:

```powershell
az ad group member add --group sg-permission-insight-users --member-id <user object id>
```

Look up an object id, list who has access, and remove someone:

```powershell
az ad user show --id someone@contoso.com --query id -o tsv
az ad group member list --group sg-permission-insight-users --query "[].{name:displayName, upn:userPrincipalName}" -o table
az ad group member remove --group sg-permission-insight-users --member-id <user object id>
```

Treat that membership as a **privileged assignment**:

- It is equivalent to read access over every SharePoint site in the tenant.
- Removing someone does not revoke an already-issued token. Access ends when the
  token expires, typically under an hour. Continuous Access Evaluation is out of
  scope for v1 and accepted as a known risk.
- Cached scan results live in that person's own browser and survive both
  sign-out and removal from the group. The application says so on screen, and
  the **Clear** button removes them.

### What the application logs, and how to read it

The backend keeps **no database**. The audit log in Application Insights is the
only server-side record of what was accessed.

Logged on every authorised request: **who** (user object id), **when**, and
**what they asked for** (route, plus query values from an allow-list).

Deliberately **not** logged:

- Item paths. A scan issues one request per broken item, so logging them would
  build a browsable index of the organisation's most exposed documents.
- Downstream error message bodies — SharePoint writes list and document names
  into them. Only the error *code* is kept.
- Anything not on the allow-list, including paging tokens. A new parameter is
  redacted until somebody classifies it.

See [`adr/0014`](adr/0014-what-the-audit-log-may-record.md).

**Where to look:** Azure portal → the `perminsight-insights-*` Application
Insights resource → **Logs**. All entries carry the category
`PermissionInsight.Audit`.

Everything a person did:

```kusto
traces
| where customDimensions.CategoryName == "PermissionInsight.Audit"
| where customDimensions.ActorObjectId == "<user object id>"
| project timestamp, action = customDimensions.AuditAction, target = customDimensions.AuditTarget
| order by timestamp desc
```

Exports and person lookups — the two actions the design singles out:

```kusto
traces
| where customDimensions.AuditAction in ("export", "person-lookup")
| project timestamp,
          who = customDimensions.ActorObjectId,
          action = customDimensions.AuditAction,
          target = customDimensions.AuditTarget,
          subject = customDimensions.AuditSubject
| order by timestamp desc
```

Refused requests — a valid token without the role is either a misconfiguration
or somebody probing, and both are worth seeing:

```kusto
traces
| where message startswith "Audit denied"
| project timestamp,
          who = customDimensions.ActorObjectId,
          action = customDimensions.AuditAction,
          reason = customDimensions.DenyReason
| order by timestamp desc
```

Errors, including what SharePoint or Graph refused:

```kusto
traces
| where severityLevel >= 3
| project timestamp, message, code = customDimensions.DownstreamCode
| order by timestamp desc
```

Who is using it at all:

```kusto
traces
| where customDimensions.CategoryName == "PermissionInsight.Audit"
| extend actor = tostring(customDimensions.ActorObjectId)
| summarize requests = count(), lastSeen = max(timestamp) by actor
| order by requests desc
```

`anonymous` appearing here is normal: it is every refused request that never got
as far as a validated identity.

**Treat the workspace as sensitive.** A complete list of the sites somebody
searched for is an organisation chart, and Log Analytics readers are an Azure
RBAC population — wider than, and governed differently from, the users group
that gates the tool. Review who can read it. Retention defaults to 365 days
(`auditRetentionDays` in `main.bicep`).

### Browser protections

The API sends `no-store`, `nosniff`, `X-Frame-Options: DENY` and
`Referrer-Policy: no-referrer` on every response.

The front end cannot: Azure Storage static website hosting does not send custom
response headers at all. So its content security policy is written into
`index.html` as a meta tag, by `deploy.ps1`, at upload time — it has to name the
API origin, which is a per-deployment value. The deployment prints the policy
and **fails if the placeholder tag is missing**, so an unprotected page cannot
ship quietly.

- **After a deployment that changes the policy, do one real sign-in.** CSP is
  enforced by the browser, so nothing server-side proves it works. The rollback
  is to redeploy the previous commit.
- Two protections stay unavailable, because a meta tag cannot carry them:
  `frame-ancestors` and HSTS. Framing is refused in JavaScript instead — the
  application mounts nothing unless it is the top-level page — and the storage
  account refuses HTTP outright, so HSTS covers only the first request on a
  hostile network. Closing both properly needs Azure Front Door in front of the
  storage account. See
  [`adr/0016`](adr/0016-response-headers-and-content-security-policy.md).

### Deployment credentials

- **Basic publishing credentials are disabled** on SCM and FTP, so the function
  app can only be deployed to through Entra ID. There is no publishing profile
  password to leak.
- This is separate from `ftpsState`, which disables only the FTP protocol.
- It does not affect `func azure functionapp publish`: Flex Consumption
  publishes through ARM, not through the publishing profile.
- If a deployment ever fails with an authorisation error from Kudu, check these
  before re-enabling them — re-enabling reintroduces a password that deploys
  code to a backend with tenant-wide read.

### Known gaps, recorded rather than hidden

- **The backend identity holds Storage Blob Data Owner at account scope**, and
  the same account serves the front end — so the backend could in principle
  rewrite the SPA. Scoping the assignment to the deployment containers would
  close it; it needs a test deployment first.
- **No rate limiting.** A role holder can drive the API as hard as they like;
  the practical ceiling is SharePoint throttling, which the tool handles.

---

## Uninstall

In this order:

- **Remove everyone from the group**, so no token can be issued while the rest
  is being taken down.
- **Delete the resource group.** This removes the function app, storage account
  (including the front end and every scan the browser did not cache), Log
  Analytics workspace and the audit log with it — export it first if it has to
  be retained.
- **Delete the Entra objects**, which live outside the resource group and are
  not removed with it: the app registration, the managed identity's app role
  assignments, and the users group.

```powershell
az group delete --name rg-permission-insight --yes
az ad app delete --id <appId printed by deploy.ps1>
az ad group delete --group sg-permission-insight-users
```

Cached scans in each user's browser are not reachable from here. They are
cleared with the **Clear** button in the application, or by clearing site data
for the origin.
