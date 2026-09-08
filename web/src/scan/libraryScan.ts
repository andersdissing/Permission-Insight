import { ApiError, type ApiClient, type ListSummary } from '../api/client';
import type { LibraryScan, ScannedItem } from '../db/scans';

/**
 * The scan could see the content but not the permissions.
 *
 * This is its own error type because it must never be reported as a result.
 * A library whose permissions cannot be read looks exactly like a library
 * where everything inherits, and a governance tool that cannot tell those
 * apart is worse than one that refuses to answer.
 */
export class PermissionsUnreadableError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'PermissionsUnreadableError';
  }
}

/**
 * Whether a completed phase A collected far fewer items than SharePoint said
 * the list holds.
 *
 * Paging stopping early is silent by nature: the loop simply ends and the scan
 * reports whatever it has, confidently. That happened once already — a next
 * link with a percent-encoded dollar was not recognised, so a large library
 * reported exactly 1,000 items and every finding past the first page was
 * invisible.
 *
 * The tolerance is deliberately loose. ItemCount is SharePoint's own figure
 * and can be slightly stale, and items can be added or removed while a scan
 * runs, so small differences are normal and only a wide gap is evidence of
 * anything.
 */
export function isSuspiciouslyIncomplete(collected: number, reported: number | null): boolean {
  // No reported figure, nothing to compare against. The guard goes quiet
  // rather than guessing, and the scan proceeds — the alternative would be
  // refusing every list whose count SharePoint withheld.
  if (reported === null || reported <= 0 || collected >= reported) {
    return false;
  }

  const missing = reported - collected;

  return missing > 25 && collected < reported * 0.9;
}

/** Four workers for phase B. Enough to keep moving, few enough not to invite throttling. */
const CONCURRENCY = 4;

const MAX_ATTEMPTS = 4;

export interface ScanProgress {
  /** 1 while reading items, 2 while reading permissions. */
  step: 1 | 2;
  done: number;
  /**
   * Unknown until phase A finishes, which is why the two steps have their own
   * bars. Null when SharePoint reported no count at all, in which case the bar
   * has no denominator and shows progress without a percentage.
   */
  total: number | null;
  throttled: boolean;
}

export interface ScanOptions {
  api: ApiClient;
  siteId: string;
  list: ListSummary;
  signal: AbortSignal;
  onProgress: (progress: ScanProgress) => void;
}

/**
 * Scans one library.
 *
 * Two phases with separate bars, because the denominator for the second is
 * unknown until the first finishes and weighting two unknowns into one bar
 * produces a number that lurches.
 */
