# Admin Subscription Fetch Resilience Bugfix Design

## Overview

The AdminSite's subscription management currently fails under load due to a combination of sync-over-async patterns, unbounded API and database calls, missing retry logic, and the absence of any incremental sync mechanism. This design introduces a layered resilience strategy: a Polly-based retry/circuit-breaker policy in front of every Marketplace API call, fully asynchronous orchestration of `FetchAllSubscriptions` with bounded concurrency via `SemaphoreSlim`, server-side pagination on the `Subscriptions` page through the `ISubscriptionsRepository`, per-subscription error isolation, and a hosted background sync that keeps local data fresh between manual fetches. All resilience components are driven by configuration so operators can tune them without redeploying code, and every retry, circuit transition, and partial failure is emitted as a structured log entry. The fix preserves the existing data model, audit-log semantics, controller routes, and per-subscription operations so that the only observable change for non-buggy inputs is added log volume.

## Glossary

- **Bug_Condition (C)**: The set of inputs that trigger the defect. Specifically: a Marketplace API request encounters a transient failure, the local subscription corpus is large enough to saturate the thread pool through sync-over-async, the database query for all subscriptions exceeds its timeout, or the Marketplace API has been failing consecutively beyond a configured threshold.
- **Property (P)**: The desired behavior on inputs in C. The fixed system must return without a 5xx, retry transient failures with exponential backoff, preserve partial progress across per-subscription failures, avoid thread-pool starvation, open a circuit breaker after sustained failures, and emit structured logs for each resilience event.
- **Preservation**: For inputs not in C, the fixed code must produce the same observable database state, view model, audit log entries, and HTTP response as the unfixed code. Logging volume may differ.
- **F**: The original (unfixed) `FetchAllSubscriptions` action and `Subscriptions()` action as they exist today in `HomeController`.
- **F'**: The fixed actions plus the new resilience layer, hosted service, and paginated repository methods.
- **Resilience Policy**: A composed Polly `IAsyncPolicy<T>` containing retry-with-jittered-exponential-backoff and circuit breaker, applied to every outbound Marketplace SaaS SDK call.
- **Bounded Concurrency**: The maximum number of in-flight `GetAllPlansForSubscriptionAsync` calls during a single fetch, enforced with `SemaphoreSlim`.
- **Lazy Loader**: The `IHostedService` that periodically calls the same fetch pipeline as the manual button, providing eventual consistency between manual fetches.
- **FetchAllSubscriptions (action)**: The HTTP `POST /Home/FetchAllSubscriptions` controller method that drives a full sync from the Marketplace API to the local database.
- **Subscriptions (action)**: The HTTP `GET /Home/Subscriptions` controller method that renders the subscription list page from the local database.
- **SubscriptionFetchPipeline**: The new service that encapsulates the fetch loop so both the controller action and the hosted service can call it with a single implementation.

## Bug Details

### Bug Condition

The bug manifests whenever a `FetchAllSubscriptions` request or a `Subscriptions` page load runs against (a) a Marketplace API that returns a transient failure, (b) a subscription corpus large enough that the existing `.GetAwaiter().GetResult()` pattern blocks more thread pool threads than are available, (c) a database query for all subscriptions whose duration exceeds the EF Core command timeout, or (d) a Marketplace API in a sustained-failure state where retries only worsen the load. In every one of these cases the current code either returns a 5xx, hangs, or compounds the upstream failure.

**Formal Specification:**
```
FUNCTION isBugCondition(input)
  INPUT: input of type SubscriptionFetchRequest
         WITH FIELDS:
           apiExperiencesTransientFailure: boolean
           subscriptionCount: integer
           dbQueryDuration: duration
           consecutiveApiFailures: integer
  OUTPUT: boolean

  RETURN input.apiExperiencesTransientFailure
      OR input.subscriptionCount > threadPoolSaturationThreshold
      OR input.dbQueryDuration  > dbTimeoutThreshold
      OR input.consecutiveApiFailures > circuitBreakerThreshold
END FUNCTION
```

