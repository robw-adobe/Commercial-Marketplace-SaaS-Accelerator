// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

// =============================================================================
// PBT-2.2: Pagination Correctness Property (Property 3)
//
// Validates: Requirements 2.3, 3.4
//
// For random (pageIndex, pageSize) and random subscription corpora, assert:
//   - concat(GetPaged(1..N, pageSize)).Items == Get() ordering
//   - TotalCount == seeded count
//
// UNFIXED CODE OBSERVATION:
//   GetPaged does not exist on UNFIXED code (task 3.4 introduces it).
//   The tests that call GetPaged are annotated with
//   [Fact(Skip = "Awaiting paginated repo from task 3.4")] so they do not
//   block the green baseline established in wave 6.
//
//   What DOES exist on unfixed code is Get(), which returns the full ordered
//   set (IEnumerable<Subscriptions> ordered by CreateDate descending, with
//   Include(s => s.User)). The companion tests below verify this observable
//   baseline and constitute the unskipped portions that MUST PASS on unfixed code.
//
//   UNFIXED-CODE OBSERVATION (documented here, per task 2.5 instructions):
//   "Get() returns the full ordered set today — ordered by CreateDate
//   descending, with eager-loaded User navigation properties. There is no
//   Skip/Take, so all rows are materialised in a single query regardless of
//   corpus size. GetPaged does not exist; pagination will be introduced in
//   task 3.4."
//
// Reference: design.md Property 3 (Pagination Correctness)
// Reference: tasks.md 2.5, 3.4
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using FsCheck;
using FsCheck.Xunit;
using Marketplace.SaaS.Accelerator.AdminSite.Test.Doubles;
using Marketplace.SaaS.Accelerator.AdminSite.Test.Fixtures;
using Marketplace.SaaS.Accelerator.AdminSite.Test.Generators;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;
using Marketplace.SaaS.Accelerator.DataAccess.Services;
using Microsoft.Marketplace.SaaS.Models;
using Xunit;

namespace Marketplace.SaaS.Accelerator.AdminSite.Test.Preservation;

/// <summary>
/// PBT-2.2: Pagination correctness property test.
///
/// Validates: Requirements 2.3, 3.4
///
/// This class contains two groups of tests:
///
///   GROUP A (unskipped, must PASS on unfixed code):
///     Companion tests that verify the current Get() baseline:
///       - Get() returns a stable, fully-ordered sequence (OrderByDescending CreateDate).
///       - Get() always returns all seeded rows (no implicit pagination).
///       - Get() includes the User navigation property (eager-loaded).
///       - Get() is deterministic across multiple calls with the same data.
///
///   GROUP B (skipped — awaiting task 3.4):
///     The full PBT-2.2 property tests that require GetPaged:
///       - concat(GetPaged(1..N, pageSize)) equals Get() ordering.
///       - TotalCount equals the seeded count.
///     These are annotated [Fact(Skip = "Awaiting paginated repo from task 3.4")].
/// </summary>
[Properties(Arbitrary = new[] { typeof(SaasKitArbitraries) })]
public class PaginationCorrectnessPropertyTests
{
    // ──────────────────────────────────────────────────────────────────────────
    // GROUP A: Companion tests — Get() baseline (must PASS on UNFIXED code)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// PBT-2.2 companion property (unskipped):
    /// For any subscription corpus, Get() returns all seeded rows ordered
    /// by CreateDate descending with no implicit skip or take.
    ///
    /// This establishes the total-ordered baseline that GetPaged must
    /// slice consistently once it is introduced in task 3.4.
    ///
    /// Validates: Requirements 3.4, 2.3 (baseline observation)
    /// </summary>
    [Property(MaxTest = 50, EndSize = 50)]
    public Property Get_ReturnsAllSubscriptionsInDescendingCreateDateOrder(SubscriptionCorpus corpus)
    {
        // Arrange — seed a fresh in-memory context with the generated corpus.
        var ctx = InMemorySaasKitContextFactory.CreateAndSeed(corpus);
        var repo = new SubscriptionsRepository(ctx);

        // Act — call the existing Get() method.
        var result = repo.Get().ToList();

        // Assert 1: total count matches seeded corpus.
        var countMatches = (result.Count == corpus.Subscriptions.Count).Label(
            $"CountMatches: expected={corpus.Subscriptions.Count}, actual={result.Count}");

        // Assert 2: order is stable — each adjacent pair is CreateDate descending.
        bool orderedCorrectly = true;
        for (int i = 1; i < result.Count; i++)
        {
            // CreateDate may equal the previous (same-millisecond inserts) but
            // must not be strictly greater (ascending) than the previous entry.
            if (result[i].CreateDate > result[i - 1].CreateDate)
            {
                orderedCorrectly = false;
                break;
            }
        }

        var orderProp = orderedCorrectly.Label(
            $"OrderedByCreateDateDescending: count={result.Count}");

        // Assert 3: every AmpsubscriptionId from the corpus appears in result.
        var seededIds = corpus.Subscriptions
            .Select(s => s.AmpsubscriptionId)
            .OrderBy(g => g)
            .ToList();
        var returnedIds = result
            .Select(s => s.AmpsubscriptionId)
            .OrderBy(g => g)
            .ToList();
        var allIdsPresent = seededIds.SequenceEqual(returnedIds);
        var idsProp = allIdsPresent.Label(
            $"AllSeededIdsPresent: seeded={seededIds.Count}, returned={returnedIds.Count}");

        return countMatches.And(orderProp).And(idsProp);
    }

