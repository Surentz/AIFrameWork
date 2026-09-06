import { Navigate, Route, Routes } from 'react-router-dom';
import { AppLayout } from './AppLayout';
import { ChangePasswordPage } from './features/auth/ChangePasswordPage';
import { LoginPage } from './features/auth/LoginPage';
import { RegisterPage } from './features/auth/RegisterPage';
import { RequireAuth } from './features/auth/RequireAuth';
import { OrderDetail } from './features/orders/OrderDetail';
import { OrderList } from './features/orders/OrderList';
import { PlaceOrderForm } from './features/orders/PlaceOrderForm';

export function AppRoutes(): React.JSX.Element {
  return (
    <Routes>
      {/* Outside the layout route on purpose: these are full-bleed pages, not something to
          render underneath the app's own header and nav. */}
      <Route path="/login" element={<LoginPage />} />
      <Route path="/register" element={<RegisterPage />} />

      {/* RequireAuth wraps the layout rather than the other way round, so a signed-out visitor
          is redirected before the shell renders a header they cannot use. */}
      <Route element={<RequireAuth />}>
        <Route element={<AppLayout />}>
          <Route path="/" element={<Navigate to="/orders" replace />} />
          <Route path="/orders" element={<OrderList />} />
          <Route path="/orders/new" element={<PlaceOrderForm />} />
          <Route path="/orders/:id" element={<OrderDetail />} />
          <Route path="/account/password" element={<ChangePasswordPage />} />
        </Route>
      </Route>
    </Routes>
  );
}
