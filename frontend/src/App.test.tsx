import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { withQueryClient } from './test/withQueryClient';
import { App } from './App';

describe('App', () => {
  // findBy, not getBy: RequireAuth checks the session before the shell renders anything, so the
  // heading arrives a tick later than it used to.
  it('lands on the dashboard once the session resolves', async () => {
    render(
      <MemoryRouter>
        <App />
      </MemoryRouter>,
      { wrapper: withQueryClient() },
    );

    expect(await screen.findByRole('heading', { name: 'Dashboard', level: 1 })).toBeInTheDocument();
  });
});
