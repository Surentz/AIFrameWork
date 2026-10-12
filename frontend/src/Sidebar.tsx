import { Link, NavLink, useLocation } from 'react-router-dom';
import { Mark } from './components/Mark';
import type { UserRole } from './features/auth/types';
import { groupForPath, visibleGroups } from './navigation';
import { useNavFold } from './useNavFold';
import './Sidebar.css';

interface SidebarProps {
  readonly role: UserRole | undefined;
  /** Small screens only: whether the overlay is showing. On a wide screen the sidebar is always there. */
  readonly open: boolean;
  /** Called when a link is followed, so the small-screen overlay can close behind it. */
  readonly onNavigate: () => void;
}

export function Sidebar({ role, open, onNavigate }: SidebarProps): React.JSX.Element {
  const { pathname } = useLocation();
  const activeGroupId = groupForPath(pathname)?.id;
  const fold = useNavFold(activeGroupId);

  return (
    <aside id="app-sidebar" className={open ? 'sidebar sidebar--open' : 'sidebar'}>
      <Link className="sidebar__brand" to="/" onClick={onNavigate}>
        <Mark size={28} />
        AI Framework
      </Link>

      {/* "Main", because the assistant page has a second navigation, "Conversations", beside this. */}
      <nav className="sidebar__nav" aria-label="Main">
        <NavLink className="sidebar__link" to="/" end onClick={onNavigate}>
          Dashboard
        </NavLink>

        {visibleGroups(role).map((group) => {
          const expanded = fold.isOpen(group.id);
          const listId = `nav-group-${group.id}`;
          return (
            <div className="sidebar__group" key={group.id}>
              {/* A button, not a heading: the e2e catalogue screen finds `heading "Catalogue"`. */}
              <button
                type="button"
                className="sidebar__heading"
                aria-expanded={expanded}
                aria-controls={listId}
                onClick={() => {
                  fold.toggle(group.id);
                }}
              >
                <span>{group.label}</span>
                {!expanded && group.id === activeGroupId && (
                  <span className="sidebar__dot" aria-hidden="true" />
                )}
                <span className="sidebar__chevron" aria-hidden="true" />
              </button>
              <ul id={listId} className="sidebar__list" hidden={!expanded}>
                {group.items.map((item) => (
                  <li key={item.to}>
                    <NavLink
                      className="sidebar__link sidebar__link--page"
                      to={item.to}
                      end={item.end === true}
                      onClick={onNavigate}
                    >
                      {item.label}
                    </NavLink>
                  </li>
                ))}
              </ul>
            </div>
          );
        })}
      </nav>
    </aside>
  );
}
