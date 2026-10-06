import { render, screen, within } from '@testing-library/react';
import { anOrderExport } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { ExportViewerDialog } from './ExportViewerDialog';

// The viewer's chunk failing to load (offline, or a hashed file a deploy replaced): importing it
// throws, the way a failed dynamic import rejects in the browser.
vi.mock('./ExportViewer', () => {
  throw new Error('Failed to fetch dynamically imported module');
});

describe('ExportViewerDialog', () => {
  it('offers the download when the viewer itself cannot load', async () => {
    render(<ExportViewerDialog exportItem={anOrderExport} onClose={() => undefined} />, {
      wrapper: withQueryClient(),
    });

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('The viewer could not load.');
    expect(within(alert).getByRole('link', { name: 'Download' })).toHaveAttribute(
      'href',
      `/api/orders/exports/${anOrderExport.id}/download`,
    );
  });
});
