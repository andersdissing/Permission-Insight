# 0012 — Group membership is walked, not resolved

Status: accepted, 2026-09-04. Reverses the refusal of `GroupMember.Read.All`
in `02-permissions.md`, and replaces the design of
`phase-5-person-search.md`.

## What the specification decided, and why it no longer holds

`02-permissions.md` refused `GroupMember.Read.All` with a good argument:

> `getUserEffectivePermissions` answers the underlying question better:
> SharePoint resolves the whole group chain server-side, including nested Entra
> groups, and returns what the user can actually do. Fewer permissions, fewer
> calls, and correct where client-side expansion would still have to guess
> about nesting.

That was right, and it depended entirely on `getUserEffectivePermissions` being
reachable. It is not: SharePoint refuses it to an app-only read-only token, and
the only permissions that would work can write. See
[`0011`](0011-permissions-are-read-through-graph.md).

So the choice is no longer "resolve server-side or expand client-side". It is
"expand client-side, or answer nothing".

## Decision

Take `GroupMember.Read.All`, and walk the chain one level at a time.

`GroupMember.Read.All` rather than `Group.Read.All`: it reads membership and
nothing else, where `Group.Read.All` would also read every group's properties
across the tenant. The narrower one is sufficient for what the panel does.

## What the feature can and cannot say

Search for people or Entra ID group reports **direct naming**. For a given
principal it finds the items whose permissions name that principal, plus any
grant on the library itself, and says which.

It does **not** compute effective access. If someone reaches a file through a
group, the group is what appears, not the person. Following the chain is done
by searching for the group as well, which is why the picker takes several
principals at once rather than one.

Where a SharePoint group holds the grant, the chain stops immediately: reading
site group membership needs the same SharePoint rights we do not have, and
Graph has no equivalent endpoint. The row says "Members not shown" rather than
rendering empty.

## Why this is stated in the interface, not just here

An empty result from a search is the dangerous case. It reads as "this person
has no access" when it may mean "this person's access arrives through a group
you did not search for". So the result screen says so in words, and the CSV
carries the same caveat in its coverage line.

That is the whole reason the specification preferred the server-side answer,
and losing it is a genuine reduction in what the tool can claim. Recording it
plainly is the least we can do about it.
