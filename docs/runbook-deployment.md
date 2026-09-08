# Deployment runbook

## Who has to run this

The deploying account needs **Privileged Role Administrator** or **Global
Administrator** in the tenant, plus rights over the resource group.

That is not an exaggeration of the requirement and not something to hand to an
ordinary pipeline identity. Granting an application permission means creating
an app role assignment on the Microsoft Graph service principal, which only
those roles can do. The assignments in question give the backend read access to
every SharePoint site in the tenant.

Only the second of the two deployments needs that privilege. Everything else —
redeploying the API, changing the front end, adjusting scaling — can be done by
an account with resource group rights alone.

## Before you start

- Azure CLI with Bicep (`az bicep install`) and Azure Functions Core Tools v4.
- Node.js 20 or later and the .NET SDK 9.
- An identifier URI under a domain the tenant has already verified.
  `api://{tenant}.onmicrosoft.com/permission-insight` works in every tenant
  without preparation. It cannot be derived automatically; see
  [`adr/0003`](adr/0003-api-identifier-uri-is-a-parameter.md).
- The SharePoint root URL, for example `https://contoso.sharepoint.com`.

## Deploy

```powershell
az login
az account set --subscription <subscription>

./scripts/deploy.ps1 `
    -ResourceGroup rg-permission-insight `
    -Location westeurope `
    -ApiIdentifierUri api://contoso.onmicrosoft.com/permission-insight `
    -SharePointRootUrl https://contoso.sharepoint.com
```

The script deploys the core template, enables the static website, attempts the
privileged grants, then builds and uploads the front end and publishes the
backend. It prints the site URL at the end.

Before uploading, it writes the front end's content security policy into
`index.html` and prints it. The policy has to name the API origin, which is only
known once the template has deployed, and the static website endpoint cannot
send response headers — see
[`adr/0016`](adr/0016-response-headers-and-content-security-policy.md). **Sign in
once after any deployment that changes it**: CSP is enforced by the browser, so
nothing in the deployment proves it works.

### If the privileged grants are refused

The script does not stop. It finishes the deployment, prints the Microsoft
Graph PowerShell an administrator has to run — with the object ids already
filled in — and exits non-zero.

Until those grants exist, the application signs in correctly and every search
returns 502. That is the expected symptom, and `/api/sites` says so in its
error message rather than returning an empty result set that would look like a
working tool with nothing to find.

### About the consent URL

The script also prints:

```
https://login.microsoftonline.com/{tenantId}/adminconsent?client_id={clientId}
```

This is optional and covers only this application's own `access_as_user` scope,
so that users do not meet a consent prompt the first time they sign in. The
application requests no permission on any other resource — OneDrive export and
its delegated `Files.ReadWrite` scope are deferred; see
[`backlog.md`](backlog.md).

**It does not grant the managed identity anything.** A managed identity has no
consent screen; its permissions exist only as app role assignments. See
[`adr/0004`](adr/0004-app-role-grants-are-a-second-deployment.md).

## Onboarding a site — not needed

> **Skip this section.** It documents a design that was superseded within a
> day. The tool reads permission data through Microsoft Graph, which accepts
> the read-only `Sites.Read.All` it already holds, so **no site grant is
> required and no administrator has to onboard anything.** See
> [`adr/0011`](adr/0011-permissions-are-read-through-graph.md).
>
> `scripts/grant-site.ps1` and the `-GrantSites` option remain for anyone who
> needs SharePoint's own permission endpoints for another reason. Nothing in
> the product uses them.
>
> The `Sites.Selected` permission this section depended on has since been
> **removed from the managed identity and from `app-roles.bicep`**, so these
> commands would need it granted again before they would do anything. See
> [`setup-guide.md`](setup-guide.md).

The tool can search and enumerate every site in the tenant with read-only
access, but ~~it cannot read *permission* data — site groups, role assignments,
effective permissions — until an administrator grants it that site~~. SharePoint
offers no read-only permission that covers those reads, so this is how the
tool avoids holding write access across the whole tenant. See
[`adr/0010`](adr/0010-hybrid-tenant-read-plus-site-grants.md).

Grant a site:

```powershell
./scripts/grant-site.ps1 `
    -SiteUrl https://contoso.sharepoint.com/sites/finance `
    -AppId <backendIdentityClientId, printed by deploy.ps1>
