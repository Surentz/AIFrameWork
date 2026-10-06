import { Suspense, lazy, useEffect, useId, useRef } from 'react';
import { orderExportDownloadUrl } from '../../api/orders';
import type { OrderExport } from './types';

interface ViewerProps {
  readonly id: string;
}

/** What the dialog shows when the viewer's own code cannot be fetched. */
function ViewerUnavailable({ id }: ViewerProps): React.JSX.Element {
  return (
    <div className="orders__state" role="alert">
      <p>The viewer could not load. You can still download the export.</p>
      <a className="btn btn--secondary" href={orderExportDownloadUrl(id)} download>
        Download
      </a>
    </div>
  );
}

// The viewer is a separate chunk, and a chunk can fail to arrive: offline, or a hashed file a
// deploy has since replaced. Without this the rejection is thrown during render and takes the whole
// page down with it. Not a swallowed error: the dialog says so, and offers the download instead.
const ExportViewer = lazy(() =>
  import('./ExportViewer').catch(() => ({ default: ViewerUnavailable })),
);

interface ExportViewerDialogProps {
  readonly exportItem: OrderExport | null;
  readonly onClose: () => void;
}

/**
 * The app's first modal: a native <dialog> opened with showModal(), so the browser supplies the
 * focus trap, Esc, the inert page behind it, and focus returning to the View button on close.
 * Full screen below 40rem (orders.css).
 */
export function ExportViewerDialog({
  exportItem,
  onClose,
}: ExportViewerDialogProps): React.JSX.Element {
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
            <button
              className="btn btn--secondary"
              type="button"
              onClick={() => {
                dialog.current?.close();
              }}
            >
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
