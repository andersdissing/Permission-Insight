import { useCallback, useEffect, useRef, useState } from 'react';
import { loadConfig, type AppConfig } from './config';
import { clearAuthState, signIn, type Session } from './auth/msal';
import { ApiClient, ApiError, type Me } from './api/client';
import { getRecentSites, rememberSite } from './db/recentSites';
import { clearSavedScans, getSettings, setSaveScans, summariseSavedScans } from './db/storage';
import type { RecentSite } from './db/database';
import { disableCloseGuard, enableCloseGuard } from './unloadGuard';
import { TopBar } from './components/TopBar';
import { SitePicker } from './screens/SitePicker';
import { SiteScreen } from './screens/SiteScreen';
import { AccessDenied } from './screens/AccessDenied';

type Boot =
  | { kind: 'starting' }
  | { kind: 'failed'; message: string }
  | { kind: 'denied'; groupName: string; displayName: string }
  | { kind: 'ready'; session: Session; api: ApiClient; me: Me };

type Route = { kind: 'picker' } | { kind: 'site'; siteId: string };

export function App() {
  const [boot, setBoot] = useState<Boot>({ kind: 'starting' });
  const started = useRef(false);

  useEffect(() => {
    // Sign-in opens a popup, and React runs effects twice in development.
    if (started.current) {
      return;
    }
    started.current = true;

    void (async () => {
      let config: AppConfig | undefined;
      let session: Session | undefined;

      try {
        config = await loadConfig();
        session = await signIn(config);
        const api = new ApiClient(config, session);

        // The backend decides. The front end deliberately does not read the
        // roles claim itself: its route guard is cosmetic, and duplicating the
        // check here would create a second place for the two to disagree.
        const me = await api.me();

        setBoot({ kind: 'ready', session, api, me });
      } catch (error) {
        if (error instanceof ApiError && error.isMissingRole) {
          setBoot({
            kind: 'denied',
            groupName: config?.usersGroupName ?? 'the Permission Insight users group',
            displayName: session?.account.name ?? session?.account.username ?? 'this account',
          });
          return;
        }

        setBoot({
          kind: 'failed',
          message: error instanceof Error ? error.message : 'Permission insight could not start.',
        });
      }
    })();
  }, []);

  if (boot.kind === 'starting') {
    return <p className="boot">Signing in</p>;
  }

  if (boot.kind === 'failed') {
    return (
      <main className="boot-failure">
        <h1>Permission insight couldn't start</h1>
        <p className="error">{boot.message}</p>
        <p className="muted">
          A sign-in interrupted part-way can leave state behind that every later attempt then trips
          over. Starting again clears it.
        </p>
        <button
          type="button"
          className="text-button"
          onClick={() => {
            clearAuthState();
            window.location.replace(window.location.origin);
          }}
        >
          Sign in again
        </button>
      </main>
    );
  }

  if (boot.kind === 'denied') {
    return <AccessDenied groupName={boot.groupName} displayName={boot.displayName} />;
  }

  return <Shell session={boot.session} api={boot.api} me={boot.me} />;
}

function Shell({ session, api, me }: { session: Session; api: ApiClient; me: Me }) {
  const [route, setRoute] = useState<Route>(() => parseRoute(window.location.pathname));
  const [recent, setRecent] = useState<RecentSite[]>([]);
  const [saveScans, setSaveScansState] = useState(true);
  const [saved, setSaved] = useState({ count: 0, bytes: null as number | null });

  // Bumped when saved scans are cleared.
  //
  // The tabs below hold their scans in memory as well as in IndexedDB, so
  // emptying the database on its own left the screen showing results that no
  // longer existed: rows still read "scanned", and Scan all skipped every one
  // of them because it only scans what is not already scanned. This is part of
  // the site screen's key, so a clear remounts it and every derived piece of
  // state goes with it — rather than resetting each tab by hand and missing
  // one, which is how the limbo arose in the first place.
  const [dataEpoch, setDataEpoch] = useState(0);

  // The unload guard reads this rather than closing over state, so it stays
  // installed once and still sees the current count.
  const savedCount = useRef(0);

  useEffect(() => {
    savedCount.current = saved.count;
  }, [saved.count]);

  useEffect(() => {
    void (async () => {
      setRecent(await getRecentSites(me.objectId));
      setSaveScansState((await getSettings(me.objectId)).saveScans);
      setSaved(await summariseSavedScans(me.objectId));
    })();
  }, [me.objectId]);

  useEffect(() => {
    // A reminder, nothing more. It cannot clear anything and it cannot tell a
    // close from a reload; see unloadGuard.ts.
    enableCloseGuard(() => savedCount.current > 0);
    return disableCloseGuard;
  }, []);

  useEffect(() => {
    const onPopState = () => setRoute(parseRoute(window.location.pathname));
    window.addEventListener('popstate', onPopState);
    return () => window.removeEventListener('popstate', onPopState);
  }, []);

  const navigate = useCallback((path: string) => {
    window.history.pushState(null, '', path);
    setRoute(parseRoute(path));
  }, []);

  const selectSite = useCallback(
    (site: RecentSite) => {
      void (async () => {
        setRecent(await rememberSite(me.objectId, site));
        setSaved(await summariseSavedScans(me.objectId));
        navigate(`/sites/${encodeURIComponent(site.siteId)}`);
      })();
    },
    [me.objectId, navigate],
  );

  const changeSaveScans = useCallback(
    (value: boolean) => {
      setSaveScansState(value);
      void setSaveScans(me.objectId, value);
    },
    [me.objectId],
  );

  const refreshSaved = useCallback(() => {
    void (async () => setSaved(await summariseSavedScans(me.objectId)))();
  }, [me.objectId]);

  const clear = useCallback(async () => {
    // The recent sites list is left alone. It is navigation history rather
    // than permission data, and the selected site is looked up in it — so
    // emptying it threw the user out of the site they were reading, mid-task,
    // as a side effect of clearing scan results.
    await clearSavedScans(me.objectId);
    setSaved(await summariseSavedScans(me.objectId));

    // Drops what the tabs are still holding. Any scan in flight is aborted on
    // unmount, which is the right outcome: its results were just deleted.
    setDataEpoch((epoch) => epoch + 1);
  }, [me.objectId]);

  const signOut = useCallback(() => {
    // The guard would otherwise fire on a navigation the application started
    // itself. Sign-out deliberately does not clear saved scans.
    disableCloseGuard();
    void session.signOut();
  }, [session]);

  const selected = route.kind === 'site' ? recent.find((site) => site.siteId === route.siteId) : undefined;

  return (
    <div className="app">
      <TopBar
        displayName={me.displayName}
        saveScans={saveScans}
        onSaveScansChange={changeSaveScans}
        saved={saved}
        onClear={clear}
        onSignOut={signOut}
      />

      {selected ? (
        <SiteScreen
          // Site and epoch both: changing either has to start from the
          // database rather than from what the previous view was holding.
          key={`${selected.siteId}:${dataEpoch}`}
          api={api}
          objectId={me.objectId}
          site={selected}
          saveScans={saveScans}
          generatedBy={me.mail ?? me.displayName}
          onSaved={refreshSaved}
          onBack={() => navigate('/')}
        />
      ) : (
        <SitePicker api={api} recent={recent} onSelect={selectSite} />
      )}
    </div>
  );
}

function parseRoute(pathname: string): Route {
  const match = /^\/sites\/(.+)$/.exec(pathname);
  return match?.[1] ? { kind: 'site', siteId: decodeURIComponent(match[1]) } : { kind: 'picker' };
}
