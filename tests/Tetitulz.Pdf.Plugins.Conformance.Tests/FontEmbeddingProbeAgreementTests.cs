// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Plugins.Conformance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// PDF/A's A06, PDF/UA's UA25 and PDF/X's X04 answer "is this font embedded?" the same way.
/// </summary>
/// <remarks>
/// <para>Three validators carried the same walk and disagreed on every edge of it. Two of the
/// three disagreements made <c>PdfAValidator</c> give a <b>wrong answer</b>, in both directions:
/// a false report on a conforming file, and silence on a non-conforming one.</para>
///
/// <para><b>Each case asserts across the validators rather than against one</b>, because each was
/// internally consistent — no test of a single validator could have surfaced the disagreement, and
/// that is the shape this repo's recurring defect takes.</para>
/// </remarks>
public class FontEmbeddingProbeAgreementTests
{
    /// <summary>
    /// <b>A Type 3 font is not "unembedded" — the requirement does not apply to it.</b> Its glyphs
    /// are content streams in <c>/CharProcs</c>; there is no font program. <c>PdfAValidator</c> had
    /// no Type 3 branch, so it fell through to "no descriptor at all" and raised a false A06 on a
    /// conforming file.
    /// </summary>
    [Fact]
    public void AType3FontIsNotReportedAsUnembeddedByAnyValidator()
    {
        byte[] pdf = BuildPdf(
            "<< /Font << /F1 5 0 R >> >>",
            (5, "<< /Type /Font /Subtype /Type3 /BaseFont /SelfDefining " +
                "/FontMatrix [0.001 0 0 0.001 0 0] /CharProcs << >> " +
                "/Encoding << /Type /Encoding >> /FirstChar 0 /LastChar 0 /Widths [0] >>"));

        Assert.DoesNotContain(PdfAValidator.Validate(new MemoryStream(pdf)).Violations,
            v => v.Code == "A06");
        Assert.DoesNotContain(PdfXValidator.Validate(new MemoryStream(pdf)).Violations,
            v => v.Code == "X04");
    }

    /// <summary>
    /// <b>A composite font whose descendants cannot be reached is reported, not skipped.</b>
    /// "Cannot confirm embedding" must not read as "conforms". <c>PdfAValidator</c> returned
    /// silently, so the font vanished from the report entirely.
    /// </summary>
    [Fact]
    public void ACompositeFontWithUnreachableDescendantsIsReported()
    {
        // /DescendantFonts names an object that is not an array.
        byte[] pdf = BuildPdf(
            "<< /Font << /F1 5 0 R >> >>",
            (5, "<< /Type /Font /Subtype /Type0 /BaseFont /BrokenComposite " +
                "/Encoding /Identity-H /DescendantFonts 6 0 R >>"),
            (6, "<< /NotAnArray true >>"));

        Assert.Contains(PdfAValidator.Validate(new MemoryStream(pdf)).Violations,
            v => v.Code == "A06" && v.Message.Contains("BrokenComposite"));
    }

    /// <summary>
    /// <b>A font with no <c>/Subtype</c> is still checked.</b> A malformed font dictionary is
    /// exactly where an unembedded program hides. <c>PdfAValidator</c> abandoned the whole check —
    /// under a comment stating that hazard as already fixed, which it was not.
    /// </summary>
    [Fact]
    public void AFontWithNoSubtypeIsStillCheckedForEmbedding()
    {
        byte[] pdf = BuildPdf(
            "<< /Font << /F1 5 0 R >> >>",
            (5, "<< /Type /Font /BaseFont /NoSubtypeAtAll /FontDescriptor 6 0 R >>"),
            (6, "<< /Type /FontDescriptor /FontName /NoSubtypeAtAll /Flags 32 >>"));

        Assert.Contains(PdfAValidator.Validate(new MemoryStream(pdf)).Violations,
            v => v.Code == "A06" && v.Message.Contains("NoSubtypeAtAll"));
    }

    /// <summary>
    /// The control for all three: an ordinary embedded font stays quiet everywhere. Without it the
    /// cases above could pass with the embedding test firing unconditionally.
    /// </summary>
    [Fact]
    public void AnEmbeddedFontIsReportedByNoValidator()
    {
        byte[] pdf = BuildPdf(
            "<< /Font << /F1 5 0 R >> >>",
            (5, "<< /Type /Font /Subtype /TrueType /BaseFont /ProperlyEmbedded " +
                "/FontDescriptor 6 0 R >>"),
            (6, "<< /Type /FontDescriptor /FontName /ProperlyEmbedded /Flags 32 /FontFile2 7 0 R >>"),
            (7, "<< /Length 1 >>\nstream\n \nendstream"));

        Assert.DoesNotContain(PdfAValidator.Validate(new MemoryStream(pdf)).Violations,
            v => v.Code == "A06");
        Assert.DoesNotContain(PdfXValidator.Validate(new MemoryStream(pdf)).Violations,
            v => v.Code == "X04");
    }

