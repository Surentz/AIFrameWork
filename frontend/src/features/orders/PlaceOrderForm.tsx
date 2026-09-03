import { useState } from 'react';
import type { SubmitEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import { usePlaceOrder } from './queries';

export function PlaceOrderForm(): React.JSX.Element {
  const [sku, setSku] = useState('');
  const [quantity, setQuantity] = useState(1);
  const navigate = useNavigate();
  const mutation = usePlaceOrder();

  function handleSubmit(event: SubmitEvent<HTMLFormElement>): void {
    event.preventDefault();
    mutation.mutate({ sku, quantity }, { onSuccess: (id) => void navigate(`/orders/${id}`) });
  }

  const fieldErrors = mutation.error?.fieldErrors ?? {};
  const skuErrors = fieldErrors.Sku ?? [];
  const quantityErrors = fieldErrors.Quantity ?? [];
  const skuErrorId = 'sku-error';
  const quantityErrorId = 'quantity-error';

  return (
    <form onSubmit={handleSubmit}>
      <h2>Place an order</h2>

      <label htmlFor="sku">Sku</label>
      <input
        id="sku"
        value={sku}
        aria-invalid={skuErrors.length > 0}
        aria-describedby={skuErrors.length > 0 ? skuErrorId : undefined}
        onChange={(e) => {
          setSku(e.target.value);
        }}
      />
      {skuErrors.length > 0 && (
        <div id={skuErrorId}>
          {skuErrors.map((message) => (
            <p key={message}>{message}</p>
          ))}
        </div>
      )}

      <label htmlFor="quantity">Quantity</label>
      <input
        id="quantity"
        type="number"
        value={quantity}
        aria-invalid={quantityErrors.length > 0}
        aria-describedby={quantityErrors.length > 0 ? quantityErrorId : undefined}
        onChange={(e) => {
          setQuantity(Number(e.target.value));
        }}
      />
      {quantityErrors.length > 0 && (
        <div id={quantityErrorId}>
          {quantityErrors.map((message) => (
            <p key={message}>{message}</p>
          ))}
        </div>
      )}

      <button type="submit" disabled={mutation.isPending}>
        Place order
      </button>

      {mutation.error && Object.keys(fieldErrors).length === 0 && (
        <p role="alert">{mutation.error.message}</p>
      )}
    </form>
  );
}
