import { Link, NavLink, Outlet, useNavigate } from 'react-router-dom';
import { Mark } from './components/Mark';
import { useLogout, useSession } from './features/auth/queries';
import './AppLayout.css';

export function AppLayout(): React.JSX.Element {
  const { data: session } = useSession();
  const logout = useLogout();
  const navigate = useNavigate();

  return (
    <div className="shell">
      <header className="shell__bar">
        <Link className="shell__brand" to="/orders">
          <Mark size={28} />
          AI Framework
        </Link>

        <nav className="shell__nav">
          {/* `end`, so that /orders/new and /orders/:id do not also light up "All orders". */}
          <NavLink to="/orders" end>
            All orders
          </NavLink>
          <NavLink to="/orders/new">Place an order</NavLink>
          <NavLink to="/account/password">Password</NavLink>
        </nav>

        <div className="shell__session">
          {/* RequireAuth guards this subtree, so session is set by the time the shell renders -
              but the query can still be refetching, so this stays defensive rather than
              asserting. */}
          {session && <span className="shell__user">{session.displayName}</span>}
          <button
            className="btn btn--secondary"
            type="button"
            disabled={logout.isPending}
            onClick={() => {
              logout.mutate(undefined, { onSuccess: () => void navigate('/login') });
            }}
          >
            {logout.isPending && <span className="spinner" aria-hidden="true" />}
            Sign out
          </button>
        </div>
      </header>

      {logout.error && (
        <p className="alert shell__error" role="alert">
          {logout.error.message}
        </p>
      )}

      <main className="shell__content">
        <Outlet />
      </main>
    </div>
  );
}
