import { useId, useState } from 'react';
import type { SubmitEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { AuthShell } from './AuthShell';
import { useRegister } from './queries';
import { ErrorPanel } from '../../components/ErrorPanel';

/** Mirrors PasswordPolicy.MinimumLength in src/Application/Users; the server is the authority. */
const MinimumPasswordLength = 12;

export function RegisterPage(): React.JSX.Element {
  const [username, setUsername] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [password, setPassword] = useState('');
  const navigate = useNavigate();
  const mutation = useRegister();

  function handleSubmit(event: SubmitEvent<HTMLFormElement>): void {
    event.preventDefault();
    mutation.mutate(
      { username, password, displayName },
      { onSuccess: () => void navigate('/orders') },
    );
  }

  const fieldErrors = mutation.error?.fieldErrors ?? {};
  const usernameErrors = fieldErrors.Username ?? [];
  const displayNameErrors = fieldErrors.DisplayName ?? [];
  const passwordErrors = fieldErrors.Password ?? [];
  const usernameId = useId();
  const displayNameId = useId();
  const passwordId = useId();
  const usernameErrorId = useId();
  const displayNameErrorId = useId();
  const passwordErrorId = useId();
  const passwordHintId = useId();

  return (
    <AuthShell
      title="Create an account"
      subtitle="Pick a username and a password."
      footer={
        <>
          Already have one?{' '}
          <Link className="login__link" to="/login">
            Sign in
          </Link>
        </>
      }
    >
      <form className="login__form" onSubmit={handleSubmit} noValidate>
        <div className="field">
          <label className="field__label" htmlFor={usernameId}>
            Username
          </label>
          <input
            className="input"
            id={usernameId}
            type="text"
            autoComplete="username"
            placeholder="ada"
            value={username}
            aria-invalid={usernameErrors.length > 0}
            aria-describedby={usernameErrors.length > 0 ? usernameErrorId : undefined}
            onChange={(e) => {
              setUsername(e.target.value);
            }}
          />
          {/* Rendered unconditionally: a live region inserted together with its text may
              not be announced, so it has to already exist when the error arrives. */}
          <div className="field__errors" id={usernameErrorId} aria-live="polite">
            {usernameErrors.map((message) => (
              <p key={message}>{message}</p>
            ))}
          </div>
        </div>

        <div className="field">
          <label className="field__label" htmlFor={displayNameId}>
            Display name
          </label>
          <input
            className="input"
            id={displayNameId}
            type="text"
            autoComplete="name"
            placeholder="Ada Lovelace"
            value={displayName}
            aria-invalid={displayNameErrors.length > 0}
            aria-describedby={displayNameErrors.length > 0 ? displayNameErrorId : undefined}
            onChange={(e) => {
              setDisplayName(e.target.value);
            }}
          />
          {/* Rendered unconditionally: a live region inserted together with its text may
              not be announced, so it has to already exist when the error arrives. */}
          <div className="field__errors" id={displayNameErrorId} aria-live="polite">
            {displayNameErrors.map((message) => (
              <p key={message}>{message}</p>
            ))}
          </div>
        </div>

        <div className="field">
          <label className="field__label" htmlFor={passwordId}>
            Password
          </label>
          <input
            className="input"
            id={passwordId}
            type="password"
            autoComplete="new-password"
            placeholder={`At least ${String(MinimumPasswordLength)} characters`}
            value={password}
            aria-invalid={passwordErrors.length > 0}
            aria-describedby={
              passwordErrors.length > 0 ? `${passwordHintId} ${passwordErrorId}` : passwordHintId
            }
            onChange={(e) => {
              setPassword(e.target.value);
            }}
          />
          <p className="login__hint" id={passwordHintId}>
            {`At least ${String(MinimumPasswordLength)} characters. Length is the only rule.`}
          </p>
          {/* Rendered unconditionally: a live region inserted together with its text may
              not be announced, so it has to already exist when the error arrives. */}
          <div className="field__errors" id={passwordErrorId} aria-live="polite">
            {passwordErrors.map((message) => (
              <p key={message}>{message}</p>
            ))}
          </div>
        </div>

        <button className="btn btn--primary btn--block" type="submit" disabled={mutation.isPending}>
          {mutation.isPending && <span className="spinner" aria-hidden="true" />}
          {mutation.isPending ? 'Creating account' : 'Create account'}
        </button>

        {mutation.error && Object.keys(fieldErrors).length === 0 && (
          <ErrorPanel error={mutation.error} />
        )}
      </form>
    </AuthShell>
  );
}
