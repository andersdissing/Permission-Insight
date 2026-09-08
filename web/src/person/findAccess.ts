import type { Assignment, Person } from '../api/client';
import type { LibraryScan, ScannedItem } from '../db/scans';

export interface AccessHit {
  library: string;
  path: string;
  isFolder: boolean;
  assignment: Assignment;
}

export interface PrincipalAccess {
  principal: Person;
  /** Granted on the library itself, so it reaches everything inside that inherits. */
  topLevel: Assignment[];
  /** Items that name this principal in their own right. */
  hits: AccessHit[];
}

/**
 * Decides whether an assignment names a given principal.
 *
 * Matching is by identifier first, then by address, then by display name. The
 * name is last and is the weakest: two people can share one, so it is only
 * reached when nothing better is available.
 */
export function matches(assignment: Assignment, principal: Person): boolean {
  const ref = assignment.principalRef?.toLowerCase();
  const id = principal.id.toLowerCase();

  if (ref && ref === id) {
    return true;
  }

  const login = assignment.loginName?.toLowerCase() ?? '';
  const address = (principal.mail || principal.userPrincipalName).toLowerCase();

  if (address.length > 0 && login.includes(address)) {
    return true;
  }

  return (
    assignment.principalName.trim().toLowerCase() === principal.displayName.trim().toLowerCase()
  );
}

/**
 * Everything the cached scans say about one principal.
 *
 * This reads what the scan already recorded rather than asking SharePoint
 * again. It therefore reports **direct** naming only: if a person is reached
 * through a group, the group appears rather than the person. Searching for
 * that group as well is how you follow the chain, which is why the picker
 * takes several principals at once.
 */
export function findAccess(
  principal: Person,
  scans: LibraryScan[],
  topLevelByList: Map<string, Assignment[]>,
): PrincipalAccess {
  const hits: AccessHit[] = [];
  const topLevel: Assignment[] = [];

  for (const [listId, assignments] of topLevelByList) {
    const scan = scans.find((candidate) => candidate.listId === listId);

    for (const assignment of assignments) {
      if (matches(assignment, principal)) {
        topLevel.push({ ...assignment, principalName: scan?.listTitle ?? assignment.principalName });
      }
    }
  }

  for (const scan of scans) {
    for (const item of scan.items) {
      if (!item.broken) {
        continue;
      }

      for (const assignment of item.assignments ?? []) {
        if (matches(assignment, principal)) {
          hits.push({
            library: scan.listTitle,
            path: item.path ?? itemLabel(item),
            isFolder: item.isFolder,
            assignment,
          });
        }
      }
    }
  }

  return { principal, topLevel, hits };
}

function itemLabel(item: ScannedItem): string {
  return item.name ?? `Item ${item.id}`;
}
