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
    },
  },
  preview: {
    port: Number(process.env.PREVIEW_PORT ?? 4173),
    proxy: {
      '/api': { target: `http://localhost:${apiPort}`, changeOrigin: true },
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    globals: true,
    // Vitest's default include glob matches *.spec.ts, which also matches
    // e2e/orders.spec.ts - a Playwright spec, not a Vitest one. Excluding e2e/ keeps the two
    // runners from fighting over the same file.
    exclude: [...configDefaults.exclude, 'e2e/**'],
  },
});
