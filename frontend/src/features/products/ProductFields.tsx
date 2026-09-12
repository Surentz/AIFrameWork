import { useId } from 'react';

export interface ProductFieldValues {
  readonly name: string;
  readonly description: string;
  readonly price: string;
}

interface ProductFieldsProps {
  readonly values: ProductFieldValues;
  readonly onChange: (values: ProductFieldValues) => void;
  /** Per-field messages from a failed request, keyed as the API returns them. */
  readonly fieldErrors: Readonly<Record<string, readonly string[]>>;
}

/**
 * The three editable fields, shared by the create and edit screens — they differ only in what
 * surrounds them (a sku input on one, a read-only sku on the other) and in which mutation they
 * submit to, so duplicating the fields themselves would mean fixing every label and aria wiring
 * twice.
 */
export function ProductFields({
  values,
  onChange,
  fieldErrors,
}: ProductFieldsProps): React.JSX.Element {
  const nameErrors = fieldErrors.Name ?? [];
  const descriptionErrors = fieldErrors.Description ?? [];
  const priceErrors = fieldErrors.Price ?? [];

  // useId, not literals: two mounted forms would emit duplicate ids, and both htmlFor and
  // aria-describedby would resolve to the first form's inputs.
  const nameId = useId();
  const descriptionId = useId();
  const priceId = useId();
  const nameErrorId = useId();
  const descriptionErrorId = useId();
  const priceErrorId = useId();

  return (
    <>
      <div className="field">
        <label className="field__label" htmlFor={nameId}>
          Name
        </label>
        <input
          className="input"
          id={nameId}
          placeholder="Widget"
          value={values.name}
          aria-invalid={nameErrors.length > 0}
          aria-describedby={nameErrors.length > 0 ? nameErrorId : undefined}
          onChange={(e) => {
            onChange({ ...values, name: e.target.value });
          }}
        />
        {/* Rendered unconditionally: a live region inserted together with its text may
            not be announced, so it has to already exist when the error arrives. */}
        <div className="field__errors" id={nameErrorId} aria-live="polite">
          {nameErrors.map((message) => (
            <p key={message}>{message}</p>
          ))}
        </div>
      </div>

      <div className="field">
        <label className="field__label" htmlFor={descriptionId}>
          Description
        </label>
        <textarea
          className="input"
          id={descriptionId}
          rows={3}
          placeholder="Optional."
          value={values.description}
          aria-invalid={descriptionErrors.length > 0}
          aria-describedby={descriptionErrors.length > 0 ? descriptionErrorId : undefined}
          onChange={(e) => {
            onChange({ ...values, description: e.target.value });
          }}
        />
        {/* Rendered unconditionally: a live region inserted together with its text may
            not be announced, so it has to already exist when the error arrives. */}
        <div className="field__errors" id={descriptionErrorId} aria-live="polite">
          {descriptionErrors.map((message) => (
            <p key={message}>{message}</p>
          ))}
        </div>
      </div>

      <div className="field">
        <label className="field__label" htmlFor={priceId}>
          Price
        </label>
        {/*
          * A text input with inputMode="decimal", NOT type="number" with step/min.
          *
          * Kept as a string all the way to the request body: the API accepts a numeric string,
          * so a value like "1.005" reaches the server's own decimal-places rule intact rather
          * than being quietly changed here.
          *
          * type="number" defeats both halves of that. Its value sanitisation drops a trailing
          * zero, so "4.50" is submitted as "4.5"; and step="0.01" makes "1.005" fail the
          * browser's own constraint validation, which blocks the submit silently — the request
          * is never sent, so the server's message about decimal places never arrives and the
          * field shows no error of its own. step/min would also be a second, client-side copy
          * of rules CreateProductValidator already owns, which is the duplication
          * src/Api/CLAUDE.md warns about in the DataAnnotations case.
          *
          * inputMode="decimal" still brings up a numeric keypad on a touch device.
          */}
        <input
          className="input"
          id={priceId}
          inputMode="decimal"
          placeholder="0.00"
          value={values.price}
          aria-invalid={priceErrors.length > 0}
          aria-describedby={priceErrors.length > 0 ? priceErrorId : undefined}
          onChange={(e) => {
            onChange({ ...values, price: e.target.value });
          }}
        />
        {/* Rendered unconditionally: a live region inserted together with its text may
            not be announced, so it has to already exist when the error arrives. */}
        <div className="field__errors" id={priceErrorId} aria-live="polite">
          {priceErrors.map((message) => (
            <p key={message}>{message}</p>
          ))}
        </div>
      </div>
    </>
  );
}
