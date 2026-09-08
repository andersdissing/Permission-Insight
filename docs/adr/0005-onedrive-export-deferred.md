# 0005 — OneDrive is not an export destination in v1

Status: accepted, 2026-09-04. Narrows the "Two destinations" section of
`09-export.md`.

## Decision

Export writes a browser download and nothing else. The OneDrive destination
moves to [`../backlog.md`](../backlog.md).

The delegated `Files.ReadWrite` scope is **removed from the app registration**
at the same time, rather than left declared against a feature that no longer
exists.

## Why remove the scope rather than leave it

Leaving it costs nothing at runtime: a declared delegated scope that is never
requested and never consented grants no access. The reason to remove it anyway
is that this repository is public, and `02-permissions.md` opens by saying it
exists "because a security reviewer will ask why a reporting tool needs
tenant-wide read access". Every permission in that document is justified by a
specific feature.

A write scope with no code path behind it is the first crack in that argument.
It invites the reviewer to wonder what else is declared speculatively, and it
sets the precedent that permissions can sit on the registration ahead of the
features that need them — which is how registrations quietly accumulate more
than they can account for.

Restoring it is step one of the backlog item, and the scope id is resolved from
the Microsoft Graph service principal by name rather than hardcoded, so
restoring it is a copy back rather than a lookup.

## Consequences

- Export needs no write permission at all. The application now works fully
  without any write scope anywhere, which was already the argument for making
  download the default and is now simply true of the whole product.
- The admin consent URL that `deploy.ps1` prints no longer covers
  `Files.ReadWrite`. It still has a purpose — pre-consenting this
  application's own `access_as_user` scope so nobody meets a consent prompt on
  first sign-in — and its wording says so.
- `09-export.md` keeps its OneDrive section. The specification records what was
  wanted; this record and the backlog say what was built and why the rest
  waits.
- Nothing in the export code changes. `ApiClient.recordExport` already takes a
  destination and always receives `download`, which is what the audit log
  should say.
