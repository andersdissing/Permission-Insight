# Phase 2 — Sharing links tab

The first tab on the site screen. Provision test data before starting this
phase; see `10-test-data.md`.

## The mechanic that makes this cheap

Every sharing link creates a hidden site group named
`SharingLinks.{itemGuid}.{kind}.{linkGuid}`. Site groups are site-collection
scoped, so a **single call** to `/_api/web/sitegroups` returns every sharing
link in the site including its subsites.

That means the total appears in well under a second and needs no progress
indicator. Resolving each link into a path with recipients is the expensive
part, and it happens progressively afterwards.

Page the endpoint. It returns 100 per page by default; use `$top` and follow
`odata.nextLink`.

## Two-stage load

**Stage 1, immediate.** Fetch site groups, filter titles starting with
`SharingLinks.`, split on `.` to get the item GUID and the link kind. Show the
total on the tab badge and as the heading.

Do not build the scope distribution from the group name. Modern SharePoint
almost always creates `Flexible` links, whose real scope lives in the permission
object, so a distribution from names would read "460 Flexible" and tell the user
nothing.

**Stage 2, progressive.** For each link group, in the background from the top of
the list:

1. Resolve the item. Try `/_api/web/GetFileById('{guid}')?$select=ServerRelativeUrl`,
   then `/_api/web/GetFolderById('{guid}')` on 404.
2. Read the permission through the Graph permissions endpoint on the item, which
   returns scope, roles, recipients, expiry and password state in one call.

Use the Graph permissions endpoint rather than `/sitegroups({id})/users`. The
group members call gives you recipients only, and would need a third call for
expiry.

Resolve from the top rather than lazily on scroll. An unresolved row holds only
a GUID and a kind, so lazy resolution would show the user almost nothing, and
export would have to force a full pass anyway.

`VERIFY` that the GUID in the group name resolves through `GetFileById`. Test
against one file you have deliberately shared before relying on it across a
whole site.

## Screen

The site screen is now the search box, framed to show the selected site, and two
tabs beneath it. Tab one is "Sharing links" with the total as a badge. Tab two
is "Lists and libraries" and shows a spinner while its own scan runs.

Under the tab bar:

- A thin progress bar with the text "Resolving 137 of 482". It disappears when
  stage 2 completes.
- An export button, always enabled. Sharing links come from one site-collection
  call and never depend on which libraries were scanned, so this tab can always
  be exported in full.
- A row of scope chips that grows as rows resolve: Anyone, Organization,
  Specific people, Orphaned. The counts must sum to the number resolved so far,
  not to the total.
- The row list.

**Resolved row.** Server-relative path in monospace on the first line. Below it,
badges and facts: the scope, an External badge if any recipient is outside the
tenant, the role, and the expiry or "No expiry". Use the warning tint for
`Anyone` and the danger tint for `External`; everything else is a neutral
outline.

**Unresolved row.** Flat grey placeholder bars at the same height as a resolved
row, so the list does not reflow as rows fill in.

## Orphaned groups

A `SharingLinks.*` group does not guarantee an active permission. The item may
be deleted, in which case both resolve calls return 404.

Do not discard these. Show them with the path replaced by "Orphaned" and keep
the group's members. An orphaned group with an external guest still in it is a
finding in its own right, and one that does not appear in Microsoft's own
sharing reports.

## Caching

Store the completed result under `sharingLinks` keyed by `{oid}:{siteId}`, but
only if the Save scans toggle is on. Write once at the end of stage 2 rather
than incrementally, so a partial result is never cached as complete.

On entering a site that already has a cached sharing link scan, show the cached
result and do not rescan. Show when it was saved, and offer a refresh action
that makes clear it starts a full rescan.

## Acceptance

1. The total appears within two seconds of selecting a site with 400+ links.
2. Rows resolve from the top, and the chip counts always equal the number of
   resolved rows.
3. A file shared with an anonymous link shows scope Anyone with the warning
   tint. A file shared with a named external guest shows the External badge.
4. A deliberately orphaned group, created by sharing a file and then deleting
   the file, appears as Orphaned with its members intact.
5. Export produces a complete file regardless of which libraries have been
   scanned.
6. Re-entering the site loads from cache with no network calls, and refresh
   performs a full rescan.
