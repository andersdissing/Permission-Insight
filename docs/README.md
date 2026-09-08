# Permission Insight — requirements specification

An internal web application that reports on broken permission inheritance and
sharing links in SharePoint Online, so a governance team can find and clean up
oversharing.

## How to use these documents with Claude Code

Each `phase-*` document is a self-contained work package. Build them in order.
Each one ends with an **Acceptance** section you can verify before starting the
next. Hand Claude Code one phase document at a time — not the whole folder — and
keep `CLAUDE.md` in the repository root so it is loaded automatically.

Read these first, in this order:

| File | What it covers |
| --- | --- |
| `CLAUDE.md` | Project context Claude Code loads on every session |
| `01-architecture.md` | Hosting, runtime shape, credentials, the one unresolved decision |
| `02-permissions.md` | Every Entra permission, why it exists, what breaks without it |
| `03-client-storage.md` | IndexedDB cache, the toggle, the clear button |
| `04-open-source-repository.md` | GitHub repository, licence, CI, what must never be committed |
| `adr/` | Decisions taken while building, including the hosting choice `01` left open. **Read `0011` and `0012` before `02-permissions.md`** — a real tenant corrected the permission model twice |
| `backlog.md` | Work deferred out of v1, and why |
| `setup-guide.md` | **Start here to install it.** Prerequisites, deployment, assigning access, and how to read the audit log |
| `runbook-deployment.md` | Deployment detail and the failure modes, including the superseded per-site grant design |

Then build, in this order:

| File | Deliverable | Testable on its own |
| --- | --- | --- |
| `phase-1-deployment-and-search.md` | Hosting, SSO, app role, site search | Yes — this is the milestone that proves deployment and SharePoint access |
| `phase-2-sharing-links.md` | Sharing links tab | Yes |
| `phase-3-library-scan.md` | Lists and libraries tab, per-library scan | Yes |
| `phase-4-tree-and-panel.md` | Folder tree and detail panel | Yes |
| `phase-5-person-search.md` | Search for person | Yes |
| `09-export.md` | CSV download; OneDrive is deferred to `backlog.md` | Cuts across phases 2–5 |

Provision test data before phase 2. See `10-test-data.md` and
`scripts/provision-test-data.ps1`.

## Scope

In scope: reading and reporting. The application never writes to SharePoint.
Remediation happens in SharePoint's own interface, which the application links
to.

Out of scope for v1, recorded so the reasons are not relitigated:

- `SharingCapability` per site (external sharing badge). Needs the SharePoint
  admin API, which escalates the app registration beyond read. Backlog.
- Expanding Entra group membership. Resolved instead by asking SharePoint for
  effective permissions. See `02-permissions.md`.
- Continuous Access Evaluation. A blocked user keeps access until the token
  expires, typically under an hour. Accepted.
- `Sites.Selected`. Incompatible with tenant-wide search, and unnecessary once
  permissions were read through Graph. Assigned briefly, then removed — see
  `setup-guide.md`.
- Server-side scan history or a central database. All cached data lives in the
  user's browser.

## Language

All user-facing strings are English, including in a Danish tenant. SharePoint
site, list and file names are data and appear in whatever language they exist.