### Examples

- **Transient 429 during bulk list**: `GetAllSubscriptionAsync()` returns HTTP 429 once. Expected: one or more retries with exponential backoff, then success. Actual: immediate 5xx returned to the browser.
- **Large fan-out**: 500 active subscriptions in the database. Expected: bounded concurrent plan fetches and a successful run within the request timeout. Actual: 500 blocking `.GetAwaiter().GetResult()` calls saturate the thread pool and the request times out.
- **Slow `Subscriptions()` page**: 50,000 rows in `Subscriptions` table with `Include(s => s.User)`. Expected: a single page of 100 rows is rendered. Actual: query exceeds command timeout and the page returns 5xx.
- **Single-subscription plan fetch failure inside the loop**: the Marketplace API returns 500 for one subscription's `GetAllPlansForSubscriptionAsync`. Expected: that subscription is skipped, error is logged, remaining subscriptions process to completion. Actual: the entire bulk operation aborts and partial progress is lost.
- **Sustained outage**: Marketplace API has returned 5xx for the last 6 calls. Expected: circuit opens, subsequent calls fail fast for 60s, then a half-open probe is attempted. Actual: every retry executes in full, adding load to the upstream.
- **Empty database, no manual fetch yet**: A freshly deployed AdminSite has zero subscriptions locally. Expected: page shows guidance prompting the operator to wait for the background sync or trigger a manual fetch. Actual: a generic empty table is shown with no operator guidance and no automatic sync ever runs.

## Expected Behavior

### Preservation Requirements

**Unchanged Behaviors:**
- The "Fetch All Subscriptions" button continues to perform a full sync of offers, plans, users, subscriptions, and audit log entries from the Marketplace API to the local database (Requirement 3.1).
- Existing subscriptions continue to have their status, plan, and quantity updated, with audit log entries created for any status, plan, or quantity change detected during a fetch (Requirement 3.2).
- New subscriptions continue to result in offer, plan, user, and subscription rows being created with the same field-level semantics as today (Requirement 3.3).
- The `Subscriptions` view continues to display purchaser email, marketplace subscription id, name, offer, plan, quantity, and status, with the same column ordering and dropdown actions (Requirement 3.4).
- Per-subscription operations (`SubscriptionDetails`, `SubscriptionOperation` for Activate/Deactivate, change plan, change quantity, `RecordUsage`) continue to function unchanged (Requirement 3.5).

**Scope:**
All inputs that do NOT match the bug condition should be completely unaffected by this fix. This includes:
- Successful Marketplace API responses on the first attempt (no retry executed, no circuit-state transition).
- Subscription corpora small enough that the original sync pattern would have completed within the request timeout.
- Pages 1..N where the database query completes well within EF Core's command timeout.
- All non-fetch controller actions and views in the AdminSite project.
- The `CustomerSite`, `MeteredScheduler`, and any other consumer of `IFulfillmentApiService` outside the AdminSite fetch pipeline. (Resilience is applied via decorator/policy at the HTTP-call boundary, so behavior on success is byte-for-byte identical.)

## Hypothesized Root Cause

Based on the bug description and current code in `HomeController.FetchAllSubscriptions`, `FulfillmentApiService`, and `SubscriptionsRepository`, the most likely contributing causes are:

1. **No retry policy on Marketplace API calls**: `FulfillmentApiService` catches `RequestFailedException` once and immediately routes to `ProcessErrorResponse`, which throws. Transient 429/5xx responses surface as user-visible failures with no retry attempt.

2. **Sync-over-async in a request-thread loop**: `FetchAllSubscriptions` calls `.GetAwaiter().GetResult()` inside a `foreach`, both for the bulk list and for each per-subscription `GetAllPlansForSubscriptionAsync`. This blocks request-pool threads for the duration of every Marketplace round trip, and at scale exhausts the pool.

3. **Unbounded fan-out**: There is no concurrency limit on the per-subscription plan fetches; all are issued sequentially today, but a naive conversion to `Task.WhenAll` would saturate the SDK's connection pool. A bounded concurrency primitive is required.

