---
name: local-dev
description: Use when starting, stopping or debugging the local dev loop - dev.ps1/worker.ps1/stop-dev.ps1, Seq, the two Postgres ports, dotnet ef and user-secrets disagreeing about the database, the e2e port clash, and local-run/control-panel.bat.
---

# Running locally

```powershell
./scripts/dev.ps1                                            # all of the below, in three windows
./scripts/dev.ps1 -WithSeq                                   # same, plus Seq at localhost:55341
./scripts/dev.ps1 -WithPartners                              # same, plus a real mTLS test partner on 55690 (health 55691)
./scripts/worker.ps1                                         # just the job worker, in this window
./scripts/new-dev-certs.ps1                                  # throwaway PKI into .certs/, for trying an external system
```

`new-dev-certs.ps1` is only needed to point a configured external system at local certificate
files (`.certs/` is git-ignored; `-Force` replaces it). It is not in the control panel menu, the
dev loop calls no external system by default, and the tests generate their own certificates —
see the `external-systems` skill.

The three windows are the API (5234), the **job worker** (5235) and Vite (5173). `worker.ps1` is
for restarting only the worker — which `dotnet run --project src/Worker -- codegen write` requires
before new adapters take effect, and which is otherwise a stop-everything-and-start-again.

`-WithPartners` launches `tests/PartnerSimulator` in a window of its own (mTLS on 55690, health on
55691) from `.certs/`, running `new-dev-certs.ps1` first if `client.pfx` is missing, and gives the
launched worker `ExternalSystems__Systems__PartnerSimulator__*` so `/monitoring/integrations` has a
system to show. It checks both ports are free, and `stop-dev.ps1` frees 55690. The e2e simulator
uses 55692/55693, so the two run side by side. The control panel has no entry for it: run the flag
from a terminal.

