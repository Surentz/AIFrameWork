// Tests for pr-report.js. Run with `node --test ".github/scripts/*.test.js"`; CI's frontend job
// runs them.
//
// The fixtures are trimmed copies of what each tool really writes (VSTest's TRX, Vitest's and
// Playwright's JSON reporters, ReportGenerator's JsonSummary, Vitest's json-summary), so a tool
// changing its format shows up here rather than as a blank PR comment.

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const {
  MARKER,
  parseTrx,
  combine,
  parseVitest,
  parsePlaywright,
  backendCoverage,
  frontendCoverage,
  escapeCell,
  render,
  collect,
} = require('./pr-report.js');

function trx({ passed = 0, failed = 0, error = 0, notExecuted = 0, start, finish, results = '' }) {
  const total = passed + failed + error + notExecuted;
  return `﻿<?xml version="1.0" encoding="utf-8"?>
<TestRun id="1" name="run" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Times creation="${start}" queuing="${start}" start="${start}" finish="${finish}" />
  <Results>${results}</Results>
  <ResultSummary outcome="${failed + error ? 'Failed' : 'Completed'}">
    <Counters total="${total}" executed="${total - notExecuted}" passed="${passed}" failed="${failed}" error="${error}" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="${notExecuted}" disconnected="0" warning="0" completed="0" inProgress="0" pending="0" />
  </ResultSummary>
</TestRun>`;
}

const failedResult = `
    <UnitTestResult executionId="a" testId="b" testName="AiFramework.Domain.Tests.OrderTests.Cancel_WhenShipped(reason: &quot;x &lt; y&quot;)" outcome="Failed" duration="00:00:00.01">
      <Output>
        <ErrorInfo>
          <Message>Expected order.Status to be Cancelled,
but found Shipped.</Message>
          <StackTrace>at OrderTests.cs:line 12</StackTrace>
        </ErrorInfo>
      </Output>
    </UnitTestResult>
    <UnitTestResult executionId="c" testId="d" testName="AiFramework.Domain.Tests.OrderTests.Place_Works" outcome="Passed" duration="00:00:00.01" />`;

test('parseTrx counts passed, failed and skipped from the summary counters', () => {
  const result = parseTrx(
    trx({
      passed: 10,
      failed: 2,
      error: 1,
      notExecuted: 3,
      start: '2026-10-03T20:44:08.000+02:00',
      finish: '2026-10-03T20:44:10.500+02:00',
    }),
  );

  assert.deepEqual(
    { passed: result.passed, failed: result.failed, skipped: result.skipped },
    { passed: 10, failed: 3, skipped: 3 },
  );
});

test('parseTrx measures the run from start to finish', () => {
  const result = parseTrx(
    trx({
      passed: 1,
      start: '2026-10-03T20:44:08.000+02:00',
      finish: '2026-10-03T20:44:10.500+02:00',
    }),
  );

  assert.equal(result.finishMs - result.startMs, 2500);
});

test('parseTrx names each failed test with the first line of its message, unescaped', () => {
  const result = parseTrx(
    trx({
      passed: 1,
      failed: 1,
      start: '2026-10-03T20:44:08Z',
      finish: '2026-10-03T20:44:09Z',
      results: failedResult,
    }),
  );

  assert.deepEqual(result.failures, [
    {
      name: 'AiFramework.Domain.Tests.OrderTests.Cancel_WhenShipped(reason: "x < y")',
      message: 'Expected order.Status to be Cancelled,',
    },
  ]);
});

test('combine adds the counts and spans the earliest start to the latest finish', () => {
  const a = parseTrx(
    trx({ passed: 5, start: '2026-10-03T20:00:00Z', finish: '2026-10-03T20:00:30Z' }),
  );
  const b = parseTrx(
    trx({
      passed: 2,
      failed: 1,
      start: '2026-10-03T20:00:10Z',
      finish: '2026-10-03T20:01:00Z',
      results: failedResult,
    }),
  );

  const result = combine([a, b]);

  assert.deepEqual(
    {
      passed: result.passed,
      failed: result.failed,
      skipped: result.skipped,
      durationMs: result.durationMs,
      failures: result.failures.length,
    },
    { passed: 7, failed: 1, skipped: 0, durationMs: 60_000, failures: 1 },
  );
});

