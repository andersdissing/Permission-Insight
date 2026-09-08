# 0002 — Backend is .NET 9 on the isolated worker

Status: accepted, 2026-09-03.

## Decision

The Function App runs .NET 9 on the isolated worker model.

## Why

The isolated model gives a real middleware pipeline
(`IFunctionsWorkerMiddleware`), and phase 1 requires that the role check "must
not be possible to bypass — enforce by construction, for example a single
router that wraps every handler, rather than by remembering to add a
decorator". A worker middleware runs before every function invocation with no
per-function opt-in, which is exactly that construction. The in-process model
has no equivalent, and a shared base class or an attribute would be the
decorator the specification warns against.

Two further things follow from the same requirement and are worth stating
because they are what makes the gate fail closed rather than fail open:

- Every HTTP trigger is registered at `AuthorizationLevel.Anonymous`. Function
  keys are deliberately not used, so there is never a second, weaker credential
  that could admit a request the role check would have refused.
- Handlers reach the caller only through `FunctionContext.GetCaller()`, which
  throws if the middleware did not populate it. A handler that somehow ran
  without the gate fails rather than serving data.

## Alternatives

TypeScript on the Node worker was the obvious alternative given the front end
is TypeScript, and would have meant one language and one dependency graph. It
was not chosen: token validation against Entra's signing keys, with issuer,
audience, version and lifetime checks, is a place where the well-trodden
library matters more than language uniformity, and
`Microsoft.IdentityModel.*` is that library. The rest of the backend is small
enough that the second language costs little.
