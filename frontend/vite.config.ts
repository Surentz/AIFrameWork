/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

const apiPort = process.env.API_PORT ?? '5234';

export default defineConfig({
  plugins: [react()],
  server: {
    port: Number(process.env.DEV_PORT ?? 5173),
    proxy: {
      '/api': { target: `http://localhost:${apiPort}`, changeOrigin: true },
    },
  },
  preview: { port: Number(process.env.PREVIEW_PORT ?? 4173) },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    globals: true,
  },
});
