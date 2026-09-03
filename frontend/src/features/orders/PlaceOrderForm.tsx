import { useId, useState } from 'react';
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
  // useId, not literals: two mounted forms would emit duplicate ids and aria-describedby
  // would resolve to the wrong one.
  const skuErrorId = useId();
  const quantityErrorId = useId();

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
      {/* Rendered unconditionally: a live region inserted together with its text may
          not be announced, so it has to already exist when the error arrives. */}
      <div id={skuErrorId} aria-live="polite">
        {skuErrors.map((message) => (
          <p key={message}>{message}</p>
        ))}
      </div>

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
      {/* Rendered unconditionally: a live region inserted together with its text may
          not be announced, so it has to already exist when the error arrives. */}
      <div id={quantityErrorId} aria-live="polite">
        {quantityErrors.map((message) => (
          <p key={message}>{message}</p>
        ))}
      </div>

      <button type="submit" disabled={mutation.isPending}>
        Place order
      </button>

      {mutation.error && Object.keys(fieldErrors).length === 0 && (
        <p role="alert">{mutation.error.message}</p>
      )}
    </form>
  );
}
