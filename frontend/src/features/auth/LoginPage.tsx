import { useId, useState } from 'react';
import type { SubmitEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useLogin } from './queries';
import './LoginPage.css';

function Mark({ size }: { size: number }): React.JSX.Element {
  return (
    <svg width={size} height={size} viewBox="0 0 32 32" fill="none" aria-hidden="true">
      <rect width="32" height="32" rx="9" fill="currentColor" />
      {/* The glyph is knocked out of the tile, so its colour has to be whatever the tile is
          sitting on. --mark-ink lets the brand panel override it: there the tile is white and
          the default (--color-surface) would be white on white. */}
      <path
        d="M16 8.5 22.5 23h-3.4l-1.2-2.9h-3.8L12.9 23H9.5L16 8.5Zm0 5.9-1.1 2.8h2.2L16 14.4Z"
        fill="var(--mark-ink, var(--color-surface))"
      />
    </svg>
  );
}

function Tick(): React.JSX.Element {
  return (
    <svg
      className="login__tick"
      width="16"
      height="16"
      viewBox="0 0 16 16"
      fill="none"
      aria-hidden="true"
    >
      <path
        d="m3.5 8.5 3 3 6-7"
        stroke="currentColor"
        strokeWidth="1.75"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  );
}

export function LoginPage(): React.JSX.Element {
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [rememberMe, setRememberMe] = useState(false);
  const [passwordVisible, setPasswordVisible] = useState(false);
  const navigate = useNavigate();
  const mutation = useLogin();

  function handleSubmit(event: SubmitEvent<HTMLFormElement>): void {
    event.preventDefault();
    mutation.mutate({ email, password, rememberMe }, { onSuccess: () => void navigate('/orders') });
  }

  const fieldErrors = mutation.error?.fieldErrors ?? {};
  const emailErrors = fieldErrors.Email ?? [];
  const passwordErrors = fieldErrors.Password ?? [];
  // useId, not literals: two mounted forms would emit duplicate ids, and both htmlFor and
  // aria-describedby would resolve to the first form's inputs.
  const emailId = useId();
  const passwordId = useId();
  const emailErrorId = useId();
  const passwordErrorId = useId();

  return (
    <div className="login">
      {/* The landmark lives here rather than in AppLayout: /login renders outside the layout
          route, so without this the page would have no <main> at all. */}
      <main className="login__panel">
        <div className="login__card">
          <div className="login__mark">
            <Mark size={36} />
          </div>

          <h1>Welcome back</h1>
          <p className="login__subtitle">Sign in to your AI Framework account.</p>

          <form className="login__form" onSubmit={handleSubmit} noValidate>
            <div className="login__field">
              <label className="login__label" htmlFor={emailId}>
                Email
              </label>
              <input
                className="login__input"
                id={emailId}
                type="email"
                autoComplete="email"
                placeholder="you@example.com"
                value={email}
                aria-invalid={emailErrors.length > 0}
                aria-describedby={emailErrors.length > 0 ? emailErrorId : undefined}
                onChange={(e) => {
                  setEmail(e.target.value);
                }}
              />
              {/* Rendered unconditionally: a live region inserted together with its text may
                  not be announced, so it has to already exist when the error arrives. */}
              <div className="login__errors" id={emailErrorId} aria-live="polite">
                {emailErrors.map((message) => (
                  <p key={message}>{message}</p>
                ))}
              </div>
            </div>

            <div className="login__field">
              <label className="login__label" htmlFor={passwordId}>
                Password
              </label>
              <div className="login__password">
                <input
                  className="login__input"
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
              <div className="login__errors" id={passwordErrorId} aria-live="polite">
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
              <Link className="login__link" to="/login">
                Forgot password?
              </Link>
            </div>

            <button className="login__submit" type="submit" disabled={mutation.isPending}>
              {mutation.isPending && <span className="login__spinner" aria-hidden="true" />}
              {mutation.isPending ? 'Signing in' : 'Sign in'}
            </button>

            {mutation.error && Object.keys(fieldErrors).length === 0 && (
              <p className="login__alert" role="alert">
                {mutation.error.message}
              </p>
            )}
          </form>

          <p className="login__footer">
            New here?{' '}
            <Link className="login__link" to="/login">
              Create an account
            </Link>
          </p>
        </div>
      </main>

      <aside className="login__brand">
        <div className="login__wordmark">
          <Mark size={32} />
          AI Framework
        </div>

        <div className="login__pitch">
          <h2>Ship features, not plumbing.</h2>
          <p>
            Clean architecture, a durable event path and a typed API contract — wired together and
            ready on day one.
          </p>
          <ul className="login__points">
            <li>
              <Tick />
              Domain events delivered exactly once
            </li>
            <li>
              <Tick />
              An OpenAPI contract the frontend is generated from
            </li>
            <li>
              <Tick />
              Debug and Release both proven in CI
            </li>
          </ul>
        </div>
      </aside>
    </div>
  );
}