4. **No partial-failure isolation**: One `RequestFailedException` from a single subscription's plan lookup unwinds the entire `foreach`, losing all in-progress work.

5. **Unbounded EF query for the page**: `SubscriptionsRepository.Get()` returns `IEnumerable<Subscriptions>` over `Include(s => s.User).OrderByDescending(...)` with no `Skip/Take`, and `HomeController.Subscriptions()` calls `.ToList()` materializing every row.

6. **No circuit breaker**: A degraded Marketplace endpoint will receive every retry from every concurrent caller, amplifying load on a service that is already failing.

7. **No incremental sync**: The system relies entirely on the manual button. Between clicks the local view is arbitrarily stale, and the operator has no automatic mechanism to surface drift.

8. **Insufficient observability**: Existing logs are unstructured strings inside `try/catch` blocks; an operator cannot count retries, measure circuit transitions, or attribute errors to specific subscriptions.

## Correctness Properties

Property 1: Bug Condition - Resilient Fetch Pipeline

_For any_ input where the bug condition holds (`isBugCondition` returns true), the fixed `FetchAllSubscriptions` action and the `SubscriptionFetchPipeline` it calls SHALL complete without a 5xx response, SHALL retry each transient Marketplace API failure with exponential backoff up to the configured maximum, SHALL execute every per-subscription plan fetch under the configured concurrency limit using fully asynchronous I/O, SHALL isolate per-subscription failures so that other subscriptions still complete, SHALL open the configured circuit breaker after the threshold of consecutive failures and fail fast for the cooldown duration, and SHALL emit a structured log entry for every retry, circuit-state transition, and per-subscription failure that includes operation name, subscription id (where applicable), attempt count, elapsed duration, and outcome.

**Validates: Requirements 2.1, 2.2, 2.4, 2.6, 2.8**

Property 2: Preservation - Non-Buggy Fetch and Page Behavior

_For any_ input where the bug condition does NOT hold (`isBugCondition` returns false), the fixed code SHALL produce the same database state, audit log entries, and view-model contents as the original code: the same set of offers, plans, users, and subscriptions are created or updated with the same field values; the same `SubscriptionAuditLogs` rows are written for status, plan, and quantity changes; the rendered `Subscriptions` view shows the same rows in the same order; and per-subscription operations (Activate, Deactivate, change plan, change quantity, record usage) behave identically. Additional structured log lines are permitted but no other observable difference is allowed.

**Validates: Requirements 3.1, 3.2, 3.3, 3.4, 3.5**

Property 3: Pagination Correctness

_For any_ valid `(pageIndex, pageSize)` request to the `Subscriptions` page, the fixed action SHALL return a contiguous slice of the same totally-ordered subscription sequence the original code would have returned (`OrderByDescending(s => s.CreateDate)`), SHALL include the same eager-loaded `User` navigation, and SHALL expose total count, page index, and page size to the view so navigation controls render correctly. The concatenation of all pages in order SHALL equal the full unpaginated result.

**Validates: Requirements 2.3**

Property 4: Background Sync Idempotence

_For any_ sequence of background lazy-loader runs interleaved with manual `FetchAllSubscriptions` invocations, the resulting database state SHALL be the same as if only the most recent successful run had executed. That is, the fetch pipeline is idempotent with respect to subscription, plan, offer, and user upserts and to audit-log creation (audit logs are written only on detected change, not on every run).

**Validates: Requirements 2.5**

Property 5: Empty-State Messaging

_For any_ `Subscriptions` page request where the subscriptions table is empty, the fixed view SHALL render guidance text directing the operator to either trigger a manual fetch or wait for the next background sync, in addition to the existing "no subscriptions" panel.

**Validates: Requirements 2.7**

## Fix Implementation

### Changes Required

Assuming the root cause analysis is correct, the fix introduces a small number of new types and refactors three existing ones. The Marketplace API surface, the EF Core entity model, and the controller routes are unchanged.