test('combine of nothing is null, so a suite with no result files reads as not run', () => {
  assert.equal(combine([]), null);
});

const vitestReport = {
  numTotalTests: 6,
  numPassedTests: 3,
  numFailedTests: 1,
  numPendingTests: 1,
  numTodoTests: 1,
  startTime: 1_791_053_087_000,
  success: false,
  testResults: [
    {
      name: '/repo/frontend/src/routes.test.tsx',
      status: 'failed',
      message: '',
      startTime: 1_791_053_087_100,
      endTime: 1_791_053_090_000,
      assertionResults: [
        {
          fullName: 'AppRoutes renders the orders screens',
          status: 'failed',
          failureMessages: ['Error: Unable to find role="link"\n\nIgnored nodes: comments'],
        },
        { fullName: 'AppRoutes redirects', status: 'passed', failureMessages: [] },
      ],
    },
    {
      name: '/repo/frontend/src/broken.test.tsx',
      status: 'failed',
      message: 'Failed to load url ./missing (resolved id: ./missing)\n  at foo',
      startTime: 1_791_053_087_100,
      endTime: 1_791_053_088_000,
      assertionResults: [],
    },
  ],
};

test('parseVitest counts pending and todo tests as skipped', () => {
  const result = parseVitest(vitestReport);

  assert.deepEqual(
    { passed: result.passed, failed: result.failed, skipped: result.skipped },
    { passed: 3, failed: 2, skipped: 2 },
  );
});

test('parseVitest reports a file that failed to load as a failure of its own', () => {
  const result = parseVitest(vitestReport);

  assert.deepEqual(result.failures, [
    { name: 'AppRoutes renders the orders screens', message: 'Error: Unable to find role="link"' },
    {
      name: 'src/broken.test.tsx',
      message: 'Failed to load url ./missing (resolved id: ./missing)',
    },
  ]);
});

test('parseVitest measures from the run start to the last file to finish', () => {
  assert.equal(parseVitest(vitestReport).durationMs, 3000);
});

const playwrightReport = {
  stats: {
    startTime: '2026-10-03T20:00:00.000Z',
    duration: 177_000,
    expected: 54,
    unexpected: 1,
    flaky: 1,
    skipped: 2,
  },
  errors: [],
  suites: [
    {
      title: 'orders/fulfilment.spec.ts',
      file: 'orders/fulfilment.spec.ts',
      specs: [],
      suites: [
        {
          title: 'fulfilment',
          file: 'orders/fulfilment.spec.ts',
          specs: [
            {
              title: 'the operator ships from the queue',
              file: 'orders/fulfilment.spec.ts',
              ok: false,
              tests: [
                {
                  status: 'unexpected',
                  results: [
                    {
                      retry: 0,
                      error: {
                        message: 'Error: expect(locator).toBeVisible() failed\n\nLocator: ...',
                      },
                    },
                    { retry: 1, error: { message: 'Error: Timed out 10000ms\nCall log:' } },
                  ],
                },
              ],
            },
            {
              title: 'a member is refused',
              file: 'orders/fulfilment.spec.ts',
              ok: true,
              tests: [
                {
                  status: 'flaky',
                  results: [{ retry: 0, error: { message: 'boom' } }, { retry: 1 }],
                },
              ],
            },
            {
              title: 'the queue pages',
              file: 'orders/fulfilment.spec.ts',
              ok: true,
              tests: [{ status: 'expected', results: [{ retry: 0 }] }],
            },
          ],
        },
      ],
    },
  ],
};

