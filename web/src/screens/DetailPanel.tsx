import { useState } from 'react';
import { ApiError, type ApiClient, type Assignment, type GroupMember } from '../api/client';
import type { ScannedItem } from '../db/scans';
import { CloseIcon, ExternalLinkIcon } from '../components/Icons';
import { originOf, originOfLink, type Origin } from '../person/parentDiff';

interface Props {
  api: ApiClient;
  siteWebUrl: string;
  listId: string;
  item: ScannedItem;
  /** What the parent grants, so the panel can say which entries were added here. */
  parentAccess: Assignment[];
  /**
   * Whether this describes an item in the library or the library itself. The
   * library has no list item behind it, so it has no per-item permissions page
   * in SharePoint to link to.
   */
  scope?: 'item' | 'library';
  onClose: () => void;
}

/**
 * Everything here comes from phase B of the library scan, so the panel opens
 * with no network call and works on a cached scan with no connection at all.
 * The one exception is expanding an Entra group, which is fetched on click.
 *
 * The caller keys this by item id, so selecting a different item without
 * closing first remounts rather than updates. That is what resets the scroll
 * position: otherwise the next item opens halfway down its own list and looks
 * like it failed to update.
 */
export function DetailPanel({
  api,
  siteWebUrl,
  listId,
  item,
  parentAccess,
  scope = 'item',
  onClose,
}: Props) {
  const assignments = item.assignments ?? [];
  const links = assignments.filter((assignment) => assignment.source === 'Sharing link');
  const access = assignments.filter((assignment) => assignment.source !== 'Sharing link');

  // Claims that cover the whole organisation, lifted out of the by-type
  // grouping below. Detected from the claim rather than the name, which is
  // localised — a Danish tenant calls this "Alle undtagen eksterne brugere".
  const tenantWide = access.filter((assignment) => assignment.tenantWide);

  // More than three and the links section would push Access off the screen.
  const [linksOpen, setLinksOpen] = useState(links.length <= 3);

  // Null for a library. The LISTITEM form addresses one item, and there is no
  // item here; the list's own permissions page uses a different address that
  // this codebase has not verified, and a guessed SharePoint URL is worse than
  // no link.
  const manageUrl =
    scope === 'library'
      ? null
      : `${siteWebUrl}/_layouts/15/user.aspx` +
        `?obj=${encodeURIComponent(`{${listId}},${item.id},LISTITEM`)}` +
        `&List=${encodeURIComponent(`{${listId}}`)}`;

  return (
    <aside className="panel" aria-label={`Permissions for ${item.name}`}>
      {/* Fixed, outside the scroll area. Sticky inside it lets rubber-banding
          on touch push the close button out of reach. */}
      <header className="panel-header">
        <div className="panel-title">
          <h3>{item.name}</h3>
          <button type="button" className="icon-button" aria-label="Close" onClick={onClose}>
            <CloseIcon />
          </button>
        </div>
        {item.path && <p className="panel-path">{item.path}</p>}
        {/* A new tab, not a preference: navigating away in this tab would hit
            the leave-site guard mid-action. */}
        {manageUrl !== null && (
          <a href={manageUrl} target="_blank" rel="noreferrer noopener">
            Manage permissions in SharePoint
          </a>
        )}
      </header>

      <div className="panel-body">
        <section>
          <h4>Access</h4>

          {/* First, above the people. A claim reaches more than every row
              below it put together, so it cannot be one entry among them. */}
          {tenantWide.length > 0 && (
            <div className="principal-group organisation-wide">
              <h5>Organisation-wide</h5>
              <ul className="access-rows">
                {tenantWide.map((assignment, index) => (
                  <AccessRow
                    key={`wide-${assignment.loginName}-${index}`}
                    api={api}
                    siteWebUrl={siteWebUrl}
                    assignment={assignment}
                    origin={originOf(assignment, parentAccess)}
                  />
                ))}
              </ul>
            </div>
          )}

          {access.length === 0 ? (
            <p className="muted">
              Nothing is assigned directly on this {scope === 'library' ? 'library' : 'item'}
            </p>
          ) : (
            // Grouped by what the principal is, because that decides what the
            // reader can do about it: a person is removed, a SharePoint group
            // is edited in SharePoint, a directory group is someone else's
            // problem entirely.
            //
            // Tenant-wide claims are pulled out ahead of that grouping. They
            // arrive as principalType 1 because SharePoint models a claim as a
            // site user, so they would otherwise sit among the people as one
            // more name — and this row is not a person, it is everybody.
            PRINCIPAL_GROUPS.map(({ type, heading }) => {
              const rows = access.filter(
                (assignment) =>
                  assignment.principalType === type && !assignment.tenantWide,
              );
              if (rows.length === 0) {
                return null;
              }

              return (
                <div className="principal-group" key={type}>
                  <h5>{heading}</h5>
                  <ul className="access-rows">
                    {rows.map((assignment, index) => (
                      <AccessRow
                        key={`${assignment.principalName}-${assignment.loginName}-${index}`}
                        api={api}
                        siteWebUrl={siteWebUrl}
                        assignment={assignment}
                        origin={originOf(assignment, parentAccess)}
                      />
                    ))}
                  </ul>
                </div>
              );
            })
          )}
        </section>

        {/* Always rendered, even when empty. A missing section reads as "not
            checked"; an empty one reads as "checked, nothing found", and those
            are different answers. */}
        <section>
          <h4>
            {links.length > 3 ? (
              <button
                type="button"
                className="section-toggle"
                aria-expanded={linksOpen}
                onClick={() => setLinksOpen((open) => !open)}
              >
                {linksOpen ? '−' : '+'} Sharing links ({links.length})
              </button>
            ) : (
              `Sharing links${links.length > 0 ? ` (${links.length})` : ''}`
            )}
          </h4>

          {links.length === 0 && <p className="muted">No sharing links on this item</p>}

          {linksOpen &&
            links.map((link, index) => (
              <LinkCard
                key={`${link.loginName}-${index}`}
                link={link}
                origin={originOfLink(link, parentAccess)}
              />
            ))}
        </section>
      </div>
    </aside>
  );
}

