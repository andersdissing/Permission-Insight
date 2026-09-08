import type { SavedScanSummary } from '../db/storage';
import { ClearScansButton } from './ClearScansButton';
import { StorageInfo } from './StorageInfo';
import { LockIcon, SignOutIcon } from './Icons';

interface Props {
  displayName: string;
  saveScans: boolean;
  onSaveScansChange: (value: boolean) => void;
  saved: SavedScanSummary;
  onClear: () => Promise<void>;
  onSignOut: () => void;
}

export function TopBar({
  displayName,
  saveScans,
  onSaveScansChange,
  saved,
  onClear,
  onSignOut,
}: Props) {
  return (
    <header className="top-bar">
      <div className="brand">
        <LockIcon className="brand-icon" />
        <span>Permission insight</span>
      </div>

      <div className="top-bar-actions">
        {/* The toggle controls future saves only. Turning it off deletes
            nothing, and turning it on saves nothing retroactively. */}
        <label className="toggle">
          <span>Save scans</span>
          <input
            type="checkbox"
            checked={saveScans}
            onChange={(event) => onSaveScansChange(event.target.checked)}
          />
          <span className="toggle-track" aria-hidden="true" />
        </label>

        <StorageInfo />

        <ClearScansButton saved={saved} onClear={onClear} />

        <span className="divider" aria-hidden="true" />

        <span className="avatar" title={displayName}>
          {initials(displayName)}
        </span>

        <button type="button" className="icon-button" aria-label="Sign out" onClick={onSignOut}>
          <SignOutIcon />
        </button>
      </div>
    </header>
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