export async function scanLibrary({
  api,
  siteId,
  list,
  signal,
  onProgress,
}: ScanOptions): Promise<LibraryScan> {
  const items = await readItems();
  const broken = items.filter((item) => item.broken);

  await readPermissions(broken);

  return {
    siteId,
    listId: list.id,
    listTitle: list.title,
    savedAt: new Date().toISOString(),
    itemCount: list.itemCount,
    items,
    permissionRoots: broken.map((item) => item.id),
    // Unknown is stored as false: it means the library is not treated as a
    // permission root of its own, which understates rather than invents. The
    // library row shows the uncertainty separately.
    libraryIsPermissionRoot: list.hasUniqueRoleAssignments === true,
  };

  async function readItems(): Promise<ScannedItem[]> {
    const collected: ScannedItem[] = [];
    let token: string | null = null;
    let throttled = false;

    do {
      const page = await withRetry(
        (attemptSignal) => api.getListItems(siteId, list.id, token, attemptSignal),
        (waiting) => {
          throttled = waiting;
          onProgress({ step: 1, done: collected.length, total: list.itemCount, throttled });
        },
      );

      // SharePoint answered, but without the permission flag. Every item would
      // look as though it inherits, and the scan would report a clean library
      // that is not clean. Stop rather than say that.
      // Reached only when SharePoint answers but withholds the flag. A refusal
      // arrives as an ApiError instead and keeps its own remedy.
      if (!page.permissionDataAvailable) {
        throw new PermissionsUnreadableError(
          `${list.title} could be read but its permissions could not. SharePoint returned items without HasUniqueRoleAssignments, so this scan cannot tell which items have unique permissions.`,
        );
      }

      for (const item of page.items) {
        collected.push({
          id: item.id,
          path: item.path,
          name: item.name,
          isFolder: item.isFolder,
          broken: item.broken,
        });
      }

      token = page.nextToken;
      onProgress({ step: 1, done: collected.length, total: list.itemCount, throttled });
    } while (token !== null && !signal.aborted);

    // Paging has finished. If it finished far short of what SharePoint says
    // the list holds, it stopped early rather than ran out, and everything
    // past that point is invisible — including findings.
    if (!signal.aborted && isSuspiciouslyIncomplete(collected.length, list.itemCount)) {
      throw new PermissionsUnreadableError(
        `${list.title} reports ${format(list.itemCount ?? 0)} items but the scan read only ${format(collected.length)}. ` +
          'Paging stopped early, so anything beyond that point — including findings — would be missing from this scan.',
      );
    }

    return collected;
  }

  async function readPermissions(broken: ScannedItem[]): Promise<void> {
    let done = 0;
    let throttled = false;
    let next = 0;
    let failures = 0;

    onProgress({ step: 2, done, total: broken.length, throttled });

    const worker = async (): Promise<void> => {
      while (!signal.aborted) {
        const item = broken[next];
        next += 1;

        if (!item) {
          return;
        }

        try {
          const result = await withRetry(
            (attemptSignal) => api.getItemPermissions(siteId, list.id, item.id, item.path, attemptSignal),
            (waiting) => {
              throttled = waiting;
              onProgress({ step: 2, done, total: broken.length, throttled });
            },
          );

          item.assignments = result.assignments;
        } catch (error) {
          if (signal.aborted) {
            return;
          }

          if (!(error instanceof ApiError)) {
            throw error;
          }

          // A refusal that names a remedy is passed through untouched, so the
          // screen can print the grant command. Wrapping it here would replace
          // "here is how to fix this" with "something went wrong", which is a
          // worse message about the same fact.
          if (error.isSiteNotOnboarded) {
            throw error;
          }

          // A refusal is not a transient failure of one item: the identity
          // cannot read role assignments at all, and every remaining item will
          // fail the same way. Continuing would produce a scan that lists
          // findings with nobody named against them, which reads as "shared
          // with no one".
          if (error.status === 401 || error.status === 403) {
            throw new PermissionsUnreadableError(
              `${list.title} has items with unique permissions, but SharePoint refused to say who has access. The scan found ${broken.length} such items and could read none of them.`,
            );
          }

          // Anything else is treated as this one item's problem. It stays in
          // the tree as broken with no assignments, which reads as "found, not
          // readable" rather than as inheriting.
          item.assignments = [];
          failures += 1;
        }

        done += 1;
        onProgress({ step: 2, done, total: broken.length, throttled });
      }
    };

    await Promise.all(Array.from({ length: CONCURRENCY }, worker));

    // A handful of individual failures is tolerable and visible in the tree.
    // Most of them failing means the result is not worth believing.
    if (broken.length > 0 && failures > broken.length / 2) {
      throw new PermissionsUnreadableError(
        `${list.title} could not be scanned reliably: ${failures} of ${broken.length} items with unique permissions could not be read.`,
      );
    }
  }

  /**
   * Honours Retry-After and backs off exponentially otherwise.
   *
   * The waiting is reported rather than absorbed: a bar that has stopped
   * moving with no explanation looks broken, and this is the one case where
   * the right answer is to wait.
   */
  async function withRetry<T>(
    call: (signal: AbortSignal) => Promise<T>,
    onWait: (waiting: boolean) => void,
  ): Promise<T> {
    let lastError: unknown;

    for (let attempt = 0; attempt < MAX_ATTEMPTS; attempt += 1) {
      if (signal.aborted) {
        throw new DOMException('Scan cancelled', 'AbortError');
      }

      try {
        const result = await call(signal);
        onWait(false);
        return result;
      } catch (error) {
        lastError = error;

        const throttled = error instanceof ApiError && error.isThrottled;
        if (!throttled || attempt === MAX_ATTEMPTS - 1) {
          throw error;
        }

        const seconds = error.retryAfterSeconds ?? 2 ** attempt * 5;
        onWait(true);
        await delay(seconds * 1000, signal);
        onWait(false);
      }
    }

    throw lastError;
  }

  function delay(ms: number, abort: AbortSignal): Promise<void> {
    return new Promise((resolve) => {
      const timer = window.setTimeout(resolve, ms);
      abort.addEventListener('abort', () => {
        window.clearTimeout(timer);
        resolve();
      });
    });
  }
}

