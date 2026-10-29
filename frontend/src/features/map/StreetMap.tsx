import type { LatLngTuple } from 'leaflet';
import { MapContainer, TileLayer } from 'react-leaflet';
import 'leaflet/dist/leaflet.css';

/** Central Denmark at a zoom that shows the whole country: where the app's data lives. */
const Denmark: LatLngTuple = [56.0, 10.5];
const DenmarkZoom = 7;

/**
 * OpenStreetMap's own tile servers: free and keyless, but under a fair-use policy
 * (https://operations.osmfoundation.org/policies/tiles/) that rules out heavy production traffic.
 * The attribution is required by the ODbL and must stay visible.
 */
const TileUrl = 'https://tile.openstreetmap.org/{z}/{x}/{y}.png';
const Attribution =
  '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors';

/** Leaflet lives in this module only, so it loads as its own chunk when the map page opens. */
export default function StreetMap(): React.JSX.Element {
  return (
    <div className="map__frame" role="region" aria-label="Street map">
      <MapContainer className="map__canvas" center={Denmark} zoom={DenmarkZoom} scrollWheelZoom>
        <TileLayer url={TileUrl} attribution={Attribution} />
      </MapContainer>
    </div>
  );
}
