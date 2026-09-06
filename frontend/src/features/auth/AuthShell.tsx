import type { ReactNode } from 'react';
import { Mark } from '../../components/Mark';
import './LoginPage.css';

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

interface AuthShellProps {
  readonly title: string;
  readonly subtitle: string;
  readonly children: ReactNode;
  readonly footer: ReactNode;
}

/**
 * The split brand/form layout, shared by sign-in and registration. Extracted rather than
 * duplicated: the brand panel is a third of the markup and all of the gradient CSS.
 */
export function AuthShell({
  title,
  subtitle,
  children,
  footer,
}: AuthShellProps): React.JSX.Element {
  return (
    <div className="login">
      {/* The landmark lives here rather than in AppLayout: these pages render outside the
          layout route, so without this they would have no <main> at all. */}
      <main className="login__panel">
        <div className="login__card">
          <div className="login__mark">
            <Mark size={36} />
          </div>

          <h1>{title}</h1>
          <p className="login__subtitle">{subtitle}</p>

          {children}

          <p className="login__footer">{footer}</p>
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
