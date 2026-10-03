# Running the stack locally

One script, one command per piece, or one keystroke if you set your IDE up. The command line is
the reference; the script and the IDE sections build on it.

## The script

```powershell
./scripts/dev.ps1                  # database, broker, migrations, API, job worker, dev server
./scripts/dev.ps1 -SkipMigrations  # when you know the schema is current
./scripts/dev.ps1 -WithSeq         # same, plus Seq (structured logs) at localhost:55341
```

It checks Docker is running and that 5234, 5235 and 5173 are free, starts Postgres and RabbitMQ
and waits for their healthchecks, applies migrations, then launches the API, the job worker and
the dev server **each in its own window**. All three are long-running foreground processes with
their own logs, so a window each keeps those readable and makes Ctrl-C mean "stop this one".
`./scripts/worker.ps1` restarts just the worker, which `codegen write` requires.

The migration step is the part worth having: it sets `ConnectionStrings__Default` before calling
`dotnet ef`, which is exactly the trap described under "The gotcha that will cost you an
afternoon" in the `local-dev` skill (`.claude/skills/local-dev/SKILL.md`). Without it,
`database update` aims at the `design_time_only` placeholder rather than your dev database.

`scripts/stop-dev.ps1` stops the database and the broker (and Seq, if `-WithSeq` started it); the
three windows are yours to Ctrl-C. A bare `docker compose down` also stops the database, but **not** Seq if it
is running — Seq sits behind a compose profile, and `down` with no `--profile` flag only tears
down the active profile set for that invocation, not whatever an earlier `up` left running. Use
`docker compose --profile observability down` (what `stop-dev.ps1` always passes) to be sure.

## The command line

```bash
docker compose up -d --wait                 # dev Postgres on 55433, RabbitMQ on 55672 (UI 55673)
dotnet run --project src/Api                # API on 5234, opens the API reference
dotnet run --project src/Worker             # job worker on 5235 (health endpoints only)
npm start --prefix frontend                 # app on 5173, opens in a browser tab
```

Migrations, if the database is new or behind:

```bash
dotnet ef database update --project src/Infrastructure --startup-project src/Infrastructure
```

**The backend must be running before the frontend is useful.** Vite proxies `/api` to
`http://localhost:5234`, so the app loads without it but every data call fails. **So must the
worker, for anything asynchronous**: jobs run only there and the API listens to no queue, so
without it an enqueued job sits on its RabbitMQ queue and nothing says so.

## What opens by itself, and why

Two separate mechanisms, which is worth knowing when only one of them fires:

| Tab | Opened by | Configured in |
|---|---|---|
| `http://localhost:5234/scalar/v1` — API reference | ASP.NET Core's launch profile | `src/Api/Properties/launchSettings.json` (`launchBrowser`, `launchUrl`) |
| `http://localhost:5173` — the app | Vite's dev server | `frontend/vite.config.ts` (`server.open`) |

Neither knows about the other. If you start both, you get two tabs; if you start one, you get
one.

**Running `bin/Debug/net10.0/AiFramework.Api.exe` directly opens nothing.** That path bypasses
`launchSettings.json` entirely — it is read by `dotnet run` and by IDE launch configurations, not
baked into the executable. This surprises people; it is not a bug.

**Before either IDE's F5: `docker compose up -d --wait`.** Neither `dotnet run` nor an IDE launch
configuration starts the dev stack's containers — both the API and the worker refuse to start
without Postgres and the message broker (ADR 0026), so the "command line" steps above (or just
the compose line) need to have been run at least once first.

## JetBrains Rider — one keystroke for both

Rider launches several configurations together with a **Compound**.

1. **Run → Edit Configurations → + → npm**
   - *package.json*: `frontend/package.json`
   - *Command*: `run`, *Script*: `start`
   - Name it `frontend`.
2. **+ → Compound**
   - Name it `Backend + Frontend`.
   - Add the API's and the worker's `http` configurations (Rider generates both from their
     `launchSettings.json`) and the `frontend` configuration from step 1.
3. Select the Compound and press F5.

You get the API, the job worker, the dev server, and both browser tabs.

