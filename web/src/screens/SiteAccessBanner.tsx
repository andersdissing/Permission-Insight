import { useEffect, useState } from 'react';
import type { ApiClient, Assignment, SiteAccessResponse } from '../api/client';

interface Props {
  api: ApiClient;
  siteId: string;
  /** Used to link to SharePoint's own permissions page when the read fails. */
  siteWebUrl: string;
}

/**
 * Access at the site itself, shown above the tabs because it outranks
 * everything below them.
 *
 * A grant here reaches every library and every item that still inherits, so
 * one row can expose the entire site. When that row is a tenant-wide claim —
 * "Everyone", "Everyone except external users" — it is the largest finding the
 * tool can produce, and it is invisible in the item scan: nothing about the
 * site looks unusual, because no inheritance is broken anywhere.
 */
export function SiteAccessBanner({ api, siteId, siteWebUrl }: Props) {
  // Tagged with the site it belongs to rather than cleared when the site
  // changes. Clearing would mean writing state synchronously inside the
  // effect; tagging lets a stale answer be ignored on the way out, so the
  // previous site's permissions can never be shown under this one's name.
  const [result, setResult] = useState<{
    siteId: string;
    access: SiteAccessResponse | null;
  } | null>(null);

  const [expanded, setExpanded] = useState(false);

  useEffect(() => {
    const controller = new AbortController();

    api
      .getSiteAccess(siteId, controller.signal)
      .then((response) => setResult({ siteId, access: response }))
      .catch(() => {
        if (!controller.signal.aborted) {
          setResult({ siteId, access: null });
        }
      });

    return () => controller.abort();
  }, [api, siteId]);

  const current = result?.siteId === siteId ? result : null;

  if (current === null) {
    return null;
  }

  const access = current.access;

  // Both failures say the same thing to the reader, so they read the same way:
  // what is missing, what that means for the rest of the screen, and where to
  // look instead. Not "no findings" — saying nothing here would read as clean.
  if (access === null || !access.determined) {
    const reason =
      access?.reason ??
      "Permission Insight couldn't read the permissions set on the site itself. " +
        'Anything granted at the site — including a grant to the whole organisation — ' +
        "wouldn't be listed below.";

    return (
      <p className="notice site-access-notice">
        <span>{reason}</span>
        <a href={`${siteWebUrl}/_layouts/15/user.aspx`} target="_blank" rel="noreferrer noopener">
          Check site permissions in SharePoint
        </a>
      </p>
    );
  }

  const wide = access.assignments.filter((entry) => entry.tenantWide);
  const rest = access.assignments.filter((entry) => !entry.tenantWide);

  return (
    <section className="site-access">
      {wide.length > 0 && (
        <div className="exposure">
          <h3>
            {wide.length === 1
              ? 'This whole site is open to the organisation'
              : `This whole site is open to the organisation (${wide.length} grants)`}
          </h3>

          <ul className="exposure-rows">
            {wide.map((entry, index) => (
              <li key={`${entry.loginName}-${index}`}>
                <span className="exposure-name">
                  {entry.tenantWide!.label}
                  {entry.tenantWide!.includesExternal && (
                    <span className="badge danger">Includes guests</span>
                  )}
                </span>

                <span className="exposure-roles">{entry.roles.join(', ')}</span>

                <span className="exposure-note">
                  {entry.tenantWide!.description}
                  {/* The tenant's own name for the principal is localised, so
                      it is shown beside the tool's label rather than instead of
                      it: a reader checking in SharePoint needs to recognise the
                      row they'll see there. */}
                  {entry.principalName && entry.principalName !== entry.tenantWide!.label && (
                    <> This tenant calls it &ldquo;{entry.principalName}&rdquo;.</>
                  )}
                </span>
              </li>
            ))}
          </ul>

          <p className="exposure-scope">
            Granted at the site, so it reaches every library and item that still inherits — including
            everything the scans below report as clean.
          </p>
        </div>
      )}

      <p className="site-access-summary">
        {/* Both answers get a label rather than only the quiet one, so the
            state is always stated instead of inferred from an absence. */}
        <span className={`site-access-status ${wide.length === 0 ? 'clear' : 'exposed'}`}>
          {wide.length === 0
            ? 'No organisation-wide grants at the site level'
            : wide.length === 1
              ? '1 organisation-wide grant at the site level'
              : `${wide.length} organisation-wide grants at the site level`}
        </span>
        <button type="button" className="text-button" onClick={() => setExpanded(!expanded)}>
          {expanded ? 'Hide site level access' : `Show site level access (${access.assignments.length})`}
        </button>
        {access.derivedFrom && (
          <span className="site-access-source">Read from {access.derivedFrom.listTitle}, which inherits</span>
        )}
      </p>

      {expanded && (
        <ul className="site-access-rows">
          {[...wide, ...rest].map((entry, index) => (
            <li key={`${entry.loginName}-all-${index}`} className={entry.tenantWide ? 'tenant-wide' : undefined}>
              <span className="principal">
                {entry.principalName}
                {entry.tenantWide && <span className="badge danger">{entry.tenantWide.label}</span>}
              </span>
              <span className="principal-kind">{kindOf(entry)}</span>
              <span className="role">{entry.roles.join(', ')}</span>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

/**
 * A tenant-wide principal is reported as a claim rather than as a user. It
 * arrives as principalType 1 because SharePoint models claims as site users,
 * and calling it "User" would understate it by an organisation.
 */
function kindOf(entry: Assignment): string {
  if (entry.tenantWide) {
    return 'Claim';
  }

  switch (entry.principalType) {
    case 1:
      return 'User';
    case 2:
      return 'Distribution list';
    case 4:
      return 'Entra ID group';
    case 8:
      return 'SharePoint group';
    default:
      return 'Principal';
  }
}
