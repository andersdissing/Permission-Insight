# 10 — Test data

A dedicated test site with enough volume and enough messiness that paging,
progress, throttling and the tree are all exercised. Provision it before phase
2. The script is `scripts/provision-test-data.ps1`.

Use a non-production tenant. If that is not possible, use a site that no one
relies on and can be deleted afterwards.

## What gets created

**Site**: a communication or team site, for example `/sites/pi-testdata`.

| Container | Type | Items | Structure |
| --- | --- | --- | --- |
| `Documents` | Document library | 12,000 | Nested folders, 3 levels |
| `FlatList` | Generic list | 7,000 | Flat, no folders |
| `SmallLib` | Document library | 500 | Folders and files |
| `MediumLib` | Document library | 1,000 | Folders and files |

Counts include folders. All four carry unique permissions and sharing links.

The four sizes are chosen to cross every size badge boundary: 500 and 1,000 are
Small, 7,000 is Large, 12,000 is Extra large. Nothing lands in Medium, so add a
`MediumLib` variant at 3,000 items if that badge needs coverage.

`Documents` at 12,000 items also crosses the 5,000 list view threshold, which is
the case that breaks naive implementations.

## Folder structure in `Documents`

Three levels, roughly 120 folders and 11,880 files, so the tree has something to
prune. Include deliberately:

- At least one folder five levels deep containing a single file with unique
  permissions, so filtered mode has a long ancestor chain to render.
- At least one **empty folder** with no files, so `Empty - no files` can be
  tested.
- At least one folder containing only subfolders.
- One folder with 800 inheriting files and no findings, so pruning is visible.

## Permission scenarios

Keep the number of unique permissions modest — around 200 across all containers.
Microsoft's guidance is to stay under 5,000 unique permissions per list, and a
pathological test site is harder to reason about, not more useful. Raise it
deliberately if you want to test the limits.

Create at least one of each:

- A folder with a **direct grant** to a test user and no sharing link.
- A folder with a direct grant to a **SharePoint group** that contains an Entra
  group, so the "Members not shown" row can be tested.
- A file with an **anonymous** sharing link, no expiry. Requires the tenant to
  permit Anyone links; if it does not, record that this scenario is untestable
  here rather than silently skipping it.
- A file with an **organisation** sharing link.
- A file with a **specific people** link to an external guest, with an expiry
  date.
- A file with **both** a direct grant and a sharing link, so classification is
  exercised.
- A **broken library**, where inheritance is broken at the library itself, so
  the library-level finding renders.
- One container with **zero findings**, so the "Everything inherits" empty state
  can be tested.

**An orphaned sharing link group**: share a file, then delete the file and empty
it from the recycle bin. This leaves the group behind and is the only way to
test the orphaned path.

### Organisation-wide claims

These need their own section because they are the only scenario that tests a
scope rather than an object, and the only one whose correctness depends on the
tenant's language.

- **"Everyone except external users" at the site.** The important one. It
  breaks no inheritance, so a scan of every item in every library finds nothing
  while the whole site is readable by every employee. If the site banner does
  not report this, the tool is failing at its widest case.
- **"Everyone except external users" on a library** that already has unique
  permissions, so the claim and an ordinary grant appear together.
- **"Everyone" on a folder.** The wider claim: it includes guests. Modern
  people pickers hide it, so a tenant that has one nearly always acquired it
  from a legacy or scripted grant.

`scripts/add-everyone-grants.ps1` creates all three without re-running the full
provision, and prints the claim beside the display name this tenant renders it
with. **Check that those two differ if your tenant is not in English** — that
mismatch is the entire reason the code matches on claims. See
[`adr/0015`](adr/0015-tenant-wide-claims-and-site-level-access.md).

Deriving site-level access needs at least one list that still inherits. A site
where every list has unique permissions is worth testing too: the tool must
report that as undetermined, never as an empty access list.

## Before you run it

PnP.PowerShell 2.0 and later dropped the multi-tenant app registration they
used to sign in with, so an interactive connection needs a client id from an
app registration in your own tenant. Create one once:

```powershell
Register-PnPEntraIDAppForInteractiveLogin `
    -ApplicationName 'PnP PowerShell' `
    -Tenant {tenant}.onmicrosoft.com `
    -Interactive
```

Pass the client id it prints as `-PnPClientId`, or set
`PNPPOWERSHELL_CLIENTID`. Allow a minute or two after registering before the
app is usable. Without it the connection fails with "Specified method is not
supported", which does not hint at the cause.

## Test users

Two users, created with Microsoft Graph PowerShell. Creating them needs
`User.ReadWrite.All`, which the provisioning operator holds, not the
application.

| User | Purpose |
| --- | --- |
| `pi-test-alpha` | Named directly on items. Person search must find these. |
| `pi-test-beta` | Access only via an Entra group nested in a SharePoint group. This is the user that proves `getUserEffectivePermissions` works where group expansion would not. |

`pi-test-beta` is the important one. It is the only way to verify the central
claim of phase 5.

Also create one Entra security group containing `pi-test-beta`, and add that
group to a SharePoint group on the site.

## Runtime expectations

Creating 12,000 files means 12,000 uploads. `Add-PnPFile` cannot be batched,
because file upload is not a list item operation. Expect roughly 25 to 70
minutes depending on throttling. The list items in `FlatList` are far faster,
because `Add-PnPListItem -Batch` genuinely batches.

The script is therefore **resumable**: it checks what already exists and creates
only what is missing, so an interrupted run can simply be started again.

Run it well before you need it.

## Repository hygiene

The script must not contain tenant URLs, real user names or real email addresses
as defaults. Every one is a mandatory parameter. See
`04-open-source-repository.md`.
