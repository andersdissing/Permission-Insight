import { useEffect, useRef, useState } from 'react';
import { InfoIcon } from './Icons';

/**
 * What is stored, described rather than reassured about.
 *
 * The order is fixed. Folder paths and file names are frequently more
 * revealing than file contents — a path like
 * /Personalesager/Opsigelse Jens Hansen december.docx tells the whole story
 * without anyone opening the document — so "file contents aren't stored" comes
 * last, where it cannot be read as the whole answer.
 */
export function StorageInfo() {
  const [open, setOpen] = useState(false);
  const container = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) {
      return;
    }

    const closeOnOutside = (event: MouseEvent) => {
      if (!container.current?.contains(event.target as Node)) {
        setOpen(false);
      }
    };

    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setOpen(false);
      }
    };

    document.addEventListener('mousedown', closeOnOutside);
    document.addEventListener('keydown', closeOnEscape);

    return () => {
      document.removeEventListener('mousedown', closeOnOutside);
      document.removeEventListener('keydown', closeOnEscape);
    };
  }, [open]);

  return (
    <div className="storage-info" ref={container}>
      <button
        type="button"
        className="icon-button"
        aria-label="What's stored in this browser"
        aria-expanded={open}
        onClick={() => setOpen((previous) => !previous)}
      >
        <InfoIcon />
      </button>

      {open && (
        <div className="popover" role="dialog" aria-label="What's stored in this browser">
          <p>
            Folder structure, folder and file names, unique permissions and sharing links are stored
            in this browser.
          </p>
          <p>
            Data stays until you clear it, including after you sign out. On a shared machine, the
            next person to use this browser profile can read it.
          </p>
          <p>
            Clearing removes scan results. The list of sites you've opened is kept, so you don't
            lose your place — it holds site names and addresses, never their contents.
          </p>
          <p>File contents aren't stored.</p>
        </div>
      )}
    </div>
  );
}
