import { useParams } from 'react-router-dom';
import { useOrder } from './queries';

export function OrderDetail(): React.JSX.Element {
  const { id } = useParams<{ id: string }>();
  const { data, isPending, error } = useOrder(id ?? '');

  if (isPending) {
    return <p>Loading order…</p>;
  }

  if (error) {
    return <p role="alert">{error.message}</p>;
  }

  return (
    <article>
      <h2>{data.sku}</h2>
      <dl>
        <dt>Quantity</dt>
        <dd>{data.quantity}</dd>
        <dt>Placed</dt>
        <dd>{new Date(data.placedAt).toLocaleString()}</dd>
      </dl>
    </article>
  );
}
