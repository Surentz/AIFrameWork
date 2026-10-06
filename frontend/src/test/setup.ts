import '@testing-library/jest-dom/vitest';
import { server } from './handlers';

// onUnhandledFrame: 'error' is the point. Without it a component that requests the wrong
// URL falls through unmocked and the test passes anyway - the request never had to be right.
beforeAll(() => {
  server.listen({ onUnhandledFrame: 'error' });
});
afterEach(() => {
  server.resetHandlers();
});
afterAll(() => {
  server.close();
});

// jsdom 30 has HTMLDialogElement and none of its behaviour. Enough of it to open a dialog, find
// what is in it, and close it - nothing more: Esc, the focus trap and returning focus are the
// browser's, and Playwright tests them (e2e/specs/orders/exports.spec.ts).
if (typeof HTMLDialogElement.prototype.showModal !== 'function') {
  Object.defineProperty(HTMLDialogElement.prototype, 'open', {
    configurable: true,
    get(this: HTMLDialogElement) {
      return this.hasAttribute('open');
    },
  });
  HTMLDialogElement.prototype.showModal = function showModal(this: HTMLDialogElement) {
    this.setAttribute('open', '');
  };
  HTMLDialogElement.prototype.close = function close(this: HTMLDialogElement) {
    if (this.hasAttribute('open')) {
      this.removeAttribute('open');
      this.dispatchEvent(new Event('close'));
    }
  };
}

// jsdom has no ResizeObserver either; the export viewer fits its pages to the width it reports.
// A stub that never reports: the viewer then renders at react-pdf's default width.
if (typeof globalThis.ResizeObserver === 'undefined') {
  globalThis.ResizeObserver = class {
    observe(): void {
      // Never reports a size.
    }
    unobserve(): void {
      // Nothing was observed.
    }
    disconnect(): void {
      // Nothing was observed.
    }
  };
}
