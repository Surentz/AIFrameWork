import { useId, useState } from 'react';
import type { SubmitEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { ProductFields } from './ProductFields';
import type { ProductFieldValues } from './ProductFields';
import { useCreateProduct } from './queries';
import '../orders/orders.css';
import './products.css';

export function CreateProductForm(): React.JSX.Element {
  const [sku, setSku] = useState('');
  const [fields, setFields] = useState<ProductFieldValues>({
    name: '',
    description: '',
    price: '',
  });
  const navigate = useNavigate();
  const mutation = useCreateProduct();

  function handleSubmit(event: SubmitEvent<HTMLFormElement>): void {
    event.preventDefault();
    mutation.mutate(
      {
        sku,
        name: fields.name,
        // An empty box means "no description", which the API models as null. Sending "" would
        // be storing a description that is not one - the domain collapses it to null anyway,
        // so this just says the same thing on the wire.
        description: fields.description.trim() === '' ? null : fields.description,
        price: fields.price,
      },
      { onSuccess: (id) => void navigate(`/products/${id}`) },
    );
  }

  const fieldErrors = mutation.error?.fieldErrors ?? {};
  const skuErrors = fieldErrors.Sku ?? [];
  const skuId = useId();
  const skuErrorId = useId();

  return (
    <>
      <Link className="page-back" to="/products">
        ← All products
      </Link>

      <div className="page-header">
        <div>
          <h1 className="page-title">Add a product</h1>
          <p className="page-subtitle">The sku is set once and cannot be changed afterwards.</p>
        </div>
      </div>

      <form className="card product-form" onSubmit={handleSubmit}>
        <div className="product-form__fields">
          <div className="field">
            <label className="field__label" htmlFor={skuId}>
              Sku
            </label>
            <input
              className="input"
              id={skuId}
              placeholder="SKU-1"
              value={sku}
              aria-invalid={skuErrors.length > 0}
              aria-describedby={skuErrors.length > 0 ? skuErrorId : undefined}
              onChange={(e) => {
                setSku(e.target.value);
              }}
            />
            {/* Rendered unconditionally: a live region inserted together with its text may
                not be announced, so it has to already exist when the error arrives. */}
            <div className="field__errors" id={skuErrorId} aria-live="polite">
              {skuErrors.map((message) => (
                <p key={message}>{message}</p>
              ))}
            </div>
          </div>

          <ProductFields values={fields} onChange={setFields} fieldErrors={fieldErrors} />

          <div>
            <button className="btn btn--primary" type="submit" disabled={mutation.isPending}>
              {mutation.isPending && <span className="spinner" aria-hidden="true" />}
              Add product
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
