# 0011 — Permissions are read through Graph, not SharePoint

Status: accepted, 2026-09-04. Supersedes the permission model in
`02-permissions.md`, and makes [`0009`](0009-no-full-control-escalation.md) and
[`0010`](0010-hybrid-tenant-read-plus-site-grants.md) mostly moot.

## What the tenant taught us, in the order we learned it

Deployed against a real tenant, an app-only `Sites.Read.All` token could read
lists and page 12,000 items happily, and was refused with 403 on every
SharePoint endpoint that reports *who has access*: `/_api/web/sitegroups`,
item `roleassignments`, and `getusereffectivepermissions`. They need
`EnumeratePermissions`, which SharePoint places in Full Control alone.

That looked like a wall. SharePoint offers no read-only application
permission that can read permissions — `Sites.Read.All` is the top of the
read-only range, and everything above it (`Sites.ReadWrite.All`,
`Sites.Manage.All`, `Sites.FullControl.All`) can write. `0009` closed off
escalation and `0010` worked around it with per-site `Sites.Selected` grants,
which meant an administrator onboarding each site by hand and the application
holding full control of the sites it audited.

Then it turned out Graph answers the same question with the permission we
already had:

```
GET /sites/{site-id}/lists/{list-id}/items/{item-id}/permissions
```

Microsoft documents **`Sites.Read.All` as the least privileged application
permission** for it.

## Decision

Permission data is read through Microsoft Graph. SharePoint REST is still used
for what it is uniquely good at — enumerating lists, and the flat
`$skiptoken`-paged item view that survives the 5,000 item list view threshold —
but never for reading permissions.

Addressed as a **listItem** rather than a driveItem. Only document library
items have driveItems, so the driveItem route cannot see a generic list at all;
the listItem route covers every list type and takes the site, list and item ids
the scan already holds, instead of needing a path resolved to a drive.

## What this buys

- The application runs on `Sites.Read.All`, `User.ReadBasic.All` and
  `GroupMember.Read.All`. All read-only. **It holds no permission that can
  write anything, anywhere.**
- No per-site grant, no `Sites.Selected`, no administrator in the loop, no
  onboarding step.
- Graph returns more than SharePoint did. `inheritedFrom` states whether a
  permission belongs to the item or came from an ancestor, which SharePoint
  made us infer, and the `link` facet carries a sharing link's scope, expiry
  and recipients in the same response.

## What it costs

- **One call per item with unique permissions.** The same shape of cost the
  library scan always had.
- **A documented ambiguity.** Microsoft's docs say that for a non-owner caller
  only the permissions applying to that caller are returned. Where an app-only
  token sits in that rule is unstated. Observed behaviour on a real tenant is
  that the full set comes back, but this is an assumption resting on
  observation rather than documentation, and it is the one that would fail
  quietly if it changed.

## Consequences for the older records

- `0009` still stands: no escalation to `Sites.FullControl.All`. It is simply
  no longer a live question.
- `0010`'s per-site grant is unnecessary. `Sites.Selected` remains assigned to
  the managed identity, granting nothing, and `scripts/grant-site.ps1` remains
  for anyone who needs the SharePoint endpoints for another reason. Neither is
  on the path any more.
- `02-permissions.md`'s claim that `Sites.Read.All` covers "everything" was
  wrong in an interesting way: it covers everything *through Graph*, and much
  less through SharePoint's own API.
