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

  return (
    <form onSubmit={handleSubmit}>
      <h2>Place an order</h2>

      <label htmlFor="sku">Sku</label>
      <input
        id="sku"
        value={sku}
        onChange={(e) => {
          setSku(e.target.value);
        }}
      />
      {fieldErrors.Sku?.map((message) => (
        <p key={message}>{message}</p>
      ))}

      <label htmlFor="quantity">Quantity</label>
      <input
        id="quantity"
        type="number"
        value={quantity}
        onChange={(e) => {
          setQuantity(Number(e.target.value));
        }}
      />
      {fieldErrors.Quantity?.map((message) => (
        <p key={message}>{message}</p>
      ))}

      <button type="submit" disabled={mutation.isPending}>
        Place order
      </button>

      {mutation.error && Object.keys(fieldErrors).length === 0 && (
        <p role="alert">{mutation.error.message}</p>
      )}
    </form>
  );
}
