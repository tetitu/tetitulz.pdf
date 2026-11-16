// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Accessibility.Checking.Ua2;
using Tetitulz.Pdf.Accessibility.Tagging;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The Accessibility feature's two <c>/F</c> decisions read it as a flag word, through
/// <c>PdfAnnotationFlags.FromNumber</c>: which annotations the auto-tagger puts in the structure
/// tree, and which the PDF/UA-2 checker requires to be artifacts.
/// </summary>
/// <remarks>
/// Each decision tests different bits, so each needs its own separating value. The old reads went
/// through <c>IntValue</c> (saturating) or truncated a real.
/// </remarks>
public class AnnotationFlagWordTests
{
    /// <summary>
    /// A hidden annotation stays out of the structure tree. <c>6.5</c> is not a flag word, so it is
    /// the default, 0, and belongs — the old read truncated it to Hidden and left it out.
    /// <c>2147483652</c> is Print plus bit 32 — the old read saturated it to Hidden.
    /// </summary>
    [Theory]
    [InlineData("6.5", true)]
    [InlineData("2147483652", true)]
    [InlineData("4", true)]      // Print alone — the control that it can belong
    [InlineData("2", false)]     // Hidden — the control that it can be left out
    public void TheTaggerLeavesOutOnlyAnnotationsThatAreReallyHidden(string flags, bool belongs)
    {
        var (doc, annot) = Annotation(flags);
        using (doc)
            Assert.Equal(belongs, PdfAutoTagger.BelongsInStructureTree(doc, annot, "Link", cropBox: null));
    }

    /// <summary>
    /// ISO 14289-2 requires an Invisible annotation, or a NoView one without ToggleNoView, to be an
    /// artifact. <c>33.5</c> is not a flag word — the old read truncated it to Invisible and NoView.
    /// <c>2147483652</c> is Print plus bit 32 — the old read saturated it to every flag, Invisible
    /// among them.
    /// </summary>
    [Theory]
    [InlineData("33.5", false)]
    [InlineData("2147483652", false)]
    [InlineData("1", true)]      // Invisible — the control that it can be required
    [InlineData("4", false)]     // Print — the control that it can be exempt
    public void TheUa2CheckerRequiresAnArtifactOnlyForTheFlagsThatSaySo(string flags, bool required)
    {
        var (doc, annot) = Annotation(flags);
        using (doc)
            Assert.Equal(required, PdfUa2StructureChecker.IsRequiredArtifactByFlags(doc, annot));
    }

    private static (PdfDocument Doc, PdfDictionary Annot) Annotation(string flags)
    {
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Link /Rect [10 10 60 60] /F " + flags + " >>",
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");

        var doc = PdfDocument.Open(new MemoryStream(Encoding.ASCII.GetBytes(sb.ToString())));
        var annots = (PdfArray)doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Annots])!;
        return (doc, (PdfDictionary)doc.ResolveObject(annots[0])!);
    }
}
