import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { ApiError, type ApiClient, type Recipient } from '../api/client';
import type { RecentSite } from '../db/database';
import { cacheSharingLinks, getCachedSharingLinks, type SharingLinkRow } from '../db/sharingLinks';
import { getCachedScansForSite } from '../db/scans';
import { linksFromScans, tallyScopes, type DerivedLink } from '../person/linksFromScans';
import { exportSharingLinks } from '../export/sharingLinks';
import { buildCsv, downloadCsv, exportFileName } from '../export/csv';
import { InfoTip } from '../components/InfoTip';

/** Four workers keep the top of the list filling in without flooding SharePoint. */
const CONCURRENCY = 4;

/** Rows are flushed on a timer rather than per resolution, so a 480 link site does not re-render 480 times. */
const FLUSH_MS = 150;

type Load =
  | { kind: 'loading' }
  | { kind: 'failed'; message: string }
  /** Built from the library scans rather than from site groups. Narrower; see docs/adr/0013. */
  | { kind: 'derived'; siteTitle: string; siteWebUrl: string }
  | { kind: 'needs-scan'; message: string }
  | { kind: 'ready'; siteTitle: string; siteWebUrl: string; savedAt: string | null };

interface Props {
  api: ApiClient;
  objectId: string;
  site: RecentSite;
  saveScans: boolean;
  generatedBy: string;
  onTotalChange: (total: number | null) => void;
  onSaved: () => void;
}