    /// <summary>
    /// <b>A key is not a program.</b> A <c>/FontFile3</c> naming an object the file does not hold,
    /// or a <c>/FontFile2</c> naming a dictionary, embeds nothing — PDF/A, PDF/UA and PDF/X all say
    /// so (the published profile's 6-2-11-4-1-t01-fail-a); the stream control beside them stays quiet.
    /// </summary>
    [Theory]
    [InlineData("/FontFile3 19 0 R", true)]
    [InlineData("/FontFile2 7 0 R", true)]
    [InlineData("/FontFile2 8 0 R", false)]
    public void AFontFileThatIsNoStream_EmbedsNothing(string entry, bool reported)
    {
        byte[] pdf = BuildPdf(
            "<< /Font << /F1 5 0 R >> >>",
            (5, "<< /Type /Font /Subtype /TrueType /BaseFont /Dangling /FontDescriptor 6 0 R >>"),
            (6, "<< /Type /FontDescriptor /FontName /Dangling /Flags 32 " + entry + " >>"),
            (7, "<< /NotAStream true >>"),
            (8, "<< /Length 1 >>\nstream\n \nendstream"));

        bool a06 = PdfAValidator.Validate(new MemoryStream(pdf)).Violations.Any(v => v.Code == "A06" && v.Message.Contains("Dangling"));
        bool x04 = PdfXValidator.Validate(new MemoryStream(pdf)).Violations.Any(v => v.Code == "X04" && v.Message.Contains("Dangling"));
        bool ua25 = PdfUaChecker.Validate(new MemoryStream(pdf)).Violations.Any(v => v.Code == "UA25" && v.Message.Contains("Dangling"));
        Assert.Equal(new[] { reported, reported, reported }, new[] { a06, x04, ua25 });
    }

    /// <summary>
    /// <b>The parity assertion.</b> On one document carrying both a conforming and a
    /// non-conforming font, PDF/A and PDF/X must name the same font — the disagreement was never
    /// visible from inside either one.
    /// </summary>
    [Fact]
    public void PdfAAndPdfXNameTheSameUnembeddedFont()
    {
        byte[] pdf = BuildPdf(
            "<< /Font << /F1 5 0 R /F2 7 0 R /F3 9 0 R >> >>",
            (5, "<< /Type /Font /Subtype /TrueType /BaseFont /Good /FontDescriptor 6 0 R >>"),
            (6, "<< /Type /FontDescriptor /FontName /Good /Flags 32 /FontFile2 10 0 R >>"),
            (7, "<< /Type /Font /Subtype /TrueType /BaseFont /Bad /FontDescriptor 8 0 R >>"),
            (8, "<< /Type /FontDescriptor /FontName /Bad /Flags 32 >>"),
            (9, "<< /Type /Font /Subtype /Type3 /BaseFont /Type3Exempt " +
                "/FontMatrix [0.001 0 0 0.001 0 0] /CharProcs << >> " +
                "/Encoding << /Type /Encoding >> /FirstChar 0 /LastChar 0 /Widths [0] >>"),
            (10, "<< /Length 1 >>\nstream\n \nendstream"));

        var a06 = PdfAValidator.Validate(new MemoryStream(pdf)).Violations
            .Where(v => v.Code == "A06").Select(v => v.Message).ToList();
        var x04 = PdfXValidator.Validate(new MemoryStream(pdf)).Violations
            .Where(v => v.Code == "X04").Select(v => v.Message).ToList();

        Assert.NotEmpty(a06);
        Assert.NotEmpty(x04);

        // Both name Bad, neither names Good, and neither names the Type 3 font.
        Assert.All(new[] { a06, x04 }, messages =>
        {
            string joined = string.Join(" | ", messages);
            Assert.Contains("Bad", joined, StringComparison.Ordinal);
            Assert.DoesNotContain("Good", joined, StringComparison.Ordinal);
            Assert.DoesNotContain("Type3Exempt", joined, StringComparison.Ordinal);
        });
    }

    // ------------------------------------------------------------------

    private static byte[] BuildPdf(string pageResources, params (int Num, string Body)[] extra)
    {
        var bytes = new List<byte>();
        var offsets = new Dictionary<int, int>();
        void Emit(string s) => bytes.AddRange(Encoding.ASCII.GetBytes(s));
        void Obj(int n, string body)
        {
            offsets[n] = bytes.Count;
            Emit($"{n} 0 obj\n{body}\nendobj\n");
        }

        // Text in every /Fn the page names: A06 judges fonts used for rendering, so a font only
        // named is not one (X04 reads every named font either way).
        var shown = System.Text.RegularExpressions.Regex.Matches(pageResources, @"/(F\d+) ")
            .Select(m => "/" + m.Groups[1].Value + " 1 Tf (x) Tj").ToList();
        string content = shown.Count == 0 ? "q Q" : "BT " + string.Join(" ", shown) + " ET";

        Emit("%PDF-1.4\n");
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R " +
               $"/Resources {pageResources} >>");
        Obj(4, $"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        foreach (var (num, body) in extra) Obj(num, body);

        int maxObj = extra.Length == 0 ? 4 : Math.Max(4, extra.Max(e => e.Num));
        int xref = bytes.Count;

        var sb = new StringBuilder();
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", maxObj + 1);
        for (int n = 1; n <= maxObj; n++)
            sb.AppendFormat("{0:D10} 00000 n \n", offsets.TryGetValue(n, out int o) ? o : 0);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n",
            maxObj + 1, xref);
        Emit(sb.ToString());

        return bytes.ToArray();
    }
}
