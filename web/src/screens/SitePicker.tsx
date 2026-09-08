import { useEffect, useMemo, useRef, useState } from 'react';
import type { ApiClient, Site } from '../api/client';
import { ApiError } from '../api/client';
import type { RecentSite } from '../db/database';
import { ChevronIcon, CloseIcon, SearchIcon } from '../components/Icons';

const DEBOUNCE_MS = 300;
const MINIMUM_QUERY = 2;
/** Matches the API's ceiling. Beyond it the user is told to refine. */
const CHIPS_SHOWN = 3;

type Status =
  | { kind: 'idle' }
  | { kind: 'searching' }
  | { kind: 'results'; results: Site[]; truncated: boolean }
  | { kind: 'failed'; message: string };

/** Tagged with the term it answers, so a slow reply never lands on a newer query. */
type Outcome = { term: string; value: Extract<Status, { kind: 'results' | 'failed' }> };

interface Props {
  api: ApiClient;
  recent: RecentSite[];
  onSelect: (site: RecentSite) => void;
}

export function SitePicker({ api, recent, onSelect }: Props) {
  const [query, setQuery] = useState('');
  const [outcome, setOutcome] = useState<Outcome | null>(null);
  const input = useRef<HTMLInputElement>(null);

  const trimmed = query.trim();
  const term = trimmed.length >= MINIMUM_QUERY ? trimmed : null;

  useEffect(() => {
    if (term === null) {
      return;
    }

    const controller = new AbortController();

    const timer = window.setTimeout(() => {
      api
        .searchSites(term, controller.signal)
        .then((response) => {
          setOutcome({
            term,
            value: { kind: 'results', results: response.results, truncated: response.truncated },
          });
        })
        .catch((error: unknown) => {
          if (controller.signal.aborted) {
            return;
          }

          setOutcome({
            term,
            value: {
              kind: 'failed',
              message:
                error instanceof ApiError ? error.message : 'The search could not be completed.',
            },
          });
        });
    }, DEBOUNCE_MS);

    return () => {
      window.clearTimeout(timer);
      controller.abort();
    };
  }, [api, term]);

  // Derived rather than stored. Anything the current term has no answer for
  // yet is still searching, which also stops a stale result showing under a
  // newer query.
  const status: Status =
    term === null ? { kind: 'idle' } : outcome?.term === term ? outcome.value : { kind: 'searching' };

  const chips = useMemo(() => recent.slice(0, CHIPS_SHOWN), [recent]);

  return (
    <main className="picker">
      <h1>Select a site</h1>
      <p className="subtitle">
        Search for the site you want to check for broken inheritance and sharing links.
      </p>

      <div className="search-box">
        <SearchIcon className="search-icon" />
        <input
          ref={input}
          type="search"
          value={query}
          placeholder="Search by title or URL"
          aria-label="Search for a site"
          autoFocus
          onChange={(event) => setQuery(event.target.value)}
        />
        {query.length > 0 && (
          <button
            type="button"
            className="icon-button"
            aria-label="Clear search"
            onClick={() => {
              setQuery('');
              input.current?.focus();
            }}
          >
            <CloseIcon />
          </button>
        )}
      </div>

      {chips.length > 0 && (
        <div className="recent">
          <span className="recent-label">Recently used</span>
          {chips.map((site) => (
            <button
              type="button"
              key={site.siteId}
              className="chip"
              title={site.url}
              onClick={() => onSelect(site)}
            >
              {site.title}
            </button>
          ))}
        </div>
      )}

      <Results status={status} hasRecent={chips.length > 0} onSelect={onSelect} />

      {/* Not a hint. With application permissions the result set is not
          trimmed to what this user can open, and that is worth stating. */}
      <p className="scope-note">Searching across all sites in your organization.</p>
    </main>
  );
}

function Results({
  status,
  hasRecent,
  onSelect,
}: {
  status: Status;
  hasRecent: boolean;
  onSelect: (site: RecentSite) => void;
}) {
  if (status.kind === 'idle') {
    // With no query the recently used row is the content. A first-time user
    // has neither, so invite the search rather than showing nothing.
    return hasRecent ? null : (
      <p className="empty">Search for a site to get started</p>
    );
  }

  if (status.kind === 'searching') {
    return <p className="empty">Searching</p>;
  }

  if (status.kind === 'failed') {
    return <p className="error">{status.message}</p>;
  }

  if (status.results.length === 0) {
    // Never suggest the user lacks access. With application permissions a
    // missing result means the site does not exist or the query is wrong.
    return <p className="empty">No site matches that search</p>;
  }

  return (
    <>
      <p className="result-count">
        {status.results.length === 1 ? '1 result' : `${status.results.length} results`}
      </p>

      <ul className="results">
        {status.results.map((site) => (
          <li key={site.id}>
            <button
              type="button"
              onClick={() => onSelect({ siteId: site.id, title: site.title, url: site.url })}
            >
              <span className="result-text">
                <span className="result-title">{site.title}</span>
                <span className="result-url">{site.url}</span>
              </span>
              <ChevronIcon className="result-chevron" />
            </button>
          </li>
        ))}
      </ul>

      {status.truncated && (
        <p className="truncated">More than {status.results.length} sites match. Refine your search.</p>
      )}
    </>
  );
}
