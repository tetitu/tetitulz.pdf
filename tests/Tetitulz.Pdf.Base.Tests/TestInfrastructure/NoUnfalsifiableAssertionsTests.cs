// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// Enforces the third banned test pattern from CLAUDE.md: <b>no assertion that cannot fail.</b>
///
/// <para>A test named for a behaviour must assert that behaviour. The archetype is a render test
/// that does <c>byte[] png = Render(pdf);</c> and then checks only that the array is non-null and
/// non-empty — a blank page encodes to a perfectly valid non-empty PNG, so the test passes with
/// the feature under test deleted outright.</para>
///
/// <para><b>This is not a hypothetical.</b> Two defects closed in the 2026-07-31 rendering round —
/// optional-content <c>/D</c> resolution and <c>BDC /OC</c> suppression, both completely
/// non-functional — sat behind six dedicated green tests for as long as they existed, because
/// every one of them asserted only that a PNG came back.</para>
///
/// <para><b>There is no allow-list, and that is deliberate.</b> The guard shipped on 2026-08-02
/// with a <c>KnownOffenders</c> set of <b>140</b> entries so it could land green over the debt that
/// already existed while making a <i>new</i> offender impossible, plus a second <c>[Fact]</c> that
/// failed when an entry no longer named a real offender — so the set could only ever shrink. It
/// reached <b>zero</b> on 2026-08-04 across 50 files, and both were then deleted.</para>
///
/// <para>Do not reintroduce one. An empty exemption set is an invitation; no exemption set at all
/// is a rule. If this guard flags your test, the fix is in the test. Along the way the burn-down
/// turned up <b>nine real defects</b> that were already "covered" by green tests — the sweep and
/// its findings are recorded in the CHANGELOG.
/// (This comment said "eight" until 2026-08-06; GAPS.md §9 enumerates nine and notes that
/// miscounting the <c>PdfEncryptor</c> <c>/P</c> defect is exactly what produced the lower number.)</para>
///
/// <para><b>What is deliberately exempt.</b> A "does not throw / does not hang on hostile input"
/// test is <i>not</i> an instance of this pattern: there the non-throw <b>is</b> the behaviour
/// under test, and CLAUDE.md asks for exactly those on every DoS / recursion guard. The exemption
/// is keyed on the input being <b>hostile</b> (see <see cref="HostileInputNameTokens"/>), not on
/// the name merely containing "DoesNotThrow". A name like
/// <c>TilingPattern_PaintType2_RendersWithoutException</c> is a behaviour claim wearing a guard's
/// clothes: a tiling pattern that draws nothing throws no exception either.</para>
/// </summary>
public sealed class NoUnfalsifiableAssertionsTests
{
    /// <summary>
    /// Assertions that hold equally well when the feature under test is deleted. A body whose
    /// assertions are drawn entirely from this set has asserted nothing.
    /// </summary>
    private static readonly Regex WeakAssertionCall =
        new(@"Assert\.(NotNull|NotEmpty)\b", RegexOptions.Compiled);

    /// <summary>
    /// The same emptiness checks written through <c>Assert.True</c>, which would otherwise look
    /// strong. <c>Assert.True(png.Length > 0)</c> is <c>Assert.NotEmpty(png)</c> in disguise.
    /// </summary>
    private static readonly Regex WeakTrueAssertion =
        new(@"Assert\.True\s*\(\s*[\w\.\[\]\(\)]+\s*(?:!=\s*null|\.Length\s*>\s*0|\.Count\s*>\s*0|\.Any\(\s*\))",
            RegexOptions.Compiled);

