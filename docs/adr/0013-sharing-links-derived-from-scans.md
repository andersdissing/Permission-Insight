# 0013 — Sharing links fall back to the library scans

Status: accepted, 2026-09-05. Narrows the two-stage design in
`phase-2-sharing-links.md` when SharePoint's site groups cannot be read.

## The problem

`phase-2` is built on a genuinely elegant mechanic: every sharing link creates
a hidden site group named `SharingLinks.{itemGuid}.{kind}.{linkGuid}`, site
groups are site-collection scoped, so **one call** to `/_api/web/sitegroups`
returns every link in the site including its subsites, and the total appears in
under a second before anything is scanned.

That call needs rights an app-only read-only token does not have. It was the
last thing in the product still depending on a per-site grant; see
[`0011`](0011-permissions-are-read-through-graph.md).

## Decision

Try site groups first. If SharePoint refuses, derive the links from the
library scans instead.

This needs no extra calls at all. Every sharing link breaks inheritance, so
every link sits on an item the library scan has already read permissions for,
and Graph returns the `link` facet — scope, role, expiry, recipients — in that
same response. The links were always in the data; nothing was asking for them.

## What the fallback cannot do

Both losses are real and are stated on screen and in the export's coverage
line, not smoothed over.

**It only covers scanned libraries.** `09-export.md` promises sharing links can
always be exported in full "regardless of which libraries were scanned",
because they came from one site-collection call. Derived this way that promise
does not hold, and a count that silently covers part of a site is worse than no
count.

**It cannot find orphaned links.** When the file behind a link is deleted the
`SharingLinks` group survives it. `phase-2` singles this out as valuable: an
orphaned group still holding an external guest is a finding, and one that does
not appear in Microsoft's own sharing reports. With no item left, there are no
item permissions to read, so the link is invisible.

**The instant total goes.** You have to scan a library before the tab has
anything to say.

## Why keep both paths

Where the site groups call works — a tenant that has granted the site, or a
future where SharePoint permits the read — the original design is better on
every axis, so it stays the first choice. The fallback exists so that the
product works with no grant at all, at a stated cost, rather than showing an
error and a PowerShell command.

If orphan detection turns out to matter more than zero-permission operation,
the remedy is a per-site grant via `scripts/grant-site.ps1`, and the tab
silently improves.
