// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// The <c>/V</c> leak that <see cref="SplitAcroFormFieldScopeTests"/> closed, arriving through the
/// other channel: a hybrid XFA form keeps its values in an <b>XML</b> packet on
/// <c>/AcroForm /XFA</c>, so carried along, extracting page 1 would hand the recipient the answers
/// written on page 2 in <c>&lt;xfa:datasets&gt;</c> even after the field filter had correctly
/// withheld the field objects.
/// </summary>
/// <remarks>
/// <para>Every leak assertion is against the <b>whole output byte array</b>, for the same reason the
/// field-scope suite gives: an object written but unreferenced parses as nothing and reads
/// perfectly well with any object dumper, which is the exact shape of this defect.</para>
/// <para>The packet is dropped rather than pruned. <c>PdfSplitter.LastXfaWarning</c> carries the
/// reason, and the tests below pin both halves of the trade — the leak is gone <i>and</i> the
/// AcroForm fields the extract does keep are untouched.</para>
/// </remarks>
public class SplitXfaPacketTests
{
    private const string KeptAnswer = "KEPT-ANSWER";
    private const string SecretAnswer = "SECRET-XFA-ANSWER";

    [Fact]
    public void SplitRange_XfaDatasets_DoNotTravelWithTheExtract()
    {
        byte[] extracted = SplitToBytes(TwoPageXfaFormPdf(), 1, 1);

        // The datasets packet held the dropped page's answer. Not one byte of it may survive.
        Assert.DoesNotContain(SecretAnswer, Ascii(extracted), StringComparison.Ordinal);
        Assert.DoesNotContain("xfa:datasets", Ascii(extracted), StringComparison.Ordinal);
    }

    [Fact]
    public void SplitRange_XfaKey_IsRemovedFromTheExtractedAcroForm()
    {
        // Leaving the key while dropping the packet would dangle; dropping the packet while
        // collecting its streams would leave them in the file as orphans. Both halves are asserted:
        // the key is gone here, the bytes are gone above.
        using var doc = Split(TwoPageXfaFormPdf(), 1, 1);

        var form = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]) as PdfDictionary;
        Assert.NotNull(form);
        Assert.Null(form![PdfName.XFA]);
    }

    [Fact]
    public void SplitRange_DroppingTheXfaPacket_IsReported()
    {
        // A packet that vanishes with no trace is the failure mode this repository keeps finding:
        // the caller cannot tell "the source had no XFA" from "the extract could not carry it".
        SplitToBytes(TwoPageXfaFormPdf(), 1, 1);

        Assert.NotNull(PdfSplitter.LastXfaWarning);
        Assert.Contains("/XFA", PdfSplitter.LastXfaWarning!, StringComparison.Ordinal);
    }

    [Fact]
    public void SplitRange_FormWithNoXfa_ReportsNothing()
    {
        // The control. A warning that fired on every form is one a caller learns to ignore — and it
        // is also what a "fix" that always reports would look like.
        SplitToBytes(NoXfaFormPdf(), 1, 1);

        Assert.Null(PdfSplitter.LastXfaWarning);
    }

    [Fact]
    public void SplitRange_TheAcroFormFieldsThemselves_AreUnaffectedByTheXfaDrop()
    {
        // The honest negative: dropping /XFA must cost the extract nothing else. A "fix" that threw
        // the whole form away would pass every assertion above.
        using var doc = Split(TwoPageXfaFormPdf(), 1, 1);

        var form = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]) as PdfDictionary;
        var fields = doc.ResolveObject(form![PdfName.Fields]) as PdfArray;
        Assert.NotNull(fields);
        Assert.Single(fields!);

        var field = doc.ResolveObject(fields![0]) as PdfDictionary;
        Assert.Equal("Kept", Text(doc, field![PdfName.T]));
        Assert.Equal(KeptAnswer, Text(doc, field[PdfName.V]));
        Assert.Equal("/Helv 10 Tf 0 g", Text(doc, form[PdfName.DA]));
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static byte[] SplitToBytes(byte[] source, int startPage, int endPage)
    {
        using var input = new MemoryStream(source);
        using var output = new MemoryStream();
        PdfSplitter.SplitRange(input, output, startPage, endPage);
        return output.ToArray();
    }

    private static PdfDocument Split(byte[] source, int startPage, int endPage) =>
        PdfDocument.Open(new MemoryStream(SplitToBytes(source, startPage, endPage)));

    private static string Ascii(byte[] bytes) => Encoding.ASCII.GetString(bytes);

    private static string Text(PdfDocument doc, PdfObject obj) =>
        doc.ResolveObject(obj) is PdfString s ? Encoding.ASCII.GetString(s.RawBytes) : null;

    // ------------------------------------------------------------------
    //  Fixtures
    // ------------------------------------------------------------------

    private const string PageContent = "BT /F1 12 Tf 72 700 Td (hi) Tj ET";

    private static string Contents(string text) =>
        $"<< /Length {text.Length} >>\nstream\n{text}\nendstream";

    /// <summary>
    /// §12.7.8's array form: alternating packet name and stream. Uncompressed, so the leak
    /// assertions can look for the answer in the output bytes directly.
    /// </summary>
    private static readonly string XfaDatasets =
        "<xfa:datasets xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\">" +
        "<xfa:data><form><Kept>" + KeptAnswer + "</Kept>" +
        "<Dropped>" + SecretAnswer + "</Dropped></form></xfa:data></xfa:datasets>";

    // The field-scope suite's two-page form, plus an /XFA packet whose datasets carry BOTH answers.
    private static byte[] TwoPageXfaFormPdf() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /AcroForm 9 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
        "/Resources << >> /Annots [7 0 R] >>",
        Contents(PageContent),
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R " +
        "/Resources << >> /Annots [8 0 R] >>",
        Contents(PageContent),
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Kept) /V (" + KeptAnswer + ") " +
        "/Rect [72 700 200 720] /P 3 0 R >>",
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Dropped) /V (" + SecretAnswer + ") " +
        "/Rect [72 700 200 720] /P 5 0 R >>",
        "<< /Fields [7 0 R 8 0 R] /DA (/Helv 10 Tf 0 g) " +
        "/DR << /Font << /Helv 10 0 R >> >> /NeedAppearances true " +
        "/XFA [(datasets) 11 0 R] >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        Contents(XfaDatasets),
    });

    // The same document with the /XFA entry and its stream removed.
    private static byte[] NoXfaFormPdf() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /AcroForm 9 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
        "/Resources << >> /Annots [7 0 R] >>",
        Contents(PageContent),
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R " +
        "/Resources << >> /Annots [8 0 R] >>",
        Contents(PageContent),
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Kept) /V (" + KeptAnswer + ") " +
        "/Rect [72 700 200 720] /P 3 0 R >>",
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Dropped) /V (" + SecretAnswer + ") " +
        "/Rect [72 700 200 720] /P 5 0 R >>",
        "<< /Fields [7 0 R 8 0 R] /DA (/Helv 10 Tf 0 g) " +
        "/DR << /Font << /Helv 10 0 R >> >> /NeedAppearances true >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
    });

    private static byte[] Assemble(List<string> bodies)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.4\n");
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int size = bodies.Count + 1;
        int xref = sb.Length;
        sb.Append($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {size} /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
