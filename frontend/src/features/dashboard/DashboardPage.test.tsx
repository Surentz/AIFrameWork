import { render, screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router-dom';
import { anAdminSession, server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { DashboardPage } from './DashboardPage';

function renderPage(): void {
  render(
    <MemoryRouter initialEntries={['/']}>
      <DashboardPage />
    </MemoryRouter>,
    { wrapper: withQueryClient() },
  );
}

const tileTitles = (): (string | null)[] =>
  screen.getAllByRole('heading', { level: 2 }).map((heading) => heading.textContent);

describe('DashboardPage', () => {
  it('has one tile for each group a member can use', () => {
    renderPage();

    expect(screen.getByRole('heading', { name: 'Dashboard', level: 1 })).toBeInTheDocument();
    expect(tileTitles()).toEqual(['Sales', 'Catalogue', 'Insights', 'Account']);
  });

  it("lists a group's pages as subtiles that link to them", () => {
    renderPage();

    expect(screen.getByRole('link', { name: 'All orders, in Sales' })).toHaveAttribute(
      'href',
      '/orders',
    );
    expect(screen.getByRole('link', { name: 'Place an order, in Sales' })).toHaveAttribute(
      'href',
      '/orders/new',
    );
    expect(screen.getByRole('link', { name: 'Exports, in Sales' })).toHaveAttribute(
      'href',
      '/orders/exports',
    );
    expect(screen.getByRole('link', { name: 'Products, in Catalogue' })).toHaveAttribute(
      'href',
      '/products',
    );
    expect(screen.getByRole('link', { name: 'Population, in Insights' })).toHaveAttribute(
      'href',
      '/statistics/population',
    );
    expect(screen.getByRole('link', { name: 'Password, in Account' })).toHaveAttribute(
      'href',
      '/account/password',
    );
  });

  it('says what each page is for', () => {
    renderPage();

    expect(screen.getByText('Download your orders as a PDF')).toBeInTheDocument();
  });

  it('greets the signed-in user by name', async () => {
    renderPage();

    expect(await screen.findByText(/Welcome back, Ada Lovelace/)).toBeInTheDocument();
  });

  it('gives a member no operations tile, and no link to its pages', async () => {
    renderPage();
    await screen.findByText(/Welcome back, Ada Lovelace/);

    expect(screen.queryByRole('heading', { name: 'Operations' })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /Monitoring/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /Fulfilment/ })).not.toBeInTheDocument();
  });

  it("adds the operations tile once an administrator's session arrives", async () => {
    server.use(http.get('/api/auth/me', () => HttpResponse.json(anAdminSession)));
    renderPage();

    expect(
      await screen.findByRole('heading', { name: 'Operations', level: 2 }),
    ).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Monitoring, in Operations' })).toHaveAttribute(
      'href',
      '/monitoring',
    );
    expect(screen.getByRole('link', { name: 'Fulfilment, in Operations' })).toHaveAttribute(
      'href',
      '/fulfilment',
    );
  });
});
