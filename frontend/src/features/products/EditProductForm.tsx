import { useState } from 'react';
import type { SubmitEvent } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { ProductFields } from './ProductFields';
import type { ProductFieldValues } from './ProductFields';
import { useProduct, useUpdateProduct } from './queries';
import { ErrorPanel } from '../../components/ErrorPanel';
import '../orders/orders.css';
import './products.css';

interface EditProductFieldsProps {
  readonly id: string;
  readonly sku: string;
  readonly initial: ProductFieldValues;
}

/**
 * Split from the screen below so the form's state can be initialised from the loaded product
 * through useState rather than an effect that syncs after the fact. The screen renders this only
 * once the query has data, and the key on it makes React remount rather than reuse the state if
 * the id ever changes underneath.
 */
function EditProductFormFields({ id, sku, initial }: EditProductFieldsProps): React.JSX.Element {
  const [fields, setFields] = useState<ProductFieldValues>(initial);
  const navigate = useNavigate();
  const mutation = useUpdateProduct();

  function handleSubmit(event: SubmitEvent<HTMLFormElement>): void {
    event.preventDefault();
    mutation.mutate(
      {
        id,
        name: fields.name,
        description: fields.description.trim() === '' ? null : fields.description,
        price: fields.price,
      },
      { onSuccess: () => void navigate(`/products/${id}`) },
    );
  }

  const fieldErrors = mutation.error?.fieldErrors ?? {};

  return (
    <form className="card product-form" onSubmit={handleSubmit}>
      <div className="product-form__fields">
        <p className="product-form__sku">
          Sku <code>{sku}</code> — set at creation and not editable.
        </p>

        <ProductFields values={fields} onChange={setFields} fieldErrors={fieldErrors} />

        <div className="product-form__actions">
          <button className="btn btn--primary" type="submit" disabled={mutation.isPending}>
            {mutation.isPending && <span className="spinner" aria-hidden="true" />}
            Save changes
          </button>
          <Link className="btn btn--secondary" to={`/products/${id}`}>
            Cancel
          </Link>
        </div>

        {mutation.error && Object.keys(fieldErrors).length === 0 && (
          <ErrorPanel error={mutation.error} />
        )}
      </div>
    </form>
  );
}

export function EditProductForm(): React.JSX.Element {
  const { id } = useParams<{ id: string }>();
  const { data, isPending, error } = useProduct(id);

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
    <>
      <Link className="page-back" to={`/products/${data.id}`}>
        ← Back to {data.name}
      </Link>

      <div className="page-header">
        <div>
          <h1 className="page-title">Edit {data.name}</h1>
          <p className="page-subtitle">Everything except the sku.</p>
        </div>
      </div>

      <EditProductFormFields
        key={data.id}
        id={data.id}
        sku={data.sku}
        initial={{
          name: data.name,
          description: data.description ?? '',
          price: String(data.price),
        }}
      />
    </>
  );
}
