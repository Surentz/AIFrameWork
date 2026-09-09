# Kubernetes Deployment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Run the whole application — API (2 replicas), SPA, and Postgres — on a local kind cluster, closely enough to a real deployment that multi-replica bugs surface here.

**Architecture:** Two container images (API and nginx-served SPA) plus a migration-bundle stage, deployed by Kustomize (`base` + `local` overlay) behind ingress-nginx on one host. The API's cookie key ring moves into Postgres so pods accept each other's sessions; cache eviction correctness is carried by ingress session affinity.

**Tech Stack:** .NET 10 / ASP.NET Core, EF Core 10 + Npgsql, Wolverine 6.33, React 19 + Vite 8, Docker, kind, Kustomize, ingress-nginx, PowerShell.

**Spec:** `docs/superpowers/specs/2026-09-09-kubernetes-deployment-design.md`

## Global Constraints

- Target framework `net10.0`; .NET SDK 10.0.400; Node 24.20.0.
- New .NET package references use version **10.0.11**, matching the existing pinned Microsoft packages.
- **Warnings are errors** from compiler, analyzers, and build (`Directory.Build.props`). No suppression without a justification comment directly above a narrow `#pragma warning disable`/`restore` pair.
- **Nullable is enabled.** `required` in `Domain`, never `[Required]`.
- **Never `catch (Exception)`.** `throw;`, never `throw ex;`.
- **Never hand-edit an applied EF migration.** Add a new one. `.claude/hooks/protect-migrations.ps1` enforces this against git history.
- **No secrets in `appsettings*.json`.**
- Container images must be built **Release** — Release loads the pre-generated Wolverine adapters in `src/Api/Internal/Generated`.
- After changing a Wolverine handler: `dotnet run --project src/Api -- codegen write`, then commit.
- `dotnet ef` uses `src/Infrastructure` as **both** `--project` and `--startup-project`, and reads only the `ConnectionStrings__Default` environment variable.
- Migrations live in `src/Infrastructure/Persistence/Migrations` — pass `--output-dir Persistence/Migrations`.
- Do not modify `docker-compose.yml`, `docker-compose.e2e.yml`, the e2e suite, or CI.
- Frontend API calls stay **relative**; never set `credentials: 'include'`.
- Kubernetes config keys use **double** underscores (`Cache__Enabled`). A single underscore binds nothing and warns nothing.

---

### Task 1: Verify Wolverine's concurrent schema migration

The spec makes this the first task because its outcome changes Task 7's scope. Two API replicas start simultaneously and durable Wolverine migrates its envelope tables during host startup. If Wolverine does not guard that with a lock, two pods race on DDL against one database.

This task produces a **decision recorded in the plan**, not shipped code.

**Files:**
- Modify: `docs/superpowers/plans/2026-09-09-kubernetes-deployment.md` (record the finding in Task 7)

- [ ] **Step 1: Start a clean database**

```bash
docker compose up -d --wait
```

- [ ] **Step 2: Drop any existing Wolverine schema so both processes race from empty**

```bash
docker exec -i aiframework-dev-postgres-1 psql -U postgres -d aiframework \
  -c "DROP TABLE IF EXISTS wolverine_incoming_envelopes, wolverine_outgoing_envelopes, wolverine_dead_letters, wolverine_nodes, wolverine_node_assignments CASCADE;"
```

- [ ] **Step 3: Launch two API processes simultaneously against the same database**

Two hosts, different ports, started in the same instant — this is the race a two-replica rollout creates.

```bash
export ConnectionStrings__Default='Host=localhost;Port=55433;Database=aiframework;Username=postgres;Password=postgres'
dotnet run --project src/Api --urls http://localhost:5301 > /tmp/wolverine-a.log 2>&1 &
dotnet run --project src/Api --urls http://localhost:5302 > /tmp/wolverine-b.log 2>&1 &
wait
```

- [ ] **Step 4: Inspect both logs for DDL failures**

```bash
grep -iE "duplicate|already exists|deadlock|42P07|42710" /tmp/wolverine-a.log /tmp/wolverine-b.log
```

Expected if the guard holds: no matches, and both processes reach "Application started".

- [ ] **Step 5: Record the outcome in Task 7**

If clean, edit Task 7 to note "Wolverine auto-migration verified safe at 2 replicas on <date>" and leave `Wolverine__Durable=true` with no further change.

If it raced, edit Task 7 to add `Wolverine__AutoBuildMessageStorageOnStartup=false` to the ConfigMap and add a step to the migrate Job that runs Wolverine's storage creation once, before the API Deployment is applied.

- [ ] **Step 6: Stop the processes and commit the finding**

```bash
kill %1 %2 2>/dev/null
git add docs/superpowers/plans/2026-09-09-kubernetes-deployment.md
git commit -m "docs: record Wolverine concurrent-migration finding for the k8s plan"
```

---

### Task 2: Persist the Data Protection key ring to Postgres

Without this, each pod mints its own ephemeral key ring and rejects cookies issued by the other. Users see intermittent 401s that look like a flaky login.

**Files:**
- Modify: `src/Infrastructure/AiFramework.Infrastructure.csproj`
- Modify: `src/Infrastructure/Persistence/AiFrameworkDbContext.cs`
- Create: `src/Infrastructure/Persistence/Migrations/<timestamp>_AddDataProtectionKeys.cs` (generated)
- Modify: `src/Api/Program.cs:139` (after `AddInfrastructure`)
- Create: `tests/Api.IntegrationTests/DataProtectionTests.cs`

**Interfaces:**
- Consumes: `AiFrameworkDbContext`, `ApiFactory` (existing).
- Produces: `AiFrameworkDbContext.DataProtectionKeys` — `DbSet<DataProtectionKey>`, used by nothing else in this plan.

> **Read this before writing the test.** The obvious test — issue a cookie on host A, present it to host B — **passes before the fix** on a developer machine. With no explicit store, ASP.NET Core Data Protection falls back to a filesystem key ring under the user profile, keyed by content-root path. Both test hosts share that path, so they share keys, and the test proves nothing. The primary assertion must therefore be that keys are in **Postgres**. The cross-host cookie test is kept as a second, behavioural guard.

- [ ] **Step 1: Write the failing test**

