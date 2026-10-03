// The PR comment that ci.yml's test-report job posts: every test suite's results and both coverage
// numbers, built from the artifacts the run's jobs uploaded. Kept apart from the workflow so it can
// be tested (`node --test ".github/scripts/*.test.js"`) and run locally against a downloaded run:
// `gh run download <run-id> --dir runs/x`, then
// `node .github/scripts/pr-report.js runs/x [baseline-artifacts-dir]`.
//
// Test names and messages come from whatever the tests printed, so anything that reaches the
// comment goes through escapeCell: it can neither break the markdown nor @-mention anyone.

const fs = require('node:fs');
const path = require('node:path');

// The sticky comment is found by this, so it must never change once comments carry it.
const MARKER = '<!-- pr-test-report -->';

const SUITES = ['Backend (Debug)', 'Backend (Release)', 'Frontend (Vitest)', 'E2E (Playwright)'];
const MAX_FAILURES = 50;
const MAX_CELL = 200;
// GitHub rejects a comment over 65,536 characters; leave room for the truncation note.
const MAX_BODY = 60_000;
// After an @, it stops GitHub reading a test name such as "@octocat" as a mention.
const ZERO_WIDTH_SPACE = String.fromCharCode(0x200b);

// --- Parsing ------------------------------------------------------------------------------------

