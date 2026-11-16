// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// The merged <c>/AcroForm</c> does not carry a source's <c>/XFA</c> packet, and the caller is told
/// so.
/// </summary>
/// <remarks>
/// <para><b>The splitter's problem in the other direction.</b> <c>PdfSplitter.LastXfaWarning</c>
/// explains why a <i>page subset</i> is not a form XFA can describe; a page <i>superset</i> is not
/// one either. The merged form's fields are the union of every source's, and no source's packet
/// describes that union.</para>
///
/// <para><b>It used to travel.</b> The merged form is a deep clone of the first source's
/// <c>/AcroForm</c> dictionary — cloned for <c>/DA</c>, <c>/DR</c> and <c>/NeedAppearances</c> — and
/// <c>/XFA</c> came along with them. A viewer that honours XFA was handed a form covering only the
/// first input's pages, laid over a document containing all of them: it renders the first source's
/// form and reports its values, for the whole merged file.</para>
/// </remarks>
public class MergeXfaPacketTests
{
    private const string FirstAnswer = "FIRST-SOURCE-XFA-ANSWER";
    private const string SecondAnswer = "SECOND-SOURCE-XFA-ANSWER";

    /// <summary>The merged form has no <c>/XFA</c>, even though the template source had one.</summary>
    [Fact]
    public void TheMergedAcroFormDoesNotInheritTheFirstSourcesXfaPacket()
    {
        using var merged = Merge(XfaFormPdf("A", FirstAnswer), NoXfaFormPdf("B"));

        var catalog = merged.Catalog!.Dictionary;
        var form = merged.ResolveObject(catalog[PdfName.AcroForm]) as PdfDictionary;

        Assert.NotNull(form);
        Assert.False(XfaPacket.IsPresent(form),
            "the merged form carried the first source's XFA packet, which describes that source's "
            + "form alone");
    }

    /// <summary>Dropping it is reported, with a count of the sources that had one.</summary>
    [Fact]
    public void DroppingTheXfaPacketsIsReportedWithTheirCount()
    {
        MergeToBytes(XfaFormPdf("A", FirstAnswer), XfaFormPdf("B", SecondAnswer));

        Assert.NotNull(PdfMerger.LastXfaWarning);
        Assert.Contains("2 source(s)", PdfMerger.LastXfaWarning!, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The acceptance control: sources with no XFA report nothing, including after a merge that
    /// reported.
    /// </summary>
    /// <remarks>
    /// The second half is the reset. A slot read after the operation must be cleared at the start of
    /// it, or the previous merge's verdict is returned for this one — and here that verdict would
    /// claim a form description was lost from a document that never had one.
    /// </remarks>
    [Fact]
    public void SourcesWithNoXfaReportNothingEvenAfterAMergeThatDid()
    {
        MergeToBytes(XfaFormPdf("A", FirstAnswer), NoXfaFormPdf("B"));
        Assert.NotNull(PdfMerger.LastXfaWarning);

        MergeToBytes(NoXfaFormPdf("A"), NoXfaFormPdf("B"));

        Assert.Null(PdfMerger.LastXfaWarning);
    }

    /// <summary>
    /// The honest negative: dropping <c>/XFA</c> costs the merged form nothing else.
    /// </summary>
    /// <remarks>
    /// Without this, "the packet is gone" would be satisfied by a merge that dropped the whole
    /// AcroForm — which is a different, worse behaviour wearing the same green tick.
    /// </remarks>
    [Fact]
    public void TheMergedFieldsAndDefaultsAreUnaffectedByTheXfaDrop()
    {
        using var merged = Merge(XfaFormPdf("A", FirstAnswer), NoXfaFormPdf("B"));

        var catalog = merged.Catalog!.Dictionary;
        var form = merged.ResolveObject(catalog[PdfName.AcroForm]) as PdfDictionary;
        var fields = merged.ResolveObject(form![PdfName.Fields]) as PdfArray;

        Assert.NotNull(fields);
        Assert.Equal(2, fields!.Count);
        Assert.Equal("/Helv 10 Tf 0 g", Text(merged, form[PdfName.DA]));

        var names = new List<string>();
        for (int i = 0; i < fields.Count; i++)
            names.Add(Text(merged, (merged.ResolveObject(fields[i]) as PdfDictionary)![PdfName.T]));

        Assert.Contains("fieldA", names);
        Assert.Contains("fieldB", names);
    }

    // ------------------------------------------------------------------

    private static byte[] MergeToBytes(params byte[][] sources)
    {
        var inputs = new Stream[sources.Length];
        for (int i = 0; i < sources.Length; i++) inputs[i] = new MemoryStream(sources[i]);

        using var output = new MemoryStream();
        try { PdfMerger.Merge(output, inputs); }
        finally { foreach (var s in inputs) s.Dispose(); }

        return output.ToArray();
    }

    private static PdfDocument Merge(params byte[][] sources) =>
        PdfDocument.Open(new MemoryStream(MergeToBytes(sources)));

    private static string Text(PdfDocument doc, PdfObject obj) =>
        doc.ResolveObject(obj) is PdfString s ? Encoding.ASCII.GetString(s.RawBytes) : null;

    // ------------------------------------------------------------------

    private const string PageContent = "BT /F1 12 Tf 72 700 Td (hi) Tj ET";

    private static string Contents(string text) =>
        $"<< /Length {text.Length} >>\nstream\n{text}\nendstream";

    /// <summary>§12.7.8's array form: alternating packet name and stream.</summary>
    private static string Datasets(string answer) =>
        "<xfa:datasets xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\">"
        + "<xfa:data><form><answer>" + answer + "</answer></form></xfa:data></xfa:datasets>";

    private static byte[] XfaFormPdf(string suffix, string answer) => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /AcroForm 6 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
        + "/Resources << >> /Annots [5 0 R] >>",
        Contents(PageContent),
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (field" + suffix + ") /V (v" + suffix + ") "
        + "/Rect [72 700 200 720] /P 3 0 R >>",
        "<< /Fields [5 0 R] /DA (/Helv 10 Tf 0 g) /DR << /Font << /Helv 7 0 R >> >> "
        + "/XFA [(datasets) 8 0 R] >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        Contents(Datasets(answer)),
    });

    private static byte[] NoXfaFormPdf(string suffix) => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /AcroForm 6 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
        + "/Resources << >> /Annots [5 0 R] >>",
        Contents(PageContent),
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (field" + suffix + ") /V (v" + suffix + ") "
        + "/Rect [72 700 200 720] /P 3 0 R >>",
        "<< /Fields [5 0 R] /DA (/Helv 10 Tf 0 g) /DR << /Font << /Helv 7 0 R >> >> >>",
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