Create `tests/Api.IntegrationTests/DataProtectionTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Api.IntegrationTests;

/// <summary>
/// The key ring must live in Postgres, not in each host's memory: two API replicas that
/// disagree about keys reject each other's session cookies, which presents as an
/// intermittent 401 rather than as an obvious failure.
/// </summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class DataProtectionTests(ApiFactory factory)
{
    /// <remarks>
    /// Asserting on the table rather than on cross-host cookie acceptance is deliberate.
    /// With no store configured, Data Protection falls back to a filesystem key ring keyed
    /// by content-root path, which both test hosts share — so a cookie test passes here
    /// even with the bug present, and only fails once the hosts are separate containers.
    /// </remarks>
    [Fact]
    public async Task IssuingACookie_PersistsTheKeyRingToTheDatabase()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        client.Dispose();

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();

        var keyCount = await context.DataProtectionKeys.AsNoTracking().CountAsync();

        keyCount.Should().BeGreaterThan(0);
    }

    /// <remarks>
    /// Registers by hand rather than through CreateAuthenticatedClientAsync: that helper
    /// relies on the client's internal cookie handler, so the cookie never appears on
    /// DefaultRequestHeaders and cannot be lifted off it. Reading Set-Cookie from the
    /// response is the only way to carry the session to a second host.
    /// </remarks>
    [Fact]
    public async Task ACookieFromOneHost_IsAcceptedByASecondHost()
    {
        using var first = factory.CreateClient();

        var registration = await first.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                Username = $"u{Guid.NewGuid():N}"[..32],
                Password = "a long enough test password",
                DisplayName = "Test User",
            });
        registration.EnsureSuccessStatusCode();

        var setCookie = registration.Headers.GetValues("Set-Cookie").First();
        var sessionCookie = setCookie.Split(';', 2)[0];

        using var secondFactory = factory.WithWebHostBuilder(_ => { });
        using var second = secondFactory.CreateClient();
        second.DefaultRequestHeaders.Add("Cookie", sessionCookie);

        var response = await second.GetAsync("/api/orders");

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test tests/Api.IntegrationTests --filter "FullyQualifiedName~DataProtectionTests"
```

Expected: FAIL — `AiFrameworkDbContext` has no member `DataProtectionKeys` (compile error).

- [ ] **Step 3: Add the package reference**

In `src/Infrastructure/AiFramework.Infrastructure.csproj`, inside the main `ItemGroup`, keeping alphabetical position near the other `Microsoft.*` entries:

```xml
<!--
  The Data Protection key ring, persisted to the application database. Two API replicas
  that each hold an in-memory ring reject each other's session cookies; with no store
  configured the framework falls back to a per-machine filesystem ring, which does not
  exist across containers. See ADR 0010.
-->
<PackageReference Include="Microsoft.AspNetCore.DataProtection.EntityFrameworkCore" Version="10.0.11" />
```

- [ ] **Step 4: Add the DbSet**

In `src/Infrastructure/Persistence/AiFrameworkDbContext.cs`, add the using and interface, then the property after `Users`:

```csharp
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
```

```csharp
public sealed class AiFrameworkDbContext(DbContextOptions<AiFrameworkDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
```

```csharp
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
```

- [ ] **Step 5: Generate the migration**

```bash
ConnectionStrings__Default='Host=localhost;Port=55433;Database=aiframework;Username=postgres;Password=postgres' \
  dotnet ef migrations add AddDataProtectionKeys \
  --project src/Infrastructure --startup-project src/Infrastructure \
  --output-dir Persistence/Migrations
```

Do not hand-edit the result.

- [ ] **Step 6: Register the store**

In `src/Api/Program.cs`, immediately after `builder.Services.AddInfrastructure(connectionString);`:

```csharp
// The key ring goes to Postgres, not to each host's memory. Two replicas with separate
// rings reject each other's session cookies, which surfaces as an intermittent 401 rather
// than an obvious failure. SetApplicationName is load-bearing, not decoration: the purpose
// string derives from it, so pods that disagree on the name share a ring and still refuse
// each other's cookies. Keys are unencrypted at rest — acceptable for a local cluster with
// throwaway credentials, and the condition ADR 0010 places on a cloud target.
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<AiFrameworkDbContext>()
    .SetApplicationName("AiFramework");
```

Add the using if the analyzer requires it:

```csharp
using AiFramework.Infrastructure.Persistence;
```

- [ ] **Step 7: Run the tests to verify they pass**

```bash
dotnet test tests/Api.IntegrationTests --filter "FullyQualifiedName~DataProtectionTests"
```

Expected: PASS, both tests.

- [ ] **Step 8: Run the full backend suite for regressions**

```bash
dotnet test
```

Expected: PASS. `ApiFactory.InitializeAsync` calls `MigrateAsync`, so the new table is created for every existing integration test automatically.

- [ ] **Step 9: Commit**

```bash
git add src/Infrastructure/AiFramework.Infrastructure.csproj \
        src/Infrastructure/Persistence/AiFrameworkDbContext.cs \
        src/Infrastructure/Persistence/Migrations \
        src/Api/Program.cs \
        tests/Api.IntegrationTests/DataProtectionTests.cs
git commit -m "feat(auth): persist the data protection key ring to postgres"
```

---

### Task 3: Add a readiness probe

`/health` must keep its current meaning. `HealthTests` runs with **no database** and asserts 200; it is deliberately container-free and `tests/CLAUDE.md` documents why. A database check belongs on a new endpoint.

**Files:**
- Modify: `src/Api/AiFramework.Api.csproj`
- Modify: `src/Api/Program.cs:169` (after the `/health` mapping)
- Create: `tests/Api.IntegrationTests/ReadinessTests.cs`

**Interfaces:**
- Produces: `GET /health/ready` → 200 when Postgres answers, 503 when it does not. Consumed by Task 8's `readinessProbe`.

- [ ] **Step 1: Write the failing test**

Create `tests/Api.IntegrationTests/ReadinessTests.cs`:

```csharp
using System.Net;
using FluentAssertions;

namespace AiFramework.Api.IntegrationTests;

/// <summary>
/// Liveness and readiness are different questions and have different endpoints. /health says
/// the process is alive and touches no database — HealthTests covers it without a container.
/// /health/ready says this pod may receive traffic, which requires Postgres.
/// </summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class ReadinessTests(ApiFactory factory)
{
    [Fact]
    public async Task GetReady_WhenTheDatabaseIsReachable_Returns200Ok()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetReady_IsAnonymous()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test tests/Api.IntegrationTests --filter "FullyQualifiedName~ReadinessTests"
```

Expected: FAIL — 404, the endpoint is not mapped.

- [ ] **Step 3: Add the package reference**

In `src/Api/AiFramework.Api.csproj`, in the `PackageReference` `ItemGroup`:

```xml
<!-- AddDbContextCheck, for the /health/ready readiness probe. -->
<PackageReference Include="Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore" Version="10.0.11" />
```

- [ ] **Step 4: Register the health check**

