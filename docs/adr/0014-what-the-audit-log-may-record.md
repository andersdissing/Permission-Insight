# 0014 — What the audit log may record

- Status: accepted
- Date: 2026-09-07

## Context

`CLAUDE.md` requires that every search, scan and person lookup is logged with
the user object id, a timestamp and the target, and states that this log is the
only audit trail the backend keeps.

The first implementation satisfied that by logging the request route together
with its **entire query string**, on the reasoning — written into `AuditLog` —
that for a search the query string *is* the target. That is true for
`/api/sites?q=finance`. It stopped being true once the library scan shipped.

Phase B of a scan calls `/api/lists/permissions` once per item with unique
permissions, and the browser sends the item's server-relative path in that
call. A library with 500 broken items therefore deposited 500 document paths
into Application Insights, retained for a year by `auditRetentionDays`.

Two things were wrong with that, and only one of them is about volume.

The application tells the user, in the storage panel, that scanned paths live
in their own browser, and `CLAUDE.md` says the backend has no database.
Server-side a substantial path index existed anyway. The tool was making a
promise its own telemetry broke.

Worse, that index sat **outside the access control model**. Reading a Log
Analytics workspace is an Azure RBAC grant, typically held by a platform team.
The tool is gated on the `PermissionInsight.Use` app role, held by a governance
group. Those are different populations, and `SECURITY.md` already says a folder
path is frequently more revealing than the document inside it. The audit
mechanism was quietly routing the most sensitive thing the application handles
around the one check that was supposed to govern it.

The same shape appeared twice more. `DownstreamException` captured 2,000
characters of the raw downstream error body and three handlers logged it;
SharePoint writes the resource into its error messages — *"List 'Personalesager'
does not exist at site with URL …"* — so a permission failure logged a list
name. And the audit endpoint accepted unbounded caller free text.

## Decision

The audit log records **who looked at which site, and when**. It does not record
what was inside.

1. Query strings pass through `AuditQuery.Redact` before they are logged. It is
   an **allow-list of parameter names** — `q`, `siteId`, `listId`, `itemId`,
   `itemGuid`, `linkGuid`, `groupId` — and every other value becomes
   `[redacted]`. The name is kept, because the fact that a parameter was sent is
   itself auditable.

2. Redaction happens inside `AuditLog.RecordRequest` rather than at its call
   site, so there is one path in and no way to log a raw query by forgetting.

3. `DownstreamException` no longer carries the error body at all. It parses the
   error *code* out of either Graph's or SharePoint's error shape and keeps only
   that. A body that does not parse yields null rather than falling back to the
   raw text, because an unparseable body is precisely the case where nobody
   knows what it contains.

4. Free text on the audit endpoint is capped at 200 characters.

## Consequences

An allow-list means a query parameter added later is redacted until somebody
classifies it. That will occasionally produce a less useful audit entry than
intended, and the fix is a one-line change with a test. This is the correct
direction to fail in: the alternative — a deny-list of the parameters known to
be sensitive today — starts leaking the moment someone adds a field and does not
think about this file.

Diagnosis loses the downstream error message. The code alone still answers what
a log is actually consulted for, which is whether the identity is missing a
grant or the item is simply gone. A message naming the resource was never worth
a year of retention outside the role gate.

The skip token is redacted as well, which is a side benefit rather than the
point: SharePoint paging tokens encode the sort column's values, so a token can
carry a file name without anyone intending it to.

## What this does not fix

Application Insights still receives the site identifiers, the search terms and
the object id of every caller, and that is deliberate — it is the audit trail.
A complete list of the sites somebody searched for is still an organisation
chart, and the workspace should be treated as sensitive and access-reviewed
accordingly. Redaction narrows what accumulates there; it does not make the
workspace unprivileged.
