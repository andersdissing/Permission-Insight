let guardActive = false;
let hasSavedScans = (): boolean => false;

const guard = (event: BeforeUnloadEvent): void => {
  if (!hasSavedScans()) {
    return;
  }

  event.preventDefault();
  event.returnValue = '';
};

/**
 * Reminds the user that scans are still stored in this browser when they close
 * the tab.
 *
 * Known and accepted limitations, recorded so nobody tries to fix them later:
 * the browser shows its own generic wording and discards ours; the dialog
 * offers only leave or stay, so there is no answer to act on and the guard
 * never clears anything; it cannot tell closing a tab from reloading, so
 * reload is a frequent false positive; and Chrome shows it only after the user
 * has interacted with the page.
 *
 * Internal navigation uses the History API and does not trigger it. Any
 * navigation the application starts does, so call disableCloseGuard first.
 * Sign-in, token renewal and sign-out are all redirects in the same window
 * (see `adr/0006`), and `auth/msal.ts` disables the guard before each one.
 * That is not a nicety: without it the browser's leave-site warning appears in
 * the middle of an action the user started, on a dialog they cannot answer
 * usefully.
 */
export function enableCloseGuard(predicate: () => boolean): void {
  hasSavedScans = predicate;

  if (guardActive) {
    return;
  }

  window.addEventListener('beforeunload', guard);
  guardActive = true;
}

export function disableCloseGuard(): void {
  window.removeEventListener('beforeunload', guard);
  guardActive = false;
}
