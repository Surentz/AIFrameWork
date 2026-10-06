# Order Export as a PDF — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the order export's CSV with a coloured, shareable A4 PDF, and let the owner read it inside the app on any device.

**Architecture:** The worker's existing `BuildOrderExport` job builds a pure `OrderExportReport` (Application) and hands it to an `IOrderExportRenderer` port, implemented in Infrastructure with PDFsharp/MigraDoc and an embedded Noto Sans. The file moves from a `text` column to `bytea`. The API's existing download endpoint serves `application/pdf`; the frontend fetches the same URL as bytes and draws it with a lazily loaded react-pdf viewer inside a native `<dialog>`.

**Tech Stack:** .NET 10, EF Core 10 + Npgsql, Wolverine, PDFsharp-MigraDoc 6.2.4, PdfPig 0.1.16 (tests only), React 19, TanStack Query 5, react-pdf 11.0.0 (pdfjs-dist 6.3.x), Vitest + MSW 3, Playwright.

**Spec:** `docs/superpowers/specs/2026-10-06-order-export-pdf-design.md` — read it first; this plan argues from it.

## Global Constraints

- One pull request on `claude/order-export-pdf`, titled `feat(orders)!: export orders as a shareable PDF, viewable in the app`.
- Warnings are errors (compiler, analyzers, MSBuild). No `catch (Exception)`. Nullable is on.
- `Domain` may not reference EF Core, ASP.NET Core, DI, `System.Data` or DataAnnotations; the PDF library is referenced by **Infrastructure only**.
- Never hand-edit a migration that is in `HEAD`; the one this plan generates is edited **before** its first commit.
- Package versions, exactly: `PDFsharp-MigraDoc` **6.2.4** (MIT), `PdfPig` **0.1.16** (Apache-2.0, `Infrastructure.Tests` only), `react-pdf` **11.0.0**. No other new dependencies.
- Font: Noto Sans Regular and Bold (SIL OFL 1.1), from `https://github.com/notofonts/notofonts.github.io/raw/main/fonts/NotoSans/hinted/ttf/`, embedded; OFL text from `https://raw.githubusercontent.com/notofonts/latin-greek-cyrillic/main/OFL.txt` committed beside them.
- Palette (hex, from `frontend/src/styles/tokens.css` light theme): brand `#3b2fa8`; on-brand `#ffffff`; on-brand soft `#e0ddf7`; card `#eef1ff`; text `#12151c`; muted `#59616f`; sunken/zebra `#f8f9fb`; hairline `#e4e7ec`; rule `#cfd4dd`; shipped `#0f6b3f` on `#f0fdf5`; cancelled `#912018` on `#fef4f3`; placed `#4f46e5` on `#eef1ff`. **`#878e9b` is not used** (fails AA).
- All document times are UTC, formatted with `CultureInfo.InvariantCulture`: dates `d MMM yyyy` ("6 Oct 2026"), generated stamp `d MMM yyyy, HH:mm 'UTC'`. Amounts `#,##0.00`, no currency symbol.
- File name: `orders-{RequestedAt UTC:yyyy-MM-dd}.pdf`. Media type `application/pdf`, `Content-Disposition: attachment`.
- User-visible copy, verbatim: page subtitle "A PDF of every order you have placed, ready to share. You are notified when it is ready; exports are kept for seven days."; viewer loading "Loading export…"; draw failure "This export couldn't be displayed."
- After changing `BuildOrderExportHandler`'s constructor: regenerate the **worker's** Wolverine adapters. After changing the download's `[ProducesResponseType]`: regenerate `openapi/AiFramework.Api.json` and `frontend/src/api/schema.d.ts`. Use the `regenerate` skill.
- Commit after each task with a `feat(orders): …` subject (or `docs(orders): …` for Task 8), ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **A Ready export from before the deploy** — the migration deletes it; a notification that still links to it must land on the exports page and its download must answer 404, not 500. Pinned in Task 1 (migration test) and Task 4 (download of a missing file).
2. **An owner whose orders all lack a recorded price, or are all cancelled** — the Total value must read `0.00` with the exclusion note, never throw or print `NaN`. Pinned in Task 2.
3. **Text the font cannot draw (CJK) or very long product names / cancellation reasons** — must not throw; long text wraps inside its cell. Pinned in Task 3.
4. **Opening the viewer, closing it, and opening it again** — pdf.js detaches the buffer it is given; the second open must still render. Pinned in Task 6 (Vitest asserts a fresh copy per mount) and Task 7 (Playwright opens twice).
5. **The pdf.js worker in the production nginx image** — served as JavaScript, or the viewer silently never draws in the cluster. Pinned in Task 7 (nginx config + a check against the real image).

---

### Task 1: Store the export file as bytes

The file becomes `byte[]` end to end while the job still produces a CSV (as UTF-8 bytes), so every later task starts from a green build. The migration here is final: it also deletes Ready exports.

**Files:**
- Modify: `src/Domain/Orders/OrderExport.cs`
- Modify: `src/Application/Orders/IOrderExportRepository.cs` (`OrderExportFile`)
- Modify: `src/Application/Orders/OrderExportCommands.cs` (`CompleteOrderExport`, validator, handler)
- Modify: `src/Application/Orders/OrderExportQueries.cs` (`OrderExportDownload`, `GetOrderExportFileHandler`)
- Modify: `src/Application/Orders/BuildOrderExport.cs` (pass UTF-8 bytes — temporary until Task 4)
- Modify: `src/Infrastructure/Persistence/Configurations/OrderExportConfiguration.cs`
- Modify: `src/Infrastructure/Persistence/OrderExportRepository.cs`
- Create: `src/Infrastructure/Persistence/Migrations/<timestamp>_StoreOrderExportsAsBytes.cs` (+ `.Designer.cs`, snapshot)
- Modify: `src/Api/Orders/OrderExportsController.cs` (`Download`)
- Test: `tests/Domain.Tests/Orders/OrderExportTests.cs`, `tests/Application.Tests/Orders/OrderExportHandlerTests.cs`, `tests/Application.Tests/Orders/BuildOrderExportHandlerTests.cs`, `tests/Infrastructure.Tests/Persistence/OrderExportRepositoryTests.cs`, `tests/Infrastructure.Tests/Persistence/OrderExportConcurrencyTests.cs`, `tests/Infrastructure.Tests/Persistence/OrderExportMigrationTests.cs` (create), `tests/Api.IntegrationTests/Orders/OrderExportsEndpointTests.cs`, `tests/Worker.IntegrationTests/Jobs/OrderExportBuildTests.cs`

**Interfaces:**
- Produces: `OrderExport.Document : byte[]?`; `OrderExport.Complete(byte[] document, int rowCount, DateTimeOffset completedAt)`; `CompleteOrderExport(Guid ExportId, byte[] Document, int RowCount) : ICommand<bool>`; `OrderExportFile(byte[] Document, DateTimeOffset RequestedAt)`; `OrderExportDownload(string FileName, byte[] Document)`. Column `order_exports."Document" bytea`.

- [ ] **Step 1: Write the failing domain tests**

In `tests/Domain.Tests/Orders/OrderExportTests.cs`, add at the top of the class:

```csharp
    private static readonly byte[] AFile = [0x25, 0x50, 0x44, 0x46]; // "%PDF"
    private static readonly byte[] AnotherFile = [0x25, 0x50, 0x44, 0x46, 0x2D];
```

Then replace every `export.Complete("…", n, …)` with `export.Complete(AFile, n, …)` (the second call in each "AlreadyReady" test uses `AnotherFile`), every `export.Content.Should().BeNull()` with `export.Document.Should().BeNull()`, and every `export.Content.Should().Be("…")` with `export.Document.Should().Equal(AFile)`. Replace `Complete_WithNoContent_Throws` with these two tests:

```csharp
    [Fact]
    public void Complete_WithNoDocument_Throws()
    {
        var export = Requested();

        // null! deliberately breaks the non-nullable contract to prove the runtime guard holds.
        var act = () => export.Complete(null!, 0, CompletedAt);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Complete_WithAnEmptyDocument_Throws()
    {
        var export = Requested();

        var act = () => export.Complete([], 0, CompletedAt);

        act.Should().Throw<DomainException>();
    }
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/Domain.Tests --filter "FullyQualifiedName~OrderExportTests"`
Expected: build FAILS — `'OrderExport' does not contain a definition for 'Document'`.

- [ ] **Step 3: Change the aggregate**

In `src/Domain/Orders/OrderExport.cs`: the class summary's first sentence becomes "A file of one user's orders, built by a job in the worker." Replace the `Content` property and `Complete` with:

```csharp
    /// <summary>The built file. Null until <see cref="Complete"/>.</summary>
    public byte[]? Document { get; private set; }
```

```csharp
    public void Complete(byte[] document, int rowCount, DateTimeOffset completedAt)
    {
        if (document is null || document.Length == 0)
        {
            throw new DomainException("A completed order export needs its file.");
        }

        if (rowCount < 0)
        {
            throw new DomainException("An order export cannot hold a negative number of orders.");
        }

        if (Status == OrderExportStatus.Ready)
        {
            return;
        }

        Status = OrderExportStatus.Ready;
        Document = document;
        RowCount = rowCount;
        CompletedAt = completedAt;
        Raise(new OrderExportCompleted(Id, UserId, rowCount));
    }
```

(Keep the existing `<summary>` on `Complete` unchanged.)

- [ ] **Step 4: Change Application to carry bytes**

`src/Application/Orders/IOrderExportRepository.cs`:

```csharp
/// <summary>A Ready export's file, and when it was asked for (which names the download).</summary>
public sealed record OrderExportFile(byte[] Document, DateTimeOffset RequestedAt);
```

and in `GetSummaryAsync`'s summary replace "must not load the CSV" with "must not load the file".

`src/Application/Orders/OrderExportCommands.cs`: `RequestOrderExport`'s summary says "Asks for a file of the caller's orders." Then:

```csharp
public sealed record CompleteOrderExport(Guid ExportId, byte[] Document, int RowCount) : ICommand<bool>;

public sealed class CompleteOrderExportValidator : AbstractValidator<CompleteOrderExport>
{
    public CompleteOrderExportValidator()
    {
        RuleFor(c => c.ExportId).NotEmpty();
        RuleFor(c => c.Document).NotEmpty();
        RuleFor(c => c.RowCount).GreaterThanOrEqualTo(0);
    }
}
```

and in the handler `export.Complete(command.Document, command.RowCount, clock.UtcNow);`.

`src/Application/Orders/OrderExportQueries.cs`:

```csharp
/// <summary>A Ready export's file, and the name it downloads as.</summary>
public sealed record OrderExportDownload(string FileName, byte[] Document);
```

and in `GetOrderExportFileHandler` (still `.csv` until Task 4):

```csharp
            : Result.Success(new OrderExportDownload(
                $"orders-{file.RequestedAt.UtcDateTime:yyyy-MM-dd}.csv", file.Document));
```

`src/Application/Orders/BuildOrderExport.cs`, the completion call (temporary — Task 4 replaces it):

```csharp
        var completed = await commands
            .SendAsync(
                new CompleteOrderExport(job.ExportId, Encoding.UTF8.GetBytes(OrderExportCsv.Build(rows)), rows.Count),
                cancellationToken)
            .ConfigureAwait(false);
```

with `using System.Text;` added.

- [ ] **Step 5: Change the mapping and the repository**

`OrderExportConfiguration.cs`, replace the `Content` mapping and its comment:

```csharp
        // bytea: the built file. Only GetFileAsync ever selects it; the list projects it away.
        builder.Property(e => e.Document).HasColumnType("bytea");
```

`OrderExportRepository.cs`: in the two "Projected" comments replace `content` with `the file`, and `GetFileAsync` becomes:

```csharp
    public Task<OrderExportFile?> GetFileAsync(
        Guid id, Guid ownerId, CancellationToken cancellationToken) =>
        context.OrderExports.AsNoTracking()
            .Where(e => e.Id == id
                && e.UserId == ownerId
                && e.Status == OrderExportStatus.Ready
                && e.Document != null)
            // ! is safe: the Where above admits only rows whose Document is not null.
            .Select(e => new OrderExportFile(e.Document!, e.RequestedAt))
            .FirstOrDefaultAsync(cancellationToken);
```

- [ ] **Step 6: Serve the bytes (still a CSV)**

In `OrderExportsController.Download`, replace the two lines after the failure check:

```csharp
        // Still a CSV until the build job renders a PDF: the byte-order mark keeps Excel reading UTF-8.
        byte[] bytes = [.. Encoding.UTF8.Preamble, .. result.Value.Document];
        return File(bytes, "text/csv; charset=utf-8", result.Value.FileName);
```

- [ ] **Step 7: Generate the migration**

Run (from the repo root; `dotnet ef` reads only this variable — see the `local-dev` skill):

```bash
ConnectionStrings__Default='Host=localhost;Database=placeholder;Username=x;Password=y' \
  dotnet ef migrations add StoreOrderExportsAsBytes --project src/Infrastructure --startup-project src/Infrastructure
```

