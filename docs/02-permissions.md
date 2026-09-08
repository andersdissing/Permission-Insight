# 02 — Permissions

This document exists because a security reviewer will ask why a reporting tool
needs tenant-wide read access. Every permission below is justified by a specific
feature, and the ones deliberately left out are listed with their reasons.

## Summary

| Permission | Type | Held by | Consent | Used by |
| --- | --- | --- | --- | --- |
| `Sites.Read.All` | Application | Managed identity | Admin | Site search, item enumeration, **and all permission reads — through Graph** |
| `User.ReadBasic.All` | Application | Managed identity | Admin | The people half of the picker |
| `GroupMember.Read.All` | Application | Managed identity | Admin | Opening an Entra group in the detail panel; see [`adr/0012`](adr/0012-group-membership-is-walked-not-resolved.md) |
| ~~`Sites.Selected`~~ | — | — | — | **Removed.** It granted nothing without a per-site grant, and none is needed. Taken off the identity and out of `app-roles.bicep` rather than left to be explained. See [`adr/0011`](adr/0011-permissions-are-read-through-graph.md) and [`setup-guide.md`](setup-guide.md) |
| `User.ReadBasic.All` | Application | Managed identity | Admin | People picker in Search for person |
| ~~`Files.ReadWrite`~~ | Delegated | App registration | User, requested on demand | Saving an export to the user's own OneDrive — **deferred, and not currently declared**; see [`adr/0005`](adr/0005-onedrive-export-deferred.md) |
| `PermissionInsight.Use` | App role | App registration | Assignment | Gates who may use the application at all |

The split matters. The two application permissions sit on the backend's
user-assigned managed identity, which has no exportable credential. The app
registration that users sign in against holds only the role gate and a delegated
scope bounded to the signed-in user's own OneDrive. Compromising the front end
therefore does not yield tenant-wide read.

## `Sites.Read.All` (Application)

> **Corrected twice by a real tenant, and it lands close to where it started.**
> This permission does not let SharePoint's *own* API read role assignments,
> site groups or effective permissions — those are refused app-only with 403,
> because they need a right belonging to Full Control. But Microsoft Graph
> reads the same data and documents `Sites.Read.All` as its least privileged
> application permission, so no further permission is required after all. See
> [`adr/0011`](adr/0011-permissions-are-read-through-graph.md), and
> [`adr/0010`](adr/0010-hybrid-tenant-read-plus-site-grants.md) for the
> per-site design this replaced.

**What it is for.** Everything — finding sites by title and URL, listing
libraries, enumerating items, and reading who has access to what. The last of
those goes through Graph rather than SharePoint REST, which is an
implementation detail everywhere except in what it costs: nothing, where the
alternative was full control of every site being audited.

**Why application and not delegated.** The requirement is that any authorised
user of the tool can search and scan any site. With a delegated token, results
are trimmed to what that user can already see. A site owner auditing their own
site would work fine; a governance analyst auditing the tenant would silently
miss everything they lack rights to, and would have no way to tell the
difference between "no findings" and "no access".

**What it actually grants, stated plainly.** Read access to the content of every
site in the tenant, not only metadata. There is no narrower scope that lists
sites without also permitting file reads. `Sites.Selected` would be narrower but
requires each site to be granted in advance, which cannot coexist with free-text
search across the tenant.

**Consequences that must be built, not assumed.**

- The application is the entire access boundary. Entra no longer trims anything.
  The app role in the next section is what stands between a signed-in employee
  and tenant-wide read.
- Site titles and URLs are themselves sensitive. A complete list of sites is an
  organisation chart, and free-text search makes specific things easy to find.
  Treat the search log as security-relevant, not as telemetry.
- The identity holding this permission is the crown jewel. It is a user-assigned
  managed identity precisely so that there is no secret to leak, rotate or
  accidentally commit. Do not reintroduce a client secret.

**Open item that blocks phase 3.** `VERIFY` whether `Sites.Read.All` is
sufficient for reading `roleassignments` through the SharePoint REST API and for
`getUserEffectivePermissions`. Both are associated with the `ManagePermissions`
right, which is part of Full Control. Test both against one item in the test
tenant before the app registration goes for approval.

~~If the test fails, the fallback is `Sites.FullControl.All`, which grants write
and delete across the tenant. **That escalation requires renewed security
approval and is not a default.** If it is taken, the read-only enforcement in
the backend proxy stops being belt-and-braces and becomes the only thing
preventing a tenant-wide write capability.~~

> **Ruled out, not merely discouraged.** There is no escalation path: the
> application holds `Sites.Read.All` and never more. If a feature cannot be
> built on read, it is narrowed or dropped rather than the permission widened.
> [`adr/0009`](adr/0009-no-full-control-escalation.md) sets out what happens to
> phases 3 and 5 if the probe comes back negative, and why a delegated fallback
> is not a substitute either.
>
> This governs the application's identity. Tooling that provisions test data
> writes by nature and is out of scope; see the same record.

