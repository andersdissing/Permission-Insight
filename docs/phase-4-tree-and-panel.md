# Phase 4 — Folder tree and detail panel

Clicking a library replaces the library list with a tree of that library, with a
breadcrumb back. The tab bar stays.

## Header

Breadcrumb: a back chevron, "Lists and libraries", a separator, then the library
name. Right side: an "Only unique permissions" toggle, on by default, and an
export button for this library.

Above the tree, a count line: `84 items with unique permissions` when filtered,
`12,480 items · 84 with unique permissions` when not.

## Filtered mode — the default

Show only items with unique permissions, **plus every ancestor folder of a
finding**, so the path stays intact. Prune any branch containing no findings.

Ancestor folders shown purely as structure must look different from folders that
have a finding of their own. Muted text, no badge, not clickable. Without that
distinction a user assumes every folder in the tree has unique permissions,
which is the opposite of what the filtered view means.

Auto-expand the ancestor chains. The rule about showing only the first level
belongs to the unfiltered view, where there is something to hide. Here the tree
is already pruned, and a collapsed scaffold would conceal exactly what the user
came for. A folder that itself has a finding and also contains more findings
below keeps its expander.

Filtered mode never shows an empty expanded folder, because a scaffold folder
exists only when it contains a finding. So "Empty - no files" cannot occur here.

## Unfiltered mode

Everything, first level only, expandable with `+` which becomes `−`. An expanded
folder with no files shows a muted italic `Empty - no files`, indented one level
under the folder.

The muting convention is the same in both modes: an item without findings is
muted and not clickable, an item with findings is dark and clickable. In
unfiltered mode most of the tree is therefore muted, which is honest — the user
can see at a glance where there is anything to click.

## Row anatomy

Expander square, type icon, name, then badges. Indent 24px per level.

Badges:

- `Direct grant` — broken inheritance with no sharing link. Someone assigned
  permissions by hand. This never expires and is rarely documented, so it is the
  category that matters most.
- Link scope badges — `Anyone` in the warning tint, `Organization` and
  `Specific people` as neutral outlines.
- `External` in the danger tint when any recipient is outside the tenant.
- `N links` when a folder carries several.

No separate filter for sharing links is needed. Every link breaks inheritance,
so links are already a subset of what the filter shows.

## Empty state

A library with no findings, in filtered mode, shows a success-tinted check, the
heading "Everything inherits", and a line naming the library: "Nothing in
{library} has unique permissions or sharing links. Access comes entirely from
the site." A "Show full structure" button turns the filter off.

Zero findings is a good answer, not a missing one, so this must not read as an
error. Keep the count line visible so zero is distinguishable from unscanned.

## Detail panel

Clicking an item with findings opens a panel overlaying the tree from the right,
400px wide, leaving the ancestor chain visible so the user keeps context. The
selected row is highlighted behind it, and that row's badges are suppressed
while the panel is open, since the panel now states the same facts.

The panel needs no network calls. Everything comes from phase B of the library
scan, so the panel works on a cached scan with no connection at all.

**Fixed header**, outside the scroll area: file or folder name, a close button,
the server-relative path in monospace, and a link "Manage permissions in
SharePoint".

The link follows `_layouts/15/user.aspx?obj={listGuid},{itemId},LISTITEM&List={listGuid}`.
`VERIFY` the encoding of the braces against one item.

It must open in a new tab. This is not a preference: the application installs a
`beforeunload` guard while cached scans exist, and navigating in the same tab
would hit the browser's leave-site warning in the middle of an action the user
started.

**Scrolling body** below the header, with `overflow-y: auto` and
`overscroll-behavior: contain` so reaching the bottom does not scroll the page
behind it. Do not make the header `position: sticky` inside the scroll area; on
touch, rubber-banding can push the close button out of reach. Reset scroll
position to the top when a different item is selected without closing the panel
first, otherwise the next item opens halfway down its own list and looks like it
failed to update. Show a visible bottom edge when content continues.

### Access section

One row per role assignment: an expander for groups, a type icon, the principal
name, and the role right-aligned.

Show the role only at the top level. Members inherit the group's role, and
repeating it per member would imply individual assignments that do not exist.

Site groups appear even though they were copied automatically when inheritance
broke. They are direct assignments on the item, and omitting them makes a file
look as though it is only shared externally.

`Limited Access` rows never appear.

**Expanding a group** calls `/_api/web/sitegroups({id})/users` once, on click.
Direct members are shown one level deep.

Where a member is an Entra group, the chain stops, and the row must say so:
"Entra group. Members not shown." An empty expansion would read as "nobody has
access", which is the tool giving a wrong answer to the question it exists to
answer. See `02-permissions.md` for why `GroupMember.Read.All` is not requested.

There is no source column. On a broken item every assignment is direct, so a
column reading "Direct" all the way down adds nothing.

### Sharing links section

One card per link: scope badge, role, expiry or "No expiry", then recipients
with an External badge where applicable.

An `Anyone` link gets an explanatory line — "No password. Anyone holding the
link can open it." — because it is the one scope whose consequence is not
obvious from its name.

If a section would run long, collapse Sharing links by default when there are
more than three, and leave Access open.

## Acceptance

1. Filtered mode shows a file five levels deep with its full ancestor chain, and
   the ancestors are muted and unclickable.
2. Toggling the filter off shows the whole library, first level collapsed.
3. An expanded folder with only subfolders and no files shows `Empty - no
   files`.
4. A library with zero findings shows the empty state, not a blank tree.
5. The panel opens with no network request when the library was loaded from
   cache.
6. Expanding a SharePoint group containing an Entra group shows the Entra group
   with the "Members not shown" line, never as empty.
7. The SharePoint link opens in a new tab and lands on the permissions page for
   the correct item.
8. Scrolling to the bottom of the panel does not scroll the page behind it.
