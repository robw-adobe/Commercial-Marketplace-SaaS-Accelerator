# Implementation Plan

## Overview

This task list follows the exploratory bugfix workflow defined for the `admin-subscription-fetch-resilience` spec:

1. Tasks 1 and 2 are standalone tests written BEFORE any production code change. Task 1 must FAIL on the unfixed code (confirming the bug). Task 2 must PASS on the unfixed code (capturing baseline behavior to preserve).
2. Task 3 contains the implementation work, broken into incrementally-buildable sub-tasks, followed by sub-tasks that re-run the SAME tests written in tasks 1 and 2.
3. Task 4 is the final checkpoint — full test suite + manual smoke checks.

Specification references throughout this file point back to `bugfix.md` (Requirements 2.x, 3.x) and `design.md` (Properties 1–5, the `isBugCondition` and `expectedBehavior` pseudocode, and the Preservation Requirements section).

## Task Dependency Graph

```json
{
  "waves": [
    {
      "wave": 1,
      "description": "Stand up the test project so every subsequent test sub-task has a place to live.",
      "tasks": ["1.1"]
    },
    {
      "wave": 2,
      "description": "Build the test doubles in parallel: fake Marketplace SaaS client and stub SaasKitContext with generators.",
      "tasks": ["1.2", "1.3"]
    },
    {
      "wave": 3,
      "description": "Author the bug-condition property test that drives the doubles from waves 1–2.",
      "tasks": ["1.4"]
    },
    {
      "wave": 4,
      "description": "Run the bug-condition property on UNFIXED code and document counterexamples (must FAIL).",
      "tasks": ["1.5"]
    },
    {
      "wave": 5,
      "description": "Observation step on UNFIXED code — capture preservation snapshots in parallel.",
      "tasks": ["2.1", "2.2", "2.3"]
    },
    {
      "wave": 6,
      "description": "Implement preservation property tests in parallel; each verifies on UNFIXED code (PBT-2.1..2.4).",
      "tasks": ["2.4", "2.5", "2.6", "2.7"]
    },
    {
      "wave": 7,
      "description": "Commit golden snapshots and confirm all four PBT files run green on unfixed code.",
      "tasks": ["2.8"]
    },
    {
      "wave": 8,
      "description": "Foundational config + package — unblocks every implementation sub-task.",
      "tasks": ["3.1"]
    },
    {
      "wave": 9,
      "description": "Independent building blocks: resilience policy and paginated repo (decorator depends on policy).",
      "tasks": ["3.2", "3.4"]
    },
    {
      "wave": 10,
      "description": "Decorator wraps the policy from 3.2.",
      "tasks": ["3.3"]
    },
    {
      "wave": 11,
      "description": "Pipeline depends on the policy-wrapped IFulfillmentApiService; Subscriptions UI depends on the paginated repo.",
      "tasks": ["3.5", "3.7"]
    },
    {
      "wave": 12,
      "description": "Controller refactor and hosted service both depend on the pipeline.",
      "tasks": ["3.6", "3.8"]
    },
    {
      "wave": 13,
      "description": "Re-run the SAME tests from tasks 1 and 2. Task 1 must now PASS; task 2 must STILL PASS.",
      "tasks": ["3.9", "3.10"]
    },
    {
      "wave": 14,
      "description": "Build the solution before running the test suite.",
      "tasks": ["4.1"]
    },
    {
      "wave": 15,
      "description": "Run the full test suite once the build is clean.",
      "tasks": ["4.2"]
    },
    {
      "wave": 16,
      "description": "Final manual smoke checks and operator-facing follow-ups can run in parallel.",
      "tasks": ["4.3", "4.4", "4.5", "4.6"]
    }
  ]
}
```

## Tasks

