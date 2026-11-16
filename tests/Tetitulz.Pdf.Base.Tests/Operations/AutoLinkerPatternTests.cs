// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Tetitulz.Pdf.Base.Operations;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfAutoLinker"/>'s URL pattern costs time in proportion to the text it is given, and
/// an e-mail address keeps RFC 5321's lengths.
/// </summary>
/// <remarks>
/// <para><b>The address alternative was unbounded on both sides of the <c>@</c>.</b> A local part of
/// <c>[A-Za-z0-9._%+\-]+</c> starting at every position of a run, each scanning to the end of the
/// run before failing, is quadratic in the run's length. Measured on .NET 10, warmed, with a run of
/// letters ending in <c>@</c>: 2.2 s at 250 000 characters, 9.2 s at 500 000, 30 s at a million —
/// and so about eight minutes at four million, which is a few kilobytes of compressed content
/// stream, from a public entry point that takes untrusted documents. The bounded pattern takes
/// 86 ms at four million. (A run with no <c>@</c> at all stayed linear, because .NET 10's regex
/// engine optimises that case, which is why the fixture ends in one.)</para>
/// <para>The local part is now <c>{1,64}</c> and may not start inside a longer run of local-part
/// characters, the domain before its top-level label is <c>{1,253}</c> and the top-level label
/// <c>{2,63}</c>, not followed by another letter: RFC 5321 §4.5.3.1.1–2 and RFC 1035 §2.3.4. Each
/// attempt is bounded, and an attempt starts only where a local part can.</para>
/// </remarks>
public class AutoLinkerPatternTests
{
    /// <summary>
    /// A four-million-character run ending in <c>@</c> is matched in bounded time and yields
    /// nothing.
    /// </summary>
    /// <remarks>
    /// <para>The guard's evidence is that the matcher returns at all: it runs on its own thread with
    /// a generous join, and the unbounded pattern does not come back within it. No clock decides the
    /// verdict.</para>
    /// <para><b>The first version used a million characters and was not potent.</b> The unbounded
    /// pattern finishes a million in about 30 seconds, inside the join, so the mutation restoring it
    /// was killed only by the length tests below. Four million is about eight minutes for it and
    /// 86 ms for the bounded one, so the join separates them by a factor of five either way.</para>
    /// </remarks>
    [Fact]
    public void AnAdversarialRunEndingInAnAtSignIsMatchedInBoundedTime()
    {
        string run = new string('a', 4_000_000) + "@";
        int count = -1;
        Exception thrown = null;

        // The pattern's own shape, without its match timeout: what is asserted is that the pattern
        // is bounded, which a timeout would hide — and the timeout, firing under a loaded machine,
        // threw on this thread, where an unhandled exception ends the test host.
        var bare = new Regex(PdfAutoLinker.UrlPattern.ToString(), PdfAutoLinker.UrlPattern.Options);

        // A background thread, so a pattern that does not return cannot hold the test host open
        // after the join has failed the test.
        var worker = new Thread(() =>
        {
            try { count = bare.Matches(run).Count; }
            catch (Exception ex) { thrown = ex; }
        }, 16 * 1024 * 1024)
        {
            IsBackground = true,
        };
        worker.Start();

        Assert.True(worker.Join(TimeSpan.FromSeconds(60)), "the URL pattern did not finish a 4M-character run");
        Assert.Null(thrown);
        Assert.Equal(0, count);
    }

