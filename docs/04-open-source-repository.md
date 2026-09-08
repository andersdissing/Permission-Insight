# 04 — Open source repository

The project is developed in public on GitHub. This is a requirement, not a
nice-to-have, and it shapes several things elsewhere in the specification.

## Why it matters here specifically

Anyone who deploys this tool grants an identity tenant-wide read access to every
SharePoint site in their organisation. A closed-source tool asking for that is
something a security reviewer has to take on trust. An open one can be read.

That cuts both ways: the role check in the backend is the only thing standing
between a signed-in employee and tenant-wide read, and once the code is public,
a flaw in it is public too. That is a good trade, but it raises the bar on the
things below rather than lowering it.

## Repository

- Public, under the organisation's GitHub account.
- MIT licence. Simple, and there is no patent surface worth an Apache grant.
- Name and description state plainly what it does and what it requires.

## README, first screen

Before installation instructions, the README states the permission model: the
tool grants an Azure managed identity read access to all SharePoint content in
the tenant, and access to the tool itself is controlled by an Entra security
group. A reader must not have to reach a subheading to learn this.

Also on the first screen: the tool never writes to SharePoint, and all scan
results stay in the user's browser.

## What must never be committed

- Tenant IDs, subscription IDs, object IDs, application IDs.
- Site URLs, list names, user names or email addresses from a real tenant. The
  sample data in `10-test-data.md` uses neutral placeholder names for this
  reason.
- Any exported report. Add `*.csv`, `*.xlsx` and any output directory to
  `.gitignore` on day one, before the first export feature exists. An export is
  a complete index of an organisation's most exposed documents and is the single
  worst thing that could end up in a public repository.
- Credentials of any kind. The architecture has none, so a secret appearing in
  the repository is by definition a mistake.

Enable GitHub secret scanning and push protection. The point is not that secrets
are expected, but that their absence should be enforced rather than assumed.

Every Bicep parameter that could carry tenant-specific data has no default
value, so a deployment fails rather than silently targeting whatever the author
was testing against.

## SECURITY.md

Required. A tool of this shape needs a disclosure route that is not a public
issue.

- How to report a vulnerability privately. Enable GitHub private vulnerability
  reporting.
- Expected response time.
- An explicit statement of the threat model: the tool assumes the deploying
  organisation controls its Entra tenant, and treats any bypass of the
  `PermissionInsight.Use` role check as a critical vulnerability.

## CONTRIBUTING.md

- How to run locally and against a test tenant.
- A pointer to `10-test-data.md`, since a contributor cannot meaningfully test
  without a populated site.
- The read-only rule from `CLAUDE.md`, stated as a contribution rule: pull
  requests that introduce a write path to SharePoint will be rejected.

## Continuous integration

GitHub Actions, authenticating to Azure with a federated identity credential.
No secrets in CI either, for the same reason there are none in the application.

On every pull request:

- Build and lint the SPA and the function app.
- Run unit tests, including tests of the role-claim middleware. That middleware
  is the access control model, so it gets tests before anything else does.
- `az deployment group what-if` against a test subscription, so a Bicep change
  that would alter permissions is visible in the pull request diff rather than
  discovered at deployment.

Branch protection on `main`: no direct pushes, at least one review, CI must
pass.

Enable Dependabot for the SPA dependencies, the function app dependencies and
the Actions workflows.

## Documentation in the repository

These specification documents live in `docs/` in the repository. The reasoning
matters as much as the code for anyone evaluating a fork — in particular
`02-permissions.md`, which explains why `GroupMember.Read.All` and
`Sites.FullControl.All` are deliberately absent. Without that, the first
contributor who hits a wall will simply add the permission.

Keep an `ADR` or decisions section recording reversed decisions and why, so
settled questions are not relitigated in issues.

## Releases

Tag releases and publish the Bicep as a release artefact, so a deploying
organisation pins a version rather than deploying whatever is on `main`.
