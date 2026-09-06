import { Navigate, Route, Routes } from 'react-router-dom';
import { AppLayout } from './AppLayout';
import { LoginPage } from './features/auth/LoginPage';
import { OrderDetail } from './features/orders/OrderDetail';
import { OrderList } from './features/orders/OrderList';
import { PlaceOrderForm } from './features/orders/PlaceOrderForm';

export function AppRoutes(): React.JSX.Element {
  return (
    <Routes>
      {/* Outside the layout route on purpose: login is a full-bleed page, not something to
          render underneath the app's own header and nav. */}
      <Route path="/login" element={<LoginPage />} />

      <Route element={<AppLayout />}>
        <Route path="/" element={<Navigate to="/orders" replace />} />
        <Route path="/orders" element={<OrderList />} />
        <Route path="/orders/new" element={<PlaceOrderForm />} />
        <Route path="/orders/:id" element={<OrderDetail />} />
      </Route>
    </Routes>
  );
}