    /// <summary>
    /// The pattern carries a finite match timeout, as every pattern over a file's text does; its
    /// linearity has no answer a test can see, the cap does.
    /// </summary>
    [Fact]
    public void TheUrlPatternCarriesAMatchTimeout()
    {
        Assert.NotEqual(Regex.InfiniteMatchTimeout, PdfAutoLinker.UrlPattern.MatchTimeout);
        Assert.True(PdfAutoLinker.UrlPattern.MatchTimeout <= TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// A match that exceeds the timeout ends the run's search: the matches before it are kept and the
    /// run is recorded, rather than the timeout escaping the extraction. Driven with a pattern that
    /// does backtrack, since the URL pattern's own bound leaves nothing to time out.
    /// </summary>
    [Fact]
    public void AMatchThatTimesOut_KeepsTheEarlierMatches_AndRecordsTheRun()
    {
        var backtracking = new Regex("(a+)+b", RegexOptions.None, TimeSpan.FromMilliseconds(20));
        string run = "ab " + new string('a', 40) + "!";
        var unsearched = new System.Collections.Generic.List<string>();

        var found = PdfAutoLinker.MatchesWithin(backtracking, run, 2, unsearched).Select(m => m.Value).ToArray();

        Assert.Equal(new[] { "ab" }, found);
        Assert.Equal($"a run of {run.Length} characters on page 3", Assert.Single(unsearched));

        // A run whose first match already exceeds it yields nothing and is recorded the same way.
        string hopeless = new string('a', 40) + "!";
        Assert.Empty(PdfAutoLinker.MatchesWithin(backtracking, hopeless, 0, unsearched));
        Assert.Equal($"a run of {hopeless.Length} characters on page 1", unsearched[1]);
    }

    /// <summary>
    /// <see cref="PdfAutoLinker.LastUnsearchedTextError"/> starts clean on every search: set it as a
    /// timed-out search would — the URL pattern's own bound leaves no document that times it out —
    /// and a search of an ordinary document clears it.
    /// </summary>
    [Fact]
    public void FindLinks_ClearsTheUnsearchedTextSlot()
    {
        var slot = typeof(PdfAutoLinker).GetField("_lastUnsearchedTextError",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(slot);
        slot!.SetValue(null, "left over");
        Assert.Equal("left over", PdfAutoLinker.LastUnsearchedTextError);

        PdfAutoLinker.FindLinks(new System.IO.MemoryStream(Tetitulz.Pdf.TestHelpers.MinimalPdfBuilder.CreateMinimalPdf("no links here")));

        Assert.Null(PdfAutoLinker.LastUnsearchedTextError);
    }

    /// <summary>The control: a run searched to its end records nothing, and yields what Matches yields.</summary>
    [Fact]
    public void ARunSearchedToItsEnd_RecordsNothing()
    {
        string run = "mail someone@example.com, then www.example.com and https://example.com/x.";
        var unsearched = new System.Collections.Generic.List<string>();

        var found = PdfAutoLinker.MatchesWithin(PdfAutoLinker.UrlPattern, run, 0, unsearched).Select(m => m.Value).ToArray();

        Assert.Equal(PdfAutoLinker.UrlPattern.Matches(run).Cast<Match>().Select(m => m.Value).ToArray(), found);
        Assert.Equal(3, found.Length);
        Assert.Empty(unsearched);
    }

    /// <summary>
    /// The potency control: the same pattern still finds an ordinary address, and a URL, after such
    /// a run.
    /// </summary>
    [Fact]
    public void AnOrdinaryAddressAndUrlAreStillFoundAfterALongRun()
    {
        string text = new string('a', 100_000) + " write to someone@example.com or see https://example.com/x";

        var found = PdfAutoLinker.UrlPattern.Matches(text).Cast<Match>().Select(m => m.Value).ToArray();

        Assert.Equal(new[] { "someone@example.com", "https://example.com/x" }, found);
    }

    /// <summary>
    /// A 64-character local part is an address and a 65-character one is not, rather than its last
    /// 64 characters being linked as one.
    /// </summary>
    [Fact]
    public void TheLocalPartIsAtMostSixtyFourCharacters()
    {
        string sixtyFour = new string('b', 64) + "@example.com";
        string sixtyFive = new string('b', 65) + "@example.com";

        Assert.Equal(sixtyFour, PdfAutoLinker.UrlPattern.Match(sixtyFour).Value);
        Assert.False(PdfAutoLinker.UrlPattern.Match(sixtyFive).Success);
    }

    /// <summary>
    /// The domain before its top-level label is at most 253 characters: 253 is an address, 254 is
    /// not.
    /// </summary>
    /// <remarks>
    /// The bound is the DoS guard's, taken from RFC 5321 §4.5.3.1.2's 253-character textual domain.
    /// It is not a full length check, which would need the top-level label counted too; a
    /// conservative matcher can afford to accept a slightly long name.
    /// </remarks>
    [Fact]
    public void TheDomainBeforeItsTopLevelLabelIsAtMostTwoHundredFiftyThreeCharacters()
    {
        string fits = "a@" + new string('x', 253) + ".com";
        string over = "a@" + new string('x', 254) + ".com";

        Assert.Equal(fits, PdfAutoLinker.UrlPattern.Match(fits).Value);
        Assert.False(PdfAutoLinker.UrlPattern.Match(over).Success);
    }

    /// <summary>
    /// A top-level label of 63 letters is matched whole, and one of 64 is not an address, rather
    /// than being linked with its last letter cut off (RFC 1035 §2.3.4: a label is at most 63).
    /// </summary>
    [Fact]
    public void TheTopLevelLabelIsAtMostSixtyThreeLettersAndIsNeverCutShort()
    {
        string fits = "a@example." + new string('c', 63);
        string over = "a@example." + new string('c', 64);

        Assert.Equal(fits, PdfAutoLinker.UrlPattern.Match(fits).Value);
        Assert.False(PdfAutoLinker.UrlPattern.Match(over).Success);
    }
}