    /// <summary>
    /// A <b>tautology</b> — an assertion whose expression cannot evaluate to anything but pass.
    ///
    /// <para>This is banned pattern 3 in its purest form, and it is worse than writing no
    /// assertion at all, because it looks like one. The guard shipped without it and three sites
    /// survived: <c>Assert.True(outline.Path.PointCount &gt; 0 || true, "…")</c>, where the
    /// <c>|| true</c> makes the real check ornamental, and two bare <c>Assert.True(true)</c> in
    /// DoS-guard tests whose message said "if we got here, the guard worked".</para>
    ///
    /// <para>"If we got here" is a real claim — CLAUDE.md explicitly allows a does-not-hang test —
    /// but the way to make it is to assert the guard's own <i>evidence</i>: a typed exception, a
    /// reported reason, a bounded result. Writing <c>Assert.True(true)</c> instead means the test
    /// still passes when the guard is deleted, provided the deletion happens not to hang.</para>
    /// </summary>
    private static readonly Regex TautologicalAssertion =
        new(@"Assert\.True\s*\(\s*true\s*[,)]"
          + @"|Assert\.False\s*\(\s*false\s*[,)]"
          + @"|Assert\.True\s*\([^;]*\|\|\s*true\s*[,)]"
          + @"|Assert\.False\s*\([^;]*&&\s*false\s*[,)]",
            RegexOptions.Compiled);

    /// <summary>
    /// A direct xUnit assertion.
    ///
    /// <para>The leading boundary is load-bearing. Written as plain <c>Assert\.\w+</c> this also
    /// matched <c>ContentStreamAssert.Contains(...)</c> and any other helper whose type name merely
    /// <i>ends</i> in "Assert" — so a body whose only real content was
    /// <c>Assert.NotNull(ContentStreamAssert.FirstOperandsOf(...))</c> counted as strongly
    /// asserting twice over. Two tests in the PostScript suite hid behind exactly that.</para>
    /// </summary>
    private static readonly Regex AnyAssertion =
        new(@"(?<![\w.])Assert\.\w+", RegexOptions.Compiled);

    // A rule rejecting `Assert.Null(x.LastError)` was tried here and removed: a test named
    // "LastError_IsClearedByASubsequentSuccessfulScan" makes the slot non-null first and then
    // asserts it was cleared, which is perfectly falsifiable. The genuinely bad case — asserting
    // only "no error" for a test named after a computation — is a mismatch between a name and an
    // assertion, and no regex can see that. It was fixed in the one test that had it instead.

    /// <summary>
    /// A local holding the result of <c>Record.Exception</c>. Captured so the
    /// <c>Assert.Null(that local)</c> below can be recognised as the "nothing was thrown" check it
    /// is, rather than as a strong assertion about a value.
    /// </summary>
    private static readonly Regex RecordExceptionLocal =
        new(@"\b(?:var|Exception)\s+(\w+)\s*=\s*(?:await\s+)?Record\.Exception(?:Async)?\b",
            RegexOptions.Compiled);

    /// <summary>The same check written without a local.</summary>
    private static readonly Regex InlineRecordExceptionAssertion =
        new(@"Assert\.Null\s*\(\s*(?:await\s+)?Record\.Exception(?:Async)?\b", RegexOptions.Compiled);

    /// <summary>Any <c>Assert.Null(someLocal)</c>, resolved against <see cref="RecordExceptionLocal"/>.</summary>
    private static readonly Regex AssertNullOfLocal =
        new(@"Assert\.Null\s*\(\s*(\w+)\s*\)", RegexOptions.Compiled);

    /// <summary>
    /// Counts the assertions that say only <b>"this did not throw"</b>.
    ///
    /// <para>This is the hole the guard shipped with, and it covered almost exactly the features
    /// that had no other coverage. <c>Assert.Null</c> is not in
    /// <see cref="WeakAssertionCall"/> — correctly, since asserting a value is null is usually a
    /// real claim — so wrapping the banned pattern in <c>Record.Exception</c> laundered it:</para>
    ///
    /// <code>
    /// var ex = Record.Exception(() => {
    ///     byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72);
    ///     Assert.NotNull(png); Assert.NotEmpty(png);      // weak, correctly discounted
    /// });
    /// Assert.Null(ex);                                    // counted as STRONG — the hole
    /// </code>
    ///
    /// <para>That body is banned pattern 3 with a <c>try</c>/<c>catch</c> around it: it passes with
    /// the renderer stubbed to produce a blank page. 217 such sites existed across 41 files when
    /// this rule was added.</para>
    ///
    /// <para>The rule is deliberately keyed on <c>Record.Exception</c> and nothing else, so the
    /// carve-out recorded above it still holds: an <c>Assert.Null</c> on any other expression stays
    /// strong. A genuine "hostile input does not throw" test is still exempt by
    /// <see cref="IsHostileInputGuard"/> — as it must be, since there the non-throw <i>is</i> the
    /// behaviour. What is no longer possible is claiming a <i>feature</i> works because rendering it
    /// raised no exception.</para>
    /// </summary>
    private static int NoThrowOnlyAssertionCount(string body)
    {
        int count = InlineRecordExceptionAssertion.Matches(body).Count;

        var locals = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in RecordExceptionLocal.Matches(body))
            locals.Add(m.Groups[1].Value);

