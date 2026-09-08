# 0004 — Application role grants are a second deployment

Status: accepted, 2026-09-03. Refines the "Admin consent" section of
`01-architecture.md` and the same section of
`phase-1-deployment-and-search.md`.

## The correction

Both documents say that if the deploying identity is not privileged enough, the
deployment "must not fail silently: it emits the tenant-scoped consent URL as
an output":

```
https://login.microsoftonline.com/{tenantId}/adminconsent?client_id={clientId}
```

That URL is still emitted, but it does **not** solve the case it was written
for. The `adminconsent` endpoint grants consent for permissions declared on an
*app registration*. The permissions that matter here —
`Sites.Read.All` and `User.ReadBasic.All` on Microsoft Graph, and
`Sites.Read.All` on SharePoint — belong to the **managed identity's** service
principal, which has no consent screen. They exist only as
`appRoleAssignedTo` objects created through Graph, and a Global Administrator
opening a URL will not create them.

Sending an administrator to that URL when the grants failed would leave them
believing the problem was fixed while the backend still returns 403 on every
SharePoint call. That is precisely the silent failure the specification is
trying to prevent, so the mechanism has to change rather than the intent.

## Decision

Split the infrastructure into two deployments, sequenced by
`scripts/deploy.ps1`:

1. `infra/main.bicep` — everything that any contributor with resource group
   rights can deploy: Azure resources, the app registration, the service
   principal, the security group, and the group's assignment to the
   `PermissionInsight.Use` app role.
2. `infra/app-roles.bicep` — only the three application role assignments on the
   managed identity's service principal. This is the part that needs Privileged
   Role Administrator or Global Administrator.

The script runs the second deployment inside a `try`. On failure it prints the
exact Microsoft Graph PowerShell an administrator has to run, with the object
ids already filled in from the first deployment's outputs, and exits non-zero
so nobody mistakes it for success. The `adminconsent` URL is printed too, for
pre-consenting the application's own `access_as_user` scope tenant-wide,
labelled with what it does and does not cover. (It covered the delegated
`Files.ReadWrite` scope too until [`0005`](0005-onedrive-export-deferred.md)
removed that scope.)

## Consequences

- The failure is loud, actionable, and hands over a command that can be pasted
  rather than a screen someone has to navigate.
- The privileged step is isolated in one small template, so a reviewer reading
  the pull request can see at a glance whether a change touches tenant-wide
  permissions. `az deployment group what-if` in CI covers both templates for
  the same reason.
- Redeploying the first template is safe for anyone with resource group rights.
  Only a permission change needs a privileged account, and only then.
