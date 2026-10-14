// Tests for dependabot-merge.js. Run with `node --test ".github/scripts/*.test.js"`; CI's frontend
// job runs them.
//
// Only the decisions are tested here: which PR may merge, which one gets its branch updated, and
// why every other one waits. The API calls in run() are a thin shell around these.

const test = require('node:test');
const assert = require('node:assert/strict');

const { updateTypes, checkVerdict, assess, plan } = require('./dependabot-merge.js');

const ACTIONS = 15368;
const REQUIRED = [
  { context: 'backend (Debug)', integration_id: ACTIONS },
  { context: 'e2e / e2e', integration_id: ACTIONS },
];

const run = (
  name,
  conclusion,
  { status = 'completed', appId = ACTIONS, startedAt = '2026-10-14T10:00:00Z' } = {},
) => ({
  name,
  status,
  conclusion,
  app: { id: appId },
  started_at: startedAt,
});

// The shape of Dependabot's commit message: a summary, then the updated-dependencies metadata
// that dependabot/fetch-metadata reads too.
const metadata = (...types) =>
  [
    'Bump the wolverine group with 2 updates',
    '',
    '---',
    'updated-dependencies:',
    ...types.flatMap((type, i) => [
      `- dependency-name: Pkg${i}`,
      '  dependency-version: 1.4.0',
      '  dependency-type: direct:production',
      `  update-type: version-update:semver-${type}`,
    ]),
    '...',
    '',
    'Signed-off-by: dependabot[bot] <support@github.com>',
  ].join('\n');

const MINOR = metadata('minor', 'patch');
const GREEN = [run('backend (Debug)', 'success'), run('e2e / e2e', 'success')];

const pr = (overrides = {}) => ({
  number: 1,
  created_at: '2026-10-13T00:00:00Z',
  draft: false,
  user: { login: 'dependabot[bot]' },
  base: { ref: 'main' },
  head: { sha: 'abc' },
  mergeable: true,
  ...overrides,
});

const ready = (overrides = {}) => ({
  required: REQUIRED,
  checkRuns: GREEN,
  behindBy: 0,
  dependabotMessage: MINOR,
  ...overrides,
});

test("updateTypes reads every update-type from Dependabot's commit metadata", () => {
  assert.deepEqual(updateTypes(metadata('minor', 'patch', 'major')), ['minor', 'patch', 'major']);
});

test('updateTypes is empty for a message without Dependabot metadata', () => {
  assert.deepEqual(updateTypes('build: regenerate the Wolverine adapters'), []);
  assert.deepEqual(updateTypes(undefined), []);
});

test('checkVerdict passes only when every required check succeeded', () => {
  assert.deepEqual(checkVerdict(REQUIRED, GREEN), {
    ok: true,
    missing: [],
    pending: [],
    failing: [],
  });
});

test('checkVerdict names a missing, a pending and a failing check', () => {
  const verdict = checkVerdict(
    [...REQUIRED, { context: 'frontend', integration_id: ACTIONS }],
    [run('backend (Debug)', null, { status: 'in_progress' }), run('e2e / e2e', 'failure')],
  );

  assert.deepEqual(verdict, {
    ok: false,
    missing: ['frontend'],
    pending: ['backend (Debug)'],
    failing: ['e2e / e2e'],
  });
});

test('checkVerdict treats skipped and neutral as not passed', () => {
  const verdict = checkVerdict(REQUIRED, [
    run('backend (Debug)', 'skipped'),
    run('e2e / e2e', 'neutral'),
  ]);

  assert.deepEqual(verdict.failing, ['backend (Debug)', 'e2e / e2e']);
});

test('checkVerdict ignores a same-named check from another app', () => {
  const verdict = checkVerdict(REQUIRED, [
    run('backend (Debug)', 'success', { appId: 999 }),
    run('e2e / e2e', 'success'),
  ]);

  assert.deepEqual(verdict.missing, ['backend (Debug)']);
});

