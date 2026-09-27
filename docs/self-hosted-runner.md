# Running CI on a self-hosted runner

Every job in `.github/workflows/ci.yml` and `e2e.yml` runs on
`${{ vars.CI_RUNNER || 'ubuntu-latest' }}`. With the repository variable `CI_RUNNER` unset, CI
uses GitHub's hosted runners and bills Actions minutes. Set it to `self-hosted` and every job runs
on your own machine for free. Delete the variable to switch back; nothing in the repo changes.

## Only while the repository is private

A self-hosted runner executes whatever a workflow tells it to. On a **public** repository a pull
request from any fork can run code on your machine. GitHub's own guidance is to use self-hosted
runners with private repositories only. If this repository is ever made public, delete
`CI_RUNNER` first — public repositories get hosted runners for free anyway.

## The machine

The workflows assume what `ubuntu-latest` provides, so the runner must be:

- **Linux x64.** Steps are bash, and Testcontainers and `docker-compose.e2e.yml` need Linux
  containers. On Windows, use a WSL2 Ubuntu distribution, not a Windows runner.
- **Running Docker**, with the Compose plugin, and the runner's user in the `docker` group.
  `dotnet test` (Testcontainers) and the e2e suite (compose Postgres on **55432**) both need it.
- **Free on ports 55432, 5234 and 5235** while e2e runs. Those are the dev loop's API and worker
  ports too (CLAUDE.md, "Running locally"), so either stop `./scripts/dev.ps1` while CI runs or use
  a machine or WSL distribution you do not develop on.
- **Registered as one runner.** Jobs then queue and run one at a time. Two runners on one machine
  could run two e2e suites at once, and they would fight over those ports.

.NET and Node are **not** preinstalled: `actions/setup-dotnet` and `actions/setup-node` download
the versions pinned in the workflows on the first run and reuse them afterwards.

## One-time setup

1. **Docker.** Install Docker Engine and the Compose plugin
   (<https://docs.docker.com/engine/install/ubuntu/>), then:

   ```bash
   sudo usermod -aG docker "$USER"   # log out and back in afterwards
   docker run --rm hello-world       # must work without sudo
   ```

2. **Chromium's system libraries.** Hosted runners install these on every e2e run with
   `playwright install --with-deps`, which needs root. The e2e workflow drops `--with-deps` on a
   self-hosted runner, so install them once yourself, from a clone of this repository with Node
   24 on the PATH:

   ```bash
   cd frontend && npm ci && npx playwright install-deps chromium   # prompts for sudo
   ```

   Repeat this after a Playwright upgrade if e2e fails to launch the browser.

3. **Register the runner.** On GitHub: **Settings → Actions → Runners → New self-hosted runner**,
   choose **Linux / x64**, and run the commands the page shows (download, then
   `./config.sh --url https://github.com/Surentz/AIFrameWork --token …`). Accept the default
   labels — they include `self-hosted`.

4. **Let `setup-dotnet` install without root.** It installs into `/usr/share/dotnet` by default,
   which the runner's user cannot write. In the runner's directory (next to `config.sh`), add a
   line to the `.env` file:

   ```bash
   echo "DOTNET_INSTALL_DIR=$HOME/.dotnet" >> .env
   ```

5. **Run it as a service** so it survives reboots and logouts:

   ```bash
   sudo ./svc.sh install "$USER" && sudo ./svc.sh start
   ```

   It should now show as **Idle** under Settings → Actions → Runners.

6. **Point CI at it.** **Settings → Secrets and variables → Actions → Variables → New repository
   variable**: name `CI_RUNNER`, value `self-hosted`. The next push to `main` or pull request runs
   there.

## Going back

Delete the `CI_RUNNER` variable. Queued and future jobs go to `ubuntu-latest` again. If the
runner machine is offline while `CI_RUNNER` is set, jobs wait in the queue for it (up to 24 hours)
rather than falling back.
