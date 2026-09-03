// Shared by playwright.config.ts and global-setup.ts, so the e2e Postgres connection
// string and the ports it depends on are declared once rather than triplicated (a third
// copy lives in docker-compose.e2e.yml, which cannot import this - it reads the same
// PG_PORT name directly via shell ${PG_PORT:-55432} substitution instead).
export const API_PORT = process.env.API_PORT ?? '5234';
export const PREVIEW_PORT = process.env.PREVIEW_PORT ?? '4173';
export const PG_PORT = process.env.PG_PORT ?? '55432';

export const E2E_CONNECTION_STRING = `Host=localhost;Port=${PG_PORT};Database=aiframework_e2e;Username=e2e;Password=e2e`;