- [x] 1. Write bug condition exploration test
  - **Property 1: Bug Condition** - Resilient Fetch Pipeline
  - **CRITICAL**: This test MUST FAIL on unfixed code — failure confirms the bug exists.
  - **DO NOT attempt to fix the test or the code when it fails.**
  - **NOTE**: This test encodes the expected behavior — it will validate the fix when it passes after implementation.
  - **GOAL**: Surface concrete counterexamples that demonstrate each branch of `isBugCondition` (transient API failure, large fan-out / sync-over-async saturation, unbounded EF query timeout, sustained API failure / missing circuit breaker, partial-failure non-isolation, missing empty-state guidance).
  - **Scoped PBT Approach**: Because the unfixed code is deterministic, scope the property-based generator to the concrete failing shapes from `design.md` "Examples" — e.g. `transientFailureCount ∈ {1,2}`, `subscriptionCount ∈ {200, 500}`, `consecutiveApiFailures = 6`, single-subscription failure index ∈ generated subscription set. This keeps the property reproducible while still exercising the input domain.

  - [x] 1.1 Add the AdminSite.Test test project
    - Add a new test project `src/AdminSite.Test/AdminSite.Test.csproj` (or extend `src/Services.Test`) with package references `xunit`, `xunit.runner.visualstudio`, `Moq`, `FsCheck.Xunit` (or `CsCheck`), `Microsoft.EntityFrameworkCore.InMemory`.
    - Add project references to `src/Services` and `src/AdminSite` so the test project can drive the controller actions and the future `SubscriptionFetchPipeline`.
    - Wire the new project into `src/SaaSAccelerator.sln` so `dotnet test` discovers it.
    - _Requirements: 2.1, 2.2, 2.4, 2.6, 2.7, 2.8_

  - [x] 1.2 Build the fake IMarketplaceSaaSClient (Moq)
    - Create a Moq-based fake `IMarketplaceSaaSClient` that lets each test inject the failure shapes from `design.md` "Examples": throw `RequestFailedException(429)` once, throw `RequestFailedException(500)` for one specific `subscriptionId`, throw `RequestFailedException(503)` indefinitely, delay N ms per call.
    - Expose helpers/builders to compose those failure modes per test case so each branch of `isBugCondition` can be exercised independently.
    - _Requirements: 2.1, 2.2, 2.6, 2.8_

  - [x] 1.3 Build the stub SaasKitContext over EF InMemory with generators
    - Build a stub `SaasKitContext` over `Microsoft.EntityFrameworkCore.InMemory` and seed varying subscription counts (50, 200, 50_000) using FsCheck/CsCheck generators.
    - Provide generators for subscription corpora, plan distributions, and status mixes so they can be reused by the preservation tests in task 2.
    - _Requirements: 2.2, 2.4, 2.7_

  - [x] 1.4 Author FetchPipeline_OnBugConditionInputs_SatisfiesProperty1
    - Write a single property test method `FetchPipeline_OnBugConditionInputs_SatisfiesProperty1` that, for any generated input where `isBugCondition(input)` evaluates true:
      - Drives `HomeController.FetchAllSubscriptions` (or the `SubscriptionFetchPipeline` once it exists — until then, drive the controller action directly) and the `Subscriptions()` action through the doubles from tasks 1.2 and 1.3.
      - Asserts the conjunction from `design.md` Property 1: no 5xx response, retries observed when `apiExperiencesTransientFailure`, partial progress preserved when one subscription fails, peak concurrent plan-fetch calls is bounded, circuit-breaker activates when `consecutiveApiFailures > 5`, and structured log entries are emitted for every retry / break / per-subscription failure.
      - Also asserts `design.md` Property 5: when seeded subscription count is zero, the rendered `Subscriptions` view contains the new empty-state guidance copy.
    - _Requirements: 2.1, 2.2, 2.4, 2.6, 2.7, 2.8_

  - [x] 1.5 Run on UNFIXED code and document counterexamples
    - Run the property test on UNFIXED code.
    - **EXPECTED OUTCOME**: Test FAILS (this is correct — it proves the bug exists).
    - Document the counterexamples in the test output / a comment block in the test class. Examples to confirm:
      - "single transient 429 produces `BadRequest` with zero retries logged"
      - "200 subscriptions × 200ms simulated SDK delay → wall-clock > 30s due to sequential `.GetAwaiter().GetResult()`"
      - "EF query against 50_000 rows with `CommandTimeout=500ms` throws `SqlException: timeout`"
      - "single per-subscription 500 unwinds the entire loop; zero rows persisted"
      - "sustained 503 → attempts == subscriptionCount (no fail-fast)"
      - "empty database → generic empty table only, no guidance text"
    - Mark task complete when the test is written, executed, and the failure(s) are documented.
    - _Requirements: 2.1, 2.2, 2.4, 2.6, 2.7, 2.8_

