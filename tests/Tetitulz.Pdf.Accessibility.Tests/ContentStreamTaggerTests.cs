// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// Byte-level tests for <see cref="ContentStreamTagger"/> — the code that splices
/// <c>BDC</c>/<c>EMC</c> into somebody else's content stream.
///
/// <para>
/// This is the highest-risk code in the accessibility feature: a splice that lands one byte inside
/// a literal string or an inline-image payload produces a file that still opens, still passes a
/// structural audit, and renders as garbage. So the tests here do not merely check that "something
/// was tagged" — they pin the exact emitted syntax, the exact operator sequence of the result, and
/// prove that the output is the input with <em>nothing but</em> the reported insertions added.
/// </para>
///
/// <para>Everything is self-contained: content streams and PDFs are built in-memory per test.</para>
/// </summary>
public class ContentStreamTaggerTests
{
    // =====================================================================
    //  Helpers
    // =====================================================================

    private const float PageHeightPt = 792f;

    /// <summary>Latin-1 round-trips bytes 0x00-0xFF 1:1, so binary payloads can be written inline.</summary>
    private static byte[] Ascii(string s) => Encoding.Latin1.GetBytes(s);

    private static string Str(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static string Str(byte[] bytes, int start, int length)
        => Encoding.Latin1.GetString(bytes, start, length);

    private static byte[] Cat(params byte[][] parts)
    {
        int total = 0;
        foreach (byte[] p in parts) total += p.Length;
        var result = new byte[total];
        int offset = 0;
        foreach (byte[] p in parts)
        {
            Buffer.BlockCopy(p, 0, result, offset, p.Length);
            offset += p.Length;
        }
        return result;
    }

    private static PageTagOptions NewOptions(float pageHeight = PageHeightPt, int firstMcid = 0)
        => new PageTagOptions { PageHeight = pageHeight, FirstMcid = firstMcid };

    /// <summary>The operator sequence of a content stream, as seen by an independent re-scan.</summary>
    private static string[] Ops(byte[] content)
    {
        IReadOnlyList<ContentToken> tokens = ContentStreamScanner.Scan(content);
        var ops = new string[tokens.Count];
        for (int i = 0; i < tokens.Count; i++) ops[i] = tokens[i].Operator;
        return ops;
    }

    private static int Count(string haystack, string needle)
    {
        int n = 0, at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { n++; at += needle.Length; }
        return n;
    }

    private static int IndexOfBytes(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] == needle[j]) continue;
                match = false;
                break;
            }
            if (match) return i;
        }
        return -1;
    }

    /// <summary>
    /// The exact ASCII the tagger must emit to open a run, derived independently from the run's
    /// reported metadata (ISO 32000-1 14.7.4.2 for content, Table 330 for artifacts).
    /// </summary>
    private static string OpenSequence(TaggedRun run)
        => run.IsArtifact
            ? "\n/Artifact <</Type /" + run.ArtifactType
              + (string.IsNullOrEmpty(run.ArtifactSubtype) ? string.Empty : " /Subtype /" + run.ArtifactSubtype)
              + ">> BDC\n"
            : "\n/" + run.Role + " <</MCID " + run.Mcid.ToString(CultureInfo.InvariantCulture) + ">> BDC\n";

    private const string CloseSequence = "\nEMC\n";

    /// <summary>
    /// Rebuilds what the spliced stream <em>must</em> be: the original bytes with the open sequence
    /// inserted at each run's <see cref="TaggedRun.TokenStart"/> and the close sequence at its
    /// <see cref="TaggedRun.TokenEnd"/>, and nothing else changed anywhere.
    ///
    /// <para>
    /// Comparing against this is the strongest available statement of "splice, never re-serialise".
    /// It is only meaningful together with an assertion that the reported offsets actually delimit
    /// the intended operations in the original — every caller below does both.
    /// </para>
    /// </summary>
    private static byte[] Reconstruct(byte[] original, IReadOnlyList<TaggedRun> runs)
    {
        var insertions = new List<(int Offset, int Kind, int Sequence, byte[] Bytes)>();
        int sequence = 0;
        foreach (TaggedRun run in runs)
        {
            insertions.Add((run.TokenStart, 1, sequence++, Ascii(OpenSequence(run))));
            insertions.Add((run.TokenEnd, 0, sequence++, Ascii(CloseSequence)));
        }

        // Same total order the tagger uses: offset, then close-before-open, then creation order.
        insertions.Sort((x, y) =>
        {
            if (x.Offset != y.Offset) return x.Offset.CompareTo(y.Offset);
            if (x.Kind != y.Kind) return x.Kind.CompareTo(y.Kind);
            return x.Sequence.CompareTo(y.Sequence);
        });

        var output = new List<byte>(original.Length + 128);
        int source = 0;
        foreach (var insertion in insertions)
        {
            while (source < insertion.Offset) output.Add(original[source++]);
            output.AddRange(insertion.Bytes);
        }
        while (source < original.Length) output.Add(original[source++]);
        return output.ToArray();
    }

    /// <summary>
    /// Proves the result is a pure insertion: the reconstruction matches byte for byte, the
    /// re-scan is clean, and BT/ET and BDC/EMC are balanced.
    /// </summary>
    private static void AssertPureInsertion(byte[] original, PageTagResult result)
    {
        Assert.True(result.Changed, "expected a successful splice, got: " + result.SkipReason);
        Assert.Equal(Reconstruct(original, result.Runs), result.NewContent);

        ContentScanResult rescan = ContentStreamScanner.ScanDetailed(result.NewContent);
        Assert.True(rescan.IsSafe, "spliced stream no longer scans cleanly: " + rescan.Error);

        int bt = 0, et = 0, bdc = 0, emc = 0;
        foreach (string op in Ops(result.NewContent))
        {
            switch (op)
            {
                case "BT": bt++; break;
                case "ET": et++; break;
                case "BDC":
                case "BMC": bdc++; break;
                case "EMC": emc++; break;
            }
        }
        Assert.Equal(bt, et);
        Assert.Equal(bdc, emc);
        Assert.Equal(result.Runs.Count, bdc);
    }

    private static PageTagResult Tag(string content, PageTagOptions options, out byte[] original)
    {
        original = Ascii(content);
        return ContentStreamTagger.TagPage(original, options);
    }

    // =====================================================================
    //  Degenerate input
    // =====================================================================

    [Fact]
    public void TagPage_NullContent_IsSkippedWithAReason()
    {
        PageTagResult result = ContentStreamTagger.TagPage(null, NewOptions());

        Assert.False(result.Changed);
        Assert.NotNull(result.SkipReason);
        Assert.NotNull(result.NewContent);
        Assert.Empty(result.NewContent);
        Assert.Empty(result.Runs);
    }

    [Fact]
    public void TagPage_ZeroByteContent_IsSkippedWithAReason()
    {
        var empty = new byte[0];

        PageTagResult result = ContentStreamTagger.TagPage(empty, NewOptions(firstMcid: 3));

        Assert.False(result.Changed);
        Assert.Contains("no content-stream bytes", result.SkipReason, StringComparison.Ordinal);
        Assert.Same(empty, result.NewContent);
        Assert.Equal(3, result.NextMcid);
    }

    [Theory]
    [InlineData("   \r\n\t  ")]
    [InlineData("% only a comment, nothing drawn\n")]
    public void TagPage_NothingDrawn_IsSkippedAndReturnsTheOriginalArray(string content)
    {
        PageTagResult result = Tag(content, NewOptions(firstMcid: 5), out byte[] original);

        Assert.False(result.Changed);
        Assert.Same(original, result.NewContent);
        Assert.Empty(result.Runs);
        Assert.Equal(5, result.NextMcid);
        Assert.Contains("no text-showing operators", result.SkipReason, StringComparison.Ordinal);
    }

    // =====================================================================
    //  The happy path — exact emitted syntax and exact resulting bytes
    // =====================================================================

    private const string OneParagraph = "BT /F1 10 Tf 72 400 Td (Body line one of the paragraph.) Tj ET\n";

    [Fact]
    public void TagPage_SingleParagraph_EmitsTheExactBdcSyntaxAndAMatchingEmc()
    {
        PageTagResult result = Tag(OneParagraph, NewOptions(), out byte[] original);

        Assert.True(result.Changed, result.SkipReason);
        Assert.Null(result.SkipReason);

        string spliced = Str(result.NewContent);
        Assert.Contains("/P <</MCID 0>> BDC", spliced, StringComparison.Ordinal);
        Assert.Contains("\nEMC\n", spliced, StringComparison.Ordinal);
        Assert.Equal(1, Count(spliced, "BDC"));
        Assert.Equal(1, Count(spliced, "EMC"));

        // Length must be exactly the original plus the two fragments — no re-formatting anywhere.
        Assert.Equal(original.Length + "\n/P <</MCID 0>> BDC\n".Length + CloseSequence.Length,
            result.NewContent.Length);
    }

    [Fact]
    public void TagPage_SingleParagraph_ProducesTheExpectedOperatorSequence()
    {
        PageTagResult result = Tag(OneParagraph, NewOptions(), out _);

        Assert.True(result.Changed, result.SkipReason);
        Assert.Equal(new[] { "BDC", "BT", "Tf", "Td", "Tj", "ET", "EMC" }, Ops(result.NewContent));
    }

    [Fact]
    public void TagPage_SingleParagraph_ReportsOffsetsThatDelimitTheRealTextObject()
    {
        PageTagResult result = Tag(OneParagraph, NewOptions(), out byte[] original);

        TaggedRun run = Assert.Single(result.Runs);
        Assert.Equal("P", run.Role);
        Assert.False(run.IsArtifact);
        Assert.Equal(0, run.Mcid);
        Assert.Equal(1, result.NextMcid);

        // The reported byte range must be the whole BT..ET, verbatim. Without this anchor the
        // reconstruction test below would only prove self-consistency.
        Assert.Equal("BT /F1 10 Tf 72 400 Td (Body line one of the paragraph.) Tj ET",
            Str(original, run.TokenStart, run.TokenEnd - run.TokenStart));
    }

    [Fact]
    public void TagPage_SingleParagraph_OutputIsTheOriginalPlusOnlyTheReportedInsertions()
    {
        PageTagResult result = Tag(OneParagraph, NewOptions(), out byte[] original);

        AssertPureInsertion(original, result);

        // And the drawing operation itself survives byte for byte.
        Assert.Contains("(Body line one of the paragraph.) Tj",
            Str(result.NewContent), StringComparison.Ordinal);
    }

    [Fact]
    public void TagPage_DoesNotMutateTheCallersBuffer()
    {
        byte[] original = Ascii(OneParagraph);
        var snapshot = (byte[])original.Clone();

        PageTagResult result = ContentStreamTagger.TagPage(original, NewOptions());

        Assert.True(result.Changed, result.SkipReason);
        Assert.Equal(snapshot, original);
        Assert.NotSame(original, result.NewContent);
    }

    [Fact]
    public void TagPage_RespectsFirstMcid()
    {
        PageTagResult result = Tag(OneParagraph, NewOptions(firstMcid: 42), out _);

        Assert.True(result.Changed, result.SkipReason);
        Assert.Equal(42, Assert.Single(result.Runs).Mcid);
        Assert.Equal(43, result.NextMcid);
        Assert.Contains("/P <</MCID 42>> BDC", Str(result.NewContent), StringComparison.Ordinal);
    }

    // =====================================================================
    //  Splicing around the constructs that break naive editors
    // =====================================================================

    /// <summary>
    /// A page whose visible text literally reads "BT ET EMC BDC". If the scanner mistook those for
    /// operators the tagger would refuse the page (or splice inside the string); it must instead
    /// tag it normally and leave the string untouched.
    /// </summary>
    [Fact]
    public void TagPage_StringContainingOperatorText_IsTaggedWithoutTouchingTheString()
    {
        const string content = "BT /F1 10 Tf 72 400 Td (BT ET EMC BDC BMC) Tj ET\n";

        PageTagResult result = Tag(content, NewOptions(), out byte[] original);

        AssertPureInsertion(original, result);
        Assert.Contains("(BT ET EMC BDC BMC) Tj", Str(result.NewContent), StringComparison.Ordinal);

        // Exactly one real marked-content sequence, despite five operator-looking words in the text.
        Assert.Equal(new[] { "BDC", "BT", "Tf", "Td", "Tj", "ET", "EMC" }, Ops(result.NewContent));
    }

    [Fact]
    public void TagPage_NestedParensEscapesAndComments_SurviveTheSplice()
    {
        const string content =
            "% page comment mentioning BT ET EMC and an unbalanced (\n" +
            "BT /F1 10 Tf 72 400 Td (a (b (c) d) e \\( \\) \\\\ z) Tj ET\n";

        PageTagResult result = Tag(content, NewOptions(), out byte[] original);

        AssertPureInsertion(original, result);

        string spliced = Str(result.NewContent);
        Assert.Contains("% page comment mentioning BT ET EMC and an unbalanced (", spliced, StringComparison.Ordinal);
        Assert.Contains("(a (b (c) d) e \\( \\) \\\\ z) Tj", spliced, StringComparison.Ordinal);
        Assert.Equal(new[] { "BDC", "BT", "Tf", "Td", "Tj", "ET", "EMC" }, Ops(result.NewContent));
    }

    /// <summary>
    /// An inline image sits between the graphics operators and the text. Its payload is arbitrary
    /// binary and must come out the far side untouched, with no insertion anywhere inside it.
    /// </summary>
    [Fact]
    public void TagPage_InlineImagePayload_IsPreservedByteForByteAndNeverSplicedInto()
    {
        byte[] payload = { 0x00, 0x7F, 0x80, 0xFF, 0x01, 0xFE };
        byte[] head = Ascii("q 100 0 0 100 72 500 cm\nBI /W 2 /H 3 /BPC 8 /CS /G ID ");
        byte[] tail = Ascii("\nEI\nQ\nBT /F1 10 Tf 72 400 Td (Caption text) Tj ET\n");
        byte[] original = Cat(head, payload, tail);

        int imageEnd = head.Length + payload.Length + 3;   // past "\nEI"
        Assert.Equal("EI", Str(original, head.Length + payload.Length + 1, 2));   // fixture self-check

        PageTagResult result = ContentStreamTagger.TagPage(original, NewOptions());

        AssertPureInsertion(original, result);

        // The payload appears once, unaltered, in the output.
        Assert.True(IndexOfBytes(result.NewContent, payload) >= 0,
            "the inline-image payload must survive the splice byte for byte");

        // Every insertion is after the image, never inside it.
        TaggedRun run = Assert.Single(result.Runs);
        Assert.True(run.TokenStart >= imageEnd,
            "a marked-content sequence must never open inside an inline image");

        Assert.Equal(new[] { "q", "cm", "BI", "Q", "BDC", "BT", "Tf", "Td", "Tj", "ET", "EMC" },
            Ops(result.NewContent));
    }

    /// <summary>
    /// The binary payoff of measuring a self-delimiting payload. A filtered inline image with no
    /// declared <c>/L</c> used to make its whole page permanently untaggable: the scanner could only
    /// guess where the samples ended, so the tagger refused with the "payload length could not be
    /// determined" skip reason rather than splice at an offset it could not prove. <c>/A85</c>
    /// carries its own <c>~&gt;</c> end-of-data marker (§7.4.3), so the length is now known and the
    /// page is tagged — with the samples still untouched.
    ///
    /// <para>
    /// The payload is the case the old heuristic provably could not survive: an ASCII85 stream is
    /// printable ASCII by construction, and <c>E</c> and <c>I</c> are both in the base-85 alphabet,
    /// so the <c>" EI "</c> inside it satisfies every condition the heuristic tests.
    /// </para>
    /// </summary>
    [Fact]
    public void TagPage_Ascii85InlineImage_IsTaggedInsteadOfRefusedAsAnUnmeasurablePayload()
    {
        byte[] payload  = Ascii("8;Xa EI 9M~>");
        byte[] head     = Ascii("q 100 0 0 100 72 500 cm\nBI /W 2 /H 2 /BPC 8 /CS /G /F /A85 ID ");
        byte[] tail     = Ascii("\nEI\nQ\nBT /F1 10 Tf 72 400 Td (Caption text) Tj ET\n");
        byte[] original = Cat(head, payload, tail);

        int imageEnd = head.Length + payload.Length + 3;   // past "\nEI"
        Assert.Equal("EI", Str(original, head.Length + payload.Length + 1, 2));   // fixture self-check

        PageTagResult result = ContentStreamTagger.TagPage(original, NewOptions());

        // The decisive pair: no refusal, and a real splice.
        Assert.Null(result.SkipReason);
        AssertPureInsertion(original, result);

        // The samples come out the far side byte for byte, with every insertion after them.
        Assert.True(IndexOfBytes(result.NewContent, payload) >= 0,
            "the inline-image payload must survive the splice byte for byte");
        TaggedRun run = Assert.Single(result.Runs);
        Assert.True(run.TokenStart >= imageEnd,
            "a marked-content sequence must never open inside an inline image");
        Assert.False(run.IsArtifact);

        Assert.Equal(new[] { "q", "cm", "BI", "Q", "BDC", "BT", "Tf", "Td", "Tj", "ET", "EMC" },
            Ops(result.NewContent));
    }

    // =====================================================================
    //  Refusals — the tagger must never "try anyway"
    // =====================================================================

    [Fact]
    public void TagPage_AlreadyMarkedContent_IsRefusedAndReturnsTheOriginalBytesUnchanged()
    {
        const string content =
            "/Span <</MCID 0>> BDC\nBT /F1 10 Tf 72 400 Td (Already tagged) Tj ET\nEMC\n";
        byte[] original = Ascii(content);
        var snapshot = (byte[])original.Clone();

        PageTagResult result = ContentStreamTagger.TagPage(original, NewOptions(firstMcid: 2));

        Assert.False(result.Changed);
        Assert.NotNull(result.SkipReason);
        Assert.Contains("already contains marked content", result.SkipReason, StringComparison.Ordinal);

        Assert.Same(original, result.NewContent);      // the identical array, not a copy
        Assert.Equal(snapshot, result.NewContent);     // and its bytes were not touched
        Assert.Empty(result.Runs);
        Assert.Equal(2, result.NextMcid);
    }

    [Fact]
    public void TagPage_CompatibilitySection_IsRefused()
    {
        const string content = "BX /MyExt 3 myop EX\nBT /F1 10 Tf 72 400 Td (Text) Tj ET\n";

        PageTagResult result = Tag(content, NewOptions(), out byte[] original);

        Assert.False(result.Changed);
        Assert.Contains("BX/EX", result.SkipReason, StringComparison.Ordinal);
        Assert.Same(original, result.NewContent);
    }

    [Fact]
    public void TagPage_UnscannableStream_IsRefused()
    {
        const string content = "BT /F1 10 Tf 72 400 Td (never closed Tj ET\n";

        PageTagResult result = Tag(content, NewOptions(), out byte[] original);

        Assert.False(result.Changed);
        Assert.Contains("could not be scanned safely", result.SkipReason, StringComparison.Ordinal);
        Assert.Same(original, result.NewContent);
    }

    [Fact]
    public void TagPage_BtWithoutEt_IsRefusedRatherThanEmitted()
    {
        const string content = "BT /F1 10 Tf 72 400 Td (Unterminated text object) Tj\n";

        PageTagResult result = Tag(content, NewOptions(), out byte[] original);

        Assert.False(result.Changed);
        Assert.Contains("BT with no ET", result.SkipReason, StringComparison.Ordinal);
        Assert.Same(original, result.NewContent);
        Assert.Empty(result.Runs);
    }

    [Fact]
    public void TagPage_EtWithoutBt_IsRefusedRatherThanEmitted()
    {
        const string content = "BT /F1 10 Tf 72 400 Td (Body) Tj ET ET\n";

        PageTagResult result = Tag(content, NewOptions(), out byte[] original);

        Assert.False(result.Changed);
        Assert.Contains("ET without a matching BT", result.SkipReason, StringComparison.Ordinal);
        Assert.Same(original, result.NewContent);
    }

    [Fact]
    public void TagPage_NestedBt_IsRefusedRatherThanEmitted()
    {
        const string content = "BT BT /F1 10 Tf 72 400 Td (Body) Tj ET ET\n";

        PageTagResult result = Tag(content, NewOptions(), out byte[] original);

        Assert.False(result.Changed);
        Assert.Contains("nests BT", result.SkipReason, StringComparison.Ordinal);
        Assert.Same(original, result.NewContent);
    }

    /// <summary>
    /// Splitting a text object that also pushes/pops the graphics state could leave a lone
    /// <c>q</c> or <c>Q</c> inside a marked-content sequence, which violates the nesting rule of
    /// ISO 32000-1 8.4.1. Refusing is the only safe answer.
    /// </summary>
    [Fact]
    public void TagPage_MixedRolesPlusGraphicsStateOpsInOneTextObject_IsRefused()
    {
        const string content =
            "BT /F1 24 Tf 72 700 Td (Heading) Tj q 1 0 0 1 0 0 cm Q /F1 10 Tf 0 -40 Td (Body) Tj ET\n";

        PageTagResult result = Tag(content, NewOptions(), out byte[] original);

        Assert.False(result.Changed);
        Assert.Contains("graphics state", result.SkipReason, StringComparison.Ordinal);
        Assert.Same(original, result.NewContent);
    }

    [Fact]
    public void TagPage_UnbalancedQInsideATextObject_IsRefused()
    {
        const string content = "BT /F1 10 Tf 72 400 Td (Body) Tj q ET\n";

        PageTagResult result = Tag(content, NewOptions(), out byte[] original);

        Assert.False(result.Changed);
        Assert.Contains("unbalanced q/Q", result.SkipReason, StringComparison.Ordinal);
        Assert.Same(original, result.NewContent);
    }

    /// <summary>The control for the two refusals above: balanced q/Q around one role is fine.</summary>
    [Fact]
    public void TagPage_BalancedQInsideASingleRoleTextObject_IsTagged()
    {
        const string content = "BT /F1 10 Tf 72 400 Td q (Body) Tj Q ET\n";

        PageTagResult result = Tag(content, NewOptions(), out byte[] original);

        AssertPureInsertion(original, result);
        Assert.Equal(new[] { "BDC", "BT", "Tf", "Td", "q", "Tj", "Q", "ET", "EMC" }, Ops(result.NewContent));
    }

    [Fact]
    public void TagPage_ClassifierReturningAnUnwritableRoleName_IsRefused()
    {
        PageTagOptions options = NewOptions();
        options.Classifier = _ => RunClassification.Content("Bad Role/Name");

        PageTagResult result = Tag(OneParagraph, options, out byte[] original);

        Assert.False(result.Changed);
        Assert.Contains("cannot be written as a", result.SkipReason, StringComparison.Ordinal);
        Assert.Same(original, result.NewContent);
    }

    [Fact]
    public void TagPage_ClassifierMarkingNothing_ReportsHonestlyInsteadOfClaimingSuccess()
    {
        PageTagOptions options = NewOptions();
        options.Classifier = _ => RunClassification.Unmarked;

        PageTagResult result = Tag(OneParagraph, options, out byte[] original);

        Assert.False(result.Changed);
        Assert.Contains("No line on the page was classified", result.SkipReason, StringComparison.Ordinal);
        Assert.Same(original, result.NewContent);
        Assert.Empty(result.Runs);
    }

    // =====================================================================
    //  Two roles inside one text object
    // =====================================================================

    private const string TwoRolesOneTextObject =
        "BT /F1 24 Tf 72 700 Td (Big Heading) Tj /F1 10 Tf 0 -40 Td (Small body text) Tj ET\n";

    [Fact]
    public void TagPage_TwoRolesInOneTextObject_ProducesTwoSequencesInsideTheTextObject()
    {
        PageTagResult result = Tag(TwoRolesOneTextObject, NewOptions(), out byte[] original);

        Assert.True(result.Changed, result.SkipReason);
        Assert.Equal(2, result.Runs.Count);
        Assert.Equal("H1", result.Runs[0].Role);
        Assert.Equal("P", result.Runs[1].Role);
        Assert.Equal(0, result.Runs[0].Mcid);
        Assert.Equal(1, result.Runs[1].Mcid);
        Assert.Equal(2, result.NextMcid);

        // Both sequences open and close strictly between BT and ET — never straddling either.
        Assert.Equal("BT", Str(original, 0, 2));               // fixture self-check
        const int btEnd = 2;
        int etStart = Str(original).LastIndexOf("ET", StringComparison.Ordinal);
        Assert.True(etStart > btEnd);
        foreach (TaggedRun run in result.Runs)
        {
            Assert.True(run.TokenStart > btEnd, "a sequence opened before/at BT");
            Assert.True(run.TokenEnd <= etStart, "a sequence closed after ET started");
        }

        // Non-overlapping, in content order.
        Assert.True(result.Runs[0].TokenEnd <= result.Runs[1].TokenStart);
    }

    [Fact]
    public void TagPage_TwoRolesInOneTextObject_WrapsOnlyTheShowOperations()
    {
        PageTagResult result = Tag(TwoRolesOneTextObject, NewOptions(), out byte[] original);

        Assert.Equal(2, result.Runs.Count);
        Assert.Equal("(Big Heading) Tj",
            Str(original, result.Runs[0].TokenStart, result.Runs[0].TokenEnd - result.Runs[0].TokenStart));
        Assert.Equal("(Small body text) Tj",
            Str(original, result.Runs[1].TokenStart, result.Runs[1].TokenEnd - result.Runs[1].TokenStart));

        AssertPureInsertion(original, result);
        Assert.Equal(
            new[] { "BT", "Tf", "Td", "BDC", "Tj", "EMC", "Tf", "Td", "BDC", "Tj", "EMC", "ET" },
            Ops(result.NewContent));
    }

    [Fact]
    public void TagPage_MultiLineParagraphInOneTextObject_CollapsesToASingleMcid()
    {
        const string content =
            "BT /F1 10 Tf 72 400 Td (Line one.) Tj 0 -12 Td (Line two.) Tj 0 -12 Td (Line three.) Tj ET\n";

        PageTagResult result = Tag(content, NewOptions(), out byte[] original);

        AssertPureInsertion(original, result);

        TaggedRun run = Assert.Single(result.Runs);
        Assert.Equal("P", run.Role);
        Assert.Equal(0, run.Mcid);
        Assert.Equal(1, result.NextMcid);
        Assert.Equal("Line one. Line two. Line three.", run.Text);

        // One sequence wrapping the whole text object — three MCIDs for one paragraph would be
        // wrong, and three overlapping sequences would be invalid.
        Assert.Equal(new[] { "BDC", "BT", "Tf", "Td", "Tj", "Td", "Tj", "Td", "Tj", "ET", "EMC" },
            Ops(result.NewContent));
    }

    // =====================================================================
    //  Classification: headings by size, headers/footers as artifacts
    // =====================================================================

    [Fact]
    public void TagPage_FontSizeRatios_ProduceH1H2H3AndP()
    {
        const string content =
            "BT /F1 24 Tf 72 700 Td (Twenty four point) Tj ET\n" +
            "BT /F1 15 Tf 72 650 Td (Fifteen point) Tj ET\n" +
            "BT /F1 12 Tf 72 600 Td (Twelve point) Tj ET\n" +
            "BT /F1 10 Tf 72 550 Td (Ten point body) Tj ET\n";

        PageTagResult result = Tag(content, NewOptions(), out byte[] original);

        AssertPureInsertion(original, result);

        var roles = new List<string>();
        foreach (TaggedRun run in result.Runs) roles.Add(run.Role);
        Assert.Equal(new[] { "H1", "H2", "H3", "P" }, roles);

        string spliced = Str(result.NewContent);
        Assert.Contains("/H1 <</MCID 0>> BDC", spliced, StringComparison.Ordinal);
        Assert.Contains("/H2 <</MCID 1>> BDC", spliced, StringComparison.Ordinal);
        Assert.Contains("/H3 <</MCID 2>> BDC", spliced, StringComparison.Ordinal);
        Assert.Contains("/P <</MCID 3>> BDC", spliced, StringComparison.Ordinal);
    }

    private const string HeaderBodyFooter =
        "BT /F1 9 Tf 72 760 Td (Confidential Header) Tj ET\n" +
        "BT /F1 10 Tf 72 400 Td (Body text.) Tj ET\n" +
        "BT /F1 9 Tf 72 30 Td (Page 1 of 1) Tj ET\n";

    [Fact]
    public void TagPage_HeaderAndFooterBands_BecomeContentLevelArtifactsWithNoMcid()
    {
        PageTagResult result = Tag(HeaderBodyFooter, NewOptions(), out byte[] original);

        AssertPureInsertion(original, result);
        Assert.Equal(3, result.Runs.Count);

        Assert.True(result.Runs[0].IsArtifact);
        Assert.Equal("Pagination", result.Runs[0].ArtifactType);
        Assert.Equal("Header", result.Runs[0].ArtifactSubtype);
        Assert.Null(result.Runs[0].Role);
        Assert.Equal(-1, result.Runs[0].Mcid);

        Assert.False(result.Runs[1].IsArtifact);
        Assert.Equal("P", result.Runs[1].Role);
        Assert.Equal(0, result.Runs[1].Mcid);

        Assert.True(result.Runs[2].IsArtifact);
        Assert.Equal("Footer", result.Runs[2].ArtifactSubtype);
        Assert.Equal(-1, result.Runs[2].Mcid);

        string spliced = Str(result.NewContent);
        Assert.Contains("/Artifact <</Type /Pagination /Subtype /Header>> BDC", spliced, StringComparison.Ordinal);
        Assert.Contains("/Artifact <</Type /Pagination /Subtype /Footer>> BDC", spliced, StringComparison.Ordinal);

        // Artifacts consume no MCID: exactly one /MCID on a page with three marked runs.
        Assert.Equal(1, Count(spliced, "/MCID"));
        Assert.Equal(1, result.NextMcid);
    }

    [Fact]
    public void TagPage_MarkHeadersFootersAsArtifactsDisabled_TagsThemAsContentInstead()
    {
        PageTagOptions options = NewOptions();
        options.MarkHeadersFootersAsArtifacts = false;

        PageTagResult result = Tag(HeaderBodyFooter, options, out byte[] original);

        AssertPureInsertion(original, result);
        Assert.Equal(3, result.Runs.Count);
        foreach (TaggedRun run in result.Runs) Assert.False(run.IsArtifact);
        Assert.DoesNotContain("/Artifact", Str(result.NewContent), StringComparison.Ordinal);
        Assert.Equal(3, result.NextMcid);
    }

    [Fact]
    public void TagPage_UnknownPageHeight_NeverInventsAnArtifact()
    {
        // PageHeight 0 means "geometry unknown"; classifying by an edge band would then be a guess.
        PageTagResult result = Tag(HeaderBodyFooter, NewOptions(pageHeight: 0f), out byte[] original);

        AssertPureInsertion(original, result);
        foreach (TaggedRun run in result.Runs) Assert.False(run.IsArtifact);
        Assert.DoesNotContain("/Artifact", Str(result.NewContent), StringComparison.Ordinal);
    }

    // =====================================================================
    //  Page boxes that do not start at the origin
    // =====================================================================

    /// <summary>
    /// A page whose box sits below the origin — <c>/MediaBox [0 -100 612 692]</c>, legal per
    /// ISO 32000-1 §7.7.3.3. The lower edge is <c>-100</c>, so the 50 pt footer band covers
    /// <c>Y ∈ [-100, -50]</c> and the line at <c>Y = 20</c> is 120 pt of body text above it.
    /// </summary>
    private const string ShiftedBoxHeaderBodyFooter =
        "BT /F1 9 Tf 72 660 Td (Confidential Header) Tj ET\n" +
        "BT /F1 10 Tf 72 400 Td (Body text.) Tj ET\n" +
        "BT /F1 10 Tf 72 20 Td (More body text, 120pt above the lower edge.) Tj ET\n" +
        "BT /F1 9 Tf 72 -80 Td (Page 1 of 1) Tj ET\n";

    private const float ShiftedBoxTop    = 692f;
    private const float ShiftedBoxBottom = -100f;

    /// <summary>
    /// Measuring the footer band from <c>Y = 0</c> instead of from the page's real lower edge marks
    /// the bottom 150 pt of a shifted page as <c>/Artifact /Pagination /Footer</c>. Artifacts carry
    /// no MCID and get no structure element, so that body text becomes permanently unreachable from
    /// the structure tree — the one failure mode that actively hides content from assistive
    /// technology. The band must follow <see cref="PageTagOptions.PageBottom"/>.
    /// </summary>
    [Fact]
    public void TagPage_PageBoxBelowTheOrigin_DoesNotArtifactBodyTextNearYZero()
    {
        PageTagOptions options = NewOptions(pageHeight: ShiftedBoxTop);
        options.PageBottom = ShiftedBoxBottom;

        PageTagResult result = Tag(ShiftedBoxHeaderBodyFooter, options, out byte[] original);

        AssertPureInsertion(original, result);
        Assert.Equal(4, result.Runs.Count);

        // 32pt below the upper edge: a genuine running head.
        Assert.True(result.Runs[0].IsArtifact);
        Assert.Equal("Header", result.Runs[0].ArtifactSubtype);

        // Y = 400 and Y = 20 are both body text: tagged, with consecutive MCIDs.
        Assert.False(result.Runs[1].IsArtifact);
        Assert.Equal("P", result.Runs[1].Role);
        Assert.Equal(0, result.Runs[1].Mcid);

        Assert.False(result.Runs[2].IsArtifact);
        Assert.Equal("P", result.Runs[2].Role);
        Assert.Equal(1, result.Runs[2].Mcid);
        Assert.Contains("120pt above the lower edge", result.Runs[2].Text, StringComparison.Ordinal);

        // Y = -80 is 20pt above the real lower edge: that one really is the footer.
        Assert.True(result.Runs[3].IsArtifact);
        Assert.Equal("Pagination", result.Runs[3].ArtifactType);
        Assert.Equal("Footer", result.Runs[3].ArtifactSubtype);
        Assert.Equal(-1, result.Runs[3].Mcid);

        Assert.Equal(2, result.NextMcid);
        Assert.Equal(2, Count(Str(result.NewContent), "/MCID"));
        Assert.Equal(1, Count(Str(result.NewContent), "/Subtype /Footer"));
    }

    /// <summary>
    /// What a caller that lies about the geometry gets, stated explicitly: with
    /// <see cref="PageTagOptions.PageBottom"/> left at its default the band is
    /// measured from the origin, so the <c>Y = 20</c> line is swallowed. This is why
    /// <c>PdfAutoTagger</c> must set <c>PageBottom</c> from the real page box (or leave both edges
    /// at 0, which disables the bands) rather than passing the upper edge alone.
    /// </summary>
    [Fact]
    public void TagPage_ShiftedBoxWithPageBottomLeftAtZero_ShowsWhyTheLowerEdgeMustBeSupplied()
    {
        PageTagResult result = Tag(ShiftedBoxHeaderBodyFooter, NewOptions(pageHeight: ShiftedBoxTop),
                                   out byte[] original);

        AssertPureInsertion(original, result);
        Assert.Equal(4, result.Runs.Count);
        Assert.True(result.Runs[2].IsArtifact);              // Y = 20, wrongly inside the band
        Assert.Equal("Footer", result.Runs[2].ArtifactSubtype);
        Assert.Equal(1, result.NextMcid);                    // only one MCID: the Y = 400 line
    }

    // =====================================================================
    //  MCID allocation
    // =====================================================================

    /// <summary>
    /// MCIDs index directly into the page's ParentTree array, so they must start at
    /// <see cref="PageTagOptions.FirstMcid"/>, increase by exactly one per content run in content
    /// order, and never skip a value — a hole would make the ParentTree entry misalign and point
    /// assistive technology at the wrong structure element.
    /// </summary>
    [Fact]
    public void TagPage_McidNumbering_StartsAtFirstMcidIsContiguousAndSkipsArtifacts()
    {
        const string content =
            "BT /F1 9 Tf 72 760 Td (Running header) Tj ET\n" +
            "BT /F1 24 Tf 72 700 Td (Chapter One) Tj ET\n" +
            "BT /F1 10 Tf 72 650 Td (First body line.) Tj ET\n" +
            "BT /F1 10 Tf 72 600 Td (Second body line.) Tj ET\n" +
            "BT /F1 9 Tf 72 30 Td (Page 1) Tj ET\n";

        const int firstMcid = 7;
        PageTagResult result = Tag(content, NewOptions(firstMcid: firstMcid), out byte[] original);

        AssertPureInsertion(original, result);
        Assert.Equal(5, result.Runs.Count);

        var contentMcids = new List<int>();
        foreach (TaggedRun run in result.Runs)
        {
            if (run.IsArtifact)
            {
                Assert.Equal(-1, run.Mcid);
                continue;
            }
            contentMcids.Add(run.Mcid);
        }

        Assert.Equal(new[] { 7, 8, 9 }, contentMcids);
        for (int i = 0; i < contentMcids.Count; i++)
            Assert.Equal(firstMcid + i, contentMcids[i]);       // contiguous, no holes
        Assert.Equal(contentMcids.Count, new HashSet<int>(contentMcids).Count);
        Assert.Equal(firstMcid + contentMcids.Count, result.NextMcid);

        string spliced = Str(result.NewContent);
        Assert.Equal(contentMcids.Count, Count(spliced, "/MCID"));
        foreach (int mcid in contentMcids)
            Assert.Contains("<</MCID " + mcid.ToString(CultureInfo.InvariantCulture) + ">> BDC",
                spliced, StringComparison.Ordinal);
    }

    // =====================================================================
    //  Form XObjects — honest under-reporting
    // =====================================================================

    [Fact]
    public void TagPage_TextOnlyInsideAFormXObject_ReportsNothingTaggedInsteadOfClaimingSuccess()
    {
        const string content = "q 1 0 0 1 0 0 cm /Fm0 Do Q\n";

        PageTagResult result = Tag(content, NewOptions(firstMcid: 4), out byte[] original);

        Assert.False(result.Changed);
        Assert.Same(original, result.NewContent);
        Assert.Empty(result.Runs);
        Assert.Equal(4, result.NextMcid);
        Assert.True(result.ContainsXObjectInvocations);

        // The reason must name the cause, so a caller can warn rather than silently under-tag.
        Assert.Contains("Form XObject", result.SkipReason, StringComparison.Ordinal);
        Assert.DoesNotContain("BDC", Str(result.NewContent), StringComparison.Ordinal);
    }

    [Fact]
    public void TagPage_TextPlusAFormXObject_TagsThePageAndStillFlagsTheInvocation()
    {
        const string content = "q /Fm0 Do Q\nBT /F1 10 Tf 72 400 Td (Visible text) Tj ET\n";

        PageTagResult result = Tag(content, NewOptions(), out byte[] original);

        AssertPureInsertion(original, result);
        Assert.Single(result.Runs);

        // Text drawn inside Fm0 is in another stream and is NOT tagged by this pass.
        Assert.True(result.ContainsXObjectInvocations,
            "the caller must be able to warn that XObject content was not covered");
        Assert.Equal(new[] { "q", "Do", "Q", "BDC", "BT", "Tf", "Td", "Tj", "ET", "EMC" },
            Ops(result.NewContent));
    }

    // =====================================================================
    //  Determinism
    // =====================================================================

    [Fact]
    public void TagPage_IsDeterministic_ForTwoEqualButDistinctBuffers()
    {
        const string content =
            "BT /F1 9 Tf 72 760 Td (Header) Tj ET\n" +
            "BT /F1 24 Tf 72 700 Td (Heading) Tj /F1 10 Tf 0 -40 Td (Body) Tj ET\n" +
            "BT /F1 10 Tf 72 500 Td (More body.) Tj ET\n" +
            "BT /F1 9 Tf 72 30 Td (Footer) Tj ET\n";

        PageTagResult first = ContentStreamTagger.TagPage(Ascii(content), NewOptions());
        PageTagResult second = ContentStreamTagger.TagPage(Ascii(content), NewOptions());

        Assert.True(first.Changed, first.SkipReason);
        Assert.Equal(first.NewContent, second.NewContent);
        Assert.Equal(first.NextMcid, second.NextMcid);
        Assert.Equal(first.Runs.Count, second.Runs.Count);
        for (int i = 0; i < first.Runs.Count; i++)
        {
            Assert.Equal(first.Runs[i].Role, second.Runs[i].Role);
            Assert.Equal(first.Runs[i].Mcid, second.Runs[i].Mcid);
            Assert.Equal(first.Runs[i].TokenStart, second.Runs[i].TokenStart);
            Assert.Equal(first.Runs[i].TokenEnd, second.Runs[i].TokenEnd);
        }
    }

    [Fact]
    public void TagPage_TaggingTheSameBufferTwice_YieldsByteIdenticalOutput()
    {
        byte[] original = Ascii(HeaderBodyFooter);

        byte[] first = ContentStreamTagger.TagPage(original, NewOptions()).NewContent;
        byte[] second = ContentStreamTagger.TagPage(original, NewOptions()).NewContent;

        Assert.Equal(first, second);
    }

    /// <summary>Re-tagging an already-tagged stream must refuse, not nest a second layer.</summary>
    [Fact]
    public void TagPage_OutputOfAPreviousRun_IsRefusedOnTheSecondPass()
    {
        PageTagResult first = Tag(HeaderBodyFooter, NewOptions(), out _);
        Assert.True(first.Changed, first.SkipReason);

        PageTagResult second = ContentStreamTagger.TagPage(first.NewContent, NewOptions());

        Assert.False(second.Changed);
        Assert.Contains("already contains marked content", second.SkipReason, StringComparison.Ordinal);
        Assert.Same(first.NewContent, second.NewContent);
    }

    // =====================================================================
    //  DefaultClassifier
    // =====================================================================

    [Fact]
    public void DefaultClassifier_NullCandidate_FallsBackToParagraph()
    {
        RunClassification classification = ContentStreamTagger.DefaultClassifier(null);

        Assert.False(classification.IsArtifact);
        Assert.Equal("P", classification.Role);
        Assert.True(classification.IsMarked);
    }

    [Theory]
    [InlineData(770f, "Header")]    // 22pt from the top, inside the 50pt band
    [InlineData(742f, "Header")]    // exactly 50pt from the top — the band is inclusive
    [InlineData(50f, "Footer")]     // exactly 50pt from the bottom
    [InlineData(20f, "Footer")]
    public void DefaultClassifier_EdgeBands_ProducePaginationArtifacts(float y, string expectedSubtype)
    {
        PageTagOptions options = NewOptions();
        RunClassification classification = ContentStreamTagger.DefaultClassifier(new RunCandidate
        {
            Options = options,
            PageHeight = PageHeightPt,
            MedianFontSize = 10f,
            FontSize = 10f,
            Y = y,
        });

        Assert.True(classification.IsArtifact);
        Assert.Equal("Pagination", classification.ArtifactType);
        Assert.Equal(expectedSubtype, classification.ArtifactSubtype);
        Assert.Null(classification.Role);
    }

    [Theory]
    [InlineData(741f)]              // 51pt from the top — outside the header band
    [InlineData(400f)]
    [InlineData(51f)]               // 51pt from the bottom — outside the footer band
    public void DefaultClassifier_InsideTheBody_IsNeverAnArtifact(float y)
    {
        RunClassification classification = ContentStreamTagger.DefaultClassifier(new RunCandidate
        {
            Options = NewOptions(),
            PageHeight = PageHeightPt,
            MedianFontSize = 10f,
            FontSize = 10f,
            Y = y,
        });

        Assert.False(classification.IsArtifact);
        Assert.Equal("P", classification.Role);
    }

    [Theory]
    [InlineData(24f, "H1")]
    [InlineData(18f, "H1")]         // exactly 1.8x
    [InlineData(15f, "H2")]
    [InlineData(14f, "H2")]         // exactly 1.4x
    [InlineData(12f, "H3")]
    [InlineData(11f, "P")]          // 1.1x — below the 1.15x H3 threshold
    [InlineData(10f, "P")]
    [InlineData(8f, "P")]
    public void DefaultClassifier_FontSizeRatio_SelectsTheHeadingLevel(float fontSize, string expectedRole)
    {
        RunClassification classification = ContentStreamTagger.DefaultClassifier(new RunCandidate
        {
            Options = NewOptions(),
            PageHeight = PageHeightPt,
            MedianFontSize = 10f,
            FontSize = fontSize,
            Y = 400f,
        });

        Assert.False(classification.IsArtifact);
        Assert.Equal(expectedRole, classification.Role);
    }

    [Fact]
    public void DefaultClassifier_ArtifactMarkingDisabled_ClassifiesEdgeTextAsContent()
    {
        PageTagOptions options = NewOptions();
        options.MarkHeadersFootersAsArtifacts = false;

        RunClassification classification = ContentStreamTagger.DefaultClassifier(new RunCandidate
        {
            Options = options,
            PageHeight = PageHeightPt,
            MedianFontSize = 10f,
            FontSize = 10f,
            Y = 780f,
        });

        Assert.False(classification.IsArtifact);
        Assert.Equal("P", classification.Role);
    }

    /// <summary>
    /// Both bands are relative to the page's real edges. On <c>[0 -100 612 692]</c> the header band
    /// is <c>Y ∈ [642, 692]</c> and the footer band is <c>Y ∈ [-100, -50]</c>; everything between is
    /// content, including the whole 150 pt strip below <c>Y = 50</c> that a band measured from the
    /// origin would have artifacted away.
    /// </summary>
    [Theory]
    [InlineData(690f, "Header")]    // 2pt below the upper edge
    [InlineData(642f, "Header")]    // exactly 50pt below it — the band is inclusive
    [InlineData(641f, null)]        // 51pt below it — outside the band
    [InlineData(20f, null)]         // 120pt above the lower edge: body text, not a footer
    [InlineData(0f, null)]          // the origin is 100pt above the lower edge here
    [InlineData(-49f, null)]        // 51pt above the lower edge — outside the band
    [InlineData(-50f, "Footer")]    // exactly 50pt above it
    [InlineData(-95f, "Footer")]
    public void DefaultClassifier_PageBoxBelowTheOrigin_MeasuresBandsFromTheRealEdges(
        float y, string expectedSubtype)
    {
        PageTagOptions options = NewOptions(pageHeight: 692f);
        options.PageBottom = -100f;

        RunClassification classification = ContentStreamTagger.DefaultClassifier(new RunCandidate
        {
            Options = options,
            PageHeight = options.PageHeight,
            PageBottom = options.PageBottom,
            MedianFontSize = 10f,
            FontSize = 10f,
            Y = y,
        });

        if (expectedSubtype == null)
        {
            Assert.False(classification.IsArtifact);
            Assert.Equal("P", classification.Role);
        }
        else
        {
            Assert.True(classification.IsArtifact);
            Assert.Equal("Pagination", classification.ArtifactType);
            Assert.Equal(expectedSubtype, classification.ArtifactSubtype);
        }
    }

    /// <summary>
    /// A page box entirely above the origin — <c>[0 100 612 892]</c>. The footer band must move up
    /// with it, otherwise a genuine running foot at <c>Y = 120</c> is never recognised.
    /// </summary>
    [Theory]
    [InlineData(120f, "Footer")]
    [InlineData(151f, null)]
    [InlineData(50f, "Footer")]     // below the box altogether: clipped, and never body text
    public void DefaultClassifier_PageBoxAboveTheOrigin_MovesTheFooterBandUpWithIt(
        float y, string expectedSubtype)
    {
        PageTagOptions options = NewOptions(pageHeight: 892f);
        options.PageBottom = 100f;

        RunClassification classification = ContentStreamTagger.DefaultClassifier(new RunCandidate
        {
            Options = options,
            PageHeight = options.PageHeight,
            PageBottom = options.PageBottom,
            MedianFontSize = 10f,
            FontSize = 10f,
            Y = y,
        });

        Assert.Equal(expectedSubtype != null, classification.IsArtifact);
        Assert.Equal(expectedSubtype, classification.ArtifactSubtype);
    }

    /// <summary>
    /// Adversarial geometry: margins wide enough to meet in the middle would classify <em>every</em>
    /// line as pagination and leave the structure tree pointing at nothing. Marking nothing is the
    /// only safe answer — the same conclusion <c>PdfArtifactMarker</c> reaches by skipping the page.
    /// </summary>
    [Theory]
    [InlineData(95f)]
    [InlineData(50f)]
    [InlineData(5f)]
    public void DefaultClassifier_BandsThatCoverTheWholePage_MarkNothingAsAnArtifact(float y)
    {
        PageTagOptions options = NewOptions(pageHeight: 100f);
        options.HeaderMargin = 60f;
        options.FooterMargin = 60f;

        RunClassification classification = ContentStreamTagger.DefaultClassifier(new RunCandidate
        {
            Options = options,
            PageHeight = options.PageHeight,
            MedianFontSize = 10f,
            FontSize = 10f,
            Y = y,
        });

        Assert.False(classification.IsArtifact);
        Assert.Equal("P", classification.Role);
    }

    /// <summary>
    /// Non-finite geometry cannot bound a band: every comparison against NaN is false in both
    /// directions, so the answer must be "no artifact" rather than whichever branch NaN happens to
    /// fall through.
    /// </summary>
    [Theory]
    [InlineData(float.NaN, 0f)]
    [InlineData(792f, float.NaN)]
    [InlineData(float.PositiveInfinity, 0f)]
    [InlineData(792f, float.NegativeInfinity)]
    public void DefaultClassifier_NonFinitePageEdges_NeverInventAnArtifact(float top, float bottom)
    {
        PageTagOptions options = NewOptions(pageHeight: top);
        options.PageBottom = bottom;

        RunClassification classification = ContentStreamTagger.DefaultClassifier(new RunCandidate
        {
            Options = options,
            PageHeight = top,
            PageBottom = bottom,
            MedianFontSize = 10f,
            FontSize = 10f,
            Y = 780f,
        });

        Assert.False(classification.IsArtifact);
        Assert.Equal("P", classification.Role);
    }

    /// <summary>An inverted or zero-height box is unusable geometry, not a reason to guess.</summary>
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(500f, 500f)]
    [InlineData(100f, 700f)]
    public void DefaultClassifier_UnusablePageExtent_NeverInventsAnArtifact(float top, float bottom)
    {
        PageTagOptions options = NewOptions(pageHeight: top);
        options.PageBottom = bottom;

        RunClassification classification = ContentStreamTagger.DefaultClassifier(new RunCandidate
        {
            Options = options,
            PageHeight = top,
            PageBottom = bottom,
            MedianFontSize = 10f,
            FontSize = 10f,
            Y = top,
        });

        Assert.False(classification.IsArtifact);
        Assert.Equal("P", classification.Role);
    }

    [Fact]
    public void RunClassification_Unmarked_IsNotMarked()
    {
        Assert.False(RunClassification.Unmarked.IsMarked);
        Assert.True(RunClassification.Content("P").IsMarked);
        Assert.True(RunClassification.Artifact("Pagination", "Header").IsMarked);
        Assert.Equal("Layout", RunClassification.Artifact(null, null).ArtifactType);
    }

    // =====================================================================
    //  ConcatenatePageContent
    // =====================================================================

    private const string PartOne = "BT /F1 10 Tf 72 400 Td (Alpha) Tj ET";
    private const string PartTwo = "BT /F1 10 Tf 72 380 Td (Beta) Tj ET";
    private const string PartThree = "BT /F1 10 Tf 72 360 Td (Gamma) Tj ET";

    /// <summary>Single-page PDF whose /Contents is one stream holding <paramref name="content"/>.</summary>
    private static byte[] SingleStreamPdf(string content)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(int n, string body)
        {
            offsets.Add(sb.Length);
            sb.Append(n).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        sb.Append("%PDF-1.7\n");
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
             + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>");
        // /Length counts exactly the content bytes; the EOL before endstream is not part of them.
        Obj(4, "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n"
             + content + "\nendstream");
        Obj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        return FinishPdf(sb, offsets);
    }

    /// <summary>Single-page PDF whose /Contents is an array of three streams.</summary>
    private static byte[] ArrayContentsPdf(string a, string b, string c)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(int n, string body)
        {
            offsets.Add(sb.Length);
            sb.Append(n).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }
        string Stream(string s) =>
            "<< /Length " + s.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + s + "\nendstream";

        sb.Append("%PDF-1.7\n");
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
             + "/Resources << /Font << /F1 7 0 R >> >> /Contents [4 0 R 5 0 R 6 0 R] >>");
        Obj(4, Stream(a));
        Obj(5, Stream(b));
        Obj(6, Stream(c));
        Obj(7, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        return FinishPdf(sb, offsets);
    }

    /// <summary>Single-page PDF whose page dictionary has no /Contents at all.</summary>
    private static byte[] NoContentsPdf()
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(int n, string body)
        {
            offsets.Add(sb.Length);
            sb.Append(n).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        sb.Append("%PDF-1.7\n");
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        return FinishPdf(sb, offsets);
    }

    private static byte[] FinishPdf(StringBuilder sb, List<int> offsets)
    {
        int size = offsets.Count + 1;
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(size.ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(size.ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    [Fact]
    public void ConcatenatePageContent_SingleStream_ReturnsItsDecodedBytesExactly()
    {
        using var doc = PdfDocument.Open(new MemoryStream(SingleStreamPdf(PartOne)));

        byte[] content = ContentStreamTagger.ConcatenatePageContent(doc, doc.Pages[0].Dictionary);

        Assert.NotNull(content);
        Assert.Equal(PartOne, Str(content));
        Assert.Null(ContentStreamTagger.LastTagError);
    }

    /// <summary>
    /// ISO 32000-1 7.8.2: array parts are concatenated with at least one white-space character
    /// between them. Without the separator a part ending in <c>Tf</c> would fuse with a part
    /// starting in <c>72</c>, and every byte offset after that point would be meaningless.
    /// </summary>
    [Fact]
    public void ConcatenatePageContent_ArrayOfStreams_JoinsThePartsWithASingleNewline()
    {
        using var doc = PdfDocument.Open(new MemoryStream(ArrayContentsPdf(PartOne, PartTwo, PartThree)));

        byte[] content = ContentStreamTagger.ConcatenatePageContent(doc, doc.Pages[0].Dictionary);

        Assert.NotNull(content);
        Assert.Equal(PartOne + "\n" + PartTwo + "\n" + PartThree, Str(content));
        Assert.Equal(PartOne.Length + PartTwo.Length + PartThree.Length + 2, content.Length);
        Assert.Null(ContentStreamTagger.LastTagError);
    }

    [Fact]
    public void ConcatenatePageContent_ArrayOfStreams_ProducesAStreamTheTaggerCanSplice()
    {
        using var doc = PdfDocument.Open(new MemoryStream(ArrayContentsPdf(PartOne, PartTwo, PartThree)));
        byte[] original = ContentStreamTagger.ConcatenatePageContent(doc, doc.Pages[0].Dictionary);

        PageTagResult result = ContentStreamTagger.TagPage(original, NewOptions());

        AssertPureInsertion(original, result);
        string spliced = Str(result.NewContent);
        Assert.Contains("(Alpha) Tj", spliced, StringComparison.Ordinal);
        Assert.Contains("(Beta) Tj", spliced, StringComparison.Ordinal);
        Assert.Contains("(Gamma) Tj", spliced, StringComparison.Ordinal);
    }

    [Fact]
    public void ConcatenatePageContent_PageWithoutContents_ReturnsAnEmptyArrayNotNull()
    {
        using var doc = PdfDocument.Open(new MemoryStream(NoContentsPdf()));

        byte[] content = ContentStreamTagger.ConcatenatePageContent(doc, doc.Pages[0].Dictionary);

        Assert.NotNull(content);
        Assert.Empty(content);
    }

    [Fact]
    public void ConcatenatePageContent_NullArguments_ReturnAnEmptyArray()
    {
        Assert.Empty(ContentStreamTagger.ConcatenatePageContent(null, null));

        using var doc = PdfDocument.Open(new MemoryStream(SingleStreamPdf(PartOne)));
        Assert.Empty(ContentStreamTagger.ConcatenatePageContent(doc, null));
        Assert.Empty(ContentStreamTagger.ConcatenatePageContent(null, doc.Pages[0].Dictionary));
    }

    // =====================================================================
    //  TryGetPageVerticalExtent
    // =====================================================================

    /// <summary>Single-page PDF with caller-chosen page boxes. <paramref name="cropBox"/> may be null.</summary>
    private static byte[] PageBoxPdf(string mediaBox, string cropBox, string content)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(int n, string body)
        {
            offsets.Add(sb.Length);
            sb.Append(n).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        sb.Append("%PDF-1.7\n");
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox " + mediaBox
             + (cropBox == null ? string.Empty : " /CropBox " + cropBox)
             + " /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>");
        Obj(4, "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n"
             + content + "\nendstream");
        Obj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        return FinishPdf(sb, offsets);
    }

    [Fact]
    public void TryGetPageVerticalExtent_MediaBoxBelowTheOrigin_ReportsBothRealEdges()
    {
        using var doc = PdfDocument.Open(new MemoryStream(
            PageBoxPdf("[0 -100 612 692]", null, PartOne)));

        Assert.True(ContentStreamTagger.TryGetPageVerticalExtent(
            doc, doc.Pages[0], out float top, out float bottom));

        Assert.Equal(692f, top);
        Assert.Equal(-100f, bottom);
    }

    /// <summary>
    /// /CropBox is what the reader sees, so it — not /MediaBox — is where a running head sits.
    /// </summary>
    [Fact]
    public void TryGetPageVerticalExtent_CropBox_TakesPrecedenceOverMediaBox()
    {
        using var doc = PdfDocument.Open(new MemoryStream(
            PageBoxPdf("[0 0 612 792]", "[0 36 612 756]", PartOne)));

        Assert.True(ContentStreamTagger.TryGetPageVerticalExtent(
            doc, doc.Pages[0], out float top, out float bottom));

        Assert.Equal(756f, top);
        Assert.Equal(36f, bottom);
    }

    /// <summary>ISO 32000-1 §7.9.5: a rectangle may be written with either corner first.</summary>
    [Fact]
    public void TryGetPageVerticalExtent_ReversedCorners_AreSortedNotTrusted()
    {
        using var doc = PdfDocument.Open(new MemoryStream(
            PageBoxPdf("[612 792 0 -100]", null, PartOne)));

        Assert.True(ContentStreamTagger.TryGetPageVerticalExtent(
            doc, doc.Pages[0], out float top, out float bottom));

        Assert.Equal(792f, top);
        Assert.Equal(-100f, bottom);
    }

    [Fact]
    public void TryGetPageVerticalExtent_ZeroHeightBox_ReturnsFalseAndZeroEdges()
    {
        using var doc = PdfDocument.Open(new MemoryStream(
            PageBoxPdf("[0 500 612 500]", null, PartOne)));

        Assert.False(ContentStreamTagger.TryGetPageVerticalExtent(
            doc, doc.Pages[0], out float top, out float bottom));

        // Both zero, which is exactly the "geometry unknown" state that disables the edge bands.
        Assert.Equal(0f, top);
        Assert.Equal(0f, bottom);
    }

    [Fact]
    public void TryGetPageVerticalExtent_NullPage_ReturnsFalse()
    {
        Assert.False(ContentStreamTagger.TryGetPageVerticalExtent(
            null, null, out float top, out float bottom));
        Assert.Equal(0f, top);
        Assert.Equal(0f, bottom);
    }

    /// <summary>
    /// The whole point of the helper, end to end: a page whose box is below the origin, tagged the
    /// way a caller is meant to drive this — read the extent, put <em>both</em> edges into the
    /// options, splice. The body text 120 pt above the lower edge keeps its MCID.
    /// </summary>
    [Fact]
    public void TryGetPageVerticalExtent_DrivingTagPage_KeepsBodyTextNearYZeroTagged()
    {
        using var doc = PdfDocument.Open(new MemoryStream(
            PageBoxPdf("[0 -100 612 692]", null, ShiftedBoxHeaderBodyFooter)));

        Assert.True(ContentStreamTagger.TryGetPageVerticalExtent(
            doc, doc.Pages[0], out float top, out float bottom));

        PageTagOptions options = NewOptions(pageHeight: top);
        options.PageBottom = bottom;

        byte[] original = ContentStreamTagger.ConcatenatePageContent(doc, doc.Pages[0].Dictionary);
        PageTagResult result = ContentStreamTagger.TagPage(original, options);

        AssertPureInsertion(original, result);
        Assert.Equal(2, result.NextMcid);
        Assert.Equal(1, Count(Str(result.NewContent), "/Subtype /Footer"));
        Assert.Equal(1, Count(Str(result.NewContent), "/Subtype /Header"));
    }

    // =====================================================================
    //  Run merging — cost of joining many lines into one paragraph run
    // =====================================================================

    /// <summary>
    /// One <c>BT…ET</c> in which every show operator is its own line yet all of them belong to a
    /// single paragraph, which is the shape that makes run merging expensive.
    ///
    /// <para>
    /// PDF user space has Y growing <em>upward</em>, and the paragraph split in
    /// <c>AssignParagraphIds</c> only fires on a gap <em>larger</em> than the threshold. Alternating
    /// the baseline between two values 2 pt apart therefore does both things at once: 2 pt exceeds
    /// <c>SameLineTolerance</c> (1 pt) so each show starts a new line group, while the computed gap
    /// (<c>previousY − y − fontSize</c>) is negative and never splits the paragraph. A page like
    /// this is a handful of bytes per line to produce and is entirely legal PDF.
    /// </para>
    /// </summary>
    private static byte[] OneParagraphOfManyLines(int lineCount, int charsPerLine, out string expectedText)
    {
        string glyphs = new string('A', charsPerLine);

        var body     = new StringBuilder(lineCount * (charsPerLine + 32) + 32);
        var expected = new StringBuilder(lineCount * (charsPerLine + 1));

        // Font size 10 == PageTagOptions.MedianFontSize, so DefaultClassifier reports every line as
        // a paragraph (ratio 1.0) rather than a heading. No injected classifier: the merge under
        // test is the one the production path takes.
        body.Append("BT\n/F1 10 Tf\n");
        for (int i = 0; i < lineCount; i++)
        {
            int y = 500 + (i % 2) * 2;
            body.Append("1 0 0 1 72 ").Append(y.ToString(CultureInfo.InvariantCulture)).Append(" Tm\n");
            body.Append('(').Append(glyphs).Append(") Tj\n");

            if (i > 0) expected.Append(' ');
            expected.Append(glyphs);
        }
        body.Append("ET\n");

        expectedText = expected.ToString();
        return Ascii(body.ToString());
    }

    /// <summary>
    /// Adversarial input: a single paragraph of thousands of lines. Merging used to rebuild the
    /// whole accumulated run text on every line — <c>sb.Length = 0; sb.Append(last.Text)…;
    /// last.Text = sb.ToString()</c> — which copies the run twice per line and allocates a
    /// progressively larger string each time. That is O(lines² × charsPerLine) work and
    /// O(lines² × charsPerLine) bytes, most of it landing straight on the large object heap; at the
    /// size below it allocates on the order of 3 GB for a half-megabyte content stream, and nothing
    /// caps how many lines one paragraph may contain.
    ///
    /// <para>
    /// Allocated bytes rather than elapsed time is the assertion, because it is deterministic and
    /// machine-independent: the linear implementation allocates a few tens of megabytes for the
    /// whole tagging pass (scan, cross-check, splice, re-validate), the quadratic one allocates
    /// three orders of magnitude more, and the budget sits an order of magnitude clear of both.
    /// </para>
    /// </summary>
    [Fact]
    public void TagPage_OneParagraphOfThousandsOfLines_MergesInLinearWorkNotQuadratic()
    {
        // Warm-up so JIT compilation and one-time statics are not charged to the measurement.
        byte[] warmup = OneParagraphOfManyLines(8, 8, out _);
        ContentStreamTagger.TagPage(warmup, NewOptions());

        const int LineCount    = 4000;
        const int CharsPerLine = 96;
        byte[] original = OneParagraphOfManyLines(LineCount, CharsPerLine, out string expectedText);

        long before = GC.GetAllocatedBytesForCurrentThread();
        PageTagResult result = ContentStreamTagger.TagPage(original, NewOptions());
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // The merge must still be correct at this size, not merely cheap.
        AssertPureInsertion(original, result);
        TaggedRun run = Assert.Single(result.Runs);
        Assert.Equal("P", run.Role);
        Assert.Equal(LineCount * CharsPerLine + (LineCount - 1), run.Text.Length);
        Assert.Equal(expectedText, run.Text);

        const long Budget = 256L * 1024 * 1024;
        Assert.True(
            allocated < Budget,
            "Tagging a " + LineCount.ToString(CultureInfo.InvariantCulture) + "-line paragraph allocated " +
            (allocated / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MB, over the " +
            (Budget / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MB budget — run merging is " +
            "rebuilding the accumulated text per line again (quadratic).");
    }

    /// <summary>
    /// The same shape split into separate paragraphs must still produce one run per paragraph with
    /// its own text — proof that the accumulator is per segment and never leaks across a split.
    /// </summary>
    [Fact]
    public void TagPage_ParagraphsSeparatedByALargeGap_KeepTheirOwnTextAndMcids()
    {
        const string Content =
            "BT\n/F1 10 Tf\n" +
            "1 0 0 1 72 600 Tm\n(Alpha) Tj\n" +
            "1 0 0 1 72 586 Tm\n(Beta) Tj\n" +
            "1 0 0 1 72 500 Tm\n(Gamma) Tj\n" +   // 86 pt below Beta: a new paragraph
            "1 0 0 1 72 486 Tm\n(Delta) Tj\n" +
            "ET\n";

        PageTagResult result = Tag(Content, NewOptions(), out byte[] original);

        AssertPureInsertion(original, result);
        Assert.Equal(2, result.Runs.Count);
        Assert.Equal("Alpha Beta", result.Runs[0].Text);
        Assert.Equal("Gamma Delta", result.Runs[1].Text);
        Assert.Equal(0, result.Runs[0].Mcid);
        Assert.Equal(1, result.Runs[1].Mcid);
        Assert.NotEqual(result.Runs[0].ParagraphId, result.Runs[1].ParagraphId);
    }

    // =====================================================================
    //  Size ceiling — one page must not be able to cost gigabytes
    // =====================================================================

    /// <summary>
    /// <paramref name="length"/> bytes of nothing but <c>q Q </c>: perfectly legal PDF that draws
    /// nothing, contains no text, and is a few kilobytes of Flate on disk. It is the cheapest way to
    /// hand the tagger a very large decoded stream — which is exactly the attack, since Core lets a
    /// single content stream inflate to <c>PdfLimits.MaxDecompressedBytes</c> (512 MiB) and the
    /// tagging pass costs a large multiple of whatever it is given.
    /// </summary>
    private static byte[] VectorNoiseOfSize(int length)
    {
        byte[] unit = Ascii("q Q ");
        var content = new byte[length];
        for (int i = 0; i < length; i++) content[i] = unit[i % unit.Length];
        return content;
    }

    /// <summary>
    /// Adversarial input: a decoded page content stream one byte past the ceiling.
    ///
    /// <para>
    /// Before the ceiling existed, this page was scanned into a token list (whose capacity alone was
    /// pre-allocated as <c>content.Length / 12</c> 32-byte structs), re-parsed in full by the
    /// production parser for the cross-check, and — had it contained any text — re-scanned again
    /// after splicing, with all three representations live at once. Measured at 16 MiB that was
    /// 20 seconds, 7.4 GB allocated and a 1.9 GB peak working set, <em>per page</em>, and the bill
    /// was paid even though this page draws no text and is then skipped for having nothing to tag.
    /// </para>
    ///
    /// <para>
    /// Allocated bytes is the assertion rather than elapsed time because it is deterministic and
    /// machine-independent: refusing on the length costs a couple of strings, while analysing the
    /// stream costs hundreds of times the stream itself, and the budget sits orders of magnitude
    /// clear of both.
    /// </para>
    /// </summary>
    [Fact]
    public void TagPage_ContentStreamPastTheCeiling_IsRefusedBeforeItIsAnalysed()
    {
        // Warm-up so JIT compilation and one-time statics are not charged to the measurement.
        ContentStreamTagger.TagPage(VectorNoiseOfSize(4096), NewOptions());

        byte[] original = VectorNoiseOfSize(ContentStreamTagger.DefaultMaxContentBytes + 1);

        long before = GC.GetAllocatedBytesForCurrentThread();
        PageTagResult result = ContentStreamTagger.TagPage(original, NewOptions(firstMcid: 7));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Refused, and refused honestly: the page keeps its exact bytes and the caller is told why.
        Assert.False(result.Changed);
        Assert.Same(original, result.NewContent);
        Assert.Empty(result.Runs);
        Assert.Equal(7, result.NextMcid);
        Assert.Contains("tagging limit", result.SkipReason, StringComparison.Ordinal);
        Assert.Contains(original.Length.ToString(CultureInfo.InvariantCulture), result.SkipReason,
                        StringComparison.Ordinal);
        Assert.Contains(ContentStreamTagger.DefaultMaxContentBytes.ToString(CultureInfo.InvariantCulture),
                        result.SkipReason, StringComparison.Ordinal);

        const long Budget = 1L * 1024 * 1024;
        Assert.True(
            allocated < Budget,
            "Refusing a " + original.Length.ToString(CultureInfo.InvariantCulture) +
            "-byte page allocated " + allocated.ToString(CultureInfo.InvariantCulture) +
            " bytes, over the " + Budget.ToString(CultureInfo.InvariantCulture) +
            "-byte budget — the stream is being scanned or parsed before its size is checked.");
    }

    /// <summary>The ceiling is inclusive: a page of exactly the limit is tagged, not refused.</summary>
    [Fact]
    public void TagPage_ContentStreamExactlyAtTheCeiling_IsStillTagged()
    {
        byte[] original = Ascii(OneParagraph);
        PageTagOptions options = NewOptions();
        options.MaxContentBytes = original.Length;

        PageTagResult result = ContentStreamTagger.TagPage(original, options);

        AssertPureInsertion(original, result);
        Assert.Equal("P", Assert.Single(result.Runs).Role);
    }

    /// <summary>
    /// One byte over, and the same page is refused — with both numbers in the reason, so a
    /// remediation report can say what happened rather than just "not tagged".
    /// </summary>
    [Fact]
    public void TagPage_ContentStreamOneByteOverTheCeiling_IsRefusedAndNamesBothNumbers()
    {
        byte[] original = Ascii(OneParagraph);
        PageTagOptions options = NewOptions(firstMcid: 4);
        options.MaxContentBytes = original.Length - 1;

        PageTagResult result = ContentStreamTagger.TagPage(original, options);

        Assert.False(result.Changed);
        Assert.Same(original, result.NewContent);
        Assert.Empty(result.Runs);
        Assert.Equal(4, result.NextMcid);
        Assert.Contains(original.Length.ToString(CultureInfo.InvariantCulture), result.SkipReason,
                        StringComparison.Ordinal);
        Assert.Contains((original.Length - 1).ToString(CultureInfo.InvariantCulture), result.SkipReason,
                        StringComparison.Ordinal);
    }

    /// <summary>
    /// A non-positive ceiling means "no ceiling", not "refuse everything" — the difference between
    /// an opt-out and a naive <c>length &gt; limit</c> that would silently stop tagging every page.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TagPage_NonPositiveCeiling_DisablesTheCeilingRatherThanRefusingEveryPage(int ceiling)
    {
        byte[] original = Ascii(OneParagraph);
        PageTagOptions options = NewOptions();
        options.MaxContentBytes = ceiling;

        PageTagResult result = ContentStreamTagger.TagPage(original, options);

        AssertPureInsertion(original, result);
    }

    /// <summary>
    /// Pins the default so no caller can end up unbounded by accident: every production caller in
    /// this assembly builds a <see cref="PageTagOptions"/> without touching
    /// <see cref="PageTagOptions.MaxContentBytes"/>, so the default <em>is</em> the guard.
    /// </summary>
    [Fact]
    public void PageTagOptions_Default_CarriesTheDocumentedCeiling()
    {
        Assert.Equal(4 * 1024 * 1024, ContentStreamTagger.DefaultMaxContentBytes);
        Assert.Equal(ContentStreamTagger.DefaultMaxContentBytes, new PageTagOptions().MaxContentBytes);
    }

    /// <summary>
    /// Adversarial input for the other half of the guard: <c>/Contents</c> is an array, and Core
    /// caps each stream individually, so the concatenation is otherwise unbounded — forty 512 MiB
    /// parts ask for a 20 GiB array, and merely crossing <c>int.MaxValue</c> used to wrap the length
    /// arithmetic and throw out of a method whose contract is to return <c>null</c>. The ceiling is
    /// exercised at a tractable size here; the production one is
    /// <see cref="ContentStreamTagger.MaxConcatenatedContentBytes"/>.
    /// </summary>
    [Fact]
    public void ConcatenatePageContent_PartsExceedTheCeiling_ReturnNullAndSayWhy()
    {
        using var doc = PdfDocument.Open(new MemoryStream(ArrayContentsPdf(PartOne, PartTwo, PartThree)));

        // Big enough for the first part alone, far too small for all three.
        byte[] content = ContentStreamTagger.ConcatenatePageContent(
            doc, doc.Pages[0].Dictionary, PartOne.Length + 1);

        Assert.Null(content);
        Assert.NotNull(ContentStreamTagger.LastTagError);
        Assert.Contains("decodes to more than", ContentStreamTagger.LastTagError, StringComparison.Ordinal);
    }

    /// <summary>A single oversized stream is refused the same way an oversized array is.</summary>
    [Fact]
    public void ConcatenatePageContent_SingleStreamExceedsTheCeiling_ReturnsNull()
    {
        using var doc = PdfDocument.Open(new MemoryStream(SingleStreamPdf(PartOne)));

        byte[] content = ContentStreamTagger.ConcatenatePageContent(
            doc, doc.Pages[0].Dictionary, PartOne.Length - 1);

        Assert.Null(content);
        Assert.NotNull(ContentStreamTagger.LastTagError);
    }

    /// <summary>
    /// The ceiling counts exactly what is assembled — every part plus the one separator between each
    /// pair — so a page that fits it precisely is still returned in full.
    /// </summary>
    [Fact]
    public void ConcatenatePageContent_CeilingEqualToTheAssembledSize_ReturnsTheWholeContent()
    {
        using var doc = PdfDocument.Open(new MemoryStream(ArrayContentsPdf(PartOne, PartTwo, PartThree)));
        int exact = PartOne.Length + PartTwo.Length + PartThree.Length + 2;

        byte[] content = ContentStreamTagger.ConcatenatePageContent(doc, doc.Pages[0].Dictionary, exact);

        Assert.NotNull(content);
        Assert.Equal(exact, content.Length);
        Assert.Equal(PartOne + "\n" + PartTwo + "\n" + PartThree, Str(content));
        Assert.Null(ContentStreamTagger.LastTagError);
    }
}