```

The same script takes several sites at once, `-List` to show what each site
already grants, and `-Revoke` to take access away. It needs only the
Microsoft.Graph module — `Connect-MgGraph` requests the delegated
`Sites.FullControl.All` scope interactively, so no app registration is
involved and the script can do no more than the person running it.

Sites can also be onboarded as part of a deployment:

```powershell
./scripts/deploy.ps1 ... -GrantSites https://contoso.sharepoint.com/sites/finance
```

### Treat a grant as an audit window, not a standing right

A grant does not expire. The recommended pattern is to open one for the audit
and close it afterwards:

```powershell
# 1. Open the window
./scripts/grant-site.ps1 -SiteUrl <site> -AppId <backend app id>

# 2. Scan the site in Permission Insight. Results are stored in the analyst's
#    browser, so they survive step 3.

# 3. Close it
./scripts/grant-site.ps1 -Revoke -SiteUrl <site> -AppId <backend app id>
```

Revoking loses nothing already collected: scan results live in the browser, not
on the server, and the detail panel needs no network call.

This matters more than it looks, because **there is no API that lists every
site an application has been granted**. `-List` answers for sites you name, but
nothing enumerates them tenant-wide. The only reliable way to know what is
outstanding is to keep the standing set empty and grant per audit.

Reading permissions requires the `FullControl` level on the granted site —
`Manage` was tested and is not sufficient, because SharePoint puts
`EnumeratePermissions` in Full Control alone. So a site that is onboarded and
left that way is a site the tool can write to indefinitely. Closing the window
is the control that keeps that from accumulating.

Until a site is granted, the application says so on screen and prints this
command. **It cannot grant itself access, deliberately** — if it could, a
per-site grant would be worth nothing.

## Afterwards

1. **Add people to `sg-permission-insight-users`.** Nobody can sign in until
   they are in it, because `appRoleAssignmentRequired` is set on the service
   principal. Treat membership as a privileged assignment: it is equivalent to
   read access over every SharePoint site in the tenant.

2. **Check the audit log is arriving.** In Application Insights:

   ```kusto
   traces
   | where message startswith "Audit"
   | project timestamp, message, customDimensions.AuditAction,
             customDimensions.ActorObjectId, customDimensions.AuditTarget
   | order by timestamp desc
   ```

   If nothing appears, the audit trail is not being kept and there is no other
   record of what was accessed. That is a stop-and-fix, not a note for later.

3. **Confirm application permissions are actually in effect.** Sign in as
   someone with no SharePoint access to a particular site and search for it. It
   must appear. If it does not, the backend is using a delegated token
   somewhere and the audit will have silent holes.

4. **Confirm no second credential path exists.** Remove the identity assignment
   from the function app and confirm the API fails. Put it back.

## Verifying the deployment holds no secret

```powershell
az functionapp config appsettings list --name <app> --resource-group <rg> `
    --query "[].name" --output tsv
az keyvault list --resource-group <rg> --output tsv
```

There should be no Key Vault, and no setting whose name or value looks like a
credential. `AzureWebJobsStorage` is identity-based —
`AzureWebJobsStorage__accountName` plus `__credential` and `__clientId`, and no
connection string.

## Redeploying

Rerunning `deploy.ps1` is safe and idempotent. The app role grants are already
in place after the first successful run, so subsequent runs by a non-privileged
account will report them as refused; use `-SkipAppRoles` to avoid the noise, or
`-SkipPublish` when only the infrastructure changed.

## Costs

Flex Consumption for the function app, Standard_LRS storage, and a Log
Analytics workspace. For a governance team of a handful of people the workspace
dominates, and its retention is a parameter (`auditRetentionDays`, 365 by
default) because it holds the audit trail. Shortening it is a governance
decision rather than a cost one.
