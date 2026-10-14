// The rules behind .github/workflows/dependabot-merge.yml: which Dependabot PR may merge tonight,
// which one gets its branch brought up to date, and why every other one waits.
//
// Each run does at most two writes: it merges the oldest PR that is green, up to date with main
// and mergeable, and then updates the branch of the oldest remaining green PR so its CI reruns
// against the new main before the next run. One merge per run means every merged PR was tested
// against exactly the main it lands on - `main`'s ruleset requires up-to-date branches, and the
// merge app's bypass is only for the two required reviews, never for that.
//
// Never merged here, always left for a human: anything not opened by Dependabot, a draft, a PR
// against another branch, a semver-major update (as Dependabot classifies it), a PR without
// Dependabot's update metadata, a conflicting PR, and any PR with a required check that failed - which is how the
// WolverineFx and API-contract bumps stay put until someone regenerates and pushes (see
// .github/dependabot.yml).
//
// The required checks are read from the ruleset itself, by name and by the app that must post
// them, so a renamed job cannot be silently skipped and a same-named check from another app
// cannot stand in for it.

const DEPENDABOT = 'dependabot[bot]';
const BASE = 'main';

// Dependabot's commit message ends with the metadata dependabot/fetch-metadata reads:
//
//   updated-dependencies:
//   - dependency-name: WolverineFx
//     update-type: version-update:semver-minor
//
// It is read from the commit, not the PR body, because GitHub truncates a long body (a grouped
// update's release notes) and the later packages' lines are lost with it.
const UPDATE_TYPE = /^\s*update-type:\s*version-update:semver-(\w+)\s*$/gm;

function updateTypes(commitMessage) {
  return [...(commitMessage ?? '').matchAll(UPDATE_TYPE)].map(([, type]) => type);
}

function checkVerdict(required, checkRuns) {
  // An empty list is a broken ruleset read, not "nothing to wait for": never merge on it.
  if (required.length === 0) {
    return {
      ok: false,
      missing: ['(no required checks found on the ruleset)'],
      pending: [],
      failing: [],
    };
  }

  const missing = [];
  const pending = [];
  const failing = [];
  for (const { context, integration_id: appId } of required) {
    const latest = checkRuns
      .filter((r) => r.name === context && (appId == null || r.app?.id === appId))
      .sort((a, b) => (b.started_at ?? '').localeCompare(a.started_at ?? ''))[0];

    if (!latest) missing.push(context);
    else if (latest.status !== 'completed') pending.push(context);
    else if (latest.conclusion !== 'success') failing.push(context);
  }

  return { ok: missing.length + pending.length + failing.length === 0, missing, pending, failing };
}

function assess(pr, { required, checkRuns, behindBy, dependabotMessage }) {
  if (pr.user?.login !== DEPENDABOT) return { action: 'skip', reason: 'not opened by Dependabot' };
  if (pr.draft) return { action: 'skip', reason: 'draft' };
  if (pr.base?.ref !== BASE)
    return { action: 'skip', reason: `targets ${pr.base?.ref}, not ${BASE}` };

  const types = updateTypes(dependabotMessage);
  if (types.length === 0)
    return { action: 'human', reason: "no Dependabot update metadata on the PR's commits" };
  if (types.includes('major')) return { action: 'human', reason: 'contains a major update' };

  const checks = checkVerdict(required, checkRuns);
  if (checks.failing.length)
    return { action: 'human', reason: `failed: ${checks.failing.join(', ')}` };
  if (checks.pending.length)
    return { action: 'wait', reason: `still running: ${checks.pending.join(', ')}` };
  if (checks.missing.length)
    return { action: 'wait', reason: `not reported yet: ${checks.missing.join(', ')}` };

  if (pr.mergeable === false) return { action: 'human', reason: 'has merge conflicts' };
  if (pr.mergeable == null)
    return { action: 'wait', reason: 'GitHub is still computing mergeability' };

  if (behindBy > 0) return { action: 'update', reason: `${behindBy} commit(s) behind ${BASE}` };
  return { action: 'merge', reason: 'all required checks passed on an up-to-date branch' };
}

