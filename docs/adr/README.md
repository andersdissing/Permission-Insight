# Decisions

One file per decision. Record the reversed ones too — a settled question that
is not written down gets relitigated in an issue about six months later.

| ADR | Decision | Status |
| --- | --- | --- |
| [0001](0001-hosting-storage-account-and-function-app.md) | Storage account for the SPA plus a separate Function App, not Static Web Apps | Accepted |
| [0002](0002-backend-runtime-dotnet-isolated.md) | Backend is .NET 9 on the isolated worker | Accepted |
| [0003](0003-api-identifier-uri-is-a-parameter.md) | The API's identifier URI is a mandatory deployment parameter | Accepted |
| [0004](0004-app-role-grants-are-a-second-deployment.md) | Application role grants are a second deployment the script can fail gracefully | Accepted |
| [0005](0005-onedrive-export-deferred.md) | OneDrive is not an export destination in v1, and its delegated scope is removed | Accepted |
| [0006](0006-redirect-flow-not-popup.md) | Sign-in is a redirect in the same window, reversing the popup choice in `01` | Accepted |
| [0007](0007-no-batch-endpoint.md) | Effective permissions are not batched, because `/_api/$batch` is a POST | Accepted |
| [0008](0008-inbound-and-downstream-methods-are-separate.md) | Inbound and downstream method allow-lists are separate; inbound admits POST | Accepted |
| [0009](0009-no-full-control-escalation.md) | The application will not escalate to `Sites.FullControl.All`, closing the fallback `02` left open | Accepted |
| [0010](0010-hybrid-tenant-read-plus-site-grants.md) | Tenant-wide read stays read-only; permission reads need a per-site `Sites.Selected` grant | Superseded by 0011 |
| [0011](0011-permissions-are-read-through-graph.md) | Permissions are read through Graph, which accepts `Sites.Read.All` where SharePoint refuses it | Accepted |
| [0012](0012-group-membership-is-walked-not-resolved.md) | `GroupMember.Read.All` is taken and the group chain walked, reversing `02` | Accepted |
| [0013](0013-sharing-links-derived-from-scans.md) | Sharing links fall back to the library scans when site groups cannot be read | Accepted |
| [0014](0014-what-the-audit-log-may-record.md) | The audit log records who looked at which site, never what was inside it | Accepted |
| [0015](0015-tenant-wide-claims-and-site-level-access.md) | Tenant-wide claims are matched on the claim, never the localised name; site access is derived | Accepted |
| [0016](0016-response-headers-and-content-security-policy.md) | The API hardens every response; the SPA's policy is written at deploy time | Accepted |

Deferred work lives in [`../backlog.md`](../backlog.md), with enough context to
pick each item up without rediscovering why it was dropped.

Decisions already recorded in the specification rather than here, and equally
settled: no client secret and no Key Vault (`01-architecture.md`), application
permissions rather than delegated or on-behalf-of (`01-architecture.md`), one
app role rather than two (`02-permissions.md`), IndexedDB rather than
`localStorage` (`03-client-storage.md`).

`02-permissions.md` also refused `GroupMember.Read.All`. A real tenant reversed
that, and `0012` records why — read the ADRs before treating anything in the
specification about permissions as current.
