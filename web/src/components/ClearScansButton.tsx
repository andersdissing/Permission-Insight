import { useEffect, useRef } from 'react';
import { formatBytes, type SavedScanSummary } from '../db/storage';

interface Props {
  saved: SavedScanSummary;
  onClear: () => Promise<void>;
}

/**
 * The count lives in the label rather than the button being disabled when
 * empty: a greyed control says something is impossible without saying why, and
 * on a touch device shows no tooltip. At zero the button is hidden instead.
 */
export function ClearScansButton({ saved, onClear }: Props) {
  const dialog = useRef<HTMLDialogElement>(null);
  const cancel = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    // Nothing left to clear, so nothing left to confirm.
    if (saved.count === 0) {
      dialog.current?.close();
    }
  }, [saved.count]);

  if (saved.count === 0) {
    return null;
  }

  const open = () => {
    dialog.current?.showModal();
    // Cancel is the default, because clearing is irreversible and can discard
    // a quarter of an hour of scanning.
    cancel.current?.focus();
  };

  const confirm = async () => {
    await onClear();
    dialog.current?.close();
    // No success message. The count disappearing from the button is the
    // message.
  };

  return (
    <>
      {saved.bytes !== null && <span className="storage-usage">{formatBytes(saved.bytes)}</span>}

      <button type="button" className="text-button" onClick={open}>
        Clear ({saved.count})
      </button>

      <dialog ref={dialog} className="confirm">
        <h2>Clear saved scans</h2>
        <p>
          This removes {saved.count === 1 ? '1 saved scan' : `${saved.count} saved scans`} from this
          browser. You can't undo it, and rescanning takes as long as it did the first time.
        </p>
        <p>
          The site you're on stays open and reloads. Any scan still running stops, because its
          results are part of what's being cleared.
        </p>
        <div className="confirm-actions">
          <button type="button" ref={cancel} onClick={() => dialog.current?.close()}>
            Cancel
          </button>
          <button type="button" className="danger" onClick={() => void confirm()}>
            Clear
          </button>
        </div>
      </dialog>
    </>
  );
}
