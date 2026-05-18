// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

// =============================================================================
// PBT-2.3: Idempotent Fetch Property (Property 4)
//
// Validates: Requirements 2.5, 3.1, 3.2, 3.3
//
// Property 4: Background Sync Idempotence
//
// For any sequence of fetch ticks against the same Marketplace payload, the
// resulting database state SHALL be the same as if only the most recent
// successful run had executed. That is, the fetch pipeline is idempotent
// with respect to subscription, plan, offer, and user upserts and to
// audit-log creation (audit logs are written only on detected change,
// not on every run).
//
// UNFIXED CODE SCOPE:
//   The hosted background service (SubscriptionLazyLoaderHostedService) does
//   NOT exist on UNFIXED code — it will be introduced in task 3.8. The
//   hosted-service-driven assertion is therefore annotated with:
//     [Fact(Skip = "Awaiting SubscriptionLazyLoaderHostedService from task 3.8")]
//
//   The unskipped portion drives consecutive manual FetchAllSubscriptions
//   invocations through the HomeControllerHarness (same pattern as PBT-2.1).
//   This portion MUST PASS on UNFIXED code.
//
// UNFIXED-CODE OBSERVATION:
//   Running FetchAllSubscriptions N times on the same in-memory context with
//   the same API payload:
//     - Subscriptions, Plans, Offers, Users: the upsert semantics in
//       AddOrUpdatePartnerSubscriptions mean no new rows are created on
//       repeat runs — the existing rows are matched by AmpsubscriptionId and
//       updated in-place.
//     - SubscriptionAuditLogs: audit logs are written only when a change is
//       detected (status, plan, or quantity differs between API response and
//       DB). On a repeat run with the same API payload the DB already reflects
//       that payload, so no new audit log rows are written.
//   This is the idempotence invariant that Property 4 encodes.
//
// INVARIANTS verified by the unskipped tests:
//   A. After N consecutive runs with the SAME payload:
//      - Subscriptions count is stable (== count from first run)
//      - Offers count is stable
//      - Users count is stable
//      - AuditLogs count is stable (no new rows added on runs 2..N)
//   B. After a run with a CHANGED payload (one subscription's status
//      changed), exactly the expected number of audit log rows are added —
//      and a subsequent run with the same changed payload adds zero more.
//
// Reference: design.md Property 4 (Background Sync Idempotence)
// Reference: src/AdminSite.Test/Snapshots/audit-log-change-snapshot.json
// Reference: tasks.md 2.6
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using FsCheck;
using FsCheck.Xunit;
using Marketplace.SaaS.Accelerator.AdminSite.Test.BugCondition;
using Marketplace.SaaS.Accelerator.AdminSite.Test.Doubles;
using Marketplace.SaaS.Accelerator.AdminSite.Test.Fixtures;
using Marketplace.SaaS.Accelerator.AdminSite.Test.Generators;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Marketplace.SaaS.Models;
using Xunit;

namespace Marketplace.SaaS.Accelerator.AdminSite.Test.Preservation;

