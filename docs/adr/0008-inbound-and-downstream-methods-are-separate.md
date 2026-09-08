# 0008 — Inbound and downstream HTTP methods are separate lists

Status: accepted, 2026-09-04.

## What changed

`HttpMethodPolicy` held one allow-list, `GET` and `HEAD`, applied both to
requests arriving from the browser and to requests leaving for SharePoint. It
now holds two:

- **Downstream:** `GET`, `HEAD`. Unchanged, and this is the rule that matters.
- **Inbound:** `GET`, `HEAD`, `POST`, `OPTIONS`.

## Why

The single list was stricter than the specification asks for, and the extra
strictness cost correctness rather than buying safety.

`CLAUDE.md` states the rule precisely:

> No `POST`, `PATCH`, `PUT` or `DELETE` against any SharePoint or Graph site
> endpoint.

That is a statement about the outbound edge. Applying it inbound as well was an
invention, and it had a visible consequence: recording an export had to be
`GET /api/audit/export`, a GET with a side effect, which is a request lying
about itself. `adr/0007` reasoned about the inbound list as though widening it
would weaken the downstream guarantee. It would not, and that reasoning was
wrong on this point — though `0007`'s conclusion still stands, because
`/_api/$batch` is a *downstream* POST and that list has not moved.

## What still holds

The guarantee the README, `SECURITY.md` and `CONTRIBUTING.md` all describe is
untouched: only `GET` and `HEAD` may carry the application identity, which can
read every site in the tenant.

Two things keep the separation honest:

- Nothing forwards a method. Handlers call typed client methods on
  `GraphClient` and `SharePointClient`; there is no generic proxy that could
  pass an inbound verb through to an outbound request. An inbound `POST` is an
  RPC shape and cannot become a SharePoint write.
- `ReadOnlyHttpClient` remains the only place an outbound request is built, and
  it checks the downstream list. A test asserts the downstream list contains
  exactly `GET` and `HEAD`, and another asserts that every method admitted
  inbound is still refused downstream.

`PUT`, `PATCH` and `DELETE` stay out of the inbound list too. Nothing needs
them, so an unexpected one arriving remains a signal rather than noise. A test
asserts the inbound list as well, so widening it is a visible change.

## Consequences

- `POST /api/audit/{action}` replaces the GET. It writes to the audit log and
  nothing else.
- A test asserts that `audit` is the only route declaring a non-read method, so
  a second one appearing is deliberate rather than accidental.
- The 405 message no longer claims the API is read-only, because it is not: the
  *application* is read-only with respect to SharePoint, which is a different
  and more accurate sentence.
