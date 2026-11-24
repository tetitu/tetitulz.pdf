// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Document;
using System.Text;
using Tetitulz.Pdf.Accessibility;
using Tetitulz.Pdf.Accessibility.Models;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>Tests for PdfAccessibility.AutoTag and AutoTagOptions.</summary>
public class PdfAutoTaggerTests
{
    // ---------------------------------------------------------------
    //  Null-guard tests
    // ---------------------------------------------------------------

    [Fact]
    public void AutoTag_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PdfAccessibility.AutoTag((Stream)null, new MemoryStream()));
    }

    [Fact]
    public void AutoTag_NullOutput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PdfAccessibility.AutoTag(new MemoryStream(PdfAccessibilityAuditTests.MinimalPdf()), null));
    }

    // ---------------------------------------------------------------
    //  AutoTag on minimal PDF
    // ---------------------------------------------------------------

    /// <summary>
    /// A page that draws no text has nothing to tag, and the auto-tagger must **not** paper over
    /// that: no structure tree, no <c>/MarkInfo /Marked</c>, and a warning saying so. Writing
    /// either key would hide the true finding (UA01/UA05 "not tagged" would vanish) and
    /// manufacture a false one, which is the rule stated in GAPS.md 5c.
    ///
    /// <para>The predecessor asserted <c>output.Length &gt; 0</c> — satisfied by a tagger that
    /// dishonestly claimed conformance, and equally by one that did nothing at all.</para>
    /// </summary>
    [Fact]
    public void AutoTag_PageWithNothingToTag_WritesNoStructureTreeAndWarnsWhy()
    {
        using var input  = new MemoryStream(PdfAccessibilityAuditTests.MinimalPdf());
        using var output = new MemoryStream();
        AutoTagResult result = PdfAccessibility.AutoTag(input, output);

        Assert.Equal(0, result.ElementsAdded);
        Assert.Empty(result.HeadingsByLevel);
        Assert.Contains(result.Warnings, w => w.Contains("no structure tree was written"));

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        Assert.False(doc.Catalog.Dictionary.ContainsKey(PdfName.StructTreeRoot));
        Assert.False(doc.Catalog.Dictionary.ContainsKey(PdfName.MarkInfo));
    }

    [Fact]
    public void AutoTag_MinimalPdf_ReturnsResult()
    {
        using var input  = new MemoryStream(PdfAccessibilityAuditTests.MinimalPdf());
        using var output = new MemoryStream();
        AutoTagResult result = PdfAccessibility.AutoTag(input, output);
        // Minimal PDF has no extractable text; PagesProcessed should be >= 0
        Assert.True(result.PagesProcessed >= 0);
        Assert.NotNull(result.HeadingsByLevel);
        Assert.NotNull(result.Warnings);
    }

    /// <summary>
    /// A document that already carries a structure tree must come back with that tree intact — the
    /// auto-tagger adds nothing here (this fixture's page has no taggable content) and must not
    /// destroy what is already there. The old name, "SkipsOrProducesOutput", described both
    /// possible behaviours and therefore asserted neither.
    /// </summary>
    [Fact]
    public void AutoTag_AlreadyTaggedDocument_LeavesItsExistingTreeIntact()
    {
        byte[] source = PdfAccessibilityAuditTests.TaggedPdf();

        using (var check = new MemoryStream(source))
        using (var before = PdfDocument.Open(check))
            Assert.True(before.Catalog.Dictionary.ContainsKey(PdfName.StructTreeRoot),
                "the fixture must start tagged, or this test proves nothing");

        using var input  = new MemoryStream(source);
        using var output = new MemoryStream();
        AutoTagResult result = PdfAccessibility.AutoTag(input, output);

        Assert.Equal(0, result.ElementsAdded);

        using var after = PdfDocument.Open(new MemoryStream(output.ToArray()));
        Assert.True(after.Catalog.Dictionary.ContainsKey(PdfName.StructTreeRoot));
        Assert.True(after.Catalog.Dictionary.ContainsKey(PdfName.MarkInfo));
    }

    /// <summary>
    /// <c>null</c> options must behave exactly like a default-constructed <see cref="AutoTagOptions"/>.
    /// Running both over a content-bearing page and comparing the outcomes is what makes that
    /// checkable; "the result is not null" is true however the null was handled, including by
    /// silently disabling every step.
    /// </summary>
    [Fact]
    public void AutoTag_WithNullOptions_BehavesLikeDefaultOptions()
    {
        byte[] source = ContentBearingPdfTests.ContentPdf();

        AutoTagResult withNull, withDefaults;
        using (var i = new MemoryStream(source))
        using (var o = new MemoryStream())
            withNull = PdfAccessibility.AutoTag(i, o, null);

        using (var i = new MemoryStream(source))
        using (var o = new MemoryStream())
            withDefaults = PdfAccessibility.AutoTag(i, o, new AutoTagOptions());

        // The fixture really does get tagged, so equality below is between two real results.
        Assert.True(withDefaults.ElementsAdded > 0);
        Assert.Equal(withDefaults.ElementsAdded, withNull.ElementsAdded);
        Assert.Equal(withDefaults.PagesProcessed, withNull.PagesProcessed);
        Assert.Equal(withDefaults.HeadingsByLevel, withNull.HeadingsByLevel);
    }

    [Fact]
    public void AutoTag_ResultToString_ContainsElementsAdded()
    {
        using var input  = new MemoryStream(PdfAccessibilityAuditTests.MinimalPdf());
        using var output = new MemoryStream();
        var result = PdfAccessibility.AutoTag(input, output);
        string s = result.ToString();
        Assert.Contains("AutoTag", s);
    }

    // ---------------------------------------------------------------
    //  AutoTagOptions model
    // ---------------------------------------------------------------

    [Fact]
    public void AutoTagOptions_Defaults_AreValid()
    {
        var opts = new AutoTagOptions();
        Assert.Equal("en-US", opts.Language);
        Assert.Null(opts.Title);
        Assert.Equal(1.8f,  opts.H1Ratio);
        Assert.Equal(1.4f,  opts.H2Ratio);
        Assert.Equal(1.15f, opts.H3Ratio);
        Assert.Equal(6f,    opts.ParagraphGapThreshold);
        Assert.True(opts.TagHeadersFootersAsArtifacts);
        Assert.Equal(50f, opts.HeaderFooterMargin);
    }

    /// <summary>
    /// The heading ratios are the auto-tagger's entire heading vocabulary, so raising them must
    /// demote a line that would otherwise be an H1 down to a plain paragraph. The element count is
    /// unchanged — the same runs are tagged, they are just classified differently — which is what
    /// separates "the ratios were applied" from "the tagger fell over".
    ///
    /// <para>The predecessor ran on a contentless page, where no classification happens at all,
    /// and asserted only that the result was non-null.</para>
    /// </summary>
    [Fact]
    public void AutoTagOptions_HeadingRatios_DecideWhetherALineBecomesAHeading()
    {
        byte[] source = ContentBearingPdfTests.ContentPdf();

        AutoTagResult withDefaults, withUnreachableRatios;
        using (var i = new MemoryStream(source))
        using (var o = new MemoryStream())
            withDefaults = PdfAccessibility.AutoTag(i, o, new AutoTagOptions());

        using (var i = new MemoryStream(source))
        using (var o = new MemoryStream())
            withUnreachableRatios = PdfAccessibility.AutoTag(i, o, new AutoTagOptions
            {
                H1Ratio = 50f, H2Ratio = 40f, H3Ratio = 30f,   // no real font size reaches these
            });

        Assert.Equal(1, withDefaults.HeadingsByLevel[1]);
        Assert.Empty(withUnreachableRatios.HeadingsByLevel);

        // Same runs tagged either way — only the roles differ.
        Assert.Equal(withDefaults.ElementsAdded, withUnreachableRatios.ElementsAdded);
    }

    /// <summary>
    /// The title option must reach the produced document's <c>/Info /Title</c> — a title nobody can
    /// read back is not a title. The old comment claimed "title is set on the PDF" and then checked
    /// only that the call returned something.
    /// </summary>
    [Fact]
    public void AutoTagOptions_WithTitle_WritesItToTheProducedDocument()
    {
        using var input  = new MemoryStream(ContentBearingPdfTests.ContentPdf());
        using var output = new MemoryStream();
        PdfAccessibility.AutoTag(input, output, new AutoTagOptions { Title = "My Document" });

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var info = doc.ResolveObject(doc.Trailer[PdfName.Info]) as PdfDictionary;
        Assert.Equal("My Document",
            (doc.ResolveObject(info?[PdfName.Title]) as PdfString)?.GetText());
    }

    // ---------------------------------------------------------------
    //  AutoTagResult model
    // ---------------------------------------------------------------

    [Fact]
    public void AutoTagResult_Constructor_SetsAllProperties()
    {
        var headings = new System.Collections.Generic.Dictionary<int, int>
            { [1] = 3, [2] = 5 };
        var warnings = new[] { "w1", "w2" };
        var r = new AutoTagResult(
            elementsAdded: 10,
            pagesProcessed: 2,
            headingsByLevel: headings,
            paragraphCount: 7,
            artifactCount: 2,
            warnings: warnings);

        Assert.Equal(10,       r.ElementsAdded);
        Assert.Equal(2,        r.PagesProcessed);
        Assert.Equal(7,        r.ParagraphCount);
        Assert.Equal(2,        r.ArtifactCount);
        Assert.Equal(2,        r.Warnings.Count);
        Assert.Equal(3,        r.HeadingsByLevel[1]);
        Assert.Equal(5,        r.HeadingsByLevel[2]);
    }

    // ---------------------------------------------------------------
    //  Adversarial input: the body-font-size measurement must be bounded
    // ---------------------------------------------------------------

    /// <summary>
    /// Adversarial-input guard for the auto-tagger's body-font-size measurement.
    ///
    /// <para>
    /// <c>ComputeMedianFontSize</c> measures the content stream linearly. A full
    /// <c>PdfOperations.ExtractStructuredText</c> over the whole document, on every AutoTag call,
    /// purely to obtain one number, is superlinear in the amount of text sharing a baseline band
    /// (extraction groups glyphs into lines), so this input — one page, a thousand text-showing
    /// operators whose baselines alternate between two values a couple of points apart, each drawing
    /// a long string — would take over three minutes to tag, and a fallback that fires only when the
    /// extractor throws never would. Growing the string length or the operator count pushes it past ten minutes on a
    /// file well under a megabyte, i.e. an unbounded hang reachable from
    /// <c>PdfAccessibility.RemediateWithReport</c>.
    /// </para>
    ///
    /// <para>
    /// The budget is deliberately enormous relative to the linear measurement (comfortably
    /// sub-second here) and far below the ~3.5 minutes the extraction takes, so it cannot flake
    /// on a slow machine yet still fails hard if the extraction ever comes back.
    /// </para>
    /// </summary>
    [Fact]
    public void AutoTag_ManyRunsOnTwoAlternatingBaselines_IsBounded()
    {
        byte[] pdf = ManyRunsPdf(runCount: 1000, charactersPerRun: 96);

        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();

        var clock = Stopwatch.StartNew();
        AutoTagResult result = PdfAccessibility.AutoTag(input, output);
        clock.Stop();

        Assert.NotNull(result);
        Assert.True(output.Length > 0);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30),
            "AutoTag took " + clock.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) +
            "s on a " + pdf.Length.ToString(CultureInfo.InvariantCulture) + "-byte file. The body "
            + "font size must be measured from the content streams in linear time, not by extracting "
            + "the whole document's structured text.");
    }

    // ---------------------------------------------------------------
    //  A revision that was never written must never be reported as tagged
    // ---------------------------------------------------------------

    /// <summary>
    /// <c>DocumentRevisionWriter.Write</c> never throws: it catches every writer failure,
    /// records it on <c>AccessibilityDiagnostics</c> and leaves the output holding just the
    /// original bytes. Returning the plan's counters unconditionally afterwards would tell a
    /// caller "3 elements, 2 MCIDs, ContentMarked = true" about a file that is byte-for-byte the
    /// untagged input — and the remediation pipeline would then skip the artifact and
    /// reading-order steps as superseded and stamp <c>pdfuaid:part=1</c> onto it.
    /// </summary>
    [Fact]
    public void AutoTag_WhenTheRevisionNeverReachesTheOutput_ReportsFailureNotTaggedContent()
    {
        byte[] input = ContentBearingPdfTests.ContentPdf();

        // Control first: this input really is taggable, so the assertions below are about the
        // write failing rather than about a document that was never going to be tagged.
        using (var healthyIn  = new MemoryStream(input))
        using (var healthyOut = new MemoryStream())
        {
            AutoTagResult ok = PdfAccessibility.AutoTag(healthyIn, healthyOut);
            Assert.True(ok.ContentMarked);
            Assert.True(ok.McidCount > 0);
            Assert.True(healthyOut.Length > input.Length);
        }

        // Now the same tagging run against a sink that accepts exactly the original document and
        // then fails — the shape of a disk filling up while the appended revision is copied out.
        var sink = new FailsAfterStream(input.Length);
        AutoTagResult result;
        using (var failingIn = new MemoryStream(input))
            result = PdfAccessibility.AutoTag(failingIn, sink);

        // What was actually produced is the untagged input, unchanged.
        Assert.Equal(input, sink.Accepted());

        // So the result must say so, rather than describing a revision that does not exist.
        Assert.False(result.ContentMarked);
        Assert.Equal(0, result.ElementsAdded);
        Assert.Equal(0, result.McidCount);
        Assert.Contains(result.Warnings,
            w => w.IndexOf("never reached the output stream", StringComparison.Ordinal) >= 0);
    }

    // ---------------------------------------------------------------
    //  Fixtures
    // ---------------------------------------------------------------

    /// <summary>
    /// Single-page, uncompressed, untagged PDF whose content stream is
    /// <paramref name="runCount"/> separate text objects, each drawing
    /// <paramref name="charactersPerRun"/> characters, with the baseline alternating between
    /// y=400 and y=402 so that every one of them starts its own line group.
    /// </summary>
    private static byte[] ManyRunsPdf(int runCount, int charactersPerRun)
    {
        string text = new string('a', charactersPerRun);

        var content = new StringBuilder(runCount * (charactersPerRun + 32));
        for (int i = 0; i < runCount; i++)
        {
            content.Append("BT /F1 10 Tf 72 ")
                   .Append((i % 2 == 0 ? 400 : 402).ToString(CultureInfo.InvariantCulture))
                   .Append(" Td (").Append(text).Append(") Tj ET\n");
        }

        return SinglePagePdf(content.ToString());
    }

    /// <summary>
    /// Wraps <paramref name="contentStream"/> in a minimal, classic-xref, 612x792 single-page
    /// document with one Type1 font. Built in memory: no file on disk, no corpus dependency.
    /// </summary>
    private static byte[] SinglePagePdf(string contentStream)
    {
        var sb = new StringBuilder(contentStream.Length + 1024);
        sb.Append("%PDF-1.7\n");
        int off1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int off2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int off3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] ")
          .Append("/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        int off4 = sb.Length;
        sb.Append("4 0 obj\n<< /Length ").Append(contentStream.Length.ToString(CultureInfo.InvariantCulture))
          .Append(" >>\nstream\n").Append(contentStream).Append("endstream\nendobj\n");
        int off5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica ")
          .Append("/Encoding /WinAnsiEncoding >>\nendobj\n");
        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off1);
        sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off2);
        sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off3);
        sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off4);
        sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off5);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Write-only sink that accepts the first <c>limit</c> bytes and then throws
    /// <see cref="IOException"/> — a disk filling up part-way through the copy. Everything it
    /// accepted is retained so a test can compare the produced file with the input.
    /// <see cref="CanSeek"/> is false, which is also the harder case for the writer.
    /// </summary>
    private sealed class FailsAfterStream : Stream
    {
        private readonly MemoryStream _accepted = new MemoryStream();
        private readonly long _limit;

        internal FailsAfterStream(long limit) => _limit = limit;

        internal byte[] Accepted() => _accepted.ToArray();

        public override bool CanRead  => false;
        public override bool CanSeek  => false;
        public override bool CanWrite => true;
        public override long Length   => _accepted.Length;

        public override long Position
        {
            get => _accepted.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            // Reject the whole write rather than truncating it: a stream that accepted part of a
            // buffer and then failed would make the test's byte-for-byte comparison ambiguous.
            if (_accepted.Length + count > _limit)
                throw new IOException("There is not enough space on the disk.");

            _accepted.Write(buffer, offset, count);
        }
    }
}
