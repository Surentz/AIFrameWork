import { Navigate, Route, Routes } from 'react-router-dom';
import { AppLayout } from './AppLayout';
import { ChangePasswordPage } from './features/auth/ChangePasswordPage';
import { LoginPage } from './features/auth/LoginPage';
import { RegisterPage } from './features/auth/RegisterPage';
import { RequireAuth } from './features/auth/RequireAuth';
import { RequireRole } from './features/auth/RequireRole';
import { FulfilmentPage } from './features/fulfilment/FulfilmentPage';
import { IntegrationsPage } from './features/monitoring/IntegrationsPage';
import { JobsPage } from './features/monitoring/JobsPage';
import { LoginsPage } from './features/monitoring/LoginsPage';
import { MonitoringPage } from './features/monitoring/MonitoringPage';
import { TrafficPage } from './features/monitoring/TrafficPage';
import { UsersPage } from './features/monitoring/UsersPage';
import { NotificationList } from './features/notifications/NotificationList';
import { OrderDetail } from './features/orders/OrderDetail';
import { OrderExportsPage } from './features/orders/OrderExportsPage';
import { OrderList } from './features/orders/OrderList';
import { PlaceOrderForm } from './features/orders/PlaceOrderForm';
import { CreateProductForm } from './features/products/CreateProductForm';
import { EditProductForm } from './features/products/EditProductForm';
import { ProductDetail } from './features/products/ProductDetail';
import { ProductList } from './features/products/ProductList';

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
          {/* Before /orders/:id, for the reason /products/new is: otherwise "exports" is an id. */}
          <Route path="/orders/exports" element={<OrderExportsPage />} />
          <Route path="/orders/:id" element={<OrderDetail />} />
          {/* /products/new is declared before /products/:id so "new" is matched as the literal
              route, not captured as an id. */}
          <Route path="/products" element={<ProductList />} />
          {/* Writing to the catalogue is an administrator's (ADR 0025). Gated here, like the
              monitoring routes, so a member who types the address is told rather than shown a
              form whose submit the API would refuse. */}
          <Route element={<RequireRole allow="Admin" />}>
            <Route path="/products/new" element={<CreateProductForm />} />
            <Route path="/products/:id/edit" element={<EditProductForm />} />
          </Route>
          <Route path="/products/:id" element={<ProductDetail />} />
          <Route path="/notifications" element={<NotificationList />} />
          <Route path="/account/password" element={<ChangePasswordPage />} />

          {/* A second layout route inside RequireAuth: signed in AND an administrator. Nested
              rather than checked in the page, so the drill-down routes phases 2 to 4 add are
              gated by being declared here rather than by each one remembering. */}
          <Route element={<RequireRole allow="Admin" />}>
            <Route path="/monitoring" element={<MonitoringPage />} />
            <Route path="/monitoring/integrations" element={<IntegrationsPage />} />
            <Route path="/monitoring/jobs" element={<JobsPage />} />
            <Route path="/monitoring/logins" element={<LoginsPage />} />
            <Route path="/monitoring/traffic" element={<TrafficPage />} />
            <Route path="/monitoring/users" element={<UsersPage />} />
            <Route path="/fulfilment" element={<FulfilmentPage />} />
          </Route>
        </Route>
      </Route>
    </Routes>
  );
}