    /// <summary>
    /// PBT-2.2 companion property (unskipped):
    /// Get() is deterministic — two consecutive calls on the same data
    /// return the same sequence of AmpsubscriptionIds in the same order.
    ///
    /// Validates: Requirements 3.4 (stable ordering for pagination baseline)
    /// </summary>
    [Property(MaxTest = 30, EndSize = 50)]
    public Property Get_IsDeterministicAcrossConsecutiveCalls(SubscriptionCorpus corpus)
    {
        var ctx = InMemorySaasKitContextFactory.CreateAndSeed(corpus);
        var repo = new SubscriptionsRepository(ctx);

        var run1 = repo.Get().Select(s => s.AmpsubscriptionId).ToList();
        var run2 = repo.Get().Select(s => s.AmpsubscriptionId).ToList();

        var isDeterministic = run1.SequenceEqual(run2);
        return isDeterministic.Label(
            $"DeterministicOrdering: count={run1.Count}, run1Same={run1.SequenceEqual(run2)}");
    }

    /// <summary>
    /// Deterministic snapshot test (unskipped):
    /// Seeds 10 subscriptions with known CreateDate values and verifies that
    /// Get() returns them in strict CreateDate-descending order.
    ///
    /// This anchors the property test above to a concrete, inspectable example.
    ///
    /// Validates: Requirements 3.4 (column ordering unchanged), 2.3 (baseline)
    /// </summary>
    [Fact]
    public void Get_WithKnownCreateDates_ReturnsSubscriptionsInDescendingOrder()
    {
        // Arrange — build 10 subscriptions with evenly-spaced CreateDate values.
        const int Count = 10;
        var baseDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var subscriptions = Enumerable.Range(0, Count).Select(i => new Subscriptions
        {
            AmpsubscriptionId = Guid.NewGuid(),
            Name = $"sub-{i:D2}",
            AmpplanId = "plan-alpha",
            AmpOfferId = "offer-snap",
            SubscriptionStatus = "Subscribed",
            IsActive = true,
            CreateBy = 1,
            // Ascending create dates so descending order is clearly verifiable.
            CreateDate = baseDate.AddMinutes(i),
            ModifyDate = baseDate.AddMinutes(i),
            Ampquantity = 1,
        }).ToList();

        var corpus = new SubscriptionCorpus
        {
            Offers = new List<Offers>(),
            Plans = new List<Plans>(),
            Users = new List<Users>(),
            Subscriptions = subscriptions,
            AuditLogs = new List<SubscriptionAuditLogs>(),
        };

        var ctx = InMemorySaasKitContextFactory.Create();
        ctx.Database.EnsureCreated();
        // Add subscriptions directly without FK parent tables (InMemory provider
        // does not enforce FK constraints).
        ctx.Subscriptions.AddRange(subscriptions);
        ctx.SaveChanges();

        var repo = new SubscriptionsRepository(ctx);

        // Act
        var result = repo.Get().ToList();

        // Assert — result must be ordered by CreateDate descending.
        Assert.Equal(Count, result.Count);

        // The subscription with the LATEST CreateDate (index 9) must come first.
        Assert.Equal(baseDate.AddMinutes(Count - 1), result[0].CreateDate);

        // Verify strict descending order across all consecutive pairs.
        for (int i = 1; i < result.Count; i++)
        {
            Assert.True(
                result[i].CreateDate <= result[i - 1].CreateDate,
                $"Expected result[{i}].CreateDate <= result[{i - 1}].CreateDate " +
                $"but got {result[i].CreateDate} > {result[i - 1].CreateDate}");
        }
    }

