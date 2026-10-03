import { useState } from 'react';
import { useOrdersToFulfil, useShipOrder } from './queries';
import type { FulfilmentOrder } from './types';
import { ErrorPanel } from '../../components/ErrorPanel';
import '../orders/orders.css';

function Header(): React.JSX.Element {
  return (
    <div className="page-header">
      <div>
        <h1 className="page-title">Fulfilment</h1>
        <p className="page-subtitle">
          Every buyer&apos;s orders waiting to ship, the longest-waiting first.
        </p>
      </div>
    </div>
  );
}

/**
 * The operator's queue: every buyer's placed orders, oldest first, and shipping them.
 *
 * The route is behind `RequireRole`, and the API behind `Orders.Fulfil` — that policy is the
 * control, this page only explains a refusal. See ADR 0024.
 */
export function FulfilmentPage(): React.JSX.Element {
  const { data, isPending, error, fetchNextPage, hasNextPage, isFetchingNextPage } =
    useOrdersToFulfil();

  if (isPending) {
    return (
      <>
        <Header />
        <div className="card orders__state">
          <span className="spinner" aria-hidden="true" />
          <p role="status">Loading the queue…</p>
        </div>
      </>
    );
  }

  // Keyed off data rather than error, as in OrderList: a failed later page leaves the rows
  // already on screen where they are.
  if (data === undefined) {
    return (
      <>
        <Header />
        <ErrorPanel error={error} />
      </>
    );
  }

  const orders = data.pages.flatMap((page) => page.items);

  if (orders.length === 0) {
    return (
      <>
        <Header />
        <div className="card orders__state">
          <p className="orders__empty-title">Nothing waiting to ship.</p>
        </div>
      </>
    );
  }

  return (
    <>
      <Header />

      <div className="card orders__card">
        <table className="orders__table">
          <caption className="visually-hidden">Orders waiting to ship</caption>
          <thead>
            <tr>
              <th scope="col">Product</th>
              <th className="orders__num" scope="col">
                Quantity
              </th>
              <th scope="col">Buyer</th>
              <th scope="col">Placed</th>
              <th scope="col">Action</th>
            </tr>
          </thead>
          <tbody>
            {orders.map((order) => (
              <QueueRow key={order.id} order={order} />
            ))}
          </tbody>
        </table>
      </div>

      {error && <ErrorPanel error={error} className="orders__error" />}

      {hasNextPage && (
        <div className="orders__more">
          <button
            className="btn btn--secondary"
            type="button"
            disabled={isFetchingNextPage}
            onClick={() => {
              void fetchNextPage();
            }}
          >
            {isFetchingNextPage && <span className="spinner" aria-hidden="true" />}
            {isFetchingNextPage ? 'Loading more…' : 'Load more'}
          </button>
        </div>
      )}
    </>
  );
}

interface QueueRowProps {
  readonly order: FulfilmentOrder;
}

function QueueRow({ order }: QueueRowProps): React.JSX.Element {
  const [confirming, setConfirming] = useState(false);
  const ship = useShipOrder();

  const product = order.productName ?? order.sku;
  const buyer = order.buyerUsername ?? 'Unknown buyer';

  return (
    <>
      <tr>
        <td>{product}</td>
        <td className="orders__num">{order.quantity}</td>
        <td>{buyer}</td>
        <td className="orders__when">{new Date(order.placedAt).toLocaleString()}</td>
        <td>
          {confirming ? (
            // Named in words, and a second click: shipping cannot be undone, and a misclick in
            // a table row must not ship the wrong buyer's order.
            <span>
              Ship {order.quantity} × {product} to {buyer}?{' '}
              <button
                className="btn btn--primary"
                type="button"
                disabled={ship.isPending}
                onClick={() => {
                  ship.mutate(order.id, {
                    onSuccess: () => {
                      setConfirming(false);
                    },
                  });
                }}
              >
                {ship.isPending && <span className="spinner" aria-hidden="true" />}
                Confirm
              </button>{' '}
              <button
                className="btn btn--secondary"
                type="button"
                disabled={ship.isPending}
                onClick={() => {
                  setConfirming(false);
                }}
              >
                Cancel
              </button>
            </span>
          ) : (
            <button
              className="btn btn--secondary"
              type="button"
              aria-label={`Ship ${product} to ${buyer}`}
              onClick={() => {
                setConfirming(true);
              }}
            >
              Ship
            </button>
          )}
        </td>
      </tr>

      {ship.error && (
        <tr>
          {/* role="alert" on a <p> inside the cell, not the <td>: see UsersPage. */}
          <td colSpan={5}>
            <ErrorPanel error={ship.error} />
          </td>
        </tr>
      )}
    </>
  );
}
