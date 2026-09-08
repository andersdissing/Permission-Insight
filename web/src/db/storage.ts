import { database, keyFor } from './database';

export interface SavedScanSummary {
  /** Library scans plus sharing link scans. The number on the clear button. */
  count: number;
  /** Bytes, as the browser estimates them. Null when the browser will not say. */
  bytes: number | null;
}

export async function getSettings(objectId: string): Promise<{ saveScans: boolean }> {
  const db = await database();
  const record = await db.get('settings', keyFor.user(objectId));
  return { saveScans: record?.saveScans ?? true };
}

export async function setSaveScans(objectId: string, saveScans: boolean): Promise<void> {
  const db = await database();
  await db.put('settings', { key: keyFor.user(objectId), saveScans });
}

export async function summariseSavedScans(objectId: string): Promise<SavedScanSummary> {
  const db = await database();
  const prefix = `${keyFor.user(objectId)}:`;
  const range = IDBKeyRange.bound(prefix, `${prefix}￿`);

  const [scans, sharingLinks] = await Promise.all([
    db.count('scans', range),
    db.count('sharingLinks', range),
  ]);

  return { count: scans + sharingLinks, bytes: await estimateBytes() };
}

/**
 * Removes the scan results this user has stored.
 *
 * Two things are deliberately kept. The save toggle is a preference, not data:
 * it governs future saves where this button removes past ones, and keeping
 * them separate is what resolves an otherwise ambiguous state.
 *
 * The list of recent sites is kept for the same reason. It is navigation
 * history — which sites you were looking at — not the permission data the
 * button exists to clear, and clearing it logged the user out of their own
 * place in the tool: the current site vanished mid-session and had to be
 * searched for again. Nothing in a site's title or URL is a finding.
 */
export async function clearSavedScans(objectId: string): Promise<void> {
  const db = await database();
  const prefix = `${keyFor.user(objectId)}:`;
  const range = IDBKeyRange.bound(prefix, `${prefix}￿`);

  const transaction = db.transaction(['scans', 'sharingLinks'], 'readwrite');

  await Promise.all([
    transaction.objectStore('scans').delete(range),
    transaction.objectStore('sharingLinks').delete(range),
    transaction.done,
  ]);
}

/**
 * Removes the recent sites list as well, for someone who wants no trace left.
 *
 * Separate from clearing scans because the two answer different questions:
 * "delete what was read" and "delete where I've been".
 */
export async function clearRecentSites(objectId: string): Promise<void> {
  const db = await database();
  await db.delete('recentSites', keyFor.user(objectId));
}

/**
 * Asks the browser not to evict the database under disk pressure. The request
 * can be refused, so "saved" is best-effort and the interface must not promise
 * otherwise.
 */
export async function requestPersistence(): Promise<boolean> {
  if (!navigator.storage?.persist) {
    return false;
  }

  try {
    return await navigator.storage.persist();
  } catch {
    return false;
  }
}

async function estimateBytes(): Promise<number | null> {
  if (!navigator.storage?.estimate) {
    return null;
  }

  try {
    const estimate = await navigator.storage.estimate();
    return estimate.usage ?? null;
  } catch {
    return null;
  }
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) {
    return `${bytes} B`;
  }

  const units = ['kB', 'MB', 'GB'];
  let value = bytes / 1024;
  let unit = 0;

  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit += 1;
  }

  return `${value < 10 ? value.toFixed(1) : Math.round(value)} ${units[unit]}`;
}