In `src/Api/Program.cs`, after `builder.Services.AddDataProtection()...` from Task 2:

```csharp
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AiFrameworkDbContext>();
```

- [ ] **Step 5: Map the endpoint**

In `src/Api/Program.cs`, directly after the existing `/health` mapping:

```csharp
// Readiness, distinct from the liveness check above: this one answers "may this pod receive
// traffic", which needs Postgres. It stays a separate endpoint because /health must remain
// database-free — HealthTests boots a host with no database at all and asserts 200 on it.
// Anonymous for the same reason /health is: no fallback authorization policy is registered.
app.MapHealthChecks("/health/ready");
```

- [ ] **Step 6: Run the tests to verify they pass**

```bash
dotnet test tests/Api.IntegrationTests --filter "FullyQualifiedName~ReadinessTests"
```

Expected: PASS, both tests.

- [ ] **Step 7: Confirm HealthTests still runs without a database**

```bash
dotnet test tests/Api.IntegrationTests --filter "FullyQualifiedName~HealthTests"
```

Expected: PASS. If this fails, the health check was registered onto `/health` by mistake.

- [ ] **Step 8: Regenerate the API contract**

`/health/ready` is a mapped endpoint, so the committed OpenAPI document changes and CI's `contract` job compares it.

```bash
dotnet restore src/Api
ConnectionStrings__Default='Host=localhost;Port=55433;Database=placeholder;Username=x;Password=y' \
  Wolverine__Durable=false \
  dotnet msbuild src/Api -t:"Build;GenerateOpenApiDocuments"
npm run generate:api --prefix frontend
```

- [ ] **Step 9: Commit**

```bash
git add src/Api/AiFramework.Api.csproj src/Api/Program.cs \
        tests/Api.IntegrationTests/ReadinessTests.cs \
        openapi/AiFramework.Api.json frontend/src/api/schema.d.ts
git commit -m "feat(api): add a database-backed readiness probe alongside /health"
```

---

### Task 4: API image, with a migration-bundle stage

**Files:**
- Create: `.dockerignore`
- Create: `Dockerfile.api`

**Interfaces:**
- Produces: image `aiframework-api:local` (entrypoint `dotnet AiFramework.Api.dll`, listening on 8080) and image target `migrator` carrying `/app/efbundle`. Both consumed by Tasks 7 and 8.

- [ ] **Step 1: Create `.dockerignore`**

Not hygiene — without it, local `bin/` and `obj/` are copied into the build context and can poison the restore, and `node_modules` makes the context enormous.

```
**/bin/
**/obj/
**/node_modules/
**/dist/
.git/
.vs/
.idea/
frontend/playwright-report/
frontend/test-results/
**/*.user
```

- [ ] **Step 2: Create `Dockerfile.api`**

```dockerfile
# syntax=docker/dockerfile:1

# Release, always. Release is what loads the pre-generated Wolverine adapters committed under
# src/Api/Internal/Generated; a Debug image would run here and diverge from what CI validates.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props .editorconfig ./
COPY src/Domain/AiFramework.Domain.csproj src/Domain/
COPY src/Application/AiFramework.Application.csproj src/Application/
COPY src/Infrastructure/AiFramework.Infrastructure.csproj src/Infrastructure/
COPY src/Api/AiFramework.Api.csproj src/Api/
RUN dotnet restore src/Api/AiFramework.Api.csproj

COPY src/ src/
RUN dotnet publish src/Api/AiFramework.Api.csproj -c Release -o /app/publish --no-restore

# A self-contained migration bundle, so the EF Design package never reaches a runtime image.
# CLAUDE.md records what that package costs when it leaks into a publish: 7.9MB to 37MB.
# src/Infrastructure is both project and startup project — it has its own design-time factory.
FROM build AS bundle
RUN dotnet tool install --global dotnet-ef --version 10.0.11
ENV PATH="/root/.dotnet/tools:${PATH}"
RUN dotnet ef migrations bundle \
      --project src/Infrastructure/AiFramework.Infrastructure.csproj \
      --startup-project src/Infrastructure/AiFramework.Infrastructure.csproj \
      --configuration Release \
      --self-contained --runtime linux-x64 \
      --output /app/efbundle

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled AS migrator
WORKDIR /app
COPY --from=bundle /app/efbundle ./efbundle
USER $APP_UID
ENTRYPOINT ["./efbundle"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "AiFramework.Api.dll"]
```

- [ ] **Step 3: Build both targets**

```bash
docker build -f Dockerfile.api --target runtime -t aiframework-api:local .
docker build -f Dockerfile.api --target migrator -t aiframework-migrator:local .
```

Expected: both succeed. A compiler or analyzer warning fails the build by design — fix the code, do not weaken the Dockerfile.

If the chiseled base is missing an ICU dependency the code needs, switch both runtime stages to `-alpine` variants. Do **not** set `InvariantGlobalization`, which changes runtime behaviour to solve a packaging problem.

- [ ] **Step 4: Verify the runtime image serves /health**

```bash
docker run --rm -d --name api-smoke -p 8080:8080 \
  -e ConnectionStrings__Default='Host=x;Database=x;Username=x;Password=x' \
  -e Wolverine__Durable=false \
  aiframework-api:local
sleep 3 && curl -fsS http://localhost:8080/health && docker rm -f api-smoke
```

Expected: `{"status":"ok"}`. `Wolverine__Durable=false` is what lets this boot with no database, the same trick `HealthTests` uses.

- [ ] **Step 5: Verify the migrator image runs**

```bash
docker run --rm aiframework-migrator:local --help
```

Expected: bundle usage text listing `--connection`.

- [ ] **Step 6: Commit**

```bash
git add .dockerignore Dockerfile.api
git commit -m "build: containerize the api, with a self-contained migration bundle stage"
```

---

### Task 5: SPA image

**Files:**
- Create: `Dockerfile.web`
- Create: `frontend/nginx.conf`

**Interfaces:**
- Produces: image `aiframework-web:local`, serving the built SPA on port 8080. Consumed by Task 8.

No frontend source changes. `frontend/src/api/client.ts` already issues relative paths and relies on `fetch`'s default `same-origin` credentials — Task 9's path routing keeps everything on one origin, so it needs no change and **must not** be given `credentials: 'include'`.

- [ ] **Step 1: Create `frontend/nginx.conf`**

```nginx
server {
    listen 8080;
    server_name _;
    root /usr/share/nginx/html;
    index index.html;

    # Client-side routing: without this, a browser refresh on /orders asks nginx for a file
    # that does not exist and gets a 404 instead of the SPA.
    location / {
        try_files $uri $uri/ /index.html;
    }

    # Hashed asset filenames are immutable; index.html must never be cached or a deploy is
    # invisible until the browser happens to revalidate.
    location /assets/ {
        expires 1y;
        add_header Cache-Control "public, immutable";
    }

    location = /index.html {
        add_header Cache-Control "no-store";
    }
}
```

