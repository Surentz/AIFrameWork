import { render, screen } from '@testing-library/react';
import { MapPage } from './MapPage';

// Leaflet runs for real here: jsdom lays nothing out and loads no tile images, but the map, its
// zoom control and its attribution are all in the DOM. Panning and tiles are Playwright's to see.
describe('MapPage', () => {
  it('shows a street map once its chunk has loaded', async () => {
    render(<MapPage />);

    expect(screen.getByRole('heading', { level: 1, name: 'Map' })).toBeInTheDocument();
    expect(await screen.findByRole('region', { name: 'Street map' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Zoom in' })).toBeInTheDocument();
  });

  it('credits OpenStreetMap, as its licence requires', async () => {
    render(<MapPage />);

    expect(await screen.findByRole('link', { name: 'OpenStreetMap' })).toHaveAttribute(
      'href',
      'https://www.openstreetmap.org/copyright',
    );
  });
});