Expected: a new `*_StoreOrderExportsAsBytes.cs` with `DropColumn("Content")` and `AddColumn<byte[]>("Document", type: "bytea", nullable: true)`, plus its `.Designer.cs` and a snapshot change. EF may rewrite the snapshot's line endings only on Windows — check `git diff -w` shows the `Document` property and no `Content`.

- [ ] **Step 8: Make the migration delete Ready exports (before its first commit)**

Edit the generated file so `Up` reads exactly (keep EF's `DropColumn`/`AddColumn` calls; add the `Sql` line first and the remarks):

```csharp
    /// <summary>
    /// The export file moves from CSV text to bytes (ADR 0030). Ready exports hold CSV and cannot be
    /// served as the PDF the API now promises, so they are deleted: at most seven days old, and one
    /// click to ask for again. Requested exports are kept; their build now writes bytes.
    /// </summary>
    public partial class StoreOrderExportsAsBytes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DELETE FROM order_exports WHERE "Status" = 'Ready';""");

            migrationBuilder.DropColumn(
                name: "Content",
                table: "order_exports");

            migrationBuilder.AddColumn<byte[]>(
                name: "Document",
                table: "order_exports",
                type: "bytea",
                nullable: true);
        }
```

`Down` stays as EF generated it (drop `Document`, add `Content text`).

- [ ] **Step 9: Update the Application, Infrastructure, Api and Worker tests**

`tests/Application.Tests/Orders/OrderExportHandlerTests.cs`: add `private static readonly byte[] AFile = [0x25, 0x50, 0x44, 0x46];`. Replace `new CompleteOrderExport(x, "csv", n)` with `new CompleteOrderExport(x, AFile, n)` (three places), `export.Content.Should().Be("csv")` with `export.Document.Should().Equal(AFile)`, and the file test's arrange/assert with:

```csharp
            .Returns(new OrderExportFile(AFile, requestedAt));
        ...
        result.Value.FileName.Should().Be("orders-2026-10-03.csv");
        result.Value.Document.Should().Equal(AFile);
```

Add one validator test:

```csharp
    [Fact]
    public void CompleteOrderExportValidator_RejectsAnEmptyDocument()
    {
        var result = new CompleteOrderExportValidator().Validate(new CompleteOrderExport(Guid.NewGuid(), [], 1));

        result.IsValid.Should().BeFalse();
    }
```

`tests/Application.Tests/Orders/BuildOrderExportHandlerTests.cs`, in `Handle_FollowsTheCursorAndCompletesWithEveryRow`, replace the two `c.Content.Contains(...)` conditions with:

```csharp
                && Encoding.UTF8.GetString(c.Document).Contains(",A,", StringComparison.Ordinal)
                && Encoding.UTF8.GetString(c.Document).Contains(",C,", StringComparison.Ordinal)),
```

and add `using System.Text;`.

`tests/Infrastructure.Tests/Persistence/OrderExportRepositoryTests.cs`: change `SeedAsync`'s parameter to `byte[]? document = null` and its body to `export.Complete(document, 3, …)`; every caller passing `content: "…"` passes `document: AFile` where `private static readonly byte[] AFile = [0x25, 0x50, 0x44, 0x46];`. In the round-trip test assert `stored.Document.Should().Equal(AFile);`. `GetFileAsync_ReturnsAReadyExportsContent` becomes `GetFileAsync_ReturnsAReadyExportsDocument` asserting:

```csharp
        file!.Document.Should().Equal(AFile);
        file.RequestedAt.Should().Be(Base);
```

(A record holding an array compares the array by reference, so `Should().Be(new OrderExportFile(...))` would fail on equal bytes.)

`tests/Infrastructure.Tests/Persistence/OrderExportConcurrencyTests.cs`: `Complete("first", …)` → `Complete("first"u8.ToArray(), …)`, `Complete("second", …)` → `Complete("second"u8.ToArray(), …)`, and the last assertion → `.Document.Should().Equal("first"u8.ToArray());`.

`tests/Api.IntegrationTests/Orders/OrderExportsEndpointTests.cs`: replace the `Csv` constant with `private static readonly byte[] CsvBytes = "OrderId,Sku\r\nabc,SKU-1\r\n"u8.ToArray();`, `export.Complete(Csv, …)` with `export.Complete(CsvBytes, …)`, and in the byte-order-mark test `bytes.Skip(3).Should().Equal(CsvBytes);`.

`tests/Worker.IntegrationTests/Jobs/OrderExportBuildTests.cs`, in `BuildOrderExport_StoresACsvOfTheOwnersOrdersOnly`:

```csharp
        System.Text.Encoding.UTF8.GetString(export.Document!).Should()
            .Contain("SKU-EXPORT-MINE-1").And.Contain("SKU-EXPORT-MINE-2")
            .And.NotContain("SKU-EXPORT-SOMEONE-ELSES");
```

- [ ] **Step 10: Pin what the migration does to old exports**

Create `tests/Infrastructure.Tests/Persistence/OrderExportMigrationTests.cs`. It migrates a scratch database to the migration *before* this one, seeds a Ready and a Requested CSV export with raw SQL, migrates to the end, and checks the outcome (Review Focus 1):

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace AiFramework.Infrastructure.Tests.Persistence;

/// <summary>
/// StoreOrderExportsAsBytes deletes Ready CSV exports (they cannot be served as PDFs) and keeps
/// Requested ones (their build writes bytes now). On a database of its own, because it migrates
/// down and up, which the shared fixture's database must never see.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class OrderExportMigrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task StoreOrderExportsAsBytes_DeletesReadyExportsAndKeepsRequestedOnes()
    {
        await using var context = fixture.CreateContextForNewDatabase($"migration_{Guid.NewGuid():N}");
        var migrator = context.GetService<IMigrator>();
        var migrations = context.Database.GetMigrations().ToList();
        var target = migrations.Single(m => m.EndsWith("_StoreOrderExportsAsBytes", StringComparison.Ordinal));
        await migrator.MigrateAsync(migrations[migrations.IndexOf(target) - 1]);

        var ready = Guid.NewGuid();
        var requested = Guid.NewGuid();
        await context.Database.ExecuteSqlAsync($"""
            INSERT INTO order_exports ("Id", "UserId", "RequestedAt", "Status", "CompletedAt", "RowCount", "Content")
            VALUES ({ready}, {Guid.NewGuid()}, now(), 'Ready', now(), 1, 'OrderId'),
                   ({requested}, {Guid.NewGuid()}, now(), 'Requested', NULL, NULL, NULL)
            """);

        await migrator.MigrateAsync(target);

        var ids = await context.Database
            .SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM order_exports""")
            .ToListAsync();
        ids.Should().Equal(requested);
    }
}
```

`PostgresFixture` does not have `CreateContextForNewDatabase` yet. Add it to `tests/Infrastructure.Tests/Persistence/PostgresFixture.cs`, beside `CreateContext()`, building the options exactly as `CreateContext()` does but with the database name replaced:

```csharp
    /// <summary>
    /// A context on a fresh, empty database in the same container, for tests that migrate down and
    /// up. The database is created by the first migration; nothing else shares it.
    /// </summary>
    public AiFrameworkDbContext CreateContextForNewDatabase(string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = databaseName };
        return new AiFrameworkDbContext(new DbContextOptionsBuilder<AiFrameworkDbContext>()
            .UseNpgsql(builder.ConnectionString)
            .Options);
    }
```

(`CreateContext()` is a plain `UseNpgsql(ConnectionString)`, so this differs only in the database name. `MigrateAsync` creates the database.)

- [ ] **Step 11: Run every affected suite**

Run: `dotnet build AiFramework.slnx` then
`dotnet test tests/Domain.Tests tests/Application.Tests --filter "FullyQualifiedName~OrderExport"` and
`dotnet test tests/Infrastructure.Tests tests/Api.IntegrationTests tests/Worker.IntegrationTests --filter "FullyQualifiedName~OrderExport"` (needs Docker).
Expected: build with 0 warnings; all PASS.

- [ ] **Step 12: Commit**

```bash
git add src tests
git commit -m "feat(orders): store order export files as bytes" -m "The file moves from a text column to bytea, ahead of the PDF. A migration drops the CSV column and the Ready exports that held it." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: OrderExportReport — what the document says

Pure Application code: every number and string the PDF shows, so the renderer only places them.

**Files:**
- Create: `src/Application/Orders/OrderExportReport.cs`
- Test: `tests/Application.Tests/Orders/OrderExportReportTests.cs`

**Interfaces:**
- Consumes: `OrderExportRow` (`OrderExportCsv.cs` today; moved in Task 4), `OrderStatus`.
- Produces:

```csharp
public sealed record OrderExportReport(
    string OwnerName,
    string Generated,              // "Generated 6 Oct 2026, 18:04 UTC"
    int OrderCount,
    string? Period,                // "12 Jan 2026 – 4 Oct 2026", one date if equal, null if empty
    string TotalValue,             // "2,469.00"
    string TotalValueNote,         // "excludes cancelled orders[ and N without a recorded price]"
    string StatusSummary,          // "Placed 12 · Shipped 30 · Cancelled 3"
    IReadOnlyList<OrderExportReportRow> Rows)
{
    public bool IsEmpty => Rows.Count == 0;
    public static OrderExportReport Create(IReadOnlyCollection<OrderExportRow> rows, string ownerName, DateTimeOffset generatedAt);
}

public sealed record OrderExportReportRow(
    string Placed, string Reference, string Product, string? Sku, string Quantity,
    string UnitPrice, string Total, OrderStatus Status, string? StatusDetail, string? CancellationReason);
```

- [ ] **Step 1: Write the failing tests**

`tests/Application.Tests/Orders/OrderExportReportTests.cs`:

```csharp
using AiFramework.Application.Orders;
using AiFramework.Domain.Orders;
using FluentAssertions;

namespace AiFramework.Application.Tests.Orders;

public sealed class OrderExportReportTests
{
    private static readonly DateTimeOffset Generated = new(2026, 10, 6, 18, 4, 59, TimeSpan.Zero);
    private static readonly DateTimeOffset Jan12 = new(2026, 1, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Oct4 = new(2026, 10, 4, 23, 30, 0, TimeSpan.FromHours(-2));

    private static OrderExportRow Row(
        DateTimeOffset placedAt,
        OrderStatus status = OrderStatus.Placed,
        decimal? unitPrice = 10m,
        int quantity = 1,
        string? productName = "Widget",
        string sku = "SKU-1",
        Guid? id = null,
        DateTimeOffset? shippedAt = null,
        DateTimeOffset? cancelledAt = null,
        string? reason = null) =>
        new(id ?? Guid.NewGuid(), sku, productName, quantity, unitPrice, status, placedAt, shippedAt, cancelledAt, reason);

    private static OrderExportReport Report(params OrderExportRow[] rows) =>
        OrderExportReport.Create(rows, "Jane Doe", Generated);

    [Fact]
    public void Create_SaysWhoAndWhenInUtc()
    {
        var report = Report();

        report.OwnerName.Should().Be("Jane Doe");
        report.Generated.Should().Be("Generated 6 Oct 2026, 18:04 UTC");
    }

    [Fact]
    public void Create_WithNoOrders_IsEmptyWithNoPeriod()
    {
        var report = Report();

        report.IsEmpty.Should().BeTrue();
        report.OrderCount.Should().Be(0);
        report.Period.Should().BeNull();
        report.TotalValue.Should().Be("0.00");
    }

    [Fact]
    public void Create_ListsTheNewestOrderFirst()
    {
        var older = Row(Jan12, sku: "OLD");
        var newer = Row(Oct4, sku: "NEW");

        var report = Report(older, newer);

        report.Rows.Select(r => r.Sku).Should().Equal("NEW", "OLD");
    }

    [Fact]
    public void Create_GivesThePeriodFromFirstToLastOrderInUtc()
    {
        // Oct4 is 4 Oct 23:30 at -02:00, which is 5 Oct in UTC.
        Report(Row(Oct4), Row(Jan12)).Period.Should().Be("12 Jan 2026 – 5 Oct 2026");
    }

    [Fact]
    public void Create_WithOrdersOnOneDay_GivesThatDayOnce()
    {
        Report(Row(Jan12), Row(Jan12.AddHours(3))).Period.Should().Be("12 Jan 2026");
    }

    [Fact]
    public void Create_TotalsQuantityTimesPriceAndExcludesCancelled()
    {
        var report = Report(
            Row(Jan12, unitPrice: 1234.5m, quantity: 2),
            Row(Jan12, status: OrderStatus.Shipped, unitPrice: 0.25m, quantity: 4),
            Row(Jan12, status: OrderStatus.Cancelled, unitPrice: 999m));

        report.TotalValue.Should().Be("2,470.00");
        report.TotalValueNote.Should().Be("excludes cancelled orders");
    }

    [Fact]
    public void Create_ExcludesOrdersWithNoRecordedPriceAndSaysHowMany()
    {
        var report = Report(Row(Jan12, unitPrice: 5m), Row(Jan12, unitPrice: null), Row(Jan12, unitPrice: null));

        report.TotalValue.Should().Be("5.00");
        report.TotalValueNote.Should().Be("excludes cancelled orders and 2 without a recorded price");
    }

    [Fact]
    public void Create_WhenEveryOrderIsCancelledOrUnpriced_TotalsZero()
    {
        var report = Report(Row(Jan12, status: OrderStatus.Cancelled), Row(Jan12, unitPrice: null));

        report.TotalValue.Should().Be("0.00");
    }

    [Fact]
    public void Create_CountsEachStatus()
    {
        var report = Report(
            Row(Jan12), Row(Jan12),
            Row(Jan12, status: OrderStatus.Shipped),
            Row(Jan12, status: OrderStatus.Cancelled));

        report.OrderCount.Should().Be(4);
        report.StatusSummary.Should().Be("Placed 2 · Shipped 1 · Cancelled 1");
    }

    [Fact]
    public void Create_ShortensTheOrderIdToEightUpperCaseCharacters()
    {
        var row = Row(Jan12, id: Guid.Parse("3f2a9c01-aaaa-bbbb-cccc-ddddeeeeffff"));

        Report(row).Rows.Single().Reference.Should().Be("3F2A9C01");
    }

    [Fact]
    public void Create_FormatsARowsDateQuantityAndMoney()
    {
        var row = Report(Row(Oct4, unitPrice: 1234.5m, quantity: 2)).Rows.Single();

        row.Placed.Should().Be("5 Oct 2026");
        row.Quantity.Should().Be("2");
        row.UnitPrice.Should().Be("1,234.50");
        row.Total.Should().Be("2,469.00");
    }

    [Fact]
    public void Create_ShowsADashForAMissingPrice()
    {
        var row = Report(Row(Jan12, unitPrice: null)).Rows.Single();

        row.UnitPrice.Should().Be("–");
        row.Total.Should().Be("–");
    }

    [Fact]
    public void Create_PutsTheSkuUnderTheProductName()
    {
        var row = Report(Row(Jan12, productName: "Smørrebrød", sku: "SKU-9")).Rows.Single();

        row.Product.Should().Be("Smørrebrød");
        row.Sku.Should().Be("SKU-9");
    }

    [Fact]
    public void Create_WithNoProductName_ShowsTheSkuAlone()
    {
        var row = Report(Row(Jan12, productName: null, sku: "SKU-9")).Rows.Single();

        row.Product.Should().Be("SKU-9");
        row.Sku.Should().BeNull();
    }

    [Fact]
    public void Create_SaysWhenAnOrderShipped()
    {
        var row = Report(Row(Jan12, status: OrderStatus.Shipped, shippedAt: new DateTimeOffset(2026, 3, 3, 8, 0, 0, TimeSpan.Zero)))
            .Rows.Single();

        row.StatusDetail.Should().Be("Shipped 3 Mar 2026");
        row.CancellationReason.Should().BeNull();
    }

    [Fact]
    public void Create_SaysWhenAndWhyAnOrderWasCancelled()
    {
        var row = Report(Row(
                Jan12,
                status: OrderStatus.Cancelled,
                cancelledAt: new DateTimeOffset(2026, 3, 5, 8, 0, 0, TimeSpan.Zero),
                reason: "Changed my mind"))
            .Rows.Single();

        row.StatusDetail.Should().Be("Cancelled 5 Mar 2026");
        row.CancellationReason.Should().Be("Changed my mind");
    }

    [Fact]
    public void Create_ForAPlacedOrder_HasNoStatusDetail()
    {
        Report(Row(Jan12)).Rows.Single().StatusDetail.Should().BeNull();
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/Application.Tests --filter "FullyQualifiedName~OrderExportReportTests"`
Expected: build FAILS — `The type or namespace name 'OrderExportReport' could not be found`.

- [ ] **Step 3: Implement**

`src/Application/Orders/OrderExportReport.cs`:

```csharp
using System.Globalization;
using AiFramework.Domain.Orders;

namespace AiFramework.Application.Orders;

/// <summary>
/// Everything the export's PDF says, already worked out and formatted: the renderer only places
/// these strings. Pure, so every rule here is tested without drawing a page. ADR 0030.
/// </summary>
/// <remarks>
/// Times are UTC, said once in <see cref="Generated"/>: the worker does not know the reader's time
/// zone. Amounts have two decimals and no currency symbol, because the domain has no currency.
/// </remarks>
public sealed record OrderExportReport(
    string OwnerName,
    string Generated,
    int OrderCount,
    string? Period,
    string TotalValue,
    string TotalValueNote,
    string StatusSummary,
    IReadOnlyList<OrderExportReportRow> Rows)
{
    public bool IsEmpty => Rows.Count == 0;

    public static OrderExportReport Create(
        IReadOnlyCollection<OrderExportRow> rows, string ownerName, DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(ownerName);

        // Counted: cancelled orders are not value, and an order with no recorded price has none to add.
        var counted = rows.Where(r => r.Status != OrderStatus.Cancelled).ToArray();
        var total = counted.Where(r => r.UnitPrice is not null).Sum(r => r.UnitPrice!.Value * r.Quantity);
        var unpriced = counted.Count(r => r.UnitPrice is null);

        return new OrderExportReport(
            ownerName,
            $"Generated {generatedAt.UtcDateTime.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture)} UTC",
            rows.Count,
            Period(rows),
            Money(total),
            unpriced == 0
                ? "excludes cancelled orders"
                : $"excludes cancelled orders and {unpriced.ToString(CultureInfo.InvariantCulture)} without a recorded price",
            $"Placed {Count(rows, OrderStatus.Placed)} · Shipped {Count(rows, OrderStatus.Shipped)} · Cancelled {Count(rows, OrderStatus.Cancelled)}",
            [.. rows.OrderByDescending(r => r.PlacedAt).ThenByDescending(r => r.OrderId).Select(ToRow)]);
    }

    private static string? Period(IReadOnlyCollection<OrderExportRow> rows)
    {
        if (rows.Count == 0)
        {
            return null;
        }

        var first = Date(rows.Min(r => r.PlacedAt));
        var last = Date(rows.Max(r => r.PlacedAt));
        return first == last ? first : $"{first} – {last}";
    }

    private static string Count(IReadOnlyCollection<OrderExportRow> rows, OrderStatus status) =>
        rows.Count(r => r.Status == status).ToString(CultureInfo.InvariantCulture);

    private static OrderExportReportRow ToRow(OrderExportRow row) => new(
        Date(row.PlacedAt),
        row.OrderId.ToString("N")[..8].ToUpperInvariant(),
        row.ProductName ?? row.Sku,
        row.ProductName is null ? null : row.Sku,
        row.Quantity.ToString(CultureInfo.InvariantCulture),
        row.UnitPrice is { } price ? Money(price) : "–",
        row.UnitPrice is { } unit ? Money(unit * row.Quantity) : "–",
        row.Status,
        row.Status switch
        {
            OrderStatus.Shipped when row.ShippedAt is { } shipped => $"Shipped {Date(shipped)}",
            OrderStatus.Cancelled when row.CancelledAt is { } cancelled => $"Cancelled {Date(cancelled)}",
            _ => null,
        },
        row.Status == OrderStatus.Cancelled ? row.CancellationReason : null);

    private static string Date(DateTimeOffset value) =>
        value.UtcDateTime.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    private static string Money(decimal value) => value.ToString("#,##0.00", CultureInfo.InvariantCulture);
}

/// <summary>One order as the PDF's table shows it. <see cref="Sku"/> is null when it is already the product.</summary>
public sealed record OrderExportReportRow(
    string Placed,
    string Reference,
    string Product,
    string? Sku,
    string Quantity,
    string UnitPrice,
    string Total,
    OrderStatus Status,
    string? StatusDetail,
    string? CancellationReason);
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/Application.Tests --filter "FullyQualifiedName~OrderExportReportTests"`
Expected: all PASS. If an analyzer flags the interpolated `Count(...)` strings for culture (MA0076/CA1305), they already pass invariant strings; fix the reported line by calling `string.Create(CultureInfo.InvariantCulture, $"...")`.

- [ ] **Step 5: Commit**

```bash
git add src/Application/Orders/OrderExportReport.cs tests/Application.Tests/Orders/OrderExportReportTests.cs
git commit -m "feat(orders): work out what the export document says" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: The PDF renderer

**Files:**
- Modify: `src/Infrastructure/AiFramework.Infrastructure.csproj` (package, embedded fonts)
- Create: `src/Application/Orders/IOrderExportRenderer.cs`
- Create: `src/Infrastructure/Exports/Fonts/NotoSans-Regular.ttf`, `NotoSans-Bold.ttf`, `OFL.txt`
- Create: `src/Infrastructure/Exports/NotoSansFontResolver.cs`
- Create: `src/Infrastructure/Exports/OrderExportPalette.cs`
- Create: `src/Infrastructure/Exports/MigraDocOrderExportRenderer.cs`
- Modify: `src/Infrastructure/InfrastructureRegistration.cs`
- Modify: `tests/Infrastructure.Tests/AiFramework.Infrastructure.Tests.csproj` (PdfPig)
- Test: `tests/Infrastructure.Tests/Exports/OrderExportPaletteTests.cs`, `tests/Infrastructure.Tests/Exports/MigraDocOrderExportRendererTests.cs`

**Interfaces:**
- Consumes: `OrderExportReport`, `OrderExportReportRow` (Task 2).
- Produces: `public interface IOrderExportRenderer { byte[] Render(OrderExportReport report); }` (Application); `internal sealed class MigraDocOrderExportRenderer : IOrderExportRenderer` registered as a singleton in `AddInfrastructure`.

- [ ] **Step 1: Add the package, the fonts and the port**

```bash
dotnet add src/Infrastructure package PDFsharp-MigraDoc --version 6.2.4
dotnet add tests/Infrastructure.Tests package PdfPig --version 0.1.16
mkdir -p src/Infrastructure/Exports/Fonts
curl -sfL -o src/Infrastructure/Exports/Fonts/NotoSans-Regular.ttf https://github.com/notofonts/notofonts.github.io/raw/main/fonts/NotoSans/hinted/ttf/NotoSans-Regular.ttf
curl -sfL -o src/Infrastructure/Exports/Fonts/NotoSans-Bold.ttf https://github.com/notofonts/notofonts.github.io/raw/main/fonts/NotoSans/hinted/ttf/NotoSans-Bold.ttf
curl -sfL -o src/Infrastructure/Exports/Fonts/OFL.txt https://raw.githubusercontent.com/notofonts/latin-greek-cyrillic/main/OFL.txt
```

Expected: two `.ttf` files of roughly 620 KB each and an `OFL.txt` starting "Copyright 2022 The Noto Project Authors".

In `src/Infrastructure/AiFramework.Infrastructure.csproj` add (comment included):

```xml
  <ItemGroup>
    <!-- The worker image (aspnet:10.0-noble-chiseled) has no system fonts, so the export PDF's font
         ships inside this assembly. Logical names keep the resource names independent of folders. -->
    <EmbeddedResource Include="Exports/Fonts/*.ttf" LogicalName="ExportFonts.%(Filename)%(Extension)" />
  </ItemGroup>
```

`src/Application/Orders/IOrderExportRenderer.cs`:

```csharp
namespace AiFramework.Application.Orders;

/// <summary>Draws an export as a file. Implemented in Infrastructure, which alone knows the PDF library.</summary>
public interface IOrderExportRenderer
{
    public byte[] Render(OrderExportReport report);
}
```

- [ ] **Step 2: Write the failing palette test**

`tests/Infrastructure.Tests/Exports/OrderExportPaletteTests.cs`:

```csharp
using AiFramework.Infrastructure.Exports;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.Exports;

/// <summary>The spec's promise that every text/background pair in the PDF meets WCAG AA (4.5:1).</summary>
public sealed class OrderExportPaletteTests
{
    public static TheoryData<string> Pairs() => [.. OrderExportPalette.TextPairs.Select(p => p.Name)];

    [Theory]
    [MemberData(nameof(Pairs))]
    public void TextPair_MeetsWcagAa(string name)
    {
        var pair = OrderExportPalette.TextPairs.Single(p => p.Name == name);

        Contrast(pair.Text, pair.Background).Should().BeGreaterThanOrEqualTo(4.5, name);
    }

    private static double Contrast(PaletteColor a, PaletteColor b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(PaletteColor c) =>
        (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));

    private static double Channel(byte value)
    {
        var v = value / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }
}
```

Run: `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~OrderExportPaletteTests"`
Expected: build FAILS — `OrderExportPalette` not found.

- [ ] **Step 3: Implement the palette**

`src/Infrastructure/Exports/OrderExportPalette.cs`:

```csharp
using MigraDoc.DocumentObjectModel;

namespace AiFramework.Infrastructure.Exports;

internal readonly record struct PaletteColor(byte R, byte G, byte B)
{
    public Color ToColor() => new(R, G, B);
}

/// <summary>
/// The export PDF's colours: the app's light theme, copied from frontend/src/styles/tokens.css
/// because the backend cannot read CSS. A palette change there does not reach the PDF by itself.
/// OrderExportPaletteTests holds every pair in <see cref="TextPairs"/> to WCAG AA.
/// </summary>
internal static class OrderExportPalette
{
    public static readonly PaletteColor Brand = new(0x3b, 0x2f, 0xa8);           // --color-brand-from
    public static readonly PaletteColor OnBrand = new(0xff, 0xff, 0xff);
    public static readonly PaletteColor OnBrandSoft = new(0xe0, 0xdd, 0xf7);     // solid stand-in for 72% white
    public static readonly PaletteColor Card = new(0xee, 0xf1, 0xff);            // --color-accent-soft
    public static readonly PaletteColor Text = new(0x12, 0x15, 0x1c);            // --color-text
    public static readonly PaletteColor Muted = new(0x59, 0x61, 0x6f);           // --color-text-muted
    public static readonly PaletteColor Sunken = new(0xf8, 0xf9, 0xfb);          // --color-surface-sunken
    public static readonly PaletteColor Surface = new(0xff, 0xff, 0xff);         // --color-surface
    public static readonly PaletteColor Hairline = new(0xe4, 0xe7, 0xec);        // --color-border
    public static readonly PaletteColor Rule = new(0xcf, 0xd4, 0xdd);            // --color-border-strong
    public static readonly PaletteColor ShippedText = new(0x0f, 0x6b, 0x3f);     // --color-success-text
    public static readonly PaletteColor ShippedBackground = new(0xf0, 0xfd, 0xf5);
    public static readonly PaletteColor CancelledText = new(0x91, 0x20, 0x18);   // --color-danger-text
    public static readonly PaletteColor CancelledBackground = new(0xfe, 0xf4, 0xf3);
    public static readonly PaletteColor PlacedText = new(0x4f, 0x46, 0xe5);      // --color-accent
    public static readonly PaletteColor PlacedBackground = new(0xee, 0xf1, 0xff);

    /// <summary>Every text colour the renderer draws, on every background it draws it on.</summary>
    public static readonly IReadOnlyList<(string Name, PaletteColor Text, PaletteColor Background)> TextPairs =
    [
        ("title on brand", OnBrand, Brand),
        ("subtitle on brand", OnBrandSoft, Brand),
        ("card label", Muted, Card),
        ("card value", Text, Card),
        ("table header", Muted, Sunken),
        ("row text", Text, Surface),
        ("zebra row text", Text, Sunken),
        ("secondary row text", Muted, Surface),
        ("secondary zebra row text", Muted, Sunken),
        ("footer", Muted, Surface),
        ("shipped badge", ShippedText, ShippedBackground),
        ("cancelled badge", CancelledText, CancelledBackground),
        ("placed badge", PlacedText, PlacedBackground),
    ];
}
```

Run the palette test again. Expected: 13 PASS.

- [ ] **Step 4: Write the failing renderer tests**

`tests/Infrastructure.Tests/Exports/MigraDocOrderExportRendererTests.cs`:

```csharp
using AiFramework.Application.Orders;
using AiFramework.Domain.Orders;
using AiFramework.Infrastructure.Exports;
using FluentAssertions;
using UglyToad.PdfPig;

namespace AiFramework.Infrastructure.Tests.Exports;

/// <summary>
/// The real renderer, read back with PdfPig. Text assertions go through extracted words, which is
/// also the proof that the embedded font draws the characters (a missing glyph extracts as nothing).
/// </summary>
public sealed class MigraDocOrderExportRendererTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 6, 18, 4, 0, TimeSpan.Zero);

    private static OrderExportRow Row(int n, string? name = null, OrderStatus status = OrderStatus.Placed, string? reason = null) =>
        new(Guid.NewGuid(), $"SKU-{n}", name ?? $"Widget {n}", 2, 10m, status, At.AddDays(-n), null,
            status == OrderStatus.Cancelled ? At : null, reason);

    private static byte[] Render(IReadOnlyCollection<OrderExportRow> rows, string owner = "Jane Doe") =>
        new MigraDocOrderExportRenderer().Render(OrderExportReport.Create(rows, owner, At));

    private static List<string> PageTexts(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return [.. document.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text)))];
    }

    [Fact]
    public void Render_ProducesAPdf()
    {
        var pdf = Render([Row(1)]);

        pdf.Take(5).Should().Equal("%PDF-"u8.ToArray());
    }

    [Fact]
    public void Render_SaysWhatItIsAndWhoseItIs()
    {
        var text = PageTexts(Render([Row(1)])).Single();

        text.Should().Contain("Order history").And.Contain("Jane Doe").And.Contain("Generated 6 Oct 2026, 18:04 UTC");
    }

    [Fact]
    public void Render_ListsEveryOrdersSkuAndTheTotal()
    {
        var text = PageTexts(Render([Row(1), Row(2)])).Single();

        text.Should().Contain("SKU-1").And.Contain("SKU-2").And.Contain("40.00");
    }

    [Fact]
    public void Render_ALongExport_NumbersEveryPageAndRepeatsTheTableHeader()
    {
        var pages = PageTexts(Render([.. Enumerable.Range(1, 80).Select(n => Row(n))]));

        pages.Should().HaveCountGreaterThan(1);
        pages.Select((text, i) => (text, i)).Should().AllSatisfy(p =>
        {
            p.text.Should().Contain($"Page {p.i + 1} of {pages.Count}");
            p.text.Should().Contain("PRODUCT");
        });
    }

    [Theory]
    [InlineData("Smørrebrød")]
    [InlineData("Ελληνικά")]
    [InlineData("Борщ")]
    public void Render_DrawsNonAsciiProductNamesAsText(string name)
    {
        PageTexts(Render([Row(1, name)])).Single().Should().Contain(name);
    }

    [Fact]
    public void Render_ShowsACancelledOrdersReason()
    {
        var text = PageTexts(Render([Row(1, status: OrderStatus.Cancelled, reason: "Changed my mind")])).Single();

        text.Should().Contain("Cancelled").And.Contain("Changed my mind");
    }

    [Fact]
    public void Render_WithNoOrders_SaysSo()
    {
        PageTexts(Render([])).Single().Should().Contain("No orders yet.");
    }

    // Review Focus 3: text the font cannot draw, and text longer than its cell, must not throw.
    [Fact]
    public void Render_WithCharactersTheFontLacksAndVeryLongText_StillRenders()
    {
        var longName = string.Concat(Enumerable.Repeat("Extraordinarily long product name ", 20));
        var rows = new[]
        {
            Row(1, "注文の品"),
            Row(2, longName, OrderStatus.Cancelled, reason: new string('x', 600)),
        };

        var pdf = Render(rows);

        PageTexts(pdf).Should().NotBeEmpty();
    }

    [Fact]
    public void Render_TitlesTheDocumentAndNamesItsAuthor()
    {
        using var document = PdfDocument.Open(Render([Row(1)]));

        document.Information.Title.Should().Be("Order history");
        document.Information.Author.Should().Be("Jane Doe");
        document.Information.Creator.Should().Be("AIFrameWork");
    }
}
```

Run: `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~MigraDocOrderExportRendererTests"`
Expected: build FAILS — `MigraDocOrderExportRenderer` not found.

- [ ] **Step 5: Implement the font resolver**

`src/Infrastructure/Exports/NotoSansFontResolver.cs`:

```csharp
using System.Reflection;
using PdfSharp.Fonts;

namespace AiFramework.Infrastructure.Exports;

/// <summary>
/// Serves the embedded Noto Sans for <b>every</b> family name. The worker image has no system
/// fonts, and MigraDoc asks for "Courier New" as its own error font before it draws anything, so a
/// resolver that answered only "Noto Sans" throws on the first render (found by a spike, 2026-10-06).
/// Italic is simulated: no italic face is shipped.
/// </summary>
internal sealed class NotoSansFontResolver : IFontResolver
{
    public const string FamilyName = "Noto Sans";

    private static readonly Lock InstallLock = new();

    private NotoSansFontResolver()
    {
    }

    /// <summary>
    /// PDFsharp's resolver is process-wide, and the Api, the worker and test hosts can share one
    /// process, so it is set once and only if nothing else has been.
    /// </summary>
    public static void Install()
    {
        lock (InstallLock)
        {
            if (GlobalFontSettings.FontResolver is not NotoSansFontResolver)
            {
                GlobalFontSettings.FontResolver = new NotoSansFontResolver();
            }
        }
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic) =>
        new(bold ? "NotoSans-Bold" : "NotoSans-Regular", mustSimulateBold: false, mustSimulateItalic: italic);

    public byte[]? GetFont(string faceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"ExportFonts.{faceName}.ttf")
            ?? throw new InvalidOperationException($"The embedded font '{faceName}' is missing from the assembly.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
```

- [ ] **Step 6: Implement the renderer**

`src/Infrastructure/Exports/MigraDocOrderExportRenderer.cs`:

```csharp
using AiFramework.Application.Orders;
using AiFramework.Domain.Orders;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;

namespace AiFramework.Infrastructure.Exports;

/// <summary>
/// Lays out an <see cref="OrderExportReport"/> as an A4 landscape PDF for sharing: a brand band,
/// three summary cards, and a table whose header repeats on every page. Static text and shapes only
/// — no links, scripts, attachments or form fields — so nothing a user typed can become active
/// content. ADR 0030.
/// </summary>
internal sealed class MigraDocOrderExportRenderer : IOrderExportRenderer
{
    // A4 landscape is 297 mm wide; 15 mm margins leave 267 mm of content.
    private const double ContentWidthMm = 267;
    private static readonly double[] ColumnsMm = [28, 24, 95, 14, 28, 30, 48];
    private static readonly string[] Headings = ["PLACED", "ORDER", "PRODUCT", "QTY", "UNIT PRICE", "TOTAL", "STATUS"];

    public MigraDocOrderExportRenderer() => NotoSansFontResolver.Install();

    public byte[] Render(OrderExportReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var document = new Document();
        document.Info.Title = "Order history";
        document.Info.Author = report.OwnerName;
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = NotoSansFontResolver.FamilyName;
        normal.Font.Size = 9;
        normal.Font.Color = OrderExportPalette.Text.ToColor();

        var section = document.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.Orientation = Orientation.Landscape;
        section.PageSetup.LeftMargin = Unit.FromMillimeter(15);
        section.PageSetup.RightMargin = Unit.FromMillimeter(15);
        section.PageSetup.TopMargin = Unit.FromMillimeter(15);
        section.PageSetup.BottomMargin = Unit.FromMillimeter(20);
        section.PageSetup.FooterDistance = Unit.FromMillimeter(8);

        Footer(section, report);
        Band(section, report);
        Summary(section, report);
        if (!report.IsEmpty)
        {
            Orders(section, report);
        }

        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        renderer.PdfDocument.Info.Creator = "AIFrameWork";
        using var output = new MemoryStream();
        renderer.PdfDocument.Save(output);
        return output.ToArray();
    }

    private static void Footer(Section section, OrderExportReport report)
    {
        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Borders.Top.Color = OrderExportPalette.Hairline.ToColor();
        footer.Format.Borders.Top.Width = Unit.FromPoint(0.5);
        footer.Format.Borders.DistanceFromTop = Unit.FromMillimeter(2);
        footer.Format.Font.Size = 8;
        footer.Format.Font.Color = OrderExportPalette.Muted.ToColor();
        footer.AddText($"Order history · {report.OwnerName} · Page ");
        footer.AddPageField();
        footer.AddText(" of ");
        footer.AddNumPagesField();
    }

    private static void Band(Section section, OrderExportReport report)
    {
        var band = section.AddTable();
        band.AddColumn(Unit.FromMillimeter(ContentWidthMm));
        var row = band.AddRow();
        row.Shading.Color = OrderExportPalette.Brand.ToColor();
        row.TopPadding = Unit.FromMillimeter(6);
        row.BottomPadding = Unit.FromMillimeter(6);
        var cell = row.Cells[0];
        cell.Format.LeftIndent = Unit.FromMillimeter(4);

        var title = cell.AddParagraph("Order history");
        title.Format.Font.Size = 22;
        title.Format.Font.Bold = true;
        title.Format.Font.Color = OrderExportPalette.OnBrand.ToColor();

        var subtitle = cell.AddParagraph($"{report.OwnerName} · {report.Generated}");
        subtitle.Format.Font.Size = 10;
        subtitle.Format.SpaceBefore = Unit.FromMillimeter(1);
        subtitle.Format.Font.Color = OrderExportPalette.OnBrandSoft.ToColor();
    }

    private static void Summary(Section section, OrderExportReport report)
    {
        var cards = section.AddTable();
        cards.TopPadding = Unit.FromMillimeter(3);
        cards.BottomPadding = Unit.FromMillimeter(3);
        cards.LeftPadding = Unit.FromMillimeter(4);

        if (report.IsEmpty)
        {
            cards.AddColumn(Unit.FromMillimeter(ContentWidthMm));
            var only = cards.AddRow();
            only.Shading.Color = OrderExportPalette.Card.ToColor();
            Value(only.Cells[0], "No orders yet.");
            Spacer(section);
            return;
        }

        // card, gap, card, gap, card: 3 × 85 mm + 2 × 6 mm = 267 mm.
        foreach (var width in new[] { 85.0, 6, 85, 6, 85 })
        {
            cards.AddColumn(Unit.FromMillimeter(width));
        }

        var row = cards.AddRow();
        Card(row.Cells[0], "ORDERS", report.OrderCount.ToString(System.Globalization.CultureInfo.InvariantCulture), report.Period);
        Card(row.Cells[2], "TOTAL VALUE", report.TotalValue, report.TotalValueNote);
        Card(row.Cells[4], "BY STATUS", report.StatusSummary, null);
        Spacer(section);
    }

    private static void Card(Cell cell, string label, string value, string? note)
    {
        cell.Shading.Color = OrderExportPalette.Card.ToColor();
        var heading = cell.AddParagraph(label);
        heading.Format.Font.Size = 7.5;
        heading.Format.Font.Bold = true;
        heading.Format.Font.Color = OrderExportPalette.Muted.ToColor();
        Value(cell, value);
        if (note is not null)
        {
            var small = cell.AddParagraph(note);
            small.Format.Font.Size = 8;
            small.Format.Font.Color = OrderExportPalette.Muted.ToColor();
        }
    }

    private static void Value(Cell cell, string value)
    {
        var big = cell.AddParagraph(value);
        big.Format.Font.Size = 15;
        big.Format.Font.Bold = true;
        big.Format.SpaceBefore = Unit.FromMillimeter(1);
    }

    private static void Spacer(Section section) =>
        section.AddParagraph().Format.SpaceAfter = Unit.FromMillimeter(4);

    private static void Orders(Section section, OrderExportReport report)
    {
        var table = section.AddTable();
        table.Borders.Bottom.Color = OrderExportPalette.Hairline.ToColor();
        table.Borders.Bottom.Width = Unit.FromPoint(0.5);
        table.TopPadding = Unit.FromMillimeter(1.8);
        table.BottomPadding = Unit.FromMillimeter(1.8);
        foreach (var width in ColumnsMm)
        {
            table.AddColumn(Unit.FromMillimeter(width));
        }

        var head = table.AddRow();
        head.HeadingFormat = true;
        head.Shading.Color = OrderExportPalette.Sunken.ToColor();
        for (var i = 0; i < Headings.Length; i++)
        {
            var p = head.Cells[i].AddParagraph(Headings[i]);
            p.Format.Font.Size = 7.5;
            p.Format.Font.Bold = true;
            p.Format.Font.Color = OrderExportPalette.Muted.ToColor();
            if (i is 3 or 4 or 5)
            {
                p.Format.Alignment = ParagraphAlignment.Right;
            }
        }

        for (var n = 0; n < report.Rows.Count; n++)
        {
            OrderRow(table, report.Rows[n], zebra: n % 2 == 1);
        }

        var total = table.AddRow();
        total.Borders.Top.Color = OrderExportPalette.Rule.ToColor();
        total.Borders.Top.Width = Unit.FromPoint(1);
        var label = total.Cells[0].AddParagraph($"Total value ({report.TotalValueNote})");
        total.Cells[0].MergeRight = 4;
        label.Format.Alignment = ParagraphAlignment.Right;
        label.Format.Font.Color = OrderExportPalette.Muted.ToColor();
        var sum = total.Cells[5].AddParagraph(report.TotalValue);
        sum.Format.Alignment = ParagraphAlignment.Right;
        sum.Format.Font.Bold = true;
    }

    private static void OrderRow(Table table, OrderExportReportRow order, bool zebra)
    {
        var row = table.AddRow();
        row.VerticalAlignment = VerticalAlignment.Top;
        if (zebra)
        {
            row.Shading.Color = OrderExportPalette.Sunken.ToColor();
        }

        row.Cells[0].AddParagraph(order.Placed);
        row.Cells[1].AddParagraph(order.Reference).Format.Font.Color = OrderExportPalette.Muted.ToColor();
        row.Cells[2].AddParagraph(order.Product);
        if (order.Sku is not null)
        {
            var sku = row.Cells[2].AddParagraph(order.Sku);
            sku.Format.Font.Size = 7.5;
            sku.Format.Font.Color = OrderExportPalette.Muted.ToColor();
        }

        Number(row.Cells[3], order.Quantity);
        Number(row.Cells[4], order.UnitPrice);
        Number(row.Cells[5], order.Total);
        Status(row.Cells[6], order);
    }

    private static void Number(Cell cell, string text) =>
        cell.AddParagraph(text).Format.Alignment = ParagraphAlignment.Right;

    private static void Status(Cell cell, OrderExportReportRow order)
    {
        var (text, background) = order.Status switch
        {
            OrderStatus.Shipped => (OrderExportPalette.ShippedText, OrderExportPalette.ShippedBackground),
            OrderStatus.Cancelled => (OrderExportPalette.CancelledText, OrderExportPalette.CancelledBackground),
            _ => (OrderExportPalette.PlacedText, OrderExportPalette.PlacedBackground),
        };

        // A rectangular badge: MigraDoc draws no rounded corners. The right indent keeps it about
        // as wide as its word rather than the whole column.
        var badge = cell.AddParagraph(order.Status.ToString());
        badge.Format.Shading.Color = background.ToColor();
        badge.Format.Font.Color = text.ToColor();
        badge.Format.Font.Bold = true;
        badge.Format.Font.Size = 8;
        badge.Format.LeftIndent = Unit.FromMillimeter(0.5);
        badge.Format.RightIndent = Unit.FromMillimeter(24);
        badge.Format.Alignment = ParagraphAlignment.Center;

        if (order.StatusDetail is not null)
        {
            Secondary(cell, order.StatusDetail, italic: false);
        }

        if (order.CancellationReason is not null)
        {
            Secondary(cell, order.CancellationReason, italic: true);
        }
    }

    private static void Secondary(Cell cell, string text, bool italic)
    {
        var p = cell.AddParagraph(text);
        p.Format.Font.Size = 7.5;
        p.Format.Font.Italic = italic;
        p.Format.Font.Color = OrderExportPalette.Muted.ToColor();
        p.Format.SpaceBefore = Unit.FromMillimeter(0.8);
    }
}
```

In `src/Infrastructure/InfrastructureRegistration.cs`, beside `services.AddScoped<IOrderExportRepository, OrderExportRepository>();` add:

```csharp
        // Stateless, and only the worker's build job calls it; registered here so both hosts resolve the
        // same graph. Constructing it installs the embedded font, once per process.
        services.AddSingleton<IOrderExportRenderer, MigraDocOrderExportRenderer>();
```

with `using AiFramework.Infrastructure.Exports;`.

- [ ] **Step 7: Run the renderer and palette tests**

Run: `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~Exports"`
Expected: all PASS, no Docker needed. If `Render_DrawsNonAsciiProductNamesAsText` fails for one name, the font is wrong (re-download), not the test.

- [ ] **Step 8: Look at it**

Write one rendered PDF to disk and open it, checking against spec section 1 (band, three cards, badge colours, zebra rows, total row, footer):

```bash
dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~Render_ALongExport" -- RunConfiguration.EnvironmentVariables.EXPORT_PDF_OUT="$TEMP/order-export-sample.pdf"
```

For this to write a file, add one line at the end of `Render_ALongExport_NumbersEveryPageAndRepeatsTheTableHeader` **temporarily**, then remove it before committing:
`if (Environment.GetEnvironmentVariable("EXPORT_PDF_OUT") is { } path) { File.WriteAllBytes(path, Render([.. Enumerable.Range(1, 80).Select(n => Row(n))])); }`
Adjust spacing values in the renderer only if the layout visibly contradicts the spec.

- [ ] **Step 9: Commit**

```bash
git add src/Application/Orders/IOrderExportRenderer.cs src/Infrastructure tests/Infrastructure.Tests
git commit -m "feat(orders): render the export as a coloured A4 PDF" -m "PDFsharp/MigraDoc 6.2.4 behind an Application port, with Noto Sans embedded: the chiseled worker image has no fonts." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: The job builds the PDF, the API serves it

**Files:**
- Modify: `src/Application/Orders/BuildOrderExport.cs`
- Delete: `src/Application/Orders/OrderExportCsv.cs` (move `OrderExportRow` first — Step 3)
- Create: `src/Application/Orders/OrderExportRow.cs`
- Delete: `tests/Application.Tests/Orders/OrderExportCsvTests.cs`
- Modify: `src/Application/Orders/OrderExportQueries.cs` (`.pdf` name)
- Modify: `src/Api/Orders/OrderExportsController.cs`
- Regenerate: `src/Worker/Internal/Generated/**`, `openapi/AiFramework.Api.json`, `frontend/src/api/schema.d.ts`
- Test: `tests/Application.Tests/Orders/BuildOrderExportHandlerTests.cs`, `tests/Application.Tests/Orders/OrderExportHandlerTests.cs`, `tests/Api.IntegrationTests/Orders/OrderExportsEndpointTests.cs`, `tests/Worker.IntegrationTests/Jobs/OrderExportBuildTests.cs`

**Interfaces:**
- Consumes: `IOrderExportRenderer.Render(OrderExportReport)`, `OrderExportReport.Create(...)` (Tasks 2–3), `GetUser(Guid Id) : IQuery<SessionView>` with `SessionView(Guid UserId, string Username, string DisplayName, string SecurityStamp, UserRole Role)`, `IClock.UtcNow`.
- Produces: `BuildOrderExportHandler(IQueryDispatcher queries, ICommandDispatcher commands, IOrderExportRenderer renderer, IClock clock)`; download answers `application/pdf`, `orders-yyyy-MM-dd.pdf`.

- [ ] **Step 1: Write the failing job tests**

In `tests/Application.Tests/Orders/BuildOrderExportHandlerTests.cs` add fields and setup:

```csharp
    private static readonly byte[] Pdf = "%PDF-1.7"u8.ToArray();
    private readonly IOrderExportRenderer _renderer = Substitute.For<IOrderExportRenderer>();
    private readonly IClock _clock = Substitute.For<IClock>();
```

and in the constructor:

```csharp
        _queries.SendAsync(Arg.Any<IQuery<SessionView>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new SessionView(OwnerId, "jane", "Jane Doe", "stamp", UserRole.Member)));
        _renderer.Render(Arg.Any<OrderExportReport>()).Returns(Pdf);
        _clock.UtcNow.Returns(Now);
```

with `private BuildOrderExportHandler Handler => new(_queries, _commands, _renderer, _clock);` and usings `AiFramework.Application.Users` and `AiFramework.Domain.Users`. Replace `Handle_FollowsTheCursorAndCompletesWithEveryRow`'s final assertion with:

```csharp
        _renderer.Received(1).Render(Arg.Is<OrderExportReport>(r => r.OrderCount == 3));
        await _commands.Received(1).SendAsync(
            Arg.Is<CompleteOrderExport>(c => c.ExportId == ExportId && c.RowCount == 3 && c.Document == Pdf),
            Arg.Any<CancellationToken>());
```

(remove `using System.Text;`) and add:

```csharp
    [Fact]
    public async Task Handle_NamesTheOwnerAndTheTimeInTheReport()
    {
        _queries.SendAsync(Arg.Any<IQuery<OrderExportRowPage>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new OrderExportRowPage([], NextCursor: null)));

        await Handler.Handle(new BuildOrderExport(ExportId, OwnerId), CancellationToken.None);

        await _queries.Received(1).SendAsync(Arg.Is<GetUser>(q => q.Id == OwnerId), Arg.Any<CancellationToken>());
        _renderer.Received(1).Render(Arg.Is<OrderExportReport>(r =>
            r.OwnerName == "Jane Doe" && r.Generated == "Generated 3 Oct 2026, 12:00 UTC"));
    }

    [Fact]
    public Task Handle_WhenTheOwnerCannotBeRead_Throws()
    {
        _queries.SendAsync(Arg.Any<IQuery<OrderExportRowPage>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new OrderExportRowPage([], NextCursor: null)));
        _queries.SendAsync(Arg.Any<IQuery<SessionView>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<SessionView>(new Error(ErrorKind.NotFound, "user.not_found", "gone")));

        var act = () => Handler.Handle(new BuildOrderExport(ExportId, OwnerId), CancellationToken.None);

        return act.Should().ThrowAsync<InvalidOperationException>();
    }
```

Run: `dotnet test tests/Application.Tests --filter "FullyQualifiedName~BuildOrderExportHandlerTests"`
Expected: build FAILS — no `BuildOrderExportHandler` constructor takes 4 arguments.

- [ ] **Step 2: Build the PDF in the job**

In `BuildOrderExport.cs`: the `BuildOrderExport` summary's first sentence becomes "Builds an export's PDF." The handler:

```csharp
public sealed class BuildOrderExportHandler(
    IQueryDispatcher queries, ICommandDispatcher commands, IOrderExportRenderer renderer, IClock clock)
{
```

and after the paging loop, replacing the completion block:

```csharp
        // The owner's display name heads the document. The job runs as the owner, so this is their
        // own record; a failure is thrown like every other, for Wolverine's retry policy to see.
        var owner = await queries.SendAsync(new GetUser(job.OwnerId), cancellationToken).ConfigureAwait(false);
        if (!owner.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Building order export {job.ExportId} failed reading its owner: {owner.Error.Code}.");
        }

        var document = renderer.Render(OrderExportReport.Create(rows, owner.Value.DisplayName, clock.UtcNow));

        var completed = await commands
            .SendAsync(new CompleteOrderExport(job.ExportId, document, rows.Count), cancellationToken)
            .ConfigureAwait(false);
```

Remove `using System.Text;`; add `using AiFramework.Application.Users;`.

- [ ] **Step 3: Delete the CSV**

Create `src/Application/Orders/OrderExportRow.cs` holding exactly the `OrderExportRow` record from `OrderExportCsv.cs`, with its summary changed to "One order as the export reads it." Then:

```bash
git rm src/Application/Orders/OrderExportCsv.cs tests/Application.Tests/Orders/OrderExportCsvTests.cs
```

In `OrderExportHandlerTests.cs` rename `GetOrderExportRows_MapsEveryColumnTheCsvNeeds` to `GetOrderExportRows_MapsEveryColumnTheExportNeeds`.

- [ ] **Step 4: Name the file .pdf and serve it as a PDF**

`OrderExportQueries.cs`, `GetOrderExportFileHandler`: `$"orders-{file.RequestedAt.UtcDateTime:yyyy-MM-dd}.pdf"`; in `OrderExportHandlerTests` the file-name assertion becomes `"orders-2026-10-03.pdf"`.

`OrderExportsController.cs`: `RequestExport`'s summary "Asks for a PDF of every order the caller has placed. …"; `Download`:

```csharp
    /// <summary>
    /// The PDF of a Ready export. 404 if it is someone else's or not built yet — the same answer for
    /// both, so an export id reveals nothing. The viewer fetches this same URL for the bytes.
    /// </summary>
    [HttpGet("{id:guid}/download")]
    [ProducesResponseType(typeof(Stream), StatusCodes.Status200OK, "application/pdf")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Download(Guid id, CancellationToken cancellationToken)
    {
        var result = await queries.SendAsync(new GetOrderExportFile(id), cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? File(result.Value.Document, "application/pdf", result.Value.FileName)
            : result.Problem(HttpContext);
    }
```

Remove `using System.Text;`.

- [ ] **Step 5: Update the Api and Worker tests**

`OrderExportsEndpointTests.cs`: replace `CsvBytes` with `private static readonly byte[] PdfBytes = "%PDF-1.7 test"u8.ToArray();` (used by `CompleteAsync`). Replace `Download_OfABuiltExport_IsTheCsvAsAnAttachment` and `Download_StartsWithAByteOrderMarkSoSpreadsheetsReadUtf8` with:

```csharp
    [Fact]
    public async Task Download_OfABuiltExport_IsThePdfAsAnAttachment()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();
        var export = await RequestAsync(client);
        await CompleteAsync(export.Id);

        var response = await client.GetAsync(new Uri($"/api/orders/exports/{export.Id}/download", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        response.Content.Headers.ContentDisposition.FileName.Should()
            .Be($"orders-{export.RequestedAt.UtcDateTime:yyyy-MM-dd}.pdf");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(PdfBytes);
    }
```

and remove `using System.Text;` if now unused.

`OrderExportBuildTests.cs`: rename `BuildOrderExport_StoresACsvOfTheOwnersOrdersOnly` → `BuildOrderExport_StoresAPdfOfTheOwnersOrdersOnly`; replace its file assertion with a PdfPig read. Add `PdfPig` 0.1.16 to `tests/Worker.IntegrationTests` (`dotnet add tests/Worker.IntegrationTests package PdfPig --version 0.1.16`), `using UglyToad.PdfPig;`, and:

```csharp
        using var pdf = PdfDocument.Open(export.Document!);
        var text = string.Join(" ", pdf.GetPages().SelectMany(p => p.GetWords()).Select(w => w.Text));
        text.Should().Contain("SKU-EXPORT-MINE-1").And.Contain("SKU-EXPORT-MINE-2")
            .And.NotContain("SKU-EXPORT-SOMEONE-ELSES");
```

The job now reads its owner through `GetUser`, and no worker test seeds a `users` row, so every test in `OrderExportBuildTests` that builds an export needs a registered owner. Add this helper (`using AiFramework.Domain.Users;`) and replace `var ownerId = Guid.NewGuid();` with `var ownerId = await RegisterOwnerAsync();` in each test there that enqueues `BuildOrderExport` for an export it requested (`…StoresAPdf…`, `…DeliveredTwice…`, `…WritesTheCompletedEvent…`, and any other). `JobDeliveryTests` and `JobRunRecordingTests` enqueue builds for exports that do not exist; those stop at NotFound before `GetUser` and need no change.

```csharp
    private async Task<Guid> RegisterOwnerAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
        var owner = User.Register(
            Guid.NewGuid(), $"export{Guid.NewGuid():N}"[..32], "not-a-real-hash", "Export Owner", DateTimeOffset.UtcNow);
        context.Users.Add(owner);
        await context.SaveChangesAsync();
        return owner.Id;
    }
```

- [ ] **Step 6: Regenerate the worker's adapters and the API contract**

Load the `regenerate` skill and run its commands for **both** Wolverine trees and the contract. Expected diffs: `src/Worker/Internal/Generated/WolverineHandlers/BuildOrderExportHandler*.cs` (new constructor arguments), `openapi/AiFramework.Api.json` and `frontend/src/api/schema.d.ts` (`text/csv` → `application/pdf` on the download). If the only change in an adapter is statement ordering in a handler you did not touch, keep the committed version (CI's Linux output is the authority).

- [ ] **Step 7: Run every affected suite, in both configurations**

Run: `dotnet build AiFramework.slnx -c Release` and `dotnet test --filter "FullyQualifiedName~OrderExport"` (Docker running). Then `dotnet test tests/Worker.IntegrationTests -c Release --filter "FullyQualifiedName~OrderExport"` — Release is where a stale adapter fails, at startup.
Expected: all PASS.

- [ ] **Step 8: Commit**

```bash
git add -A src tests openapi frontend/src/api/schema.d.ts
git commit -m "feat(orders)!: build and serve the order export as a PDF" -m "The build job renders the report through IOrderExportRenderer; the download answers application/pdf. The CSV writer is gone." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Fetching the file as bytes

**Files:**
- Modify: `frontend/src/api/client.ts`, `frontend/src/api/orders.ts`, `frontend/src/features/orders/queries.ts`
- Modify: `frontend/src/test/handlers.ts` (a download handler)
- Test: `frontend/src/features/orders/queries.test.tsx`

**Interfaces:**
- Produces: `requestBytes(path: string, init?: RequestInit): Promise<ArrayBuffer>`; `getOrderExportDocument(id: string): Promise<ArrayBuffer>`; `orderKeys.exportDocument(id)`; `useOrderExportDocument(id: string): UseQueryResult<ArrayBuffer, ApiError>`; MSW fixture `aPdf: Uint8Array`.

- [ ] **Step 1: Add the MSW download handler**

In `frontend/src/test/handlers.ts`, after `aBuildingExport`:

```ts
/** Bytes that start like a PDF. Unit tests never draw it: react-pdf is stubbed (OrderExportsPage.test.tsx). */
export const aPdf = new TextEncoder().encode('%PDF-1.7 test');
```

and in the handler list, before `http.get('/api/orders/:id', …)`:

```ts
  http.get('/api/orders/exports/:id/download', () =>
    HttpResponse.arrayBuffer(aPdf.slice().buffer, { headers: { 'Content-Type': 'application/pdf' } }),
  ),
```

- [ ] **Step 2: Write the failing hook tests**

Append to `frontend/src/features/orders/queries.test.tsx` (load the `react-testing` skill first; follow the file's existing imports and `renderHook` + `withQueryClient` pattern):

```tsx
describe('useOrderExportDocument', () => {
  it('fetches the export file as bytes from its download URL', async () => {
    let requested = '';
    server.use(
      http.get('/api/orders/exports/:id/download', ({ request }) => {
        requested = new URL(request.url).pathname;
        return HttpResponse.arrayBuffer(aPdf.slice().buffer);
      }),
    );

    const { result } = renderHook(() => useOrderExportDocument(anOrderExport.id), { wrapper: withQueryClient() });

    await waitFor(() => { expect(result.current.isSuccess).toBe(true); });
    expect(requested).toBe(`/api/orders/exports/${anOrderExport.id}/download`);
    expect(new Uint8Array(result.current.data ?? new ArrayBuffer(0))).toEqual(aPdf);
  });

  it('reports a missing export as an ApiError', async () => {
    server.use(
      http.get('/api/orders/exports/:id/download', () =>
        HttpResponse.json({ title: 'Not found', detail: 'That export does not exist or is not ready.' }, { status: 404 }),
      ),
    );

    const { result } = renderHook(() => useOrderExportDocument(anOrderExport.id), { wrapper: withQueryClient() });

    await waitFor(() => { expect(result.current.isError).toBe(true); });
    expect(result.current.error?.status).toBe(404);
  });
});
```

Run: `npm test --prefix frontend -- --run src/features/orders/queries.test.tsx`
Expected: FAIL — `useOrderExportDocument` is not exported.

- [ ] **Step 3: Implement**

`client.ts`, after `requestVoid`:

```ts
/** For the endpoints that answer a file. Same error handling as `request`. */
export async function requestBytes(path: string, init?: RequestInit): Promise<ArrayBuffer> {
  return (await send(path, init)).arrayBuffer();
}
```

`api/orders.ts`, after `orderExportDownloadUrl` (import `requestBytes`):

```ts
// The viewer's copy of the same file the link downloads.
export function getOrderExportDocument(id: string): Promise<ArrayBuffer> {
  return requestBytes(orderExportDownloadUrl(id));
}
```

`queries.ts`: add to `orderKeys`

```ts
  // Not under exports(): invalidating the list must not refetch an open viewer's file.
  exportDocument: (id: string) => [...orderKeys.all, 'export-document', id] as const,
```

and

```ts
export function useOrderExportDocument(id: string): UseQueryResult<ArrayBuffer, ApiError> {
  return useQuery({
    queryKey: orderKeys.exportDocument(id),
    queryFn: () => getOrderExportDocument(id),
    // A Ready export never changes, so nothing to refetch while the viewer is open.
    staleTime: Infinity,
    // Someone's order history: dropped as soon as the viewer closes, not kept for five minutes.
    gcTime: 0,
  });
}
```

- [ ] **Step 4: Run the tests**

Run: `npm test --prefix frontend -- --run src/features/orders` and `npm run lint --prefix frontend`
Expected: PASS, lint clean.

- [ ] **Step 5: Commit**

```bash
git add frontend/src
git commit -m "feat(orders): fetch an export's file as bytes" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: The viewer

**Files:**
- Modify: `frontend/package.json`, `frontend/package-lock.json` (react-pdf)
- Modify: `frontend/src/test/setup.ts` (a `<dialog>` stub for jsdom)
- Create: `frontend/src/features/orders/ExportViewer.tsx` (lazy: react-pdf lives only here)
- Create: `frontend/src/features/orders/ExportViewerDialog.tsx`
- Modify: `frontend/src/features/orders/OrderExportsPage.tsx`, `frontend/src/features/orders/orders.css`
- Test: `frontend/src/features/orders/OrderExportsPage.test.tsx`

**Interfaces:**
- Consumes: `useOrderExportDocument`, `orderExportDownloadUrl`, `OrderExport` (Task 5).
- Produces: `ExportViewerDialog({ exportItem, onClose }: { exportItem: OrderExport | null; onClose: () => void })`; default export `ExportViewer({ id }: { id: string })`.

- [ ] **Step 1: Install react-pdf**

Run: `npm install --prefix frontend react-pdf@11.0.0`
Expected: `react-pdf` 11.0.0 in `dependencies`, `pdfjs-dist` 6.3.x in the lockfile, `npm ls react-pdf` clean.

- [ ] **Step 2: Give jsdom enough of `<dialog>` and `ResizeObserver`**

jsdom 30 has `HTMLDialogElement` but no `showModal`, `close` or `open`. Append to `frontend/src/test/setup.ts`:

```ts
// jsdom 30 has HTMLDialogElement and none of its behaviour. Enough of it to open a dialog, find
// what is in it, and close it - nothing more: Esc, the focus trap and returning focus are the
// browser's, and Playwright tests them (e2e/specs/orders/exports.spec.ts).
if (typeof HTMLDialogElement.prototype.showModal !== 'function') {
  Object.defineProperty(HTMLDialogElement.prototype, 'open', {
    configurable: true,
    get(this: HTMLDialogElement) {
      return this.hasAttribute('open');
    },
  });
  HTMLDialogElement.prototype.showModal = function showModal(this: HTMLDialogElement) {
    this.setAttribute('open', '');
  };
  HTMLDialogElement.prototype.close = function close(this: HTMLDialogElement) {
    if (this.hasAttribute('open')) {
      this.removeAttribute('open');
      this.dispatchEvent(new Event('close'));
    }
  };
}

// jsdom has no ResizeObserver either; the export viewer fits its pages to the width it reports.
// A stub that never reports: the viewer then renders at react-pdf's default width.
if (typeof globalThis.ResizeObserver === 'undefined') {
  globalThis.ResizeObserver = class {
    observe(): void {}
    unobserve(): void {}
    disconnect(): void {}
  };
}
```

- [ ] **Step 3: Write the failing page tests**

At the top of `OrderExportsPage.test.tsx`, stub react-pdf (it cannot draw in jsdom) — the stub records each `file` it receives so Review Focus 4 can be pinned:

```tsx
// react-pdf is stubbed: pdf.js cannot draw in jsdom. This is a rendering library, not the API
// client the react-testing skill forbids mocking - the request still goes through MSW. Real
// drawing is Playwright's job (e2e/specs/orders/exports.spec.ts).
const received = vi.hoisted(() => [] as { data: Uint8Array }[]);
vi.mock('react-pdf', () => ({
  pdfjs: { GlobalWorkerOptions: {} },
  Document: ({ file, children, onLoadSuccess }: {
    file: { data: Uint8Array };
    children: React.ReactNode;
    onLoadSuccess?: (pdf: { numPages: number }) => void;
  }) => {
    received.push(file);
    queueMicrotask(() => onLoadSuccess?.({ numPages: 2 }));
    return <div data-testid="pdf-document">{children}</div>;
  },
  Page: ({ pageNumber }: { pageNumber: number }) => <div>{`pdf page ${String(pageNumber)}`}</div>,
}));
vi.mock('react-pdf/dist/Page/TextLayer.css', () => ({}));
```

Then add, inside `describe('OrderExportsPage', …)`:

```tsx
  it('offers to view a ready export', async () => {
    renderPage();

    expect(await screen.findByRole('button', { name: /^View export requested / })).toBeInTheDocument();
  });

  it('opens the export in a viewer, page by page', async () => {
    renderPage();

    await userEvent.click(await screen.findByRole('button', { name: /^View export requested / }));

    const dialog = await screen.findByRole('dialog', { name: /^Export requested / });
    expect(await within(dialog).findByText('pdf page 2')).toBeInTheDocument();
    expect(within(dialog).getByText('Page 1 of 2')).toBeInTheDocument();
    expect(within(dialog).getByRole('link', { name: 'Download' }))
      .toHaveAttribute('href', `/api/orders/exports/${anOrderExport.id}/download`);
  });

  it('says it is loading while the file is fetched', async () => {
    server.use(http.get('/api/orders/exports/:id/download', async () => {
      await delay('infinite');
      return HttpResponse.arrayBuffer(new ArrayBuffer(0));
    }));
    renderPage();

    await userEvent.click(await screen.findByRole('button', { name: /^View export requested / }));

    expect(await screen.findByText('Loading export…')).toBeInTheDocument();
  });

  it('renders the failure when the file cannot be fetched', async () => {
    server.use(http.get('/api/orders/exports/:id/download', () =>
      HttpResponse.json({ title: 'Not found', detail: 'That export does not exist or is not ready.' }, { status: 404 })));
    renderPage();

    await userEvent.click(await screen.findByRole('button', { name: /^View export requested / }));

    expect(await screen.findByText('That export does not exist or is not ready.')).toBeInTheDocument();
  });

  it('closes the viewer', async () => {
    renderPage();
    await userEvent.click(await screen.findByRole('button', { name: /^View export requested / }));
    const dialog = await screen.findByRole('dialog', { name: /^Export requested / });

    await userEvent.click(within(dialog).getByRole('button', { name: 'Close' }));

    // By name: a closed <dialog> may stay in jsdom's tree, but its title goes with its content.
    await waitFor(() => { expect(screen.queryByRole('dialog', { name: /^Export requested / })).not.toBeInTheDocument(); });
  });

  // Review Focus 4: pdf.js detaches the buffer it is given, so each open must hand it a fresh copy.
  it('gives the viewer a fresh copy of the file every time it opens', async () => {
    received.length = 0;
    renderPage();
    const view = await screen.findByRole('button', { name: /^View export requested / });

    await userEvent.click(view);
    await userEvent.click(
      within(await screen.findByRole('dialog', { name: /^Export requested / })).getByRole('button', { name: 'Close' }),
    );
    await userEvent.click(view);
    await screen.findByRole('dialog', { name: /^Export requested / });

    await waitFor(() => { expect(received.length).toBeGreaterThanOrEqual(2); });
    expect(received[0]?.data).not.toBe(received.at(-1)?.data);
    expect(received.at(-1)?.data).toEqual(aPdf);
  });

  it('describes the export as a PDF to share', async () => {
    renderPage();

    expect(await screen.findByText(/^A PDF of every order you have placed, ready to share\./)).toBeInTheDocument();
  });
```

Add `within`, `waitFor` to the `@testing-library/react` import, `delay` to the `msw` import, and `aPdf` to the handlers import.

Run: `npm test --prefix frontend -- --run src/features/orders/OrderExportsPage.test.tsx`
Expected: the new tests FAIL (no View button, no dialog, old subtitle).

- [ ] **Step 4: Implement the lazy viewer**

`frontend/src/features/orders/ExportViewer.tsx`:

```tsx
import { useEffect, useMemo, useRef, useState } from 'react';
import { Document, Page, pdfjs } from 'react-pdf';
import 'react-pdf/dist/Page/TextLayer.css';
import { ErrorPanel } from '../../components/ErrorPanel';
import { orderExportDownloadUrl } from '../../api/orders';
import { useOrderExportDocument } from './queries';

// In this module, not main.tsx: react-pdf's docs warn that a workerSrc set elsewhere can be
// overwritten by its own default, depending on module order. Bundled by Vite and served from this
// origin (nginx.conf maps .mjs to JavaScript - without it the worker never starts in the cluster).
pdfjs.GlobalWorkerOptions.workerSrc = new URL('pdfjs-dist/build/pdf.worker.min.mjs', import.meta.url).toString();

const ZOOM_STEP = 0.2;
const MIN_ZOOM = 0.6;
const MAX_ZOOM = 2.4;

interface ExportViewerProps {
  readonly id: string;
}

/**
 * The export, drawn by pdf.js. Lazy-loaded (ExportViewerDialog), so pdf.js downloads only on the
 * first View. Fit-to-width by default; zoom multiplies the fitted width.
 */
export default function ExportViewer({ id }: ExportViewerProps): React.JSX.Element {
  const { data, error, isPending } = useOrderExportDocument(id);
  const [pages, setPages] = useState(0);
  const [current, setCurrent] = useState(1);
  const [zoom, setZoom] = useState(1);
  const [width, setWidth] = useState(0);
  const body = useRef<HTMLDivElement>(null);

  // pdf.js transfers (detaches) the buffer it is given, so it gets a copy: handing it the cached
  // one would leave nothing to draw the next time the viewer opens.
  const file = useMemo(() => (data === undefined ? undefined : { data: new Uint8Array(data.slice(0)) }), [data]);

  useEffect(() => {
    const element = body.current;
    if (element === null) {
      return undefined;
    }
    const observer = new ResizeObserver(([entry]) => {
      setWidth(entry?.contentRect.width ?? 0);
    });
    observer.observe(element);
    return () => {
      observer.disconnect();
    };
  }, []);

  function onScroll(): void {
    const element = body.current;
    if (element === null || pages === 0) {
      return;
    }
    const pageHeight = element.scrollHeight / pages;
    setCurrent(Math.min(pages, Math.floor(element.scrollTop / pageHeight) + 1));
  }

  return (
    <>
      <div className="export-viewer__toolbar" role="toolbar" aria-label="Viewer controls">
        <span className="export-viewer__pages">{pages > 0 ? `Page ${String(current)} of ${String(pages)}` : ''}</span>
        <button className="btn btn--secondary" type="button" aria-label="Zoom out"
          onClick={() => { setZoom((z) => Math.max(MIN_ZOOM, z - ZOOM_STEP)); }}>−</button>
        <button className="btn btn--secondary" type="button" aria-label="Zoom in"
          onClick={() => { setZoom((z) => Math.min(MAX_ZOOM, z + ZOOM_STEP)); }}>+</button>
        <button className="btn btn--secondary" type="button"
          onClick={() => { setZoom(1); }}>Fit to width</button>
        <a className="btn btn--secondary" href={orderExportDownloadUrl(id)} download>Download</a>
      </div>
      <div className="export-viewer__body" ref={body} onScroll={onScroll}>
        {isPending && (
          <div className="orders__state">
            <span className="spinner" aria-hidden="true" />
            <p role="status">Loading export…</p>
          </div>
        )}
        {error && <ErrorPanel error={error} />}
        {file && (
          <Document
            file={file}
            suspense={false}
            loading={<p role="status">Loading export…</p>}
            error={<p role="alert">This export couldn&apos;t be displayed. You can still download it.</p>}
            onLoadSuccess={(pdf) => { setPages(pdf.numPages); }}
          >
            {Array.from({ length: pages }, (_, i) => (
              <Page
                key={i + 1}
                className="export-viewer__page"
                pageNumber={i + 1}
                width={width > 0 ? width * zoom : undefined}
                renderAnnotationLayer={false}
                renderTextLayer
              />
            ))}
          </Document>
        )}
      </div>
    </>
  );
}
```

If `react-pdf`'s `Document` props or the `pdf` type of `onLoadSuccess` differ from the above under `tsc -b`, follow the types in `node_modules/react-pdf/dist/*.d.ts` (11.0.0) — the README excerpt this plan was written from is `suspense={false}` + `loading`/`error` props + `onLoadSuccess({ numPages })`.

- [ ] **Step 5: Implement the dialog**

`frontend/src/features/orders/ExportViewerDialog.tsx`:

```tsx
import { Suspense, lazy, useEffect, useId, useRef } from 'react';
import type { OrderExport } from './types';

const ExportViewer = lazy(() => import('./ExportViewer'));

interface ExportViewerDialogProps {
  readonly exportItem: OrderExport | null;
  readonly onClose: () => void;
}

/**
 * The app's first modal: a native <dialog> opened with showModal(), so the browser supplies the
 * focus trap, Esc, the inert page behind it, and focus returning to the View button on close.
 * Full screen below 40rem (orders.css).
 */
export function ExportViewerDialog({ exportItem, onClose }: ExportViewerDialogProps): React.JSX.Element {
  const dialog = useRef<HTMLDialogElement>(null);
  const titleId = useId();

  useEffect(() => {
    const element = dialog.current;
    if (element === null) {
      return;
    }
    if (exportItem !== null && !element.open) {
      element.showModal();
    } else if (exportItem === null && element.open) {
      element.close();
    }
  }, [exportItem]);

  return (
    // onClose fires for Esc and for close() alike, so the page's state follows the browser's.
    <dialog ref={dialog} className="export-viewer" aria-labelledby={titleId} onClose={onClose}>
      {exportItem !== null && (
        <>
          <div className="export-viewer__header">
            <h2 id={titleId} className="export-viewer__title">
              {`Export requested ${new Date(exportItem.requestedAt).toLocaleString()}`}
            </h2>
            <button className="btn btn--secondary" type="button" onClick={() => { dialog.current?.close(); }}>
              Close
            </button>
          </div>
          <Suspense
            fallback={
              <div className="orders__state">
                <span className="spinner" aria-hidden="true" />
                <p role="status">Loading export…</p>
              </div>
            }
          >
            <ExportViewer id={exportItem.id} />
          </Suspense>
        </>
      )}
    </dialog>
  );
}
```

- [ ] **Step 6: Wire it into the page**

In `OrderExportsPage.tsx`:
- import `useState` from React and `ExportViewerDialog` from `./ExportViewerDialog`;
- `Header`'s subtitle text becomes exactly: `A PDF of every order you have placed, ready to share. You are notified when it is ready; exports are kept for seven days.`
- `Exports` gains `const [viewing, setViewing] = useState<OrderExport | null>(null);`, renders `<ExportViewerDialog exportItem={viewing} onClose={() => { setViewing(null); }} />` after the table card, and the Ready cell becomes:

```tsx
                    <td className="orders__actions">
                      {exp.status === 'Ready' && (
                        <>
                          <button
                            className="btn btn--secondary"
                            type="button"
                            onClick={() => { setViewing(exp); }}
                            aria-label={`View export requested ${requested}`}
                          >
                            View
                          </button>
                          <a
                            className="btn btn--secondary"
                            href={orderExportDownloadUrl(exp.id)}
                            download
                            // Each row's link says which export it is: a screen reader's link list
                            // would otherwise show a column of identical "Download"s.
                            aria-label={`Download export requested ${requested}`}
                          >
                            Download
                          </a>
                        </>
                      )}
                    </td>
```

- the visually hidden column heading `Download` becomes `Actions`.

Append to `orders.css`:

```css
.orders__actions {
  display: flex;
  gap: var(--space-2, 0.5rem);
  justify-content: flex-end;
}

/* The export viewer: full screen on phones, a large panel above 40rem. */
.export-viewer {
  border: none;
  padding: 0;
  width: 100vw;
  height: 100dvh;
  max-width: none;
  max-height: none;
  background: var(--color-surface);
  color: var(--color-text);
}

.export-viewer[open] {
  display: flex;
  flex-direction: column;
}

.export-viewer::backdrop {
  background: rgb(12 14 20 / 60%);
}

.export-viewer__header,
.export-viewer__toolbar {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.75rem 1rem;
  border-bottom: 1px solid var(--color-border);
}

.export-viewer__title {
  flex: 1;
  margin: 0;
  font-size: var(--text-base);
}

.export-viewer__pages {
  flex: 1;
  color: var(--color-text-muted);
  font-size: var(--text-sm);
}

.export-viewer__body {
  flex: 1;
  overflow: auto;
  padding: 1rem;
  background: var(--color-canvas);
}

.export-viewer__page {
  margin: 0 auto 1rem;
  box-shadow: 0 1px 3px rgb(18 21 28 / 15%);
}

@media (min-width: 40rem) {
  .export-viewer {
    width: min(72rem, 92vw);
    height: 90dvh;
    border-radius: 0.75rem;
  }
}
```

(Check `tokens.css` for the spacing token's real name before using `--space-2`; fall back to the literal if there is none.)

- [ ] **Step 7: Run the tests, lint and build**

Run: `npm test --prefix frontend -- --run` then `npm run lint --prefix frontend` then `npm run build --prefix frontend`
Expected: all PASS; lint clean; the build prints a separate chunk for `ExportViewer` (pdf.js) and `pdf.worker.min-*.mjs` under `dist/assets/`, and the main chunk is not larger by pdf.js's size.

- [ ] **Step 8: Commit**

```bash
git add frontend
git commit -m "feat(orders): read an export in the app" -m "A native <dialog> with a lazily loaded react-pdf viewer: pdf.js downloads only on the first View." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Production serving and end-to-end

**Files:**
- Modify: `frontend/nginx.conf`
- Modify: `frontend/e2e/screens/exports.ts`, `frontend/e2e/specs/orders/exports.spec.ts`

- [ ] **Step 1: Serve .mjs as JavaScript in nginx**

nginx's stock `mime.types` maps only `.js`; the pdf.js worker is `.mjs`, and a module worker served as `application/octet-stream` is refused by the browser. In `frontend/nginx.conf`, replace the `/assets/` location with:

```nginx
    location /assets/ {
        add_header Cache-Control "public, max-age=31536000, immutable";

        # The pdf.js worker (the order export viewer) is an ES module, .mjs, which nginx's stock
        # mime.types does not map: served as application/octet-stream, the browser refuses to run
        # it and the viewer never draws. An empty `types` here, so default_type applies to it alone.
        location ~ \.mjs$ {
            types { }
            default_type text/javascript;
        }
    }
```

(The nested location has no `add_header` of its own, so it inherits the immutable `Cache-Control`.)

- [ ] **Step 2: Check it against the real image**

With Docker running, from the repo root:

```bash
docker build -f Dockerfile.web -t aiframework-web-check .
docker run --rm -d -p 18080:8080 --name web-check aiframework-web-check
curl -sI "http://localhost:18080/assets/$(ls frontend/dist/assets | grep -m1 'pdf.worker.*\.mjs$')" | grep -iE "^(HTTP|content-type|cache-control)"
docker stop web-check
```

Expected: `HTTP/1.1 200`, `Content-Type: text/javascript`, `Cache-Control: public, max-age=31536000, immutable`. (Use the file name from the image's build, not your local `dist`, if they differ: `docker run --rm aiframework-web-check ls /usr/share/nginx/html/assets`.)

- [ ] **Step 3: Update the e2e screen**

In `frontend/e2e/screens/exports.ts` add:

```ts
/** The newest Ready export's View button. */
export const viewButton = (p: Page): Locator =>
  p.getByRole('table', { name: 'Exports' }).getByRole('button', { name: /^View export requested / }).first();

/** The open viewer. */
export const viewer = (p: Page): Locator => p.getByRole('dialog', { name: /^Export requested / });
```

- [ ] **Step 4: Update the e2e spec**

In `frontend/e2e/specs/orders/exports.spec.ts`: the test title becomes `'exports the caller\'s orders to a PDF they can read and download'`; the file comment's "downloaded as a file" becomes "read in the viewer and downloaded as a PDF". Replace the `download it` step and add the viewer step:

```ts
    await test.step('read it in the viewer', async () => {
      await signedInPage.reload();
      const view = exportsPage.viewButton(signedInPage);

      // Twice: pdf.js detaches the buffer it draws from, and a second open must still draw.
      for (const attempt of [1, 2]) {
        await view.click();
        const viewer = exportsPage.viewer(signedInPage);
        await expect(viewer).toBeVisible();
        // The text layer: proof pdf.js drew the real document, not just a canvas.
        // .first(): the title and every page footer both say "Order history".
        await expect(viewer.getByText('Order history').first()).toBeVisible();
        await expect(viewer.getByText(sku, { exact: true })).toBeVisible();

        // The browser's own dialog behaviour, which jsdom cannot show: Esc closes it and focus
        // returns to the button that opened it.
        await signedInPage.keyboard.press('Escape');
        await expect(viewer).toBeHidden();
        await expect(view, `focus after closing, attempt ${String(attempt)}`).toBeFocused();
      }
    });

    await test.step('download it', async () => {
      const [download] = await Promise.all([
        signedInPage.waitForEvent('download'),
        exportsPage.downloadLink(signedInPage).click(),
      ]);

      expect(download.suggestedFilename()).toMatch(/^orders-\d{4}-\d{2}-\d{2}\.pdf$/);
      const pdf = await readFile(await download.path());
      expect(pdf.subarray(0, 5).toString('latin1')).toBe('%PDF-');
    });
```

- [ ] **Step 5: Run the e2e spec**

Stop the dev loop first (`./scripts/stop-dev.ps1`), then:
Run: `npm run e2e --prefix frontend -- --grep "order exports"`
Expected: PASS. A failure to find "Order history" with a visible canvas means the text layer is off or its CSS is missing; a blank viewer means the worker did not load (check the browser console in the trace).

- [ ] **Step 6: Commit**

```bash
git add frontend/nginx.conf frontend/e2e
git commit -m "feat(orders): serve the PDF viewer's worker and test the viewer end to end" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Docs

**Files:**
- Create: `docs/adr/0030-order-exports-are-pdfs-rendered-by-migradoc.md`
- Modify: `docs/adr/0029-order-exports-are-stored-in-postgres-and-fail-by-staleness.md` (status line only)
- Modify: `README.md` (export rows, ADR list), `frontend/CLAUDE.md`
- Modify: `docs/superpowers/specs/2026-10-06-order-export-pdf-design.md` (status: built)

- [ ] **Step 1: Write ADR 0030**

Use the `adr` command's format (read `docs/adr/0029-…` for house style). Content, in sections Context / Decision / Consequences / Alternatives considered:
- **Context:** the export was a CSV (ADR 0029); users share it with people outside the app; it must be presentable and readable in the app on any device.
- **Decision:** a PDF rendered in the worker by PDFsharp/MigraDoc 6.2.4 (MIT, managed) behind `IOrderExportRenderer`; `OrderExportReport` holds what the document says; Noto Sans embedded and served for every family name (MigraDoc's Courier New error font); stored as `bytea`; Ready CSV exports deleted by `StoreOrderExportsAsBytes`; the app reads it with react-pdf (pdf.js) in a native `<dialog>`, lazily loaded, worker same-origin, `.mjs` mapped in nginx.
- **Consequences:** CJK renders as missing glyphs; the palette is a copy of `tokens.css` (`OrderExportPalette`), held to WCAG AA by a test; old Ready exports are gone; rolling the deploy briefly fails a CSV-era worker's build (retried) and a CSV-era API pod's download (500 until replaced); every page renders in the viewer (no windowing); the formula-injection guard and the byte-order mark are gone with the CSV.
- **Alternatives:** QuestPDF (native Skia on a chiseled image with no fonts; revenue-based licence); headless Chromium (a browser in the worker image, not chiseled); the browser's built-in viewer (most mobile browsers cannot embed a PDF); keeping CSV beside PDF (not wanted).

- [ ] **Step 2: Point ADR 0029 at it**

Change 0029's status line to: `**Status:** Accepted. Its CSV format, formula guard and byte-order mark are superseded by [ADR 0030](0030-order-exports-are-pdfs-rendered-by-migradoc.md); storage, the outbox, staleness and retention stand.`

- [ ] **Step 3: README and frontend/CLAUDE.md**

README: the `POST /api/orders/exports` row says "Ask for a PDF of all your orders; …"; the download row says "The PDF; 404 if it is someone else's or not built yet"; add `| [0030](docs/adr/0030-order-exports-are-pdfs-rendered-by-migradoc.md) | Order exports are PDFs rendered by MigraDoc |` after 0029.

`frontend/CLAUDE.md`, a new section after "The API contract":

```markdown
## The export viewer

`/orders/exports` reads a PDF with react-pdf (pdf.js) inside a native `<dialog>` (ADR 0030).

- **`ExportViewer.tsx` is lazy-loaded** (`ExportViewerDialog.tsx`), so pdf.js — about 1 MB — loads only
  on the first View. Do not import `react-pdf` anywhere else, or it lands in the main bundle.
- **`workerSrc` is set in `ExportViewer.tsx`**, the module that renders `<Document>`: react-pdf warns that
  one set elsewhere can be overwritten by its default. The worker is bundled and same-origin.
- **The worker is `.mjs`, which nginx does not map by default.** `nginx.conf` maps it under `/assets/`;
  without that the viewer never draws in the cluster while working in dev and e2e (both run Vite).
- **pdf.js detaches the buffer it is given**, so the viewer hands it a copy. Passing the query's cached
  `ArrayBuffer` breaks the second open.
- **Unit tests stub `react-pdf`** (pdf.js cannot draw in jsdom) and `src/test/setup.ts` stubs `<dialog>`
  (jsdom has none of its behaviour). Drawing, Esc and focus return are tested in Playwright.
```

- [ ] **Step 4: Mark the spec built**

In the spec's header: `**Status:** Approved and built (<date>). Recorded as ADR 0030.`

- [ ] **Step 5: Commit**

```bash
git add docs README.md frontend/CLAUDE.md
git commit -m "docs(orders): record order exports as PDFs in ADR 0030" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Verify and open the pull request

- [ ] **Step 1: /verify**

Run the `/verify` command (both stacks, codegen and contract diffs, e2e, hooks). Docker must be running. Expected: every step ran and passed; report any skipped step as skipped.

- [ ] **Step 2: Release build and worker startup**

Run: `dotnet build AiFramework.slnx -c Release` and `dotnet test tests/Worker.IntegrationTests -c Release`
Expected: PASS — a stale worker adapter fails here, at startup, and nowhere else.

- [ ] **Step 3: Reviews**

Dispatch the `dotnet-reviewer` and `react-reviewer` agents on the branch. Fix every Blocking or Should-fix finding in its own commit.

- [ ] **Step 4: Migrate the dev database**

```bash
ConnectionStrings__Default='Host=localhost;Port=55433;Database=aiframework;Username=aiframework;Password=aiframework' \
  dotnet ef database update --project src/Infrastructure --startup-project src/Infrastructure
```

(Take the dev connection string from `appsettings.Development.json` if it differs — see the `local-dev` skill.) Expected: `StoreOrderExportsAsBytes` applied. Then `./scripts/dev.ps1`, place an order, export, View, Download — the manual check of spec section 1's look.

- [ ] **Step 5: Push and open the PR**

```bash
git push -u origin claude/order-export-pdf
gh pr create --base main --title "feat(orders)!: export orders as a shareable PDF, viewable in the app" --body-file <body>
```

The body: what changed (spec sections 1–3 in a few lines), **the breaking change** (download is now `application/pdf`; Ready CSV exports are deleted by the migration), the rollout note (dev database needs `dotnet ef database update`), what was verified, and `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.

- [ ] **Step 6: Watch CI**

`gh pr checks <number> --watch`. Expected: all seven required checks green, plus CodeQL.
