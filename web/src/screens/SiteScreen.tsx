import { useCallback, useState } from 'react';
import type { ApiClient } from '../api/client';
import type { RecentSite } from '../db/database';
import { SearchIcon } from '../components/Icons';
import { SharingLinksTab } from './SharingLinksTab';
import { LibrariesTab } from './LibrariesTab';
import { SiteAccessBanner } from './SiteAccessBanner';

type Tab = 'links' | 'libraries';

interface Props {
  api: ApiClient;
  objectId: string;
  site: RecentSite;
  saveScans: boolean;
  generatedBy: string;
  onSaved: () => void;
  onBack: () => void;
}

export function SiteScreen({
  api,
  objectId,
  site,
  saveScans,
  generatedBy,
  onSaved,
  onBack,
}: Props) {
  const [tab, setTab] = useState<Tab>('links');
  const [linkTotal, setLinkTotal] = useState<number | null>(null);

  // The libraries tab starts its own scan on open, so it is not mounted until
  // the user asks for it. Once mounted it stays, and a scan survives switching
  // back to the sharing links.
  const [opened, setOpened] = useState({ libraries: false });

  // Stable, so changing it does not restart the scan.
  const onTotalChange = useCallback((total: number | null) => setLinkTotal(total), []);

  return (
    <main className="site">
      <div className="search-box framed">
        <SearchIcon className="search-icon" />
        <span className="selected-site">
          <span className="result-title">{site.title}</span>
          <span className="result-url">{site.url}</span>
        </span>
        <button type="button" className="text-button" onClick={onBack}>
          Change site
        </button>
      </div>

      {/* Above the tabs, and outside them. A grant at the site reaches both
          what the links tab lists and what the libraries tab scans, so it
          belongs to neither and outranks both. */}
      <SiteAccessBanner api={api} siteId={site.siteId} siteWebUrl={site.url} />

      <div className="tabs" role="tablist">
        <button
          type="button"
          role="tab"
          aria-selected={tab === 'links'}
          className={tab === 'links' ? 'tab selected' : 'tab'}
          onClick={() => setTab('links')}
        >
          Sharing links
          {linkTotal !== null && <span className="tab-badge">{linkTotal}</span>}
        </button>

        <button
          type="button"
          role="tab"
          aria-selected={tab === 'libraries'}
          className={tab === 'libraries' ? 'tab selected' : 'tab'}
          onClick={() => {
            setTab('libraries');
            setOpened((previous) => ({ ...previous, libraries: true }));
          }}
        >
          Lists and libraries
        </button>
      </div>

      {/* Both tabs stay mounted once opened, so switching away does not throw
          away a scan that is still running. */}
      <div hidden={tab !== 'links'}>
        <SharingLinksTab
          api={api}
          objectId={objectId}
          site={site}
          saveScans={saveScans}
          generatedBy={generatedBy}
          onTotalChange={onTotalChange}
          onSaved={onSaved}
        />
      </div>

      {opened.libraries && (
        <div hidden={tab !== 'libraries'}>
          <LibrariesTab
            api={api}
            objectId={objectId}
            site={site}
            saveScans={saveScans}
            generatedBy={generatedBy}
            onSaved={onSaved}
          />
        </div>
      )}
    </main>
  );
}
