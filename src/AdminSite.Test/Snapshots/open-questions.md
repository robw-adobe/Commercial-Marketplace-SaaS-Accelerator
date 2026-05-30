# Open Questions — admin-subscription-fetch-resilience

_Captured during Task 4.6 (code-inspection smoke-check pass).  
These items need operator/team decisions before the service is promoted to production._

---

## Smoke-Check Findings (Tasks 4.3 – 4.5)

### Task 4.3 — AdminSite startup and Subscriptions page rendering

**Verified by code inspection** (`src/AdminSite/Views/Home/Subscriptions.cshtml`):

1. **Model type** — The view's `@model` directive is
   `Marketplace.SaaS.Accelerator.Services.Models.PaginatedSubscriptionViewModel`.
   ✅ Requirement 2.3, 3.4 satisfied.

2. **Empty-state panel** — When `Model.IsEmpty` is `true` the view renders a
   `<div class="cm-panel-default mt40">` block containing:
   - "No subscriptions from your customers yet!" heading
   - If `Model.BackgroundSyncEnabled`: "The background sync runs every N seconds;
     you can also click **Fetch All Subscriptions** to trigger it now."
   - If background sync is disabled: a simple prompt to click the fetch button.
   - A primary "Fetch All Subscriptions" CTA button.
   ✅ Requirement 2.7 / Property 5 satisfied.

3. **Pagination controls** — Inside `@if (Model.TotalCount > Model.PageSize)` the
   view renders Previous / "Page X of Y (Z total)" / Next controls with
   `?pageIndex=&pageSize=` query-string navigation.  Controls are disabled
   (`.disabled` CSS class) at the first and last page boundaries.
   ✅ Requirement 2.3 / Property 3 satisfied.

**Expected behaviour against an empty database:**

- `SubscriptionsRepository.GetPaged(1, 100)` returns `Items=[]`, `TotalCount=0`.
- `HomeController.Subscriptions()` populates `PaginatedSubscriptionViewModel` with
  `IsEmpty=true`, `BackgroundSyncEnabled=true`, `BackgroundSyncIntervalSeconds=300`.
- The rendered page shows the enriched empty-state panel (no table, no pagination
  controls) and the "Fetch All Subscriptions" button.
- `SubscriptionLazyLoaderHostedService` starts in the background and emits a
  `background_sync_started` log entry at startup; the first tick fires after ~300 s.

---

### Task 4.4 — Hosted service tick and parity with manual fetch

**Verified by code inspection:**

1. **Registration in `Startup.cs`** — Lines 195–201 of
   `src/AdminSite/Startup.cs` read the `MarketplaceResilience` section at
   startup and conditionally call
   `services.AddHostedService<SubscriptionLazyLoaderHostedService>()` when
   `BackgroundSyncEnabled` is `true` (the default).
   ✅ Requirement 2.5 satisfied.

2. **Structured log payload per tick** — `SubscriptionLazyLoaderHostedService`
   emits the following JSON log lines:

   | Event name                       | Fields                                                              |
   |----------------------------------|---------------------------------------------------------------------|
   | `background_sync_started`        | `event`, `intervalSeconds`                                         |
   | `background_sync_tick_completed` | `event`, `startUtc`, `durationMs`, `total`, `succeeded`, `failed`  |
   | `background_sync_tick_error`     | `event`, `errorMessage`, `exceptionType`                           |
   | `background_sync_stopped`        | `event`                                                             |

   ✅ Requirement 2.8 / Property 1 (structured logs) satisfied.

3. **Same `SubscriptionFetchPipeline` as the manual fetch** —
   `SubscriptionLazyLoaderHostedService.ExecuteAsync` resolves
   `SubscriptionFetchPipeline` from a child DI scope on every tick and calls
   `pipeline.ExecuteAsync(systemUserId, stoppingToken)`.
   `HomeController.FetchAllSubscriptions` also resolves the same scoped
   `SubscriptionFetchPipeline` and calls `pipeline.ExecuteAsync(currentUserId, ...)`.
   Both paths go through the identical service implementation, including the
   Polly resilience decorator on `IFulfillmentApiService`.
   ✅ Requirements 3.1–3.3 / idempotence (Property 4) satisfied.

**Expected log output on the first tick:**
```json
{"event":"background_sync_started","intervalSeconds":300}
{"event":"background_sync_tick_completed","startUtc":"…","durationMs":…,"total":0,"succeeded":0,"failed":0}
```

