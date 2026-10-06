import { useState } from 'react';
import { orderExportDownloadUrl } from '../../api/orders';
import type { ApiError } from '../../api/client';
import { ErrorPanel } from '../../components/ErrorPanel';
import { ExportViewerDialog } from './ExportViewerDialog';
import { useOrderExports, useRequestOrderExport } from './queries';
import type { OrderExport, OrderExportState } from './types';
import './orders.css';

const STATUS_LABEL: Record<OrderExportState, string> = {
  Requested: 'Being built',
  Ready: 'Ready',
  Failed: 'Failed',
};

// What the status region says about the newest export. A screen reader announces it when it
// changes, so "being built" turning into "ready" is heard without the user going looking.
const ANNOUNCEMENT: Record<OrderExportState, string> = {
  Requested: 'Your export is being built.',
  Ready: 'Your latest export is ready to download.',
  Failed: 'Your latest export failed.',
};

interface NewestProps {
  readonly newest: OrderExport | undefined;
}

/**
 * One button, three faces. While the newest export is being built it does nothing - the API would
 * only hand back that same export - and once it has failed it offers to try again.
 *
 * aria-disabled rather than disabled, with the click guarded: a disabled button cannot hold focus,
 * so a keyboard user who pressed it would be dropped back to the top of the page at the exact
 * moment they are waiting on it.
 */
function ExportButton({ newest }: NewestProps): React.JSX.Element {
  const request = useRequestOrderExport();
  const building = newest?.status === 'Requested';
  const inert = building || request.isPending;

  let label = 'Export my orders';
  if (building) {
    label = 'Export in progress…';
  } else if (newest?.status === 'Failed') {
    label = 'Try again';
  }

  return (
    <div className="orders__export">
      <button
        className="btn btn--primary"
        type="button"
        aria-disabled={inert}
        onClick={() => {
          if (!inert) {
            request.mutate();
          }
        }}
      >
        {request.isPending && <span className="spinner" aria-hidden="true" />}
        {label}
      </button>
      {request.error && <ErrorPanel error={request.error} className="orders__error" />}
    </div>
  );
}

function Header({ newest }: NewestProps): React.JSX.Element {
  return (
    <div className="page-header">
      <div>
        <h1 className="page-title">Exports</h1>
        <p className="page-subtitle">
          A PDF of every order you have placed, ready to share. You are notified when it is ready;
          exports are kept for seven days.
        </p>
      </div>
      <ExportButton newest={newest} />
    </div>
  );
}

interface ExportsProps {
  readonly exports: readonly OrderExport[];
  readonly error: ApiError | null;
}

function Exports({ exports, error }: ExportsProps): React.JSX.Element {
  const newest = exports[0];
  const [viewing, setViewing] = useState<OrderExport | null>(null);

  return (
    <>
      <p className="visually-hidden" role="status">
        {newest === undefined ? '' : ANNOUNCEMENT[newest.status]}
      </p>

      {newest === undefined ? (
        <div className="card orders__state">
          <p className="orders__empty-title">No exports yet.</p>
        </div>
      ) : (
        <div className="card orders__card">
          <table className="orders__table">
            <caption className="visually-hidden">Exports</caption>
            <thead>
              <tr>
                <th scope="col">Requested</th>
                <th scope="col">Status</th>
                <th className="orders__num" scope="col">
                  Orders
                </th>
                <th scope="col">
                  <span className="visually-hidden">Actions</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {exports.map((exp) => {
                const requested = new Date(exp.requestedAt).toLocaleString();
                return (
                  <tr key={exp.id}>
                    <td className="orders__when">{requested}</td>
                    <td>{STATUS_LABEL[exp.status]}</td>
                    <td className="orders__num">{exp.rowCount ?? '–'}</td>
                    <td className="orders__actions">
                      {exp.status === 'Ready' && (
                        <>
                          <button
                            className="btn btn--secondary"
                            type="button"
                            onClick={() => {
                              setViewing(exp);
                            }}
                            aria-label={`View export requested ${requested}`}
                          >
                            View
                          </button>
                          <a
                            className="btn btn--secondary"
                            href={orderExportDownloadUrl(exp.id)}
                            download
                            // Each row's link says which export it is: a screen reader's link list
                            // would otherwise show a column of identical "Download"s.
                            aria-label={`Download export requested ${requested}`}
                          >
                            Download
                          </a>
                        </>
                      )}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}

      <ExportViewerDialog
        exportItem={viewing}
        onClose={() => {
          setViewing(null);
        }}
      />

      {/* In every branch that has data: a re-read that fails keeps the last list on screen, the
          empty one included, and must still say it failed. */}
      {error && <ErrorPanel error={error} className="orders__error" />}
    </>
  );
}

export function OrderExportsPage(): React.JSX.Element {
  const { data, isPending, error } = useOrderExports();

  // The API lists newest first, so the first export is the one the button and the status track.
  //
  // The header is rendered here, ONCE, at the same position in every state, so React keeps the
  // same button element from loading to loaded. Rendered per branch, it was a different element
  // after the data arrived, and a keyboard user focused on it while loading lost focus.
  let body: React.JSX.Element;
  if (isPending) {
    body = (
      <div className="card orders__state">
        <span className="spinner" aria-hidden="true" />
        <p role="status">Loading exports…</p>
      </div>
    );
  } else if (data === undefined) {
    body = <ErrorPanel error={error} />;
  } else {
    body = <Exports exports={data} error={error} />;
  }

  return (
    <>
      <Header newest={data?.[0]} />
      {body}
    </>
  );
}