## `User.ReadBasic.All` (Application)

**What it is for.** The people picker in Search for person. It reads display
name, mail and user principal name so the user can pick a person to check.

**Why not use the principals already found in the scan.** That alternative
exists and needs no permission at all: every principal that appears in a role
assignment or as a sharing link recipient is already cached. It was rejected
because a person with no access would then not appear in the picker, and users
would read a missing name as a failed search rather than as an answer.

**Scope.** `ReadBasic` rather than `Read.All`, because the picker needs a name
and an address and nothing else.

## `Files.ReadWrite` (Delegated)

> **Not currently requested.** OneDrive export is deferred to
> [`backlog.md`](backlog.md), so this scope was removed from the app
> registration rather than left declared against a feature that does not exist.
> The application therefore holds no write permission of any kind. The
> reasoning below stands for when it is restored; see
> [`adr/0005`](adr/0005-onedrive-export-deferred.md).

**What it is for.** Writing an export file to the signed-in user's own OneDrive,
when the user chooses OneDrive instead of a browser download.

**Why delegated and not application.** There is no application scope limited to
one user's OneDrive. `Files.ReadWrite.All` as an application permission grants
write access to every user's files in the tenant, which is a far larger
capability than the feature needs and worse than the problem it was meant to
solve. A delegated token is bounded to the signed-in user's own drive by
definition.

**Why requested on demand.** Asking for it at sign-in makes the consent screen
heavier for every user, including the majority who never export to OneDrive.
Acquire it incrementally when the OneDrive option is chosen.

**Why download is the default.** Browser download needs no permission at all.
An application that works fully without any write scope is easier to defend.
OneDrive is the option for users who want the file to land inside the
organisation's own policies and labels — a governance benefit, not a security
one.

## `PermissionInsight.Use` (App role)

**What it is for.** Deciding who may use the application. With application
permissions behind it, signing in is equivalent to tenant-wide read, so this is
the real access control.

**Why an app role and not a group claim.** Entra omits the `groups` claim
entirely once a user is a member of more than roughly 150 to 200 groups, and
substitutes an overage indicator pointing at Graph. The users who hit that limit
are administrators — exactly this application's audience. A gate built on group
claims would fail for the most privileged users and work for everyone else,
which is a miserable class of bug. An app role emits a `roles` claim containing
only roles for this application, so overage cannot occur, and the claim value is
a readable role name rather than a GUID.

**How assignment works.** Bicep provisions an Entra security group and assigns
that group to the app role. Membership of the group is managed normally.

**Set `appRoleAssignmentRequired` to `true`** on the service principal.
Without it, any user in the tenant can obtain a token for the application and
simply arrives with no `roles` claim. With it, Entra refuses sign-in before the
application's own code runs. Two layers: identity blocks most, code catches the
rest.

**One role, not two.** An earlier draft split search from scanning. It was
dropped because with application permissions every authorised user can do
everything anyway, so a second role would describe a distinction that does not
exist.

Note for the record: Search for person is a different shape of capability from
auditing a library. It profiles a named individual's document access, and any
app user can run it on any colleague. It runs under the same role by decision.
The compensating control is that it is logged separately. If that ever feels
insufficient, a second role is the remedy, and adding one later means touching
every existing assignment.

## Deliberately not requested

> **Reversed: `GroupMember.Read.All` is now requested.** The argument below
> was sound and depended entirely on `getUserEffectivePermissions` being
> reachable. It is not — SharePoint refuses it to an app-only read-only token —
> so the chain is walked one level at a time instead. See
> [`adr/0012`](adr/0012-group-membership-is-walked-not-resolved.md).

**`GroupMember.Read.All`.** ~~Would let the application expand Entra group
membership and answer "is this person in that group". It is not requested
because `getUserEffectivePermissions` answers the underlying question better:~~
SharePoint resolves the whole group chain server-side, including nested Entra
groups, and returns what the user can actually do. Fewer permissions, fewer
calls, and correct where client-side expansion would still have to guess about
nesting.

The visible consequence: in the detail panel, a SharePoint group can be expanded
to show its direct members, and where a member is an Entra group the chain
stops. That row must say so explicitly — "Entra group. Members not shown." — and
must never render as an empty group. An empty expansion reads as "nobody has
access", which would be the tool giving a wrong answer to the exact question it
exists to answer.

**`Sites.Selected`.** Requires per-site grants in advance. Incompatible with
searching across the tenant.

**SharePoint admin API.** Would supply `SharingCapability` per site, which is
the external-sharing badge dropped from v1. It escalates the registration into
tenant administration, which is a much harder internal approval for one badge.
Backlog.

## Token lifetime and revocation

Removing a user from the group does not invalidate a token already issued.
Access ends when the token expires, typically within an hour. Continuous Access
Evaluation would close that window and is deliberately not in v1. Record it as
accepted risk: acceptable for an internal read-only tool with a small user
group, and a different judgement entirely if the application ever gains write
access.
