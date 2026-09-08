# Contributing

## The rule that gets pull requests rejected

**The application never writes to SharePoint.** No `POST`, `PATCH`, `PUT` or
`DELETE` against any SharePoint or Microsoft Graph site endpoint. This is
enforced in `api/src/PermissionInsight.Api/Downstream/ReadOnlyHttpClient.cs`,
which is the only place an outbound request can be built, and by
`HttpMethodPolicy`, which holds the allow-lists. A pull request that widens the
**downstream** list, or that reaches SharePoint by some other route, will be
rejected.

`HttpMethodPolicy` holds two lists and they answer different questions. The
downstream list — `GET` and `HEAD` — governs what may carry the application
identity, which can read every site in the tenant. The inbound list also admits
`POST`, for actions that record something in the audit log; a `POST` there
reaches a handler in this process and nothing else. Nothing forwards a method
between the two, because handlers call typed client methods rather than a
generic proxy. See [`docs/adr/0008`](docs/adr/0008-inbound-and-downstream-methods-are-separate.md).

The design allows one exception — writing an export file to the signed-in
user's own OneDrive, using a delegated token from the browser and never the
application identity — and that exception is currently deferred to
`docs/backlog.md`. So today the rule has no exceptions at all: the application
holds no write permission anywhere.

**Leave the Content-Security-Policy meta tag in `web/index.html`.** It looks like
a stub worth tidying away; it is not. `scripts/deploy.ps1` replaces that tag with
the real policy at upload time, because the policy has to name the API origin and
a static file cannot know it. The deployment **throws** if the tag is missing, so
removing it fails the deploy rather than quietly shipping an unprotected page —
but renaming or reformatting it can break the match. If you change how the front
end is built, check that the tag still survives into `web/dist/index.html`. See
[`docs/adr/0016`](docs/adr/0016-response-headers-and-content-security-policy.md).

Adding a new outbound destination — an analytics script, a font CDN, a second API
— means adding it to the policy in `New-ContentSecurityPolicy` as well, or the
browser will block it in production and nowhere else.

Two more that are settled, not open:

- No client secret, no Key Vault. See `docs/01-architecture.md`.
- No new Entra permission without a change to `docs/02-permissions.md`
  explaining what feature needs it and what was considered instead.
  `GroupMember.Read.All` and `Sites.FullControl.All` are absent deliberately,
  and the reasoning is written down so it is not rediscovered as a wall to work
  around.

## Prerequisites

- .NET SDK 9
- Node.js 20 or later
- Azure CLI, with the Bicep CLI (`az bicep install`)
- Azure Functions Core Tools v4
- A non-production Microsoft 365 tenant you can deploy into

## Running locally

You need a deployed app registration to sign in against; there is no offline
mode, because every screen is a view over live SharePoint data. Deploy into a
test tenant first:

```powershell
./scripts/deploy.ps1 -ResourceGroup rg-permission-insight -Location westeurope `
    -ApiIdentifierUri api://yourtenant.onmicrosoft.com/permission-insight `
    -SharePointRootUrl https://yourtenant.sharepoint.com
```

The script prints the values the front end needs. Then:

```powershell
# Backend
cp api/src/PermissionInsight.Api/local.settings.example.json `
   api/src/PermissionInsight.Api/local.settings.json
# fill in TenantId, ApiClientId, ApiIdentifierUri
cd api/src/PermissionInsight.Api && func start

# Front end
mkdir -p web/public && cp web/config.example.json web/public/config.json
# fill in the same values plus apiBaseUrl http://localhost:7071/api
cd web && npm install && npm run dev
```

Locally the backend acquires its downstream token through `AzureCliCredential`
instead of the managed identity, so run `az login` as an account that holds the
same application permissions, or accept that site search returns 403. This
local-only fallback is gated on `AZURE_FUNCTIONS_ENVIRONMENT=Development` and
must never be reachable in a deployed app; see the comment in
`DownstreamTokenProvider`.

## Test data

You cannot meaningfully test against an empty site. `docs/10-test-data.md`
describes the site to provision and `scripts/provision-test-data.ps1` creates
it. Run it well before you need it — filling a 12,000 item library takes 25 to
70 minutes. The script is resumable.

## Tests

```powershell
dotnet test api/PermissionInsight.sln
cd web && npm run build
```

The role-claim middleware is the access control model, so it has tests before
anything else does. `api/tests/PermissionInsight.Api.Tests/RequestGateTests.cs`
covers it. Changes to authorisation need a test that fails without the change.

## What must never be committed

Tenant ids, subscription ids, object ids, application ids, site URLs, list
names, user names, email addresses, any exported report, and credentials of any
kind. Every Bicep parameter that could carry tenant-specific data has no
default value, so a deployment fails rather than silently targeting whatever the
author was testing against. Keep it that way.

## Decisions

`docs/adr/` records decisions that were made and, more usefully, decisions that
were reversed. If you are about to reopen a settled question, check there first
— the answer is probably written down along with what it cost.
