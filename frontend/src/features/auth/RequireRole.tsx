import { Navigate, Outlet, useLocation } from 'react-router-dom';
import { useSession } from './queries';
import type { UserRole } from './types';

interface RequireRoleProps {
  /**
   * Named `allow` rather than `role`: `jsx-a11y/aria-role` inspects a `role` prop on ANY JSX
   * element, component or not, and rejects "Admin" as an invalid ARIA role. Renaming is the fix
   * rather than a disable comment — it also stops a reader wondering whether this is ARIA.
   */
  readonly allow: UserRole;
}

/**
 * Gate for the routes that need a particular role, layered inside `RequireAuth` rather than
 * replacing it: that one answers "is anyone signed in", this one answers "may they be here".
 *
 * Like `RequireAuth`, this is a convenience and NOT the security boundary — that is
 * `[Authorize(Policy = "Monitoring")]` on the API, which reads the role from the database on
 * every request. All this does is explain the refusal instead of letting a page fill with 403s.
 * See ADR 0020.
 */
export function RequireRole({ allow }: RequireRoleProps): React.JSX.Element {
  const { data: session, isPending, error } = useSession();
  const location = useLocation();

  if (isPending) {
    return <p role="status">Checking your session…</p>;
  }

  if (error) {
    return (
      <p className="alert" role="alert">
        {error.message}
      </p>
    );
  }

  if (!session) {
    return <Navigate to="/login" replace state={{ from: location.pathname }} />;
  }

  // Told, not bounced. Reaching here means the address was typed by hand — the nav entry is
  // hidden for anyone without the role — and a silent redirect would leave that person unsure
  // whether the page was missing, broken, or refused.
  if (session.role !== allow) {
    return (
      <p className="alert" role="alert">
        This page is for administrators. Your account does not have access.
      </p>
    );
  }

  return <Outlet />;
}
