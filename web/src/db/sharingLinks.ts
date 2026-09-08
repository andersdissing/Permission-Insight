import type { SharingLinkDetail, SharingLinkGroup } from '../api/client';
import { database, keyFor } from './database';

/** One row: the group, plus its resolution once stage two has reached it. */
export interface SharingLinkRow extends SharingLinkGroup {
  detail: SharingLinkDetail | null;
  /**
   * Resolution was attempted and did not succeed.
   *
   * Distinguished from a null detail that has simply not been reached yet,
   * because the two look identical on screen and must not: an unresolved row
   * shows placeholder bars, and leaving those in place after the scan has
   * finished reads as "still loading" forever.
   */
  failed?: string;
}

export interface CachedSharingLinks {
  siteId: string;
  siteTitle: string;
  siteWebUrl: string;
  savedAt: string;
  rows: SharingLinkRow[];
}

export async function getCachedSharingLinks(
  objectId: string,
  siteId: string,
): Promise<CachedSharingLinks | null> {
  const db = await database();
  const record = await db.get('sharingLinks', keyFor.site(objectId, siteId));

  return record?.payload ?? null;
}

/**
 * Written once, at the end of stage two.
 *
 * Never incrementally: a partial result cached as complete would be
 * indistinguishable from a finished scan on the next visit, and the whole
 * point of the cache is that re-entering a site does not rescan.
 */
export async function cacheSharingLinks(
  objectId: string,
  payload: CachedSharingLinks,
): Promise<void> {
  const db = await database();

  await db.put('sharingLinks', {
    key: keyFor.site(objectId, payload.siteId),
    siteId: payload.siteId,
    savedAt: payload.savedAt,
    payload,
  });
}