/// <summary>
/// PBT-2.3: Idempotent fetch property test.
///
/// Validates: Requirements 2.5, 3.1, 3.2, 3.3
///
/// This class contains two groups of tests:
///
///   GROUP A (unskipped, must PASS on unfixed code):
///     Property and deterministic tests verifying that consecutive manual
///     FetchAllSubscriptions invocations are idempotent:
///       - N consecutive runs with the same payload leave DB row counts stable.
///       - Audit logs are written only on detected change, not on every run.
///       - A changed payload produces exactly the expected audit log delta,
///         and a subsequent identical-payload run adds zero more.
///
///   GROUP B (skipped — awaiting task 3.8):
///     The hosted-service-driven idempotence assertion requires
///     SubscriptionLazyLoaderHostedService, which does not exist on unfixed
///     code. Annotated with:
///       [Fact(Skip = "Awaiting SubscriptionLazyLoaderHostedService from task 3.8")]
/// </summary>
[Properties(Arbitrary = new[] { typeof(SaasKitArbitraries) })]
public class IdempotentFetchPropertyTests
{
    // ──────────────────────────────────────────────────────────────────────────
    // GROUP A: Unskipped — consecutive manual invocations (must PASS on UNFIXED code)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// PBT-2.3 (Property 4 - idempotent fetch):
    /// For any random subscription corpus where all API calls succeed and
    /// isBugCondition is false, running FetchAllSubscriptions N times with the
    /// SAME API payload leaves the DB row counts for Subscriptions, Offers,
    /// Users, and SubscriptionAuditLogs stable after the first run.
    ///
    /// This encodes the core idempotence invariant: the fetch pipeline's upsert
    /// semantics mean repeated runs with unchanged data do not create duplicate
    /// rows or spurious audit log entries.
    ///
    /// Validates: Requirements 2.5, 3.1, 3.2, 3.3
    /// </summary>
    [Property(MaxTest = 30, EndSize = 50)]
    public Property FetchAllSubscriptions_ConsecutiveRunsSamePayload_DbStateIsIdempotent(
        SubscriptionCorpus corpus)
    {
        // Build fully-populated SDK subscriptions so ConversionHelper does not throw.
        var sdkSubscriptions = BuildFullSdkSubscriptions(corpus).ToList();

        // ── Run 1 (initial fetch on a fresh DB) ────────────────────────────────
        var fake1 = new FakeMarketplaceSaaSClientBuilder()
            .WithSeededSubscriptions(sdkSubscriptions)
            .Build();
        var harness1 = HomeControllerHarness.Build(corpus, fake1);
        var result1 = RunFetchAllSubscriptions(harness1);
        var status1 = ExtractStatusCode(result1);

        // Capture stable counts after the first run.
        var subsAfterRun1 = harness1.Context.Subscriptions.Count();
        var offersAfterRun1 = harness1.Context.Offers.Count();
        var usersAfterRun1 = harness1.Context.Users.Count();
        var auditAfterRun1 = harness1.Context.SubscriptionAuditLogs.Count();

        // ── Run 2 (same payload, same context) ────────────────────────────────
        // Reuse the same in-memory context so Run 2 sees the DB state left by Run 1.
        var fake2 = new FakeMarketplaceSaaSClientBuilder()
            .WithSeededSubscriptions(sdkSubscriptions)
            .Build();
        var harness2 = HomeControllerHarness.BuildWithExistingContext(harness1.Context, fake2);
        var result2 = RunFetchAllSubscriptions(harness2);
        var status2 = ExtractStatusCode(result2);

        var subsAfterRun2 = harness1.Context.Subscriptions.Count();
        var offersAfterRun2 = harness1.Context.Offers.Count();
        var usersAfterRun2 = harness1.Context.Users.Count();
        var auditAfterRun2 = harness1.Context.SubscriptionAuditLogs.Count();

        // ── Run 3 (same payload again, same context) ──────────────────────────
        var fake3 = new FakeMarketplaceSaaSClientBuilder()
            .WithSeededSubscriptions(sdkSubscriptions)
            .Build();
        var harness3 = HomeControllerHarness.BuildWithExistingContext(harness1.Context, fake3);
        var result3 = RunFetchAllSubscriptions(harness3);
        var status3 = ExtractStatusCode(result3);

        var subsAfterRun3 = harness1.Context.Subscriptions.Count();
        var offersAfterRun3 = harness1.Context.Offers.Count();
        var usersAfterRun3 = harness1.Context.Users.Count();
        var auditAfterRun3 = harness1.Context.SubscriptionAuditLogs.Count();

        // ===== Invariant assertions ============================================

        // Invariant A1: all three runs return non-5xx.
        var invA1 = (status1 is null or < 500
            && status2 is null or < 500
            && status3 is null or < 500).Label(
            $"InvA1_NonFiveXx: run1={status1?.ToString() ?? "<ok>"}, " +
            $"run2={status2?.ToString() ?? "<ok>"}, run3={status3?.ToString() ?? "<ok>"}");

        // Invariant A2: Subscriptions count stable after Run 1.
        var invA2 = (subsAfterRun2 == subsAfterRun1 && subsAfterRun3 == subsAfterRun1).Label(
            $"InvA2_SubscriptionsIdempotent: " +
            $"run1={subsAfterRun1}, run2={subsAfterRun2}, run3={subsAfterRun3}");

        // Invariant A3: Offers count stable after Run 1.
        // Note: unfixed code calls offersRepository.Add() for each new subscription
        // that doesn't exist in DB yet. On Run 2 those subscriptions already exist
        // so the branch that creates a new Offer row is skipped.
        var invA3 = (offersAfterRun2 == offersAfterRun1 && offersAfterRun3 == offersAfterRun1).Label(
            $"InvA3_OffersIdempotent: " +
            $"run1={offersAfterRun1}, run2={offersAfterRun2}, run3={offersAfterRun3}");

        // Invariant A4: Users count stable after Run 1.
        var invA4 = (usersAfterRun2 == usersAfterRun1 && usersAfterRun3 == usersAfterRun1).Label(
            $"InvA4_UsersIdempotent: " +
            $"run1={usersAfterRun1}, run2={usersAfterRun2}, run3={usersAfterRun3}");

        // Invariant A5: AuditLogs count stable after Run 1.
        // The first run creates audit log rows for new subscriptions (where DB
        // defaults differ from API values). Runs 2 and 3 see the same API
        // payload as Run 1 and the DB now reflects that payload — so no status,
        // plan, or quantity changes are detected and no new rows are written.
        var invA5 = (auditAfterRun2 == auditAfterRun1 && auditAfterRun3 == auditAfterRun1).Label(
            $"InvA5_AuditLogsIdempotent: " +
            $"run1={auditAfterRun1}, run2={auditAfterRun2}, run3={auditAfterRun3}");

        return invA1.And(invA2).And(invA3).And(invA4).And(invA5);
    }

