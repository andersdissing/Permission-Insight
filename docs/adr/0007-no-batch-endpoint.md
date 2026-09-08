# 0007 — Effective permissions are not batched

Status: accepted, 2026-09-04. **Declines** the batching instruction in
`phase-5-person-search.md`.

## The conflict

Two documents give incompatible instructions, and both are emphatic.

`phase-5-person-search.md`, on cost:

> The saving is in batching, not in reducing the set. Pack the calls into
> `/_api/$batch`, which turns 89 logical calls into two or three HTTP
> requests.

`CLAUDE.md`, on the rule the whole design rests on:

> **The application never writes to SharePoint.** No POST, PATCH, PUT or DELETE
> against any SharePoint or Graph site endpoint. Enforce this in the backend's
> proxy layer, not by convention.

`/_api/$batch` is a POST. Every operation inside the body is a GET, but the
request that carries them is not.

## Decision

No batching. `getusereffectivepermissions` is called once per permission root,
as an ordinary GET, with eight in flight at a time.

## Why this way round

The read-only rule is the load-bearing one. It is what lets the README promise
that an identity holding tenant-wide read cannot damage a tenant, it is what
`SECURITY.md` names as critical, and it is enforced structurally in
`HttpMethodPolicy` precisely so that it does not depend on anyone's judgement
later. Batching is a performance optimisation.

Allowing POST to one path would change the enforcement from "no write method
leaves this process" to "no write method leaves this process except to an
endpoint whose body we believe contains only reads". The second is not a rule a
reviewer can check by reading one short file, and verifying that a multipart
batch body really does contain only GETs is more code, and more trust, than the
saving is worth.

## What it costs

One request per root instead of one per twenty or so. For the specification's
own example — a site with 84 broken items and four libraries, so 89 roots —
that is 89 downstream calls rather than three.

They are server-side, concurrent and fast, and the feature already presents
itself as a computation rather than a lookup: it has a progress bar with the
permission roots as its denominator, and the primary action is named "Check
access" rather than "Search" for exactly this reason. The user-visible
difference is small.

The real risk is throttling on a large site, which the existing `Retry-After`
handling absorbs and reports rather than hides.

## When to revisit

If a site with thousands of broken items makes this unusable, the options in
order of preference are: reduce the root set (there is no obvious slack — items
that inherit are redundant by definition, and the site and libraries are the
baseline that answers for everything inheriting); cache masks per principal
across libraries; and only then reopen the batching question, which would need
a security review of its own and an amendment to `CLAUDE.md` rather than a
quiet widening of the allow-list.
