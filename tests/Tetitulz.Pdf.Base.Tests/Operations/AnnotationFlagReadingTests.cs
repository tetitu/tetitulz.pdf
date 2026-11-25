// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Annotations;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Every reader of an annotation's <c>/F</c> in Core and Base gives the same answer, through
/// <see cref="PdfAnnotationFlags.FromNumber"/>.
/// </summary>
/// <remarks>
/// <para><b>Two fixture values, each chosen to separate one kind of old reader from the rule.</b>
/// <c>/F 6.5</c>: every old reader truncated it to 6 — Hidden and Print — and hid the annotation;
/// a number that is not an integer is not a flag word, so it now reads as the default, 0.
/// <c>/F 2147483652</c> — Print plus bit 32, the unsigned spelling: the readers over
/// <c>IntValue</c> saturated it to every named flag and hid the annotation; its low 32 bits say
/// Print.</para>
///
/// <para><b>The readers are asserted together</b>, because each was internally consistent and the
/// defect was that they disagreed.</para>
/// </remarks>
public class AnnotationFlagReadingTests
{
    private const string NonInteger = "6.5";
    private const string PrintPlusBit32 = "2147483652";

    [Theory]
    [InlineData(NonInteger, 0)]
    [InlineData(PrintPlusBit32, int.MinValue | PdfAnnotationFlags.Print)]
    public void EveryReaderInCoreAndBaseReadsTheSameFlagWord(string written, int expected)
    {
        byte[] pdf = OneAnnotation("/F " + written);

        // Base's extractor.
        PdfAnnotationInfo listed = Assert.Single(PdfAnnotationExtractor.ListAnnotations(new MemoryStream(pdf)));
        Assert.Equal(expected, listed.Flags);

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        PdfDictionary annot = Annotation(doc);

        // Core's annotation model.
        Assert.Equal(expected, PdfAnnotation.Create(annot, doc).AnnotationFlags);

        // The FDF/XFDF annotation map. A /F that is not a flag word leaves Flags unset, which is
        // how the map says "absent".
        int? mapped = AnnotationDictionaryMap.FromDictionary(annot, doc.ResolveObject).Flags;
        Assert.Equal(expected == 0 ? (int?)null : expected, mapped);
    }

    /// <summary>
    /// The verdict two readings of the flags could differ on: neither fixture is hidden on screen. The non-integer one is
    /// hidden in print, since its default of 0 leaves Print clear; the Print one prints.
    /// </summary>
    [Theory]
    [InlineData(NonInteger, false, true)]
    [InlineData(PrintPlusBit32, false, false)]
    public void TheRenderersVisibilityRuleReadsTheSameWord(string written, bool hiddenOnScreen, bool hiddenInPrint)
    {
        using var doc = PdfDocument.Open(new MemoryStream(OneAnnotation("/F " + written)));
        PdfDictionary annot = Annotation(doc);

        Assert.Equal(hiddenOnScreen, AnnotationVisibility.IsHidden(annot, doc, new PdfRenderOptions { Intent = RenderIntent.Screen }));
        Assert.Equal(hiddenInPrint, AnnotationVisibility.IsHidden(annot, doc, new PdfRenderOptions { Intent = RenderIntent.Print }));
    }

    /// <summary>
    /// The flattener refuses to flatten a hidden annotation, because that would make invisible
    /// content visible. Neither fixture is hidden, so both are flattened — and a control whose
    /// <c>/F</c> really is Hidden is still refused.
    /// </summary>
    [Theory]
    [InlineData("/F " + NonInteger, true)]
    [InlineData("/F " + PrintPlusBit32, true)]
    [InlineData("/F 2", false)]
    public void TheFlattenerReadsTheSameWord(string flags, bool flattened)
    {
        using var output = new MemoryStream();
        PdfAnnotationEditor.AnnotationEditResult result =
            PdfAnnotationEditor.Flatten(new MemoryStream(OneAnnotation(flags)), output);

        Assert.Equal(flattened ? 1 : 0, result.Affected);
        // The refusal names its reason, so a 0 above is the hidden rule and not some other failure.
        if (!flattened) Assert.Contains(result.Warnings, w => w.Contains("hidden", StringComparison.Ordinal));
    }

    private static PdfDictionary Annotation(PdfDocument doc)
    {
        var annots = (PdfArray)doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Annots])!;
        return (PdfDictionary)doc.ResolveObject(annots[0])!;
    }

    /// <summary>One page, one Square annotation with a small appearance, and the caller's <c>/F</c>.</summary>
    private static byte[] OneAnnotation(string flagsEntry) => FormPdfFixtures.Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Annots [5 0 R] >>",
        "<< /Length 0 >>\nstream\n\nendstream",
        "<< /Type /Annot /Subtype /Square /Rect [100 100 200 200] " + flagsEntry + " /AP << /N 6 0 R >> >>",
        "<< /Type /XObject /Subtype /Form /BBox [0 0 100 100] /Length 20 >>\nstream\n0 0 100 100 re f\nendstream");
}
