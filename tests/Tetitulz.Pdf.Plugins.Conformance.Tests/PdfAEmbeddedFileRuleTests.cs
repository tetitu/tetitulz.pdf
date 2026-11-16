// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// The embedded file rules: A67 (a file specification's keys, the stream's MIME type, PDF/A-3's
/// association, PDF/A-4f's name tree) and A68 (an embedded file is a conforming PDF/A file of a part
/// the part admits) — ISO 19005-2/3 6.8, 19005-4 6.9, the published profile's rules of those numbers — and A03's
/// narrowing to PDF/A-1.
/// </summary>
public class PdfAEmbeddedFileRuleTests
{
    /// <summary>
    /// A one-page PDF/A file of <paramref name="part"/> that validates clean: the identification, a
    /// trailer /ID, the binary comment; nothing drawn.
    /// </summary>
    internal static byte[] PdfA(int part, string catalogExtra = "", string level = "B", params string[] extra)
    {
        string identification = part == 4
            ? "pdfaid:part='4' pdfaid:rev='2020'" + (level != "B" ? " pdfaid:conformance='" + level + "'" : "")
            : $"pdfaid:part='{part}' pdfaid:conformance='{level}'";
        string xmp = "<?xpacket begin='' id='W5M0MpCehiHzreSzNTczkc9d'?><x:xmpmeta xmlns:x='adobe:ns:meta/'>" +
                     "<rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'><rdf:Description rdf:about='' " +
                     "xmlns:pdfaid='http://www.aiim.org/pdfa/ns/id/' " + identification + "/></rdf:RDF></x:xmpmeta><?xpacket end='w'?>";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R" + catalogExtra + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>",
            "<< /Type /Metadata /Subtype /XML /Length " + xmp.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + xmp + "\nendstream",
        };
        objects.AddRange(extra);
        var sb = new StringBuilder(part == 4 ? "%PDF-2.0\n" : part == 1 ? "%PDF-1.4\n" : "%PDF-1.7\n").Append("%âãÏÓ\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((objects.Count + 1).ToString(CultureInfo.InvariantCulture)).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append((objects.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R /ID [<00112233445566778899AABBCCDDEEFF> <00112233445566778899AABBCCDDEEFF>] >>\nstartxref\n")
          .Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static PdfAValidationResult Validate(byte[] pdf, int part)
    {
        using var ms = new MemoryStream(pdf);
        return PdfAValidator.Validate(ms, (PdfAConformancePart)part);
    }

    private static List<PdfAViolation> Coded(byte[] pdf, int part, string code)
        => Validate(pdf, part).Violations.Where(v => v.Code == code).ToList();

    private const string Tree = " /Names << /EmbeddedFiles << /Names [(a.pdf) 5 0 R] >> >>";
    private const string Spec = "/Type /Filespec /F (a.pdf) /UF (a.pdf) /AFRelationship /Data /EF << /F 6 0 R >>";

    /// <summary>A file with one embedded file: its specification's keys, its stream's subtype, its bytes.</summary>
    private static byte[] Embedding(int part, string spec, string subtype, byte[] file, string catalogExtra = " /AF [5 0 R]", string level = "B")
    {
        string hex = Convert.ToHexString(file) + ">";
        return PdfA(part, Tree + catalogExtra, level,
            "<< " + spec + " >>",
            "<< /Type /EmbeddedFile " + subtype + " /Filter /ASCIIHexDecode /Length " + hex.Length.ToString(CultureInfo.InvariantCulture) +
            " >>\nstream\n" + hex + "\nendstream");
    }

    private static readonly byte[] Text = Encoding.ASCII.GetBytes("plain text");

