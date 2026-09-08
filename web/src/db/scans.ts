import type { Assignment } from '../api/client';
import { database, keyFor } from './database';

/**
 * One item as the scan found it.
 *
 * Every item is kept, not only the broken ones. The tree needs the full
 * hierarchy or it has holes, and a folder that inherits is still a folder the
 * user has to walk through to reach a finding.
 */
export interface ScannedItem {
  id: number;
  path: string | null;
  name: string | null;
  isFolder: boolean;
  broken: boolean;
  /** Present only where broken is true. Limited Access is already filtered out. */
  assignments?: Assignment[];
}

export interface LibraryScan {
  siteId: string;
  listId: string;
  listTitle: string;
  savedAt: string;
  /** What SharePoint reported before the scan started. Null if it reported nothing. */
  itemCount: number | null;
  items: ScannedItem[];
  /**
   * The item ids that carry their own permissions, plus whether the library
   * itself does. Search for person queries exactly these and would otherwise
   * force a rescan, which is why they are stored rather than derived later.
   */
  permissionRoots: number[];
  libraryIsPermissionRoot: boolean;
}

export function findingCount(scan: LibraryScan): number {
  return scan.permissionRoots.length;
}

export async function getCachedScan(
  objectId: string,
  siteId: string,
  listId: string,
): Promise<LibraryScan | null> {
  const db = await database();
  const record = await db.get('scans', keyFor.list(objectId, siteId, listId));
  return record?.payload ?? null;
}

export async function getCachedScansForSite(
  objectId: string,
  siteId: string,
): Promise<Map<string, LibraryScan>> {
  const db = await database();
  const prefix = `${keyFor.user(objectId)}:${siteId}:`;
  const records = await db.getAll('scans', IDBKeyRange.bound(prefix, `${prefix}￿`));

  return new Map(records.map((record) => [record.listId, record.payload]));
}

/**
 * Written once, when both phases have finished. Caching per library rather
 * than per site is what lets one library be saved while another is never
 * touched.
 */
export async function cacheScan(objectId: string, scan: LibraryScan): Promise<void> {
  const db = await database();

  await db.put('scans', {
    key: keyFor.list(objectId, scan.siteId, scan.listId),
    siteId: scan.siteId,
    listId: scan.listId,
    savedAt: scan.savedAt,
    hasFindings: scan.permissionRoots.length > 0 ? 1 : 0,
    payload: scan,
  });
}