---

### Task 4.5 — Stubbed circuit-breaker scenario

**Test coverage in `MarketplaceResiliencePolicyTests`:**

The following test methods in
`src/AdminSite.Test/Resilience/MarketplaceResiliencePolicyTests.cs` cover the
circuit-breaker end-to-end scenario with a stubbed delegate (no live server):

| Test method | What it verifies |
|---|---|
| `CircuitBreaker_OpenAfterThresholdFailures_FailsFast` | Circuit opens after `ConsecutiveFailureThreshold` sustained 5xx failures; subsequent calls throw `BrokenCircuitException` without invoking the delegate (fail-fast during cooldown); `circuit_opened` log entry emitted. |
| `CircuitBreaker_ClosedAfterSuccessfulProbe_AllowsSubsequentCalls` | After `manualControl.CloseAsync()` (simulating cooldown elapsed), a successful probe closes the circuit and normal calls proceed; `circuit_closed` log entry emitted. |
| `CircuitBreaker_HalfOpenLogEntry_IsEmitted` | Half-open / closed log entries are produced when the circuit transitions. |
| `StructuredLog_CircuitOpenedEntry_ContainsRequiredFields` | The `circuit_opened` JSON entry has all required fields (`event`, `delayMs` > 0). |

All four tests use `FakeTimeProvider` (fires timers instantly) so there is no
real-time sleep; tests complete in milliseconds.

✅ Requirements 2.1, 2.6, 2.8 / Property 1 (circuit breaker) satisfied.

---

## Open Questions — Resolved

All items below were reviewed and resolved. Decisions are recorded here for audit purposes and have been reflected in `design.md` (Configuration Decisions section), `appsettings.json`, and the implementation.

### OQ-1 — `BackgroundSyncIntervalSeconds` default (300 s / 5 minutes)

**Decision: Accepted as reasonable default.**
300 s balances API quota consumption against data freshness. May need to be tuned downward for high-volume publishers (e.g. Adobe). Operators can override via `appsettings.json` without redeploying code. Setting `BackgroundSyncEnabled: false` disables the service entirely.

---

### OQ-2 — `MaxConcurrentPlanFetches` default

**Decision: Raised to 20.**
Adobe is a large-volume publisher. The Marketplace API quota is 400 req/min per tenant; 20 concurrent plan fetches stays well within that budget while cutting fetch wall-clock time by ~4× versus the original default of 5. Updated in both `MarketplaceResilienceOptions.cs` and `appsettings.json`.

---

### OQ-3 — Synthetic system user (`system@saas-accelerator.local`)

**Decision: Accepted as-is.**
`system@saas-accelerator.local` is acceptable as a well-known system identity for background-sync audit-log rows. Email domain is not made configurable.

---

### OQ-4 — `PageSize` default and user-adjustable pagination

**Decision: Default kept at 100; per-page selector added to the view.**
The `Subscriptions` page now renders a rows-per-page selector (25 / 50 / 100 / 200) alongside the pagination controls so operators can expand or contract what they see without a config change. The selector aligns with the fetch page size so the UI and the background sync are in sync by default.

---

### OQ-5 — `DatabaseQueryTimeoutSeconds` EF Core wiring

**Decision: Wired to `sqlOptions.CommandTimeout()` in `Startup.cs`.**
`resilienceOpts.DatabaseQueryTimeoutSeconds` is now passed to `sqlOptions.CommandTimeout()` inside the `AddDbContext` call. The `resilienceOpts` object is read from config once at startup, before `AddDbContext`, and reused for both the timeout and the hosted-service registration. No per-repository changes required. This is the standard, single-place approach.

---

### OQ-6 — `ConsecutiveFailureThreshold` vs Polly 8 sampling window

**Decision: No change for now; document the trade-off.**
Polly 8 uses a minimum-throughput sampling window rather than a strict consecutive counter. The current mapping (`MinimumThroughput = ConsecutiveFailureThreshold`) is correct for high-traffic deployments. For low-traffic environments where requests are infrequent, the window may not fill quickly enough. If that proves problematic in production, the mitigation is to reduce `SamplingDuration` (currently `CooldownSeconds * 2`) or switch to Polly's `ConsecutiveCircuitBreakerStrategy`. This will be monitored post-deployment.

---

_Last updated: post-implementation review — all items resolved._
