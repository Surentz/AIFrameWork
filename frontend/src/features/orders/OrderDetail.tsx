import { Link, useParams } from 'react-router-dom';
import { formatPrice } from '../products/types';
import { useOrder } from './queries';
import './orders.css';

export function OrderDetail(): React.JSX.Element {
  const { id } = useParams<{ id: string }>();
  const { data, isPending, error } = useOrder(id);

  if (isPending) {
    return (
      <div className="card orders__state">
        <span className="spinner" aria-hidden="true" />
        <p role="status">Loading order…</p>
      </div>
    );
  }

  if (error) {
    return (
      <p className="alert" role="alert">
        {error.message}
      </p>
    );
  }

  return (
    <article>
      <Link className="page-back" to="/orders">
        ← All orders
      </Link>

      <div className="page-header">
        <div>
          <h1 className="page-title">{data.productName ?? data.sku}</h1>
          <p className="page-subtitle">Order detail</p>
        </div>
      </div>

      <div className="card">
        <dl className="order-facts">
          <div>
            <dt>Sku</dt>
            <dd>
              {data.productId === undefined || data.productId === null ? (
                data.sku
              ) : (
                <Link to={`/products/${data.productId}`}>{data.sku}</Link>
              )}
            </dd>
          </div>
          {data.unitPrice !== undefined && data.unitPrice !== null && (
            <>
              <div>
                <dt>Unit price</dt>
                <dd>{formatPrice(data.unitPrice)}</dd>
              </div>
              <div>
                <dt>Total</dt>
                <dd>{formatPrice(Number(data.unitPrice) * Number(data.quantity))}</dd>
              </div>
            </>
          )}
          <div>
            <dt>Quantity</dt>
            <dd>{data.quantity}</dd>
          </div>
          <div>
            <dt>Placed</dt>
            <dd>{new Date(data.placedAt).toLocaleString()}</dd>
          </div>
          <div>
            <dt>Order id</dt>
            <dd className="order-facts__id">{data.id}</dd>
          </div>
        </dl>
      </div>
    </article>
  );
}