**File**: `src/Services/Services/Resilience/MarketplaceResiliencePolicy.cs` (new)

**Function**: `MarketplaceResiliencePolicy.Build(MarketplaceResilienceOptions, ILogger)`

**Specific Changes**:
1. **Polly package reference**: Add `Polly` (>= 8.x) to `src/Services/Services.csproj` so the resilience layer is reusable from any caller of `IFulfillmentApiService`.
2. **Composite policy**: Build an `AsyncPolicyWrap<T>` containing `WaitAndRetryAsync` (count = `MaxRetries`, delay = `BaseDelay * 2^(attempt-1)` with optional jitter) and `CircuitBreakerAsync` (`HandledEventsAllowedBeforeBreaking = ConsecutiveFailureThreshold`, `DurationOfBreak = CooldownDuration`).
3. **Predicate**: Treat `RequestFailedException` with status in `{408, 429, 500, 502, 503, 504}` as transient. Non-transient failures (`401`, `403`, `404`, `409`, `400`) are not retried and are not counted toward the breaker.
4. **Structured logging hooks**: `onRetry`, `onBreak`, `onReset`, and `onHalfOpen` callbacks emit a JSON-formatted log entry (`operation`, `attempt`, `delayMs`, `subscriptionId`, `outcome`).

**File**: `src/Services/Services/FulfillmentApiServiceWithPolicy.cs` (new, decorator)

**Function**: All `IFulfillmentApiService` methods.

**Specific Changes**:
1. Implement `IFulfillmentApiService` by delegating to the existing `FulfillmentApiService`, wrapping each call in the composite policy from step 1.
2. Pass per-call `Context` so each retry/break log line carries the operation name and subscription id.
3. Register as the public `IFulfillmentApiService` in `Startup.ConfigureServices`; the underlying `FulfillmentApiService` is registered as a private dependency. This guarantees every Marketplace call - including the existing call sites in `CustomerSite`, status handlers, and the new pipeline - benefits from the policy without touching call sites.

**File**: `src/Services/Configurations/MarketplaceResilienceOptions.cs` (new)

**Specific Changes**:
1. POCO bound from `appsettings.json` section `MarketplaceResilience` with: `MaxRetries` (default 3), `BaseDelaySeconds` (default 1), `ConsecutiveFailureThreshold` (default 5), `CooldownSeconds` (default 60), `MaxConcurrentPlanFetches` (default 5), `BackgroundSyncIntervalSeconds` (default 300), `PageSize` (default 100), `DatabaseQueryTimeoutSeconds` (default 30).
2. Register via `services.Configure<MarketplaceResilienceOptions>(Configuration.GetSection("MarketplaceResilience"))`.

**File**: `src/Services/Services/SubscriptionFetchPipeline.cs` (new)

**Function**: `Task<FetchResult> ExecuteAsync(int currentUserId, CancellationToken ct)`

**Specific Changes**:
1. Encapsulate the body of the existing `HomeController.FetchAllSubscriptions` so the controller and the hosted service share one implementation.
2. Replace `.GetAwaiter().GetResult()` calls with `await`.
3. Wrap the per-subscription block in a `try/catch` that logs `(subscriptionId, exception)` and continues - implementing partial-progress preservation.
4. Use a `SemaphoreSlim(MaxConcurrentPlanFetches)` to gate concurrent `GetAllPlansForSubscriptionAsync` calls; collect with `Task.WhenAll`.
5. Return a `FetchResult` describing counts: `Total`, `Succeeded`, `Failed`, `DurationMs`, plus a list of per-subscription failure summaries.

**File**: `src/AdminSite/Controllers/HomeController.cs`

**Function**: `FetchAllSubscriptions`, `Subscriptions`

