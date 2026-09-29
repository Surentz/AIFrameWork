import { useId, useState } from 'react';
import type { SubmitEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { AuthShell } from './AuthShell';
import { useLogin } from './queries';
import { ErrorPanel } from '../../components/ErrorPanel';

export function LoginPage(): React.JSX.Element {
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [rememberMe, setRememberMe] = useState(false);
  const [passwordVisible, setPasswordVisible] = useState(false);
  const navigate = useNavigate();
  const mutation = useLogin();

  function handleSubmit(event: SubmitEvent<HTMLFormElement>): void {
    event.preventDefault();
    mutation.mutate(
      { username, password, rememberMe },
      { onSuccess: () => void navigate('/orders') },
    );
  }

  const fieldErrors = mutation.error?.fieldErrors ?? {};
  const usernameErrors = fieldErrors.Username ?? [];
  const passwordErrors = fieldErrors.Password ?? [];
  // useId, not literals: two mounted forms would emit duplicate ids, and both htmlFor and
  // aria-describedby would resolve to the first form's inputs.
  const usernameId = useId();
  const passwordId = useId();
  const usernameErrorId = useId();
  const passwordErrorId = useId();

  return (
    <AuthShell
      title="Welcome back"
      subtitle="Sign in to your AI Framework account."
      footer={
        <>
          New here?{' '}
          <Link className="login__link" to="/register">
            Create an account
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
          <label className="field__label" htmlFor={passwordId}>
            Password
          </label>
          <div className="login__password">
            <input
              className="input"
              id={passwordId}
              type={passwordVisible ? 'text' : 'password'}
              autoComplete="current-password"
              placeholder="Enter your password"
              value={password}
              aria-invalid={passwordErrors.length > 0}
              aria-describedby={passwordErrors.length > 0 ? passwordErrorId : undefined}
              onChange={(e) => {
                setPassword(e.target.value);
              }}
            />
            <button
              className="login__reveal"
              type="button"
              aria-label={passwordVisible ? 'Hide password' : 'Show password'}
              onClick={() => {
                setPasswordVisible((visible) => !visible);
              }}
            >
              {passwordVisible ? 'Hide' : 'Show'}
            </button>
          </div>
          {/* Rendered unconditionally: a live region inserted together with its text may
              not be announced, so it has to already exist when the error arrives. */}
          <div className="field__errors" id={passwordErrorId} aria-live="polite">
            {passwordErrors.map((message) => (
              <p key={message}>{message}</p>
            ))}
          </div>
        </div>

        <div className="login__meta">
          <label className="login__remember">
            <input
              type="checkbox"
              checked={rememberMe}
              onChange={(e) => {
                setRememberMe(e.target.checked);
              }}
            />
            Remember me
          </label>
        </div>

        <button className="btn btn--primary btn--block" type="submit" disabled={mutation.isPending}>
          {mutation.isPending && <span className="spinner" aria-hidden="true" />}
          {mutation.isPending ? 'Signing in' : 'Sign in'}
        </button>

        {mutation.error && Object.keys(fieldErrors).length === 0 && (
          <ErrorPanel error={mutation.error} />
        )}
      </form>
    </AuthShell>
  );
}
