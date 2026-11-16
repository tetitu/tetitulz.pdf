// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// What the producers write is not localised. The analyzer pre-pass of 2026-10-24 listed 363 sites
/// formatting without a culture, every one reaching the file through <c>PdfRealFormat</c> or a
/// formatter of its own, so the class is held by running rather than by reading each: the SVG of a
/// page with negative coordinates and the appearance stream of a form field are the same bytes
/// under <c>sv-SE</c>, whose minus sign is U+2212, and <c>de-DE</c>, whose decimal separator is a
/// comma, as under the invariant culture. (The HTML engine has <c>HtmlCssCultureTests</c>; the
/// Hebrew invoice parser reads U+2212 deliberately.)
/// </summary>
public sealed class ProducersUnderAForeignCultureTests
{
    [Theory]
    [InlineData("sv-SE")]
    [InlineData("de-DE")]
    public void TheSvgAndTheFormAppearance_AreTheSameBytesUnderTheCulture(string cultureName)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        // The culture is the test's premise: a runtime whose sv-SE has no U+2212 minus would pass vacuously.
        if (cultureName == "sv-SE") Assert.Equal("−", culture.NumberFormat.NegativeSign);
        if (cultureName == "de-DE") Assert.Equal(",", culture.NumberFormat.NumberDecimalSeparator);

        string invariantSvg = Under(CultureInfo.InvariantCulture, Svg);
        string foreignSvg = Under(culture, Svg);
        string invariantAppearance = Under(CultureInfo.InvariantCulture, FieldAppearance);
        string foreignAppearance = Under(culture, FieldAppearance);

        // Potency: the fixture's negative coordinates and fractions reached the outputs.
        Assert.Contains("-12.5", invariantSvg, StringComparison.Ordinal);
        Assert.Contains("-30.25", invariantAppearance, StringComparison.Ordinal);
        Assert.Contains(" Tf", invariantAppearance, StringComparison.Ordinal);

        Assert.Equal(invariantSvg, foreignSvg);
        Assert.Equal(invariantAppearance, foreignAppearance);
    }

    private static T Under<T>(CultureInfo culture, Func<T> produce)
    {
        var saved = (Thread.CurrentThread.CurrentCulture, Thread.CurrentThread.CurrentUICulture);
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        try { return produce(); }
        finally
        {
            Thread.CurrentThread.CurrentCulture = saved.CurrentCulture;
            Thread.CurrentThread.CurrentUICulture = saved.CurrentUICulture;
        }
    }

    private static string Svg()
    {
        using var ms = new MemoryStream(Page(), writable: false);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    /// <summary>
    /// The revision the form builder appends for a text field at fractional and negative positions —
    /// its <c>/Rect</c>, its <c>/DA</c>, its appearance stream, every number the builder wrote — as
    /// the bytes after the original file, plus the appearance stream decoded.
    /// </summary>
    private static string FieldAppearance()
    {
        byte[] page = Page();
        using var output = new MemoryStream();
        PdfFormFieldBuilder.AddFields(new MemoryStream(page, writable: false), output, new List<FormFieldBase>
        {
            new FormTextField { FieldName = "t", PageIndex = 0, X = 20.5, Y = -30.25, Width = 150.5, Height = 18.75, DefaultValue = "value", FontSize = 9.5 },
        });
        byte[] written = output.ToArray();
        string revision = Encoding.Latin1.GetString(written, page.Length, written.Length - page.Length);

        using var doc = PdfDocument.Open(new MemoryStream(written, writable: false));
        var annots = Assert.IsType<PdfArray>(doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Annots]));
        foreach (var entry in annots)
        {
            if (doc.ResolveObject(entry) is PdfDictionary annot
                && doc.ResolveObject(annot[PdfName.AP]) is PdfDictionary ap
                && doc.ResolveObject(ap[PdfName.N]) is PdfStream n)
                return revision + "\n--appearance--\n" + Encoding.Latin1.GetString(n.GetDecodedData());
        }
        throw new InvalidOperationException("the field's appearance was not written");
    }

    /// <summary>One page drawing a stroked path through negative and fractional coordinates, and a line of text.</summary>
    private static byte[] Page()
    {
        const string content = "q 1.5 w -12.5 -7.25 m 100 50.5 l 0.5 0.25 0 RG S Q\n" +
                               "BT /F1 12 Tf -3.5 700.5 Td (Shown) Tj ET\n";
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        return FormPdfFixtures.Build(bodies);
    }
}
