import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { OrderList } from '../orders/OrderList';
import { LoginPage } from './LoginPage';

function renderPage(): void {
  render(
    <MemoryRouter initialEntries={['/login']}>
      <LoginPage />
    </MemoryRouter>,
    { wrapper: withQueryClient() },
  );
}

// Exercises the real route swap (/login -> /orders) the way the app renders it; a bare
// MemoryRouter with no <Routes> lets navigate() go nowhere, which would let a broken redirect
// pass this test.
function renderPageWithRoutes(): void {
  render(
    <MemoryRouter initialEntries={['/login']}>
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route path="/orders" element={<OrderList />} />
      </Routes>
    </MemoryRouter>,
    { wrapper: withQueryClient() },
  );
}

function requireElementById(id: string): HTMLElement {
  const element = document.getElementById(id);
  if (element === null) {
    throw new Error(`expected an element with id "${id}"`);
  }
  return element;
}

describe('LoginPage', () => {
  it('sends the typed credentials to the api and lands on the orders page', async () => {
    let body: unknown = null;
    server.use(
      http.post('/api/auth/login', async ({ request }) => {
        body = await request.json();
        return HttpResponse.json({
          userId: '33333333-3333-3333-3333-333333333333',
          email: 'ada@example.com',
          displayName: 'Ada Lovelace',
        });
      }),
    );
    renderPageWithRoutes();

    await userEvent.type(screen.getByLabelText('Email'), 'ada@example.com');
    await userEvent.type(screen.getByLabelText('Password'), 'correct horse');
    await userEvent.click(screen.getByLabelText('Remember me'));
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByRole('link', { name: 'SKU-1' })).toBeInTheDocument();
    expect(body).toEqual({
      email: 'ada@example.com',
      password: 'correct horse',
      rememberMe: true,
    });
  });

  it('shows a server field error against the field it belongs to', async () => {
    server.use(
      http.post('/api/auth/login', () =>
        HttpResponse.json(
          {
            title: 'validation.failed',
            detail: 'Enter a valid email address.',
            errors: { Email: ['Enter a valid email address.'] },
          },
          { status: 400 },
        ),
      ),
    );
    renderPage();

    await userEvent.type(screen.getByLabelText('Email'), 'not-an-email');
    await userEvent.type(screen.getByLabelText('Password'), 'correct horse');
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    await screen.findByText('Enter a valid email address.');

    const emailInput = screen.getByLabelText('Email');
    expect(emailInput).toHaveAttribute('aria-invalid', 'true');

    const describedBy = emailInput.getAttribute('aria-describedby');
    if (describedBy === null) {
      throw new Error('expected the email input to have aria-describedby set');
    }
    const errorRegion = requireElementById(describedBy);
    expect(within(errorRegion).getByText('Enter a valid email address.')).toBeInTheDocument();

    // Pins the field-level placement: the generic role="alert" banner branch must not also be
    // rendering, or a bug that routes field errors to the banner would pass this test too.
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('shows the general failure when the credentials are rejected without field errors', async () => {
    server.use(
      http.post('/api/auth/login', () =>
        HttpResponse.json(
          { title: 'auth.failed', detail: 'That email and password do not match.' },
          { status: 401 },
        ),
      ),
    );
    renderPage();

    await userEvent.type(screen.getByLabelText('Email'), 'ada@example.com');
    await userEvent.type(screen.getByLabelText('Password'), 'wrong');
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'That email and password do not match.',
    );
  });

  it('reveals and re-hides the password', async () => {
    renderPage();

    const password = screen.getByLabelText('Password');
    expect(password).toHaveAttribute('type', 'password');

    await userEvent.click(screen.getByRole('button', { name: 'Show password' }));
    expect(password).toHaveAttribute('type', 'text');

    await userEvent.click(screen.getByRole('button', { name: 'Hide password' }));
    expect(password).toHaveAttribute('type', 'password');
  });
});
