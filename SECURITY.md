# Security policy

## Reporting a vulnerability

Report privately through GitHub's private vulnerability reporting on this
repository: **Security → Report a vulnerability**. Do not open a public issue.

Expected response time: an acknowledgement within three working days, and an
assessment within ten. If a fix is needed we will agree a disclosure date with
you before publishing.

## Threat model

Permission Insight grants an Azure managed identity `Sites.Read.All` across the
whole SharePoint Online tenant. Anyone who can obtain a token for the
application and pass the role check can read permission data for every site in
the organisation, including site titles, folder paths and file names. A
complete list of sites is an organisation chart; a folder path is frequently
more revealing than the document inside it.

The tool assumes:

- The deploying organisation controls its Entra tenant and the Azure
  subscription the resources live in.
- Membership of the `sg-permission-insight-users` group is managed as a
  privileged assignment.
- Anyone with access to a user's browser profile can read that user's cached
  scans. Client storage is origin-scoped and unencrypted by design; see
  `docs/03-client-storage.md`.

### What we treat as critical

- Any bypass of the `PermissionInsight.Use` app role check in the backend. That
  check is the access control model. Everything else is defence in depth.
- Any path that lets a request reach SharePoint or Microsoft Graph with a
  method other than `GET` or `HEAD` while carrying the application identity.
  The application is read-only by construction and must stay that way.
- Any leak of the managed identity's token outside the backend process.
- Any code path that writes an export, a scan result or a site list to a server
  the deploying organisation does not control.

### What we do not treat as a vulnerability

- A user with the app role reading data they would not see in SharePoint
  itself. That is the point of the tool and is documented in
  `docs/02-permissions.md`.
- Cached scans surviving sign-out. This is a documented decision with a stated
  consequence; see `docs/03-client-storage.md`.
- Access continuing for the lifetime of an already-issued token after a user is
  removed from the group. Continuous Access Evaluation is deliberately out of
  scope for v1 and recorded as accepted risk.

## What the audit log holds, and what it does not

The audit trail is the only server-side record the application keeps, and it
lives in Application Insights. That matters because the population who can read
a Log Analytics workspace is an Azure RBAC population — normally the platform
team — and it is wider than, and governed differently from, the
`sg-permission-insight-users` group that gates the tool. Anything written there
has effectively left the app role behind.

So the audit log records **who looked at which site, and when**. It does not
record what was inside. Concretely:

- Query strings are written through an allow-list of parameter names
  (`AuditQuery`): identifiers and search terms are kept, everything else is
  replaced with `[redacted]`. A parameter nobody has classified is redacted, so
  the failure direction is silence rather than disclosure.
- Item paths are never logged. A scan issues one permission read per broken
  item, so logging them would build a browsable index of the organisation's most
  exposed documents — the exact artefact the threat model above calls more
  revealing than the documents themselves.
- Downstream failures record the error *code* and not the message. SharePoint
  writes list and document names into error messages; see `DownstreamException`.
- Caller-supplied free text on the audit endpoint is length-capped, so a role
  holder cannot pad or flood the one record the design depends on.

This is what lets the application tell users that scanned paths are held in
their own browser. See
[`docs/adr/0014`](docs/adr/0014-what-the-audit-log-may-record.md).

## What the browser is told

Every API response carries `Cache-Control: no-store`, `X-Content-Type-Options:
nosniff`, `X-Frame-Options: DENY` and `Referrer-Policy: no-referrer`. `no-store`
matters most: every body is permission data for a named site, and without it
nothing stops a proxy or the back-forward cache holding it after sign-out.

The front end is served from an Azure Storage static website, which cannot send
custom response headers at all. Its content security policy is therefore a meta
tag written by `scripts/deploy.ps1` at upload time, because the policy has to
name the API origin and that is a per-deployment value. `script-src` is exact;
`style-src` allows inline styles, which is what the tree indentation and the
progress bar need and is the price of keeping `script-src` strict.

Two protections cannot be carried by a meta tag and are therefore absent:
`frame-ancestors` and HSTS. Framing is refused in JavaScript instead — the
application mounts nothing unless it is the top-level page — and the storage
account refuses plain HTTP. Reports about either are welcome but already known;
see [`docs/adr/0016`](docs/adr/0016-response-headers-and-content-security-policy.md).

## No escalation

The application's identity holds `Sites.Read.All` and never more.
`Sites.FullControl.All` is ruled out rather than merely discouraged: an
identity that can read every site in the tenant must not also be able to write
to it, and a permission that cannot express a write is a stronger guarantee
than a proxy that declines to make one. If a feature cannot be built on read,
it is narrowed or dropped. See
[`docs/adr/0009`](docs/adr/0009-no-full-control-escalation.md).

A pull request that adds a write-capable application permission is a change to
the threat model, not a bug fix.

## No credentials

The architecture has no client secret, no Key Vault and no connection string
holding a key. A credential appearing anywhere in this repository is by
definition a mistake, and reporting one is welcome. Secret scanning and push
protection are enabled.

That extends to deployment. **Basic publishing credentials are disabled on both
SCM and FTP**, so the function app cannot be deployed to with a username and
password — only through Entra ID. This is a separate control from `ftpsState`,
which disables only the FTP protocol; left at its default the app would still
accept the publishing profile, a downloadable credential that deploys arbitrary
code to a backend holding tenant-wide SharePoint read. A pull request that
re-enables either is a change to the threat model.
