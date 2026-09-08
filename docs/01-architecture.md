# 01 — Architecture

## The decision that has to be made first

The requirement says "SPA hosted in an Azure Storage account". A storage account
static website serves files and runs nothing. That conflicts with the permission
model, which the rest of this specification depends on.

The application must let **any authorised user search and scan any site in the
tenant**, regardless of that user's own SharePoint rights. That requires
Microsoft Graph and SharePoint *application* permissions, which no browser can
hold.

Two ways to avoid a backend were considered and rejected.

**Pure delegated access**, where the SPA calls Graph directly with the user's
token, needs no backend and no credential at all. It also security-trims every
result to what that user can already see, which contradicts the requirement
above and produces an audit with silent holes for anyone who is not a SharePoint
administrator.

**On-behalf-of flow**, where a backend exchanges the user's token for a Graph
token in the user's name, looks like a middle path and is the worst of the
three. The exchange still requires a confidential client with a credential, and
the resulting token is still delegated and still trimmed. It pays the cost of a
backend and buys none of the benefit.

So: application permissions, held by a backend. But the backend does not need a
secret. See "Credentials" below.

Three ways forward:

**A. Storage account for the SPA, plus a Function App for the API.** Keeps the
stated hosting for the front end. Two resources, one CORS configuration, one
custom domain if wanted. This is what the rest of the specification assumes.

**B. Azure Static Web Apps.** Static hosting and managed Functions in one
resource, with built-in Entra authentication and no CORS to configure. Fewer
moving parts than A. Choose this unless something specific requires a plain
storage account.

**C. Delegated permissions only, no backend.** Genuinely a pure SPA in a storage
account, and genuinely cheaper. Only viable if the audience is SharePoint
administrators, and it reverses the decision that every app user can search all
sites.

**Pick one before starting phase 1.** Nothing else in this specification changes
between A and B. Choosing C changes `02-permissions.md` substantially and
removes the backend from every phase.

> **Resolved: A.** Static Web Apps was rejected on one technical fact — its
> managed functions cannot be assigned a user-assigned managed identity, and
> the credential design below depends on one. See
> [`adr/0001`](adr/0001-hosting-storage-account-and-function-app.md).

## Runtime shape (assuming A or B)

```
Browser (SPA)
  │  MSAL: sign in against the app registration, acquire a token for the API
  │  Bearer token on every call
  ▼
Backend API (Azure Functions)
  │  Validates the token: signature, audience, issuer, and the app role claim
  │  Acquires an app-only token using its own user-assigned managed identity
  │  Proxies read-only requests, rejects all writes
  ▼
Microsoft Graph  +  SharePoint REST
```

Note that two separate identities are in play, and keeping them separate is the
point. The **app registration** handles user sign-in and carries nothing
dangerous. The **managed identity** carries the tenant-wide read permission and
has no credential that can be copied out of Azure.

The backend is deliberately thin. It does not aggregate, cache or transform
beyond what is needed to keep credentials server-side and to enforce the
read-only rule. Scan orchestration, progress tracking and all caching happen in
the browser, because the results belong to the user's session and are stored in
the user's browser.

### Why the backend still has to enforce authorisation

The SPA hides buttons the user is not entitled to. That is a convenience, not a
control. Anyone can call the backend directly with a token they legitimately
hold. The role check therefore belongs in a shared middleware that runs before
every handler, and a handler must not be reachable without it.

### Why the backend must block writes structurally

The application identity is powerful. A bug or a future feature could turn a
read tool into a write tool without anyone noticing. Implement the proxy so that
the HTTP method is validated against an allow-list before the request is
forwarded, and so that adding a write path requires deliberately editing that
allow-list.

## Sign-in

Sign-in is automatic. If there is no valid account, the SPA triggers MSAL
sign-in on load rather than showing a sign-in button. There is no anonymous
state to design.

