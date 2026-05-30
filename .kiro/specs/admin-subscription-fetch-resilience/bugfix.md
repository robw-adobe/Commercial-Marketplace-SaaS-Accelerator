# Bugfix Requirements Document

## Introduction

The AdminSite's subscription management is causing 5xx errors under load due to multiple resilience failures in the data fetching pipeline. The `FetchAllSubscriptions` action uses sync-over-async patterns (`.GetAwaiter().GetResult()`) causing thread pool starvation, makes unbounded API calls with no retry or timeout handling, and issues N+1 API calls for plan details per subscription. Additionally, the `Subscriptions()` action loads all subscriptions from the database in a single unbounded query with eager-loaded navigation properties, which can timeout on large datasets. The system lacks any incremental sync mechanism, forcing administrators to rely on the expensive "Fetch All" bulk operation to keep subscription data current.

## Bug Analysis

### Current Behavior (Defect)

1.1 WHEN the Marketplace API is temporarily unavailable or rate-limited during FetchAllSubscriptions THEN the system returns a 5xx error with no retry attempt and the entire operation fails

1.2 WHEN FetchAllSubscriptions is called with a large number of subscriptions THEN the system makes N+1 synchronous API calls to `GetAllPlansForSubscriptionAsync` per subscription using `.GetAwaiter().GetResult()`, causing thread pool starvation and request timeouts

1.3 WHEN the `Subscriptions()` page is loaded and the database contains a large number of subscriptions THEN the system loads all records with `Include(s => s.User)` in a single unbounded query, risking database timeouts and 5xx errors

1.4 WHEN a transient network error occurs during any individual Marketplace API call within FetchAllSubscriptions THEN the entire bulk operation fails and no partial progress is preserved

1.5 WHEN subscription data changes in the Marketplace (new subscriptions, plan changes, cancellations) THEN the AdminSite has no mechanism to detect or sync these changes until an administrator manually clicks "Fetch All Subscriptions"

1.6 WHEN the Marketplace API is consistently failing (not just transient errors) THEN the system continues to retry indefinitely, adding load to an already degraded service

1.7 WHEN retry attempts, sync operations, or partial failures occur THEN the system does not emit structured logs or metrics that operators can use to monitor system health

### Expected Behavior (Correct)

2.1 WHEN the Marketplace API is temporarily unavailable or rate-limited during FetchAllSubscriptions THEN the system SHALL retry the failed request with exponential backoff (default: 3 retries starting at 1 second, doubling each retry) before reporting failure; retries SHALL apply to both the bulk `GetAllSubscriptionAsync` call and individual per-subscription calls like `GetAllPlansForSubscriptionAsync`

2.2 WHEN FetchAllSubscriptions is called THEN the system SHALL use fully asynchronous execution (no sync-over-async) and SHALL process plan detail fetches with bounded concurrency (default: max 5 concurrent API calls) to prevent thread pool starvation

2.3 WHEN the `Subscriptions()` page is loaded THEN the system SHALL use server-side pagination (default page size: 100 subscriptions) with UI pagination controls to navigate between pages, preventing database timeouts

2.4 WHEN a transient error occurs during an individual subscription's processing within FetchAllSubscriptions THEN the system SHALL log the error for that subscription and continue processing remaining subscriptions, preserving partial progress

2.5 WHEN the AdminSite is running THEN the system SHALL provide a background lazy-loading mechanism that incrementally syncs subscription data from the Marketplace API at a configurable interval (default: every 5 minutes); the sync SHALL fetch the full subscription list and diff against local data to detect additions, updates, and cancellations

2.6 WHEN the Marketplace API is consistently failing (e.g., more than 5 consecutive failures within a time window) THEN the system SHALL activate a circuit breaker that temporarily stops API calls for a cooldown period (default: 60 seconds) before attempting again, preventing cascading load on a degraded service

2.7 WHEN the background sync has not yet run or the database contains no subscriptions THEN the Subscriptions page SHALL display a message prompting the administrator to trigger a manual fetch or wait for the background sync to complete

2.8 WHEN retry attempts, sync operations, circuit breaker state changes, or partial failures occur THEN the system SHALL emit structured log entries including operation type, subscription ID (where applicable), attempt count, duration, and outcome so that operators can monitor system health

### Unchanged Behavior (Regression Prevention)

3.1 WHEN the "Fetch All Subscriptions" button is clicked THEN the system SHALL CONTINUE TO sync all subscription data (offers, plans, users, audit logs) from the Marketplace API to the local database

3.2 WHEN a subscription is fetched and already exists in the database THEN the system SHALL CONTINUE TO update its status, plan, and quantity and create audit log entries for any changes

3.3 WHEN a new subscription is fetched that does not exist in the database THEN the system SHALL CONTINUE TO create the offer, plans, user, and subscription records

3.4 WHEN the Subscriptions page is loaded THEN the system SHALL CONTINUE TO display subscription details including purchaser email, subscription ID, name, offer, plan, quantity, and status

3.5 WHEN individual subscription operations (activate, deactivate, change plan, change quantity) are performed THEN the system SHALL CONTINUE TO function correctly and independently of the fetch resilience changes

---

## Bug Condition Derivation

**Bug Condition Function** — Identifies inputs that trigger the bug:

```pascal
FUNCTION isBugCondition(X)
  INPUT: X of type SubscriptionFetchRequest
  OUTPUT: boolean
  
  // Returns true when any of the following conditions are met:
  // 1. The Marketplace API experiences transient failures (timeout, rate-limit, 5xx)
  // 2. The number of subscriptions is large enough to cause thread starvation via sync-over-async
  // 3. The database query for all subscriptions exceeds timeout thresholds
  // 4. The Marketplace API is persistently degraded (consecutive failures)
  RETURN X.apiExperiencesTransientFailure 
      OR X.subscriptionCount > threadPoolSaturationThreshold
      OR X.dbQueryDuration > dbTimeoutThreshold
      OR X.consecutiveApiFailures > circuitBreakerThreshold
END FUNCTION
```

**Property Specification** — Defines correct behavior for buggy inputs:

```pascal
// Property: Fix Checking - Resilient Fetch
FOR ALL X WHERE isBugCondition(X) DO
  result ← FetchAllSubscriptions'(X)
  ASSERT no_5xx_error(result)
    AND (X.apiExperiencesTransientFailure IMPLIES retries_attempted(result, maxRetries=3, backoff=exponential))
    AND partial_progress_preserved(result)
    AND no_thread_pool_starvation(result)
    AND (X.consecutiveApiFailures > 5 IMPLIES circuit_breaker_activated(result))
    AND structured_logs_emitted(result)
END FOR
```

**Preservation Goal** — Expressed in structured pseudocode:

```pascal
// Property: Preservation Checking
FOR ALL X WHERE NOT isBugCondition(X) DO
  ASSERT FetchAllSubscriptions(X) = FetchAllSubscriptions'(X)
    AND Subscriptions_page(X) = Subscriptions_page'(X)
    AND subscription_operations(X) = subscription_operations'(X)
END FOR
```
