import { useEffect, useMemo, useRef, useState } from 'react';
import { ApiError, type ApiClient, type Person } from '../api/client';
import type { LibraryScan } from '../db/scans';
import { kindGlyph, kindLabel, sharePointGroupsFromScans } from '../person/localPrincipals';
import { CloseIcon } from '../components/Icons';

interface Props {
  api: ApiClient;
  /** Searched locally for SharePoint groups, which Graph cannot see. */
  scans: LibraryScan[];
  scannedCount: number;
  totalCount: number;
  running: boolean;
  onCancel: () => void;
  onCheck: (principals: Person[]) => void;
}

/**
 * Picks any number of people and Entra groups at once.
 *
 * Several at a time is the point rather than a convenience. The scan records
 * the principal named on each item, and that is usually a group; following the
 * chain therefore means asking about the person *and* the groups they are in,
 * and comparing. One at a time would make that tedious enough that nobody
 * would do it.
 */
export function PersonSearchDialog({
  api,
  scans,
  scannedCount,
  totalCount,
  running,
  onCancel,
  onCheck,
}: Props) {
  const [query, setQuery] = useState('');
  const [directory, setDirectory] = useState<Person[]>([]);
  const [selected, setSelected] = useState<Person[]>([]);
  const [failed, setFailed] = useState<string | null>(null);
  const [searching, setSearching] = useState(false);

  const dialog = useRef<HTMLDialogElement>(null);

  useEffect(() => {
    dialog.current?.showModal();
  }, []);

  const term = query.trim();

  useEffect(() => {
    if (term.length < 2) {
      return;
    }

    const controller = new AbortController();

    const timer = window.setTimeout(() => {
      setSearching(true);
      api
        .searchPeople(term, controller.signal)
        .then((response) => {
          setDirectory(response.results);
          setFailed(null);
        })
        .catch((error: unknown) => {
          if (!controller.signal.aborted) {
            setFailed(error instanceof ApiError ? error.message : 'The search failed.');
          }
        })
        .finally(() => {
          if (!controller.signal.aborted) {
            setSearching(false);
          }
        });
    }, 300);

    return () => {
      window.clearTimeout(timer);
      controller.abort();
    };
  }, [api, term]);

  // Two sources, because no single one covers both. Graph knows users and
  // Entra groups including those with no access at all; only the scans know
  // SharePoint groups, since Graph has no endpoint for them.
  const results = useMemo(
    () => [...sharePointGroupsFromScans(scans, term), ...directory],
    [scans, term, directory],
  );

  const toggle = (person: Person) => {
    setSelected((current) =>
      current.some((candidate) => candidate.id === person.id)
        ? current.filter((candidate) => candidate.id !== person.id)
        : [...current, person],
    );
  };

  return (
    <dialog ref={dialog} className="person-dialog" onCancel={onCancel}>
      <h2>Search for people or Entra ID group</h2>

      {/* Before the search field, so expectations are set before the result
          rather than after it. */}
      {scannedCount < totalCount && (
        <p className="coverage-warning">
          {scannedCount} of {totalCount} libraries scanned. Results cover scanned content only.
        </p>
      )}

      <label className="field">
        <span>Find people and groups in Entra ID</span>
        <input
          type="search"
          value={query}
          autoFocus
          disabled={running}
          placeholder="Name, or email address"
          onChange={(event) => setQuery(event.target.value)}
        />
      </label>

      {selected.length > 0 && (
        <div className="selected-chips">
          {selected.map((person) => (
            <span className="chip selected-chip" key={person.id}>
              {/* Wrapped so a long name truncates on its own, rather than the
                  chip clipping its own remove button along with it. */}
              <span className="selected-chip-name">{person.displayName}</span>
              <button
                type="button"
                className="chip-remove"
                aria-label={`Remove ${person.displayName}`}
                disabled={running}
                onClick={() => toggle(person)}
              >
                <CloseIcon />
              </button>
            </span>
          ))}
        </div>
      )}

      {failed !== null && <p className="error">{failed}</p>}

      <ul className="people">
        {searching && results.length === 0 && <li className="muted">Searching</li>}
        {!searching && term.length >= 2 && results.length === 0 && failed === null && (
          <li className="muted">Nobody matches that search</li>
        )}

        {results.map((person) => {
          const isSelected = selected.some((candidate) => candidate.id === person.id);

          return (
            <li key={person.id}>
              <button
                type="button"
                className={isSelected ? 'person selected' : 'person'}
                disabled={running}
                onClick={() => toggle(person)}
              >
                <span className={person.kind === 'user' ? 'avatar' : 'avatar group'}>
                  {kindGlyph(person.kind) ?? initials(person.displayName)}
                </span>
                <span className="person-text">
                  <span>{person.displayName}</span>
                  {/* The kind always shows. Two principals can share a name,
                      and a SharePoint group is a different thing to act on
                      from a directory group. */}
                  <span className="muted">
                    {kindLabel(person.kind)}
                    {person.mail ? ` · ${person.mail}` : ''}
                  </span>
                </span>
                {isSelected && <span aria-hidden="true">✓</span>}
              </button>
            </li>
          );
        })}
      </ul>

      <div className="confirm-actions">
        <button type="button" onClick={onCancel}>
          Cancel
        </button>
        <button
          type="button"
          className="primary"
          disabled={selected.length === 0 || running}
          onClick={() => onCheck(selected)}
        >
          {selected.length > 1 ? `Show access (${selected.length})` : 'Show access'}
        </button>
      </div>
    </dialog>
  );
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
