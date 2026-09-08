# Phase 5 — Search for person

> **Substantially rebuilt.** This document is written around
> `getusereffectivepermissions`, which SharePoint refuses to an app-only
> read-only token. What shipped is a different, weaker feature: it reports
> direct naming from the cached scan rather than computing effective access,
> takes several principals at once so a group chain can be followed by hand,
> and says on screen that access arriving through a group appears against the
> group. See [`adr/0012`](adr/0012-group-membership-is-walked-not-resolved.md)
> and [`adr/0011`](adr/0011-permissions-are-read-through-graph.md). The
> reasoning below still explains why the server-side answer was worth wanting.

Answers "what can this person reach in this site". Reached from the "Search for
person" button in the lists and libraries header, and displayed in the same area
as the tree.

## Why this is not a cache lookup

The scan records which principals are named on each broken item. That answers
almost nothing on its own, because the principal named is usually a group, and
the application cannot see who is in it. Inherited access is worse still: it
comes from the site or the library, always through a group, and in a
group-connected team site that group typically contains an M365 group rather
than individuals.

So the question is asked of SharePoint instead:

```
GET /_api/web/getusereffectivepermissions(@u)?@u='{encoded login name}'
```

and the equivalent on lists and on list items. SharePoint resolves the entire
group chain server-side, including nested Entra groups, and returns what the
user can actually do. This is why `GroupMember.Read.All` is not requested.

`VERIFY` this endpoint against the test tenant before building the screen,
including whether `Sites.Read.All` is sufficient for it. See the open item in
`phase-1-deployment-and-search.md`.

## Query scope: permission roots only

Do not query every item. Query only **permission roots**: the site, each scanned
library, and each item with broken inheritance. Items that inherit have no
independent permission state, so asking about them returns the same answer as
their nearest broken ancestor — redundant by definition, not merely wasteful.

For a site with 84 broken items and four libraries that is 89 calls. The site
and the libraries cannot be dropped; they are the baseline that answers for the
12,000 items that inherit.

The saving is in batching, not in reducing the set. Pack the calls into
`/_api/$batch`, which turns 89 logical calls into two or three HTTP requests.
Include only libraries that have actually been scanned.

> **Not done.** `/_api/$batch` is a POST, and no POST reaches SharePoint from
> this application. The calls are made individually, concurrently, as GETs. See
> [`adr/0007`](adr/0007-no-batch-endpoint.md) for the trade-off and for what to
> try first if a large site makes this too slow.

Permission roots come from the cached scan records; phase 3 stores them for this
reason.

## Determining the source

`getUserEffectivePermissions` returns a permission mask: what the person can do,
not why. Derive the source by elimination rather than by another lookup.

For a given root, the role assignments are already cached. Cross-reference:

- The person named directly on the root — source is certain.
- The person is a direct member of a SharePoint group on the root — source is
  certain. Group membership costs one call per group, and a site has a handful
  of real groups, so this is roughly ten calls fetched once and reused across
  every root.
- Only one remaining candidate, and it is an Entra group — the source follows by
  elimination. The mask proves the person has access; the Entra group is the
  only way it can have arrived. The answer is reached without ever seeing inside
  the group.
- Two or more candidates grant the same level — genuinely ambiguous. Say so:
  "Source unclear. Two groups on this folder both grant Read." Do not guess.

## Dialog

Modal. Title "Search for person".

If not every library has been scanned, a warning at the top, before the search
field so expectations are set before the result: "2 of 4 libraries scanned.
Results cover scanned content only."

A search field labelled "Find a person in Entra ID", backed by
`User.ReadBasic.All`. Results as rows with initials avatar, display name and
email; the selected row is highlighted with a check.

Buttons: Cancel, and a primary "Check access". Name it that rather than "Search"
because it starts a computation that takes time, not a lookup.

Show progress during the run, with permission roots as the denominator. This is
the third kind of progress in the application; it belongs to this feature, not
to the library rows.

## Result view

Replaces the tree in the same area, breadcrumb back to "Lists and libraries",
with the person's name as the current crumb. Export button in the header.

Person header: avatar, display name, email.

A persistent line naming coverage: "Covers {libraries}. {others} are not
scanned." Not a dismissible banner.

**Site baseline**, its own block: the site name, the effective role, and a line
naming the source and stating that everything inheriting follows it. Without a
baseline the exception list reads as though the person has access in four places
when the truth is 12,000 items plus four exceptions.

**Exceptions**, one row each: path, effective level, and a line explaining the
source. Show deviations in both directions — a folder where a break *removes*
access is as interesting as one that adds it, and a report showing only
additions would hide that the personnel folder is properly protected.

Colour follows the direction of the deviation, not the permission level. Less
access than baseline is the success tint, more is the warning tint, and equal is
neutral. `No access` therefore appears green, which reads oddly until you
remember the report is about exposure.

Only exceptions are listed, never every item. That is what makes the result four
rows instead of thousands.

## Logging

Log this action separately from ordinary scans, with the actor, the subject and
the site. It profiles a named individual's document access, any authorised user
can run it on any colleague, and it runs under the same role as everything else
by decision. Separate logging is the compensating control for that decision.

## Acceptance

1. A person whose access comes only through a nested Entra group is correctly
   reported as having access. This is the assertion that proves the approach.
2. The number of permission-check calls equals the number of permission roots,
   not the number of items, and they are batched.
3. A folder that removes the person's access appears as an exception with `No
   access`.
4. A root with two groups granting the same level shows the ambiguity rather
   than picking one.
5. The coverage line names the unscanned libraries by name and appears as a
   header row in the export.
6. Running the feature writes one audit entry naming both actor and subject.
