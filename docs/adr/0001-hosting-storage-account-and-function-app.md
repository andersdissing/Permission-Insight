# 0001 — Storage account for the SPA plus a separate Function App

Status: accepted, 2026-09-03. Resolves the open decision in
`01-architecture.md`, which listed options A, B and C and required one to be
picked before phase 1.

## Decision

**Option A.** The SPA is served from a storage account static website. The
backend is a separate Azure Functions app on a Flex Consumption plan, with a
user-assigned managed identity.

## Why not B, Static Web Apps

Static Web Apps looked like the better option in `01-architecture.md` — one
resource, no CORS, built-in authentication — and it was rejected on a single
technical fact: **managed functions in Static Web Apps cannot be assigned a
user-assigned managed identity.** They run in a Microsoft-managed subscription
that the customer does not control identity for.

The whole credential design depends on that identity. Without it the backend
would need a client secret or a federated credential chain rooted in something
else, and `01-architecture.md` is explicit that a client secret is not an
acceptable outcome. The alternative inside Static Web Apps is a *linked* backend
— bring your own Function App — which is option A with an extra resource in
front of it.

The built-in Entra authentication would also not have earned its keep. The
application validates the app role claim itself on every request, because the
role check is the access control model and must live in code we can read and
test. Static Web Apps' auth would sit in front of that, not replace it.

## Why not C, delegated permissions and no backend

Option C reverses the requirement that any authorised user can search and scan
any site. It would security-trim every result to what the signed-in user can
already see, which produces an audit with silent holes and no way to tell "no
findings" from "no access". That is the failure mode the tool exists to
prevent.

## Consequences

- Two resources and one CORS configuration. The Function App's
  `cors.allowedOrigins` is set to the static site origin by the deployment.
- Enabling the static website on a storage account is a data-plane operation
  that ARM cannot express, so `scripts/deploy.ps1` does it with the Azure CLI
  after the Bicep deployment. This is the reason there is a deployment script
  at all rather than a bare `az deployment group create`.
- The front end reads `config.json` at boot rather than baking the tenant id
  and client id in at build time. The deployment writes that file from the
  Bicep outputs, which keeps tenant-specific values out of the repository and
  means a second tenant does not need a second build.
- `AzureWebJobsStorage` uses an identity-based connection rather than a
  connection string, so the no-credential rule holds for the platform's own
  storage access too, not just for ours.

Nothing else in the specification changes. Moving to a linked backend under
Static Web Apps later would change hosting and CORS and touch nothing else.
