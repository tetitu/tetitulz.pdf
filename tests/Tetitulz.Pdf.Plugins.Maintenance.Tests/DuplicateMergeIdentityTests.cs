// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.Maintenance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// The duplicate merge leaves alone an object something refers to <i>as itself</i> — an
/// optional-content group, a structure element, a form field — however identical its bytes.
/// </summary>
/// <remarks>
/// <para><b>Found by measurement, not review (GAPS.md §19h).</b> another reader's <c>issue269_1</c> has two
/// optional-content groups named "One", one listed under <c>/OFF</c>. Their <c>/Intent</c> and
/// <c>/Usage</c> children merged in the first round, the groups themselves in the second, and the
/// content marked for the shown group was hidden — a glyph disappeared under the <b>default</b>
/// options, and the run reported a saving.</para>
///
/// <para><b>Every test asserts a merge happened as well.</b> "The two stayed apart" is also what a
/// merge pass that never ran produces, so each fixture carries a pair of plain objects that must
/// merge, and the result's <see cref="OptimizationResult.DuplicatesRemoved"/> is checked beside the
/// identity.</para>
/// </remarks>
public sealed class DuplicateMergeIdentityTests
{
    [Fact]
    public void TwoIdenticalLayersOneHiddenStayTwoUnderTheDefaultOptions()
    {
        // 5 and 6 are byte-identical once 7 and 8 merge; 5 is hidden, 6 is shown.
        byte[] input = Pdf(
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [5 0 R 6 0 R]"
                + " /D << /OFF [5 0 R] /ON [6 0 R] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R"
                + " /Resources << /Properties << /Shown 6 0 R /Hidden 5 0 R >> >> >>",
            Stream("/OC /Shown BDC 0 0 10 10 re f EMC /OC /Hidden BDC 20 0 10 10 re f EMC"),
            "<< /Type /OCG /Name (One) /Usage 7 0 R >>",
            "<< /Type /OCG /Name (One) /Usage 8 0 R >>",
            "<< /CreatorInfo << /Creator (Illustrator) /Subtype /Artwork >> >>",
            "<< /CreatorInfo << /Creator (Illustrator) /Subtype /Artwork >> >>");

        var (output, result) = Optimize(input, OptimizationOptions.Default);

        using var doc = PdfDocument.Open(new MemoryStream(output));
        var properties = Resolve<PdfDictionary>(doc,
            Resolve<PdfDictionary>(doc, doc.Pages[0].Dictionary[PdfName.Resources])[PdfName.Properties]);
        int shown = RefNumber(properties[new PdfName("Shown")]);
        int hidden = RefNumber(properties[new PdfName("Hidden")]);

        Assert.NotEqual(shown, hidden);

        // ...and the default configuration still names the hidden one hidden and the shown one shown.
        var ocDefault = Resolve<PdfDictionary>(doc,
            Resolve<PdfDictionary>(doc, Catalog(doc)[PdfName.OCProperties])[PdfName.D]);
        Assert.Equal(hidden, RefNumber(Resolve<PdfArray>(doc, ocDefault[PdfName.OFF])[0]));
        Assert.Equal(shown, RefNumber(Resolve<PdfArray>(doc, ocDefault[PdfName.ON])[0]));

        // Potency: the groups' identical /Usage children did merge, so the pass ran.
        Assert.True(result.DuplicatesRemoved >= 1,
            $"nothing merged ({result.DuplicatesRemoved}); the layers staying apart proves nothing");
    }

    [Fact]
    public void TwoIdenticalUntypedStructureElementsStayTwo()
    {
        // 7 and 8 are empty spans with the same parent and no /Type, which is optional on a
        // structure element — only the tree from /StructTreeRoot finds them. 9 and 10 must merge.
        byte[] input = Pdf(
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R /MarkInfo << /Marked true >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R"
                + " /Resources << /ExtGState << /A 9 0 R /B 10 0 R >> >> >>",
            Stream("/A gs 0 0 10 10 re f /B gs 20 0 10 10 re f"),
            "<< /Type /StructTreeRoot /K 6 0 R >>",
            "<< /S /Document /P 5 0 R /K [7 0 R 8 0 R] >>",
            "<< /S /Span /P 6 0 R >>",
            "<< /S /Span /P 6 0 R >>",
            "<< /Type /ExtGState /CA 0.5 >>",
            "<< /Type /ExtGState /CA 0.5 >>");

        var (output, result) = Optimize(input, OptimizationOptions.Default);

        using var doc = PdfDocument.Open(new MemoryStream(output));
        var root = Resolve<PdfDictionary>(doc, Catalog(doc)[PdfName.StructTreeRoot]);
        var document = Resolve<PdfDictionary>(doc, root[PdfName.K]);
        var kids = Resolve<PdfArray>(doc, document[PdfName.K]);

        Assert.Equal(2, kids.Count);
        Assert.NotEqual(RefNumber(kids[0]), RefNumber(kids[1]));
        Assert.True(result.DuplicatesRemoved >= 1,
            $"the identical graphics states did not merge ({result.DuplicatesRemoved}); the pass did not run");
    }

