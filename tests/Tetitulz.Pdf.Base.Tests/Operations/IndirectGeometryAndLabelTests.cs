// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Base.Operations.Structure;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Three reads whose null yields a <b>plausible wrong value</b> rather than a missing one — a
/// widget at the page origin, a field with no appearance, and a page numbered from 1 that the
/// document numbers otherwise.
/// </summary>
/// <remarks>
/// <para><b><c>/Rect</c> in <c>PdfFormReader</c> reports every widget of the field at (0, 0) with
/// zero size</b> — indistinguishable from a genuinely degenerate widget. <c>GetFields</c> is a
/// documented public entry point, so a caller drawing an overlay, hit-testing a click or exporting
/// field geometry places the field wrongly and has no way to know.</para>
///
/// <para><b><c>/Rect</c> in <c>PdfFormFiller</c> is the same read with a worse ending.</b> (0, 0)
/// trips <c>FormAppearance.Build</c>'s first guard — "the widget rectangle is empty, so there is
/// nowhere to draw the value" — so <c>/V</c> <i>is</i> written, no appearance is generated, and
/// <c>/NeedAppearances</c> is not set for it either. The value is invisible in every viewer that
/// honours appearance streams, and the field lands in <c>NoAppearanceList</c> as though the
/// document were at fault.</para>
///
/// <para><b>Each <c>/PageLabels</c> entry fails differently.</b> The label dictionaries reached
/// through the number tree are resolved; their entries were not. No <c>/S</c> means §12.4.2's "no
/// numbering style", so front matter is reported unlabelled instead of <c>i, ii, iii</c>; no
/// <c>/P</c> loses the prefix; no <c>/St</c> silently restarts at 1. A caller navigating or
/// printing by the <i>printed</i> page number then addresses the wrong page.</para>
/// </remarks>
public class IndirectGeometryAndLabelTests
{
    /// <summary>
    /// The widget's rectangle is reported whichever way <c>/Rect</c> is spelled. <c>false</c> is
    /// the control.
    /// </summary>
    [Theory]
    [InlineData(false)]  // /Rect [100 200 300 240]
    [InlineData(true)]   // /Rect 9 0 R
    public void TheWidgetRectangleIsReported_WhicheverWayRectIsSpelled(bool indirect)
    {
        using var ms = new MemoryStream(FormPdf(indirect));
        IReadOnlyList<PdfFormFieldInfo> fields = PdfFormReader.GetFields(ms);

        var field = Assert.Single(fields);
        var widget = Assert.Single(field.Widgets);

        Assert.Equal(100, widget.X, 3);
        Assert.Equal(200, widget.Y, 3);
        Assert.Equal(200, widget.Width, 3);
        Assert.Equal(40, widget.Height, 3);
    }

    /// <summary>
    /// Filling the field produces an appearance stream whichever way <c>/Rect</c> is spelled.
    /// </summary>
    /// <remarks>
    /// Asserted on the appearance <i>and</i> on the absence of the "no appearance" report, because
    /// the two halves of the failure are separable: the value is written either way, so a test that
    /// only checked <c>/V</c> passes with the defect intact.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FillingProducesAnAppearance_WhicheverWayRectIsSpelled(bool indirect)
    {
        using var input = new MemoryStream(FormPdf(indirect));
        using var output = new MemoryStream();

        var result = PdfFormFiller.Fill(input, output,
            new Dictionary<string, string> { ["Text1"] = "hello" },
            new FormFillOptions());

        Assert.Empty(result.FieldsWithoutAppearance);

        // …and the appearance really is on the widget. The report and the artefact are separable:
        // an empty FieldsWithoutAppearance would also be produced by a filler that stopped
        // reporting, so the output is asserted too.
        output.Position = 0;
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(output);
        var annots = doc.ResolveObject(doc.Pages[0].Dictionary[Tetitulz.Pdf.Core.Objects.PdfName.Annots])
                     as Tetitulz.Pdf.Core.Objects.PdfArray;
        var widget = doc.ResolveObject(annots![0]) as Tetitulz.Pdf.Core.Objects.PdfDictionary;
        var ap = doc.ResolveObject(widget![Tetitulz.Pdf.Core.Objects.PdfName.AP])
                 as Tetitulz.Pdf.Core.Objects.PdfDictionary;

        Assert.NotNull(ap);
        Assert.NotNull(doc.ResolveObject(ap![Tetitulz.Pdf.Core.Objects.PdfName.N]));
    }

    /// <summary>
    /// The page label's style, prefix and start value are read whichever way they are spelled.
    /// </summary>
    [Theory]
    [InlineData(false)]  // /S /r  /P (A-)  /St 5
    [InlineData(true)]   // all three indirect
    public void ThePageLabelIsRead_WhicheverWayItIsSpelled(bool indirect)
    {
        using var ms = new MemoryStream(PageLabelPdf(indirect));
        IReadOnlyList<PageLabelRange> ranges = PdfPageLabels.GetLabels(ms);

        var range = Assert.Single(ranges);
        Assert.Equal("r", range.Style);
        Assert.Equal("A-", range.Prefix);
        Assert.Equal(5, range.StartValue);
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// A form with one text field whose widget is 200x40 at (100, 200). Object 9 holds the
    /// <c>/Rect</c> array when <paramref name="indirect"/> — the only difference.
    /// </summary>
    private static byte[] FormPdf(bool indirect)
    {
        string rect = indirect ? "9 0 R" : "[100 200 300 240]";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /DA (/Helv 0 Tf 0 g)"
                + " /DR << /Font << /Helv 6 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R"
                + " /Annots [5 0 R] >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Text1) /Rect " + rect
                + " /F 4 /P 3 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Name /Helv >>",
            "null", "null",
            "[100 200 300 240]");
    }

    /// <summary>
    /// A catalog with one page-label range. Objects 9, 10 and 11 hold its three entries when
    /// <paramref name="indirect"/>.
    /// </summary>
    private static byte[] PageLabelPdf(bool indirect)
    {
        string s = indirect ? "9 0 R" : "/r";
        string p = indirect ? "10 0 R" : "(A-)";
        string st = indirect ? "11 0 R" : "5";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R /PageLabels << /Nums [0 << /S " + s
                + " /P " + p + " /St " + st + " >>] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "null", "null", "null", "null",
            "/r",
            "(A-)",
            "5");
    }

    /// <summary>Assembles objects 1..n with a correct xref table. Bodies must be ASCII.</summary>
    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