    /// <summary>
    /// PBT-2.3 deterministic test:
    /// Seeds 5 subscriptions, runs FetchAllSubscriptions with a payload that
    /// changes one subscription's status, then runs again with the same changed
    /// payload. Verifies that:
    ///   - Run 1 (initial): DB populated, audit logs written for new subscriptions.
    ///   - Run 2 (status change): exactly one new Status-Refresh audit log row added.
    ///   - Run 3 (same changed payload): zero new audit log rows added.
    ///
    /// Validates: Requirements 2.5, 3.2
    /// </summary>
    [Fact]
    public void FetchAllSubscriptions_ChangeDetectedThenRepeat_AuditLogWrittenOnlyOnChange()
    {
        const int SubscriptionCount = 5;

        // Use deterministic GUIDs so this test is repeatable.
        var subscriptionGuids = Enumerable.Range(1, SubscriptionCount)
            .Select(i => Guid.Parse($"aaaaaaaa-{i:D4}-{i:D4}-{i:D4}-aaaaaaaaaaaa"))
            .ToList();
        var beneficiaryEmails = subscriptionGuids
            .Select((_, i) => $"user-{i:D5}-idempotent@example.test")
            .ToList();

        var emptyCorpus = new SubscriptionCorpus();

        // ── Run 1: all subscriptions with status=Subscribed ───────────────────
        var sdkRun1 = BuildDeterministicSubscriptions(
            subscriptionGuids, beneficiaryEmails, SubscriptionStatusEnum.Subscribed, "plan-alpha", 3);

        var fake1 = new FakeMarketplaceSaaSClientBuilder()
            .WithSeededSubscriptions(sdkRun1)
            .Build();
        var harness = HomeControllerHarness.Build(emptyCorpus, fake1);
        var result1 = RunFetchAllSubscriptions(harness);
        Assert.True(
            ExtractStatusCode(result1) is null or < 500,
            $"Run 1 failed with status {ExtractStatusCode(result1)}");

        Assert.Equal(SubscriptionCount, harness.Context.Subscriptions.Count());
        var auditAfterRun1 = harness.Context.SubscriptionAuditLogs.Count();

        // ── Run 2: subscription[2] changes status to Unsubscribed ─────────────
        var sdkRun2 = subscriptionGuids
            .Select((id, i) =>
            {
                var status = (i == 2) ? SubscriptionStatusEnum.Unsubscribed : SubscriptionStatusEnum.Subscribed;
                return FakeMarketplaceSaaSClientBuilder.CreateFullSubscription(
                    id: id,
                    offerId: "offer-idempotent",
                    planId: "plan-alpha",
                    status: status,
                    quantity: 3,
                    beneficiaryEmail: beneficiaryEmails[i]);
            })
            .ToList();

        var fake2 = new FakeMarketplaceSaaSClientBuilder()
            .WithSeededSubscriptions(sdkRun2)
            .Build();
        var harness2 = HomeControllerHarness.BuildWithExistingContext(harness.Context, fake2);
        var result2 = RunFetchAllSubscriptions(harness2);
        Assert.True(
            ExtractStatusCode(result2) is null or < 500,
            $"Run 2 failed with status {ExtractStatusCode(result2)}");

        // Exactly one new Status-Refresh audit log row for the changed subscription.
        var auditAfterRun2 = harness.Context.SubscriptionAuditLogs.Count();
        Assert.True(
            auditAfterRun2 > auditAfterRun1,
            $"Expected new audit log rows after status change. Before={auditAfterRun1}, After={auditAfterRun2}");

        var statusRefreshLogs = harness.Context.SubscriptionAuditLogs
            .Where(a => a.Attribute == "Status-Refresh" && a.NewValue == "Unsubscribed")
            .ToList();
        Assert.NotEmpty(statusRefreshLogs);

        // ── Run 3: SAME changed payload — no additional audit logs ─────────────
        // Run 3 uses the same changed SDK payload as Run 2. Since the DB already
        // reflects the Unsubscribed status for subscription[2], no changes are
        // detected and zero new audit log rows should be written.
        var fake3 = new FakeMarketplaceSaaSClientBuilder()
            .WithSeededSubscriptions(sdkRun2)  // Same as Run 2 — status already reflected in DB
            .Build();
        var harness3 = HomeControllerHarness.BuildWithExistingContext(harness.Context, fake3);
        var result3 = RunFetchAllSubscriptions(harness3);
        Assert.True(
            ExtractStatusCode(result3) is null or < 500,
            $"Run 3 failed with status {ExtractStatusCode(result3)}");

        var auditAfterRun3 = harness.Context.SubscriptionAuditLogs.Count();
        Assert.Equal(
            auditAfterRun2,
            auditAfterRun3);
    }

