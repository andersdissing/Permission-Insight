import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { ApiError, type ApiClient, type Assignment, type ListSummary } from '../api/client';
import type { RecentSite } from '../db/database';
import { cacheScan, findingCount, getCachedScansForSite, type LibraryScan } from '../db/scans';
import { exportBrokenInheritance } from '../export/brokenInheritance';
import {
  format,
  isEmptyList,
  PermissionsUnreadableError,
  progressText,
  scanLibrary,
  sizeBadge,
  type ScanProgress,
} from '../scan/libraryScan';
import { InfoIcon } from '../components/Icons';
import { LibraryTree } from './LibraryTree';
import { SiteNotOnboarded } from '../components/SiteNotOnboarded';
import { PersonSearchDialog } from './PersonSearchDialog';
import { PersonResult } from './PersonResult';
import { findAccess, type PrincipalAccess } from '../person/findAccess';
import { exportPersonResults } from '../export/personResults';
import type { Person } from '../api/client';

type RowState =
  | { kind: 'not-scanned' }
  | { kind: 'scanning'; progress: ScanProgress }
  | { kind: 'scanned'; scan: LibraryScan }
  | { kind: 'failed'; message: string; retryable: boolean };

type Load =
  | { kind: 'loading' }
  | { kind: 'failed'; message: string }
  | { kind: 'ready'; siteTitle: string; siteWebUrl: string; lists: ListSummary[] };

interface Props {
  api: ApiClient;
  objectId: string;
  site: RecentSite;
  saveScans: boolean;
  generatedBy: string;
  onSaved: () => void;
}

