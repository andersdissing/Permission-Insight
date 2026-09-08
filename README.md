# Permission Insight

Finds broken permission inheritance and sharing links in SharePoint Online, so
a governance team can see where an organisation is oversharing and go and fix
it.

## Read this before you deploy it

**This tool grants an Azure managed identity read access to all SharePoint
content in your tenant.** It holds `Sites.Read.All`, `User.ReadBasic.All` and
`GroupMember.Read.All` — all read-only, and none of them able to write
anything, anywhere. `Sites.Read.All` cannot be narrowed without losing the
ability to search across sites. Access to the tool itself is controlled by an
Entra security group whose members hold the `PermissionInsight.Use` app role —
that group membership is the real access control, so treat it as a privileged
assignment.

**The tool never writes to SharePoint.** Only `GET` and `HEAD` reach SharePoint
or Graph, and the allow-list that enforces it is a single file in the backend
rather than a convention. As it stands the tool holds no write permission at
all, anywhere: exports are browser downloads, so nothing is written outside
your own machine.

**All scan results stay in the user's browser.** There is no database and no
scan history. The backend is a token holder and a read-only proxy; it keeps no
record of what was found, only an audit log of who looked at what.

There is no client secret, no Key Vault and no connection string holding a key.
The backend authenticates as a user-assigned managed identity, so there is no
credential material outside Azure to leak, rotate or commit.

## What it shows you

- Every sharing link in a site collection, resolved to a path, with scope,
  recipients, expiry, and which ones point at deleted files.
- Every item in a library whose permissions have stopped inheriting, separated
  into sharing links and direct grants, because a direct grant never expires
  and is rarely documented.
- A folder tree pruned to just the findings and the folders you need to walk
  through to reach them.
- Which items name a given person or Entra group, several at a time, so a
  group chain can be followed. This reports direct naming rather than computed
  effective access — see [`docs/adr/0012`](docs/adr/0012-group-membership-is-walked-not-resolved.md)
  for what that does and does not tell you.
- CSV export of any of the above, as a download.

Remediation happens in SharePoint's own interface, which the tool links to.

## Shape

```
Browser (SPA)                 Static website in an Azure storage account
  │  MSAL sign-in, bearer token on every call
  ▼
Backend API                   Azure Functions, .NET 9 isolated
  │  Validates the token and the app role claim on every request
  │  Acquires an app-only token from its user-assigned managed identity
  │  Forwards read-only requests, rejects everything else
  ▼
Microsoft Graph + SharePoint REST
```

Scan orchestration, progress and all caching happen in the browser. The backend
does not aggregate, store or transform.

## Install

Prerequisites: Azure CLI with Bicep, an Azure subscription, and an account with
**Privileged Role Administrator** or **Global Administrator** in the tenant.
Granting an application permission means creating an app role assignment on the
Microsoft Graph service principal, which an ordinary pipeline identity cannot
do.

```powershell
git clone https://github.com/<org>/permission-insight
cd permission-insight
./scripts/deploy.ps1 `
    -ResourceGroup rg-permission-insight `
    -Location westeurope `
    -ApiIdentifierUri api://yourtenant.onmicrosoft.com/permission-insight `
    -SharePointRootUrl https://yourtenant.sharepoint.com
```

The script deploys the Azure and Entra resources, enables the static website,
publishes the backend and the front end, and prints the site URL. If it could
not create the application role assignments because the signed-in account is
not privileged enough, it prints exactly what an administrator has to run
instead rather than failing quietly.

Full steps, including what to check afterwards, are in
[`docs/runbook-deployment.md`](docs/runbook-deployment.md).

## Documentation

The reasoning matters as much as the code if you are evaluating this for your
own tenant.

| Document | What it covers |
| --- | --- |
| [`docs/01-architecture.md`](docs/01-architecture.md) | Hosting, runtime shape, why there is a backend and why it holds no secret |
| [`docs/02-permissions.md`](docs/02-permissions.md) | Every Entra permission, why it exists, and which ones were deliberately not requested |
| [`docs/03-client-storage.md`](docs/03-client-storage.md) | What is cached in the browser, what that exposes, and how it is cleared |
| [`docs/04-open-source-repository.md`](docs/04-open-source-repository.md) | Repository rules, CI, what must never be committed |
| [`docs/adr/`](docs/adr) | Decisions, including reversed ones and what they cost |
| [`docs/10-test-data.md`](docs/10-test-data.md) | The test site you need before you can meaningfully test anything |

Read `02-permissions.md` before opening an issue asking why the tool does not
just expand the group. The answer is there.

## Status

All five phases are built: infrastructure, sign-in and the app role gate, site
search, the sharing links tab, the lists and libraries tab with the
broken-inheritance scan, the folder tree and detail panel, and search for
person. CSV export works everywhere.

Not built, and deliberately so: exporting to OneDrive, which is deferred with
the rest of [`docs/backlog.md`](docs/backlog.md).

Nothing has been exercised against a large real tenant yet. Several endpoints
this depends on are marked `VERIFY` in the code and answered by
`scripts/probe-verify.ps1`; run it against a test tenant before trusting a
report.

Deferred rather than pending: exporting to OneDrive, which is in
[`docs/backlog.md`](docs/backlog.md) along with everything else deliberately
left out of v1.

Phase 2 has not been run against a real tenant. Two things it depends on are
marked `VERIFY` in the code and answered by `scripts/probe-verify.ps1`: that
the GUID in a sharing link group's name resolves through `GetFileById`, and how
a group is matched to its Graph permission object. Run that probe against a
test tenant before trusting a report.

## Contributing and security

[`CONTRIBUTING.md`](CONTRIBUTING.md) for how to run it against a test tenant.
[`SECURITY.md`](SECURITY.md) for the threat model and how to report a
vulnerability privately. Please do not report vulnerabilities in a public
issue.

MIT licensed.
