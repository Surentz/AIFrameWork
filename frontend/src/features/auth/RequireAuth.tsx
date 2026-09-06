import { Navigate, Outlet, useLocation } from 'react-router-dom';
import { useSession } from './queries';

/**
 * Gate for the routes that need a session. A layout route, so it guards its whole subtree from
 * one place rather than each screen remembering to check.
 *
 * The guard is a convenience, not the security boundary — that is `[Authorize]` on the API. All
 * this does is send a signed-out visitor somewhere useful instead of letting them watch a page
 * fill with 401s.
 */
export function RequireAuth(): React.JSX.Element {
  const { data: session, isPending, error } = useSession();
  const location = useLocation();

  if (isPending) {
    return <p role="status">Checking your session…</p>;
  }

  // A 401 already resolved to null in useSession, so reaching here means the request itself
  // failed. Redirecting would hide that behind a login page the user does not need.
  if (error) {
    return (
      <p className="alert" role="alert">
        {error.message}
      </p>
    );
  }

  // `replace`, so Back does not bounce off the guard into a redirect loop; `state` remembers
  // where they were headed for a later "return to where you were".
  return session ? (
    <Outlet />
  ) : (
    <Navigate to="/login" replace state={{ from: location.pathname }} />
  );
}
