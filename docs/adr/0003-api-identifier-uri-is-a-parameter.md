# 0003 — The API's identifier URI is a mandatory deployment parameter

Status: accepted, 2026-09-03.

## The problem

The SPA acquires a token for the backend by requesting a scope such as
`api://.../access_as_user`. Entra can only resolve that scope if the string in
front of it is one of the application's `identifierUris`.

The usual value is `api://{appId}`, and it cannot be written in Bicep. The
application's `appId` is assigned by Entra when the resource is created, and a
resource cannot reference its own output as one of its own inputs. Declaring
the application twice — once to create it, once to update it with the now-known
`appId` — risks the second declaration resetting properties the first one set,
because these resources are declarative rather than patch semantics.

## Decision

`apiIdentifierUri` is a **mandatory Bicep parameter with no default**. The
deployer supplies a URI under a domain the tenant has already verified, for
example:

```
api://yourtenant.onmicrosoft.com/permission-insight
```

Every tenant has `{tenant}.onmicrosoft.com` verified, so this needs no
preparation. The deployment writes the matching scope into the front end's
`config.json`, so the two never drift.

## Consequences

- The parameter carries tenant-specific data and therefore has no default,
  which is what `04-open-source-repository.md` requires anyway: a deployment
  fails rather than silently targeting whatever the author was testing against.
- The backend accepts **either** the identifier URI or the application's client
  id as the token audience. Entra issues v2 tokens with `aud` set to the client
  id, and pinning only the URI would reject every real token.
- Everything stays in Bicep. The rejected alternative was a post-deployment
  `az ad app update --identifier-uris`, which would have moved a security-
  relevant property of the app registration out of the template and into a
  script step someone could skip.