**Specific Changes**:
1. Convert `FetchAllSubscriptions` to `public async Task<IActionResult> FetchAllSubscriptions()`. Inject `SubscriptionFetchPipeline` via constructor, `await pipeline.ExecuteAsync(currentUserId, HttpContext.RequestAborted)`, and return `Ok(result)` or `BadRequest(result)` based on whether all subscriptions failed.
2. Update `Subscriptions(int pageIndex = 1, int pageSize = 100)` to read `pageIndex` and `pageSize` from query string (clamped against `MarketplaceResilienceOptions.PageSize`), call the new paginated repository method, and populate a `PaginatedSubscriptionViewModel` with `TotalCount`, `PageIndex`, `PageSize`, and the page's items.
3. When `TotalCount == 0`, set a flag on the view model so the view renders the new empty-state guidance.

**File**: `src/DataAccess/Contracts/ISubscriptionsRepository.cs` and `src/DataAccess/Services/SubscriptionsRepository.cs`

**Function**: New `GetPaged(int pageIndex, int pageSize, out int totalCount)` (or async equivalent `GetPagedAsync`).

**Specific Changes**:
1. Add `GetPaged(int pageIndex, int pageSize)` returning `PagedResult<Subscriptions>` (a small record with `Items`, `TotalCount`, `PageIndex`, `PageSize`). Implementation uses `IQueryable` with `Include(s => s.User).OrderByDescending(s => s.CreateDate).Skip((pageIndex-1)*pageSize).Take(pageSize)`, plus a separate `CountAsync()` for the total.
2. Keep the existing `Get()` method intact - it is used by the fetch pipeline (which iterates the full set) and by other controllers; preservation requires its signature and ordering to be unchanged.

**File**: `src/Services/Services/Hosted/SubscriptionLazyLoaderHostedService.cs` (new)

**Specific Changes**:
1. Implement `BackgroundService`. In `ExecuteAsync`, loop with `Task.Delay(BackgroundSyncIntervalSeconds, stoppingToken)` and call `IServiceProvider.CreateScope()` -> resolve `SubscriptionFetchPipeline` -> `ExecuteAsync(systemUserId, stoppingToken)`.
2. Catch and log all exceptions inside the loop body so a transient failure does not terminate the background service.
3. Emit a structured log entry on each tick recording start, end, duration, and `FetchResult` summary.
4. Register via `services.AddHostedService<SubscriptionLazyLoaderHostedService>()` in `Startup.ConfigureServices`. The service is gated behind a `MarketplaceResilienceOptions.BackgroundSyncEnabled` flag (default `true`) so it can be disabled in environments that do not need it.

**File**: `src/AdminSite/Views/Home/Subscriptions.cshtml`

**Specific Changes**:
1. Update `@model` to the new `PaginatedSubscriptionViewModel`.
2. Add pagination controls (Previous, Next, page indicator) that POST/GET with `?pageIndex=&pageSize=`.
3. Replace the existing `else` branch ("No subscriptions from your customers yet!") with a richer empty-state panel that conditionally shows guidance about the background sync interval and a primary call-to-action to trigger a manual fetch.

## Testing Strategy

### Validation Approach

Testing follows the workflow's two-phase pattern. First, before applying the fix, write tests that simulate each scenario in the bug condition and observe the failures - this confirms (or refutes) the root-cause hypotheses. Then, after applying the fix, the same tests must pass and additional preservation tests must continue to pass for inputs outside the bug condition. Property-based tests are used wherever the input domain is large enough that hand-written cases would miss edge behavior, especially for pagination correctness and idempotent upserts.

### Exploratory Bug Condition Checking

**Goal**: Surface concrete counterexamples that demonstrate each defect on the unfixed code, confirming the root-cause hypotheses before any code change.

**Test Plan**: Build a fake `IMarketplaceSaaSClient` (xUnit + Moq) that lets each test inject specific failure shapes (transient 429, sustained 5xx, slow response, exception on a single subscription id) and a stub `SaasKitContext` over an in-memory provider. Drive `FetchAllSubscriptions` and `Subscriptions` through these doubles and assert the documented incorrect behavior is observed on `F`.

