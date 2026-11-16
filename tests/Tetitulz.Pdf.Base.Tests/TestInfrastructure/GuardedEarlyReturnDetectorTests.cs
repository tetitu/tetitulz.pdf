// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// Unit tests for the detector behind
/// <see cref="NoHardcodedPathsTests.NoTestSilentlyReturnsFromAGuardedEarlyReturn"/>.
///
/// <para><b>Why the guard needs its own tests.</b> A repo-wide guard asserts that the tree is
/// clean, so on a clean tree it passes — and it passes just as well with its detector broken, or
/// deleted. That is the same shape as the defect it exists to catch. These feed it sources it
/// cannot see on disk: the hidden bail-out it must flag, and the legitimate <c>return</c>s it must
/// not.</para>
///
/// <para>The fixtures are string literals rather than real methods on purpose. A real method
/// written in the banned shape would be flagged by the guard itself and fail the build — which is
/// the correct behaviour, and unusable as a fixture.</para>
/// </summary>
public sealed class GuardedEarlyReturnDetectorTests
{
    private static int Hits(string methodBody) =>
        NoHardcodedPathsTests.FindGuardedEarlyReturns(methodBody).Count;

    // ------------------------------------------------------------------ must flag

    /// <summary>
    /// The exact shape this guard was widened for: the predicate is a helper call, so the sibling
    /// regex — which knows only <c>File.Exists</c> — sees nothing at all. This is verbatim what
    /// <c>BuilderPathTests.BuilderPath_Hebrew_EmbedsCompositeFont</c> opened with.
    /// </summary>
    [Fact]
    public void APredicateCallGuardingABareReturn_IsFlagged()
    {
        Assert.Equal(1, Hits(@"
        {
            if (!HasHebrewFont()) return;
            byte[] pdf = Render();
            Assert.Contains(""Type0"", Raw(pdf));
        }"));
    }

    /// <summary>
    /// Same bail-out, wearing the inline spelling the old regex already knew — the widened check
    /// has to be a superset, not a replacement that quietly lost a case.
    ///
    /// <para>The fixture is assembled from two literals so the <i>sibling</i> guard does not match
    /// it in this file's text and fail the build: that one scans string literals too, deliberately
    /// — "a banned pattern inside a literal is still a banned pattern".</para>
    /// </summary>
    [Fact]
    public void AnInlineFileExistsGuard_IsFlagged()
    {
        string body = @"
        {
            if (!System.IO.File." + @"Exists(path)) return;
            Assert.NotEmpty(Read(path));
        }";

        Assert.Equal(1, Hits(body));
    }

    /// <summary>A brace block around the bail-out changes nothing about what gets reported.</summary>
    [Fact]
    public void ABracedBailOut_IsFlagged()
    {
        Assert.Equal(1, Hits(@"
        {
            if (!Probe.IsAvailable(""David""))
            {
                return;
            }
            Assert.True(Render().Length > 0);
        }"));
    }

    /// <summary>
    /// A bail-out nested one plain block deep still ends the test. Only lambdas and local functions
    /// sever the walk; <c>foreach</c>, <c>using</c> and <c>try</c> do not.
    /// </summary>
    [Fact]
    public void ABailOutInsideAForeachBlock_IsFlagged()
    {
        Assert.Equal(1, Hits(@"
        {
            foreach (var name in Families)
            {
                if (!Installed(name)) return;
            }
            Assert.True(Render().Length > 0);
        }"));
    }

    // ------------------------------------------------------------------ must not flag

    /// <summary>
    /// The thread-filtered diagnostics handler this suite is full of. Its <c>return</c> ends a
    /// callback invoked on some other thread; the test itself runs on to its assertions. Flagging
    /// these would make the guard unusable, so this is the case that decides whether lambda
    /// severing works at all — and the condition carries a call on purpose, or the test would pass
    /// with severing deleted, for the unrelated reason that the condition asks nothing.
    /// </summary>
    [Fact]
    public void AReturnInsideAHandlerLambda_IsNotFlagged()
    {
        Assert.Equal(0, Hits(@"
        {
            RenderDiagnostics.OnEvent += (kind, detail, ex) =>
            {
                if (Volatile.Read(ref workerThreadId) != Environment.CurrentManagedThreadId) return;
                seen.Add(kind);
            };
            Assert.Contains(RenderDiagnosticKind.IccTransformFailed, seen);
        }"));
    }

    /// <summary>
    /// A recursive tree walk declared inside the test. <c>if (!seen.Add(node)) return;</c> is a
    /// cycle guard bottoming out one branch, not a test giving up — and it carries a call, so this
    /// fixture really does exercise local-function severing.
    /// </summary>
    [Fact]
    public void AReturnInsideALocalFunction_IsNotFlagged()
    {
        Assert.Equal(0, Hits(@"
        {
            var seen = new HashSet<PdfObject>();
            void Walk(PdfObject node, int depth)
            {
                if (!seen.Add(node)) return;
                foreach (var kid in Kids(node)) Walk(kid, depth + 1);
            }
            Walk(root, 0);
            Assert.Equal(7, seen.Count);
        }"));
    }

    /// <summary>
    /// A condition over data the test already holds — no call, so no predicate was asked. Flagging
    /// these would sweep in a dozen tests whose <c>if</c> is ordinary control flow, and a guard
    /// that noisy gets relaxed back out within a week.
    /// </summary>
    [Fact]
    public void AConditionWithNoCallInIt_IsNotFlagged()
    {
        Assert.Equal(0, Hits(@"
        {
            if (tables.Count == 0) return;
            Assert.Equal(3, tables[0].Rows.Count);
        }"));
    }

    /// <summary>
    /// The rule is <c>return;</c>, not <c>return</c>. Belt and braces: a value-carrying bail-out
    /// cannot occur in a void test method, and inside a lambda or local function the walk has
    /// already severed — so this fixture is deliberately not compilable as a test body. It pins the
    /// rule where it is cheapest to state, which is the detector.
    /// </summary>
    [Fact]
    public void AReturnCarryingAValue_IsNotFlagged()
    {
        Assert.Equal(0, Hits(@"
        {
            if (IsSmall(n)) return true;
            Assert.True(n < 10);
        }"));
    }

    /// <summary>
    /// A brace, a parenthesis and the word <c>return</c> inside a content-stream fixture are data.
    /// The literal skip is what stops the walk being truncated at the wrong place — the trap
    /// <c>TestSourceScanner</c> records against its own copy of it.
    /// </summary>
    [Fact]
    public void ABraceInsideAStringLiteral_DoesNotDerailTheWalk()
    {
        Assert.Equal(1, Hits(@"
        {
            string ps = ""{ 1 2 add } if (Missing()) return; }"";
            if (!HasFixture(ps)) return;
            Assert.NotEmpty(Run(ps));
        }"));
    }
}
