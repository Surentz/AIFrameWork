import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QuickAnswerCard } from './QuickAnswerCard';
import type { AssistantAnswer } from './types';

const answer: AssistantAnswer = {
  kind: 'Instant',
  text: 'Your last order shipped on 3 Oct 2026, 14:12 UTC.',
  facts: [{ label: 'Product', value: 'Widget Pro (SKU-1042)' }],
  table: { columns: ['Order', 'Status'], rows: [['3f2a1b9c', 'Shipped']] },
  link: { label: 'Open order', to: '/orders/3f2a1b9c-0000-4000-8000-000000000001' },
};

function renderCard(card: AssistantAnswer): void {
  render(
    <MemoryRouter>
      <QuickAnswerCard answer={card} />
    </MemoryRouter>,
  );
}

describe('QuickAnswerCard', () => {
  it('shows the badge, the sentence, the facts, the table and the link', () => {
    renderCard(answer);

    const card = screen.getByRole('article', { name: 'Instant answer' });
    expect(card).toHaveTextContent('Instant answer');
    expect(card).toHaveTextContent('Your last order shipped on 3 Oct 2026, 14:12 UTC.');
    expect(screen.getByText('Widget Pro (SKU-1042)')).toBeInTheDocument();
    expect(screen.getByRole('cell', { name: 'Shipped' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /Open order/ })).toHaveAttribute(
      'href',
      '/orders/3f2a1b9c-0000-4000-8000-000000000001',
    );
  });

  it('names a help answer by its article', () => {
    renderCard({ ...answer, kind: 'Help', title: 'Exporting your orders', facts: [], table: null });

    expect(screen.getByRole('article', { name: 'Help answer' })).toHaveTextContent('Help · Exporting your orders');
  });

  it('says what it answered when the spelling was corrected', () => {
    renderCard({ ...answer, answeredAs: 'has my last order shipped' });

    expect(screen.getByText('Answered as: has my last order shipped')).toBeInTheDocument();
  });

  it('never renders a link outside the app', () => {
    renderCard({ ...answer, link: { label: 'Click me', to: 'https://evil.example' } });

    expect(screen.queryByRole('link')).not.toBeInTheDocument();
  });

  it('renders text, never markup', () => {
    renderCard({ ...answer, text: '<img src=x onerror=alert(1)>' });

    expect(screen.getByText('<img src=x onerror=alert(1)>')).toBeInTheDocument();
    expect(document.querySelector('img')).toBeNull();
  });
});
