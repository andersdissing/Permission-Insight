import { openDB, type DBSchema, type IDBPDatabase } from 'idb';

/**
 * Everything the application caches lives here, in the user's own browser.
 * There is no server-side database and no scan history: a stored report is a
 * complete index of an organisation's most exposed documents, and not keeping
 * one centrally is a security benefit rather than an omission.
 *
 * Every key starts with the signed-in user's object id. Not for security —
 * anyone who can read the browser profile can read every record regardless —
 * but so the application never serves one user's cached data to another on a
 * shared machine, which is the most likely accidental disclosure and the
 * cheapest one to prevent.
 */
export interface RecentSite {
  siteId: string;
  title: string;
  url: string;
}

export interface RecentSitesRecord {
  key: string;
  sites: RecentSite[];
}

export interface SettingsRecord {
  key: string;
  saveScans: boolean;
}

export interface ScanRecord {
  key: string;
  siteId: string;
  listId: string;
  savedAt: string;
  /** 0 or 1: IndexedDB cannot index a boolean. */
  hasFindings: number;
  /** The whole scan. Typed in db/scans.ts, which owns it. */
  payload: import('./scans').LibraryScan;
}

export interface SharingLinksRecord {
  key: string;
  siteId: string;
  savedAt: string;
  /** The whole resolved scan. Typed in db/sharingLinks.ts, which owns it. */
  payload: import('./sharingLinks').CachedSharingLinks;
}

interface PermissionInsightDB extends DBSchema {
  scans: {
    key: string;
    value: ScanRecord;
    indexes: { siteId: string; hasFindings: number };
  };
  sharingLinks: {
    key: string;
    value: SharingLinksRecord;
    indexes: { siteId: string };
  };
  recentSites: { key: string; value: RecentSitesRecord };
  settings: { key: string; value: SettingsRecord };
}

const DATABASE_NAME = 'permission-insight';
const VERSION = 1;

let connection: Promise<IDBPDatabase<PermissionInsightDB>> | null = null;

export function database(): Promise<IDBPDatabase<PermissionInsightDB>> {
  connection ??= openDB<PermissionInsightDB>(DATABASE_NAME, VERSION, {
    upgrade(db) {
      const scans = db.createObjectStore('scans', { keyPath: 'key' });
      // Search for person gathers permission roots across every cached
      // library, so it needs to find records without reading whole ones.
      scans.createIndex('siteId', 'siteId');
      scans.createIndex('hasFindings', 'hasFindings');

      const sharingLinks = db.createObjectStore('sharingLinks', { keyPath: 'key' });
      sharingLinks.createIndex('siteId', 'siteId');

      db.createObjectStore('recentSites', { keyPath: 'key' });
      db.createObjectStore('settings', { keyPath: 'key' });
    },
  });

  return connection;
}

export const keyFor = {
  user: (objectId: string) => objectId,
  site: (objectId: string, siteId: string) => `${objectId}:${siteId}`,
  list: (objectId: string, siteId: string, listId: string) => `${objectId}:${siteId}:${listId}`,
};
