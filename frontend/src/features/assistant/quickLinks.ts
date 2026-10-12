/**
 * The screens an answer may link to. A link the server sends is rendered only if it matches one of
 * these exactly, so a reply can never send someone off the app. Kept in step with
 * tests/Application.Tests/Assistant/Quick/QuickCatalogTests.cs.
 */
const allowed: readonly RegExp[] = [
  /^\/orders$/,
  /^\/orders\/new$/,
  /^\/orders\/exports$/,
  /^\/orders\/[0-9a-f-]{36}$/,
  /^\/products$/,
  /^\/products\/[0-9a-f-]{36}$/,
  /^\/statistics\/population$/,
  /^\/monitoring(\/(jobs|traffic|integrations|logins|users))?$/,
  /^\/account\/password$/,
  /^\/notifications$/,
];

export function isAllowedLink(to: string): boolean {
  return allowed.some((pattern) => pattern.test(to));
}
