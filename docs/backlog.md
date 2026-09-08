# Backlog

Work deliberately left out of v1, with enough context to pick it up without
rediscovering why it was dropped. Items here are deferred, not rejected —
things that were considered and refused are in `02-permissions.md` under
"Deliberately not requested" and in `adr/`.

---

## Export to OneDrive

**Status:** deferred out of v1 on 2026-09-04. See
[`adr/0005`](adr/0005-onedrive-export-deferred.md).

Export currently offers one destination: a browser download. `09-export.md`
specifies a second, writing the CSV to the signed-in user's own OneDrive.

**Why it was worth having.** The advantage is governance, not security. A file
that lands in OneDrive sits inside the organisation's own retention policies
and sensitivity labels instead of a downloads folder. That is a real benefit
for the audience this tool is built for, and it is the reason to come back to
this rather than close it.

**What it needs, in order.**

1. Restore the delegated `Files.ReadWrite` scope to the app registration's
   `requiredResourceAccess` in `infra/main.bicep`. It was removed when this was
   deferred, because a declared write scope with no code path behind it is
   exactly what `02-permissions.md` exists to prevent. The scope id is resolved
   from the Microsoft Graph service principal by name rather than hardcoded;
   the removed code is in the history of that file.
2. Acquire it **incrementally**, at the moment the user picks OneDrive, never
   at sign-in. Asking for it up front makes the consent screen heavier for
   every user including the majority who never export.
3. Never `Files.ReadWrite.All` as an application permission. That grants write
   access to every user's files in the tenant, which is a far larger capability
   than the feature needs and worse than the problem it solves. A delegated
   token is bounded to the signed-in user's own drive by definition.
4. Call `disableCloseGuard()` before any consent redirect. MSAL popup flow
   avoids the problem, and popup is what `auth/msal.ts` uses, but a future
   change to redirect flow would hit the browser's leave-site warning in the
   middle of an action the user started.
5. Keep download as the default. An application that works fully without any
   write scope is easier to defend, and this is the only write anywhere in the
   product.
6. Log the destination. `ApiClient.recordExport` already takes one and
   currently always receives `download`.

**Acceptance**, carried over from `09-export.md`: consent is requested only on
first use, and the file appears in the signed-in user's own drive.

---

## External sharing badge (`SharingCapability` per site)

**Status:** out of scope for v1, recorded in `README.md` and
`02-permissions.md`.

Showing whether a site permits external sharing needs the SharePoint admin API,
which escalates the app registration into tenant administration. That is a much
harder internal approval to win for one badge, and it would widen the identity
that already holds tenant-wide read.

Revisit only if the badge turns out to be load-bearing for the governance team,
and expect to justify the escalation on its own terms rather than as an
extension of the existing permissions.

---

## A second app role for Search for person

**Status:** conditional. Not needed unless the compensating control proves
insufficient.

Search for person profiles a named individual's document access, and any
authorised user can run it on any colleague. It runs under the single
`PermissionInsight.Use` role by decision, with separate audit logging as the
compensating control.

If that ever feels insufficient, a second role is the remedy. Note the cost
before starting: adding one later means touching every existing assignment.

---

## Continuous Access Evaluation

**Status:** accepted risk, not planned.

Removing a user from `sg-permission-insight-users` does not invalidate a token
already issued; access ends when the token expires, typically within an hour.
CAE would close that window.

Accepted for an internal read-only tool with a small user group. **This
judgement changes entirely if the application ever gains write access** — if
that is ever proposed, this item stops being backlog and becomes a
prerequisite.
