# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Stack

- .NET 10 (LTS) — `brew install dotnet` (resolves to `/opt/homebrew/opt/dotnet`)
- Blazor Server (`dotnet new blazor` template; **interactivity = Server**, no WebAssembly)
- Quartz.NET (in-process, RAMJobStore)
- EF Core + SQLite for app config storage
- Provider drivers: `Oracle.ManagedDataAccess.Core`, `Microsoft.Data.SqlClient`
- DynamicExpresso for column-transform expressions
- Solution file is `EtlTool.slnx` (XML format — .NET 10 default, **not** `.sln`)

## Common Commands

The project root path contains a space (`/Volumes/ADATA SE770G/Code/etl-tool`). Quote it in shell commands.

```bash
# Build everything
dotnet build EtlTool.slnx

# Run the app (Blazor Server on http://localhost:5247)
dotnet run --project src/EtlTool.App

# Unit tests (no DB required)
dotnet test tests/EtlTool.Tests

# E2E integration tests (require docker-compose dev DBs + seeded tables)
dotnet test tests/EtlTool.IntegrationTests

# Run a single test
dotnet test tests/EtlTool.IntegrationTests \
  --filter "FullyQualifiedName~Oracle_to_MSSQL_Upsert"

# EF Core migrations (dotnet-ef must be installed globally)
dotnet ef migrations add <Name> \
  --project src/EtlTool.Data --startup-project src/EtlTool.Data
```

### E2E DB setup (one-time per fresh machine)

```bash
docker compose -f docker-compose.dev.yml up -d
# Oracle XE first init: ~30s with -faststart image, ~5-10min on cold cache
# Wait until: docker logs etltool-oracle | grep "DATABASE IS READY TO USE!"

# Seed sample tables (HR.EMPLOYEES_SRC/TGT and dbo.EMPLOYEES_SRC/TGT)
docker cp tests/scripts/seed-oracle.sql etltool-oracle:/tmp/seed.sql
docker exec etltool-oracle bash -c \
  'sqlplus -S system/oracle@//localhost:1521/XEPDB1 @/tmp/seed.sql'
docker cp tests/scripts/seed-mssql.sql etltool-mssql:/tmp/seed.sql
docker exec etltool-mssql /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "Dev_Password1!" -No -i /tmp/seed.sql
```

## Architecture

### Project dependency direction (do not break)

```
EtlTool.Core ◄── EtlTool.Connectors
       ▲     ◄── EtlTool.Data
       └────────────── EtlTool.App ──► both Connectors + Data
```

`EtlTool.Core` has **zero project references**. It defines the abstractions
(`IDbConnector`, `IConnectionStringProtector`, `IRunHistorySink`, `IConnectionLookup`,
`IEtlTaskLookup`, `IAllEtlTasksProvider`) plus the engine logic. `Connectors` and
`Data` each depend on `Core` and provide implementations. `App` is the only project
that wires them together via DI.

If you find yourself wanting to add a `Core → Connectors` or `Core → Data`
reference, you've taken a wrong turn — extract a new abstraction in Core instead.

### ETL execution flow

A single run, encapsulated in [`EtlEngine.ExecuteAsync`](src/EtlTool.Core/Engine/EtlEngine.cs):

1. Open source connection + target connection
2. **Begin DbTransaction on TARGET only** — the entire run is one transaction
3. If `WriteMode = DeleteInsert`: run `DELETE FROM target WHERE <delete-condition>` first
4. Build read SQL via `FilterCompiler` (form mode → parameterized) or raw WHERE
5. `ExecuteReader` on source — **streaming** (does not buffer the full result set)
6. Per row: `TransformEvaluator.Project()` applies any DynamicExpresso expressions
7. Buffer to `task.BatchSize` then flush via `IBulkWriter` (DeleteInsert) or
   `IUpsertWriter` (Upsert with key columns)
8. Commit on success → `RunHistory.Success`; any exception → rollback → `Failed`
9. RunHistory record gets generated SQL, sample payload (first 5 rows), error text

Source is streamed but target is one transaction — large data writes hold a long
transaction. Don't change this without surfacing it in the UI.

### Filter compilation

`FilterCompiler.Compile(FilterNode)` walks the tree and emits
`(whereSql, params)` using the **target connector's** quote style and parameter
prefix (`[X] @p` for SQL Server, `"X" :p` for Oracle). The same form-tree JSON
is compiled twice during a run: once with the source connector (for SELECT) and
once with the target connector (for DELETE if `DeleteWhereSameAsFilter`). They
share the form tree but produce dialect-specific SQL.

`FilterTreeJson` uses a custom `JsonConverter<FilterNode>` that dispatches on a
`"kind": "group"|"condition"` discriminator. **Do not strip this converter from
the options inside the converter** — `JsonSerializer.Deserialize<FilterGroup>`
already bypasses it for concrete subtypes, and stripping breaks nested children.