        if (locals.Count == 0) return count;

        foreach (Match m in AssertNullOfLocal.Matches(body))
            if (locals.Contains(m.Groups[1].Value)) count++;

        return count;
    }

    /// <summary>
    /// A call to a helper that does the asserting on the test's behalf. Bodies with no direct
    /// <c>Assert.*</c> but a call to one of these are treated as delegating, not as asserting
    /// nothing — several suites here are written that way
    /// (e.g. <c>AssertPixelIs(...)</c>, <c>VerifyRoundTrip(...)</c>).
    /// </summary>
    /// <remarks>
    /// The second alternative recognises a helper type whose name <i>ends</i> in "Assert"
    /// (<c>ContentStreamAssert.ContainsSequence(...)</c>), but only when the call is the whole
    /// statement. That distinction is the point: such a call standing alone IS the assertion, while
    /// the same type appearing as an ARGUMENT — <c>Assert.NotNull(ContentStreamAssert.First(...))</c>
    /// — is a projection whose result something else is weakly asserting on, which is exactly how a
    /// test hid behind this helper. Requiring at least one word character before "Assert" keeps a
    /// bare <c>Assert.NotNull(x);</c> out of this set, since that must stay weak.
    /// </remarks>
    private static readonly Regex DelegatedAssertionCall =
        new(@"\b(?:Assert|Verify|Expect|Check|Should)\w*\s*\(" +
            @"|(?m)^\s*(?:=>\s*)?\w+Assert\.\w+\s*\(", RegexOptions.Compiled);

    /// <summary>
    /// Name fragments that mark the input as <b>hostile</b>, which is what earns the guard
    /// exemption. Surviving malformed / adversarial / unbounded input is a real behaviour and the
    /// non-throw is the only observable it has.
    ///
    /// <para>Note what is <b>absent</b>: "DoesNotThrow", "NoException" and "RendersSuccessfully"
    /// on their own. Those describe the assertion, not the input, and are how a behaviour test
    /// gets written weakly in the first place.</para>
    /// </summary>
    /// <remarks>
    /// <para>The second row was added on 2026-08-06 with the <c>Record.Exception</c> rule below,
    /// which flagged a handful of tests that <i>are</i> hostile-input guards and simply did not use
    /// a word from the first row — an empty operand stack, a negative count, an oversized key
    /// length, random bytes, absurd declared dimensions. Extending this set is legitimate when the
    /// token genuinely names a hostile <b>input</b>; it is the wrong fix when the name describes the
    /// assertion ("DoesNotThrow") or claims a feature works.</para>
    ///
    /// <para><b>"Nesting" and "Nested" were REMOVED on 2026-08-06.</b> They describe a document's
    /// <i>structure</i>, not a hostile input — nesting two <c>BDC /OC</c> blocks is precisely what
    /// that feature is for — and they were exempting
    /// <c>NestedBdcBlocks_OuterHidden_BothSuppressed</c>, a behaviour claim whose body was the
    /// laundered <c>Record.Exception</c> shape. Every genuine DoS guard whose name contains
    /// "Nested" also carries a real token ("Bomb", "Overflow", "Depth", "Recursion", "Budget",
    /// "Refused", "DoesNotHang", "Adversarial"), so removing these two cost no legitimate
    /// exemption. A token that names the *shape of the input* rather than its *hostility* is
    /// always suspect.</para>
    /// </remarks>
    private static readonly string[] HostileInputNameTokens =
    [
        "Malformed", "Adversarial", "Hostile", "Corrupt", "Truncated", "Invalid", "Garbage",
        "Bomb", "Cycle", "Cyclic", "Recursion", "Recursive",
        "Depth", "Budget", "Ceiling", "Limit", "Cap", "Overflow", "StackOverflow", "Infinite",
        "DoesNotHang", "Terminates", "Bounded", "Guard", "Refuse", "Refused", "Refuses",
        "Reject", "Rejected", "Rejects", "FailsClosed",

        "Empty", "Negative", "Oversized", "Huge", "Absurd", "RandomBytes", "Unbounded",
    ];

    private static bool IsHostileInputGuard(string testName) =>
        HostileInputNameTokens.Any(token => testName.Contains(token, StringComparison.Ordinal));

    /// <summary>
    /// True when nothing in the body can distinguish working code from broken code.
    /// </summary>
    private static bool CannotFail(string body)
    {
        var assertions = AnyAssertion.Matches(body);

        if (assertions.Count == 0)
            return !DelegatedAssertionCall.IsMatch(body);

        int strong = assertions.Count(m => !WeakAssertionCall.IsMatch(m.Value))
                   - WeakTrueAssertion.Matches(body).Count
                   - TautologicalAssertion.Matches(body).Count
                   - NoThrowOnlyAssertionCount(body);

        // A body whose direct assertions are all weak can still be saved by a standalone helper
        // call that does the real asserting — the same rule the no-direct-assertions branch above
        // applies. Without this, tightening AnyAssertion to exclude `<X>Assert.` (so a helper on an
        // assert-named type stops counting as a direct xUnit assertion) would flag two dozen tests
        // that genuinely delegate.
        return strong <= 0 && !DelegatedAssertionCall.IsMatch(body);
    }

    /// <summary>
    /// A method is an offender when nothing in it can distinguish working code from broken code.
    ///
    /// <para><b>A tautology is an offender whatever the method is called.</b> The hostile-input
    /// exemption exists because "this does not hang on adversarial input" is a real claim that a
    /// non-throw genuinely evidences — but <c>Assert.True(true)</c> evidences nothing, and the
    /// exemption was letting it through on any name containing <c>Infinite</c> or
    /// <c>Recursion</c>. Two DoS-guard tests in the sanitizer suite sat behind exactly that,
    /// asserting a literal <c>true</c> under a comment reading "if we got here, the guard worked".
    /// If completion really is the whole claim, write no assertion and say so — the branch above
    /// permits that for an exempt name. Do not write one that cannot fail.</para>
    /// </summary>
    private static List<TestMethodSource> FindOffenders() =>
        TestSourceScanner.EnumerateTestMethods()
            .Where(m => TautologicalAssertion.IsMatch(m.Body)
                     || (!IsHostileInputGuard(m.Name) && CannotFail(m.Body)))
            .ToList();

    /// <summary>
    /// No test may assert only that something came back. Assert on <b>decoded pixels</b> (colour
    /// at a point, ink inside a rect and not outside it, a difference against a baseline render)
    /// or on the parsed object the code actually produced.
    /// </summary>
    [RepoSourceFact]
    public void NoTestAssertsOnlyThatSomethingWasReturned()
    {
        var offenders = FindOffenders()
            .OrderBy(m => m.RelativePath, StringComparer.Ordinal)
            .ThenBy(m => m.Name, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} test(s) assert only that a call returned non-null / non-empty output, " +
            "so they pass with the feature under test deleted. Assert on decoded pixels or on the " +
            "parsed object the code produced. If the non-throw genuinely IS the behaviour, the input " +
            "must be hostile and the name must say so.\n\n" +
            "There is no allow-list to add these to — see this class's summary for why.\n\n" +
            string.Join("\n", offenders.Select(m => $"  {m}")));
    }
}