**These configurations are personal, not shared.** `.idea/` is in `.gitignore`, so what you
create here stays on your machine. Rider also reads shared configurations from a committed
`.run/` directory, but a Compound refers to its members by name and type, and the backend
configuration has no file of its own — Rider synthesises it from `launchSettings.json`. A
committed Compound pointing at a synthesised configuration is fragile, which is why this repo
documents the steps instead of shipping the XML.

## Visual Studio

> **Not verified.** The Rider steps above were followed and confirmed working before being
> written down. These were not — Visual Studio was not available on the machine this was written
> on, so the steps come from documentation rather than from running them. Treat this section as a
> starting point and correct it once someone has actually followed it.

Visual Studio's **multiple startup projects** cannot start the frontend here, and it is worth
understanding why before trying: `AiFramework.slnx` contains the five `src` projects and the five
test projects, and nothing else. The frontend is an npm workspace, not a project in the solution,
so there is nothing for that dialog to list.

### Recommended: VS runs the backend, a terminal runs the frontend

No repository change, and the behaviour matches the command line.

1. **Solution Properties → Startup Project → Multiple startup projects**, and start both
   `AiFramework.Api` and `AiFramework.Worker`.
2. Press F5. Visual Studio honours `launchSettings.json`, so the API and the worker start and
   the API reference opens.
3. **View → Terminal**, then:

   ```bash
   npm start --prefix frontend
   ```

The frontend opens its own tab. Two windows to start rather than one keystroke, but nothing to
maintain.

### If you want true F5 parity

Add a JavaScript project (`.esproj`, the Visual Studio JavaScript Project System) that wraps
`frontend/`, add it to `AiFramework.slnx`, and then **Solution Properties → Startup Project →
Multiple startup projects** can start both.

The cost is real and worth weighing: another project file to keep in step with `package.json`, an
entry in the solution that Rider and the command line neither need nor use, and a second place
where the dev server's startup can be configured. Worth it if Visual Studio is your primary
environment; not worth it as a convenience.

### What to avoid

`Microsoft.AspNetCore.SpaProxy` is the Visual Studio template's usual answer, and it fits this
repo badly. It makes the backend responsible for launching and proxying to the SPA — the reverse
of the arrangement here, where Vite proxies `/api` to the API. Adopting it means changing which
URL you open, adding a package to `Api`, and having two proxy configurations that disagree about
direction.

## Ports, and the one collision that matters

| Port | What | Override |
|---|---|---|
| 5234 | API | `API_PORT` |
| 5235 | Job worker (health endpoints only) | `WORKER_PORT` |
| 5173 | Vite dev server | `DEV_PORT` |
| 4173 | Vite preview (e2e only) | `PREVIEW_PORT` |
| 55433 | Dev Postgres | `DEV_PG_PORT` |
| 55432 | e2e Postgres | `PG_PORT` |
| 55672/55673 | Dev RabbitMQ: AMQP / management UI | `RABBITMQ_PORT` / `RABBITMQ_UI_PORT` |
| 55682/55683 | e2e RabbitMQ: AMQP / management UI | `E2E_RABBITMQ_PORT` / `E2E_RABBITMQ_UI_PORT` |

The two *databases* and the two *brokers* are designed to run at the same time. The API and
worker processes are not: `npm run e2e` starts its own API on 5234 and worker on 5235 with
`reuseExistingServer: false`, so it collides with a running dev loop. **Stop the dev loop before
an e2e run**, or set `API_PORT` and `WORKER_PORT`.

`API_PORT` is read by both `vite.config.ts` and the e2e setup, so moving the API keeps the proxy
pointed at it.

## Two things that look like bugs and are not

**A failed API call immediately after launch.** A Rider Compound starts both stacks in parallel,
so Vite can serve the app a moment before the API is listening. The first data call fails and
TanStack Query retries. Nothing to fix.

**`MSB3026` / `MSB3021` on build: "the file is locked by AiFramework.Api".** The app is still
running and holding `bin/Debug/net10.0/AiFramework.Api.exe`. Stop it and rebuild. The error count
can look alarming — a dozen errors, zero warnings — but there is nothing wrong with the code, and
nothing to fix in it.