export function LibrariesTab({ api, objectId, site, saveScans, generatedBy, onSaved }: Props) {
  const [load, setLoad] = useState<Load>({ kind: 'loading' });
  const [states, setStates] = useState<Record<string, RowState>>({});
  const [showSystem, setShowSystem] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  /** The list id whose tree is open, or null for the library list. */
  const [opened, setOpened] = useState<string | null>(null);

  const [notOnboarded, setNotOnboarded] = useState<{
    message: string;
    grantCommand: string | null;
  } | null>(null);

  const [personDialog, setPersonDialog] = useState(false);
  const [personRunning, setPersonRunning] = useState(false);
  const [personResults, setPersonResults] = useState<PrincipalAccess[] | null>(null);

  const running = useRef(new Map<string, AbortController>());
  const autoStarted = useRef(false);

  const setState = useCallback((listId: string, state: RowState) => {
    setStates((previous) => ({ ...previous, [listId]: state }));
  }, []);

  const startScan = useCallback(
    (list: ListSummary, force = false) => {
      if (running.current.has(list.id)) {
        return;
      }

      // Nothing to read, so nothing is read. Scanning an empty list would
      // produce a result of zero findings that is indistinguishable on screen
      // from a real clean result, and the two mean different things: one says
      // the library was examined, the other that there was nothing to examine.
      //
      // Overridable, because the count is SharePoint's and this is the only
      // place a wrong one could hide a library completely. "Scan anyway" costs
      // one call and removes that failure mode.
      if (!force && isEmptyList(list.itemCount)) {
        return;
      }

      const controller = new AbortController();
      running.current.set(list.id, controller);

      setState(list.id, {
        kind: 'scanning',
        progress: { step: 1, done: 0, total: list.itemCount, throttled: false },
      });

      void (async () => {
        try {
          const scan = await scanLibrary({
            api,
            siteId: site.siteId,
            list,
            signal: controller.signal,
            onProgress: (progress) => setState(list.id, { kind: 'scanning', progress }),
          });

          if (controller.signal.aborted) {
            return;
          }

          setState(list.id, { kind: 'scanned', scan });

          if (saveScans) {
            await cacheScan(objectId, scan);
            onSaved();
          }
        } catch (error) {
          if (controller.signal.aborted) {
            return;
          }

          // Being unable to read permissions is not a transient failure and
          // retrying will not help. It is also the one failure that must never
          // be shown as a result, because "cannot see" and "nothing to see"
          // look identical on screen.
          if (error instanceof PermissionsUnreadableError) {
            setState(list.id, { kind: 'failed', message: error.message, retryable: false });
            return;
          }

          // The site has not been granted to the tool. Nothing about this
          // library is wrong, and retrying will not change it.
          if (error instanceof ApiError && error.isSiteNotOnboarded) {
            setNotOnboarded({ message: error.message, grantCommand: error.grantCommand });
            setState(list.id, { kind: 'not-scanned' });
            return;
          }

          // Throttling and a permission failure look alike from here, and only
          // one of them is worth retrying.
          const throttled = error instanceof ApiError && error.isThrottled;

          setState(list.id, {
            kind: 'failed',
            message:
              error instanceof ApiError ? error.message : 'The scan could not be completed.',
            retryable: throttled || !(error instanceof ApiError) || error.status >= 500,
          });
        } finally {
          running.current.delete(list.id);
        }
      })();
    },
    [api, objectId, onSaved, saveScans, setState, site.siteId],
  );

  useEffect(() => {
    const controller = new AbortController();
    const inFlight = running.current;

    void (async () => {
      try {
        const [response, cached] = await Promise.all([
          api.getLists(site.siteId, controller.signal),
          getCachedScansForSite(objectId, site.siteId),
        ]);

        if (controller.signal.aborted) {
          return;
        }

        setStates(
          Object.fromEntries(
            [...cached.entries()].map(([listId, scan]) => [listId, { kind: 'scanned', scan }] as const),
          ),
        );

        setLoad({
          kind: 'ready',
          siteTitle: response.siteTitle,
          siteWebUrl: response.siteWebUrl,
          lists: response.lists,
        });

        // The default document library scans on its own. Everything else is
        // opt-in, because a site can hold four libraries of twelve thousand
        // items and scanning all of them uninvited is rude.
        const defaultLibrary = response.lists.find(
          (list) => list.isDefaultDocumentLibrary && !list.isSystem,
        );

        if (
          defaultLibrary &&
          !isEmptyList(defaultLibrary.itemCount) &&
          !cached.has(defaultLibrary.id) &&
          !autoStarted.current
        ) {
          autoStarted.current = true;
          startScan(defaultLibrary);
        }
      } catch (error) {
        if (controller.signal.aborted) {
          return;
        }

        setLoad({
          kind: 'failed',
          message:
            error instanceof ApiError ? error.message : 'The lists could not be read.',
        });
      }
    })();

    return () => {
      controller.abort();
      for (const scan of inFlight.values()) {
        scan.abort();
      }
      inFlight.clear();
    };
  }, [api, objectId, site.siteId, startScan]);

  const visible = useMemo(() => {
    if (load.kind !== 'ready') {
      return [];
    }
    return load.lists.filter((list) => showSystem || !list.isSystem);
  }, [load, showSystem]);

  const scans = useMemo(
    () =>
      visible
        .map((list) => states[list.id])
        .filter((state): state is Extract<RowState, { kind: 'scanned' }> => state?.kind === 'scanned')
        .map((state) => state.scan),
    [states, visible],
  );

  // An empty list is neither scanned nor waiting to be scanned. Counting it as
  // unscanned would make "Export all" nag about libraries that hold nothing,
  // and counting it as scanned would claim an examination that never happened.
  const scannable = useMemo(() => visible.filter((list) => !isEmptyList(list.itemCount)), [visible]);

  const empty = useMemo(() => visible.filter((list) => isEmptyList(list.itemCount)), [visible]);

  const unscanned = scannable.filter((list) => states[list.id]?.kind !== 'scanned');

  const scanAll = useCallback(() => {
    for (const list of scannable) {
      if (states[list.id]?.kind !== 'scanned') {
        startScan(list);
      }
    }
  }, [startScan, states, scannable]);

  const exportAll = useCallback(() => {
    if (load.kind !== 'ready') {
      return;
    }

    // Prefer prompting over disabling: say how many are missing and offer to
    // scan them, rather than greying out a button with no explanation.
    if (unscanned.length > 0 && notice === null) {
      setNotice(
        `${unscanned.length} of ${scannable.length} ${
          scannable.length === 1 ? 'list has' : 'lists have'
        } not been scanned. Export what's scanned, or scan the rest first?`,
      );
      return;
    }

    setNotice(null);

    const rows = exportBrokenInheritance({
      siteTitle: load.siteTitle,
      siteWebUrl: load.siteWebUrl,
      generatedBy,
      scans,
      unscanned: unscanned.map((list) => list.title),
      empty: empty.map((list) => list.title),
    });

    void api.recordExport(`${load.siteTitle} broken inheritance`, rows, 'download');
  }, [api, empty, generatedBy, load, notice, scannable.length, scans, unscanned]);

  const exportOne = useCallback(
    (scan: LibraryScan) => {
      if (load.kind !== 'ready') {
        return;
      }

      const rows = exportBrokenInheritance({
        siteTitle: load.siteTitle,
        siteWebUrl: load.siteWebUrl,
        generatedBy,
        scans: [scan],
        unscanned: [],
      });

      void api.recordExport(`${load.siteTitle} / ${scan.listTitle}`, rows, 'download');
    },
    [api, generatedBy, load],
  );

  const runPersonCheck = useCallback(
    (principals: Person[]) => {
      setPersonRunning(true);

      void (async () => {
        try {
          // Library level grants are the one thing the cached scan does not
          // already hold, because phase B only reads items. One call per
          // scanned library, and a site has a handful.
          const topLevel = new Map<string, Assignment[]>();

          await Promise.all(
            scans.map(async (scan) => {
              try {
                const response = await api.getListAccess(site.siteId, scan.listId);
                topLevel.set(scan.listId, response.assignments);
              } catch {
                // A library whose own permissions cannot be read still
                // contributes its item level findings. Losing the top level
                // box is better than losing the whole answer.
              }
            }),
          );

          setPersonResults(principals.map((principal) => findAccess(principal, scans, topLevel)));
          setPersonDialog(false);
        } catch (error) {
          setNotice(
            error instanceof ApiError ? error.message : 'The access check could not be completed.',
          );
          setPersonDialog(false);
        } finally {
          setPersonRunning(false);
        }
      })();
    },
    [api, scans, site.siteId],
  );

  const closePersonDialog = useCallback(() => {
    setPersonDialog(false);
    setPersonRunning(false);
  }, []);

  if (load.kind === 'loading') {
    return <p className="empty">Reading lists and libraries</p>;
  }

  if (load.kind === 'failed') {
    return <p className="error">{load.message}</p>;
  }

  // Nothing else on this tab works until the site is granted, so it replaces
  // the content rather than sitting above it.
  if (notOnboarded !== null) {
    return (
      <SiteNotOnboarded message={notOnboarded.message} grantCommand={notOnboarded.grantCommand} />
    );
  }

  // The person result replaces the tree in the same area, with its own crumb.
  if (personResults !== null) {
    return (
      <PersonResult
        results={personResults}
        coverage={{
          scanned: scans.map((scan) => scan.listTitle),
          unscanned: unscanned.map((list) => list.title),
        }}
        onBack={() => setPersonResults(null)}
        onExport={() => {
          const rows = exportPersonResults({
            siteTitle: load.siteTitle,
            siteWebUrl: load.siteWebUrl,
            generatedBy,
            results: personResults,
            scanned: scans.map((scan) => scan.listTitle),
            unscanned: unscanned.map((list) => list.title),
          });

          void api.recordExport(
            `${load.siteTitle} / principals: ${personResults.map((result) => result.principal.displayName).join(', ')}`,
            rows,
            'download',
          );
        }}
      />
    );
  }

  // Opening a library replaces the list with its tree. The tab bar stays.
  if (opened !== null) {
    const state = states[opened];

    if (state?.kind === 'scanned') {
      return (
        <LibraryTree
          api={api}
          siteId={site.siteId}
          siteWebUrl={load.siteWebUrl}
          scan={state.scan}
          onBack={() => setOpened(null)}
          onExport={() => exportOne(state.scan)}
        />
      );
    }
  }

  return (
    <div className="tab-body">
      <div className="tab-header">
        <h2>
          {visible.length === 1 ? '1 list' : `${visible.length} lists and libraries`}
          <SizeBadgeExplainer />
        </h2>

        <div className="tab-actions">
          <label className="system-toggle">
            <input
              type="checkbox"
              checked={showSystem}
              onChange={(event) => setShowSystem(event.target.checked)}
            />
            System lists
          </label>
          <button type="button" className="text-button" onClick={scanAll}>
            Scan all
          </button>
          <button type="button" className="text-button" onClick={exportAll}>
            Export all
          </button>
          {/* Always enabled. When not every library is scanned the dialog says
              so before the search field, rather than the button refusing. */}
          <button type="button" className="text-button" onClick={() => setPersonDialog(true)}>
            Search for people or group
          </button>
        </div>
      </div>

      {personDialog && (
        <PersonSearchDialog
          api={api}
          scans={scans}
          scannedCount={scans.length}
          // Empty libraries excluded: the coverage line counts what could hold
          // a finding, and they cannot.
          totalCount={scannable.length}
          running={personRunning}
          onCancel={closePersonDialog}
          onCheck={runPersonCheck}
        />
      )}

      {notice !== null && (
        <div className="notice">
          <span>{notice}</span>
          <button type="button" className="text-button" onClick={exportAll}>
            Export anyway
          </button>
          <button
            type="button"
            className="text-button"
            onClick={() => {
              setNotice(null);
              scanAll();
            }}
          >
            Scan the rest
          </button>
        </div>
      )}

      <ul className="library-rows">
        {visible.map((list) => (
          <LibraryRow
            key={list.id}
            list={list}
            state={states[list.id] ?? { kind: 'not-scanned' }}
            onScan={() => startScan(list)}
            onScanAnyway={() => startScan(list, true)}
            onExport={exportOne}
            onOpen={() => setOpened(list.id)}
          />
        ))}
      </ul>
    </div>
  );
}