- [ ] **Step 2: Create `Dockerfile.web`**

```dockerfile
# syntax=docker/dockerfile:1

FROM node:24-alpine AS build
WORKDIR /src
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
# `npm run build` is `tsc -b && vite build`, so a type error fails the image build.
RUN npm run build

# Unprivileged rather than nginx:alpine, which runs as root in order to bind port 80.
FROM nginxinc/nginx-unprivileged:alpine AS runtime
COPY --from=build /src/dist /usr/share/nginx/html
COPY frontend/nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 8080
```

- [ ] **Step 3: Build the image**

```bash
docker build -f Dockerfile.web -t aiframework-web:local .
```

Expected: success.

- [ ] **Step 4: Verify it serves the SPA and its client-side routes**

```bash
docker run --rm -d --name web-smoke -p 8081:8080 aiframework-web:local
sleep 2
curl -fsS -o /dev/null -w '%{http_code}\n' http://localhost:8081/
curl -fsS -o /dev/null -w '%{http_code}\n' http://localhost:8081/orders
docker rm -f web-smoke
```

Expected: `200` for both. A `404` on the second means `try_files` is not in effect.

- [ ] **Step 5: Commit**

```bash
git add Dockerfile.web frontend/nginx.conf
git commit -m "build: containerize the spa behind unprivileged nginx"
```

---

### Task 6: kind cluster and the stateful base

**Files:**
- Create: `deploy/kind-cluster.yaml`
- Create: `k8s/base/namespace.yaml`
- Create: `k8s/base/postgres.yaml`
- Create: `k8s/base/kustomization.yaml`
- Create: `k8s/overlays/local/kustomization.yaml`
- Create: `k8s/overlays/local/config.yaml`
- Create: `k8s/overlays/local/secret.yaml`

**Interfaces:**
- Produces: namespace `aiframework`; Service `postgres:5432`; Secret `app-secrets` (keys `ConnectionStrings__Default`, `POSTGRES_PASSWORD`); ConfigMap `app-config`. Consumed by Tasks 7, 8, 9.

- [ ] **Step 1: Create `deploy/kind-cluster.yaml`**

```yaml
kind: Cluster
apiVersion: kind.x-k8s.io/v1alpha4
name: aiframework
nodes:
  - role: control-plane
    kubeadmConfigPatches:
      # ingress-nginx's kind manifest schedules onto a node carrying this label.
      - |
        kind: InitConfiguration
        nodeRegistration:
          kubeletExtraArgs:
            node-labels: "ingress-ready=true"
    extraPortMappings:
      - containerPort: 80
        hostPort: 80
        protocol: TCP
      - containerPort: 443
        hostPort: 443
        protocol: TCP
```

- [ ] **Step 2: Create `k8s/base/namespace.yaml`**

```yaml
apiVersion: v1
kind: Namespace
metadata:
  name: aiframework
```

- [ ] **Step 3: Create `k8s/base/postgres.yaml`**

```yaml
apiVersion: v1
kind: Service
metadata:
  name: postgres
spec:
  clusterIP: None
  selector:
    app: postgres
  ports:
    - port: 5432
      targetPort: 5432
---
apiVersion: apps/v1
kind: StatefulSet
metadata:
  name: postgres
spec:
  serviceName: postgres
  replicas: 1
  selector:
    matchLabels:
      app: postgres
  template:
    metadata:
      labels:
        app: postgres
    spec:
      containers:
        - name: postgres
          # Matches docker-compose.yml, so the cluster and the dev loop agree on the engine.
          image: postgres:17-alpine
          ports:
            - containerPort: 5432
          env:
            - name: POSTGRES_USER
              value: postgres
            - name: POSTGRES_DB
              value: aiframework
            - name: POSTGRES_PASSWORD
              valueFrom:
                secretKeyRef:
                  name: app-secrets
                  key: POSTGRES_PASSWORD
            # The image refuses to initialise into a non-empty directory, and a mounted
            # volume root is non-empty (lost+found).
            - name: PGDATA
              value: /var/lib/postgresql/data/pgdata
          readinessProbe:
            exec:
              command: ['pg_isready', '-U', 'postgres', '-d', 'aiframework']
            initialDelaySeconds: 5
            periodSeconds: 5
          resources:
            requests:
              cpu: 100m
              memory: 256Mi
            limits:
              memory: 512Mi
          volumeMounts:
            - name: pgdata
              mountPath: /var/lib/postgresql/data
  volumeClaimTemplates:
    - metadata:
        name: pgdata
      spec:
        accessModes: ['ReadWriteOnce']
        resources:
          requests:
            storage: 2Gi
```

- [ ] **Step 4: Create `k8s/base/kustomization.yaml`**

```yaml
apiVersion: kustomize.config.k8s.io/v1beta1
kind: Kustomization
namespace: aiframework
resources:
  - namespace.yaml
  - postgres.yaml
```

- [ ] **Step 5: Create `k8s/overlays/local/config.yaml`**

```yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: app-config
data:
  # Production is deliberate. It is also why Task 9's ingress must terminate TLS:
  # Production sets CookieSecurePolicy.Always, and over plain HTTP the browser discards
  # the session cookie silently.
  ASPNETCORE_ENVIRONMENT: 'Production'
  # Note the DOUBLE underscore. A single one binds nothing and warns nothing.
  Cache__Enabled: 'true'
  Wolverine__Durable: 'true'
  RateLimiting__Auth__PermitLimit: '10'
  RateLimiting__Auth__WindowSeconds: '60'
```

- [ ] **Step 6: Create `k8s/overlays/local/secret.yaml`**

Throwaway credentials for a localhost-only cluster that is never deployed — the same judgement already applied to `docker-compose.e2e.yml` and the committed dev connection string. The no-secrets rule still holds for `appsettings*.json`.

```yaml
apiVersion: v1
kind: Secret
metadata:
  name: app-secrets
type: Opaque
stringData:
  POSTGRES_PASSWORD: postgres
  ConnectionStrings__Default: 'Host=postgres;Port=5432;Database=aiframework;Username=postgres;Password=postgres'
```

- [ ] **Step 7: Create `k8s/overlays/local/kustomization.yaml`**

```yaml
apiVersion: kustomize.config.k8s.io/v1beta1
kind: Kustomization
namespace: aiframework
resources:
  - ../../base
  - config.yaml
  - secret.yaml
```

