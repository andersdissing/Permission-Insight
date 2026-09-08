import {
  PublicClientApplication,
  InteractionRequiredAuthError,
  type AccountInfo,
} from '@azure/msal-browser';
import type { AppConfig } from '../config';
import { disableCloseGuard } from '../unloadGuard';

export interface Session {
  msal: PublicClientApplication;
  account: AccountInfo;
  /** A token for the backend API. Silent where possible, a redirect when not. */
  getToken: () => Promise<string>;
  signOut: () => Promise<void>;
}

/**
 * The page is on its way out. Returning a promise that never settles keeps
 * callers from carrying on as though sign-in had failed.
 */
const leavingThePage = (): Promise<never> => new Promise<never>(() => {});

/**
 * Failures a redirect can fix.
 *
 * `timed_out` and `monitor_window_timeout` are the hidden renewal frame not
 * coming back — a stale directory session, a blocked third-party cookie, or a
 * slow network. None of them is a fault the user can do anything about, and
 * all of them are resolved by signing in again, so they are treated exactly
 * like an explicit interaction_required rather than shown as an error.
 *
 * Matched on the code rather than the class so a version bump that reshapes
 * MSAL's error types cannot quietly turn a recoverable case into a dead end.
 */
function needsInteraction(error: unknown): boolean {
  const code = (error as { errorCode?: string } | null)?.errorCode;

  return (
    code === 'timed_out' ||
    code === 'monitor_window_timeout' ||
    code === 'interaction_required' ||
    code === 'login_required' ||
    code === 'consent_required' ||
    code === 'no_account_error'
  );
}

/**
 * Clears MSAL's own storage for this origin.
 *
 * A redirect interrupted part-way — a reload while the sign-in page is open,
 * a closed tab — can leave an interaction flag set, and every later attempt
 * then fails on state nobody can see. Offering this beats telling somebody to
 * clear their browser storage by hand.
 */
export function clearAuthState(): void {
  for (const key of Object.keys(sessionStorage)) {
    if (key.startsWith('msal.') || key.includes('login.windows.net') || key.includes('login.microsoftonline.com')) {
      sessionStorage.removeItem(key);
    }
  }
}

/**
 * Signs in.
 *
 * Automatic on load: there is no sign-in button and no anonymous state to
 * design.
 *
 * Redirect flow, in the same window. `01-architecture.md` originally chose
 * popup, because the application installs a `beforeunload` guard while cached
 * scans exist and a redirect would trigger the browser's leave-site warning in
 * the middle of an action the user started. That decision was reversed; see
 * `adr/0006`. The specification's own instruction for taking this route is the
 * rule this module now follows: **the guard must be removed before every
 * application-initiated navigation**, which is why `disableCloseGuard` is
 * called before each of the three redirects below and nowhere else.
 */
export async function signIn(config: AppConfig): Promise<Session> {
  const msal = new PublicClientApplication({
    auth: {
      clientId: config.clientId,
      authority: `https://login.microsoftonline.com/${config.tenantId}`,
      redirectUri: window.location.origin,
    },
    cache: {
      // Session rather than local storage. Cached scans already outlive
      // sign-out by design; the token should not, and on a shared machine a
      // closed tab ends the session rather than leaving one open for whoever
      // sits down next. A redirect stays in the same tab, so the interaction
      // state survives it.
      cacheLocation: 'sessionStorage',
    },
  });

  await msal.initialize();

  // Completes a sign-in that began before the redirect. This has to run before
  // anything reads the account list, or the account that just arrived is
  // invisible and the app redirects again in a loop.
  const returned = await msal.handleRedirectPromise();

  const scopes = [config.apiScope];
  const account = returned?.account ?? msal.getAllAccounts()[0];

  if (!account) {
    disableCloseGuard();
    await msal.loginRedirect({ scopes });
    return leavingThePage();
  }

  msal.setActiveAccount(account);

  const getToken = async (): Promise<string> => {
    try {
      const result = await msal.acquireTokenSilent({ scopes, account });
      return result.accessToken;
    } catch (error) {
      // Anything that only interaction can fix is answered with interaction,
      // rather than surfaced as an error the user cannot act on.
      if (error instanceof InteractionRequiredAuthError || needsInteraction(error)) {
        disableCloseGuard();
        await msal.acquireTokenRedirect({ scopes, account });
        return leavingThePage();
      }
      throw error;
    }
  };

  const signOut = async (): Promise<void> => {
    disableCloseGuard();
    await msal.logoutRedirect({ account });
  };

  return { msal, account, getToken, signOut };
}