test('parsePlaywright takes its counts from the stats, flaky included', () => {
  const result = parsePlaywright(playwrightReport);

  assert.deepEqual(
    {
      passed: result.passed,
      failed: result.failed,
      skipped: result.skipped,
      flaky: result.flaky,
      durationMs: result.durationMs,
    },
    { passed: 54, failed: 1, skipped: 2, flaky: 1, durationMs: 177_000 },
  );
});

test("parsePlaywright names failed tests by file and title, with the last attempt's error", () => {
  assert.deepEqual(parsePlaywright(playwrightReport).failures, [
    {
      name: 'orders/fulfilment.spec.ts › fulfilment › the operator ships from the queue',
      message: 'Error: Timed out 10000ms',
    },
  ]);
});

test('parsePlaywright lists the tests that passed only on retry', () => {
  assert.deepEqual(parsePlaywright(playwrightReport).flakyTests, [
    'orders/fulfilment.spec.ts › fulfilment › a member is refused',
  ]);
});

test('parsePlaywright counts a run-level error, such as a failed global setup, as a failure', () => {
  const result = parsePlaywright({
    stats: {
      startTime: '2026-10-03T20:00:00.000Z',
      duration: 5000,
      expected: 0,
      unexpected: 0,
      flaky: 0,
      skipped: 0,
    },
    errors: [{ message: 'Error: seed-admin.ts: sign-in refused\n    at seed' }],
    suites: [],
  });

  assert.deepEqual(
    { failed: result.failed, failures: result.failures },
    {
      failed: 1,
      failures: [{ name: 'Run error', message: 'Error: seed-admin.ts: sign-in refused' }],
    },
  );
});

test('backendCoverage reads line, branch and method coverage, total and per assembly', () => {
  const result = backendCoverage({
    summary: {
      linecoverage: 84.1,
      branchcoverage: 71,
      methodcoverage: 90,
      coveredlines: 841,
      coverablelines: 1000,
    },
    coverage: {
      assemblies: [
        { name: 'AiFramework.Domain', coverage: 96.1, branchcoverage: 100, methodcoverage: 93.4 },
        { name: 'AiFramework.Api', coverage: 80.25, branchcoverage: null, methodcoverage: 75 },
      ],
    },
  });

  assert.deepEqual(result, {
    lines: 84.1,
    branches: 71,
    methods: 90,
    coveredLines: 841,
    coverableLines: 1000,
    parts: [
      { name: 'AiFramework.Api', lines: 80.25, branches: null, methods: 75 },
      { name: 'AiFramework.Domain', lines: 96.1, branches: 100, methods: 93.4 },
    ],
  });
});

test('frontendCoverage reads lines, branches and functions as methods', () => {
  const result = frontendCoverage({
    total: {
      lines: { total: 200, covered: 157, skipped: 0, pct: 78.5 },
      branches: { total: 100, covered: 66, skipped: 0, pct: 66.2 },
      functions: { total: 50, covered: 40, skipped: 0, pct: 80 },
    },
  });

  assert.deepEqual(result, {
    lines: 78.5,
    branches: 66.2,
    methods: 80,
    coveredLines: 157,
    coverableLines: 200,
    parts: [],
  });
});

test('escapeCell keeps untrusted test names from becoming markup, mentions or table breaks', () => {
  assert.equal(
    escapeCell('a | b <script> @octocat `x`\nnext'),
    String.raw`a \| b &lt;script&gt; @${String.fromCharCode(0x200b)}octocat \`x\` next`,
  );
});

test('escapeCell truncates a long value', () => {
  const result = escapeCell('x'.repeat(500));

  assert.equal(result.length, 201);
  assert.ok(result.endsWith('…'));
});

const passing = {
  passed: 10,
  failed: 0,
  skipped: 1,
  flaky: 0,
  durationMs: 130_000,
  failures: [],
  flakyTests: [],
};
const meta = { sha: 'abc1234def', runUrl: 'https://github.com/o/r/actions/runs/1' };