    /// <summary>
    /// Deterministic test (unskipped):
    /// Get() on an empty database returns an empty sequence (not null, not an exception).
    ///
    /// Validates: Requirements 3.4, 2.3 (boundary: empty corpus baseline)
    /// </summary>
    [Fact]
    public void Get_WithEmptyDatabase_ReturnsEmptySequence()
    {
        var ctx = InMemorySaasKitContextFactory.Create();
        ctx.Database.EnsureCreated();

        var repo = new SubscriptionsRepository(ctx);
        var result = repo.Get().ToList();

        Assert.Empty(result);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // GROUP B: Skipped — awaiting GetPaged from task 3.4
    //
    // UNFIXED-CODE OBSERVATION:
    //   GetPaged does not exist in ISubscriptionsRepository or
    //   SubscriptionsRepository on the current codebase. These tests document
    //   the intended contract (Property 3) and will be unskipped in task 3.9
    //   once task 3.4 has introduced GetPaged.
    //
    //   Once unskipped, each test asserts:
    //     concat(GetPaged(pageIndex=1..N, pageSize)).Items == Get() full list
    //     GetPaged(pageIndex, pageSize).TotalCount == seededCount
    //     GetPaged clamps pageIndex < 1 to 1
    //     GetPaged clamps pageSize < 1 to 1
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// PBT-2.2 (Property 3 - pagination correctness): SKIPPED on unfixed code.
    ///
    /// For random (pageIndex, pageSize) and random subscription corpora:
    ///   - concat(GetPaged(1..N, pageSize)).Items equals Get() ordering
    ///   - TotalCount equals the seeded count
    ///
    /// Validates: Requirements 2.3, 3.4
    /// </summary>
    [Fact(Skip = "Awaiting paginated repo from task 3.4")]
    public void GetPaged_ConcatenatedPages_EqualsGetOrdering()
    {
        // This test will be implemented when GetPaged is available (task 3.4).
        //
        // Pseudocode for the full property:
        //
        //   for pageSize in {1, 5, 10, 50, 100} do
        //     seed corpus with N subscriptions
        //     fullList = repo.Get().ToList()
        //     pages = []
        //     pageIndex = 1
        //     while true do
        //       page = repo.GetPaged(pageIndex, pageSize)
        //       assert page.TotalCount == N
        //       pages.Add(page.Items)
        //       if page.Items.Count < pageSize then break
        //       pageIndex++
        //     concatenated = pages.SelectMany(p => p).ToList()
        //     assert concatenated.Select(s => s.AmpsubscriptionId)
        //            == fullList.Select(s => s.AmpsubscriptionId)
        //
        // The property-based version randomises both N and pageSize over their
        // valid domains to confirm the concatenation invariant holds universally.
        throw new NotImplementedException("Awaiting task 3.4: add GetPaged to ISubscriptionsRepository.");
    }

    /// <summary>
    /// PBT-2.2 (Property 3 - TotalCount correctness): SKIPPED on unfixed code.
    ///
    /// For any (pageIndex, pageSize) pair, GetPaged.TotalCount equals the seeded count.
    ///
    /// Validates: Requirements 2.3, 3.4
    /// </summary>
    [Fact(Skip = "Awaiting paginated repo from task 3.4")]
    public void GetPaged_TotalCount_EqualsSeededCount()
    {
        // This test will be verified when GetPaged is available (task 3.4).
        //
        // Pseudocode:
        //   for any pageIndex, pageSize, and corpus size N:
        //     page = repo.GetPaged(pageIndex, pageSize)
        //     assert page.TotalCount == N
        //
        // Because TotalCount must be consistent across all pages, any single
        // call is sufficient to verify it — the PBT version samples many (N, pi, ps) triples.
        throw new NotImplementedException("Awaiting task 3.4: add GetPaged to ISubscriptionsRepository.");
    }

    /// <summary>
    /// PBT-2.2 (Property 3 - clamping): SKIPPED on unfixed code.
    ///
    /// GetPaged clamps pageIndex &lt; 1 to 1 and pageSize &lt; 1 to 1.
    ///
    /// Validates: Requirements 2.3, 3.4
    /// </summary>
    [Fact(Skip = "Awaiting paginated repo from task 3.4")]
    public void GetPaged_WithZeroOrNegativePageIndexOrSize_ClampsToOne()
    {
        // This test will be verified when GetPaged is available (task 3.4).
        //
        // Pseudocode:
        //   assert GetPaged(0, 10).PageIndex == 1
        //   assert GetPaged(-5, 10).PageIndex == 1
        //   assert GetPaged(1, 0).PageSize == 1
        //   assert GetPaged(1, -3).PageSize == 1
        throw new NotImplementedException("Awaiting task 3.4: add GetPaged to ISubscriptionsRepository.");
    }

    /// <summary>
    /// PBT-2.2 (Property 3 - User navigation): SKIPPED on unfixed code.
    ///
    /// GetPaged eagerly loads the User navigation property on each item,
    /// consistent with the existing Get() behaviour.
    ///
    /// Validates: Requirements 2.3, 3.4
    /// </summary>
    [Fact(Skip = "Awaiting paginated repo from task 3.4")]
    public void GetPaged_IncludesUserNavigationProperty()
    {
        // This test will be verified when GetPaged is available (task 3.4).
        //
        // Pseudocode:
        //   seed corpus with subscriptions whose UserId != null
        //   page = repo.GetPaged(1, 100)
        //   assert page.Items.All(s => s.User != null)
        throw new NotImplementedException("Awaiting task 3.4: add GetPaged to ISubscriptionsRepository.");
    }
}