**Test Cases**:
1. **Transient 429 on bulk list**: Configure `ListSubscriptionsAsync` to throw `RequestFailedException(429)` once. On `F`, expect a `BadRequest` response and zero retries logged (will fail on unfixed code: it returns 5xx without retrying).
2. **Sync-over-async fan-out**: Seed 200 subscriptions, configure `ListAvailablePlansAsync` to delay 200 ms each. On `F`, expect total wall-clock time of `~200 * 200ms = 40s` because of the sequential blocking pattern (will exceed default 30s test timeout on unfixed code).
3. **Unbounded EF query**: Seed 50,000 rows with a forced 500ms `CommandTimeout`. On `F`, expect `Subscriptions()` to throw `SqlException: timeout` (will fail on unfixed code).
4. **Single-subscription failure**: Configure `ListAvailablePlansAsync(subId=42)` to throw `RequestFailedException(500)` while every other subscription succeeds. On `F`, expect zero subscriptions persisted (will fail on unfixed code: the whole loop aborts).
5. **Sustained outage**: Configure all calls to return `RequestFailedException(503)`. On `F`, expect every call to execute fully (no fail-fast). Count attempts and assert `attempts == subscriptionCount` (will fail on unfixed code).
6. **Empty database**: With zero subscriptions, hit `GET /Home/Subscriptions`. On `F`, expect generic empty table only (will fail to satisfy Property 5 on unfixed code).

**Expected Counterexamples**:
- A 5xx response observed for a single transient 429.
- Wall-clock fetch time scaling linearly with `subscriptionCount`.
- A SQL timeout exception on the page load.
- Database state with zero new rows after a partial failure.
- Possible causes (any of which would be falsified by passing tests after the fix): missing retry policy, sync-over-async pattern, unbounded EF query, exception-unwound loop, missing circuit breaker.

### Fix Checking

**Goal**: Verify that for all inputs in the bug condition, the fixed pipeline produces the expected behavior (Property 1, 3, 4, 5).

**Pseudocode:**
```
FOR ALL input WHERE isBugCondition(input) DO
  result := SubscriptionFetchPipeline_fixed.ExecuteAsync(input)
  ASSERT no_5xx(result)
  ASSERT (input.apiExperiencesTransientFailure
          IMPLIES retries_attempted(result, maxRetries=3, backoff=exponential))
  ASSERT partial_progress_preserved(result)
  ASSERT no_thread_pool_starvation(result)
  ASSERT (input.consecutiveApiFailures > 5
          IMPLIES circuit_breaker_activated(result))
  ASSERT structured_logs_emitted(result)
END FOR
```

### Preservation Checking

**Goal**: Verify that for all inputs outside the bug condition, the fixed code produces the same observable result as the original (Property 2).

**Pseudocode:**
```
FOR ALL input WHERE NOT isBugCondition(input) DO
  ASSERT db_state(F(input))             = db_state(F'(input))
  ASSERT audit_logs(F(input))           = audit_logs(F'(input))
  ASSERT view_model(F(input))           = view_model(F'(input))
  ASSERT controller_response(F(input))  = controller_response(F'(input))
END FOR
```

**Testing Approach**: Property-based testing (FsCheck or CsCheck on .NET) is the right tool for preservation because the input domain - subscription corpora of varying size, plan distributions, user counts, status mixes - is far larger than hand-written cases can cover. Generate randomized but constrained inputs, run the unfixed and fixed pipelines against parallel in-memory databases, and assert structural equality on the resulting state.

**Test Plan**:
1. Capture the unfixed behavior on a curated set of "happy path" inputs into golden snapshots.
2. Re-run the same inputs through the fixed pipeline, asserting equality against the snapshots.
3. Generate randomized inputs with FsCheck and assert mutual equality on the live database state of `F` versus `F'` for every generated input where `isBugCondition` is false.

