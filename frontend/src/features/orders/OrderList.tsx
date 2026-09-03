import { Link } from 'react-router-dom';
import { useOrders } from './queries';

export function OrderList(): React.JSX.Element {
  const { data, isPending, error, fetchNextPage, hasNextPage, isFetchingNextPage } = useOrders();

  if (isPending) {
    return <p role="status">Loading orders…</p>;
  }

  // Keyed off data rather than error so that a failed *later* page leaves the rows already on
  // screen where they are; only a failed first page has nothing to show but the failure.
  if (data === undefined) {
    return <p role="alert">{error.message}</p>;
  }

  const orders = data.pages.flatMap((page) => page.items);

  if (orders.length === 0) {
    return <p>No orders yet.</p>;
  }

  return (
    <>
      <table>
        <caption>Orders</caption>
        <thead>
          <tr>
            <th scope="col">Sku</th>
            <th scope="col">Quantity</th>
            <th scope="col">Placed</th>
          </tr>
        </thead>
        <tbody>
          {orders.map((order) => (
            <tr key={order.id}>
              <td>
                <Link to={`/orders/${order.id}`}>{order.sku}</Link>
              </td>
              <td>{order.quantity}</td>
              <td>{new Date(order.placedAt).toLocaleString()}</td>
            </tr>
          ))}
        </tbody>
      </table>

      {error && <p role="alert">{error.message}</p>}

      {hasNextPage && (
        <button
          type="button"
          disabled={isFetchingNextPage}
          onClick={() => {
            void fetchNextPage();
          }}
        >
          {isFetchingNextPage ? 'Loading more…' : 'Load more'}
        </button>
      )}
    </>
  );
}