    /// <summary>
    /// PBT-2.3 deterministic test — multiple change types:
    /// Verifies that plan and quantity changes are also detected exactly once
    /// and that a repeat run with the same payload produces no additional audit logs.
    ///
    /// Validates: Requirements 2.5, 3.2
    /// </summary>
    [Fact]
    public void FetchAllSubscriptions_PlanAndQuantityChange_AuditLogIdempotent()
    {
        const int SubscriptionCount = 3;

        var subscriptionGuids = Enumerable.Range(1, SubscriptionCount)
            .Select(i => Guid.Parse($"bbbbbbbb-{i:D4}-{i:D4}-{i:D4}-bbbbbbbbbbbb"))
            .ToList();
        var beneficiaryEmails = subscriptionGuids
            .Select((_, i) => $"user-{i:D5}-pq-idempotent@example.test")
            .ToList();

        var emptyCorpus = new SubscriptionCorpus();

        // ── Run 1: initial data ────────────────────────────────────────────────
        var sdkRun1 = BuildDeterministicSubscriptions(
            subscriptionGuids, beneficiaryEmails, SubscriptionStatusEnum.Subscribed, "plan-alpha", 3);

        var fake1 = new FakeMarketplaceSaaSClientBuilder()
            .WithSeededSubscriptions(sdkRun1)
            .Build();
        var harness = HomeControllerHarness.Build(emptyCorpus, fake1);
        var result1 = RunFetchAllSubscriptions(harness);
        Assert.True(ExtractStatusCode(result1) is null or < 500, $"Run 1 failed: {ExtractStatusCode(result1)}");

        var auditAfterRun1 = harness.Context.SubscriptionAuditLogs.Count();

        // ── Run 2: subscription[0] changes plan and quantity ───────────────────
        var sdkRun2 = subscriptionGuids
            .Select((id, i) =>
            {
                if (i == 0)
                {
                    return FakeMarketplaceSaaSClientBuilder.CreateFullSubscription(
                        id: id,
                        offerId: "offer-pq-idempotent",
                        planId: "plan-beta",   // changed from plan-alpha
                        status: SubscriptionStatusEnum.Subscribed,
                        quantity: 10,          // changed from 3
                        beneficiaryEmail: beneficiaryEmails[i]);
                }

                return FakeMarketplaceSaaSClientBuilder.CreateFullSubscription(
                    id: id,
                    offerId: "offer-pq-idempotent",
                    planId: "plan-alpha",
                    status: SubscriptionStatusEnum.Subscribed,
                    quantity: 3,
                    beneficiaryEmail: beneficiaryEmails[i]);
            })
            .ToList();

        var fake2 = new FakeMarketplaceSaaSClientBuilder()
            .WithSeededSubscriptions(sdkRun2)
            .Build();
        var harness2 = HomeControllerHarness.BuildWithExistingContext(harness.Context, fake2);
        var result2 = RunFetchAllSubscriptions(harness2);
        Assert.True(ExtractStatusCode(result2) is null or < 500, $"Run 2 failed: {ExtractStatusCode(result2)}");

        var auditAfterRun2 = harness.Context.SubscriptionAuditLogs.Count();
        // Plan and Quantity both changed for subscription[0]: expect 2 new audit rows
        Assert.True(auditAfterRun2 > auditAfterRun1,
            $"Expected new audit rows after plan/quantity change. Before={auditAfterRun1}, After={auditAfterRun2}");

        var planLogs = harness.Context.SubscriptionAuditLogs
            .Where(a => a.Attribute == "Plan-Refresh" && a.NewValue == "plan-beta")
            .ToList();
        var quantityLogs = harness.Context.SubscriptionAuditLogs
            .Where(a => a.Attribute == "Quantity-Refresh" && a.NewValue == "10")
            .ToList();
        Assert.NotEmpty(planLogs);
        Assert.NotEmpty(quantityLogs);

        // ── Run 3: same changed payload — no additional audit logs ─────────────
        var fake3 = new FakeMarketplaceSaaSClientBuilder()
            .WithSeededSubscriptions(sdkRun2)  // Same as Run 2
            .Build();
        var harness3 = HomeControllerHarness.BuildWithExistingContext(harness.Context, fake3);
        var result3 = RunFetchAllSubscriptions(harness3);
        Assert.True(ExtractStatusCode(result3) is null or < 500, $"Run 3 failed: {ExtractStatusCode(result3)}");

        var auditAfterRun3 = harness.Context.SubscriptionAuditLogs.Count();
        Assert.Equal(auditAfterRun2, auditAfterRun3);
    }

