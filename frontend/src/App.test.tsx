import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { withQueryClient } from './test/withQueryClient';
import { App } from './App';

describe('App', () => {
  it('renders the application heading', () => {
    render(
      <MemoryRouter>
        <App />
      </MemoryRouter>,
      { wrapper: withQueryClient() },
    );

    expect(screen.getByRole('heading', { name: 'Orders' })).toBeInTheDocument();
  });
});