- [ ] **Step 8: Create the cluster and apply**

```bash
kind create cluster --config deploy/kind-cluster.yaml
kubectl apply -k k8s/overlays/local
kubectl -n aiframework wait --for=condition=ready pod -l app=postgres --timeout=180s
```

Expected: the pod reaches Ready.

- [ ] **Step 9: Verify the database accepts connections**

```bash
kubectl -n aiframework exec statefulset/postgres -- psql -U postgres -d aiframework -c '\l'
```

Expected: the database list, including `aiframework`.

- [ ] **Step 10: Commit**

```bash
git add deploy/kind-cluster.yaml k8s/base k8s/overlays
git commit -m "feat(k8s): kind cluster, namespace, postgres, and local config"
```

---

### Task 7: Migration Job

**Files:**
- Create: `k8s/base/migrate-job.yaml`
- Modify: `k8s/base/kustomization.yaml`

**Interfaces:**
- Consumes: image `aiframework-migrator:local` (Task 4), Secret `app-secrets` (Task 6).
- Produces: Job `migrate`. Task 10's script waits on its completion before applying the API.

> **Wolverine finding from Task 1 (verified 2026-09-09 against a freshly created empty
> database, `wolverine` schema absent beforehand and present after):** an earlier pass of this
> probe ran against the persistent dev database and was a false negative — its named volume
> survives `docker compose down`, and Wolverine's storage had already existed there since local
> development began, so that run never exercised the creation DDL at all. Re-run against a
> throwaway `wolverine_probe` database created fresh for this check: confirmed via `\dn` and
> `pg_namespace` that no `wolverine` schema existed before the race. Two `dotnet run` hosts were
> then launched in the same instant (ports 5301/5302, same `ConnectionStrings__Default`
> `Database=wolverine_probe`). Both reached "Application started". Host A's log shows `Applied
> database migration for Wolverine Envelope Storage` (the actual `CREATE SCHEMA` /
> `CREATE TABLE IF NOT EXISTS` DDL); host B's log has no such line at all — it found the
> storage host A had just created and skipped re-running it, rather than racing Postgres
> directly. The only grep hit for `duplicate|already exists|deadlock|42P07|42710` is the
> literal text `WHEN duplicate_schema THEN NULL;` inside host A's own logged migration
> script — a defensive exception clause Wolverine's DDL carries for exactly this scenario, not
> a thrown/caught error; no `fail:`, `Unhandled exception`, or `Npgsql.PostgresException` tied
> to the `wolverine` schema appears in either log. Afterward, `\dn` against `wolverine_probe`
> showed the `wolverine` schema present with all 8 tables, proving the DDL actually ran.
> `wolverine.wolverine_nodes` shows both nodes registered 0.34s apart (node 1 at
> 19:15:18.77952+00, node 2 at 19:15:19.115072+00), confirming the race was genuine.
> Wolverine auto-migration is verified safe at 2 replicas — leave `Wolverine__Durable: 'true'`
> as-is and make no further change here.
>
> One unrelated failure mode showed up in both logs and does not bear on this finding:
> `wolverine_probe` had no application schema either (no `dotnet ef database update` was run
> against it), so `OutboxPollerService` logged repeating `42P01: relation "outbox" does not
> exist` on its poll cycle in both hosts. That is a background poller hitting a missing
> application table, unrelated to Wolverine's own envelope storage, and both hosts still
> reached "Application started" and completed Wolverine's storage bootstrap regardless.

- [ ] **Step 1: Create `k8s/base/migrate-job.yaml`**

```yaml
apiVersion: batch/v1
kind: Job
metadata:
  name: migrate
spec:
  backoffLimit: 3
  template:
    spec:
      restartPolicy: Never
      containers:
        - name: efbundle
          image: aiframework-migrator:local
          imagePullPolicy: Never
          # Passed explicitly rather than relying on DesignTimeDbContextFactory's environment
          # variable, so what this Job connects to is visible in the manifest.
          args:
            - '--connection'
            - '$(ConnectionStrings__Default)'
          env:
            - name: ConnectionStrings__Default
              valueFrom:
                secretKeyRef:
                  name: app-secrets
                  key: ConnectionStrings__Default
          resources:
            requests:
              cpu: 100m
              memory: 128Mi
            limits:
              memory: 512Mi
```

`imagePullPolicy: Never` is required throughout: images are side-loaded into kind, never pulled from a registry.

- [ ] **Step 2: Add it to the base kustomization**

`k8s/base/kustomization.yaml` becomes:

```yaml
apiVersion: kustomize.config.k8s.io/v1beta1
kind: Kustomization
namespace: aiframework
resources:
  - namespace.yaml
  - postgres.yaml
  - migrate-job.yaml
```

- [ ] **Step 3: Load the migrator image into kind and run the Job**

```bash
kind load docker-image aiframework-migrator:local --name aiframework
kubectl apply -k k8s/overlays/local
kubectl -n aiframework wait --for=condition=complete job/migrate --timeout=180s
```

Expected: the Job completes.

- [ ] **Step 4: Verify the schema, including the Task 2 table**

```bash
kubectl -n aiframework exec statefulset/postgres -- \
  psql -U postgres -d aiframework -c '\dt'
```

Expected: `Orders`, `Users`, `Outbox`, `OrderAudits`, `DataProtectionKeys`, `__EFMigrationsHistory`.

- [ ] **Step 5: Verify the Job is re-runnable**

```bash
kubectl -n aiframework delete job migrate
kubectl apply -k k8s/overlays/local
kubectl -n aiframework wait --for=condition=complete job/migrate --timeout=180s
```

Expected: completes again. This confirms the delete-then-apply that Task 10 automates — a completed Job has immutable fields, so a plain re-`apply` fails.

- [ ] **Step 6: Commit**

```bash
git add k8s/base/migrate-job.yaml k8s/base/kustomization.yaml
git commit -m "feat(k8s): run ef migrations as a job before the api rolls out"
```

---

### Task 8: API and SPA Deployments

**Files:**
- Create: `k8s/base/api.yaml`
- Create: `k8s/base/web.yaml`
- Modify: `k8s/base/kustomization.yaml`

**Interfaces:**
- Consumes: images from Tasks 4 and 5; `app-config` and `app-secrets` from Task 6; `/health` and `/health/ready` from Task 3.
- Produces: Services `api:8080` and `web:8080`. Consumed by Task 9.

- [ ] **Step 1: Create `k8s/base/api.yaml`**

```yaml
apiVersion: v1
kind: Service
metadata:
  name: api
spec:
  selector:
    app: api
  ports:
    - port: 8080
      targetPort: 8080
