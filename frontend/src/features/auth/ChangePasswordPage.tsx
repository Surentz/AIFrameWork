import { useId, useState } from 'react';
import type { SubmitEvent } from 'react';
import { Link } from 'react-router-dom';
import { useChangePassword, useSignOutEverywhere } from './queries';
import '../orders/orders.css';

/** Mirrors PasswordPolicy.MinimumLength in src/Application/Users; the server is the authority. */
const MinimumPasswordLength = 12;

export function ChangePasswordPage(): React.JSX.Element {
  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const mutation = useChangePassword();
  const signOutEverywhere = useSignOutEverywhere();

  function handleSubmit(event: SubmitEvent<HTMLFormElement>): void {
    event.preventDefault();
    mutation.mutate(
      { currentPassword, newPassword },
      {
        onSuccess: () => {
          setCurrentPassword('');
          setNewPassword('');
        },
      },
    );
  }

  const fieldErrors = mutation.error?.fieldErrors ?? {};
  const currentErrors = fieldErrors.CurrentPassword ?? [];
  const newErrors = fieldErrors.NewPassword ?? [];
  const currentId = useId();
  const newId = useId();
  const currentErrorId = useId();
  const newErrorId = useId();

  return (
    <>
      <Link className="page-back" to="/orders">
        ← All orders
      </Link>

      <div className="page-header">
        <div>
          <h1 className="page-title">Change password</h1>
          <p className="page-subtitle">Your current password, then the new one.</p>
        </div>
      </div>

      <form className="card order-form" onSubmit={handleSubmit} noValidate>
        <div className="order-form__fields">
          <div className="field">
            <label className="field__label" htmlFor={currentId}>
              Current password
            </label>
            <input
              className="input"
              id={currentId}
              type="password"
              autoComplete="current-password"
              value={currentPassword}
              aria-invalid={currentErrors.length > 0}
              aria-describedby={currentErrors.length > 0 ? currentErrorId : undefined}
              onChange={(e) => {
                setCurrentPassword(e.target.value);
              }}
            />
            {/* Rendered unconditionally: a live region inserted together with its text may
                not be announced, so it has to already exist when the error arrives. */}
            <div className="field__errors" id={currentErrorId} aria-live="polite">
              {currentErrors.map((message) => (
                <p key={message}>{message}</p>
              ))}
            </div>
          </div>

          <div className="field">
            <label className="field__label" htmlFor={newId}>
              New password
            </label>
            <input
              className="input"
              id={newId}
              type="password"
              autoComplete="new-password"
              placeholder={`At least ${String(MinimumPasswordLength)} characters`}
              value={newPassword}
              aria-invalid={newErrors.length > 0}
              aria-describedby={newErrors.length > 0 ? newErrorId : undefined}
              onChange={(e) => {
                setNewPassword(e.target.value);
              }}
            />
            {/* Rendered unconditionally: a live region inserted together with its text may
                not be announced, so it has to already exist when the error arrives. */}
            <div className="field__errors" id={newErrorId} aria-live="polite">
              {newErrors.map((message) => (
                <p key={message}>{message}</p>
              ))}
            </div>
          </div>

          <div>
            <button className="btn btn--primary" type="submit" disabled={mutation.isPending}>
              {mutation.isPending && <span className="spinner" aria-hidden="true" />}
              Change password
            </button>
          </div>

          {mutation.error && Object.keys(fieldErrors).length === 0 && (
            <p className="alert" role="alert">
              {mutation.error.message}
            </p>
          )}

          {mutation.isSuccess && (
            <p className="alert alert--success" role="status">
              Your password has been changed.
            </p>
          )}
        </div>
      </form>

      <div className="card order-form">
        <h2>Other sessions</h2>
        <p>
          Signs you out on every device, including this one. Changing your password already ends
          your other sessions.
        </p>
        <button
          className="btn btn--secondary"
          type="button"
          onClick={() => {
            signOutEverywhere.mutate();
          }}
          disabled={signOutEverywhere.isPending}
        >
          {signOutEverywhere.isPending && <span className="spinner" aria-hidden="true" />}
          {signOutEverywhere.isPending ? 'Signing out…' : 'Sign out everywhere'}
        </button>
        {signOutEverywhere.isError && (
          <p className="alert" role="alert">
            {signOutEverywhere.error.message}
          </p>
        )}
      </div>
    </>
  );
}
