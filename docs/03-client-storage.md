# 03 — Client storage

All scan results live in the user's browser. There is no server-side database
and no scan history. This was chosen deliberately: a stored report is a complete
index of an organisation's most exposed documents, and not keeping one centrally
is a security benefit rather than an omission.

## IndexedDB, not localStorage

Use IndexedDB through the `idb` wrapper. The raw API is verbose enough that
hand-rolling it invites bugs in the upgrade path.

`localStorage` was the first choice and was rejected for four reasons:

- **Quota.** Five megabytes, hard. A single library tree of 12,000 items with
  permissions and links approaches that on its own, before any other site is
  cached.
- **Synchronous API.** Writing during a scan blocks the main thread, so the
  progress bar visibly stutters at exactly the moment the user is watching it.
- **Serialisation.** `JSON.parse` on several megabytes is a perceptible freeze
  on load. IndexedDB stores structured objects directly.
- **No indexes.** Search for person needs lookups across every cached library.
  With `localStorage` that means deserialising everything on each query.

What does **not** improve: security. Both stores are origin-scoped, unencrypted
and readable by anyone with access to the browser profile. A database is not
safer than a key-value list. Removing the quota ceiling removes the accidental
limit that was keeping the volume of sensitive data on the workstation small, so
the clear button and the information text matter more after this change, not
less.

## Schema

Database `permission-insight`, one object store per concern.

| Store | Key | Value |
| --- | --- | --- |
| `scans` | `{oid}:{siteId}:{listId}` | Full scan result for one library or list |
| `sharingLinks` | `{oid}:{siteId}` | Sharing link scan for one site collection |
| `recentSites` | `{oid}` | Ordered list of `{siteId, title, url}`, max 10 |
| `settings` | `{oid}` | `{ saveScans: boolean }` |

**Key on the signed-in user's object id.** Not for security — anyone who can
read the browser profile can read every key regardless — but so the application
never serves one user's cached data to another on a shared machine. That is the
most likely accidental disclosure and the cheapest one to prevent.

A `scans` record contains the whole hierarchy, not only the broken items. The
tree has to be navigable, which means every folder is needed even when it
inherits. Each record also stores the list of **permission roots** for that
library, because Search for person queries them and would otherwise force a
rescan.

Index `scans` on `siteId` and on a boolean `hasFindings` so a person lookup can
gather roots across libraries without reading whole records.

## Persistence and quota

Call `navigator.storage.persist()` on first successful scan. Without it, the
browser may evict the database under disk pressure. The request can be denied,
so "saved" is best-effort and the interface must not promise otherwise.

Call `navigator.storage.estimate()` and show actual usage next to the clear
button. This turns an abstract amount of stored data into a visible number.

Handle `QuotaExceededError` explicitly during a scan. The default behaviour is a
silent failure part-way through, which leaves a half-written record that looks
complete. On quota failure: stop the scan, discard the partial record, and tell
the user that storage is full and offer the clear button.

## The save toggle and the clear button

Both live in the top bar. Their meanings are deliberately separate, and the
separation must be preserved because it resolves an otherwise ambiguous state.

**The toggle controls future saves only.** Turning it off does not delete
anything already stored. Turning it on does not retroactively save the current
session.

**The clear button removes everything already stored** and does not change the
toggle.

The clear button's label carries a count — `Clear (3)` — rather than being
disabled when empty. A greyed control communicates that something is impossible
without saying why, and on a touch device shows no tooltip. When the count is
zero, hide the button rather than disabling it.

Clearing is irreversible and can discard a quarter of an hour of scanning, so it
requires confirmation. The confirmation names the number of saved scans and
states that it cannot be undone, with cancel as the default. After clearing,
show no success message: the count disappearing from the button is the message.

## The information icon

Next to the toggle. Its text describes what is stored rather than reassuring
about what is not, because folder paths and file names are frequently more
revealing than file contents. A path like
`/Personalesager/Opsigelse Jens Hansen december.docx` tells the whole story
without anyone opening the document.

Required content, in this order:

1. Folder structure, folder and file names, unique permissions and sharing links
   are stored in this browser.
2. Data stays until you clear it, including after you sign out.
3. File contents are not stored.

The third point may be included but must come last, so it is not read as the
whole answer.

## No expiry, not cleared on sign-out

Both are deliberate decisions with a consequence that must be documented rather
than discovered.

Saved scans survive sign-out and are removed only by the clear button or by
clearing browser data. On a shared or communal machine, the next person can open
the application and see the previous user's structure and permission data. The
per-user keying above prevents the application from *presenting* it, but the
data is still on disk. Acceptable on a personal workstation; a real exposure on
a shared one. Say so in the information text, because most users assume the
opposite.

## The unload guard

While saved scans exist, install a `beforeunload` handler.

```js
let guardActive = false;

const guard = (e) => {
  if (!hasSavedScans()) return;
  e.preventDefault();
  e.returnValue = '';
};

export function enableCloseGuard() {
  if (guardActive) return;
  window.addEventListener('beforeunload', guard);
  guardActive = true;
}

export function disableCloseGuard() {
  window.removeEventListener('beforeunload', guard);
  guardActive = false;
}
```

Known and accepted limitations, recorded so nobody tries to fix them later:

- The browser shows its own generic wording. Custom text is ignored, and
  `returnValue` must be set even though its content is discarded.
- The dialog offers only leave or stay. There is no way to receive an answer and
  act on it, so **the guard never clears anything**. It is a reminder, nothing
  more.
- It cannot distinguish closing a tab from reloading or navigating away. Reload
  is a frequent false positive.
- Chrome shows it only if the user has interacted with the page.

Because the application is a SPA using the History API, internal navigation does
not trigger it. Application-initiated navigation does: call
`disableCloseGuard()` immediately before sign-out and before any consent
redirect. Using MSAL popup flow avoids this class of bug entirely and is the
recommended option in `01-architecture.md`.

> **Reversed.** Sign-in is a redirect in the same window, so this class of bug
> is managed rather than avoided: `auth/msal.ts` disables the guard before
> sign-in, interactive token renewal and sign-out. See
> [`adr/0006`](adr/0006-redirect-flow-not-popup.md).
