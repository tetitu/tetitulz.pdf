// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// Locks in the §46 DoS guards on PdfSanitizer:
/// <list type="bullet">
///   <item><c>SanitizeNextChain</c> bounds recursion — but at <c>PdfSanitizer.MaxSanitizeDepth</c>
///         (100), NOT at <c>PdfLimits.MaxActionChainDepth</c> (64) as this summary claimed for its
///         whole life. See the third test for the mechanism and why the obvious assertion was
///         unfalsifiable.</item>
///   <item><c>SanitizeNextChain</c> tracks visited dicts to break cycles.</item>
/// </list>
/// Without these guards a circular or pathologically-deep <c>/Next</c>
/// action chain would cause stack-overflow during sanitisation.
/// </summary>
public class SanitizerDosGuardTests
{
    /// <summary>
    /// Builds a circular action: A → B → A.  Pre-§46 the visited-set
    /// guard didn't exist and this would re-enter SanitizeDictionary
    /// indefinitely.  We invoke the private SanitizeNextChain via
    /// reflection so the test doesn't have to construct a full PDF.
    /// </summary>
    [Fact]
    public void SanitizeNextChain_CircularChain_DoesNotInfiniteLoop()
    {
        var actionA = new PdfDictionary();
        actionA.Set(PdfName.S, new PdfName("URI"));
        var actionB = new PdfDictionary();
        actionB.Set(PdfName.S, new PdfName("URI"));
        // A.Next = B; B.Next = A — circular.
        actionA.Set(PdfName.Next, actionB);
        actionB.Set(PdfName.Next, actionA);

        var options = new SanitizeOptions();
        InvokeSanitizeNextChain(actionA, options);

        // Completion IS the assertion here, and there is deliberately no other one. The cycle
        // guard is `visited.Add(actionDict)` returning false; it mutates nothing, so on a chain of
        // actions the sanitizer does not consider dangerous there is no post-condition to read.
        // Without the guard this recurses A -> B -> A forever and the test host dies; with it, the
        // call returns. An `Assert.True(true)` here would assert nothing while looking like it
        // asserted something, and would pass just as well against a deleted guard, provided the
        // deletion happened not to hang.
        //
        // The companion below CAN read a post-condition, and does.
    }

    /// <summary>
    /// A 200-deep linear chain must not overflow the stack. Completion is the assertion for the
    /// *no-hang* claim; the two tests after it assert the bound itself.
    /// </summary>
    [Fact]
    public void SanitizeNextChain_DeeplyNested_DoesNotOverflowTheStack()
    {
        InvokeSanitizeNextChain(BuildChain(200, dangerousAt: -1, out _), new SanitizeOptions());

        // Without the recursion guard a 200-deep chain overflows the stack and the test host dies
        // (a StackOverflowException is uncatchable in .NET, so there is nothing to assert on).
        // With it, this returns. The bound that makes it return is asserted below.
    }

    /// <summary>
    /// A dangerous action <b>within</b> the depth bound is severed: its parent loses <c>/Next</c>.
    /// </summary>
    /// <remarks>
    /// The floor under the test below. Without this pair, "the deep one survives" would be
    /// satisfied by a sanitizer that severs nothing at all.
    /// </remarks>
    [Fact]
    public void SanitizeNextChain_DangerousActionWithinTheBound_IsSevered()
    {
        var head = BuildChain(200, dangerousAt: 30, out var parentOfDangerous);

        InvokeSanitizeNextChain(head, new SanitizeOptions());

        Assert.False(parentOfDangerous.ContainsKey(PdfName.Next),
            "a JavaScript action 30 links down was not severed, so the walk is not reaching " +
            "actions it is supposed to strip.");
    }

