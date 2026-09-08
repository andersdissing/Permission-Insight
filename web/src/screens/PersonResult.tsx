import type { PrincipalAccess } from '../person/findAccess';
import { kindGlyph, kindLabel } from '../person/localPrincipals';
import { ChevronIcon } from '../components/Icons';

interface Props {
  results: PrincipalAccess[];
  coverage: { scanned: string[]; unscanned: string[] };
  onBack: () => void;
  onExport: () => void;
}

export function PersonResult({ results, coverage, onBack, onExport }: Props) {
  return (
    <div className="tree-screen">
      <div className="tab-header">
        <nav className="breadcrumb">
          <button type="button" className="icon-button" aria-label="Back" onClick={onBack}>
            <ChevronIcon className="chevron-back" />
          </button>
          <button type="button" className="crumb-link" onClick={onBack}>
            Lists and libraries
          </button>
          <span className="crumb-separator">/</span>
          <span className="crumb-current">
            {results.length === 1 ? results[0]?.principal.displayName : `${results.length} principals`}
          </span>
        </nav>

        <div className="tab-actions">
          <button type="button" className="text-button" onClick={onExport}>
            Export
          </button>
        </div>
      </div>

      {/* Persistent, not a dismissible banner. A report that does not say what
          it covered can be read as evidence of absence. */}
      <p className="scope-note">
        Covers {coverage.scanned.join(', ') || 'nothing'}.
        {coverage.unscanned.length > 0 && ` ${coverage.unscanned.join(', ')} not scanned.`}
      </p>

      {results.map((result) => (
        <PrincipalBlock key={result.principal.id} result={result} />
      ))}
    </div>
  );
}

function PrincipalBlock({ result }: { result: PrincipalAccess }) {
  const { principal, topLevel, hits } = result;

  return (
    <section className="principal-result">
      <div className="person-header">
        <span className={principal.kind === 'user' ? 'avatar large' : 'avatar large group'}>
          {kindGlyph(principal.kind) ?? initials(principal.displayName)}
        </span>
        <span className="person-text">
          <strong>{principal.displayName}</strong>
          <span className="muted">
            {kindLabel(principal.kind)}
            {principal.mail ? ` · ${principal.mail}` : ''}
          </span>
        </span>
      </div>

      {/* Its own box, because a grant here reaches everything inside that
          inherits — which is usually thousands of items, not one. */}
      {topLevel.length > 0 && (
        <div className="notice top-level">
          <span>
            Granted at the top level of{' '}
            {topLevel.map((assignment) => assignment.principalName).join(', ')}. This reaches
            everything inside that inherits, not just the items listed below.
          </span>
        </div>
      )}

      {hits.length === 0 ? (
        <p className="muted">
          {topLevel.length > 0
            ? 'Nothing else names this principal directly.'
            : 'Nothing in the scanned libraries names this principal directly. Access may still come through a group — search for the group as well.'}
        </p>
      ) : (
        <ul className="exception-rows">
          {hits.map((hit, index) => (
            <li key={`${hit.path}-${index}`} className="exception neutral">
              <div className="exception-head">
                <span className="link-path">{hit.path}</span>
                <span className="level">{roleLabel(hit.assignment.roles)}</span>
              </div>
              <p className="muted">
                {hit.library} · {hit.assignment.source}
                {hit.assignment.linkScope ? ` · ${hit.assignment.linkScope}` : ''}
              </p>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

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

function initials(displayName: string): string {
  const parts = displayName.trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) {
    return '?';
  }
  const first = parts[0]!.charAt(0);
  const last = parts.length > 1 ? parts[parts.length - 1]!.charAt(0) : '';
  return (first + last).toUpperCase();
}