test('checkVerdict uses the latest run of a re-run check', () => {
  const verdict = checkVerdict(REQUIRED, [
    run('backend (Debug)', 'failure', { startedAt: '2026-10-14T09:00:00Z' }),
    run('backend (Debug)', 'success', { startedAt: '2026-10-14T11:00:00Z' }),
    run('e2e / e2e', 'success'),
  ]);

  assert.equal(verdict.ok, true);
});

test('checkVerdict refuses when the ruleset requires no checks at all', () => {
  assert.equal(checkVerdict([], GREEN).ok, false);
});

test('assess merges a green, up-to-date, mergeable minor update', () => {
  assert.deepEqual(assess(pr(), ready()), {
    action: 'merge',
    reason: 'all required checks passed on an up-to-date branch',
  });
});

test('assess updates a green branch that is behind main', () => {
  assert.equal(assess(pr(), ready({ behindBy: 3 })).action, 'update');
});

test('assess waits while a required check is still running', () => {
  const result = assess(
    pr(),
    ready({ checkRuns: [run('backend (Debug)', null, { status: 'queued' }), GREEN[1]] }),
  );

  assert.equal(result.action, 'wait');
  assert.match(result.reason, /backend \(Debug\)/);
});

test('assess hands a failing PR to a human', () => {
  const result = assess(
    pr(),
    ready({ checkRuns: [run('backend (Debug)', 'success'), run('e2e / e2e', 'failure')] }),
  );

  assert.equal(result.action, 'human');
  assert.match(result.reason, /e2e \/ e2e/);
});

test('assess hands a major update to a human even when green', () => {
  const result = assess(pr(), ready({ dependabotMessage: metadata('minor', 'major') }));

  assert.equal(result.action, 'human');
  assert.match(result.reason, /major/);
});

test('assess hands a PR without Dependabot metadata to a human', () => {
  assert.equal(assess(pr(), ready({ dependabotMessage: 'no metadata here' })).action, 'human');
});

test('assess waits while GitHub is still computing mergeability', () => {
  assert.equal(assess(pr({ mergeable: null }), ready()).action, 'wait');
});

test('assess hands a conflicting PR to a human', () => {
  assert.equal(assess(pr({ mergeable: false }), ready()).action, 'human');
});

test("assess skips PRs that are not Dependabot's, drafts, and PRs against other branches", () => {
  assert.equal(assess(pr({ user: { login: 'Surentz' } }), ready()).action, 'skip');
  assert.equal(assess(pr({ draft: true }), ready()).action, 'skip');
  assert.equal(assess(pr({ base: { ref: 'release' } }), ready()).action, 'skip');
});

test('plan merges only the oldest mergeable PR and updates the oldest that is behind', () => {
  const assessed = [
    { pr: pr({ number: 3, created_at: '2026-10-14T00:00:00Z' }), result: { action: 'merge' } },
    { pr: pr({ number: 2, created_at: '2026-10-13T00:00:00Z' }), result: { action: 'merge' } },
    { pr: pr({ number: 5, created_at: '2026-10-15T00:00:00Z' }), result: { action: 'update' } },
    { pr: pr({ number: 4, created_at: '2026-10-12T00:00:00Z' }), result: { action: 'update' } },
  ];

  const { merge, update } = plan(assessed);

  assert.equal(merge.number, 2);
  // Merging #2 puts #3 behind main, so #3 is as much a candidate as #4: the oldest of all of them goes next.
  assert.equal(update.number, 4);
});

test('plan updates the next-oldest mergeable PR when nothing else is behind', () => {
  const assessed = [
    { pr: pr({ number: 2, created_at: '2026-10-13T00:00:00Z' }), result: { action: 'merge' } },
    { pr: pr({ number: 3, created_at: '2026-10-14T00:00:00Z' }), result: { action: 'merge' } },
  ];

  const { merge, update } = plan(assessed);

  assert.equal(merge.number, 2);
  assert.equal(update.number, 3);
});

test('plan does nothing when no PR is ready', () => {
  const { merge, update } = plan([{ pr: pr(), result: { action: 'wait' } }]);

  assert.equal(merge, null);
  assert.equal(update, null);
});