    [Fact]
    public void TwoIdenticalFieldsStayTwo()
    {
        // 6 and 7 are fields with no /Type, no /T and no widget of their own: nothing but the
        // field tree says what they are. 8 and 9 must merge.
        byte[] input = Pdf(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R"
                + " /Resources << /ExtGState << /A 8 0 R /B 9 0 R >> >> >>",
            Stream("/A gs 0 0 10 10 re f /B gs 20 0 10 10 re f"),
            "<< /Fields [6 0 R 7 0 R] >>",
            "<< /FT /Tx /V (same) >>",
            "<< /FT /Tx /V (same) >>",
            "<< /Type /ExtGState /CA 0.5 >>",
            "<< /Type /ExtGState /CA 0.5 >>");

        var (output, result) = Optimize(input, OptimizationOptions.Default);

        using var doc = PdfDocument.Open(new MemoryStream(output));
        var acroForm = Resolve<PdfDictionary>(doc, Catalog(doc)[PdfName.AcroForm]);
        var fields = Resolve<PdfArray>(doc, acroForm[PdfName.Fields]);

        Assert.Equal(2, fields.Count);
        Assert.NotEqual(RefNumber(fields[0]), RefNumber(fields[1]));
        Assert.True(result.DuplicatesRemoved >= 1,
            $"the identical graphics states did not merge ({result.DuplicatesRemoved}); the pass did not run");
    }

    /// <summary>
    /// A structure tree whose <c>/K</c> leads back to an ancestor: the walk that finds the
    /// elements must end, and still keep the two identical ones apart.
    /// </summary>
    /// <remarks>
    /// Hostile input. Without the walk's entered-set this loops for ever, so the mutation that
    /// removes it does not return — the strongest kill there is.
    /// </remarks>
    [Fact]
    public void AStructureTreeWhoseKCyclesToAnAncestorStillCompletes()
    {
        byte[] input = Pdf(
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R >>",
            Stream("0 0 10 10 re f"),
            "<< /Type /StructTreeRoot /K 6 0 R >>",
            "<< /S /Document /P 5 0 R /K [7 0 R 8 0 R] >>",
            "<< /S /Span /P 6 0 R /K 6 0 R >>",
            "<< /S /Span /P 6 0 R /K 6 0 R >>");

        var (output, _) = Optimize(input, OptimizationOptions.Default);

        using var doc = PdfDocument.Open(new MemoryStream(output));
        var root = Resolve<PdfDictionary>(doc, Catalog(doc)[PdfName.StructTreeRoot]);
        var kids = Resolve<PdfArray>(doc, Resolve<PdfDictionary>(doc, root[PdfName.K])[PdfName.K]);
        Assert.NotEqual(RefNumber(kids[0]), RefNumber(kids[1]));
    }

    // ------------------------------------------------------------------

    private static (byte[] output, OptimizationResult result) Optimize(byte[] input, OptimizationOptions options)
    {
        using var source = new MemoryStream(input);
        using var sink = new MemoryStream();
        var result = PdfOptimizer.Optimize(source, sink, options);
        return (sink.ToArray(), result);
    }

    private static PdfDictionary Catalog(PdfDocument doc) =>
        Resolve<PdfDictionary>(doc, doc.Trailer[PdfName.Root]);

    private static T Resolve<T>(PdfDocument doc, PdfObject obj) where T : PdfObject
    {
        var resolved = doc.ResolveObject(obj);
        Assert.IsType<T>(resolved);
        return (T)resolved;
    }

    private static int RefNumber(PdfObject obj)
    {
        var reference = Assert.IsType<PdfReference>(obj);
        return reference.ReferenceObjectNumber;
    }

    private static string Stream(string content) =>
        "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n"
        + content + "\nendstream";

    /// <summary>Objects numbered from 1 in the order given, with a classic cross-reference table.</summary>
    internal static byte[] Pdf(params string[] objects)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n")
              .Append(objects[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        int size = objects.Length + 1;
        sb.Append("xref\n0 ").Append(size.ToString(CultureInfo.InvariantCulture)).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);
        sb.Append("trailer\n<< /Size ").Append(size.ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref.ToString(CultureInfo.InvariantCulture))
          .Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
