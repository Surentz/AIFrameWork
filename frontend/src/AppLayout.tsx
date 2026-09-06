import { Link, NavLink, Outlet } from 'react-router-dom';
import { Mark } from './components/Mark';
import './AppLayout.css';

export function AppLayout(): React.JSX.Element {
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
          <NavLink to="/login">Sign in</NavLink>
        </nav>
      </header>

      <main className="shell__content">
        <Outlet />
      </main>
    </div>
  );
}