function LinkCard({ link, origin }: { link: Assignment; origin: Origin }) {
  const scope = link.linkScope ?? 'unknown';
  const external = /#ext#|\bguest\b/i.test(link.loginName ?? '');

  return (
    // Marked like an access row, and for the same reason. A link that is not
    // on the parent was created on this item, which is usually the very reason
    // inheritance broke — so it is the finding, not context around one.
    <div className={origin === 'copied' ? 'link-card' : `link-card ${origin}`}>
      <div className="badges">
        <span className={scope === 'anonymous' ? 'badge warning' : 'badge'}>
          {scopeLabel(scope)}
        </span>
        <span className="badge">{roleLabel(link.roles)}</span>
        <span className="badge">
          {link.expiry ? `Expires ${new Date(link.expiry).toLocaleDateString()}` : 'No expiry'}
        </span>
        {external && <span className="badge danger">External</span>}
        {origin === 'added' && <span className="origin-tag">Added here</span>}
        {origin === 'changed' && <span className="origin-tag">Changed here</span>}
      </div>

      {link.principalName && <p className="muted">{link.principalName}</p>}

      {/* The one scope whose consequence is not obvious from its name. */}
      {scope === 'anonymous' && (
        <p className="warning-note">No sign-in required. Anyone holding the link can open it.</p>
      )}
    </div>
  );
}

/** Users first: they are the ones a reader can act on directly. */
const PRINCIPAL_GROUPS = [
  { type: 1, heading: 'Users' },
  { type: 8, heading: 'SharePoint groups' },
  { type: 4, heading: 'Entra groups' },
  { type: 2, heading: 'Distribution lists' },
  { type: 0, heading: 'Other principals' },
] as const;

