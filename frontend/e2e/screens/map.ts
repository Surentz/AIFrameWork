import type { Locator, Page } from '@playwright/test';

export const pageHeading = (p: Page): Locator => p.getByRole('heading', { level: 1, name: 'Map' });

export const streetMap = (p: Page): Locator => p.getByRole('region', { name: 'Street map' });

export const zoomIn = (p: Page): Locator => streetMap(p).getByRole('button', { name: 'Zoom in' });

export const attribution = (p: Page): Locator =>
  streetMap(p).getByRole('link', { name: 'OpenStreetMap' });

/** The tile images Leaflet has placed. */
export const tiles = (p: Page): Locator => streetMap(p).locator('img.leaflet-tile');

/**
 * Answers every OpenStreetMap tile request with a blank PNG, so the spec neither depends on nor
 * leans on the public tile servers (their policy forbids automated bulk use).
 */
export async function stubTiles(p: Page): Promise<void> {
  const blankPng = Buffer.from(
    'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=',
    'base64',
  );
  await p.route('https://tile.openstreetmap.org/**', (route) =>
    route.fulfill({ contentType: 'image/png', body: blankPng }),
  );
}
