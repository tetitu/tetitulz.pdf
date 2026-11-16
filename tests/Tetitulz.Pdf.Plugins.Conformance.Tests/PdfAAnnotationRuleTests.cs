// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// PDF/A's annotation rules: A37 (a type the part does not permit), A38 (<c>/F</c>), A39 (the
/// appearance) — ISO 19005-1 6.5.2/6.5.3, ISO 19005-2/3/4 6.3.1–6.3.3, the published profile's rules of the same
/// numbers; the conformance corpus is the wider oracle (<see cref="ConformanceCorpusTests"/>).
/// </summary>
public class PdfAAnnotationRuleTests
{
    private const string Stream = "4 0 R";
    private const string Box = "/Rect [10 10 50 50]";

    /// <summary>A one-page document whose page holds <paramref name="annotations"/> (objects 5 on).</summary>
    private static byte[] Document(string? level, string[] annotations, string[]? extras = null)
    {
        extras ??= Array.Empty<string>();
        string metadata = level == null ? "" : " /Metadata " + (annotations.Length + extras.Length + 5).ToString(CultureInfo.InvariantCulture) + " 0 R";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R" + metadata + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots [" +
                string.Join(" ", annotations.Select((_, i) => (i + 5).ToString(CultureInfo.InvariantCulture) + " 0 R")) + "] >>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 40 40] /Length 0 >>\nstream\n\nendstream",
        };
        objects.AddRange(annotations);
        objects.AddRange(extras);
        if (level != null)
        {
            string xmp = "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
                         "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\"" +
                         " xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\" pdfaid:part=\"4\" pdfaid:rev=\"2020\"" +
                         (level.Length == 0 ? "" : " pdfaid:conformance=\"" + level + "\"") +
                         "/></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
            objects.Add("<< /Type /Metadata /Subtype /XML /Length " + xmp.Length.ToString(CultureInfo.InvariantCulture) +
                        " >>\nstream\n" + xmp + "\nendstream");
        }
        var sb = new StringBuilder("%PDF-1.7\n");
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
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static List<PdfAViolation> Coded(int part, string code, params string[] annotations)
        => CodedAtLevel(part, null, code, annotations);

    private static List<PdfAViolation> CodedAtLevel(int part, string? level, string code, params string[] annotations)
        => CodedWith(part, level, code, annotations, null);

    private static List<PdfAViolation> CodedWith(int part, string? level, string code, string[] annotations, string[]? extras)
    {
        using var ms = new MemoryStream(Document(level, annotations, extras));
        return PdfAValidator.Validate(ms, (PdfAConformancePart)part).Violations.Where(v => v.Code == code).ToList();
    }

    private static string Annot(string subtype, string extra = "/F 4 /AP << /N 4 0 R >>", string rect = Box)
        => "<< /Type /Annot /Subtype /" + subtype + " " + rect + " " + extra + " >>";

    // -------------------------------------------------------------- A37: types