function xmlUnescape(text) {
  return text
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'")
    .replace(/&#(\d+);/g, (_, code) => String.fromCodePoint(Number(code)))
    .replace(/&amp;/g, '&');
}

function attribute(attributes, name) {
  const match = new RegExp(`\\b${name}="([^"]*)"`).exec(attributes);
  return match ? xmlUnescape(match[1]) : undefined;
}

function firstLine(text) {
  return (
    (text ?? '')
      .split(/\r?\n/)
      .map((line) => line.trim())
      .find((line) => line) ?? ''
  );
}

// VSTest's TRX. The counts come from ResultSummary/Counters rather than from counting results, so
// they match what `dotnet test` printed. A regex is enough: TRX is machine-written, flat, and its
// attribute values are always escaped.
function parseTrx(xml) {
  const counters = /<Counters\b([^>]*)\/?>/.exec(xml)?.[1] ?? '';
  const count = (name) => Number(attribute(counters, name) ?? 0);
  const times = /<Times\b([^>]*)\/?>/.exec(xml)?.[1] ?? '';

  const failures = [];
  const results = /<UnitTestResult\b([^>]*?)(?:\/>|>([\s\S]*?)<\/UnitTestResult>)/g;
  for (const [, attributes, body] of xml.matchAll(results)) {
    if (!['Failed', 'Error', 'Timeout', 'Aborted'].includes(attribute(attributes, 'outcome'))) {
      continue;
    }
    const message = /<Message>([\s\S]*?)<\/Message>/.exec(body ?? '')?.[1] ?? '';
    failures.push({
      name: attribute(attributes, 'testName') ?? '',
      message: firstLine(xmlUnescape(message)),
    });
  }

  return {
    passed: count('passed'),
    failed: count('failed') + count('error') + count('timeout') + count('aborted'),
    skipped: count('notExecuted'),
    startMs: Date.parse(attribute(times, 'start')),
    finishMs: Date.parse(attribute(times, 'finish')),
    failures,
  };
}

// One leg runs five test projects in parallel, each writing its own TRX. Their durations overlap,
// so the leg's time is the span from the first start to the last finish, not the sum.
function combine(results) {
  if (results.length === 0) {
    return null;
  }
  return {
    passed: results.reduce((sum, r) => sum + r.passed, 0),
    failed: results.reduce((sum, r) => sum + r.failed, 0),
    skipped: results.reduce((sum, r) => sum + r.skipped, 0),
    flaky: 0,
    durationMs:
      Math.max(...results.map((r) => r.finishMs)) - Math.min(...results.map((r) => r.startMs)),
    failures: results.flatMap((r) => r.failures),
    flakyTests: [],
  };
}

function relativeToFrontend(file) {
  const normalized = file.replace(/\\/g, '/');
  const index = normalized.lastIndexOf('/frontend/');
  return index === -1 ? normalized : normalized.slice(index + '/frontend/'.length);
}

// Vitest's `json` reporter. A file that fails to import has no failed assertions, so it is only
// visible as a failed file with a message, and is reported as a failure of its own.
function parseVitest(report) {
  const failures = [];
  const unloaded = [];
  for (const file of report.testResults) {
    const failedAssertions = file.assertionResults.filter((a) => a.status === 'failed');
    for (const assertion of failedAssertions) {
      failures.push({
        name: assertion.fullName,
        message: firstLine(assertion.failureMessages.join('\n')),
      });
    }
    if (file.status === 'failed' && failedAssertions.length === 0) {
      unloaded.push({ name: relativeToFrontend(file.name), message: firstLine(file.message) });
    }
  }

  const finished = report.testResults.map((f) => f.endTime).filter(Number.isFinite);
  return {
    passed: report.numPassedTests,
    failed: report.numFailedTests + unloaded.length,
    skipped: report.numPendingTests + report.numTodoTests,
    flaky: 0,
    durationMs: finished.length ? Math.max(...finished) - report.startTime : null,
    failures: [...failures, ...unloaded],
    flakyTests: [],
  };
}

// Playwright's `json` reporter. `stats` already distinguishes a test that failed every attempt
// (unexpected) from one that passed on retry (flaky); the suites are walked only for names.
function parsePlaywright(report) {
  const failures = [];
  const flakyTests = [];

  const walk = (suite, titles) => {
    const here = [...titles, suite.title].filter(Boolean);
    for (const spec of suite.specs ?? []) {
      const name = [...here, spec.title].join(' › ');
      for (const t of spec.tests) {
        if (t.status === 'unexpected') {
          const last = [...t.results].reverse().find((r) => r.error);
          failures.push({ name, message: firstLine(last?.error?.message) });
        } else if (t.status === 'flaky') {
          flakyTests.push(name);
        }
      }
    }
    for (const child of suite.suites ?? []) {
      walk(child, here);
    }
  };
  for (const suite of report.suites) {
    walk(suite, []);
  }

  // A run-level error (global setup failing, a spec that cannot load) fails the run without
  // failing any test, so it is counted here or the comment would say everything passed.
  const runErrors = (report.errors ?? []).map((e) => ({
    name: 'Run error',
    message: firstLine(e.message),
  }));

  return {
    passed: report.stats.expected,
    failed: report.stats.unexpected + runErrors.length,
    skipped: report.stats.skipped,
    flaky: report.stats.flaky,
    durationMs: report.stats.duration,
    failures: [...failures, ...runErrors],
    flakyTests,
  };
}

// ReportGenerator's JsonSummary, merged from every test project's Cobertura file.
function backendCoverage(summary) {
  return {
    lines: summary.summary.linecoverage,
    branches: summary.summary.branchcoverage ?? null,
    methods: summary.summary.methodcoverage ?? null,
    coveredLines: summary.summary.coveredlines,
    coverableLines: summary.summary.coverablelines,
    parts: summary.coverage.assemblies
      .map((a) => ({
        name: a.name,
        lines: a.coverage,
        branches: a.branchcoverage ?? null,
        methods: a.methodcoverage ?? null,
      }))
      .sort((a, b) => a.name.localeCompare(b.name)),
  };
}

// Vitest's (istanbul's) json-summary. Istanbul's "functions" is the nearest thing to a method.
function frontendCoverage(summary) {
  const { lines, branches, functions } = summary.total;
  return {
    lines: lines.pct,
    branches: branches.pct,
    methods: functions.pct,
    coveredLines: lines.covered,
    coverableLines: lines.total,
    parts: [],
  };
}

// --- Rendering ----------------------------------------------------------------------------------

// Test names and messages come from the PR. In a table cell they must not be able to end the row,
// open HTML, ping someone, or start a code span that swallows the rest of the comment.
function escapeCell(value) {
  const flat = String(value ?? '').replace(/\r?\n/g, ' ');
  const cut = flat.length > MAX_CELL ? `${flat.slice(0, MAX_CELL)}…` : flat;
  return cut
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/\|/g, '\\|')
    .replace(/`/g, '\\`')
    .replace(/@/g, `@${ZERO_WIDTH_SPACE}`);
}

function duration(ms) {
  if (ms === null || ms === undefined || !Number.isFinite(ms)) {
    return '–';
  }
  const seconds = Math.round(ms / 1000);
  if (seconds < 60) {
    return `${seconds}s`;
  }
  return `${Math.floor(seconds / 60)}m ${String(seconds % 60).padStart(2, '0')}s`;
}

function percent(value) {
  return value === null || value === undefined ? '–' : `${value.toFixed(1)}%`;
}

function count(value) {
  return value.toLocaleString('en-US');
}

function delta(current, baseline) {
  const change = Math.round((current - baseline) * 10) / 10;
  if (change === 0) {
    return '±0.0';
  }
  return change > 0 ? `+${change.toFixed(1)}` : `−${Math.abs(change).toFixed(1)}`;
}

// A flaky test failed and then passed on retry, so it counts as passed - but the Result cell says
// so, because a test that needs a retry is a test that will one day need two.
function resultCell(r) {
  if (r.failed > 0) {
    return '❌';
  }
  return r.flaky > 0 ? `✅ ${r.flaky} flaky` : '✅';
}

function coverageLine(label, c, base) {
  if (!c) {
    return `**${label}** — not measured`;
  }
  const vsMain = base ? `, ${delta(c.lines, base.lines)} vs main` : '';
  return (
    `**${label}** — **Line ${percent(c.lines)}** ` +
    `(${count(c.coveredLines)} of ${count(c.coverableLines)} lines${vsMain}) · ` +
    `**Branch ${percent(c.branches)}** · **Method ${percent(c.methods)}**`
  );
}

function render({ suites, coverage, baseline, meta }) {
  const results = SUITES.map((name) => suites[name] ?? null);
  const lines = [
    MARKER,
    '## Test results',
    '',
    '| Suite | Result | Passed | Failed | Skipped | Total | Duration |',
    '|---|---|--:|--:|--:|--:|--:|',
  ];
  SUITES.forEach((name, i) => {
    const r = results[i];
    if (r === null) {
      lines.push(`| ${name} | ⏭️ not run | | | | | |`);
      return;
    }
    const passed = r.passed + r.flaky;
    const total = passed + r.failed + r.skipped;
    lines.push(
      `| ${name} | ${resultCell(r)} | ${count(passed)} | ${count(r.failed)} | ${count(r.skipped)} | ${count(total)} | ${duration(r.durationMs)} |`,
    );
  });

  const failures = SUITES.flatMap((name, i) =>
    (results[i]?.failures ?? []).map((f) => ({ suite: name, ...f })),
  );
  if (failures.length > 0) {
    const total = results.reduce((sum, r) => sum + (r?.failed ?? 0), 0);
    lines.push(
      '',
      `<details><summary>Failed tests (${total})</summary>`,
      '',
      '| Suite | Test | Message |',
      '|---|---|---|',
    );
    for (const f of failures.slice(0, MAX_FAILURES)) {
      lines.push(`| ${f.suite} | ${escapeCell(f.name)} | ${escapeCell(f.message)} |`);
    }
    if (failures.length > MAX_FAILURES) {
      lines.push('', `…and ${failures.length - MAX_FAILURES} more — see the run.`);
    }
    lines.push('', '</details>');
  }

  const flaky = SUITES.flatMap((name, i) =>
    (results[i]?.flakyTests ?? []).map((t) => ({ suite: name, name: t })),
  );
  if (flaky.length > 0) {
    lines.push('', `<details><summary>Passed only on retry (${flaky.length})</summary>`, '');
    for (const f of flaky.slice(0, MAX_FAILURES)) {
      lines.push(`- ${f.suite}: ${escapeCell(f.name)}`);
    }
    lines.push('', '</details>');
  }

  // Two paragraphs rather than one table: each side is one line of headline numbers.
  lines.push(
    '',
    '### Coverage',
    '',
    coverageLine('Backend', coverage.backend, baseline?.backend),
    '',
    coverageLine('Frontend', coverage.frontend, baseline?.frontend),
  );

  const parts = coverage.backend?.parts ?? [];
  if (parts.length > 0) {
    lines.push(
      '',
      '<details><summary>Backend coverage per assembly (unit + integration merged)</summary>',
      '',
      '| Assembly | Line | Branch | Method |',
      '|---|--:|--:|--:|',
    );
    for (const p of parts) {
      lines.push(
        `| ${escapeCell(p.name)} | ${percent(p.lines)} | ${percent(p.branches)} | ${percent(p.methods)} |`,
      );
    }
    lines.push('', '</details>');
  }

  const footer =
    `<sub>Commit \`${meta.sha.slice(0, 7)}\` · [workflow run](${meta.runUrl}) · ` +
    'TRX, JSON and Cobertura files are attached to the run as artifacts.</sub>';
  const body = lines.join('\n');
  // GitHub rejects an over-long comment; cut the body but always keep the footer's run link.
  const trimmed =
    body.length > MAX_BODY
      ? `${body.slice(0, MAX_BODY)}\n\n…truncated — see the run for the rest.`
      : body;
  return `${trimmed}\n\n${footer}`;
}

// --- Reading a downloaded run -------------------------------------------------------------------

function filesUnder(dir, suffix) {
  if (!fs.existsSync(dir)) {
    return [];
  }
  return fs
    .readdirSync(dir, { recursive: true })
    .map((rel) => path.join(dir, String(rel)))
    .filter((file) => file.endsWith(suffix) && fs.statSync(file).isFile());
}

function readJson(file) {
  return fs.existsSync(file) ? JSON.parse(fs.readFileSync(file, 'utf8')) : null;
}

// The artifact names are ci.yml's and e2e.yml's; download-artifact puts each in a folder of its
// own name. A missing artifact means that job did not run (or did not get that far): not run.
function collect(dir) {
  const trx = (leg) =>
    combine(
      filesUnder(path.join(dir, `test-results-backend-${leg}`), '.trx').map((f) =>
        parseTrx(fs.readFileSync(f, 'utf8')),
      ),
    );
  const vitest = readJson(path.join(dir, 'test-results-frontend', 'vitest.json'));
  const playwright = readJson(path.join(dir, 'test-results-e2e', 'e2e-results.json'));
  const backend = readJson(path.join(dir, 'coverage-backend', 'Summary.json'));
  const frontend = readJson(path.join(dir, 'coverage-frontend', 'coverage-summary.json'));

  return {
    suites: {
      'Backend (Debug)': trx('Debug'),
      'Backend (Release)': trx('Release'),
      'Frontend (Vitest)': vitest && parseVitest(vitest),
      'E2E (Playwright)': playwright && parsePlaywright(playwright),
    },
    coverage: {
      backend: backend && backendCoverage(backend),
      frontend: frontend && frontendCoverage(frontend),
    },
  };
}

module.exports = {
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
};

if (require.main === module) {
  const [current, baselineDir] = process.argv.slice(2);
  if (!current) {
    console.error(
      'usage: node .github/scripts/pr-report.js <artifacts-dir> [baseline-artifacts-dir]',
    );
    process.exit(2);
  }
  const { suites, coverage } = collect(current);
  const baseline = baselineDir ? collect(baselineDir).coverage : null;
  process.stdout.write(
    render({
      suites,
      coverage,
      baseline,
      meta: {
        sha: process.env.HEAD_SHA ?? 'unknown',
        runUrl: process.env.RUN_URL ?? '',
      },
    }),
  );
}
