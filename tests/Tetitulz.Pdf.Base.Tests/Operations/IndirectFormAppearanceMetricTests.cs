// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A <c>/DR</c> font whose <c>/FirstChar</c> is an <b>indirect reference</b> must still measure
/// text correctly when an appearance stream is generated.
/// </summary>
/// <remarks>
/// <para><b>Same defect as the Core font classes, in the writer this time.</b>
/// <c>MeasureSimple</c> read <c>/FirstChar</c> with <c>GetInteger</c> and fell back to <c>0</c> —
/// which is the <b>array origin</b>, not a neutral default. The loop indexes
/// <c>c - firstChar</c>, so a font declaring <c>/FirstChar 32</c> measured every glyph 32 entries
/// early and got a real width belonging to a different one. Nothing downstream can detect it: the
/// measurement is used to place and auto-size the text, so the appearance is <b>generated wrong and
/// baked into the document</b>.</para>
///
/// <para><b>Asserted by comparing the two appearances byte for byte.</b> Both spellings describe the
/// same font, so the generated stream must be identical; under the defect the right-aligned
/// <c>Td</c> offset differs because the measured width does. A floor case fills a different value,
/// proving the comparison distinguishes anything at all rather than comparing two empty
/// streams.</para>
///
/// <para>The <c>/BaseFont</c> is deliberately <b>not</b> one of the standard 14: otherwise
/// <c>MeasureSimple</c> falls through to <c>Standard14Metrics</c> and the <c>/Widths</c> array —
/// and therefore <c>/FirstChar</c> — is never consulted.</para>
/// </remarks>
public class IndirectFormAppearanceMetricTests
{
    /// <summary>
    /// The generated appearance is identical whichever way <c>/FirstChar</c> is spelled. The direct
    /// spelling is the control: it always worked.
    /// </summary>
    [Fact]
    public void RightAlignedField_MeasuresTheSame_WhicheverWayFirstCharIsSpelled()
    {
        string direct = Appearance(WidthsFontForm(indirect: false), "A");
        string indirect = Appearance(WidthsFontForm(indirect: true), "A");

        Assert.Equal(direct, indirect);
    }

    /// <summary>
    /// The floor: a different value really does produce a different appearance, so the equality
    /// above is a claim about the measurement rather than about two empty streams.
    /// </summary>
    [Fact]
    public void ADifferentValue_ProducesADifferentAppearance()
    {
        string a = Appearance(WidthsFontForm(indirect: false), "A");
        string b = Appearance(WidthsFontForm(indirect: false), "AAAA");

        Assert.NotEqual(a, b);
    }

    // ------------------------------------------------------------------

    /// <summary>The decoded <c>/AP /N</c> stream the filler generated for the field.</summary>
    private static string Appearance(byte[] pdf, string value)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();

        PdfFormFiller.Fill(input, output, new Dictionary<string, string> { ["name"] = value },
                           new FormFillOptions());

        using var ms = new MemoryStream(output.ToArray());
        using var doc = PdfDocument.Open(ms);

        var annots = doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Annots]) as PdfArray;
        Assert.NotNull(annots);
        var widget = doc.ResolveObject(annots![0]) as PdfDictionary;
        Assert.NotNull(widget);

        var ap = doc.ResolveObject(widget![PdfName.AP]) as PdfDictionary;
        Assert.NotNull(ap);
        var normal = doc.ResolveObject(ap![PdfName.N]) as PdfStream;
        Assert.NotNull(normal);

        return Encoding.Latin1.GetString(normal!.GetDecodedData());
    }

    /// <summary>
    /// A right-aligned text field whose <c>/DR</c> font carries a real <c>/Widths</c> array and
    /// <c>/FirstChar 32</c>. Object 7 carries that number when <paramref name="indirect"/>.
    /// </summary>
    private static byte[] WidthsFontForm(bool indirect)
    {
        string firstChar = indirect ? "7 0 R" : "32";

        // Index 33 is 'A' when /FirstChar is 32; index 65 is 'A' when it is misread as 0. The two
        // carry deliberately different widths so the measured total cannot coincide.
        var widths = new StringBuilder("[");
        for (int i = 0; i <= 100; i++)
            widths.Append(' ').Append(i == 33 ? "1000" : (i == 65 ? "100" : "500"));
        widths.Append(" ]");

        return Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 3 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
            "<< /Fields [6 0 R] /DR << /Font << /Helv 5 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [6 0 R] >>",
            // NOT a standard-14 base font: otherwise the metrics fallback hides /Widths entirely.
            "<< /Type /Font /Subtype /Type1 /BaseFont /CustomSans /Encoding /WinAnsiEncoding" +
            " /FirstChar " + firstChar + " /LastChar 132 /Widths 8 0 R >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /Rect [100 700 300 720]" +
            " /Q 2 /DA (/Helv 12 Tf 0 g) >>",
            "32",
            widths.ToString());
    }

    private static byte[] Build(params string[] bodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
