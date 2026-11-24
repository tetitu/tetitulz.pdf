// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Util;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Util;

/// <summary>
/// A <see cref="WorkBudget"/> with a parent spends the parent too — a stream's budget inside a
/// page's. The parent is charged what the child could pay, never the child's overrun; the parent's
/// refusal is thrown as the parent's, before the child is charged, so a decoder that turns its own
/// refusal into a fallback can tell the two apart by <see cref="WorkBudgetExceededException.Budget"/>.
/// </summary>
public class WorkBudgetParentTests
{
    [Fact]
    public void AChildsTick_ChargesTheParent_ClampedToWhatTheChildCouldPay()
    {
        var parent = new WorkBudget(100, "page");
        var child = new WorkBudget(10, "stream", false, parent);

        child.Tick(4);
        Assert.Equal(4, parent.Spent);
        Assert.Equal(4, child.Spent);

        // Ten more overruns the child by four: the child refuses as itself, and the parent paid the
        // six the child had left — not the ten.
        var ex = Assert.Throws<WorkBudgetExceededException>(() => child.Tick(10));
        Assert.Same(child, ex.Budget);
        Assert.Equal(10, parent.Spent);
        Assert.True(child.IsExhausted);
    }

    [Fact]
    public void AParentsRefusal_IsThrownAsTheParents_BeforeTheChildIsCharged()
    {
        var parent = new WorkBudget(5, "page");
        var child = new WorkBudget(100, "stream", false, parent);

        var ex = Assert.Throws<WorkBudgetExceededException>(() => child.Tick(10));

        Assert.Same(parent, ex.Budget);
        Assert.Contains("page", ex.Message, System.StringComparison.Ordinal);
        Assert.Equal(0, child.Spent);
        Assert.False(child.IsExhausted);
    }

    [Fact]
    public void ASharedChild_ForwardsToItsParentToo()
    {
        var parent = new WorkBudget(100, "page");
        var child = new WorkBudget(50, "tiles", shared: true, parent);

        child.Tick(3);
        child.Tick(4);

        Assert.Equal(7, parent.Spent);
        Assert.Same(parent, child.Parent);
    }

    /// <summary>
    /// A negative cost (a product of file fields gone negative) is charged as nothing. As a credit to
    /// the budget and its parent, a JBIG2 symbol width of −2 147 483 600 would make 38 G units of room
    /// for the next stream.
    /// </summary>
    [Fact]
    public void ANegativeCost_IsChargedAsNothing_NeverACredit()
    {
        var parent = new WorkBudget(100, "page");
        var child = new WorkBudget(50, "stream", false, parent);

        child.Tick(30);
        child.Tick(-2_147_483_600L * 18);

        Assert.Equal(30, child.Spent);
        Assert.Equal(30, parent.Spent);

        // The room is what it was: twenty-one more is past the child's fifty.
        var ex = Assert.Throws<WorkBudgetExceededException>(() => child.Tick(21));
        Assert.Same(child, ex.Budget);
    }

    [Fact]
    public void ABudgetWithoutAParent_RefusesAsItselfAndNamesItself()
    {
        var alone = new WorkBudget(2, "alone");
        alone.Tick(2);
        var ex = Assert.Throws<WorkBudgetExceededException>(() => alone.Tick());
        Assert.Same(alone, ex.Budget);
        Assert.Null(alone.Parent);
    }
}
