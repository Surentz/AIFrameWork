import { isAllowedLink } from './quickLinks';

describe('isAllowedLink', () => {
  it.each([
    '/orders',
    '/orders/new',
    '/orders/exports',
    '/orders/3f2a1b9c-0000-4000-8000-000000000001',
    '/products/3f2a1b9c-0000-4000-8000-000000000001',
    '/statistics/population',
    '/monitoring/jobs',
    '/account/password',
  ])('allows %s', (to) => {
    expect(isAllowedLink(to)).toBe(true);
  });

  it.each(['https://evil.example', '//evil.example', '/orders/../admin', 'javascript:alert(1)', '/orders?x=1', ''])(
    'refuses %s',
    (to) => {
      expect(isAllowedLink(to)).toBe(false);
    },
  );
});
