import { Link } from 'react-router-dom';
import { useOrders } from './queries';
import './orders.css';

function Header(): React.JSX.Element {
  return (
    <div className="page-header">
      <div>
        <h1 className="page-title">Orders</h1>
        <p className="page-subtitle">Every order placed, newest first.</p>
      </div>
      <Link className="btn btn--primary" to="/orders/new">
        Place an order
      </Link>
    </div>
  );
}

export function OrderList(): React.JSX.Element {
  const { data, isPending, error, fetchNextPage, hasNextPage, isFetchingNextPage } = useOrders();

  if (isPending) {
    return (
      <>
        <Header />
        <div className="card orders__state">
          <span className="spinner" aria-hidden="true" />
          <p role="status">Loading orders…</p>
        </div>
      </>
    );
  }

  // Keyed off data rather than error so that a failed *later* page leaves the rows already on
  // screen where they are; only a failed first page has nothing to show but the failure.
  if (data === undefined) {
    return (
      <>
        <Header />
        <p className="alert" role="alert">
          {error.message}
        </p>
      </>
    );
  }

  const orders = data.pages.flatMap((page) => page.items);

  if (orders.length === 0) {
    return (
      <>
        <Header />
        <div className="card orders__state">
          <p className="orders__empty-title">No orders yet.</p>
          <Link className="btn btn--secondary" to="/orders/new">
            Place the first one
          </Link>
        </div>
      </>
    );
  }

  return (
    <>
      <Header />

      <div className="card orders__card">
        <table className="orders__table">
          {/* Hidden rather than removed: the table still needs its accessible name, but the
              page heading above already says "Orders" in the design. */}
          <caption className="visually-hidden">Orders</caption>
          <thead>
            <tr>
              <th scope="col">Product</th>
              <th className="orders__num" scope="col">
                Quantity
              </th>
              <th scope="col">Placed</th>
            </tr>
          </thead>
          <tbody>
            {orders.map((order) => (
              <tr key={order.id}>
                <td>
                  <Link className="orders__sku" to={`/orders/${order.id}`}>
                    {order.productName ?? order.sku}
                  </Link>
                </td>
                <td className="orders__num">{order.quantity}</td>
                <td className="orders__when">{new Date(order.placedAt).toLocaleString()}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {error && (
        <p className="alert orders__error" role="alert">
          {error.message}
        </p>
      )}

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