- [x] 2. Write preservation property tests (BEFORE implementing fix)
  - **Property 2: Preservation** - Non-Buggy Fetch and Page Behavior
  - **IMPORTANT**: Follow observation-first methodology — first run the UNFIXED code on non-bug-condition inputs and record the actual outputs, then encode those outputs as property assertions.
  - Property-based testing is required here because the input space (subscription corpora of varying size, plan distributions, status mixes, page sizes) is too large for hand-written cases.
  - **EXPECTED OUTCOME**: Tests PASS on unfixed code (this confirms baseline behavior to preserve).

  - [x] 2.1 Observation: capture happy-path 50-subscription snapshot on UNFIXED code
    - Run the UNFIXED code with 50 subscriptions where every Marketplace API call succeeds first try.
    - Record the final state of `Subscriptions`, `Plans`, `Offers`, `Users`, and `SubscriptionAuditLogs` tables into a golden snapshot under `src/AdminSite.Test/Snapshots/`.
    - This snapshot becomes the baseline for PBT-2.1 in task 2.4.
    - _Requirements: 3.1, 3.2, 3.3_

  - [x] 2.2 Observation: capture status/plan/quantity-change audit-log snapshot on UNFIXED code
    - Seed five subscriptions where one's status, plan, or quantity changes between two consecutive fetches and run the UNFIXED code through both fetches.
    - Record the resulting audit-log rows into a golden snapshot under `src/AdminSite.Test/Snapshots/`.
    - This snapshot becomes the baseline for the audit-log assertions in PBT-2.1 / PBT-2.3.
    - _Requirements: 3.2_

  - [x] 2.3 Observation: capture per-operation snapshots on UNFIXED code
    - For each of `SubscriptionDetails`, `SubscriptionOperation` (Activate / Deactivate), change-plan, change-quantity, and `RecordUsage`, run the UNFIXED code on a successful, non-failing input.
    - Record the controller response and resulting DB state into golden snapshots under `src/AdminSite.Test/Snapshots/`.
    - These snapshots become the baseline for PBT-2.4 in task 2.7.
    - _Requirements: 3.5, 2.5_

  - [x] 2.4 Implement PBT-2.1 (core preservation) and verify on UNFIXED code
    - **PBT-2.1 (Property 2 - core preservation)**: For all inputs where the API succeeds first try, `subscriptionCount ≤ threadPoolSaturationThreshold`, and `dbQueryDuration ≤ dbTimeoutThreshold`, assert structural equality of `Subscriptions`, `Plans`, `Offers`, `Users`, `SubscriptionAuditLogs` tables between `F` (unfixed) and `F'` (fixed).
    - Compare against the snapshots from tasks 2.1 and 2.2 so the assertion can run on UNFIXED code today (with `F == F`).
    - Verify the test PASSES on UNFIXED code.
    - _Requirements: 3.1, 3.2, 3.3, 3.4, 3.5_

  - [x] 2.5 Implement PBT-2.2 (pagination correctness) and verify on UNFIXED code
    - **PBT-2.2 (Property 3 - pagination correctness)**: For random `(pageIndex, pageSize)` and random subscription corpora, assert that `concat(GetPaged(1..N, pageSize)).Items` equals `Get()` ordering, and that `TotalCount` equals the seeded count.
    - Because `GetPaged` does not exist on UNFIXED code, add a temporary `[Skip("Awaiting paginated repo from task 3.4")]` annotation (or equivalent) and document the unfixed-code observation: `Get()` returns the full ordered set today.
    - Verify the unskipped portions PASS on UNFIXED code.
    - _Requirements: 2.3, 3.4_

  - [x] 2.6 Implement PBT-2.3 (idempotent fetch) and verify on UNFIXED code
    - **PBT-2.3 (Property 4 - idempotent fetch)**: For random sequences of fetch ticks against the same Marketplace payload, assert final DB state is identical to running a single fetch (audit logs are written only on detected change, not on every run).
    - Because the hosted background service does not exist on UNFIXED code, drive consecutive manual `FetchAllSubscriptions` invocations and add a temporary `[Skip("Awaiting SubscriptionLazyLoaderHostedService from task 3.8")]` annotation on the hosted-service-driven assertion.
    - Verify the unskipped portions PASS on UNFIXED code.
    - _Requirements: 2.5, 3.1, 3.2, 3.3_

  - [x] 2.7 Implement PBT-2.4 (subscription operations unaffected) and verify on UNFIXED code
    - **PBT-2.4 (Subscription operations unaffected)**: For each per-subscription operation (Activate, Deactivate, change plan, change quantity, RecordUsage), assert identical controller response and DB state between `F` and `F'` for a non-failing input.
    - Compare against the snapshots from task 2.3 so the assertion can run on UNFIXED code today.
    - Verify the test PASSES on UNFIXED code.
    - _Requirements: 3.5_

  - [x] 2.8 Commit golden snapshots and confirm all four PBT files run green on UNFIXED code
    - Commit golden snapshots under `src/AdminSite.Test/Snapshots/` so the unfixed-vs-fixed comparison in 3.10 is fully reproducible.
    - Run PBT-2.1 through PBT-2.4 together on UNFIXED code and confirm all four files report green (modulo the documented temporary skips on PBT-2.2 / PBT-2.3).
    - Mark task complete when tests are written, run, and passing on unfixed code.
    - _Requirements: 3.1, 3.2, 3.3, 3.4, 3.5, 2.3, 2.5_