`-WithSeq` starts `docker-compose.yml`'s `observability` profile alongside Postgres and points
the launched API at it (`Observability__Otlp__Enabled`/`__Endpoint`, set on the API's own
process environment, never baked into `appsettings.Development.json` — every developer's `dotnet
run` would otherwise try to export to a collector nobody started). It also sets
`Monitoring__TraceLinkTemplate` on the API, so the monitoring tables' Trace column links into
Seq. `scripts/stop-dev.ps1` always
passes `--profile observability` to `docker compose down`, whether or not `-WithSeq` was used —
confirmed empirically, not assumed, that a bare `docker compose down` does NOT stop a
profile-started container even when it is currently running. See the `observability` skill.

Or by hand:

```bash
docker compose up -d --wait                                  # dev Postgres on 55433, RabbitMQ on 55672
dotnet ef database update --project src/Infrastructure --startup-project src/Infrastructure
dotnet run --project src/Api                                 # then `npm start` in frontend/
```

Both open a browser tab of their own: the API reference at `/scalar/v1` (from
`launchSettings.json`) and the app at `http://localhost:5173` (from `vite.config.ts`). For
one-keystroke startup in Rider or Visual Studio, and the gotchas that come with it, see
[docs/local-development.md](docs/local-development.md).

Two databases, two ports, and they are meant to coexist: **55433** is the dev database from
`docker-compose.yml` (named volume, data persists); **55432** is the e2e one from
`docker-compose.e2e.yml` (throwaway). Override either with `DEV_PG_PORT` / `PG_PORT`.

The message broker follows the same split: the dev one is in `docker-compose.yml` too, on
**55672** (AMQP) and **55673** (management UI); the e2e one is in `docker-compose.e2e.yml`, on
**55682** and **55683**. Unlike Seq and Redis, both the API and the worker refuse to start
without a broker (ADR 0026) — so `dotnet run` on its own, or an IDE's F5 (Rider, Visual Studio),
needs `docker compose up -d` run first. `dev.ps1` and `worker.ps1` already do this.

The dev connection string is committed in `src/Api/appsettings.Development.json` — throwaway
credentials against a localhost-only container that is never deployed, the same judgement
already applied to `docker-compose.e2e.yml`. The no-secrets-in-`appsettings*.json` rule still
holds for everything else.

The two *databases* coexist happily, but the .NET processes do not: `npm run e2e` starts its own
API on 5234 and its own worker on 5235 — the dev loop's ports — with `reuseExistingServer: false`.
Stop the dev loop before an e2e run, or set `API_PORT` / `WORKER_PORT`.

**The gotcha that will cost you an afternoon: `dotnet ef` cannot see user-secrets.** Migrations
run through `src/Infrastructure/Persistence/DesignTimeDbContextFactory.cs`, which reads only the
`ConnectionStrings__Default` environment variable — so `database update` against anything but
the default needs it passed explicitly:

```bash
ConnectionStrings__Default='Host=localhost;Port=55433;Database=aiframework;Username=postgres;Password=postgres' \
  dotnet ef database update --project src/Infrastructure --startup-project src/Infrastructure
```

The reverse also bites: a user-secret sits *above* `appsettings.Development.json` in the
configuration order, so a stale `ConnectionStrings:Default` there silently wins over the
committed value at runtime while `dotnet ef` ignores it. Check with
`dotnet user-secrets list --project src/Api` if the app and the migrations disagree about which
database they are talking to.


## One-click start/stop

`local-run/control-panel.bat` is a double-clickable menu for both setups, for anyone who would
rather not open a terminal — it has no logic of its own beyond the menu:

| Menu option | Runs |
|---|---|
| Install/check prerequisites | `scripts/install-prereqs.ps1` — see below |
| Start dev loop | `scripts/dev.ps1` — the plain local dev loop: Postgres, **RabbitMQ**, API, **job worker**, Vite |
| Start dev loop + Seq | `scripts/dev.ps1 -WithSeq` — same, plus Seq at `localhost:55341` |
| Start job worker only | `scripts/worker.ps1` — restarts just the worker, leaving a working API and Vite alone. Runs in the foreground, so you watch its log; `codegen write` needs a worker restart to take effect |
| Stop dev loop | `scripts/stop-dev.ps1` — kills the API/worker/Vite ports, tears down the database and the broker (and Seq, if it was started). Messages waiting on a queue survive in the `rabbitmqdata` volume, as rows do in `pgdata` |
| Start Kubernetes | `deploy/start-cluster.ps1` — creates the kind cluster if missing, else redeploys onto it |
| Start Kubernetes + observability | `deploy/start-cluster.ps1 -WithObservability` — same, plus OpenSearch, Prometheus, Grafana and the alert rules. **Once a cluster has observability, redeploy it with this option every time**: plain "Start Kubernetes" drops the apps' OTLP settings, and the UIs keep running but stop receiving anything |
| Open observability UIs | `deploy/observability-ui.ps1`, in a window of its own — port-forwards Grafana (3000), Prometheus (9090) and OpenSearch Dashboards (5601), each reconnecting on its own after a redeploy, and opens Grafana. Closing the window stops the forwards |
| Stop Kubernetes | `deploy/teardown.ps1` — `kind delete cluster`; Postgres data inside it goes with it |
| Run e2e tests (local stack) | `scripts/e2e.ps1` — stop the dev loop first, it uses ports 5234 and 5235 |
| Run e2e tests (against Kubernetes) | `deploy/e2e-k8s.ps1` — deploy it first with "Start Kubernetes" |
| Open last e2e report | `scripts/e2e-report.ps1` |
| Pull latest | `scripts/update-branch.ps1` — fast-forwards whatever branch is currently checked out |

The `.ps1` scripts it calls are the source of truth and work the same run directly.

`scripts/install-prereqs.ps1` is what a genuinely new machine needs run first — it checks for
(and installs via `winget` whatever is missing) the .NET SDK, Node.js, Docker Desktop, `kubectl`,
`kind`, `k9s`, and Playwright's chromium browser. It only installs what is entirely absent; a
tool that is present but older
than expected is reported, not silently upgraded, since upgrading Docker Desktop or Node.js
touches every other project on the machine, not just this one. It also flags a global `~/.npmrc`
pinning `os=`/`cpu=` to the wrong platform — the exact cause of a `npm install` failure
(`Cannot find native binding`, rolldown's Windows binding silently never downloaded) hit and
fixed on this repo once already. Docker Desktop's own first-run setup (WSL2 backend, license
terms, a restart) cannot be scripted unattended; the script starts that install and says so.