function suites(overrides = {}) {
  return {
    'Backend (Debug)': passing,
    'Backend (Release)': passing,
    'Frontend (Vitest)': passing,
    'E2E (Playwright)': passing,
    ...overrides,
  };
}

test('render starts with the marker the workflow finds its own comment by', () => {
  assert.ok(render({ suites: suites(), coverage: {}, baseline: null, meta }).startsWith(MARKER));
});

test('render gives each suite that passed a tick, its counts, total and duration', () => {
  const md = render({ suites: suites(), coverage: {}, baseline: null, meta });

  assert.match(md, /## Test results/);
  assert.match(
    md,
    /\| Suite \| Result \| Passed \| Failed \| Skipped \| Total \| Duration \|\n\|---\|---\|--:\|--:\|--:\|--:\|--:\|/,
  );
  assert.match(md, /\| Backend \(Debug\) \| ✅ \| 10 \| 0 \| 1 \| 11 \| 2m 10s \|/);
});

test('render marks a suite with failures and lists them', () => {
  const failing = {
    ...passing,
    failed: 2,
    failures: [
      { name: 'A.B.C', message: 'boom' },
      { name: 'A.B.D', message: '' },
    ],
  };

  const md = render({
    suites: suites({ 'Frontend (Vitest)': failing }),
    coverage: {},
    baseline: null,
    meta,
  });

  assert.match(md, /\| Frontend \(Vitest\) \| ❌ \| 10 \| 2 \| 1 \| 13 \|/);
  assert.match(md, /<summary>Failed tests \(2\)<\/summary>/);
  assert.match(md, /\| Frontend \(Vitest\) \| A\.B\.C \| boom \|/);
});

test('render counts a test that passed on retry as passed, and says so in the result', () => {
  const md = render({
    suites: suites({ 'E2E (Playwright)': { ...passing, flaky: 1, flakyTests: ['a › b'] } }),
    coverage: {},
    baseline: null,
    meta,
  });

  assert.match(md, /\| E2E \(Playwright\) \| ✅ 1 flaky \| 11 \| 0 \| 1 \| 12 \|/);
  assert.match(md, /<summary>Passed only on retry \(1\)<\/summary>/);
});

test('render shows a suite with no results as not run, never as passed', () => {
  const md = render({
    suites: suites({ 'E2E (Playwright)': null }),
    coverage: {},
    baseline: null,
    meta,
  });

  assert.match(md, /\| E2E \(Playwright\) \| ⏭️ not run \| \| \| \| \| \|/);
});

test('render pads the seconds of a duration over a minute', () => {
  const md = render({
    suites: suites({
      'Backend (Debug)': { ...passing, durationMs: 61_000 },
      'Frontend (Vitest)': { ...passing, durationMs: 2_000 },
    }),
    coverage: {},
    baseline: null,
    meta,
  });

  assert.match(md, /\| Backend \(Debug\) \| ✅ \| 10 \| 0 \| 1 \| 11 \| 1m 01s \|/);
  assert.match(md, /\| Frontend \(Vitest\) \| ✅ \| 10 \| 0 \| 1 \| 11 \| 2s \|/);
});

test('render caps the failed-test list and says how many it left out', () => {
  const many = Array.from({ length: 60 }, (_, i) => ({ name: `T${i}`, message: '' }));
  const md = render({
    suites: suites({ 'Backend (Debug)': { ...passing, failed: 60, failures: many } }),
    coverage: {},
    baseline: null,
    meta,
  });

  assert.match(md, /\| Backend \(Debug\) \| T49 \|/);
  assert.doesNotMatch(md, /\| T50 \|/);
  assert.match(md, /…and 10 more/);
});

const backend = {
  lines: 84.13,
  branches: 71,
  methods: 90,
  coveredLines: 4_772,
  coverableLines: 5_672,
  parts: [{ name: 'AiFramework.Api', lines: 80.26, branches: null, methods: 75 }],
};
const frontend = {
  lines: 78.5,
  branches: 66.2,
  methods: 80,
  coveredLines: 157,
  coverableLines: 200,
  parts: [],
};

test("render summarises each side's coverage on one line, compared against main", () => {
  const md = render({
    suites: suites(),
    coverage: { backend, frontend },
    baseline: { backend: { ...backend, lines: 83.8 }, frontend: { ...frontend, lines: 78.6 } },
    meta,
  });

  assert.match(md, /### Coverage/);
  assert.match(
    md,
    /\*\*Backend\*\* — \*\*Line 84\.1%\*\* \(4,772 of 5,672 lines, \+0\.3 vs main\) · \*\*Branch 71\.0%\*\* · \*\*Method 90\.0%\*\*/,
  );
  assert.match(
    md,
    /\*\*Frontend\*\* — \*\*Line 78\.5%\*\* \(157 of 200 lines, −0\.1 vs main\) · \*\*Branch 66\.2%\*\* · \*\*Method 80\.0%\*\*/,
  );
});

test('render leaves out the comparison when main has no baseline yet', () => {
  const md = render({ suites: suites(), coverage: { backend }, baseline: null, meta });

  assert.match(md, /\*\*Line 84\.1%\*\* \(4,772 of 5,672 lines\) ·/);
  assert.match(md, /\*\*Frontend\*\* — not measured/);
});

test('render breaks backend coverage down per assembly, folded away', () => {
  const md = render({ suites: suites(), coverage: { backend }, baseline: null, meta });

  assert.match(
    md,
    /<details><summary>Backend coverage per assembly \(unit \+ integration merged\)<\/summary>/,
  );
  assert.match(md, /\| Assembly \| Line \| Branch \| Method \|\n\|---\|--:\|--:\|--:\|/);
  assert.match(md, /\| AiFramework\.Api \| 80\.3% \| – \| 75\.0% \|/);
});

test('render ends with the commit and the run, in small print', () => {
  const md = render({ suites: suites(), coverage: {}, baseline: null, meta });

  assert.match(
    md,
    /<sub>Commit `abc1234` · \[workflow run\]\(https:\/\/github\.com\/o\/r\/actions\/runs\/1\) · TRX, JSON and Cobertura files are attached to the run as artifacts\.<\/sub>$/,
  );
});

test('collect reads every artifact it finds and leaves the rest as not run', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pr-report-'));
  const write = (rel, content) => {
    fs.mkdirSync(path.dirname(path.join(dir, rel)), { recursive: true });
    fs.writeFileSync(
      path.join(dir, rel),
      typeof content === 'string' ? content : JSON.stringify(content),
    );
  };
  write(
    'test-results-backend-Debug/a.trx',
    trx({ passed: 3, start: '2026-10-03T20:00:00Z', finish: '2026-10-03T20:00:05Z' }),
  );
  write(
    'test-results-backend-Debug/nested/b[1].trx',
    trx({ passed: 4, start: '2026-10-03T20:00:00Z', finish: '2026-10-03T20:00:09Z' }),
  );
  write('test-results-frontend/vitest.json', vitestReport);
  write('test-results-e2e/e2e-results.json', playwrightReport);
  write('coverage-backend/Summary.json', {
    summary: {
      linecoverage: 80,
      branchcoverage: 60,
      methodcoverage: 70,
      coveredlines: 8,
      coverablelines: 10,
    },
    coverage: { assemblies: [] },
  });

  const result = collect(dir);

  assert.equal(result.suites['Backend (Debug)'].passed, 7);
  assert.equal(result.suites['Backend (Release)'], null);
  assert.equal(result.suites['Frontend (Vitest)'].failed, 2);
  assert.equal(result.suites['E2E (Playwright)'].flaky, 1);
  assert.deepEqual(result.coverage, {
    backend: {
      lines: 80,
      branches: 60,
      methods: 70,
      coveredLines: 8,
      coverableLines: 10,
      parts: [],
    },
    frontend: null,
  });
});