- [x] 3. Fix for admin subscription fetch resilience

  - [x] 3.1 Add Polly package and resilience configuration POCO
    - Add `Polly` (>= 8.x) `<PackageReference>` to `src/Services/Services.csproj`.
    - Create `src/Services/Configurations/MarketplaceResilienceOptions.cs` as a POCO with: `MaxRetries` (default 3), `BaseDelaySeconds` (default 1), `ConsecutiveFailureThreshold` (default 5), `CooldownSeconds` (default 60), `MaxConcurrentPlanFetches` (default 5), `BackgroundSyncIntervalSeconds` (default 300), `BackgroundSyncEnabled` (default true), `PageSize` (default 100), `DatabaseQueryTimeoutSeconds` (default 30).
    - Add a `MarketplaceResilience` section with these defaults to `src/AdminSite/appsettings.json`.
    - Wire `services.Configure<MarketplaceResilienceOptions>(Configuration.GetSection("MarketplaceResilience"))` in `src/AdminSite/Startup.cs` (`ConfigureServices`).
    - Build verification: `dotnet build src/SaaSAccelerator.sln`.
    - _Bug_Condition: isBugCondition(input) — config knobs gate every other branch of the bug condition_
    - _Expected_Behavior: design.md "Fix Implementation" → MarketplaceResilienceOptions block_
    - _Preservation: defaults are chosen so existing happy-path inputs are unaffected_
    - _Requirements: 2.1, 2.2, 2.3, 2.5, 2.6_

  - [x] 3.2 Implement MarketplaceResiliencePolicy
    - Create `src/Services/Services/Resilience/MarketplaceResiliencePolicy.cs` exposing `static IAsyncPolicy Build(MarketplaceResilienceOptions options, ILogger logger)`.
    - Compose `WaitAndRetryAsync(MaxRetries, attempt => TimeSpan.FromSeconds(BaseDelaySeconds * Math.Pow(2, attempt-1)))` (with optional decorrelated jitter) wrapped by `CircuitBreakerAsync(handledEventsAllowedBeforeBreaking: ConsecutiveFailureThreshold, durationOfBreak: TimeSpan.FromSeconds(CooldownSeconds))` via `Policy.WrapAsync`.
    - Predicate: handle `RequestFailedException` whose `Status` is in `{408, 429, 500, 502, 503, 504}`. Do not retry on `400/401/403/404/409` and do not count them toward the breaker.
    - Wire `onRetry`, `onBreak`, `onReset`, `onHalfOpen` callbacks to emit a single structured JSON log line per event including `operation`, `attempt`, `delayMs`, `subscriptionId` (from `Polly.Context`), `outcome`.
    - Add unit tests in the test project covering: retry count = `MaxRetries`, exponential backoff schedule, transient-vs-non-transient classification, closed → open → half-open → closed transitions, structured-log content shape.
    - _Bug_Condition: input.apiExperiencesTransientFailure ∨ input.consecutiveApiFailures > circuitBreakerThreshold_
    - _Expected_Behavior: design.md "Fix Implementation" → MarketplaceResiliencePolicy block, Property 1 conjuncts (no 5xx, retries, circuit breaker, structured logs)_
    - _Preservation: success-on-first-attempt path executes the inner delegate exactly once with no observable side effects_
    - _Requirements: 2.1, 2.6, 2.8_

  - [x] 3.3 Implement FulfillmentApiServiceWithPolicy decorator
    - Create `src/Services/Services/FulfillmentApiServiceWithPolicy.cs` implementing `IFulfillmentApiService` and constructor-injecting an inner `FulfillmentApiService` plus the policy from 3.2.
    - For every method, wrap the inner call in `policy.ExecuteAsync(ctx => innerCall(), new Context { ["operation"] = nameof(Method), ["subscriptionId"] = subscriptionId })`.
    - In `src/AdminSite/Startup.cs`, register the inner `FulfillmentApiService` as a private dependency (`services.AddScoped<FulfillmentApiService>()`) and register `IFulfillmentApiService` to resolve `FulfillmentApiServiceWithPolicy`. Mirror the registration in `src/CustomerSite/Startup.cs` and `src/MeteredTriggerJob` if those projects also bind `IFulfillmentApiService`, so resilience applies uniformly.
    - Verify all existing call sites of `IFulfillmentApiService` still compile and existing unit tests still pass.
    - _Bug_Condition: every call site that satisfies `apiExperiencesTransientFailure`_
    - _Expected_Behavior: design.md "Fix Implementation" → FulfillmentApiServiceWithPolicy block_
    - _Preservation: Section "Preservation Requirements / Scope" — CustomerSite, MeteredScheduler, status handlers must be byte-for-byte identical on success path_
    - _Requirements: 2.1, 2.6, 2.8, 3.5_

  - [x] 3.4 Add paginated repository method
    - Add `GetPaged(int pageIndex, int pageSize)` (or `GetPagedAsync`) to `src/DataAccess/Contracts/ISubscriptionsRepository.cs` returning a new `PagedResult<Subscriptions>` record (`Items`, `TotalCount`, `PageIndex`, `PageSize`) — place the record under `src/DataAccess/Entities/PagedResult.cs`.
    - Implement in `src/DataAccess/Services/SubscriptionsRepository.cs` using `IQueryable` with `Include(s => s.User).OrderByDescending(s => s.CreateDate).Skip((pageIndex-1)*pageSize).Take(pageSize)` plus a separate `Count()` for the total. Clamp `pageIndex >= 1`, `pageSize >= 1`.
    - **Do not change the existing `Get()` method** — its signature and ordering must be preserved (it is consumed by the fetch pipeline and other controllers).
    - Add unit tests asserting: ordering preserved against `Get()`, `concat(GetPaged(1..N)) == Get()`, `TotalCount` correct, `User` navigation eagerly loaded, clamp behavior on `pageIndex=0` and `pageSize=0`.
    - _Bug_Condition: input.dbQueryDuration > dbTimeoutThreshold_
    - _Expected_Behavior: design.md Property 3 — pagination correctness_
    - _Preservation: existing `Get()` callers see no change_
    - _Requirements: 2.3, 3.4_

  - [x] 3.5 Implement SubscriptionFetchPipeline
    - Create `src/Services/Services/SubscriptionFetchPipeline.cs` exposing `Task<FetchResult> ExecuteAsync(int currentUserId, CancellationToken ct)`.
    - Define `FetchResult` (record / class) with `Total`, `Succeeded`, `Failed`, `DurationMs`, and `IReadOnlyList<SubscriptionFailure> Failures` (each carrying `subscriptionId`, `operation`, `errorMessage`).
    - Move the body of the existing `HomeController.FetchAllSubscriptions` into this service. Replace every `.GetAwaiter().GetResult()` with `await`.
    - Wrap the per-subscription block in `try { ... } catch (Exception ex) { logger.LogWarning(ex, structuredLogPayload); failures.Add(...); continue; }` — implements partial-progress preservation (Requirement 2.4).
    - Gate concurrent `GetAllPlansForSubscriptionAsync` calls with `using var semaphore = new SemaphoreSlim(options.MaxConcurrentPlanFetches);` and `Task.WhenAll(subscriptions.Select(async s => { await semaphore.WaitAsync(ct); try { ... } finally { semaphore.Release(); } }))`.
    - Inject `IFulfillmentApiService` (which is now the policy-wrapped decorator from 3.3), `ISubscriptionsRepository`, `IPlansRepository`, `IOffersRepository`, `IUsersRepository`, `ISubscriptionLogRepository`, `IOptions<MarketplaceResilienceOptions>`, `ILogger<SubscriptionFetchPipeline>`.
    - Register the pipeline in `src/AdminSite/Startup.cs` as `services.AddScoped<SubscriptionFetchPipeline>()`.
    - Add unit tests covering: per-subscription error isolation (one failure → others succeed), bounded concurrency (peak in-flight ≤ `MaxConcurrentPlanFetches` measured via instrumented fake), idempotent upsert behavior, audit-log generation on detected status / plan / quantity change.
    - _Bug_Condition: input.subscriptionCount > threadPoolSaturationThreshold ∨ single-subscription failure_
    - _Expected_Behavior: design.md "Fix Implementation" → SubscriptionFetchPipeline block, Property 1 (no thread-pool starvation, partial progress preserved, structured logs)_
    - _Preservation: Requirements 3.1–3.3 — same offers/plans/users/subscriptions/audit-log writes for non-buggy inputs_
    - _Requirements: 2.1, 2.2, 2.4, 2.8, 3.1, 3.2, 3.3_

  - [x] 3.6 Refactor HomeController.FetchAllSubscriptions to fully async
    - Change signature to `public async Task<IActionResult> FetchAllSubscriptions()`.
    - Inject `SubscriptionFetchPipeline` via constructor.
    - Body becomes: `var result = await pipeline.ExecuteAsync(currentUserId, HttpContext.RequestAborted); return result.Failed == result.Total && result.Total > 0 ? BadRequest(result) : Ok(result);`.
    - Remove the duplicated foreach body that has now moved into the pipeline.
    - Verify the AJAX call in `Subscriptions.cshtml` (`fetchAllSubscriptions()`) still POSTs to the same route and handles JSON success / error consistently.
    - _Bug_Condition: full bug condition — controller is the entry point on the user-driven path_
    - _Expected_Behavior: design.md "Fix Implementation" → HomeController.FetchAllSubscriptions block_
    - _Preservation: Requirements 3.1–3.3, 3.5_
    - _Requirements: 2.1, 2.2, 2.4, 2.8, 3.1, 3.2, 3.3_

  - [x] 3.7 Add pagination + empty-state to HomeController.Subscriptions and view
    - Create `src/Services/Models/PaginatedSubscriptionViewModel.cs` extending or wrapping `SubscriptionViewModel` with `int TotalCount`, `int PageIndex`, `int PageSize`, `bool IsEmpty`, `bool BackgroundSyncEnabled`, `int BackgroundSyncIntervalSeconds`.
    - Update `Subscriptions(int pageIndex = 1, int pageSize = 100)` to read query-string params, clamp them against `MarketplaceResilienceOptions.PageSize`, call `subscriptionRepo.GetPaged(pageIndex, pageSize)`, map the slice to `SubscriptionResultExtension` using the existing logic, and populate the new view model. When `TotalCount == 0`, set `IsEmpty = true`.
    - Update `src/AdminSite/Views/Home/Subscriptions.cshtml`:
      - Change `@model` to `Marketplace.SaaS.Accelerator.Services.Models.PaginatedSubscriptionViewModel`.
      - Add pagination controls (Previous / page indicator / Next) that navigate via `?pageIndex=&pageSize=`.
      - Replace the existing "No subscriptions from your customers yet!" panel with a richer empty-state panel that, when `Model.IsEmpty`, shows guidance text referencing the background sync interval ("The background sync runs every N seconds; you can also click Fetch All Subscriptions to trigger it now.") plus the existing call-to-action button.
    - _Bug_Condition: input.dbQueryDuration > dbTimeoutThreshold ∨ empty-database first run_
    - _Expected_Behavior: design.md Property 3 (pagination correctness), Property 5 (empty-state messaging)_
    - _Preservation: Requirement 3.4 — same columns, same ordering, same dropdown actions_
    - _Requirements: 2.3, 2.7, 3.4_

  - [x] 3.8 Implement SubscriptionLazyLoaderHostedService
    - Create `src/Services/Services/Hosted/SubscriptionLazyLoaderHostedService.cs` deriving from `BackgroundService`.
    - In `ExecuteAsync(CancellationToken stoppingToken)`: loop `while (!stoppingToken.IsCancellationRequested)` { `try { using var scope = serviceProvider.CreateScope(); var pipeline = scope.ServiceProvider.GetRequiredService<SubscriptionFetchPipeline>(); var systemUserId = ...; var result = await pipeline.ExecuteAsync(systemUserId, stoppingToken); logger.LogInformation(structuredPayload); } catch (OperationCanceledException) { break; } catch (Exception ex) { logger.LogError(ex, ...); } await Task.Delay(TimeSpan.FromSeconds(options.BackgroundSyncIntervalSeconds), stoppingToken); }`.
    - Resolve `systemUserId` by upserting a synthetic user (e.g. `system@saas-accelerator.local`) on first run so audit-log foreign keys remain valid.
    - Register in `src/AdminSite/Startup.cs` via `if (options.BackgroundSyncEnabled) services.AddHostedService<SubscriptionLazyLoaderHostedService>();`.
    - Add unit tests: tick interval respected, exception inside loop body does not terminate the service, graceful shutdown on `stoppingToken` cancellation, idempotence across consecutive ticks against the same payload.
    - _Bug_Condition: input.consecutiveApiFailures > circuitBreakerThreshold (background pressure on degraded API) ∧ "no incremental sync" root cause_
    - _Expected_Behavior: design.md "Fix Implementation" → SubscriptionLazyLoaderHostedService block, Property 4 (idempotent fetch)_
    - _Preservation: idempotence is the preservation contract — repeated runs do not produce duplicate audit logs or row changes_
    - _Requirements: 2.5, 2.8_

  - [x] 3.9 Verify bug condition exploration test now passes
    - **Property 1: Expected Behavior** - Resilient Fetch Pipeline
    - **IMPORTANT**: Re-run the SAME test from task 1 — do NOT write a new test.
    - The test from task 1 encodes the expected behavior; when it passes, the expected behavior is satisfied for all inputs in `isBugCondition`.
    - Run the exploration property test and any unit tests added in 3.2, 3.4, 3.5, 3.8.
    - **EXPECTED OUTCOME**: Test PASSES (confirms bug is fixed across all branches of `isBugCondition`).
    - If the test still fails, return to the relevant 3.x sub-task and address the failing assertion — do not weaken the assertion.
    - _Requirements: 2.1, 2.2, 2.4, 2.6, 2.7, 2.8 (Property 1 + Property 5)_

  - [x] 3.10 Verify preservation tests still pass
    - **Property 2: Preservation** - Non-Buggy Fetch and Page Behavior
    - **IMPORTANT**: Re-run the SAME tests from task 2 — do NOT write new tests.
    - Run PBT-2.1 through PBT-2.4. The snapshot comparisons must still hold against the fixed pipeline.
    - **EXPECTED OUTCOME**: All four property tests PASS (confirms no regressions for inputs outside `isBugCondition`).
    - Confirm per-subscription operations (Activate, Deactivate, change plan, change quantity, RecordUsage) and the smoke tests for `OffersController`, `PlansController`, `KnownUsersController`, `ApplicationLogController` still pass.
    - _Requirements: 3.1, 3.2, 3.3, 3.4, 3.5_

- [x] 4. Checkpoint - Ensure all tests pass

  - [x] 4.1 Build the full solution
    - Run `dotnet build src/SaaSAccelerator.sln`.
    - Confirm zero errors and zero new warnings versus the baseline before this fix.
    - _Requirements: 2.1, 2.2, 2.3, 2.4, 2.5, 2.6, 2.7, 2.8, 3.1, 3.2, 3.3, 3.4, 3.5_

  - [x] 4.2 Run the full test suite
    - Run `dotnet test src/SaaSAccelerator.sln`.
    - Confirm every test (unit, property-based, integration) passes — including the property tests from tasks 1 and 2 and the unit tests added by 3.2, 3.4, 3.5, 3.8.
    - _Requirements: 2.1, 2.2, 2.3, 2.4, 2.5, 2.6, 2.7, 2.8, 3.1, 3.2, 3.3, 3.4, 3.5_

  - [x] 4.3 Smoke check: AdminSite startup and Subscriptions page rendering
    - Start the AdminSite (`dotnet run --project src/AdminSite`) against an empty database.
    - Confirm `/Home/Subscriptions` renders with pagination controls and the empty-state panel as designed in task 3.7.
    - _Requirements: 2.3, 2.7, 3.4_

  - [x] 4.4 Smoke check: hosted service tick and parity with manual fetch
    - With the AdminSite running, wait for the first tick of `SubscriptionLazyLoaderHostedService`.
    - Confirm a structured log entry is produced on that tick.
    - Confirm DB state matches what a manual `FetchAllSubscriptions` produces against the same Marketplace fixture.
    - _Requirements: 2.5, 2.8, 3.1, 3.2, 3.3_

  - [x] 4.5 Smoke check: stubbed circuit-breaker scenario end-to-end
    - Drive a stubbed circuit-breaker scenario (sustained 5xx) end-to-end through the AdminSite.
    - Confirm calls fail fast during the cooldown window, then succeed when the stub recovers and the breaker transitions back to closed.
    - _Requirements: 2.1, 2.6, 2.8_

  - [x] 4.6 Surface remaining open questions for the user
    - Surface any remaining open questions for the user, particularly defaults for `BackgroundSyncIntervalSeconds`, `MaxConcurrentPlanFetches`, and the location of the synthetic system user used by the hosted service.
    - Capture decisions on these questions in `bugfix.md` / `design.md` if they change behavior.
    - _Requirements: 2.5, 2.8_

## Notes

- **Test framework choice**: The existing `src/Services.Test` project uses MSTest + Moq. For the property-based tests required by tasks 1 and 2, this plan introduces an `xunit + FsCheck.Xunit` project (`src/AdminSite.Test`) so MSTest does not have to be retro-fitted. If the team prefers to stay on MSTest, swap `FsCheck.Xunit` for `FsCheck` plus `MSTest` adapters — the property assertions are framework-agnostic.
- **Snapshot location**: Golden snapshots from task 2 live under `src/AdminSite.Test/Snapshots/` and are committed alongside the tests so the unfixed-vs-fixed comparison in 3.10 is fully reproducible.
- **Decorator vs middleware**: The resilience policy is applied as an `IFulfillmentApiService` decorator (3.3) rather than at the HTTP message-handler level so the same policy applies to `CustomerSite` and `MeteredTriggerJob` once their DI registrations are aligned. Per `design.md`'s Preservation scope, behavior on the success path must be byte-for-byte identical for those consumers.
- **Background user**: `SubscriptionLazyLoaderHostedService` (3.8) needs a stable `currentUserId` for audit-log foreign keys. The plan upserts a synthetic `system@saas-accelerator.local` user on first run; if operators prefer a different convention, surface this as an option on `MarketplaceResilienceOptions` and revisit before merging.
- **Defaults**: Configuration defaults match the values stated in `bugfix.md` Requirements 2.1, 2.2, 2.3, 2.5, 2.6 (3 retries, base 1s exponential, 5 concurrent plan fetches, page size 100, 5-minute sync, 5-failure breaker with 60s cooldown). Operators can override these without redeploying code.