    private static void AssertFinding(List<PdfAViolation> found, string? expected)
    {
        if (expected == null)
            Assert.Empty(found);
        else
            Assert.Contains(expected, Assert.Single(found).Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------- the fixtures, clean

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void TheFixture_IsConformant(int part)
    {
        var result = Validate(PdfA(part), part);
        Assert.True(result.IsConformant, string.Join(" | ", result.Violations));
    }

    [Theory]
    [InlineData(2, "/Subtype /application#2Fpdf")]
    [InlineData(3, "/Subtype /application#2Fpdf")]
    [InlineData(4, "/Subtype /application#2Fpdf")]
    public void AConformingEmbeddedPdfA_IsAccepted(int part, string subtype)
    {
        var result = Validate(Embedding(part, Spec, subtype, PdfA(1)), part);
        Assert.True(result.IsConformant, string.Join(" | ", result.Violations));
        Assert.Empty(result.Informational);
    }

    // -------------------------------------------------------------- A03

    [Fact]
    public void A03_IsPdfA1sAlone()
    {
        Assert.Single(Coded(Embedding(1, Spec, "", PdfA(1)), 1, "A03"));
        Assert.Empty(Coded(Embedding(2, Spec, "", PdfA(1)), 2, "A03"));
    }

    // -------------------------------------------------------------- A67

    [Theory]
    [InlineData(2, "/Type /Filespec /F (a.pdf) /EF << /F 6 0 R >>", "(a.pdf) has no /UF")]
    [InlineData(3, "/Type /Filespec /UF (a.pdf) /AFRelationship /Data /EF << /F 6 0 R >>", "(a.pdf) has no /F")]
    [InlineData(4, "/Type /Filespec /F (a.pdf) /AFRelationship /Data /EF << /F 6 0 R >>", "(a.pdf) has no /UF")]
    [InlineData(3, "/Type /Filespec /F (a.pdf) /UF (a.pdf) /EF << /F 6 0 R >>", "(a.pdf) has no /AFRelationship")]
    [InlineData(2, "/Type /Filespec /F (a.pdf) /UF (a.pdf) /EF << /F 6 0 R >>", null)]       // PDF/A-2 asks no relationship
    [InlineData(2, "/Type /Filespec /F (a.pdf)", null)]                                     // no /EF, no file: both names are not asked
    public void A67_AFileSpecificationsKeys(int part, string spec, string? expected)
        => AssertFinding(Coded(Embedding(part, spec, "/Subtype /application#2Fpdf", PdfA(1)), part, "A67"), expected);

    [Theory]
    [InlineData(3, "", "(a.pdf) /Subtype missing is not a MIME type")]
    [InlineData(4, "/Subtype /application", "(a.pdf) /Subtype /application is not a MIME type")]
    [InlineData(3, "/Subtype /a#2Fb#2Fc", "/Subtype /a/b/c is not a MIME type")]
    [InlineData(3, "/Subtype /#2Fpdf", "is not a MIME type")]
    [InlineData(3, "/Subtype /text#2F", "is not a MIME type")]
    [InlineData(3, "/Subtype /text#2Fpl#20ain", "is not a MIME type")]
    [InlineData(3, "/Subtype /application#2Fvnd.ms-excel", null)]
    [InlineData(4, "/Subtype /image#2Fsvg+xml", null)]
    [InlineData(3, "/Subtype /x_y#2Fa-b", null)]
    [InlineData(2, "", null)]                                                               // PDF/A-2 asks no MIME type
    public void A67_AnEmbeddedFilesSubtype_IsAMimeType(int part, string subtype, string? expected)
        => AssertFinding(Coded(Embedding(part, Spec, subtype, PdfA(1)), part, "A67"), expected);

    [Fact]
    public void A67_PdfA3_EachEmbeddedFileIsAssociated()
    {
        AssertFinding(Coded(Embedding(3, Spec, "/Subtype /text#2Fplain", Text, catalogExtra: ""), 3, "A67"), "(a.pdf) is in no /AF array");
        // Associated with a page, not the catalog: anywhere will do.
        var onPage = Embedding(3, Spec, "/Subtype /text#2Fplain", Text, catalogExtra: "");
        string text = Encoding.Latin1.GetString(onPage).Replace("/Resources << >>", "/Resources << >> /AF [5 0 R]", StringComparison.Ordinal);
        Assert.Empty(Coded(Encoding.Latin1.GetBytes(text), 3, "A67"));
        // In a direct dictionary inside an object: an annotation held in the page's /Annots.
        string inAnnotation = Encoding.Latin1.GetString(onPage).Replace("/Resources << >>",
            "/Resources << >> /Annots [<< /Type /Annot /Subtype /FileAttachment /Rect [0 0 0 0] /F 4 /FS 5 0 R /AF [5 0 R] >>]",
            StringComparison.Ordinal);
        Assert.Empty(Coded(Encoding.Latin1.GetBytes(inAnnotation), 3, "A67"));
        // PDF/A-4 asks no association.
        Assert.Empty(Coded(Embedding(4, Spec, "/Subtype /text#2Fplain", PdfA(1), catalogExtra: ""), 4, "A67"));
    }

    [Theory]
    [InlineData("F", "has no /EmbeddedFiles")]
    [InlineData("E", null)]
    [InlineData("B", null)]
    public void A67_PdfA4f_HasAnEmbeddedFilesTree(string level, string? expected)
        => AssertFinding(Coded(PdfA(4, level: level), 4, "A67"), expected);

    // -------------------------------------------------------------- A68

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void A68_AFileThatIsNotPdf_IsReported(int part)
        => AssertFinding(Coded(Embedding(part, Spec, "/Subtype /text#2Fplain", Text), part, "A68"), "(a.pdf) is not a PDF file");

    [Fact]
    public void A68_APdfThatIsNotPdfA_IsReported()
    {
        byte[] plain = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(PdfA(1)).Replace("pdfaid:part='1'", "x='1'", StringComparison.Ordinal));
        AssertFinding(Coded(Embedding(2, Spec, "/Subtype /application#2Fpdf", plain), 2, "A68"), "(a.pdf) declares pdfaid:part (none)");
    }

    [Theory]
    [InlineData(2, 4, "(a.pdf) declares pdfaid:part 4")]
    [InlineData(2, 3, "(a.pdf) declares pdfaid:part 3")]
    [InlineData(4, 3, "(a.pdf) declares pdfaid:part 3")]
    [InlineData(2, 2, null)]
    [InlineData(4, 4, null)]
    [InlineData(4, 2, null)]
    public void A68_TheEmbeddedFilesPart_IsOneThePartAdmits(int part, int embeddedPart, string? expected)
        => AssertFinding(Coded(Embedding(part, Spec, "/Subtype /application#2Fpdf", PdfA(embeddedPart)), part, "A68"), expected);

    [Fact]
    public void A68_ANonConformingEmbeddedPdfA_IsReportedWithItsFirstViolation()
    {
        // The embedded file's trailer loses its /ID: an A42 of its own.
        string inner = Encoding.Latin1.GetString(PdfA(1));
        inner = inner.Replace(" /ID [<00112233445566778899AABBCCDDEEFF> <00112233445566778899AABBCCDDEEFF>]", "", StringComparison.Ordinal);
        AssertFinding(Coded(Embedding(2, Spec, "/Subtype /application#2Fpdf", Encoding.Latin1.GetBytes(inner)), 2, "A68"),
            "(a.pdf) breaks PDF/A-1 (1 violation(s), the first A42)");
    }

    [Theory]
    [InlineData(3, "B")]
    [InlineData(4, "F")]
    [InlineData(4, "E")]
    public void A68_PdfA3And4fAnd4e_AdmitAnyFile(int part, string level)
        => Assert.Empty(Coded(Embedding(part, Spec, "/Subtype /text#2Fplain", Text, level: level), part, "A68"));

    [Fact]
    public void A68_AnEmbeddedFilesOwnEmbeddedFiles_AreNotValidated_AndAnA99SaysSo()
    {
        // A PDF/A-2 file embedding text, embedded in a PDF/A-2 file: the inner text is a level too deep.
        byte[] middle = Embedding(2, Spec, "/Subtype /text#2Fplain", Text);
        var result = Validate(Embedding(2, Spec, "/Subtype /application#2Fpdf", middle), 2);
        Assert.DoesNotContain(result.Violations, v => v.Code == "A68");
        Assert.Contains(result.Informational, i => i.Code == "A99"
            && i.Message.Contains("An embedded file's validation did not run in full", StringComparison.Ordinal));
        // Potency: the middle file validated on its own reports its text.
        AssertFinding(Coded(middle, 2, "A68"), "(a.pdf) is not a PDF file");
    }

    [Fact]
    public void A68_TheEmbeddedValidation_LeavesTheOuterValidationsSlotsAlone()
    {
        // The inner's partial entries stay out of the outer's but for the one entry naming it.
        byte[] middle = Embedding(2, Spec, "/Subtype /text#2Fplain", Text);
        var result = Validate(Embedding(2, Spec, "/Subtype /application#2Fpdf", middle), 2);
        Assert.Single(result.Informational);

        // A check that runs after the embedded files still reports into the outer validation: a
        // page whose content will not decode, beside a conforming embedded file.
        string undecodable = Encoding.Latin1.GetString(Embedding(2, Spec, "/Subtype /application#2Fpdf", PdfA(1)))
            .Replace("/Resources << >> >>", "/Resources << >> /Contents 7 0 R >>", StringComparison.Ordinal);
        undecodable = AppendObject(undecodable, "<< /Filter /FlateDecode /Length 12 >>\nstream\nNOT-DEFLATE!\nendstream");
        var partial = Validate(Encoding.Latin1.GetBytes(undecodable), 2);
        Assert.Contains(partial.Informational, i => i.Message.Contains("A50-A53 content-stream checks did not run in full", StringComparison.Ordinal));

        // And the diagnostic slot: an embedded file whose metadata will not decode reports into the
        // inner validation, not the outer one.
        string corrupt = Encoding.Latin1.GetString(PdfA(1));
        corrupt = corrupt.Replace("/Type /Metadata /Subtype /XML", "/Type /Metadata /Subtype /XML /Filter /FlateDecode", StringComparison.Ordinal);
        AssertFinding(Coded(Embedding(2, Spec, "/Subtype /application#2Fpdf", Encoding.Latin1.GetBytes(corrupt)), 2, "A68"),
            "(a.pdf) declares pdfaid:part (none)");
        Assert.Null(PdfAValidator.LastValidationError);
    }

    /// <summary>
    /// Each embedded file is a whole validation, and a file chooses how many it holds: a hundred names
    /// for one specification are judged once, and seventy distinct embedded files are judged up to
    /// <see cref="PdfAValidator.MaxEmbeddedFilesValidated"/>, the rest said in an A99.
    /// </summary>
    [Fact]
    public void A68_AStreamIsJudgedOnce_AndTheFilesJudgedAreCapped()
    {
        string hex = Convert.ToHexString(PdfA(1)) + ">";
        string Stream() => "<< /Type /EmbeddedFile /Subtype /application#2Fpdf /Filter /ASCIIHexDecode /Length " +
                           hex.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + hex + "\nendstream";

        string names = string.Concat(Enumerable.Range(0, 100).Select(i => $"(a{i:D3}.pdf) 5 0 R "));
        var shared = Validate(PdfA(2, " /Names << /EmbeddedFiles << /Names [" + names + "] >> >>", "B", "<< " + Spec + " >>", Stream()), 2);
        Assert.True(shared.IsConformant, string.Join(" | ", shared.Violations));
        Assert.Equal(1, PdfAValidator.EmbeddedFilesValidated);

        const int files = 70;
        var objects = new List<string>();
        var entries = new StringBuilder();
        for (int i = 0; i < files; i++)
        {
            int spec = 5 + 2 * i;
            entries.Append($"(b{i:D3}.pdf) {spec} 0 R ");
            objects.Add($"<< /Type /Filespec /F (b{i}.pdf) /UF (b{i}.pdf) /EF << /F {spec + 1} 0 R >> >>");
            objects.Add(Stream());
        }
        var capped = Validate(PdfA(2, " /Names << /EmbeddedFiles << /Names [" + entries + "] >> >>", "B", objects.ToArray()), 2);
        Assert.Equal(PdfAValidator.MaxEmbeddedFilesValidated, PdfAValidator.EmbeddedFilesValidated);
        Assert.Contains(capped.Informational, i => i.Code == "A99" && i.Message.Contains("More than 64 embedded files", StringComparison.Ordinal));
        Assert.DoesNotContain(capped.Violations, v => v.Code == "A68");
    }

    /// <summary>A file with one more object, written as an incremental update (its number the next free one).</summary>
    private static string AppendObject(string pdf, string body)
    {
        int size = int.Parse(System.Text.RegularExpressions.Regex.Match(pdf, @"/Size (\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
        int startxref = int.Parse(System.Text.RegularExpressions.Regex.Match(pdf, @"startxref\n(\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
        var sb = new StringBuilder(pdf);
        int at = sb.Length;
        sb.Append(size.ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        int xref = sb.Length;
        sb.Append("xref\n").Append(size.ToString(CultureInfo.InvariantCulture)).Append(" 1\n").Append(at.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append((size + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R /Prev ").Append(startxref.ToString(CultureInfo.InvariantCulture))
          .Append(" /ID [<00112233445566778899AABBCCDDEEFF> <00112233445566778899AABBCCDDEEFF>] >>\nstartxref\n")
          .Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        return sb.ToString();
    }

    // -------------------------------------------------------------- the converter's repairs

    private static (PdfAConversionResult Result, byte[] Output) ConvertToPdfA3(byte[] input)
    {
        var output = new MemoryStream();
        var result = PdfAConverter.ConvertToPdfA3b(new MemoryStream(input), output,
            new PdfAConversionOptions { IccProfile = IccProfileFixtures.Rgb() });
        return (result, output.ToArray());
    }

    private static (PdfDictionary Spec, PdfStream File, PdfArray? CatalogAf) Attachment(PdfDocument doc)
    {
        var names = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Names]);
        var tree = (PdfDictionary)doc.ResolveObject(names[PdfName.EmbeddedFiles]);
        var spec = (PdfDictionary)doc.ResolveObject(((PdfArray)doc.ResolveObject(tree[PdfName.Names]))[1]);
        var file = (PdfStream)doc.ResolveObject(((PdfDictionary)doc.ResolveObject(spec[PdfName.EF]))[PdfName.F]);
        return (spec, file, doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AF]) as PdfArray);
    }

    [Fact]
    public void TheConverter_CompletesWhatPdfA3AsksOfAnAttachment()
    {
        // A specification with /F alone, no relationship, a stream with no type, no /AF anywhere.
        string bare = Encoding.Latin1.GetString(Embedding(3, "/Type /Filespec /F (a.csv) /EF << /F 6 0 R >>", "", Text, catalogExtra: ""));
        bare = bare.Replace("pdfaid:part='3' pdfaid:conformance='B'", "x='1'", StringComparison.Ordinal);
        var (result, output) = ConvertToPdfA3(Encoding.Latin1.GetBytes(bare));

        using var doc = PdfDocument.Open(new MemoryStream(output));
        var (spec, file, af) = Attachment(doc);
        Assert.Equal("a.csv", ((PdfString)doc.ResolveObject(spec[PdfName.UF])).GetText());
        Assert.Equal("Unspecified", ((PdfName)doc.ResolveObject(spec[PdfName.AFRelationship])).Value);
        Assert.Equal("application/octet-stream", ((PdfName)doc.ResolveObject(file[PdfName.Subtype])).Value);
        Assert.NotNull(af);
        Assert.Contains(af!.Cast<PdfObject>(), e => ReferenceEquals(doc.ResolveObject(e), spec));
        Assert.Empty(Coded(output, 3, "A67"));
        Assert.Contains(result.Warnings, w => w.StartsWith("Associated 1 embedded file(s)", StringComparison.Ordinal));
    }

    /// <summary>One specification under two names is one attachment: repaired once, associated once.</summary>
    [Fact]
    public void TheConverter_RepairsASpecificationTwoNamesShare_Once()
    {
        string bare = Encoding.Latin1.GetString(Embedding(3, "/Type /Filespec /F (a.csv) /EF << /F 6 0 R >>", "", Text, catalogExtra: ""));
        bare = bare.Replace("pdfaid:part='3' pdfaid:conformance='B'", "x='1'", StringComparison.Ordinal);
        string twice = bare.Replace("[(a.pdf) 5 0 R]", "[(a.pdf) 5 0 R (b.pdf) 5 0 R]", StringComparison.Ordinal);
        Assert.NotEqual(bare, twice);   // potency: the tree names it twice
        var (result, output) = ConvertToPdfA3(Encoding.Latin1.GetBytes(twice));

        using var doc = PdfDocument.Open(new MemoryStream(output));
        var (spec, _, af) = Attachment(doc);
        Assert.True(ReferenceEquals(doc.ResolveObject(Assert.Single(af!)), spec));
        Assert.Contains(result.Warnings, w => w.StartsWith("Associated 1 embedded file(s)", StringComparison.Ordinal));
        Assert.Empty(Coded(output, 3, "A67"));
    }

    [Fact]
    public void TheConverter_LeavesACompleteAttachmentAsItWas()
    {
        string complete = Encoding.Latin1.GetString(Embedding(3, Spec, "/Subtype /text#2Fcsv", Text));
        complete = complete.Replace("pdfaid:part='3' pdfaid:conformance='B'", "x='1'", StringComparison.Ordinal);
        var (result, output) = ConvertToPdfA3(Encoding.Latin1.GetBytes(complete));

        using var doc = PdfDocument.Open(new MemoryStream(output));
        var (spec, file, af) = Attachment(doc);
        Assert.Equal("Data", ((PdfName)doc.ResolveObject(spec[PdfName.AFRelationship])).Value);
        Assert.Equal("text/csv", ((PdfName)doc.ResolveObject(file[PdfName.Subtype])).Value);
        Assert.Single(af!);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("embedded file", StringComparison.Ordinal));
    }
}
