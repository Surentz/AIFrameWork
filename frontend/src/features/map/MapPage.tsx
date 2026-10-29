import { Suspense, lazy } from 'react';
import './map.css';

/** What the page shows when the map's own code cannot be fetched. */
function MapUnavailable(): React.JSX.Element {
  return (
    <p className="map__note" role="alert">
      The map could not load. Reload the page to try again.
    </p>
  );
}

// Leaflet is a separate chunk, and a chunk can fail to arrive: offline, or a hashed file a deploy
// has since replaced. Without this the rejection is thrown during render and takes the page down.
const StreetMap = lazy(() => import('./StreetMap').catch(() => ({ default: MapUnavailable })));

/** A pannable, zoomable street map. Order tracking will draw on it later. */
export function MapPage(): React.JSX.Element {
  return (
    <section className="map">
      <h1>Map</h1>
      <Suspense fallback={<p role="status">Loading map…</p>}>
        <StreetMap />
      </Suspense>
    </section>
  );
}
