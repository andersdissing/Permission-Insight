# 0010 — Tenant-wide read, plus elevated access one site at a time

Status: **superseded** by [`0011`](0011-permissions-are-read-through-graph.md),
2026-09-04, hours after it was accepted.

> The wall this record works around turned out to have a door in it. Microsoft
> Graph reads the same permission data with plain `Sites.Read.All`, so no
> per-site grant is needed and no administrator has to onboard anything. Read
> `0011` instead; this is kept because the reasoning about what SharePoint's
> own API will and will not do is still accurate, and because a decision that
> was reversed within a day is worth being able to trace.

## What the tenant told us

Deployed and tried, the managed identity holding app-only `Sites.Read.All` got:

| Endpoint | Result |
| --- | --- |
| `/_api/web/lists` | works |
| `/_api/web/lists(…)/items` | works — 12,000 items paged fine |
| `/_api/web/sitegroups` | **403** Access is denied |
| item `roleassignments` | **403** Attempted to perform an unauthorized operation |
| `getusereffectivepermissions` | **403** |

The token is valid and accepted; specifically the permission-reading endpoints
are refused. They need `EnumeratePermissions`, which belongs to Full Control,
and app-only `Sites.Read.All` does not carry it.

The SharePoint application roles are `Sites.Selected`, `Sites.Read.All`,
`Sites.ReadWrite.All`, `Sites.Manage.All`, `Sites.FullControl.All`, and two
admin-metadata ones. **There is no read-only permission that can read
permissions.** Everything that would work is write-capable, and `0009` ruled
out taking one across the tenant.

## Decision

Two permissions, doing different jobs:

- **`Sites.Read.All` (application)** stays. It powers site search and item
  enumeration across the tenant, which demonstrably work, and it cannot write
  anything.
- **`Sites.Selected` (application)** is added. It grants nothing by itself. An
  administrator grants this identity elevated access to one named site at a
  time, and only then can the tool read that site's permission data.

Tenant-wide access remains read-only. Write capability exists only on sites
somebody has deliberately onboarded.

## Why this is better than the alternatives

Against tenant-wide `Sites.FullControl.All`: the blast radius is the set of
onboarded sites rather than every site in the organisation, and that set is
visible, auditable and revocable per site.

Against pure `Sites.Selected`: there is no Graph API that enumerates the sites
an application has been granted, so a pure design cannot discover its own
scope; the site picker would need a hand-maintained list, and `01-architecture.md`'s
requirement that any authorised user can search any site would be lost. The
hybrid keeps search tenant-wide and read-only, which is exactly the shape that
requirement wants.

## The rule that makes it worth anything

**The application must never be able to grant itself site access.** If it
could, `Sites.Selected` would be indistinguishable from tenant-wide write.

Onboarding is therefore an administrator action taken outside the tool, with a
delegated credential, and the backend has no code path that calls
`POST /sites/{id}/permissions`. The front end explains what is missing and
prints the command; it does not offer a button that performs it.

## Consequences

- A site that has not been onboarded is a **normal state**, not an error. The
  API answers `site_not_onboarded` with the exact grant command, and the front
  end presents it as a missing step. Reporting it as a failure would train
  users to ignore a message that means something specific.
- Onboarding becomes a documented step in the runbook, and arguably a feature:
  a governance tool that can only report on sites somebody explicitly enrolled
  is easier to defend than one that reads everything by default.
- The grant level is `FullControl` on the granted site. **`Manage` was tried
  first and is not sufficient** — tested against a real tenant on 2026-09-04:
  with a `manage` grant, SharePoint still refused `roleassignments` with 403.
  The missing right is `EnumeratePermissions`, which belongs to Full Control
  alone, so `FullControl` is the floor rather than a convenience. Do not spend
  time trying to narrow it again without new information from Microsoft.
- `02-permissions.md`'s summary table is now incomplete: it predates this and
  does not list `Sites.Selected`. Read this record alongside it.
