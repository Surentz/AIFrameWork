// Shared by playwright.config.ts and prepare-database.ts, so the e2e Postgres connection
// string and the ports it depends on are declared once rather than triplicated (a third
// copy lives in docker-compose.e2e.yml, which cannot import this - it reads the same
// PG_PORT name directly via shell ${PG_PORT:-55432} substitution instead).
export const API_PORT = process.env.API_PORT ?? '5234';
// The job worker's health endpoint. 5235 is also the dev loop's worker, so the two collide the
// same way the APIs do - stop the dev loop or set this.
export const WORKER_PORT = process.env.WORKER_PORT ?? '5235';
export const PREVIEW_PORT = process.env.PREVIEW_PORT ?? '4173';
export const PG_PORT = process.env.PG_PORT ?? '55432';

export const E2E_CONNECTION_STRING = `Host=localhost;Port=${PG_PORT};Database=aiframework_e2e;Username=e2e;Password=e2e`;

export const E2E_RABBITMQ_PORT = process.env.E2E_RABBITMQ_PORT ?? '55682';
export const E2E_RABBITMQ_UI_PORT = process.env.E2E_RABBITMQ_UI_PORT ?? '55683';
export const E2E_RABBITMQ_URL = `amqp://e2e:e2e@localhost:${E2E_RABBITMQ_PORT}/`;
/** The management HTTP API, which e2e/support/broker.ts publishes through. */
export const E2E_RABBITMQ_UI_URL = `http://localhost:${E2E_RABBITMQ_UI_PORT}`;
