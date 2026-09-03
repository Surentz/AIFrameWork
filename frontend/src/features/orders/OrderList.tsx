import { Link } from 'react-router-dom';
import { useOrders } from './queries';

export function OrderList(): React.JSX.Element {
  const { data, isPending, error } = useOrders();

  if (isPending) {
    return <p role="status">Loading orders…</p>;
  }

  if (error) {
    return <p role="alert">{error.message}</p>;
  }

  if (data.items.length === 0) {
    return <p>No orders yet.</p>;
  }

  return (
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
        {data.items.map((order) => (
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
  );
}