### Oracle array binding gotcha

`OracleBulkWriter` and `OracleUpsertWriter` use `cmd.ArrayBindCount = N` plus
`object?[]` parameter values. **You must construct each parameter as
`new OracleParameter(name, OracleDbType.X) { Value = arr }`** — not
`new OracleParameter(name, arr)`. The two-arg constructor expects a scalar
value and rejects arrays with `ArgumentException: Value does not fall within
the expected range`. The DbType is inferred from the first non-null element by
`OracleBulkWriter.InferOracleDbType()`.

### SQL Server upsert chunking

`SqlServerUpsertWriter` builds `MERGE ... USING (VALUES ...)` with one row per
parenthesized group. SQL Server caps queries at 2100 parameters; the writer
chunks each batch into sub-batches of `2000 / column_count` rows. If you add
columns to upsert payloads, this chunk size adjusts automatically.

### Scheduler

`SchedulerService` ([src/EtlTool.Core/Scheduling/SchedulerService.cs](src/EtlTool.Core/Scheduling/SchedulerService.cs))
uses Quartz's RAMJobStore — every app start rebuilds all job/triggers from the
SQLite `EtlTasks` table via `InitializeAsync()`. **There is no Quartz-side
persistence**, so any task CRUD must call `RescheduleAsync(taskId)` to keep
the in-memory scheduler in sync.

`EtlJob` is registered as scoped (`builder.Services.AddScoped<EtlJob>()`); the
default `MicrosoftDependencyInjectionJobFactory` shipped with
`Quartz.Extensions.Hosting` creates a fresh DI scope per execution.

### Razor Pages need `_ViewImports.cshtml` for TagHelpers

`Pages/_ViewImports.cshtml` registers `@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers`.
Without it, `asp-for`, `asp-action`, etc. are emitted as literal attributes on the rendered
HTML, so `<input asp-for="Username" />` becomes an input **with no `name` attribute** —
form POSTs silently fail validation. Symptom: login page "flashes" on submit because
ModelState is invalid (Username/Password are empty).

If you add a new Razor Page outside `Pages/`, mirror the file there too.

### Authentication / fallback authorization policy

`Program.cs` registers `options.FallbackPolicy = RequireAuthenticatedUser`. **This applies
to every endpoint by default — including static assets via `MapStaticAssets()`**, which
will silently redirect CSS / JS / favicon / Blazor JS to `/Account/Login`. The login page
then loads with no styling and the user thinks the app is broken.

Fix: `app.MapStaticAssets().AllowAnonymous()`. Same applies to any other endpoint that
must not require auth — `/healthz` is already explicitly anonymous.

### Connection-string encryption

Connection strings are encrypted by ASP.NET Core Data Protection
(`DataProtectionConnectionStringProtector`). Keys persist to
`<DataDirectory>/keys`. **If you delete that directory, all stored connection
strings become unrecoverable** — there is no master password fallback.

`DataDirectory` resolves in this order: `DataDirectory` config key →
`ETLTOOL_DATA_DIR` env var → `<ContentRoot>/data`.

### Blazor + interactivity

Pages use `@rendermode InteractiveServer`. Component data binding uses
`@bind` + `@bind:after` for change callbacks — **avoid manual
`@onchange="e => Foo(m, \"x\", e.Value)"` patterns**, the embedded `\"` doesn't
parse inside Razor attribute strings. Either use `@bind:after` or write
dedicated handler methods.

**Component parameter binding requires `@`**: when passing a non-literal value
to a child component, prefix with `@`:

```razor
<!-- BROKEN: passes literal string "_task.CronExpression" -->
<CronEditor Value="_task.CronExpression" />

<!-- CORRECT: evaluates expression -->
<CronEditor Value="@_task.CronExpression" ValueChanged="@(v => _task.CronExpression = v)" />
```

The compiler does not warn about this; you only see it in the rendered HTML.
For lambda callbacks, wrap them in `@(...)` too.

## Test layout

`tests/EtlTool.Tests` runs anywhere — pure unit tests on FilterCompiler,
TransformEvaluator, FilterTreeJson with stub connectors.

`tests/EtlTool.IntegrationTests` requires the docker-compose DBs to be **running
and seeded**. Each test calls `_fx.ResetTablesAsync()` first to put the live DBs
back to a known state. The `E2EFixture` builds a real DI container (sans Quartz
+ Blazor) using actual repositories and `EtlEngine` — it exercises the same
code paths the running app uses.

If integration tests fail on `Run failed: ...`, the test helper already prints
the run's `ErrorMessage`, `GeneratedReadSql`, and `GeneratedWriteSql` so you can
diagnose without re-running.