function plan(assessed) {
  const oldestFirst = [...assessed].sort((a, b) => a.pr.created_at.localeCompare(b.pr.created_at));
  const merge = oldestFirst.find((a) => a.result.action === 'merge')?.pr ?? null;
  // Once `merge` lands, every other green PR is behind main, so they all compete for the update.
  const update =
    oldestFirst.find(
      (a) => a.pr !== merge && (a.result.action === 'update' || a.result.action === 'merge'),
    )?.pr ?? null;
  return { merge, update };
}

// How the summary reads for a PR this run did not act on.
const WAITING = {
  merge: 'ready - a later run merges it',
  update: 'green but behind main - a later run updates it',
  wait: 'waiting',
  human: 'needs a human',
  skip: 'not handled here',
};

async function run({ github, context, core, dryRun }) {
  const { owner, repo } = context.repo;

  const rules = await github.request('GET /repos/{owner}/{repo}/rules/branches/{branch}', {
    owner,
    repo,
    branch: BASE,
  });
  const required = rules.data
    .filter((r) => r.type === 'required_status_checks')
    .flatMap((r) => r.parameters.required_status_checks);

  const open = await github.paginate(github.rest.pulls.list, {
    owner,
    repo,
    state: 'open',
    base: BASE,
    per_page: 100,
  });

  const assessed = [];
  for (const listed of open.filter((p) => p.user?.login === DEPENDABOT)) {
    // The list omits mergeability; the single-PR read computes it.
    const { data: pr } = await github.rest.pulls.get({ owner, repo, pull_number: listed.number });
    const checkRuns = await github.paginate(github.rest.checks.listForRef, {
      owner,
      repo,
      ref: pr.head.sha,
      per_page: 100,
    });
    const { data: compare } = await github.rest.repos.compareCommitsWithBasehead({
      owner,
      repo,
      basehead: `${BASE}...${pr.head.sha}`,
    });
    // Dependabot's own commit: the first one it authored. Others on the branch (a regenerated
    // adapter tree, a merge from main) carry no metadata.
    const commits = await github.paginate(github.rest.pulls.listCommits, {
      owner,
      repo,
      pull_number: pr.number,
      per_page: 100,
    });
    const dependabotMessage = commits.find((c) => c.author?.login === DEPENDABOT)?.commit.message;
    assessed.push({
      pr,
      result: assess(pr, { required, checkRuns, behindBy: compare.behind_by, dependabotMessage }),
    });
  }

  const { merge, update } = plan(assessed);
  const done = new Map();

  if (merge) {
    if (dryRun) {
      done.set(merge.number, 'would merge (dry run)');
    } else {
      try {
        // `sha` pins the merge to the commit whose checks were read: a push since then refuses it.
        await github.rest.pulls.merge({
          owner,
          repo,
          pull_number: merge.number,
          merge_method: 'merge',
          sha: merge.head.sha,
        });
        done.set(merge.number, 'merged');
      } catch (error) {
        core.setFailed(
          `#${merge.number} could not be merged: ${error.message}. If this is a ruleset refusal, the merge app is missing from the main ruleset's bypass list (see the workflow header).`,
        );
        done.set(merge.number, `merge refused: ${error.message}`);
      }
    }
  }

  if (update) {
    if (dryRun) {
      done.set(update.number, 'would update its branch (dry run)');
    } else {
      try {
        await github.rest.pulls.updateBranch({
          owner,
          repo,
          pull_number: update.number,
          expected_head_sha: update.head.sha,
        });
        done.set(update.number, 'branch updated with main; CI is re-running');
      } catch (error) {
        core.warning(`#${update.number}: could not update the branch: ${error.message}`);
        done.set(update.number, `branch update failed: ${error.message}`);
      }
    }
  }

  const rows = assessed
    .sort((a, b) => a.pr.number - b.pr.number)
    .map(({ pr, result }) => [
      `#${pr.number}`,
      pr.title,
      done.get(pr.number) ?? WAITING[result.action],
      result.reason,
    ]);
  await core.summary
    .addHeading(dryRun ? 'Dependabot merge (dry run)' : 'Dependabot merge')
    .addTable([
      [
        { data: 'PR', header: true },
        { data: 'Title', header: true },
        { data: 'Outcome', header: true },
        { data: 'Why', header: true },
      ],
      ...rows,
    ])
    .write();
  core.info(rows.length ? rows.map((r) => r.join(' | ')).join('\n') : 'No open Dependabot PRs.');
}

module.exports = { DEPENDABOT, BASE, updateTypes, checkVerdict, assess, plan, run };
