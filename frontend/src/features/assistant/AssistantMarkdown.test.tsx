import { render, screen } from '@testing-library/react';
import { AssistantMarkdown } from './AssistantMarkdown';

describe('AssistantMarkdown', () => {
  it('renders a GFM table', () => {
    render(<AssistantMarkdown text={'| Item | Qty |\n|---|---|\n| SKU-1 | 2 |'} />);

    expect(screen.getByRole('table')).toHaveTextContent('SKU-1');
  });

  it('renders a list and bold text', () => {
    render(<AssistantMarkdown text={'- **Placed** today\n- Shipped yesterday'} />);

    expect(screen.getAllByRole('listitem')).toHaveLength(2);
    expect(screen.getByText('Placed').tagName).toBe('STRONG');
  });

  it('never turns HTML from the model into elements', () => {
    const { container } = render(
      <AssistantMarkdown text={'Hi <img src="x" onerror="alert(1)"> <script>alert(2)</script>'} />,
    );

    expect(container.querySelector('img')).toBeNull();
    expect(container.querySelector('script')).toBeNull();
    expect(container).toHaveTextContent('Hi');
  });

  it('opens links in a new tab without access to this page', () => {
    render(<AssistantMarkdown text="[docs](https://example.com)" />);

    const link = screen.getByRole('link', { name: 'docs' });
    expect(link).toHaveAttribute('target', '_blank');
    expect(link).toHaveAttribute('rel', 'noopener noreferrer');
  });

  it('does not make a javascript: link clickable', () => {
    render(<AssistantMarkdown text="[click](javascript:alert(1))" />);

    expect(screen.getByText('click').closest('a')?.getAttribute('href') ?? '').not.toContain('javascript');
  });
});
