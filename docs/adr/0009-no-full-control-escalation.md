# 0009 — The application will not escalate to `Sites.FullControl.All`

Status: accepted, 2026-09-04. **Closes** the fallback that
`02-permissions.md` and `phase-1-deployment-and-search.md` left open.

## Decision

The application's identity holds `Sites.Read.All` and never more.
`Sites.FullControl.All` is ruled out — not "requires renewed approval", but
ruled out. If a feature cannot be built on read, the feature does not get
built that way.

This governs the **application**: the user-assigned managed identity the
backend authenticates as. It does not govern tooling that provisions test
data, which is covered below.

## What the specification said, and why this changes it

`02-permissions.md` flagged an open item: whether `Sites.Read.All` is enough
to read `roleassignments` and to call `getusereffectivepermissions`, both of
which are associated with the `ManagePermissions` right. It then said:

> If the test fails, the fallback is `Sites.FullControl.All`, which grants
> write and delete across the tenant. **That escalation requires renewed
> security approval and is not a default.**

Leaving that sentence in place makes the escalation a documented option, and a
documented option is one somebody eventually takes when a probe comes back
unhelpful on a Friday afternoon. The whole argument of this project — the
README's first screen, `SECURITY.md`, the structural enforcement in
`HttpMethodPolicy` — is that an identity able to read every site in the tenant
must not also be able to write to it. Granting `Sites.FullControl.All` would
make the read-only enforcement in the proxy the *only* thing preventing a
tenant-wide write capability, rather than a second line behind a permission
that cannot express one.

## What happens if the probe says read is not enough

There is no escalation path, so the honest answers are narrower:

- Establish first whether it genuinely fails. `scripts/probe-verify.ps1`
  answers it app-only; an interactive run does not, because it inherits the
  operator's own rights and will succeed regardless.
- If reading `roleassignments` app-only is refused, the library scan cannot
  report *who* has access on a broken item. It can still report *that*
  inheritance is broken, from `HasUniqueRoleAssignments`, which is the cheap
  half of the scan and needs no extra right.
- If `getusereffectivepermissions` is refused app-only, Search for person does
  not work as designed. Expanding groups client-side is not a substitute — it
  cannot see through nested Entra groups, which is the case the feature exists
  for, and `GroupMember.Read.All` is refused for the same reason it always was.
- A delegated token for those specific reads is not a fix either. It
  security-trims to the signed-in user, which reintroduces exactly the silent
  holes `01-architecture.md` rejected delegated access to avoid.

Shipping a narrower tool that is honest about what it cannot see is preferable
to shipping a broader one that can delete a tenant.

## Tooling is a separate question

`scripts/provision-test-data.ps1` creates sites, breaks inheritance and makes
sharing links. That is writing, unavoidably, and it is fine:

- It runs as an operator, interactively, against a **test** tenant.
- It uses its own throwaway app registration (`Register-PnPEntraIDAppForInteractiveLogin`
  or equivalent) holding **delegated** `AllSites.FullControl` — delegated, so
  bounded by the operator's own rights, and unrelated to the application.
- The application never uses it, and the registration should be deleted once
  the test data exists.

The rule is about the identity Permission Insight itself runs as. Provisioning
tooling is not that identity and is not covered by it.