---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: api
spec:
  # Two replicas is the point of the exercise: it is what makes the data protection key ring
  # and the ingress session affinity load-bearing rather than decorative.
  replicas: 2
  selector:
    matchLabels:
      app: api
  template:
    metadata:
      labels:
        app: api
    spec:
      # 60s against the host's 30s shutdown timeout. The Kubernetes default is also 30s, so a
      # host using its full budget would be SIGKILLed exactly at the boundary — and the outbox
      # increments Attempts at claim time, so a kill mid-handler burns an attempt against
      # MaxAttempts.
      terminationGracePeriodSeconds: 60
      containers:
        - name: api
          image: aiframework-api:local
          imagePullPolicy: Never
          ports:
            - containerPort: 8080
          envFrom:
            - configMapRef:
                name: app-config
            - secretRef:
                name: app-secrets
          lifecycle:
            preStop:
              # Leave the Service endpoints before we stop accepting connections. Without this,
              # in-flight requests are refused during a rolling update while endpoint removal
              # propagates.
              exec:
                command: ['sleep', '5']
          startupProbe:
            httpGet:
              path: /health
              port: 8080
            # Generous: durable Wolverine does schema work at boot, and a slow start must not
            # read as a crash loop.
            failureThreshold: 30
            periodSeconds: 5
          livenessProbe:
            httpGet:
              path: /health
              port: 8080
            periodSeconds: 10
          readinessProbe:
            httpGet:
              path: /health/ready
              port: 8080
            periodSeconds: 10
          resources:
            requests:
              cpu: 200m
              memory: 256Mi
            limits:
              memory: 768Mi
          securityContext:
            allowPrivilegeEscalation: false
            readOnlyRootFilesystem: true
            capabilities:
              drop: ['ALL']
```

- [ ] **Step 2: Create `k8s/base/web.yaml`**

```yaml
apiVersion: v1
kind: Service
metadata:
  name: web
spec:
  selector:
    app: web
  ports:
    - port: 8080
      targetPort: 8080
---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: web
spec:
  replicas: 2
  selector:
    matchLabels:
      app: web
  template:
    metadata:
      labels:
        app: web
    spec:
      containers:
        - name: web
          image: aiframework-web:local
          imagePullPolicy: Never
          ports:
            - containerPort: 8080
          readinessProbe:
            httpGet:
              path: /
              port: 8080
            periodSeconds: 10
          livenessProbe:
            httpGet:
              path: /
              port: 8080
            periodSeconds: 10
          resources:
            requests:
              cpu: 50m
              memory: 32Mi
            limits:
              memory: 128Mi
          securityContext:
            allowPrivilegeEscalation: false
            capabilities:
              drop: ['ALL']
```

`readOnlyRootFilesystem` is deliberately absent here — nginx writes cache and temp files under `/var/cache/nginx` and `/tmp`.

- [ ] **Step 3: Add both to the base kustomization**

```yaml
apiVersion: kustomize.config.k8s.io/v1beta1
kind: Kustomization
namespace: aiframework
resources:
  - namespace.yaml
  - postgres.yaml
  - migrate-job.yaml
  - api.yaml
  - web.yaml
```

- [ ] **Step 4: Load the images and apply**

```bash
kind load docker-image aiframework-api:local aiframework-web:local --name aiframework
kubectl apply -k k8s/overlays/local
kubectl -n aiframework wait --for=condition=available deployment/api deployment/web --timeout=300s
```

Expected: both Deployments available, with 2/2 replicas each.

- [ ] **Step 5: Verify the API answers from inside the cluster**

```bash
kubectl -n aiframework run curl --rm -it --restart=Never --image=curlimages/curl -- \
  sh -c 'curl -fsS http://api:8080/health && echo && curl -fsS http://api:8080/health/ready'
```

Expected: both succeed. A failing readiness check means the pod cannot reach Postgres.

- [ ] **Step 6: Commit**

```bash
git add k8s/base/api.yaml k8s/base/web.yaml k8s/base/kustomization.yaml
git commit -m "feat(k8s): deploy the api at two replicas and the spa behind services"
```

---

### Task 9: Ingress, TLS, and session affinity

**Files:**
- Create: `k8s/base/ingress.yaml`
- Modify: `k8s/base/kustomization.yaml`
- Modify: `k8s/overlays/local/kustomization.yaml`

**Interfaces:**
- Consumes: Services `api` and `web` (Task 8).
- Produces: `https://aiframework.localtest.me` serving the SPA at `/` and the API at `/api`.

TLS is not optional. `ASPNETCORE_ENVIRONMENT=Production` sets `CookieSecurePolicy.Always`, and over plain HTTP the browser silently discards the session cookie — login appears to succeed and every later request returns 401 with nothing in the logs.

`aiframework.localtest.me` resolves to `127.0.0.1` publicly, so no `/etc/hosts` editing is needed.

- [ ] **Step 1: Install ingress-nginx**

```bash
kubectl apply -f https://raw.githubusercontent.com/kubernetes/ingress-nginx/main/deploy/static/provider/kind/deploy.yaml
kubectl -n ingress-nginx wait --for=condition=ready pod \
  -l app.kubernetes.io/component=controller --timeout=300s
```

- [ ] **Step 2: Generate a self-signed certificate and load it as a Secret**

```bash
openssl req -x509 -nodes -days 365 -newkey rsa:2048 \
  -keyout /tmp/tls.key -out /tmp/tls.crt \
  -subj "/CN=aiframework.localtest.me" \
  -addext "subjectAltName=DNS:aiframework.localtest.me"

kubectl -n aiframework create secret tls aiframework-tls \
  --cert=/tmp/tls.crt --key=/tmp/tls.key \
  --dry-run=client -o yaml > k8s/overlays/local/tls.yaml
```

- [ ] **Step 3: Add the certificate to the overlay**

`k8s/overlays/local/kustomization.yaml` becomes:

```yaml
apiVersion: kustomize.config.k8s.io/v1beta1
kind: Kustomization
namespace: aiframework
resources:
  - ../../base
  - config.yaml
  - secret.yaml
  - tls.yaml
```

- [ ] **Step 4: Create `k8s/base/ingress.yaml`**