Use **popup flow**, not redirect. The application installs a `beforeunload`
guard while cached scans exist (see `03-client-storage.md`), and a redirect
would trigger the browser's leave-site warning in the middle of an action the
user started. Popup flow avoids the problem instead of working around it. If
redirect flow is chosen anyway, the guard must be removed before every
application-initiated navigation.

> **Reversed: redirect flow, in the same window.** The last sentence above is
> now the rule the code follows — `auth/msal.ts` disables the guard before each
> of its three redirects. See
> [`adr/0006`](adr/0006-redirect-flow-not-popup.md).

Sign-out clears the MSAL cache. It deliberately does **not** clear cached scans.
See `03-client-storage.md` for the consequence and how it is communicated.

## Deployment

Everything is Bicep, including the Entra objects. Bicep support for Microsoft
Entra ID resources went generally available on 29 July 2025 through the
Microsoft Graph Bicep extension, which covers `Microsoft.Graph/applications`,
`Microsoft.Graph/servicePrincipals`, `Microsoft.Graph/groups` and
`Microsoft.Graph/appRoleAssignedTo`.

Two notes that will otherwise cost time:

- The built-in extension is retired. Use dynamic types, referenced in
  `bicepconfig.json` as
  `br:mcr.microsoft.com/bicep/extensions/microsoftgraph/v1.0:<version>`. Check
  the current version in the Microsoft Artifact Registry.
- The deploying identity is far more privileged than the application. Granting
  an application permission means creating an app role assignment on the
  Microsoft Graph service principal, which needs Privileged Role Administrator
  or Global Administrator. This is not something to run from an ordinary
  pipeline identity. Say so in the deployment runbook.

### Admin consent

Attempt it in Bicep via `Microsoft.Graph/appRoleAssignedTo` for the application
permissions. If the deployment identity is not privileged enough, the deployment
must not fail silently: it emits the tenant-scoped consent URL as an output.

```
https://login.microsoftonline.com/{tenantId}/adminconsent?client_id={clientId}
```

The deployment runbook prints this URL with a one-line instruction. A Global
Administrator opens it once and grants consent for the tenant.

## Credentials

**There is no client secret and no Key Vault.** Both were in an earlier draft
and both are unnecessary.

The Function App is assigned a **user-assigned managed identity**. That
identity's service principal is granted the application permissions directly
through app role assignments, and the backend calls Graph and SharePoint with
its own managed identity token. Nothing needs to be stored, rotated or
protected, because there is no credential material outside Azure.

Use a user-assigned identity rather than a system-assigned one. It survives
recreating the function app, and it can be granted permissions before the
compute exists, which keeps the Bicep deployment ordering simple.

`VERIFY` that a managed identity token is accepted by the SharePoint REST API
for the `https://{tenant}.sharepoint.com` resource, and that the SharePoint
`Sites.Read.All` app role can be assigned to a managed identity's service
principal. If SharePoint's own API declines managed identity tokens, fall back
to the federated identity credential pattern below rather than to a secret.

**Fallback: federated identity credential.** Managed identities as federated
credentials for Entra apps are generally available, so the app registration can
be configured to trust the managed identity and the backend exchanges a managed
identity token for an app-only token on the registration. Configure it as the
`Managed Identity` federated credential scenario. Still no secret. An
application can hold at most 20 federated identity credentials, which is not a
constraint here.

A client secret is not an acceptable outcome of this decision. If both options
above fail, stop and revisit the architecture rather than reintroducing one.

## Resources

| Resource | Purpose |
| --- | --- |
| Storage account (static website) or Static Web App | Hosts the SPA |
| Function App | Backend API |
| User-assigned managed identity | The backend's identity for Graph and SharePoint |
| Application Insights | Audit log sink |
| Entra application registration | User sign-in, the API audience, the app role |
| Entra service principal | With `appRoleAssignmentRequired` set to `true` |
| Entra security group | Members get the app role, provisioned by Bicep |
| App role assignments | Group to app role; managed identity to Graph and SharePoint roles |