    /// <summary>
    /// The chain walk follows the <b>whole</b> chain, however long. A dangerous action at link
    /// 1 000 is severed exactly like one at link 3.
    /// </summary>
    /// <remarks>
    /// <para><b>No reach that no constant describes.</b> A recursive walk in which each hop calls
    /// <c>SanitizeDictionary</c>, which sees <c>/Next</c> and calls straight back in at
    /// <c>depth: 0</c>, never reaches <c>PdfLimits.MaxActionChainDepth</c> (64), while an ambient
    /// depth (100) that unwinds on exit gauges stack depth rather than accumulating: the two compose
    /// into an accidental reach of 227 links, and a mutation of <c>MaxActionChainDepth</c> shows no
    /// red, because that cap is not the operative one.</para>
    /// <para><b>Unbounded is the right answer for a sanitizer</b>, because failing to strip is the
    /// harmful direction — a 228-link chain would keep its JavaScript action with nothing reporting
    /// it. The walk is iterative and admits each dictionary once, so it consumes no stack per link
    /// and cannot run away.</para>
    /// <para>1 000 is far past every such cap (64, 100, and the 227 they produce), so this goes red
    /// if any of them comes back.</para>
    /// </remarks>
    [Theory]
    [InlineData(3)]
    [InlineData(63)]      // just inside the old MaxActionChainDepth
    [InlineData(226)]     // the last link the old walk reached
    [InlineData(227)]     // the first it did not
    [InlineData(1000)]
    public void SanitizeNextChain_SeversADangerousActionAtAnyDepth(int dangerousAt)
    {
        var head = BuildChain(1200, dangerousAt, out var parentOfDangerous);

        InvokeSanitizeNextChain(head, new SanitizeOptions());

        Assert.False(parentOfDangerous.ContainsKey(PdfName.Next),
            $"a JavaScript action at link {dangerousAt} was left in place — the walk stopped short " +
            "of it. Check whether a depth cap has been reintroduced, or whether the chain walk " +
            "recurses through SanitizeDictionary again instead of iterating.");
    }

    /// <summary>
    /// A chain long enough to have overflowed the stack under the recursive walk is handled
    /// iteratively — no cap, no stack growth.
    /// </summary>
    [Fact]
    public void SanitizeNextChain_TenThousandLinks_DoesNotOverflowTheStack()
    {
        var head = BuildChain(10_000, dangerousAt: 9_999, out var parentOfDangerous);

        InvokeSanitizeNextChain(head, new SanitizeOptions());

        // The post-condition doubles as proof the walk really traversed all 10 000 links rather
        // than merely returning early without overflowing.
        Assert.False(parentOfDangerous.ContainsKey(PdfName.Next));
    }

    /// <summary>
    /// Builds a linear <c>/Next</c> chain of <paramref name="length"/> URI actions. When
    /// <paramref name="dangerousAt"/> is non-negative that link is a JavaScript action instead, and
    /// <paramref name="parentOfDangerous"/> receives the link before it — the dictionary whose
    /// <c>/Next</c> the sanitizer must remove if the walk gets that far.
    /// </summary>
    private static PdfDictionary BuildChain(int length, int dangerousAt,
        out PdfDictionary parentOfDangerous)
    {
        var head = new PdfDictionary();
        head.Set(PdfName.S, new PdfName("URI"));

        var current = head;
        parentOfDangerous = head;

        for (int i = 0; i < length; i++)
        {
            var next = new PdfDictionary();
            // URI is NOT stripped by the default options, so the walk keeps going.
            next.Set(PdfName.S, i == dangerousAt ? PdfName.JavaScript : new PdfName("URI"));
            if (i == dangerousAt) parentOfDangerous = current;
            current.Set(PdfName.Next, next);
            current = next;
        }

        return head;
    }

    // ==================================================================
    //  Helpers
    // ==================================================================

    private static void InvokeSanitizeNextChain(PdfDictionary action, SanitizeOptions options)
    {
        // The public SanitizeNextChain(actionDict, options, parser) overload
        // takes a parser; pass null and let the inner helpers handle it
        // (they use ResolveAsDictionary which tolerates a null parser
        // when the input is already a direct dict).
        var method = typeof(PdfSanitizer).GetMethod(
            "SanitizeNextChain",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types: new[]
            {
                typeof(PdfDictionary),
                typeof(SanitizeOptions),
                typeof(Tetitulz.Pdf.Core.Parsing.PdfParser),
            },
            modifiers: null);

        Assert.NotNull(method);
        method!.Invoke(null, new object[] { action, options, null! });
    }
}
