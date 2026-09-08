# Permission Insight

Internal governance tool. Reports broken permission inheritance and sharing
links in SharePoint Online.

## Shape

Single-page application plus a thin backend API. The backend exists only
because the application holds Microsoft Graph and SharePoint **application**
permissions, which require a confidential client. No secret ever reaches the
browser.

The backend is a proxy and a token holder. It has no database. Everything the
user sees is either fetched live or read from IndexedDB in their own browser.

## Non-negotiable rules

- **The application never writes to SharePoint.** No POST, PATCH, PUT or DELETE
  against any SharePoint or Graph site endpoint. Enforce this in the backend's
  proxy layer, not by convention. The one exception is writing an export file to
  the signed-in user's own OneDrive, which uses a delegated token, never the
  application identity. **That exception is currently deferred** — OneDrive
  export is in `docs/backlog.md`, the delegated scope is not declared, and the
  application therefore holds no write permission at all.
- **Validate the app role claim on every API request**, not only at sign-in. The
  SPA's route guard is cosmetic.
- **Log every search, every scan and every person lookup** with user object id,
  timestamp and target. This log is the only audit trail; the backend holds no
  other record of what was accessed.
- **User-facing strings are English.** Sentence case. No terminal punctuation on
  buttons and labels. Use contractions.
- **Never invent a SharePoint API shape.** If an endpoint or property is
  uncertain, write a probe script and check it against the test tenant before
  building on it. Several are flagged `VERIFY` in these documents.

## Stack

- Frontend: SPA, MSAL browser for sign-in, `idb` for IndexedDB.
- Backend: Azure Functions (HTTP triggers), one function app.
- Infrastructure: Bicep, including the Microsoft Graph Bicep extension for the
  Entra objects.

## Key domain facts

These are established, not assumptions. They shape most of the code.

- `HasUniqueRoleAssignments` describes only the object it sits on. A library
  that inherits can contain thousands of broken items. There is no cheap way to
  ask "does this library contain any broken items" — you enumerate.
- The REST endpoint `/_api/web/lists(guid'...')/items` is **flat**. It returns
  every item at every folder depth, with folders appearing as items where
  `FileSystemObjectType` is `1`. Folder depth never requires recursion.
- `Get-PnPListItem`-style per-item property loading is 10,000 round trips and
  will be throttled. Always select `HasUniqueRoleAssignments` in the OData
  `$select` and page with `odata.nextLink`.
- Every sharing link breaks inheritance and creates a hidden site group named
  `SharingLinks.{itemGuid}.{kind}.{linkGuid}`. So sharing links are a strict
  subset of items with unique permissions. The one exception is a link with
  scope `existingAccess`, which changes no ACL and creates no group.
- Site groups are site-collection scoped. One call to `/_api/web/sitegroups`
  covers subsites too.
- Modern SharePoint almost always creates `Flexible` links. The `kind` segment
  of the group name therefore tells you a link exists but not its scope. Scope,
  expiry and recipients live in the permission object.
- SharePoint grants `Limited Access` upward automatically so a recipient can
  navigate to a shared item. These role assignments are noise. Filter them out
  everywhere.
- When inheritance is broken through the SharePoint interface, existing
  assignments are copied by default. A broken library is not by itself a
  finding; it is a point where two permission sets start to drift apart.
