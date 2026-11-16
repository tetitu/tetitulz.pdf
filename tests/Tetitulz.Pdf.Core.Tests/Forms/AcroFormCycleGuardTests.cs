// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Forms;

/// <summary>
/// Adversarial input: an AcroForm whose <c>/Kids</c> form a cycle.
///
/// <para><b>Every other walk over this tree already had a visited set.</b>
/// <c>Base.Operations.Forms.PdfFormReader.Walk</c>, <c>PdfFormFiller.FillFields</c> and
/// <c>Signing.SignatureFieldRegistration.FieldNames</c> each pair the depth cap with one — and
/// <c>PdfFormFiller</c>'s comment even says it duplicates <c>PdfFormReader</c>'s "in the same shape
/// and for the same reason it states". <c>Core.Forms.PdfAcroForm</c>'s two signature-field walks
/// had the cap and no set, and they are the ones a signature lookup goes through.</para>
///
/// <para><b>A depth cap is not a cycle guard.</b> It bounds one branch and not the number of
/// branches: a two-node cycle under a cap of <c>MaxTreeDepth</c> still walks the cap's worth of
/// nodes on every path, and a node reached from two parents doubles the work at each level. The
/// assertions below are on the guard's own evidence — the walk terminates and returns the right
/// answer — never on a wall clock, which per CLAUDE.md would be the banned shape and would also
/// pass on a machine fast enough.</para>
/// </summary>
public sealed class AcroFormCycleGuardTests
{
    /// <summary>
    /// Two fields that are each other's kid. <c>GetSignatureFields</c> must terminate and must
    /// report the one signature field exactly once, rather than once per lap.
    /// </summary>
    [Fact]
    public void CyclicFieldKids_TerminateAndReportEachFieldOnce()
    {
        using var ms = new MemoryStream(CyclicAcroFormPdf());
        using var doc = PdfDocument.Open(ms);

        var fields = doc.AcroForm.GetSignatureFields();

        Assert.Single(fields);
        Assert.Equal("Sig1", fields[0].FieldName);
    }

    /// <summary>
    /// The by-name lookup takes the other of the two walks, so it needs its own case: a cycle must
    /// not stop it finding a field that really is there.
    /// </summary>
    [Fact]
    public void CyclicFieldKids_DoNotHideAFieldFromTheByNameLookup()
    {
        using var ms = new MemoryStream(CyclicAcroFormPdf());
        using var doc = PdfDocument.Open(ms);

        Assert.NotNull(doc.AcroForm.FindSignatureField("Sig1"));
        Assert.Null(doc.AcroForm.FindSignatureField("NoSuchField"));
    }

    /// <summary>
    /// A <b>diamond</b> rather than a loop: one field reached from two parents. This is the shape a
    /// depth cap cannot bound at all — no path is deep, and the work still doubles per level — and
    /// it must not report the shared field twice.
    /// </summary>
    [Fact]
    public void DiamondFieldGraph_ReportsTheSharedFieldOnce()
    {
        using var ms = new MemoryStream(DiamondAcroFormPdf());
        using var doc = PdfDocument.Open(ms);

        var fields = doc.AcroForm.GetSignatureFields();

        Assert.Single(fields);
        Assert.Equal("Shared", fields[0].FieldName);
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// <c>/Fields [5]</c>; field 5 has kid 6, and field 6 has kid 5 again. Field 5 is the signature.
    /// </summary>
    private static byte[] CyclicAcroFormPdf() =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>",
            Stream("", "\n"),
            "<< /FT /Sig /T (Sig1) /Kids [6 0 R] >>",
            // /Tx, not /Sig: the loop node exists to close the cycle, and making it a second
            // signature would mean "two results" was the correct answer and the test could not
            // tell a working guard from one that reported each lap.
            "<< /FT /Tx /T (Loop) /Kids [5 0 R] >>");

    /// <summary>
    /// <c>/Fields [5 6]</c>; both name field 7 as a kid. Field 7 is the signature.
    /// </summary>
    private static byte[] DiamondAcroFormPdf() =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R 6 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>",
            Stream("", "\n"),
            "<< /FT /Tx /T (LeftBranch) /Kids [7 0 R] >>",
            "<< /FT /Tx /T (RightBranch) /Kids [7 0 R] >>",
            "<< /FT /Sig /T (Shared) >>");

    private static string Stream(string dictEntries, string data) =>
        $"<< {dictEntries} /Length {data.Length} >>\nstream\n{data}\nendstream";

    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