    [Theory]
    [InlineData("FileAttachment", 1, false)]
    [InlineData("FileAttachment", 2, true)]
    [InlineData("FileAttachment", 3, true)]
    [InlineData("FileAttachment", 4, false)]
    [InlineData("Polygon", 1, false)]
    [InlineData("Polygon", 2, true)]
    [InlineData("Redact", 2, true)]
    [InlineData("Projection", 2, false)]
    [InlineData("Projection", 4, true)]
    [InlineData("Sound", 2, false)]
    [InlineData("Screen", 4, false)]
    [InlineData("3D", 2, false)]
    [InlineData("Square", 1, true)]
    [InlineData("Invented", 2, false)]
    public void A37_EachPart_PermitsItsOwnAnnotationTypes(string subtype, int part, bool permitted)
    {
        var a37 = Coded(part, "A37", Annot(subtype));
        if (permitted)
            Assert.Empty(a37);
        else
            Assert.Contains("page 1: /" + subtype, Assert.Single(a37).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("FileAttachment", "F", true)]
    [InlineData("FileAttachment", "E", false)]
    [InlineData("FileAttachment", "", false)]
    [InlineData("3D", "E", true)]
    [InlineData("RichMedia", "E", true)]
    [InlineData("3D", "F", false)]
    [InlineData("RichMedia", "", false)]
    public void A37_PdfA4eAndPdfA4f_EachAdmitTheirOwnTypes(string subtype, string level, bool permitted)
    {
        var a37 = CodedAtLevel(4, level, "A37", Annot(subtype));
        if (permitted)
            Assert.Empty(a37);
        else
            Assert.Single(a37);
    }

    // -------------------------------------------------------------- A38: /F

    [Theory]
    [InlineData(1, "Text", "/AP << /N 4 0 R >>", Box, true)]                // no /F
    [InlineData(2, "Text", "/AP << /N 4 0 R >>", Box, true)]
    [InlineData(1, "Popup", "", Box, true)]                                 // PDF/A-1 exempts nothing
    [InlineData(2, "Popup", "", Box, false)]
    [InlineData(2, "Text", "", "/Rect [5 5 5 5]", true)]                    // zero size: PDF/A-4 only
    [InlineData(4, "Text", "", "/Rect [5 5 5 5]", false)]
    [InlineData(4, "Text", "", "/Rect [5 5 5 40]", true)]                   // zero width alone is not zero size
    [InlineData(4, "Text", "", "/Rect [5 5 40 5]", true)]
    [InlineData(2, "Text", "/F 4 /AP << /N 4 0 R >>", Box, false)]
    [InlineData(2, "Text", "/F 28 /AP << /N 4 0 R >>", Box, false)]          // NoZoom, NoRotate alongside Print
    [InlineData(2, "Text", "/F 0 /AP << /N 4 0 R >>", Box, true)]            // Print clear
    [InlineData(2, "Text", "/F 6 /AP << /N 4 0 R >>", Box, true)]            // Hidden
    [InlineData(2, "Text", "/F 5 /AP << /N 4 0 R >>", Box, true)]            // Invisible
    [InlineData(2, "Text", "/F 36 /AP << /N 4 0 R >>", Box, true)]           // NoView
    [InlineData(2, "Text", "/F 260 /AP << /N 4 0 R >>", Box, true)]          // ToggleNoView, from PDF/A-2
    [InlineData(1, "Text", "/F 260 /AP << /N 4 0 R >>", Box, false)]
    public void A38_TheFlags_AreReadAsEachPartReadsThem(int part, string subtype, string extra, string rect, bool reported)
    {
        var a38 = Coded(part, "A38", Annot(subtype, extra, rect));
        if (reported)
            Assert.Contains("page 1: /" + subtype, Assert.Single(a38).Message, StringComparison.Ordinal);
        else
            Assert.Empty(a38);
    }

    // -------------------------------------------------------------- A39: appearances

    [Theory]
    [InlineData(2, "Text", "/F 4", Box, "has no /AP")]
    [InlineData(4, "Text", "/F 4", Box, "has no /AP")]
    [InlineData(2, "Square", "/F 4 /AP << /N 4 0 R /D 4 0 R >>", Box, "/AP holds /D")]
    [InlineData(1, "Square", "/F 4 /AP << /N 4 0 R /R 4 0 R >>", Box, "/AP holds /R")]
    [InlineData(2, "Square", "/F 4 /AP << /N << /On 4 0 R >> >>", Box, "/AP /N is not a stream")]
    [InlineData(1, "Widget", "/F 4 /FT /Btn /AP << /N 4 0 R >>", Box, "is not a subdictionary of appearance streams")]
    [InlineData(2, "Widget", "/F 4 /FT /Btn /AP << /N << /On 4 0 R /Off 5 >> >>", Box, "is not a subdictionary of appearance streams")]
    [InlineData(2, "Widget", "/F 4 /FT /Btn /AP << /N << >> >>", Box, "is not a subdictionary of appearance streams")]
    [InlineData(1, "Widget", "/F 4 /FT /Tx", Box, "has no /AP (ISO 19005-1 6.9")]              // PDF/A-1's one required appearance
    [InlineData(1, "Widget", "/F 4 /FT /Sig", "/Rect [5 5 5 5]", "has no /AP (ISO 19005-1 6.9")]  // a zero-size widget too
    public void A39_AnAppearanceThePartDoesNotAdmit_IsReported(int part, string subtype, string extra, string rect, string expected)
    {
        Assert.Contains(expected, Assert.Single(Coded(part, "A39", Annot(subtype, extra, rect))).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "Text", "/F 4", Box)]                                        // PDF/A-1 requires none but a widget's
    [InlineData(2, "Link", "/F 4", Box)]
    [InlineData(2, "Popup", "/F 4", Box)]
    [InlineData(2, "Text", "/F 4", "/Rect [5 5 5 5]")]
    [InlineData(4, "Projection", "/F 4", Box)]
    [InlineData(2, "Square", "/F 4 /AP << /N 4 0 R >>", Box)]
    [InlineData(2, "Widget", "/F 4 /FT /Btn /AP << /N << /On 4 0 R /Off 4 0 R >> >>", Box)]
    [InlineData(2, "Widget", "/F 4 /FT /Tx /AP << /N 4 0 R >>", Box)]
    public void A39_AnAppearanceThePartAdmits_IsAccepted(int part, string subtype, string extra, string rect)
    {
        Assert.Empty(Coded(part, "A39", Annot(subtype, extra, rect)));
        // Potency: the same annotation beside one with no appearance is judged.
        Assert.Single(Coded(part, "A39", Annot(subtype, extra, rect), Annot(part == 1 ? "Widget" : "Square", "/F 4")));
    }

    [Fact]
    public void A39_AButtonsTypeInheritedFromItsParentField_DecidesTheAppearanceKind()
    {
        // Object 5 is the widget, object 6 its parent field carrying /FT /Btn.
        string widget = "<< /Type /Annot /Subtype /Widget " + Box + " /F 4 /Parent 6 0 R /AP << /N 4 0 R >> >>";
        const string field = "<< /FT /Btn /T (b) /Kids [5 0 R] >>";
        Assert.Contains("is not a subdictionary of appearance streams",
            Assert.Single(CodedWith(2, null, "A39", new[] { widget }, new[] { field })).Message, StringComparison.Ordinal);
        // Control: the same widget under a text field draws a stream, as it should.
        Assert.Empty(CodedWith(2, null, "A39", new[] { widget }, new[] { field.Replace("/Btn", "/Tx", StringComparison.Ordinal) }));
    }

    // -------------------------------------------------------------- A85: PDF/A-1's annotation colours

    /// <summary>
    /// ISO 19005-1 6.5.3 (the published profiles rules 6.5.3-1 and -3, a public PDF/A suite's 6-5-3-t01 and -t03): an annotation's
    /// <c>/CA</c> is 1.0; it holds <c>/C</c> or <c>/IC</c> only when the PDF/A-1 output intent's profile
    /// is RGB. PDF/A-2 on permit both.
    /// </summary>
    [Theory]
    [InlineData(1, "/CA 1.0", "RGB ", null)]
    [InlineData(1, "/CA 0.5", "RGB ", "/Square /CA 0.5")]
    [InlineData(1, "/C [1 0 0]", "RGB ", null)]
    [InlineData(1, "/C [1 0 0]", "CMYK", "/Square holds /C and the output intent's profile is 'CMYK'")]
    [InlineData(1, "/IC [1 0 0]", null, "/Square holds /IC and the output intent's profile is absent or unreadable")]
    [InlineData(2, "/CA 0.5 /C [1 0 0]", "CMYK", null)]
    public void A85_PdfA1_AnnotationOpacityAndColours(int part, string extra, string? profileColourSpace, string? expected)
    {
        string annotation = "<< /Type /Annot /Subtype /Square /Rect [10 10 50 50] /F 4 /AP << /N 4 0 R >> " + extra + " >>";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R" + (profileColourSpace == null ? "" : " /OutputIntents [6 0 R]") + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots [5 0 R] >>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 40 40] /Length 0 >>\nstream\n\nendstream",
            annotation,
        };
        if (profileColourSpace != null)
        {
            var profile = new byte[132];
            profile[3] = 132;
            profile[8] = 2;
            Encoding.ASCII.GetBytes("mntr").CopyTo(profile, 12);
            Encoding.ASCII.GetBytes(profileColourSpace).CopyTo(profile, 16);
            Encoding.ASCII.GetBytes("XYZ ").CopyTo(profile, 20);
            Encoding.ASCII.GetBytes("acsp").CopyTo(profile, 36);
            objects.Add("<< /Type /OutputIntent /S /GTS_PDFA1 /OutputConditionIdentifier (sRGB) /DestOutputProfile 7 0 R >>");
            objects.Add("<< /N " + (profileColourSpace == "CMYK" ? "4" : "3") + " /Length 132 >>\nstream\n" + Encoding.Latin1.GetString(profile) + "\nendstream");
        }
        var sb = new StringBuilder("%PDF-1.4\n");
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
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");

        var a85 = PdfAValidator.Validate(new MemoryStream(Encoding.Latin1.GetBytes(sb.ToString())), (PdfAConformancePart)part)
            .Violations.Where(v => v.Code == "A85").ToList();
        if (expected == null) Assert.Empty(a85);
        else Assert.Contains(expected, Assert.Single(a85).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ManyFindings_NameTheFirstFiveAndCountTheRest()
    {
        var annotations = Enumerable.Range(0, 8).Select(_ => Annot("Sound")).ToArray();
        var a37 = Assert.Single(Coded(2, "A37", annotations));
        Assert.Contains("and 3 more", a37.Message, StringComparison.Ordinal);
        Assert.Equal(5, a37.Message.Split("page 1: /Sound").Length - 1);
    }

    // -------------------------------------------------------------- the converter's repairs

    private static (PdfAConversionResult Result, byte[] Output) Convert(byte[] pdf)
    {
        var output = new MemoryStream();
        var result = PdfAConverter.ConvertToPdfA2b(new MemoryStream(pdf), output,
            new PdfAConversionOptions { IccProfile = IccProfileFixtures.Rgb() });
        return (result, output.ToArray());
    }

    private static List<PdfObject> AnnotationsOf(byte[] pdf, PdfName key)
    {
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(pdf));
        var annots = (PdfArray)doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Annots]);
        return Enumerable.Range(0, annots.Count)
            .Select(i => doc.ResolveObject(((PdfDictionary)doc.ResolveObject(annots[i]))[key]))
            .ToList();
    }

    [Fact]
    public void TheConverter_SetsThePrintFlag_WhereNothingHidesTheAnnotation()
    {
        var (result, output) = Convert(Document(null, new[]
        {
            Annot("Link", "", Box),                                   // no /F
            Annot("Square", "/F 24 /AP << /N 4 0 R >>", Box),       // NoZoom | NoRotate, no Print
        }));

        Assert.DoesNotContain(result.Blockers, b => b.StartsWith("A38", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("Set the Print flag on 2 annotation(s)", StringComparison.Ordinal));
        var flags = AnnotationsOf(output, PdfName.F).Select(f => ((PdfNumber)f).IntValue).ToList();
        Assert.Equal(new[] { 4, 28 }, flags);
    }

    [Fact]
    public void TheConverter_LeavesAHiddenAnnotationHidden_AndBlocks()
    {
        // Hidden and not printing: making it print would show what its author hid.
        var (result, output) = Convert(Document(null, new[] { Annot("Square", "/F 2 /AP << /N 4 0 R >>", Box) }));

        Assert.Contains(result.Blockers, b => b.StartsWith("A38", StringComparison.Ordinal));
        Assert.False(result.DeclaredPdfAIdentifier);
        Assert.Equal(2, ((PdfNumber)AnnotationsOf(output, PdfName.F)[0]).IntValue);
    }

    [Fact]
    public void TheConverter_KeepsOnlyTheNormalAppearance()
    {
        var (result, output) = Convert(Document(null, new[] { Annot("Square", "/F 4 /AP << /N 4 0 R /D 4 0 R /R 4 0 R >>", Box) }));

        Assert.DoesNotContain(result.Blockers, b => b.StartsWith("A39", StringComparison.Ordinal));
        var ap = (PdfDictionary)AnnotationsOf(output, PdfName.AP)[0];
        Assert.Equal(new[] { "N" }, ap.Keys.Select(k => k.Value).ToArray());
        Assert.Contains(result.Warnings, w => w.Contains("normal appearance of 1", StringComparison.Ordinal));
    }

    [Fact]
    public void TheConverter_RepairsADirectAnnotationThroughItsPage()
    {
        // The annotation written inside the page's /Annots array, not as an object of its own.
        byte[] pdf = Document(null, Array.Empty<string>());
        string text = Encoding.ASCII.GetString(pdf)
            .Replace("/Annots []", "/Annots [<< /Type /Annot /Subtype /Link /Rect [10 10 50 50] >>]", StringComparison.Ordinal);
        byte[] rebuilt = Reflow(text);

        var (result, output) = Convert(rebuilt);
        Assert.DoesNotContain(result.Blockers, b => b.StartsWith("A38", StringComparison.Ordinal));
        Assert.Equal(4, ((PdfNumber)AnnotationsOf(output, PdfName.F)[0]).IntValue);
    }

    /// <summary>Re-lays a classic file's objects so its cross-reference offsets are right again.</summary>
    private static byte[] Reflow(string pdf)
    {
        var objects = System.Text.RegularExpressions.Regex.Matches(pdf, @"\d+ 0 obj\n(.*?)\nendobj", System.Text.RegularExpressions.RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value).ToList();
        var sb = new StringBuilder("%PDF-1.7\n");
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
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
