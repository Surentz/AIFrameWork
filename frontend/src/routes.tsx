import { Navigate, Route, Routes } from 'react-router-dom';
import { OrderDetail } from './features/orders/OrderDetail';
import { OrderList } from './features/orders/OrderList';
import { PlaceOrderForm } from './features/orders/PlaceOrderForm';

export function AppRoutes(): React.JSX.Element {
  return (
    <Routes>
      <Route path="/" element={<Navigate to="/orders" replace />} />
      <Route path="/orders" element={<OrderList />} />
      <Route path="/orders/new" element={<PlaceOrderForm />} />
      <Route path="/orders/:id" element={<OrderDetail />} />
    </Routes>
  );
}