function SizeBadgeExplainer() {
  const [open, setOpen] = useState(false);

  return (
    <span className="storage-info">
      <button
        type="button"
        className="icon-button"
        aria-label="What the size badges mean"
        aria-expanded={open}
        onClick={() => setOpen((previous) => !previous)}
      >
        <InfoIcon />
      </button>

      {open && (
        <span className="popover" role="dialog">
          {/* Describes size, not duration. The badge measures the first of two
              phases: 12,000 items with nothing broken finishes quickly, while
              3,000 items with 800 broken is slower. */}
          <p>
            The badge describes how many items a library holds: Small under 2,000, Medium to 6,000,
            Large to 10,000, Extra large above that.
          </p>
          <p>
            Empty means the library holds nothing, so there's nothing to scan and it isn't counted
            as scanned either. Size unknown means SharePoint didn't report a count — that one is
            still scanned, because it might hold anything.
          </p>
          <p>
            It isn't a guess at how long a scan takes. Reading permissions costs one call per item
            with unique permissions, so a big library with nothing broken finishes sooner than a
            small one with a lot.
          </p>
        </span>
      )}
    </span>
  );
}

function LibraryRow({
  list,
  state,
  onScan,
  onScanAnyway,
  onExport,
  onOpen,
}: {
  list: ListSummary;
  state: RowState;
  onScan: () => void;
  onScanAnyway: () => void;
  onExport: (scan: LibraryScan) => void;
  onOpen: () => void;
}) {
  const badge = sizeBadge(list.itemCount);
  const scanned = state.kind === 'scanned';
  const empty = isEmptyList(list.itemCount);

  return (
    <li className="library-row">
      <div className="library-head">
        {scanned ? (
          <button type="button" className="library-name link" onClick={onOpen}>
            {list.title}
          </button>
        ) : (
          <span className="library-name">{list.title}</span>
        )}
        {list.isDefaultDocumentLibrary && <span className="badge">Default</span>}
        {/* Prefixed. The bare kind would be a class like "empty", which is
            already a global for empty-state text and carries a 32px margin —
            it silently doubled the height of every empty row. */}
        <span className={`badge size-${badge.kind}${badge.warning ? ' warning' : ''}`}>
          {badge.label}
        </span>

        {/* About the list object itself, which says nothing about its
            contents: a library that inherits can still hold thousands of
            broken items. Null means SharePoint did not report it, which is
            not the same as "inherits" and is not shown as one. */}
        {list.hasUniqueRoleAssignments === true && (
          <span className="badge warning">Unique permissions</span>
        )}
        {list.hasUniqueRoleAssignments === null && (
          <span className="badge">Permissions unknown</span>
        )}
        <span className="library-spacer" />
        {empty && state.kind === 'not-scanned' ? (
          // Quiet, but present. SharePoint's count is the only thing standing
          // between this library and a scan, and it is occasionally wrong.
          <button type="button" className="text-button subtle" onClick={onScanAnyway}>
            Scan anyway
          </button>
        ) : (
          <RowActions state={state} onScan={onScan} onExport={onExport} />
        )}
      </div>

      <RowDetail state={state} empty={empty} />
    </li>
  );
}

