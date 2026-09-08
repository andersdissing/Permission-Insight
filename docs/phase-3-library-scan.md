# Phase 3 — Lists and libraries tab

The second tab. This is where the broken-inheritance scan lives.

## Enumerating lists

One call returns titles, `ItemCount`, `Hidden`, `BaseType` and
`HasUniqueRoleAssignments` for every list. Exclude hidden lists and the standard
system lists: Form Templates, Site Assets, Style Library, Preservation Hold
Library, Master Page Gallery, User Information List, Web Part Gallery, Workflow
History. A "System lists" toggle in the tab header brings them back, off by
default.

Fetch `ItemCount` here even though it is not displayed in the list. It is the
denominator for the progress bar and the input to the size badge.

## Size badges

Shown on every library row. Boundaries are inclusive at the lower end:

| Items | Badge | Tint |
| --- | --- | --- |
| 0–1,999 | Small | Neutral outline |
| 2,000–5,999 | Medium | Neutral outline |
| 6,000–9,999 | Large | Warning |
| 10,000+ | Extra large | Warning |

An information icon next to the library count explains what the badge means. Its
wording must describe **size**, not duration. The badge only measures the first
of two scan phases: a library with 12,000 items and no broken inheritance
finishes quickly, while one with 3,000 items and 800 broken items is slower,
because the second phase costs a call per broken item.

## What gets scanned

The **default document library scans automatically** when the user opens the
tab. Every other list and library is opt-in, with a Scan button on its row.
Selecting one starts its scan immediately; there is no separate start button.

Header actions next to the library count: "Scan all", "Export all" and "Search
for person". Search for person is always enabled, but when not every library has
been scanned it shows a warning that results cover scanned content only. See
`phase-5-person-search.md`.

For "Export all", prefer prompting over disabling: on click, if libraries are
unscanned, say how many and offer to scan them now.

## The scan

### Phase A — enumerate

```
GET /_api/web/lists(guid'{listId}')/items
    ?$select=ID,HasUniqueRoleAssignments,FileRef,FileLeafRef,FileSystemObjectType
    &$top=1000
```

Follow `odata.nextLink` until exhausted. Do not use CAML with `ViewFields`; it
hits the list view threshold above 5,000 items. Do not load
`HasUniqueRoleAssignments` per item; that is 10,000 round trips and throttling.

Page size is 1,000 rather than 5,000 deliberately. At 5,000 a 12,000-item
library jumps 0, 42, 83, 100 — three progress updates. At 1,000 it moves
thirteen times, which is still cheap and actually looks like progress.

Keep **every** item, not just the broken ones. The tree in phase 4 needs the
full hierarchy or it has holes. Folders arrive in the same flat response with
`FileSystemObjectType` of `1`.

Progress text: `Step 1 of 2 · Reading items · 7,000 of 12,480 (56%)`.

### Phase B — resolve

For each item where `HasUniqueRoleAssignments` is true:

```
GET /_api/web/lists(guid'{listId}')/items({itemId})/roleassignments
    ?$expand=Member,RoleDefinitionBindings
```

Filter out every assignment whose only role is `Limited Access`. SharePoint
grants it automatically up the tree so a recipient can navigate to a shared
item; it is noise, and leaving it in makes every library look far worse than it
is.

Classify each remaining assignment. A `Member.LoginName` starting with
`SharingLinks.` is a sharing link; anything else is a direct grant. The
distinction drives the labels in phase 4 and matters for remediation, because a
direct grant never expires and is rarely documented.

The denominator for this phase is unknown until phase A finishes. Show it as a
second step with its own bar rather than weighting two unknowns into one:
`Step 2 of 2 · Reading permissions · 31 of 84`.

Store the list of **permission roots** for the library alongside the results.
Phase 5 needs them and would otherwise force a rescan.

## Library row states

**Scanning.** Name, Default badge if applicable, size badge, progress bar,
progress text.

**Cached.** Name, size badge, and a line reading `Saved 2 Sep · 47 of 3,240
items with unique permissions`. Trailing icon buttons for rescan and export.
Without the result count, a cached scan and an empty scan look identical.

**Not scanned.** Name, size badge, "Not scanned", and a Scan button.

**Failed.** Name, size badge, what failed, and a Retry button. Distinguish
throttling from permission failures, because only one of them is worth retrying.

Caching is **per library**, keyed `{oid}:{siteId}:{listId}`, so a site can have
one library saved and another never touched. Only write if the Save scans toggle
is on.

## Throttling

Expect 429 with `Retry-After`. Honour the header, back off exponentially, and
surface a sustained retry in the progress text rather than freezing a bar that
appears to have stalled.

## Acceptance

1. A 12,000-item library completes both phases and the progress bar advances at
   least ten times during phase A.
2. Item counts appear nowhere in the library list, only in progress text and in
   the cached result line.
3. `Limited Access`-only assignments never appear in the stored result.
4. Sharing link assignments and direct grants are classified correctly, checked
   against test data where both exist on the same folder.
5. Scanning one library and not another leaves exactly one cached record for
   that site.
6. A deliberately induced 429 causes a visible backoff message and eventual
   completion, not a stalled bar.
7. The stored record contains inheriting folders, not only broken items.