function AccessRow({
  api,
  siteWebUrl,
  assignment,
  origin,
}: {
  api: ApiClient;
  siteWebUrl: string;
  assignment: Assignment;
  origin: Origin;
}) {
  const [members, setMembers] = useState<GroupMember[] | null>(null);
  const [expanded, setExpanded] = useState(false);
  const [failed, setFailed] = useState<string | null>(null);

  // 4 is an Entra group, and Graph can open one. 8 is a SharePoint group,
  // whose membership lives behind an endpoint that read-only permissions
  // cannot reach and for which Graph has no equivalent.
  const isEntraGroup = assignment.principalType === 4;
  const isSharePointGroup = assignment.principalType === 8;

  const groupLink = deepLink(assignment, siteWebUrl);

  const toggle = () => {
    const next = !expanded;
    setExpanded(next);

    if (next && members === null && failed === null) {
      void api
        .getEntraGroupMembers(assignment.principalRef ?? assignment.loginName)
        .then((response) => setMembers(response.members))
        .catch((error: unknown) =>
          setFailed(error instanceof ApiError ? error.message : 'The members could not be read.'),
        );
    }
  };

  return (
    // Highlighted when it was added or changed here. SharePoint copies the
    // parent's assignments down when inheritance is broken, so most rows on a
    // broken item are noise; the ones that differ are what somebody actually
    // did.
    <li className={origin === 'copied' ? 'access-row' : `access-row ${origin}`}>
      <div className="access-head">
        {isEntraGroup ? (
          <button type="button" className="expander" aria-expanded={expanded} onClick={toggle}>
            {expanded ? '−' : '+'}
          </button>
        ) : (
          <span className="expander-spacer" />
        )}

        <span className="principal">
          <span className="principal-name">
            {assignment.principalName}
            {/* Straight to the group, in a new tab. Same tab would hit the
                leave-site guard mid-action. */}
            {groupLink !== null && (
              <a
                href={groupLink}
                target="_blank"
                rel="noreferrer noopener"
                className="principal-link"
                aria-label={`Open ${assignment.principalName}`}
                title={`Open ${assignment.principalName}`}
              >
                <ExternalLinkIcon />
              </a>
            )}
          </span>
          <span className="principal-kind">
            {assignment.tenantWide ? 'Claim' : kindLabel(assignment.principalType)}
            {assignment.tenantWide && (
              <span className="badge danger">{assignment.tenantWide.label}</span>
            )}
            {assignment.tenantWide?.includesExternal && (
              <span className="badge danger">Includes guests</span>
            )}
            {origin === 'added' && <span className="origin-tag">Added here</span>}
            {origin === 'changed' && <span className="origin-tag">Role changed here</span>}
          </span>
        </span>

        {/* Shown only at the top level. Repeating it per member would imply
            individual assignments that do not exist. */}
        <span className="role">{roleLabel(assignment.roles)}</span>
      </div>

      {/* Said plainly rather than left as an expander that fails. Reading
          SharePoint group membership needs rights the tool deliberately does
          not hold, and Graph has no equivalent endpoint. */}
      {isSharePointGroup && <p className="chain-stops">Members not shown</p>}

      {/* There is nothing to expand and nobody to enumerate, which is exactly
          what makes this easy to miss: the row looks like one principal. */}
      {assignment.tenantWide && (
        <p className="warning-note">{assignment.tenantWide.description}</p>
      )}

      {expanded && (
        <ul className="member-rows">
          {failed !== null && <li className="error">{failed}</li>}
          {members === null && failed === null && <li className="muted">Reading members</li>}
          {members?.length === 0 && <li className="muted">This group has no direct members</li>}
          {members?.map((member) => (
            <li key={`${member.name}-${member.email ?? ''}`}>
              <span className="principal">
                <span className="principal-name">{member.name}</span>
                {member.email && <span className="principal-kind">{member.email}</span>}
              </span>
              {member.external && <span className="badge danger">External</span>}
              {/* Never render a nested group as if it were empty. The chain
                  stops one level down, and saying so beats implying nobody is
                  in it. */}
              {member.isEntraGroup && <span className="badge">Nested group</span>}
            </li>
          ))}
        </ul>
      )}
    </li>
  );
}

/**
 * Where to send someone who wants to look at the group itself.
 *
 * A SharePoint group is managed inside the site; an Entra group or
 * distribution list is managed in the directory, which is usually a different
 * team's responsibility. Users get no link: there is nothing useful to open.
 */
function deepLink(assignment: Assignment, siteWebUrl: string): string | null {
  const id = assignment.principalRef;
  if (!id) {
    return null;
  }

  if (assignment.principalType === 8) {
    return `${siteWebUrl}/_layouts/15/people.aspx?MembershipGroupId=${encodeURIComponent(id)}`;
  }

  if (assignment.principalType === 4 || assignment.principalType === 2) {
    return `https://entra.microsoft.com/#view/Microsoft_AAD_IAM/GroupDetailsMenuBlade/~/Overview/groupId/${encodeURIComponent(id)}`;
  }

  return null;
}

function kindLabel(principalType: number): string {
  switch (principalType) {
    case 1:
      return 'User';
    case 2:
      return 'Distribution list';
    case 4:
      return 'Entra group';
    case 8:
      return 'SharePoint group';
    default:
      return 'Unknown principal';
  }
}

/** Graph returns read, write and owner. SharePoint's own words read better. */
function roleLabel(roles: string[]): string {
  if (roles.length === 0) {
    return 'No role';
  }

  return roles
    .map((role) => {
      switch (role.toLowerCase()) {
        case 'read':
          return 'Read';
        case 'write':
          return 'Edit';
        case 'owner':
        case 'fullcontrol':
          return 'Full control';
        default:
          return role.charAt(0).toUpperCase() + role.slice(1);
      }
    })
    .join(', ');
}

function scopeLabel(scope: string): string {
  switch (scope) {
    case 'anonymous':
      return 'Anyone';
    case 'organization':
      return 'Organization';
    case 'users':
      return 'Specific people';
    case 'existingAccess':
      return 'Existing access';
    default:
      return 'Sharing link';
  }
}