```yaml
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: aiframework
  annotations:
    # Cookie affinity is what keeps query-cache eviction correct. HybridCache is L1-only, so
    # a PlaceOrder handled by one pod cannot evict an entry held by the other; pinning a user
    # to a pod means the pod that evicts is always the pod that reads. See ADR 0010, which
    # amends ADR 0009's single-process premise.
    nginx.ingress.kubernetes.io/affinity: 'cookie'
    nginx.ingress.kubernetes.io/affinity-mode: 'persistent'
    nginx.ingress.kubernetes.io/session-cookie-name: 'aiframework.route'
    nginx.ingress.kubernetes.io/session-cookie-max-age: '3600'
spec:
  ingressClassName: nginx
  tls:
    - hosts:
        - aiframework.localtest.me
      secretName: aiframework-tls
  rules:
    - host: aiframework.localtest.me
      http:
        paths:
          # /api first: Prefix rules are matched longest-first, but keeping the order explicit
          # makes the intent readable.
          - path: /api
            pathType: Prefix
            backend:
              service:
                name: api
                port:
                  number: 8080
          - path: /
            pathType: Prefix
            backend:
              service:
                name: web
                port:
                  number: 8080
```

- [ ] **Step 5: Add the ingress to the base kustomization**

```yaml
apiVersion: kustomize.config.k8s.io/v1beta1
kind: Kustomization
namespace: aiframework
resources:
  - namespace.yaml
  - postgres.yaml
  - migrate-job.yaml
  - api.yaml
  - web.yaml
  - ingress.yaml
```

- [ ] **Step 6: Apply and verify both routes**

```bash
kubectl apply -k k8s/overlays/local
curl -fsSk -o /dev/null -w 'spa %{http_code}\n' https://aiframework.localtest.me/
curl -fsSk -o /dev/null -w 'api %{http_code}\n' https://aiframework.localtest.me/api/orders
```

Expected: `spa 200`, and `api 401` — unauthenticated, which proves the API is reachable and the auth pipeline is running.

- [ ] **Step 7: Verify the session survives both pods**

The end-to-end check that Task 2 exists for.

```bash
COOKIE=/tmp/aif-cookies.txt
curl -sk -c $COOKIE -X POST https://aiframework.localtest.me/api/auth/register \
  -H 'Content-Type: application/json' \
  -d '{"username":"k8ssmoke01","password":"a long enough test password","displayName":"K8s Smoke"}'

kubectl -n aiframework delete pod -l app=api --wait=true
kubectl -n aiframework wait --for=condition=available deployment/api --timeout=300s

curl -sk -b $COOKIE -o /dev/null -w 'after restart %{http_code}\n' \
  https://aiframework.localtest.me/api/orders
```

Expected: `after restart 200`. A `401` means the key ring is not shared — revisit Task 2.

- [ ] **Step 8: Commit**

```bash
git add k8s/base/ingress.yaml k8s/base/kustomization.yaml \
        k8s/overlays/local/kustomization.yaml k8s/overlays/local/tls.yaml
git commit -m "feat(k8s): ingress with tls and cookie affinity for cache correctness"
```

---

### Task 10: Deploy script and documentation

**Files:**
- Create: `deploy/deploy.ps1`
- Modify: `CLAUDE.md` (new "Running on Kubernetes" section after "Running locally")

**Interfaces:**
- Consumes: everything from Tasks 4–9.
- Produces: `./deploy/deploy.ps1` — one command from empty machine to running application.

Kustomize has no hook mechanism, so the migrate-before-rollout ordering lives here. That was the acknowledged cost of choosing Kustomize over Helm.

- [ ] **Step 1: Create `deploy/deploy.ps1`**

```powershell
#requires -Version 5.1
<#
.SYNOPSIS
  Builds the images and deploys the application to a local kind cluster.
.DESCRIPTION
  Kustomize has no hook mechanism, so the ordering that Helm would express as a
  pre-upgrade hook is explicit here: migrations must finish before any API pod starts.
#>
[CmdletBinding()]
param(
    [switch]$CreateCluster,
    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$cluster = 'aiframework'
$namespace = 'aiframework'

function Invoke-Step {
    param([string]$Name, [scriptblock]$Action)
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE." }
}

if ($CreateCluster) {
    Invoke-Step 'Creating the kind cluster' {
        kind create cluster --config (Join-Path $PSScriptRoot 'kind-cluster.yaml')
    }
    Invoke-Step 'Installing ingress-nginx' {
        kubectl apply -f https://raw.githubusercontent.com/kubernetes/ingress-nginx/main/deploy/static/provider/kind/deploy.yaml
    }
    Invoke-Step 'Waiting for ingress-nginx' {
        kubectl -n ingress-nginx wait --for=condition=ready pod `
            -l app.kubernetes.io/component=controller --timeout=300s
    }
}

if (-not $SkipBuild) {
    Invoke-Step 'Building the api image' {
        docker build -f (Join-Path $repoRoot 'Dockerfile.api') --target runtime `
            -t aiframework-api:local $repoRoot
    }
    Invoke-Step 'Building the migrator image' {
        docker build -f (Join-Path $repoRoot 'Dockerfile.api') --target migrator `
            -t aiframework-migrator:local $repoRoot
    }
    Invoke-Step 'Building the web image' {
        docker build -f (Join-Path $repoRoot 'Dockerfile.web') `
            -t aiframework-web:local $repoRoot
    }
    Invoke-Step 'Loading images into kind' {
        kind load docker-image aiframework-api:local aiframework-migrator:local `
            aiframework-web:local --name $cluster
    }
}

$overlay = Join-Path $repoRoot 'k8s/overlays/local'

# 1. Namespace, config, secrets, and Postgres.
Invoke-Step 'Applying the base stack' { kubectl apply -k $overlay }

# 2. Postgres must answer before migrations can run.
Invoke-Step 'Waiting for postgres' {
    kubectl -n $namespace wait --for=condition=ready pod -l app=postgres --timeout=180s
}

# 3. Migrations, to completion. Deleted first: a completed Job has immutable fields, so a
#    plain re-apply fails on the second deployment.
Invoke-Step 'Running migrations' {
    kubectl -n $namespace delete job migrate --ignore-not-found
    kubectl apply -k $overlay
    kubectl -n $namespace wait --for=condition=complete job/migrate --timeout=180s
}

# 4. Roll the application out onto the migrated schema.
Invoke-Step 'Rolling out the application' {
    kubectl -n $namespace rollout restart deployment/api deployment/web
    kubectl -n $namespace rollout status deployment/api --timeout=300s
    kubectl -n $namespace rollout status deployment/web --timeout=300s
}

