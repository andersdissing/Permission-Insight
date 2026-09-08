import type { Person } from '../api/client';
import type { LibraryScan } from '../db/scans';

/** Graph knows nothing about SharePoint groups, so this is the only source for them. */
export const SHAREPOINT_GROUP = 'sharePointGroup';

/**
 * The SharePoint groups the scans have already seen.
 *
 * Microsoft Graph cannot search SharePoint groups — they are not directory
 * objects and there is no endpoint for them — and `/_api/web/sitegroups`
 * refuses a read-only token. So the picker searches what the scan recorded
 * instead.
 *
 * That is narrower than a directory search in a way worth understanding: it
 * finds only groups that hold a permission somewhere in a scanned library. A
 * SharePoint group that grants nothing will not appear. For this feature that
 * is the right set anyway — a group with no permissions has no access to
 * report — but it means an empty result does not prove the group does not
 * exist.
 */
export function sharePointGroupsFromScans(scans: LibraryScan[], query: string): Person[] {
  const term = query.trim().toLowerCase();

  if (term.length < 2) {
    return [];
  }

  const found = new Map<string, Person>();

  for (const scan of scans) {
    for (const item of scan.items) {
      for (const assignment of item.assignments ?? []) {
        if (assignment.principalType !== 8) {
          continue;
        }

        const name = assignment.principalName.trim();
        if (!name.toLowerCase().includes(term)) {
          continue;
        }

        // Keyed on the identifier where there is one, so the same group found
        // on twenty items appears once.
        const key = (assignment.principalRef ?? name).toLowerCase();

        if (!found.has(key)) {
          found.set(key, {
            id: assignment.principalRef ?? name,
            displayName: name,
            mail: '',
            userPrincipalName: assignment.loginName || name,
            kind: SHAREPOINT_GROUP,
          });
        }
      }
    }
  }

  return [...found.values()].sort((left, right) =>
    left.displayName.localeCompare(right.displayName, undefined, { sensitivity: 'base' }),
  );
}

/** What to call each kind on screen. */
export function kindLabel(kind: string): string {
  switch (kind) {
    case 'user':
      return 'User';
    case 'group':
      return 'Entra ID group';
    case SHAREPOINT_GROUP:
      return 'SharePoint group';
    default:
      return 'Principal';
  }
}

/** A short glyph for the avatar, so the kind reads at a glance. */
export function kindGlyph(kind: string): string | null {
  switch (kind) {
    case 'group':
      return '◍';
    case SHAREPOINT_GROUP:
      return '▣';
    default:
      return null;
  }
}
