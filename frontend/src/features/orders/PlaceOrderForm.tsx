import { useId, useState } from 'react';
import type { SubmitEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { formatPrice } from '../products/types';
import { useAllProducts } from '../products/queries';
import { usePlaceOrder } from './queries';
import './orders.css';

export function PlaceOrderForm(): React.JSX.Element {
  const { data: products, isPending: productsPending, error: productsError } = useAllProducts();
  const [sku, setSku] = useState('');
  const [quantity, setQuantity] = useState(1);
  const navigate = useNavigate();
  const mutation = usePlaceOrder();
  const selected = products?.find((p) => p.sku === sku);

  function handleSubmit(event: SubmitEvent<HTMLFormElement>): void {
    event.preventDefault();
    mutation.mutate({ sku, quantity }, { onSuccess: (id) => void navigate(`/orders/${id}`) });
  }

  const fieldErrors = mutation.error?.fieldErrors ?? {};
  const skuErrors = fieldErrors.Sku ?? [];
  const quantityErrors = fieldErrors.Quantity ?? [];
  // useId, not literals: two mounted forms would emit duplicate ids, and both htmlFor and
  // aria-describedby would resolve to the first form's inputs.
  const skuId = useId();
  const quantityId = useId();
  const skuErrorId = useId();
  const quantityErrorId = useId();

  return (
    <>
      <Link className="page-back" to="/orders">
        ← All orders
      </Link>

      <div className="page-header">
        <div>
          <h1 className="page-title">Place an order</h1>
          <p className="page-subtitle">A product and how many of it you want.</p>
        </div>
      </div>

      {productsError && (
        <p className="alert" role="alert">
          The products could not be loaded. {productsError.message}
        </p>
      )}

      <form className="card order-form" onSubmit={handleSubmit}>
        <div className="order-form__fields">
          <div className="field">
            <label className="field__label" htmlFor={skuId}>
              Product
            </label>
            <select
              className="input"
              id={skuId}
              value={sku}
              disabled={productsPending || products === undefined || products.length === 0}
              aria-invalid={skuErrors.length > 0}
              aria-describedby={skuErrors.length > 0 ? skuErrorId : undefined}
              onChange={(e) => {
                setSku(e.target.value);
              }}
            >
              <option value="">Choose a product…</option>
              {products?.map((product) => (
                <option key={product.id} value={product.sku}>
                  {product.name} — {product.sku} — {formatPrice(product.price)}
                </option>
              ))}
            </select>
            {products?.length === 0 && (
              <p className="field__hint">
                There are no products in the catalogue yet.{' '}
                <Link to="/products/new">Add a product</Link> first.
              </p>
            )}
            {/* Rendered unconditionally: a live region inserted together with its text may
                not be announced, so it has to already exist when the error arrives. */}
            <div className="field__errors" id={skuErrorId} aria-live="polite">
              {skuErrors.map((message) => (
                <p key={message}>{message}</p>
              ))}
            </div>
          </div>

          <div className="field">
            <label className="field__label" htmlFor={quantityId}>
              Quantity
            </label>
            <input
              className="input"
              id={quantityId}
              type="number"
              value={quantity}
              aria-invalid={quantityErrors.length > 0}
              aria-describedby={quantityErrors.length > 0 ? quantityErrorId : undefined}
              onChange={(e) => {
                setQuantity(Number(e.target.value));
              }}
            />
            {/* Rendered unconditionally: a live region inserted together with its text may
                not be announced, so it has to already exist when the error arrives. */}
            <div className="field__errors" id={quantityErrorId} aria-live="polite">
              {quantityErrors.map((message) => (
                <p key={message}>{message}</p>
              ))}
            </div>
          </div>

          {selected && quantity > 0 && (
            <p className="order-form__total">
              Total: {formatPrice(Number(selected.price) * quantity)}
            </p>
          )}

          <div>
            <button
              className="btn btn--primary"
              type="submit"
              disabled={mutation.isPending || sku === '' || products?.length === 0}
            >
              {mutation.isPending && <span className="spinner" aria-hidden="true" />}
              Place order
            </button>
          </div>

          {mutation.error && Object.keys(fieldErrors).length === 0 && (
            <p className="alert" role="alert">
              {mutation.error.message}
            </p>
          )}
        </div>
      </form>
    </>
  );
}
