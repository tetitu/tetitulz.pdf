// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Core;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

public class PdfUsageRightsReaderTests
{
    // -----------------------------------------------------------------------
    //  Helpers — build minimal in-memory PDFs with /UR3 signatures
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds a minimal PDF that contains a signature field with an explicit
    /// /UR3 SigRef carrying the given /Form and /Annots flags.
    /// The sig dict is stored as a direct value on the field dictionary so
    /// the reader can find it without any real PKCS#7 content.
    /// </summary>
    private static byte[] BuildPdfWithUr3(
        bool formFillIn = true,
        bool formImportExport = false,
        bool formSave = false,
        bool annotation = false,
        bool documentSave = false,
        string signerName = "TestSigner")
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        // Catalog
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm 5 0 R >>\nendobj\n");

        // Pages
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        // Page
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");

        // Build /TransformParams content
        var tpContent = new StringBuilder();
        tpContent.Append("<< /Type /TransformParams /V /2.2");
        if (formFillIn)        tpContent.Append(" /Form [/FillIn]");
        if (formImportExport)  tpContent.Append(" /Form [/Import /Export]");
        if (formSave)          tpContent.Append(" /Form [/SubmitStandalone]");
        if (annotation)        tpContent.Append(" /Annots [/Create /Modify /Copy /Import /Export]");
        if (documentSave)      tpContent.Append(" /Document [/FullSave]");
        tpContent.Append(" >>");

        // Sig dict (object 4): a /Sig value dict with /Reference containing /UR3
        int o4 = sb.Length;
        string safeSignerName = signerName.Replace("(", "\\(").Replace(")", "\\)");
        sb.Append($"4 0 obj\n<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached");
        sb.Append($" /Name ({safeSignerName})");
        sb.Append($" /Reference [<< /Type /SigRef /TransformMethod /UR3 /DigestMethod /SHA256");
        sb.Append($" /TransformParams {tpContent} >>]");
        sb.Append(" >>\nendobj\n");

        // AcroForm with one Sig field (object 5)
        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Fields [6 0 R] >>\nendobj\n");

        // Signature field (object 6)
        int o6 = sb.Length;
        sb.Append("6 0 obj\n<< /Type /Annot /Subtype /Widget /FT /Sig /T (Sig1) /V 4 0 R >>\nendobj\n");

        // xref
        int xref = sb.Length;
        sb.Append("xref\n0 7\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.AppendFormat("{0:D10} 00000 n \n", o6);
        sb.Append("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdfNoSignature()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length; sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length; sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length; sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        int xref = sb.Length;
        sb.Append("xref\n0 4\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.Append("trailer\n<< /Size 4 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // -----------------------------------------------------------------------
    //  Tests
    // -----------------------------------------------------------------------

    [Fact]
    public void Read_NoPdfSignature_ReturnsEmptyList()
    {
        byte[] pdf = BuildPdfNoSignature();
        using var ms = new MemoryStream(pdf);
        var results = PdfUsageRightsReader.Read(ms);
        Assert.Empty(results);
    }

    [Fact]
    public void Read_Ur3WithFormFillIn_ReportsFormFillIn()
    {
        byte[] pdf = BuildPdfWithUr3(formFillIn: true);
        using var ms = new MemoryStream(pdf);
        var results = PdfUsageRightsReader.Read(ms);
        Assert.Single(results);
        Assert.True(results[0].HasFormFillIn);
    }

    [Fact]
    public void Read_Ur3WithAnnotation_ReportsAnnotation()
    {
        byte[] pdf = BuildPdfWithUr3(annotation: true);
        using var ms = new MemoryStream(pdf);
        var results = PdfUsageRightsReader.Read(ms);
        Assert.Single(results);
        Assert.True(results[0].HasAnnotation);
    }

    [Fact]
    public void Read_Ur3WithDocumentSave_ReportsDocumentSave()
    {
        byte[] pdf = BuildPdfWithUr3(documentSave: true);
        using var ms = new MemoryStream(pdf);
        var results = PdfUsageRightsReader.Read(ms);
        Assert.Single(results);
        Assert.True(results[0].HasDocumentSave);
    }

    [Fact]
    public void Read_Ur3WithAllFlags_ReportsAllRights()
    {
        byte[] pdf = BuildPdfWithUr3(
            formFillIn: true, annotation: true, documentSave: true);
        using var ms = new MemoryStream(pdf);
        var results = PdfUsageRightsReader.Read(ms);
        Assert.Single(results);
        var e = results[0];
        Assert.True(e.HasFormFillIn);
        Assert.True(e.HasAnnotation);
        Assert.True(e.HasDocumentSave);
    }

    [Fact]
    public void Read_Ur3NoFlags_RightsIsNone()
    {
        // A /UR3 entry with no specific rights arrays.
        byte[] pdf = BuildPdfWithUr3(
            formFillIn: false, annotation: false, documentSave: false);
        using var ms = new MemoryStream(pdf);
        var results = PdfUsageRightsReader.Read(ms);
        Assert.Single(results);
        Assert.Equal(UsageRights.None, results[0].Rights);
    }

    [Fact]
    public void Read_Ur3_SignerNamePopulated()
    {
        byte[] pdf = BuildPdfWithUr3(signerName: "Alice Corp");
        using var ms = new MemoryStream(pdf);
        var results = PdfUsageRightsReader.Read(ms);
        Assert.Single(results);
        Assert.Equal("Alice Corp", results[0].SignerName);
    }

    [Fact]
    public void Read_Ur3_DigestMethodPopulated()
    {
        byte[] pdf = BuildPdfWithUr3();
        using var ms = new MemoryStream(pdf);
        var results = PdfUsageRightsReader.Read(ms);
        Assert.Single(results);
        Assert.Equal("SHA256", results[0].DigestMethod);
    }

    [Fact]
    public void Read_Ur3_VersionIsPopulated()
    {
        byte[] pdf = BuildPdfWithUr3();
        using var ms = new MemoryStream(pdf);
        var results = PdfUsageRightsReader.Read(ms);
        Assert.Single(results);
        Assert.Equal("2.2", results[0].Ur3Version);
    }

    [Fact]
    public void Read_NullStream_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PdfUsageRightsReader.Read((Stream)null));
    }

    [Fact]
    public void Read_NullPath_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PdfUsageRightsReader.Read((string)null));
    }

    [Fact]
    public void Read_CorruptPdf_ReturnsEmptyAndSetsLastError()
    {
        byte[] garbage = Encoding.ASCII.GetBytes("not a pdf at all");
        using var ms = new MemoryStream(garbage);
        var results = PdfUsageRightsReader.Read(ms);
        Assert.Empty(results);
        Assert.NotNull(PdfUsageRightsReader.LastError);
    }
}
