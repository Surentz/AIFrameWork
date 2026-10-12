import { Link } from 'react-router-dom';
import { NavIcon } from '../../NavIcon';
import { visibleGroups } from '../../navigation';
import { useSession } from '../auth/queries';
import './dashboard.css';

/**
 * The landing page: a tile per navigation group, a subtile per page in it. Built from the same
 * config as the sidebar, so the two cannot disagree. The only data is the session, which
 * RequireAuth has already resolved, so there is no query here and no error state to render.
 */
export function DashboardPage(): React.JSX.Element {
  const { data: session } = useSession();

  return (
    <div className="dashboard">
      <h1>Dashboard</h1>
      <p className="dashboard__lead">
        {session ? `Welcome back, ${session.displayName}. ` : ''}Jump to anything in the framework.
      </p>

      <div className="dashboard__grid">
        {visibleGroups(session?.role).map((group) => {
          const titleId = `dashboard-tile-${group.id}`;
          return (
            <section className="dashboard__tile" key={group.id} aria-labelledby={titleId}>
              <h2 id={titleId} className="dashboard__title">
                <span className="dashboard__icon">
                  <NavIcon name={group.icon} />
                </span>
                {group.label}
              </h2>
              <p className="dashboard__description">{group.description}</p>
              <ul className="dashboard__subs">
                {group.items.map((item) => (
                  <li key={item.to}>
                    {/* Named with its group so it never shares a name with the sidebar's link to
                        the same page: Playwright's getByRole is strict. */}
                    <Link
                      className="dashboard__sub"
                      to={item.to}
                      aria-label={`${item.label}, in ${group.label}`}
                    >
                      <span className="dashboard__sub-label">{item.label}</span>
                      <span className="dashboard__sub-description">{item.description}</span>
                    </Link>
                  </li>
                ))}
              </ul>
            </section>
          );
        })}
      </div>
    </div>
  );
}
