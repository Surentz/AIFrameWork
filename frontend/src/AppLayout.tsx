import { Link, Outlet } from 'react-router-dom';
import './AppLayout.css';

export function AppLayout(): React.JSX.Element {
  return (
    <div className="shell">
      <header className="shell__header">
        <h1 className="shell__title">Orders</h1>
        <nav className="shell__nav">
          <Link to="/orders">All orders</Link>
          <Link to="/orders/new">Place an order</Link>
          <Link to="/login">Sign in</Link>
        </nav>
      </header>
      <main className="shell__content">
        <Outlet />
      </main>
    </div>
  );
}
