import { Link } from 'react-router-dom';
import { useCanManageCatalogue, useProducts } from './queries';
import { formatPrice } from './types';
import { ErrorPanel } from '../../components/ErrorPanel';
import '../orders/orders.css';
import './products.css';

function Header(): React.JSX.Element {
  const canManage = useCanManageCatalogue();

  return (
    <div className="page-header">
      <div>
        <h1 className="page-title">Catalogue</h1>
        <p className="page-subtitle">Every product, newest first.</p>
      </div>
      {canManage && (
        <Link className="btn btn--primary" to="/products/new">
          Add a product
        </Link>
      )}
    </div>
  );
}

function EmptyState(): React.JSX.Element {
  const canManage = useCanManageCatalogue();

  return (
    <div className="card products__state">
      <p className="products__empty-title">No products yet.</p>
      {canManage ? (
        <Link className="btn btn--secondary" to="/products/new">
          Add the first one
        </Link>
      ) : (
        <p className="page-subtitle">An administrator adds products to the catalogue.</p>
      )}
    </div>
  );
}

export function ProductList(): React.JSX.Element {
  const { data, isPending, error, fetchNextPage, hasNextPage, isFetchingNextPage } = useProducts();

  if (isPending) {
    return (
      <>
        <Header />
        <div className="card products__state">
          <span className="spinner" aria-hidden="true" />
          <p role="status">Loading products…</p>
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
        <ErrorPanel error={error} />
      </>
    );
  }

  const products = data.pages.flatMap((page) => page.items);

  if (products.length === 0) {
    return (
      <>
        <Header />
        <EmptyState />
      </>
    );
  }

  return (
    <>
      <Header />

      <div className="card products__card">
        <table className="products__table">
          {/* Hidden rather than removed: the table still needs its accessible name, but the
              page heading above already says "Catalogue" in the design. */}
          <caption className="visually-hidden">Catalogue</caption>
          <thead>
            <tr>
              <th scope="col">Name</th>
              <th scope="col">Sku</th>
              <th className="products__num" scope="col">
                Price
              </th>
              <th scope="col">Added</th>
            </tr>
          </thead>
          <tbody>
            {products.map((product) => (
              <tr key={product.id}>
                <td>
                  <Link className="products__name" to={`/products/${product.id}`}>
                    {product.name}
                  </Link>
                </td>
                <td className="products__sku">{product.sku}</td>
                <td className="products__num">{formatPrice(product.price)}</td>
                <td className="products__when">{new Date(product.createdAt).toLocaleString()}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {error && <ErrorPanel error={error} className="products__error" />}

      {hasNextPage && (
        <div className="products__more">
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
