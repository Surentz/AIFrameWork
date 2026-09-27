// The rules behind .github/workflows/pr-labels.yml: which labels a pull request gets from its
// title (its type) and from the files it changes (its areas). Kept apart from the workflow so the
// same code can be run locally against old PRs - `node .github/scripts/pr-labels.js` prints the
// label definitions, and the exported functions are what the workflow calls.
//
// Changing a label here changes it everywhere: the workflow creates or recolours every label in
// LABELS on its next run, and removes only labels it manages (never one someone added by hand).

// Conventional Commit types this repo uses, and the label each one maps to. `!` after the type or
// scope (`feat(api)!: ...`) additionally adds `breaking`.
const TYPES = {
  feat: 'feature',
  fix: 'bug',
  perf: 'performance',
  refactor: 'refactor',
  test: 'tests',
  docs: 'docs',
  ci: 'ci',
  build: 'build',
  deps: 'dependencies',
  chore: 'chore',
  revert: 'revert',
};

// Areas, by path. A PR gets every area any of its changed files falls in. Tested in order, but
// order does not matter: a file can put a PR in several areas (a worker migration is `backend`,
// `worker` and `database`).
const AREAS = [
  { label: 'playwright', test: (f) => f.startsWith('frontend/e2e/') || f === 'frontend/playwright.config.ts' || f === '.github/workflows/e2e.yml' },
  { label: 'frontend', test: (f) => f.startsWith('frontend/') && !f.startsWith('frontend/e2e/') && f !== 'frontend/playwright.config.ts' && !f.endsWith('.md') },
  { label: 'backend', test: (f) => (f.startsWith('src/') || f.startsWith('tests/') || f === 'Directory.Build.props' || f.endsWith('.slnx') || f === '.editorconfig' || f === '.config/dotnet-tools.json') && !f.endsWith('.md') },
  { label: 'worker', test: (f) => (f.startsWith('src/Worker/') || f.startsWith('tests/Worker.IntegrationTests/')) && !f.endsWith('.md') },
  { label: 'database', test: (f) => f.startsWith('src/Infrastructure/Persistence/Migrations/') },
  { label: 'api-contract', test: (f) => f.startsWith('openapi/') || f === 'frontend/src/api/schema.d.ts' },
  { label: 'kubernetes', test: (f) => f.startsWith('k8s/') || f.startsWith('deploy/') },
  { label: 'ci', test: (f) => f.startsWith('.github/') },
  { label: 'docs', test: (f) => f.startsWith('docs/') || f.endsWith('.md') },
];

// Type labels are strong colours, area labels one muted colour, so the two read apart in the PR
// list. `docs` and `ci` are both a type and an area: one label, the type's colour.
const AREA_COLOUR = 'c5def5';
const LABELS = {
  feature: { color: '0e8a16', description: 'Type: a new feature (feat)' },
  bug: { color: 'd73a4a', description: 'Type: a bug fix (fix)' },
  performance: { color: 'fbca04', description: 'Type: a performance improvement (perf)' },
  refactor: { color: '5319e7', description: 'Type: restructuring with no behaviour change (refactor)' },
  tests: { color: '1d76db', description: 'Type: tests only (test)' },
  docs: { color: '0075ca', description: 'Type or area: documentation (docs)' },
  ci: { color: '6e7781', description: 'Type or area: CI and GitHub configuration (ci)' },
  build: { color: 'fef2c0', description: 'Type: build system or tooling (build)' },
  dependencies: { color: '0366d6', description: 'Type: dependency updates (deps)' },
  chore: { color: 'bfbfbf', description: 'Type: maintenance with no product change (chore)' },
  revert: { color: 'b60205', description: 'Type: reverts an earlier change (revert)' },
  breaking: { color: 'b60205', description: 'A breaking change (! in the title)' },
  playwright: { color: AREA_COLOUR, description: 'Area: Playwright e2e suite' },
  frontend: { color: AREA_COLOUR, description: 'Area: React frontend' },
  backend: { color: AREA_COLOUR, description: 'Area: .NET backend and its tests' },
  worker: { color: AREA_COLOUR, description: 'Area: the job worker host' },
  database: { color: AREA_COLOUR, description: 'Area: EF Core migrations - review with care' },
  'api-contract': { color: AREA_COLOUR, description: 'Area: the committed OpenAPI contract - review with care' },
  kubernetes: { color: AREA_COLOUR, description: 'Area: k8s manifests and deploy scripts' },
};

const TITLE = /^(?<type>[a-z]+)(?:\((?<scope>[^()]+)\))?(?<breaking>!)?: \S/;
// GitHub's own "Revert" button titles the PR `Revert "<original title>"`.
const GITHUB_REVERT = /^Revert "/;

// Returns { labels } for a valid title, or { error } saying what is wrong with it.
function typeLabels(title) {
  if (GITHUB_REVERT.test(title)) return { labels: ['revert'] };
  const match = TITLE.exec(title);
  if (!match) {
    return {
      error:
        `The PR title must start with a Conventional Commit type, e.g. "feat(orders): add paging". ` +
        `Allowed types: ${Object.keys(TYPES).join(', ')}. Add ! before the colon for a breaking change.`,
    };
  }
  const label = TYPES[match.groups.type];
  if (!label) {
    return { error: `"${match.groups.type}" is not an allowed type. Allowed: ${Object.keys(TYPES).join(', ')}.` };
  }
  return { labels: match.groups.breaking ? [label, 'breaking'] : [label] };
}

function areaLabels(files) {
  return AREAS.filter((area) => files.some((f) => area.test(f))).map((area) => area.label);
}

module.exports = { TYPES, AREAS, LABELS, typeLabels, areaLabels };

if (require.main === module) {
  console.log(JSON.stringify(LABELS, null, 2));
}