**Test Cases**:
1. **Happy path bulk fetch**: 50 subscriptions, all API calls succeed. Observe row-by-row equality of the `Subscriptions`, `Plans`, `Offers`, `Users`, `SubscriptionAuditLogs` tables between `F` and `F'`.
2. **Status-change audit logs**: Seed five subscriptions with one whose status changes between calls. Verify exactly one audit log row is added by `F'`, identical in shape to what `F` produces.
3. **Pagination round-trip**: For random `(pageIndex, pageSize)` and random subscription corpora, assert `F(all)` equals `concat(F'(page=1) ... F'(page=N))`.
4. **Subscription operations unaffected**: For each of Activate, Deactivate, change plan, change quantity, record usage, run end-to-end against `F` and `F'` and assert identical responses and DB state.
5. **Other controllers unchanged**: Smoke-test `OffersController`, `PlansController`, `KnownUsersController`, `ApplicationLogController` to confirm no regression from the policy decorator on `IFulfillmentApiService`.

### Unit Tests

- `MarketplaceResiliencePolicy`: retry count, backoff schedule, transient-vs-non-transient classification, circuit transitions (closed -> open -> half-open -> closed), structured-log content.
- `SubscriptionFetchPipeline`: per-subscription error isolation, semaphore-bounded concurrency (assert no more than `MaxConcurrentPlanFetches` in flight at any moment), idempotent upsert behavior, audit-log generation.
- `SubscriptionsRepository.GetPaged`: ordering preserved, slice equals expected window, total count correct, eager-load semantics for `User` retained.
- `SubscriptionLazyLoaderHostedService`: tick interval, exception swallowing, graceful shutdown on `stoppingToken` cancellation.
- `HomeController.FetchAllSubscriptions`: status-code mapping for `FetchResult` outcomes (success / partial / total failure).
- `HomeController.Subscriptions`: clamping of `pageIndex`/`pageSize`, empty-state flag toggling.

### Property-Based Tests

- **PBT-1 Pagination correctness (Property 3)**: Generator produces a list of subscription rows of arbitrary size and a random `pageSize`. For all valid `pageIndex` values, assert that `concat(GetPaged(i, pageSize))` equals the unpaginated `Get()` ordering, and that `TotalCount` equals the seeded count.
- **PBT-2 Idempotent fetch (Property 4)**: Generator produces a sequence of fetch ticks where each tick may or may not see new/updated subscriptions. Run `n` ticks and assert that the final database state is determined only by the most recent tick's payload (modulo audit logs, which monotonically grow).
- **PBT-3 Preservation under non-buggy inputs (Property 2)**: Generator produces input shapes where `isBugCondition` is false. Run `F` and `F'` against parallel in-memory databases and assert equality of `Subscriptions`, `Plans`, `Offers`, `Users`, and `SubscriptionAuditLogs`.
- **PBT-4 Concurrency bound (Property 1)**: Generator produces subscription corpora and a random `MaxConcurrentPlanFetches` between 1 and 20. Instrument the fake API to record peak in-flight count and assert `peak <= MaxConcurrentPlanFetches`.
- **PBT-5 Retry budget (Property 1)**: Generator produces a transient-failure pattern (a finite list of transient failures followed by a success). Assert that the pipeline succeeds iff the failure-prefix length is `<= MaxRetries` and that the number of attempts equals `min(failurePrefix+1, MaxRetries+1)`.

### Integration Tests

- End-to-end fetch with a stubbed marketplace HTTP server returning real Marketplace SaaS SDK shapes; verify both manual `FetchAllSubscriptions` and the hosted service produce identical end states.
- Page navigation through the new `Subscriptions` view: load page 1, page 2, last page, page 0 (clamped), page beyond last (empty); assert that the rendered HTML contains expected rows and the pagination controls have correct hrefs.
- Empty-state path: bring up the AdminSite against an empty database; assert the new guidance text is rendered and the manual fetch button still works.
- Background sync visibility: start the host, wait for one tick, assert that database state matches what a manual fetch would produce, and that the structured log contains an entry per tick.
- Circuit-breaker transition: drive sustained 5xx responses through the stubbed server, assert calls fail fast during the cooldown, then succeed after the cooldown when the stub returns 200.
