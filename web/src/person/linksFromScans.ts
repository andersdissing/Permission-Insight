import type { Assignment } from '../api/client';
import type { LibraryScan } from '../db/scans';

export interface DerivedLink {
  library: string;
  path: string;
  isFolder: boolean;
  scope: string;
  roles: string[];
  expiry: string | null;
  recipients: string;
  external: boolean;
}

/**
 * The sharing links a scan already found.
 *
 * Every sharing link breaks inheritance, so every link sits on an item the
 * library scan has already read permissions for, and Graph returns the link
 * facet in that same response. This needs no further calls at all — it is a
 * different view over data already in the browser.
 *
 * Two things it cannot see, and both matter:
 *
 * Links in libraries nobody has scanned. `09-export.md` promises sharing links
 * can always be exported in full regardless of which libraries were scanned;
 * derived this way, that promise does not hold and the coverage line has to
 * say so.
 *
 * Orphaned links. When the file behind a link is deleted the SharingLinks
 * group survives it, and an orphaned group still holding an external guest is
 * a finding Microsoft's own reports do not show. There is no item left to read
 * permissions from, so it cannot appear here.
 */
export function linksFromScans(scans: LibraryScan[]): DerivedLink[] {
  const links: DerivedLink[] = [];

  for (const scan of scans) {
    for (const item of scan.items) {
      if (!item.broken) {
        continue;
      }

      for (const assignment of item.assignments ?? []) {
        if (assignment.source !== 'Sharing link') {
          continue;
        }

        links.push({
          library: scan.listTitle,
          path: item.path ?? item.name ?? `Item ${item.id}`,
          isFolder: item.isFolder,
          scope: assignment.linkScope ?? 'unknown',
          roles: assignment.roles,
          expiry: assignment.expiry ?? null,
          recipients: assignment.principalName,
          external: isExternal(assignment),
        });
      }
    }
  }

  return links;
}

export function tallyScopes(links: DerivedLink[]) {
  const counts = { anyone: 0, organization: 0, specific: 0, other: 0 };

  for (const link of links) {
    switch (link.scope) {
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
        counts.other += 1;
        break;
    }
  }

  return counts;
}

/**
 * A guest keeps #ext# in the login name even when the address looks internal,
 * and an anonymous link is external by definition whoever holds it.
 */
function isExternal(assignment: Assignment): boolean {
  if (assignment.linkScope === 'anonymous') {
    return true;
  }

  const haystack = `${assignment.loginName ?? ''} ${assignment.principalName}`.toLowerCase();
  return haystack.includes('#ext#') || haystack.includes('urn:spo:guest');
}
