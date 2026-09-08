# 0015 — Tenant-wide claims, and reading access at the site

- Status: accepted
- Date: 2026-09-07

## Context

Two gaps, found together because they compound.

**The tool could not recognise a grant to everybody.** SharePoint has claim
principals that stand for the whole organisation: "Everyone", which includes
guests, and "Everyone except external users", which is every licensed user.
Granting one is a single role assignment. It has no members to enumerate, so
nothing grows and nothing looks unusual, and the tool rendered it as one
ordinary user row among the others.

The obvious fix — match the display name — is a trap, and the reason this ADR
exists. **The display name is localised.** The same principal is "Alle undtagen
eksterne brugere" on a Danish tenant and "Jeder außer externen Benutzern" on a
German one. Code matching English text would work perfectly in development and
report nothing on the tenants it was deployed to, and it would do so by showing
a clean result rather than an error. That is the same failure family already
recorded four times in this project: the tool answering confidently instead of
admitting it cannot see.

**The tool never read the site's own permissions.** It reported sharing links,
library-level breaks and item-level breaks — all of them exceptions to
inheritance. A grant at the site is not an exception to anything. It breaks no
inheritance, so a complete scan of every item in every library finds nothing,
while every one of those items is readable by the whole organisation. The
widest exposure the product exists to find was the one scope it never looked at.

## Decision

**Match on the claim, never on the name.** `TenantWideClaim` holds the claim
table and is handed every identifier Graph returned for a principal — the
display name is deliberately not among them. The claim is invariant; only the
tenant id inside it varies, so "Everyone except external users" is matched on
its `spo-grid-all-users` role manager segment rather than on the whole string.

**Report the tool's own label.** A detected principal is shown as "Everyone
except external users" whatever the tenant calls it, with the tenant's own name
beside it so a reader can recognise the row they will see in SharePoint. An
export then reads the same in any tenant.

**Read site access by derivation, and say so.** SharePoint's
`/_api/web/roleassignments` refuses an app-only `Sites.Read.All` token and Graph
has no web-permissions route, so the site's assignments are read through a list
that inherits: such a list has, by definition, exactly the site's permissions.
The list used is named in the response, because a derived answer that does not
say what it was derived from cannot be checked.

**When it cannot be derived, say that instead of showing nothing.** If every
list has unique permissions, or SharePoint did not report inheritance, the
response is `determined: false` with a reason. An empty access list would read
as "nobody has access", which is the most dangerous sentence this application
could produce.

## Consequences

Detection is only as good as the claim table, and the table is a fixed list. A
claim form not in it is missed silently — the same failure this ADR is about,
one level down. The table is small, in one file, and covered by tests naming
each claim, so adding one is a two-line change; that is the mitigation, not a
guarantee.

`scripts/add-everyone-grants.ps1` grants the claims at all three scopes and
prints both the claim and the tenant's display name for it, which is the VERIFY
`CLAUDE.md` requires before building on a SharePoint shape. It also prints the
Graph identity facet the claim arrives in, because which of `siteUser`, `user`
or `siteGroup` carries it was not certain when this was written — hence
`Classify` taking every identifier rather than one.

Site access costs one extra call per site, on a path already fetching the list
collection. It is read once when the site opens rather than per scan.

Deriving through an inheriting list means the answer is only as trustworthy as
that list's inheritance flag. Lists whose flag SharePoint did not report are
excluded rather than assumed to inherit, which is why the undetermined case
exists at all.