function RowActions({
  state,
  onScan,
  onExport,
}: {
  state: RowState;
  onScan: () => void;
  onExport: (scan: LibraryScan) => void;
}) {
  if (state.kind === 'scanned') {
    return (
      <span className="row-actions">
        <button type="button" className="text-button" onClick={onScan}>
          Rescan
        </button>
        <button type="button" className="text-button" onClick={() => onExport(state.scan)}>
          Export
        </button>
      </span>
    );
  }

  if (state.kind === 'failed') {
    return state.retryable ? (
      <button type="button" className="text-button" onClick={onScan}>
        Retry
      </button>
    ) : null;
  }

  if (state.kind === 'not-scanned') {
    return (
      <button type="button" className="text-button" onClick={onScan}>
        Scan
      </button>
    );
  }

  return null;
}

function RowDetail({ state, empty }: { state: RowState; empty: boolean }) {
  // Said plainly, and distinct from "Not scanned". One means nobody has looked
  // yet; this one means there is nothing to look at. Only while untouched: once
  // "Scan anyway" has run, the real result outranks the count that said not to
  // bother.
  if (empty && state.kind === 'not-scanned') {
    return <p className="library-status">Nothing to scan</p>;
  }

  if (state.kind === 'scanning') {
    const { progress } = state;

    // No denominator, no bar. An indeterminate stripe is honest where a
    // fraction of an unknown total is not.
    const percent =
      progress.total !== null && progress.total > 0
        ? Math.round((progress.done / progress.total) * 100)
        : null;

    return (
      <div className="progress">
        <div className="progress-track">
          {percent === null ? (
            <div className="progress-fill indeterminate" />
          ) : (
            <div className="progress-fill" style={{ width: `${percent}%` }} />
          )}
        </div>
        <span className="progress-text">{progressText(progress)}</span>
      </div>
    );
  }

  if (state.kind === 'scanned') {
    const findings = findingCount(state.scan);

    // Without the result count a cached scan and an empty scan look identical.
    return (
      <p className="library-status">
        Saved {new Date(state.scan.savedAt).toLocaleDateString(undefined, {
          day: 'numeric',
          month: 'short',
        })}
        {' · '}
        {format(findings)} of {format(state.scan.items.length)} items with unique permissions
      </p>
    );
  }

  if (state.kind === 'failed') {
    // is-error, not error: the bare class is a global for centred empty-state
    // text, and inheriting it centres this message alone among the statuses.
    return <p className="library-status is-error">{state.message}</p>;
  }

  return <p className="library-status">Not scanned</p>;
}
