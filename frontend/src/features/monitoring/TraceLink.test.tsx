import { render, screen } from '@testing-library/react';
import { TraceLink } from './TraceLink';

const id = '0af7651916cd43dd8448eb211c80319c';

describe('TraceLink', () => {
  it('links to the trace when a template is configured', () => {
    render(<TraceLink traceId={id} template="https://logs.test/trace/{traceId}" />);

    const link = screen.getByRole('link', { name: `Open trace ${id} in the log store` });
    expect(link).toHaveAttribute('href', `https://logs.test/trace/${id}`);
    // The log store is another application: leaving the monitoring page for it would lose the
    // operator's filters and page.
    expect(link).toHaveAttribute('target', '_blank');
  });

  it('shows the id as text when no template is configured', () => {
    render(<TraceLink traceId={id} template={undefined} />);

    expect(screen.queryByRole('link')).not.toBeInTheDocument();
    expect(screen.getByText(id)).toBeInTheDocument();
  });

  it('shows a dash for a row that was not traced', () => {
    render(<TraceLink traceId={null} template="https://logs.test/trace/{traceId}" />);

    expect(screen.queryByRole('link')).not.toBeInTheDocument();
    expect(screen.getByText('—')).toBeInTheDocument();
  });
});
