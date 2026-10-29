import { render, screen } from '@testing-library/react';
import { MapPage } from './MapPage';

// The map's chunk failing to load (offline, or a hashed file a deploy replaced): importing it
// throws, the way a failed dynamic import rejects in the browser. A file of its own because the
// mock covers every test in the file.
vi.mock('./StreetMap', () => {
  throw new Error('Failed to fetch dynamically imported module');
});

describe('MapPage', () => {
  it('says so when the map itself cannot load', async () => {
    render(<MapPage />);

    expect(await screen.findByRole('alert')).toHaveTextContent('The map could not load.');
  });
});
