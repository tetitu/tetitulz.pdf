// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The per-page annotation ceiling is <see cref="PdfLimits.MaxAnnotationsPerPage"/>, read by both
/// extractors, and a truncated read reaches the autolinker's caller.
/// </summary>
/// <remarks>
/// <para><b>Two constants said they matched.</b> <c>PdfAnnotationExtractor.MaxAnnotationsPerPage</c>
/// and a private copy in <c>PdfLinkExtractor</c> were both 50,000, and the first one's summary said
/// it matched the second. Neither could be set, so a host that needed a smaller ceiling had none,
/// and a test could only reach the truncation with a page of fifty thousand annotations.</para>
/// <para><b>The autolinker read the link list and ignored its truncation report.</b> With
/// <c>SkipAlreadyLinked</c>, a link past the ceiling is one it does not know exists, so it links
/// the text again under it and says nothing.</para>
/// </remarks>
[Collection(TestInfrastructure.PdfLimitsCollection.Name)]
public class AnnotationCeilingTests
{
    [Fact]
    public void BothExtractorsStopAtTheSettableCeilingAndSaySo()
    {
        byte[] pdf = PageWithLinks("Plain text only");

        try
        {
            PdfLimits.MaxAnnotationsPerPage = 1;

            Assert.Single(PdfLinkExtractor.ExtractLinks(new MemoryStream(pdf)));
            Assert.Contains("incomplete", PdfLinkExtractor.LastError ?? "", StringComparison.Ordinal);

            Assert.Single(PdfAnnotationExtractor.ListAnnotations(new MemoryStream(pdf)));
            Assert.Contains("incomplete", PdfAnnotationExtractor.LastError ?? "", StringComparison.Ordinal);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    /// <summary>The control: at the default ceiling both see both annotations and report nothing.</summary>
    [Fact]
    public void AtTheDefaultCeilingNothingIsCut()
    {
        byte[] pdf = PageWithLinks("Plain text only");

        Assert.Equal(2, PdfLinkExtractor.ExtractLinks(new MemoryStream(pdf)).Count);
        Assert.Null(PdfLinkExtractor.LastError);
        Assert.Equal(2, PdfAnnotationExtractor.ListAnnotations(new MemoryStream(pdf)).Count);
        Assert.Null(PdfAnnotationExtractor.LastError);
    }

    /// <summary>
    /// When the existing links could not all be read, the autolinker says so, because its
    /// "already linked" check was made against a partial list.
    /// </summary>
    [Fact]
    public void TheAutoLinkerReportsThatItsExistingLinkListWasCut()
    {
        byte[] pdf = PageWithLinks("Visit https://example.com/a today");

        try
        {
            PdfLimits.MaxAnnotationsPerPage = 1;

            var found = PdfAutoLinker.FindLinks(new MemoryStream(pdf));

            // The link over the URL is the second annotation, past the ceiling, so the URL looks
            // unlinked. That is the documented best-effort answer, and the slot says why.
            Assert.Equal("https://example.com/a", Assert.Single(found).Uri);
            Assert.NotNull(PdfAutoLinker.LastExistingLinksError);

            // Each entry point clears the slot first, before its argument check, so even a call
            // that throws cannot leave the previous document's answer standing.
            Assert.Throws<ArgumentNullException>(() => PdfAutoLinker.AddLinks(null, new MemoryStream()));
            Assert.Null(PdfAutoLinker.LastExistingLinksError);

            PdfAutoLinker.FindLinks(new MemoryStream(pdf));
            Assert.NotNull(PdfAutoLinker.LastExistingLinksError);
            Assert.Throws<ArgumentNullException>(() => PdfAutoLinker.FindLinks(null));
            Assert.Null(PdfAutoLinker.LastExistingLinksError);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    /// <summary>
    /// The control: with every existing link read, the URL is already linked, nothing is found and
    /// nothing is reported.
    /// </summary>
    [Fact]
    public void WithEveryExistingLinkReadTheLinkedUrlIsSkippedAndNothingIsReported()
    {
        byte[] pdf = PageWithLinks("Visit https://example.com/a today");

        Assert.Empty(PdfAutoLinker.FindLinks(new MemoryStream(pdf)));
        Assert.Null(PdfAutoLinker.LastExistingLinksError);
    }

    /// <summary>
    /// One page of Helvetica text at (40, 700) and two links: one in the corner, then one over the
    /// whole text line.
    /// </summary>
    private static byte[] PageWithLinks(string text)
    {
        string content = "BT /F1 12 Tf 40 700 Td (" + text + ") Tj ET";
        return ActionTargetAgreementTests.Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
            + "/Resources << /Font << /F1 5 0 R >> >> /Annots [6 0 R 7 0 R] >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A << /S /URI /URI (https://corner.test) >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [30 690 400 720] /A << /S /URI /URI (https://example.com/a) >> >>");
    }
}
