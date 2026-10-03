import { Link, useParams } from 'react-router-dom';
import { useCanManageCatalogue, useProduct } from './queries';
import { formatPrice } from './types';
import { ErrorPanel } from '../../components/ErrorPanel';
import '../orders/orders.css';
import './products.css';

export function ProductDetail(): React.JSX.Element {
  const { id } = useParams<{ id: string }>();
  const { data, isPending, error } = useProduct(id);
  const canManage = useCanManageCatalogue();

  if (isPending) {
    return (
      <div className="card products__state">
        <span className="spinner" aria-hidden="true" />
        <p role="status">Loading product…</p>
      </div>
    );
  }

  if (error) {
    return <ErrorPanel error={error} />;
  }

  return (
    <article>
      <Link className="page-back" to="/products">
        ← All products
      </Link>

      <div className="page-header">
        <div>
          <h1 className="page-title">{data.name}</h1>
          <p className="page-subtitle">{data.sku}</p>
        </div>
        {canManage && (
          <Link className="btn btn--secondary" to={`/products/${data.id}/edit`}>
            Edit
          </Link>
        )}
      </div>

      <div className="card">
        <dl className="product-facts">
          <div>
            <dt>Price</dt>
            <dd>{formatPrice(data.price)}</dd>
          </div>
          <div>
            <dt>Sku</dt>
            <dd className="product-facts__id">{data.sku}</dd>
          </div>
          <div className="product-facts__wide">
            <dt>Description</dt>
            <dd className="product-facts__description">{data.description ?? 'No description.'}</dd>
          </div>
          <div>
            <dt>Added</dt>
            <dd>{new Date(data.createdAt).toLocaleString()}</dd>
          </div>
          <div>
            <dt>Last updated</dt>
            <dd>{new Date(data.updatedAt).toLocaleString()}</dd>
          </div>
          <div className="product-facts__wide">
            <dt>Product id</dt>
            <dd className="product-facts__id">{data.id}</dd>
          </div>
        </dl>
      </div>
    </article>
  );
}
