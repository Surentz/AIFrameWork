import '@testing-library/jest-dom/vitest';
import { server } from './handlers';

// onUnhandledRequest: 'error' is the point. Without it a component that requests the wrong
// URL falls through unmocked and the test passes anyway - the request never had to be right.
beforeAll(() => {
  server.listen({ onUnhandledRequest: 'error' });
});
afterEach(() => {
  server.resetHandlers();
});
afterAll(() => {
  server.close();
});
