// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Accessibility;
using Tetitulz.Pdf.Accessibility.Models;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>Tests for PdfAccessibility.MarkHeadersAndFooters.</summary>
public class PdfArtifactMarkerTests
{
    // ---------------------------------------------------------------
    //  Null-guard tests
    // ---------------------------------------------------------------

    [Fact]
    public void MarkHeadersAndFooters_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PdfAccessibility.MarkHeadersAndFooters(null, new MemoryStream()));
    }

    [Fact]
    public void MarkHeadersAndFooters_NullOutput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PdfAccessibility.MarkHeadersAndFooters(
                new MemoryStream(PdfAccessibilityAuditTests.MinimalPdf()), null));
    }

    // ---------------------------------------------------------------
    //  Basic behavior
    // ---------------------------------------------------------------

    /// <summary>
    /// A page with no text has no running header or footer, so nothing is marked and the document
    /// must come back <b>byte for byte</b> — the invariant that a reported count above zero implies
    /// the output differs from the input, checked from the zero end. The predecessor asserted only
    /// that bytes came back, which an appended empty revision also satisfies.
    /// </summary>
    [Fact]
    public void MarkHeadersAndFooters_NothingToMark_ReportsZeroAndLeavesTheBytesAlone()
    {
        byte[] source = PdfAccessibilityAuditTests.MinimalPdf();

        using var input  = new MemoryStream(source);
        using var output = new MemoryStream();
        ArtifactMarkResult result = PdfAccessibility.MarkHeadersAndFooters(input, output);

        Assert.Equal(0, result.ArtifactsMarked);
        Assert.Equal(source, output.ToArray());
    }

    [Fact]
    public void MarkHeadersAndFooters_MinimalPdf_ZeroArtifactsMarked()
    {
        // No extractable text in minimal PDF → 0 header/footer lines detected
        using var input  = new MemoryStream(PdfAccessibilityAuditTests.MinimalPdf());
        using var output = new MemoryStream();
        var result = PdfAccessibility.MarkHeadersAndFooters(input, output);
        Assert.Equal(0, result.ArtifactsMarked);
    }

    /// <summary>Same passthrough invariant on a document that already carries a structure tree.</summary>
    [Fact]
    public void MarkHeadersAndFooters_TaggedDocumentWithNothingToMark_LeavesTheBytesAlone()
    {
        byte[] source = PdfAccessibilityAuditTests.TaggedPdf();

        using var input  = new MemoryStream(source);
        using var output = new MemoryStream();
        ArtifactMarkResult result = PdfAccessibility.MarkHeadersAndFooters(input, output);

        Assert.Equal(0, result.ArtifactsMarked);
        Assert.Equal(source, output.ToArray());
    }

    /// <summary>
    /// The margins decide what counts as a running header or footer, so they must change the
    /// outcome on a page that actually draws text: a normal band marks artifacts, a zero band
    /// marks none.
    ///
    /// <para>The predecessor passed custom margins to a <b>contentless</b> fixture — where nothing
    /// can ever be marked, whatever the margins — and asserted only that bytes came back.</para>
    /// </summary>
    [Fact]
    public void MarkHeadersAndFooters_Margins_DecideWhatCountsAsAHeaderOrFooter()
    {
        byte[] source = ContentBearingPdfTests.ContentPdf();

        using (var i = new MemoryStream(source))
        using (var o = new MemoryStream())
        {
            var normal = PdfAccessibility.MarkHeadersAndFooters(
                i, o, headerMarginPt: 50f, footerMarginPt: 50f);

            Assert.True(normal.ArtifactsMarked > 0,
                "a 50pt band over a content-bearing page should mark at least one artifact");
            Assert.NotEqual(source, o.ToArray());
        }

        using (var i = new MemoryStream(source))
        using (var o = new MemoryStream())
        {
            var none = PdfAccessibility.MarkHeadersAndFooters(
                i, o, headerMarginPt: 0f, footerMarginPt: 0f);

            Assert.Equal(0, none.ArtifactsMarked);
            Assert.Equal(source, o.ToArray());
        }
    }

    /// <summary>
    /// Bands wide enough to cover the whole page would mark <b>every</b> element on it as an
    /// artifact — which hides the page's real content from assistive technology entirely. The
    /// marker refuses that page and says so rather than doing as it was told.
    ///
    /// <para>This is the same principle as the rest of the feature (never claim, never destroy on
    /// insufficient evidence) and it had no test at all.</para>
    /// </summary>
    [Fact]
    public void MarkHeadersAndFooters_BandsCoveringTheWholePage_AreRefusedWithAReason()
    {
        byte[] source = ContentBearingPdfTests.ContentPdf();

        using var input  = new MemoryStream(source);
        using var output = new MemoryStream();
        var result = PdfAccessibility.MarkHeadersAndFooters(
            input, output, headerMarginPt: 400f, footerMarginPt: 400f);

        Assert.Equal(0, result.ArtifactsMarked);
        Assert.Equal(source, output.ToArray());
        Assert.Contains("cover the whole", PdfAccessibility.LastDiagnostic ?? "");
    }

    [Fact]
    public void MarkHeadersAndFooters_ZeroMargins_ProducesOutput()
    {
        using var input  = new MemoryStream(PdfAccessibilityAuditTests.MinimalPdf());
        using var output = new MemoryStream();
        // Zero margins = nothing qualifies as header/footer
        var result = PdfAccessibility.MarkHeadersAndFooters(
            input, output, headerMarginPt: 0f, footerMarginPt: 0f);
        Assert.Equal(0, result.ArtifactsMarked);
        Assert.True(output.Length > 0);
    }

    // ---------------------------------------------------------------
    //  ArtifactMarkResult model
    // ---------------------------------------------------------------

    [Fact]
    public void ArtifactMarkResult_Constructor_SetsProperty()
    {
        var r = new ArtifactMarkResult(7);
        Assert.Equal(7, r.ArtifactsMarked);
    }

    [Fact]
    public void ArtifactMarkResult_Zero_NoArtifacts()
    {
        var r = new ArtifactMarkResult(0);
        Assert.Equal(0, r.ArtifactsMarked);
    }
}