export function SharingLinksTab({
  api,
  objectId,
  site,
  saveScans,
  generatedBy,
  onTotalChange,
  onSaved,
}: Props) {
  const [load, setLoad] = useState<Load>({ kind: 'loading' });
  const [rows, setRows] = useState<SharingLinkRow[]>([]);
  const [derived, setDerived] = useState<{ links: DerivedLink[]; libraries: string[] } | null>(null);
  const [resolving, setResolving] = useState(false);
  const [waitingUntil, setWaitingUntil] = useState<number | null>(null);
  const [attempt, setAttempt] = useState(0);

  const working = useRef<SharingLinkRow[]>([]);

  useEffect(() => {
    const controller = new AbortController();
    let flushTimer: number | undefined;

    const flush = () => setRows([...working.current]);

    void (async () => {
      try {
        // A cached scan is shown as it stands. Re-entering a site must not
        // rescan it; that is what the cache is for.
        const cached = await getCachedSharingLinks(objectId, site.siteId);

        if (cached && attempt === 0) {
          working.current = cached.rows;
          flush();
          onTotalChange(cached.rows.length);
          setLoad({
            kind: 'ready',
            siteTitle: cached.siteTitle,
            siteWebUrl: cached.siteWebUrl,
            savedAt: cached.savedAt,
          });
          return;
        }

        // Stage one. One site-collection call, so the total lands in well
        // under a second and needs no progress indicator.
        const response = await api.getSharingLinks(site.siteId, controller.signal);
        if (controller.signal.aborted) {
          return;
        }

        working.current = response.links.map((link) => ({ ...link, detail: null }));
        flush();
        onTotalChange(response.links.length);
        setLoad({
          kind: 'ready',
          siteTitle: response.siteTitle,
          siteWebUrl: response.siteWebUrl,
          savedAt: null,
        });

        if (response.links.length === 0) {
          return;
        }

        // Stage two, from the top of the list. An unresolved row holds only a
        // GUID and a kind, so lazy resolution on scroll would show the user
        // almost nothing and export would force a full pass anyway.
        setResolving(true);
        flushTimer = window.setInterval(flush, FLUSH_MS);

        await resolveAll(api, site.siteId, working.current, controller.signal, setWaitingUntil);

        window.clearInterval(flushTimer);
        flush();
        setResolving(false);

        if (controller.signal.aborted) {
          return;
        }

        // Written once, at the end. A partial result cached as complete would
        // look identical to a finished scan on the next visit.
        if (saveScans) {
          await cacheSharingLinks(objectId, {
            siteId: site.siteId,
            siteTitle: response.siteTitle,
            siteWebUrl: response.siteWebUrl,
            savedAt: new Date().toISOString(),
            rows: working.current,
          });
          onSaved();
        }
      } catch (error) {
        if (controller.signal.aborted) {
          return;
        }

        setResolving(false);

        // SharePoint refuses to list site groups on a read-only token. Fall
        // back to what the library scans already found: every sharing link
        // breaks inheritance, so every link is on an item the scan has read
        // permissions for. See docs/adr/0013.
        if (error instanceof ApiError && error.isSiteNotOnboarded) {
          const cached = await getCachedScansForSite(objectId, site.siteId);
          const scans = [...cached.values()];

          if (scans.length > 0) {
            working.current = [];
            setDerived({ links: linksFromScans(scans), libraries: scans.map((s) => s.listTitle) });
            onTotalChange(linksFromScans(scans).length);
            setLoad({ kind: 'derived', siteTitle: site.title, siteWebUrl: site.url });
            return;
          }

          setLoad({
            kind: 'needs-scan',
            message:
              'Sharing links come from the library scans. Scan a library on the Lists and libraries tab and they appear here.',
          });
          return;
        }

        setLoad({
          kind: 'failed',
          message:
            error instanceof ApiError ? error.message : 'The sharing links could not be read.',
        });
      }
    })();

    return () => {
      controller.abort();
      window.clearInterval(flushTimer);
    };
    // onTotalChange and onSaved are stable callbacks from the parent.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, objectId, site.siteId, attempt]);

  const counts = useMemo(() => tally(rows), [rows]);
  const resolved = rows.filter((row) => row.detail !== null).length;

  const download = useCallback(() => {
    if (load.kind !== 'ready') {
      return;
    }

    const count = exportSharingLinks({
      siteTitle: load.siteTitle,
      siteWebUrl: load.siteWebUrl,
      generatedBy,
      rows,
    });

    void api.recordExport(`${load.siteTitle} sharing links`, count, 'download');
  }, [api, generatedBy, load, rows]);

  if (load.kind === 'loading') {
    return <p className="empty">Reading sharing links</p>;
  }

  if (load.kind === 'needs-scan') {
    return <p className="empty">{load.message}</p>;
  }

  if (load.kind === 'failed') {
    return <p className="error">{load.message}</p>;
  }

  if (load.kind === 'derived') {
    return (
      <DerivedLinks
        links={derived?.links ?? []}
        libraries={derived?.libraries ?? []}
        siteTitle={load.siteTitle}
        siteWebUrl={load.siteWebUrl}
        generatedBy={generatedBy}
        api={api}
      />
    );
  }

  if (rows.length === 0) {
    return (
      <div className="tab-body">
        <p className="empty">Nothing in this site is shared by link</p>
      </div>
    );
  }

  return (
    <div className="tab-body">
      <div className="tab-header">
        <h2>
          {rows.length === 1 ? '1 sharing link' : `${rows.length} sharing links`}
          {load.savedAt !== null && (
            <span className="saved-at"> · saved {formatSaved(load.savedAt)}</span>
          )}
        </h2>

        <div className="tab-actions">
          {load.savedAt !== null && (
            <button type="button" className="text-button" onClick={() => setAttempt((n) => n + 1)}>
              Rescan
            </button>
          )}
          {/* Always enabled. Sharing links come from one site-collection call
              and never depend on which libraries were scanned. */}
          <button type="button" className="text-button" onClick={download}>
            Export
          </button>
        </div>
      </div>

      {resolving && (
        <div className="progress">
          <div className="progress-track">
            <div
              className="progress-fill"
              style={{ width: `${Math.round((resolved / rows.length) * 100)}%` }}
            />
          </div>
          <span className="progress-text">
            {waitingUntil !== null
              ? 'SharePoint is throttling. Waiting, then continuing'
              : `Resolving ${resolved} of ${rows.length}`}
          </span>
        </div>
      )}

      <div className="chips">
        <Chip label="Anyone" count={counts.anyone} tint="warning" />
        <Chip label="Organization" count={counts.organization} />
        <Chip label="Specific people" count={counts.specific} />
        {/* No Orphaned chip. Finding an orphan means listing the site's
            groups, which a read-only token cannot do, so the count would read
            zero whether or not any existed — and a confident zero is worse
            than no number. Orphaned rows still render where the site groups
            call succeeds. See docs/adr/0013. */}
      </div>

      <ul className="link-rows">
        {rows.map((row) => (
          <LinkRow key={`${row.groupId}-${row.linkGuid}`} row={row} />
        ))}
      </ul>
    </div>
  );
}

/**
 * Sharing links as the library scans found them.
 *
 * Narrower than the site-groups view in two ways that must be stated rather
 * than glossed: it covers only scanned libraries, and it cannot show orphaned
 * links because a deleted file has no permissions left to read.
 */
function DerivedLinks({
  links,
  libraries,
  siteTitle,
  siteWebUrl,
  generatedBy,
  api,
}: {
  links: DerivedLink[];
  libraries: string[];
  siteTitle: string;
  siteWebUrl: string;
  generatedBy: string;
  api: ApiClient;
}) {
  const counts = tallyScopes(links);

  const download = () => {
    const rows = links.map((link) => [
      link.path,
      link.isFolder ? 'Folder' : 'File',
      link.scope,
      link.roles.join('; '),
      link.recipients,
      link.external,
      link.expiry ?? '',
      '',
      '',
      'Resolved',
    ]);

    const content = buildCsv(
      {
        siteTitle,
        siteUrl: siteWebUrl,
        scope: 'sharing links in scanned libraries',
        generatedBy,
        coverage: `Covers ${libraries.join(', ')}. Found from the library scans, so links in libraries that have not been scanned are NOT included, and orphaned links — where the file was deleted but the link survives — cannot be detected this way.`,
      },
      ['Path', 'ItemType', 'LinkScope', 'LinkRole', 'Recipients', 'External', 'Expiry', 'HasPassword', 'GroupId', 'Status'],
      rows,
    );

    downloadCsv(exportFileName(siteTitle, 'sharing-links'), content);
    void api.recordExport(`${siteTitle} sharing links`, rows.length, 'download');
  };

  return (
    <div className="tab-body">
      <div className="tab-header">
        <h2>{links.length === 1 ? '1 sharing link' : `${links.length} sharing links`}</h2>
        <div className="tab-actions">
          <button type="button" className="text-button" onClick={download}>
            Export
          </button>
        </div>
      </div>

      {/* Persistent. A count that silently covers only part of a site is worse
          than no count. */}
      <p className="scope-note">
        Found in {libraries.join(', ')}. Libraries you haven't scanned aren't included, and orphaned
        links — where the file was deleted but the link survives — can't be found this way.
      </p>

      <div className="chips">
        <Chip label="Anyone" count={counts.anyone} tint="warning" />
        <Chip label="Organization" count={counts.organization} />
        <Chip label="Specific people" count={counts.specific} />
        {counts.other > 0 && <Chip label="Other" count={counts.other} />}
      </div>

      {links.length === 0 ? (
        <p className="empty">No sharing links in the scanned libraries</p>
      ) : (
        <ul className="link-rows">
          {links.map((link, index) => (
            <li className="link-row" key={`${link.path}-${index}`}>
              <span className="link-path">{link.path}</span>
              <span className="badges">
                <span className={link.scope === 'anonymous' ? 'badge warning' : 'badge'}>
                  {scopeLabel(link.scope)}
                </span>
                {link.external && <span className="badge danger">External</span>}
                <span className="badge">{link.roles.includes('write') ? 'Can edit' : 'Can view'}</span>
                <span className="badge">
                  {link.expiry ? `Expires ${new Date(link.expiry).toLocaleDateString()}` : 'No expiry'}
                </span>
                <span className="badge">{link.library}</span>
              </span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function Chip({ label, count, tint }: { label: string; count: number; tint?: 'warning' }) {
  return (
    <span className={tint === 'warning' && count > 0 ? 'chip-count warning' : 'chip-count'}>
      {label} {count}
    </span>
  );
}

function LinkRow({ row }: { row: SharingLinkRow }) {
  const detail = row.detail;

  if (detail === null && row.failed !== undefined) {
    // Tried and could not. Saying so beats a skeleton that never fills in,
    // which reads as "still loading" long after the scan has finished.
    return (
      <li className="link-row">
        <span className="link-path orphaned">Couldn't resolve this link</span>
        <span className="badges">
          <span className="badge danger">{row.failed}</span>
          <span className="badge">{row.kind}</span>
        </span>
      </li>
    );
  }

  if (detail === null) {
    // Flat placeholder bars at the height of a resolved row, so the list does
    // not reflow as rows fill in.
    return (
      <li className="link-row unresolved">
        <span className="placeholder wide" />
        <span className="placeholder narrow" />
      </li>
    );
  }

  if (detail.status === 'ItemUnreadable') {
    // Deliberately not "Orphaned". That would assert the file was deleted, and
    // SharePoint only refused to answer — the file is probably sitting there.
    // The link itself is real either way, so the row stays.
    return (
      <li className="link-row">
        <span className="link-path unreadable">Item couldn't be read</span>
        <span className="badges">
          <span className="badge warning">Not checked</span>
          <span className="badge">{row.kind}</span>
        </span>
      </li>
    );
  }

  if (detail.status === 'Orphaned') {
    // Kept, not discarded. The item is gone but the group survives, and one
    // still holding an external guest is a finding Microsoft's own sharing
    // reports do not show.
    return (
      <li className="link-row">
        <span className="link-path orphaned">Orphaned</span>
        <span className="badges">
          <span className="badge">Item deleted</span>
          {/* Who still holds the link is the whole point of an orphan, so an
              unreadable membership is stated rather than left as an absence
              that reads like "nobody". */}
          {detail.membersUnavailable ? (
            <span className="badge warning">Members not readable</span>
          ) : (
            <>
              {detail.members.length > 0 && (
                <span className="badge">{recipientSummary(detail.members)}</span>
              )}
              {detail.members.some((member) => member.external) && (
                <span className="badge danger">External</span>
              )}
            </>
          )}
        </span>
      </li>
    );
  }

  if (detail.status === 'ScopeUnavailable' || detail.permission === null) {
    return (
      <li className="link-row">
        <span className="link-path">{detail.path}</span>
        <span className="badges">
          <span className="badge">
            Scope unavailable
            <InfoTip text={scopeUnavailableReason(detail.scopeReason)} />
          </span>
        </span>
      </li>
    );
  }

  const { permission } = detail;
  const external = permission.recipients.some((recipient) => recipient.external);

  return (
    <li className="link-row">
      <span className="link-path">{detail.path}</span>
      <span className="badges">
        <span className={permission.scope === 'anonymous' ? 'badge warning' : 'badge'}>
          {scopeLabel(permission.scope)}
        </span>
        {external && <span className="badge danger">External</span>}
        <span className="badge">{permission.role === 'write' ? 'Can edit' : 'Can view'}</span>
        <span className="badge">
          {permission.expiry === null
            ? 'No expiry'
            : `Expires ${new Date(permission.expiry).toLocaleDateString()}`}
        </span>
        {permission.recipients.length > 0 && (
          <span className="badge">{recipientSummary(permission.recipients)}</span>
        )}
      </span>
    </li>
  );
}

/**
 * Why the audience is missing, in one sentence.
 *
 * Both cases end at the same badge, and neither is a failure to fix: the link
 * is real and the file is named. What differs is why the audience could not be
 * established, and only one of them is worth chasing.
 */
function scopeUnavailableReason(reason: string | null | undefined): string {
  switch (reason) {
    case 'outside-library':
      return "This item isn't in a document library. Link audiences are only readable through a library, so this one can't be determined — the link itself is real.";
    case 'link-not-matched':
      return "The item's permissions were read, but none could be matched to this link with certainty. Showing another link's audience against this file would be worse than showing none.";
    default:
      return "SharePoint didn't return an audience for this link. The link exists and the file is named; who it lets in couldn't be established.";
  }
}

function scopeLabel(scope: string): string {
  switch (scope) {
    case 'anonymous':
      return 'Anyone';
    case 'organization':
      return 'Organization';
    case 'users':
      return 'Specific people';
    case 'existingAccess':
      return 'Existing access';
    default:
      return scope;
  }
}

function recipientSummary(recipients: Recipient[]): string {
  const first = recipients[0];

  if (!first) {
    return 'No recipients';
  }

  const name = first.email ?? first.name;
  return recipients.length === 1 ? name : `${name} and ${recipients.length - 1} more`;
}

function formatSaved(savedAt: string): string {
  return new Date(savedAt).toLocaleDateString(undefined, { day: 'numeric', month: 'short' });
}

/**
 * The chip counts add up to the number of rows that reached a scope, not to
 * the total. A count that included unresolved rows would be a promise the
 * screen has not kept yet.
 */
function tally(rows: SharingLinkRow[]) {
  const counts = { anyone: 0, organization: 0, specific: 0, orphaned: 0 };

  for (const row of rows) {
    const detail = row.detail;

    if (detail === null) {
      continue;
    }

    if (detail.status === 'Orphaned') {
      counts.orphaned += 1;
      continue;
    }

    switch (detail.permission?.scope) {
      case 'anonymous':
        counts.anyone += 1;
        break;
      case 'organization':
        counts.organization += 1;
        break;
      case 'users':
        counts.specific += 1;
        break;
      default:
        break;
    }
  }

  return counts;
}

/**
 * Resolves every row, from the top, with a small pool of workers.
 *
 * Throttling is honoured rather than fought: SharePoint says how long to wait
 * in Retry-After, and the wait is surfaced in the progress text so the bar
 * does not look stalled.
 */
async function resolveAll(
  api: ApiClient,
  siteId: string,
  rows: SharingLinkRow[],
  signal: AbortSignal,
  onThrottle: (until: number | null) => void,
): Promise<void> {
  let next = 0;

  const worker = async (): Promise<void> => {
    while (!signal.aborted) {
      const index = next;
      next += 1;

      const row = rows[index];
      if (!row) {
        return;
      }

      for (let attempt = 0; attempt < 3; attempt += 1) {
        try {
          row.detail = await api.getSharingLinkDetail(siteId, row, signal);
          break;
        } catch (error) {
          if (signal.aborted) {
            return;
          }

          if (error instanceof ApiError && error.isThrottled && attempt < 2) {
            const seconds = error.retryAfterSeconds ?? 5 * (attempt + 1);
            onThrottle(Date.now() + seconds * 1000);
            await delay(seconds * 1000, signal);
            onThrottle(null);
            continue;
          }

          // One row that will not resolve must not stop the other 481, but it
          // must say so rather than sit as a placeholder that never fills in.
          row.detail = null;
          row.failed =
            error instanceof ApiError ? error.message : 'This link could not be resolved.';
          break;
        }
      }
    }
  };

  await Promise.all(Array.from({ length: CONCURRENCY }, worker));
}

function delay(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve) => {
    const timer = window.setTimeout(resolve, ms);
    signal.addEventListener('abort', () => {
      window.clearTimeout(timer);
      resolve();
    });
  });
}
