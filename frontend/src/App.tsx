import { Link } from 'react-router-dom';
import { AppRoutes } from './routes';

export function App(): React.JSX.Element {
  return (
    <main>
      <h1>Orders</h1>
      <nav>
        <Link to="/orders">All orders</Link>
        <Link to="/orders/new">Place an order</Link>
      </nav>
      <AppRoutes />
    </main>
  );
}
