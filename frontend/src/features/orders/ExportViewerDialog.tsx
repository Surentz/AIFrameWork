import { Suspense, lazy, useEffect, useId, useRef } from 'react';
import type { OrderExport } from './types';

const ExportViewer = lazy(() => import('./ExportViewer'));

interface ExportViewerDialogProps {
  readonly exportItem: OrderExport | null;
  readonly onClose: () => void;
}

/**
 * The app's first modal: a native <dialog> opened with showModal(), so the browser supplies the
 * focus trap, Esc, the inert page behind it, and focus returning to the View button on close.
 * Full screen below 40rem (orders.css).
 */
export function ExportViewerDialog({ exportItem, onClose }: ExportViewerDialogProps): React.JSX.Element {
  const dialog = useRef<HTMLDialogElement>(null);
  const titleId = useId();

  useEffect(() => {
    const element = dialog.current;
    if (element === null) {
      return;
    }
    if (exportItem !== null && !element.open) {
      element.showModal();
    } else if (exportItem === null && element.open) {
      element.close();
    }
  }, [exportItem]);

  return (
    // onClose fires for Esc and for close() alike, so the page's state follows the browser's.
    <dialog ref={dialog} className="export-viewer" aria-labelledby={titleId} onClose={onClose}>
      {exportItem !== null && (
        <>
          <div className="export-viewer__header">
            <h2 id={titleId} className="export-viewer__title">
              {`Export requested ${new Date(exportItem.requestedAt).toLocaleString()}`}
            </h2>
            <button className="btn btn--secondary" type="button" onClick={() => { dialog.current?.close(); }}>
              Close
            </button>
          </div>
          <Suspense
            fallback={
              <div className="orders__state">
                <span className="spinner" aria-hidden="true" />
                <p role="status">Loading export…</p>
              </div>
            }
          >
            <ExportViewer id={exportItem.id} />
          </Suspense>
        </>
      )}
    </dialog>
  );
}
