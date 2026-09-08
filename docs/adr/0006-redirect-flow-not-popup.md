# 0006 — Sign-in is a redirect in the same window, not a popup

Status: accepted, 2026-09-04. **Reverses** the decision in the "Sign-in"
section of `01-architecture.md` and the recommendation at the end of
`03-client-storage.md`.

## What changed

Sign-in, interactive token renewal and sign-out all navigate in the same
window. `loginPopup`, `acquireTokenPopup` and `logoutPopup` are gone.

## Why the original decision existed

The specification chose popup for one specific reason, and it was a good one:
the application installs a `beforeunload` guard while cached scans exist, and a
redirect fires that guard. The user is then shown the browser's leave-site
warning in the middle of an action they deliberately started, on a dialog whose
answer the application cannot see or act on. Popup sidesteps the problem
instead of managing it.

## Why it was reversed

Requested directly: the authentication flow should stay in the window the user
is already looking at. That is a reasonable preference and a common one —
popups are blocked by default in some managed browser configurations, they lose
the visual thread of what the user was doing, and on a narrow window they are
easy to miss entirely.

This is a user-experience decision overriding an implementation convenience.
The problem popup avoided is real but tractable, and the specification already
said how to make it tractable.

## What this obliges us to do

`01-architecture.md` states the condition plainly: "If redirect flow is chosen
anyway, the guard must be removed before every application-initiated
navigation." That is now the rule `auth/msal.ts` is built around, and it has
exactly three places to honour:

1. `loginRedirect`, when there is no account.
2. `acquireTokenRedirect`, when a silent token renewal needs interaction.
3. `logoutRedirect`, on sign-out.

`disableCloseGuard()` is called immediately before each, and nowhere else in
that module. A fourth redirect added later without that call reintroduces the
bug, so the calls sit next to the navigations rather than in a wrapper where
they could be forgotten.

One supporting detail: `handleRedirectPromise()` runs before anything reads the
account list. Skip it and the account that just arrived is invisible, so the
application redirects again, forever.

One known cost: a deep link into a site does not survive signing in. MSAL v5
removed `navigateToLoginRequestUrl`, so an unauthenticated user opening
`/sites/{id}` lands back at the picker after signing in rather than at the site
they asked for. Popup had no such problem. Worth fixing by stashing the path
before the redirect and restoring it after, if it turns out to annoy anyone.

## Consequences

- Cached scans are unaffected. Sign-out still deliberately does not clear them.
- `sessionStorage` remains the token cache. A redirect stays in the same tab,
  so the interaction state survives it, and the shared-machine argument for
  session-scoped tokens is unchanged.
- The redirect URIs the deployment registers already cover this. Entra treats
  the SPA redirect URI the same for both flows, so no infrastructure change was
  needed.
- If the guard is ever seen firing during sign-in, this ADR is the first place
  to look: something navigated without disabling it.
