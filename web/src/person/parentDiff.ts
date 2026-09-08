import type { Assignment } from '../api/client';

export type Origin = 'added' | 'changed' | 'copied';

/**
 * Whether an assignment on a broken item was put there deliberately, or simply
 * copied down when inheritance was broken.
 *
 * SharePoint copies the existing assignments by default when someone breaks
 * inheritance, so a freshly broken folder arrives carrying the site's Owners,
 * Members and Visitors groups. Those are real assignments on the item — which
 * is why they are shown at all — but they are not what anyone broke
 * inheritance *for*.
 *
 * Comparing against the parent separates the two. What is present above is
 * noise; what is not is the finding.
 */
export function originOf(assignment: Assignment, parent: Assignment[]): Origin {
  const match = parent.find((candidate) => samePrincipal(candidate, assignment));

  if (!match) {
    return 'added';
  }

  return sameRoles(match, assignment) ? 'copied' : 'changed';
}

/**
 * The same question for a sharing link, which needs stricter matching.
 *
 * Links carry no principal of their own: every organisation link is named
 * "Anyone in the organization", so the display-name fallback above would
 * happily decide that a link created on this item is a copy of an unrelated
 * link on the library — and suppress the highlight on the very thing that
 * broke inheritance.
 *
 * So a link counts as copied only on an exact identifier match. Anything else
 * is treated as created here, which is almost always the truth: inherited
 * permissions are filtered out before they reach the panel, so a link that
 * appears on an item was made on that item.
 */
export function originOfLink(link: Assignment, parent: Assignment[]): Origin {
  if (!link.principalRef) {
    return 'added';
  }

  const match = parent.find(
    (candidate) =>
      candidate.principalRef?.toLowerCase() === link.principalRef?.toLowerCase(),
  );

  if (!match) {
    return 'added';
  }

  return sameRoles(match, link) ? 'copied' : 'changed';
}

/** True when nothing on the item was added or changed relative to its parent. */
export function allCopied(assignments: Assignment[], parent: Assignment[]): boolean {
  return assignments.length > 0 && assignments.every((a) => originOf(a, parent) === 'copied');
}

function samePrincipal(left: Assignment, right: Assignment): boolean {
  if (left.principalRef && right.principalRef) {
    return left.principalRef.toLowerCase() === right.principalRef.toLowerCase();
  }

  if (left.loginName && right.loginName) {
    return left.loginName.toLowerCase() === right.loginName.toLowerCase();
  }

  return left.principalName.trim().toLowerCase() === right.principalName.trim().toLowerCase();
}

function sameRoles(left: Assignment, right: Assignment): boolean {
  const normalise = (roles: string[]) =>
    [...roles].map((role) => role.toLowerCase()).sort().join(',');

  return normalise(left.roles) === normalise(right.roles);
}
