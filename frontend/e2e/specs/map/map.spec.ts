import { expect, test } from '../../fixtures/index.ts';
import * as map from '../../screens/map.ts';
import * as shell from '../../screens/shell.ts';

test('shows a street map from the Sales group, credited to OpenStreetMap', async ({
  signedInPage,
}) => {
  await map.stubTiles(signedInPage);
  await signedInPage.goto('/');

  await shell.goTo(signedInPage, 'Sales', 'Map');

  await expect(map.pageHeading(signedInPage)).toBeVisible();
  await expect(map.streetMap(signedInPage)).toBeVisible();
  await expect(map.attribution(signedInPage)).toBeVisible();
  await expect(map.tiles(signedInPage).first()).toBeVisible();
});

test('zooms in', async ({ signedInPage }) => {
  await map.stubTiles(signedInPage);
  await signedInPage.goto('/map');
  // Tile URLs carry the zoom level first: /{z}/{x}/{y}.png. The page opens at 7.
  await expect(map.tiles(signedInPage).first()).toHaveAttribute('src', /\/7\/\d+\/\d+\.png$/);

  await map.zoomIn(signedInPage).click();

  await expect(map.tiles(signedInPage).first()).toHaveAttribute('src', /\/8\/\d+\/\d+\.png$/);
});