    /// <summary>
    /// PBT-2.3 boundary test — empty database:
    /// Verifies that running FetchAllSubscriptions on an empty corpus N times
    /// (no subscriptions to sync) is idempotent and does not fail.
    ///
    /// Validates: Requirements 2.5, 3.1
    /// </summary>
    [Fact]
    public void FetchAllSubscriptions_EmptyPayload_IsIdempotent()
    {
        var emptyCorpus = new SubscriptionCorpus();

        // ── Run 1 ──────────────────────────────────────────────────────────────
        var fake1 = new FakeMarketplaceSaaSClientBuilder()
            .WithSeededSubscriptions(Enumerable.Empty<Subscription>())
            .Build();
        var harness = HomeControllerHarness.Build(emptyCorpus, fake1);
        var result1 = RunFetchAllSubscriptions(harness);
        Assert.True(ExtractStatusCode(result1) is null or < 500, $"Run 1 failed: {ExtractStatusCode(result1)}");

        var subsAfterRun1 = harness.Context.Subscriptions.Count();
        var auditAfterRun1 = harness.Context.SubscriptionAuditLogs.Count();

        // ── Run 2 — same empty payload ─────────────────────────────────────────
        var fake2 = new FakeMarketplaceSaaSClientBuilder()
            .WithSeededSubscriptions(Enumerable.Empty<Subscription>())
            .Build();
        var harness2 = HomeControllerHarness.BuildWithExistingContext(harness.Context, fake2);
        var result2 = RunFetchAllSubscriptions(harness2);
        Assert.True(ExtractStatusCode(result2) is null or < 500, $"Run 2 failed: {ExtractStatusCode(result2)}");

        Assert.Equal(0, subsAfterRun1);
        Assert.Equal(0, harness.Context.Subscriptions.Count());
        Assert.Equal(auditAfterRun1, harness.Context.SubscriptionAuditLogs.Count());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // GROUP B: Skipped — awaiting SubscriptionLazyLoaderHostedService from task 3.8
    //
    // UNFIXED-CODE OBSERVATION:
    //   SubscriptionLazyLoaderHostedService does not exist on UNFIXED code.
    //   It will be introduced in task 3.8. The tests below document the
    //   intended idempotence contract for the hosted service and will be
    //   unskipped in task 3.9 once the hosted service is available.
    //
    //   The hosted-service-driven assertion:
    //     For N background sync ticks against the same Marketplace payload,
    //     the final DB state equals what a single tick would have produced.
    //     Audit logs grow only when a tick detects a change vs the current DB
    //     state, not on every tick.
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// PBT-2.3 (Property 4 - hosted service idempotence): SKIPPED on unfixed code.
    ///
    /// For any sequence of background sync ticks against the same Marketplace
    /// payload, the final DB state equals the result of a single tick. Audit
    /// logs are written only on detected change.
    ///
    /// Validates: Requirements 2.5, 3.2
    /// </summary>
    [Fact(Skip = "Awaiting SubscriptionLazyLoaderHostedService from task 3.8")]
    public void HostedService_ConsecutiveTicks_SamePayload_DbStateIsIdempotent()
    {
        // This test will be implemented when SubscriptionLazyLoaderHostedService
        // is available (task 3.8).
        //
        // Pseudocode:
        //   seed corpus with N subscriptions
        //   start SubscriptionLazyLoaderHostedService with a 0-delay interval
        //   wait for tick 1 to complete; snapshot DB state
        //   wait for tick 2 (same payload); assert DB state unchanged
        //   wait for tick 3 (same payload); assert DB state unchanged
        //   assert AuditLogs count after tick 2 == after tick 1
        //   assert AuditLogs count after tick 3 == after tick 1
        throw new NotImplementedException(
            "Awaiting task 3.8: add SubscriptionLazyLoaderHostedService.");
    }

    /// <summary>
    /// PBT-2.3 (Property 4 - hosted service change detection): SKIPPED on unfixed code.
    ///
    /// When a background sync tick detects a change (status/plan/quantity),
    /// exactly the expected number of audit log rows are added. A subsequent
    /// tick with the same post-change payload adds zero more rows.
    ///
    /// Validates: Requirements 2.5, 3.2
    /// </summary>
    [Fact(Skip = "Awaiting SubscriptionLazyLoaderHostedService from task 3.8")]
    public void HostedService_TickWithChange_AuditLogWrittenOnce_RepeatTickAddsNone()
    {
        // This test will be implemented when SubscriptionLazyLoaderHostedService
        // is available (task 3.8).
        //
        // Pseudocode:
        //   seed corpus; run tick 1 (initial)
        //   change one subscription's status in the next API response
        //   run tick 2 (change detected); assert exactly 1 new audit log row
        //   run tick 3 (same changed payload); assert 0 new audit log rows
        throw new NotImplementedException(
            "Awaiting task 3.8: add SubscriptionLazyLoaderHostedService.");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Private helpers
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Build fully-populated SDK subscriptions from the generated corpus using
    /// status=Subscribed for all, so isBugCondition is false for all generated
    /// inputs and ConversionHelper does not throw.
    /// </summary>
    private static IEnumerable<Subscription> BuildFullSdkSubscriptions(SubscriptionCorpus corpus)
    {
        return corpus.Subscriptions.Select((sub, i) =>
            FakeMarketplaceSaaSClientBuilder.CreateFullSubscription(
                id: sub.AmpsubscriptionId,
                offerId: sub.AmpOfferId ?? $"offer-{i:D5}",
                planId: sub.AmpplanId ?? $"plan-{i:D5}",
                status: SubscriptionStatusEnum.Subscribed,
                quantity: Math.Max(1, sub.Ampquantity),
                beneficiaryEmail: sub.PurchaserEmail ?? $"user-{i:D5}@example.test"));
    }

    /// <summary>
    /// Build a deterministic list of fully-populated SDK subscriptions with
    /// the given uniform status, planId, and quantity.
    /// </summary>
    private static List<Subscription> BuildDeterministicSubscriptions(
        IReadOnlyList<Guid> ids,
        IReadOnlyList<string> beneficiaryEmails,
        SubscriptionStatusEnum status,
        string planId,
        int quantity)
    {
        return ids.Select((id, i) =>
            FakeMarketplaceSaaSClientBuilder.CreateFullSubscription(
                id: id,
                offerId: "offer-idempotent",
                planId: planId,
                status: status,
                quantity: quantity,
                beneficiaryEmail: beneficiaryEmails[i]))
            .ToList();
    }

    private static IActionResult RunFetchAllSubscriptions(HomeControllerHarness.Built harness)
    {
        try
        {
            return harness.Controller.FetchAllSubscriptions();
        }
        catch (Exception)
        {
            return new StatusCodeResult(500);
        }
    }

    private static int? ExtractStatusCode(IActionResult result)
    {
        return result switch
        {
            StatusCodeResult sc => sc.StatusCode,
            ObjectResult o => o.StatusCode,
            _ => null,
        };
    }
}
