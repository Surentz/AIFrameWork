import { defineConfig } from 'vite';
import { configDefaults } from 'vitest/config';
import react from '@vitejs/plugin-react';

const apiPort = process.env.API_PORT ?? '5234';

export default defineConfig({
  plugins: [react()],
  server: {
    port: Number(process.env.DEV_PORT ?? 5173),
    // Opens the app in its own browser tab on `npm start`, so a Rider Compound
    // configuration that launches both stacks gives you two tabs: the API reference from
    // src/Api/Properties/launchSettings.json, and this. Only affects the dev server -
    // `preview` below is what `npm run e2e` drives, and it stays headless.
    open: true,
    proxy: {
      '/api': { target: `http://localhost:${apiPort}`, changeOrigin: true },
      // The SignalR hub, which Program.cs maps at /hubs/notifications. Without this entry the
      // browser asks the DEV SERVER for the hub and gets Vite's own 404, so push silently never
      // works while /api keeps working - and `ws: true` is what upgrades the connection rather
      // than leaving it on the long-polling fallback.
      '/hubs': { target: `http://localhost:${apiPort}`, changeOrigin: true, ws: true },
    },
  },
  preview: {
    port: Number(process.env.PREVIEW_PORT ?? 4173),
    proxy: {
      '/api': { target: `http://localhost:${apiPort}`, changeOrigin: true },
      '/hubs': { target: `http://localhost:${apiPort}`, changeOrigin: true, ws: true },
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    globals: true,
    // Vitest's default include glob matches *.spec.ts, which also matches specs under
    // e2e/specs/ (e.g. e2e/specs/orders/place-order.spec.ts) - Playwright specs, not Vitest
    // ones. Excluding e2e/ keeps the two runners from fighting over the same files.
    exclude: [...configDefaults.exclude, 'e2e/**'],
  },
});