export type SizeKind = 'empty' | 'unknown' | 'small' | 'medium' | 'large' | 'extra-large';

/**
 * Size badges. Boundaries are inclusive at the lower end.
 *
 * The badge measures the first of two phases only. A library with 12,000 items
 * and nothing broken finishes quickly; one with 3,000 items and 800 broken is
 * slower, because the second phase costs a call per broken item. So the
 * wording that accompanies this must describe size, never duration.
 *
 * Two of these are not sizes at all. `empty` means there is nothing to scan,
 * so the list is left alone rather than scanned to a result of zero. `unknown`
 * means SharePoint did not report a count — which must not be read as empty,
 * because skipping a list on that basis could silently omit thousands of
 * items. When in doubt the list is scanned.
 */
export function sizeBadge(itemCount: number | null | undefined): {
  label: string;
  warning: boolean;
  kind: SizeKind;
} {
  // Undefined as well as null. A JSON serialiser that omits nulls would
  // otherwise land here as undefined and fall through every comparison to
  // "Small", which is the one answer that is definitely wrong.
  if (typeof itemCount !== 'number') {
    return { label: 'Size unknown', warning: false, kind: 'unknown' };
  }
  if (itemCount <= 0) {
    return { label: 'Empty', warning: false, kind: 'empty' };
  }
  if (itemCount >= 10000) {
    return { label: 'Extra large', warning: true, kind: 'extra-large' };
  }
  if (itemCount >= 6000) {
    return { label: 'Large', warning: true, kind: 'large' };
  }
  if (itemCount >= 2000) {
    return { label: 'Medium', warning: false, kind: 'medium' };
  }
  return { label: 'Small', warning: false, kind: 'small' };
}

/**
 * Whether a list holds nothing, and so has nothing to scan.
 *
 * Deliberately false when the count is unknown. "There is nothing here" and
 * "SharePoint didn't say how much is here" lead to opposite actions, and only
 * one of them is safe to guess.
 */
export function isEmptyList(itemCount: number | null | undefined): boolean {
  return typeof itemCount === 'number' && itemCount <= 0;
}

export function progressText(progress: ScanProgress): string {
  if (progress.throttled) {
    return 'SharePoint is throttling. Waiting, then continuing';
  }

  if (progress.step === 1) {
    // No denominator when SharePoint reported no count. Counting up without a
    // total is honest; inventing 0% or 100% is not.
    if (progress.total === null) {
      return `Step 1 of 2 · Reading items · ${format(progress.done)} so far`;
    }

    const percent = progress.total > 0 ? Math.round((progress.done / progress.total) * 100) : 0;
    return `Step 1 of 2 · Reading items · ${format(progress.done)} of ${format(progress.total)} (${percent}%)`;
  }

  return `Step 2 of 2 · Reading permissions · ${format(progress.done)} of ${format(progress.total ?? 0)}`;
}

export function format(value: number): string {
  return value.toLocaleString('en-US');
}
