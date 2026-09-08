import { database, keyFor, type RecentSite } from './database';

/** Ten are kept; the picker shows three. */
const LIMIT = 10;

export async function getRecentSites(objectId: string): Promise<RecentSite[]> {
  const db = await database();
  const record = await db.get('recentSites', keyFor.user(objectId));
  return record?.sites ?? [];
}

/**
 * Moves a site to the front of the list, or adds it. Chips hold only a title
 * and a URL — nothing about permissions ever goes in here.
 */
export async function rememberSite(objectId: string, site: RecentSite): Promise<RecentSite[]> {
  const existing = await getRecentSites(objectId);
  const sites = [site, ...existing.filter((one) => one.siteId !== site.siteId)].slice(0, LIMIT);

  const db = await database();
  await db.put('recentSites', { key: keyFor.user(objectId), sites });

  return sites;
}