Write-Host ''
Write-Host 'Ready: https://aiframework.localtest.me' -ForegroundColor Green
Write-Host 'The certificate is self-signed, so the browser will warn once.' -ForegroundColor DarkGray
```

- [ ] **Step 2: Verify against a fresh cluster**

```bash
kind delete cluster --name aiframework
pwsh ./deploy/deploy.ps1 -CreateCluster
```

Expected: completes and prints the URL. This is the spec's headline acceptance criterion — empty machine to running application, one command.

- [ ] **Step 3: Verify a rolling restart drops no requests**

```bash
kubectl -n aiframework rollout restart deployment/api
kubectl -n aiframework rollout status deployment/api --timeout=300s
curl -fsSk -o /dev/null -w '%{http_code}\n' https://aiframework.localtest.me/api/orders
```

Expected: `401` (unauthenticated but served). A connection error means `preStop` or the grace period is wrong.

- [ ] **Step 4: Document it in `CLAUDE.md`**

Add after the "Running locally" section:

````markdown
## Running on Kubernetes

A local kind cluster that runs the whole stack at **two API replicas**, to rehearse the
things that only break above one. `docker compose` remains the inner development loop;
this is additive.

```powershell
./deploy/deploy.ps1 -CreateCluster   # first run: creates the cluster and ingress-nginx
./deploy/deploy.ps1                  # later runs: rebuild, migrate, roll out
```

Then open `https://aiframework.localtest.me` — that name resolves to `127.0.0.1` publicly,
so there is nothing to add to `hosts`. The certificate is self-signed.

Three things that will cost you time:

- **TLS is not optional.** `ASPNETCORE_ENVIRONMENT=Production` sets
  `CookieSecurePolicy.Always`, so over plain HTTP the browser discards the session cookie
  silently: login appears to succeed and every later request is a 401, with nothing in the
  logs.
- **Config keys need double underscores.** `Cache__Enabled`, not `Cache_Enabled`. A single
  underscore binds nothing, warns nothing, and leaves the default in place.
- **Migrations run as a Job, before the rollout**, via a self-contained `dotnet ef migrations
  bundle` — which is what keeps the EF Design package out of the runtime image (CLAUDE.md's
  7.9MB → 37MB note). The script deletes the Job before re-applying it, because a completed
  Job has immutable fields.

Cache eviction correctness depends on the ingress's cookie affinity: `HybridCache` is L1-only,
so a write handled by one pod cannot evict an entry held by the other. See ADR 0010.
````

- [ ] **Step 5: Commit**

```bash
git add deploy/deploy.ps1 CLAUDE.md
git commit -m "feat(k8s): one-command deploy script, and document the cluster workflow"
```

---

### Task 11: ADR 0010

This work changes a premise ADR 0009 was written on. That belongs in an ADR, not in a code comment.

**Files:**
- Create: `docs/adr/0010-running-on-kubernetes.md`

- [ ] **Step 1: Read an existing ADR for the house format**

```bash
cat docs/adr/0009-caching-scoped-to-the-caller.md
```

- [ ] **Step 2: Write the ADR**

Follow the format observed in Step 1. It must record:

- **Context** — two API replicas is the goal; three pieces of state were per-process.
- **Decision: the key ring moves to Postgres.** Mandatory, not a preference. Without it pods reject each other's cookies and a single restart signs everyone out.
- **Decision: cache eviction correctness now rests on ingress cookie affinity.** State plainly that this **amends ADR 0009's single-process premise**, quote the failing sequence (write on pod B cannot evict pod A's entry, stale read for up to the 30s TTL), and record that per-user key scoping means there is no cross-user leakage.
- **Decision: Kustomize over Helm**, with the migration-ordering cost moved into `deploy/deploy.ps1`.
- **Decision: Postgres only, no Redis.** Record Redis L2 as the principled exit, conditional on verifying HybridCache's tag-based eviction against an L2 tier — support was incomplete in .NET 9 and its .NET 10 status is unverified.
- **Accepted: the auth rate limit becomes N × `PermitLimit`.** ADR 0008's per-account lockout is database-backed and unaffected, so this is a volume-defence loss, not a correctness one. Moving the limit to the ingress is the exit.
- **Accepted: Data Protection keys are unencrypted at rest.** Acceptable for a localhost-only cluster with throwaway credentials; a cloud target requires `ProtectKeysWith*`. Record this as a condition, not an aside.

- [ ] **Step 3: Add the cross-reference to ADR 0009**

Append a line to `docs/adr/0009-caching-scoped-to-the-caller.md` noting that ADR 0010 amends its single-process assumption. Do not rewrite 0009's decision — ADRs are a log.

- [ ] **Step 4: Commit**

```bash
git add docs/adr/0010-running-on-kubernetes.md docs/adr/0009-caching-scoped-to-the-caller.md
git commit -m "docs: ADR 0010 records the kubernetes decisions and amends 0009's premise"
```

---

## Final verification

- [ ] **Run the full verification suite**

```bash
/verify
```

Expected: backend build and tests, frontend lint/build/test, and e2e all pass. This work must not have disturbed the `docker compose` loop.

- [ ] **Confirm the spec's "Done when" list**

| Criterion | Verified by |
|---|---|
| Fresh kind cluster → working application | Task 10, Step 2 |
| Session survives either API pod | Task 9, Step 7 |
| Place an order, reload, see it | Manual, through the browser |
| `rollout restart` drops no requests | Task 10, Step 3 |
| Two-host cookie test passes | Task 2, Step 7 |
| ADR 0010 written | Task 11 |

- [ ] **Open the pull request**

```bash
git push -u origin docs/kubernetes-deployment-design
gh pr create --title "Run the application on Kubernetes" --body "$(cat <<'EOF'
## Summary

- Containerizes the API and the SPA, and runs the whole stack on a local kind cluster at
  **two API replicas** — which is the point, since it is what makes the multi-replica
  failures reachable.
- Moves the Data Protection key ring into Postgres. Without it each pod mints its own
  ephemeral ring and rejects cookies issued by the other, which presents as an intermittent
  401 rather than an obvious failure.
- Adds `/health/ready` for readiness. `/health` is unchanged and stays database-free, because
  `HealthTests` boots a host with no database and asserts 200 on it.
- Cache eviction correctness now rests on ingress cookie affinity. `HybridCache` is L1-only,
  so a write handled by one pod cannot evict an entry held by the other. ADR 0010 records
  this as an amendment to ADR 0009's single-process premise.

## Test plan

- [ ] `dotnet test` passes, including the new `DataProtectionTests` and `ReadinessTests`
- [ ] `/verify` passes — the `docker compose` loop and the e2e suite are untouched
- [ ] `./deploy/deploy.ps1 -CreateCluster` takes an empty machine to a running application
- [ ] Signing in through the ingress works, and the session survives `kubectl delete pod -l app=api`
- [ ] Placing an order and immediately reloading the list shows the new order
- [ ] `kubectl rollout restart deployment/api` completes with no failed requests

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```
