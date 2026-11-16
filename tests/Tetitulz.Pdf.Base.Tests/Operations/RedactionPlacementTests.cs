// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A region removes the glyph that stands in it, where the page draws it. Each case places a glyph in
/// a way the plain advance along the baseline does not reach, puts a small region on that glyph
/// alone, and reads which of the strings the output still shows; the position of each is worked out
/// here from the specification, and the raster renderer agrees with it for every case.
/// </summary>
public sealed class RedactionPlacementTests
{
    private const string Helvetica = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";
    private const string SquareGlyph = "1000 0 d0 0 0 800 800 re f";

    private static string S(string body, string dict = "") => $"<< {dict} /Length {body.Length} >>\nstream\n{body}\nendstream";

    /// <summary>A Type 3 font whose codes 65 and 66 draw object 7.</summary>
    private static string Type3(string matrix = "[0.001 0 0 0.001 0 0]", string widths = "[1000 1000]", string more = "") =>
        $"<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000] /FontMatrix {matrix} /CharProcs << /a 7 0 R /b 7 0 R >> " +
        $"/Encoding << /Differences [65 /a /b] >> /FirstChar 65 /LastChar 66 /Widths {widths} {more} >>";

    private const string Vertical =
        "<< /Type /Font /Subtype /Type0 /BaseFont /Arial /Encoding /Identity-V /DescendantFonts " +
        "[<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Arial /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /DW 1000 >>] >>";

    private const string FormWithHelvetica =
        "/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>";

    private static byte[] Pdf(string content, string font, string resources = "", params string[] extra)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> {resources} >> >>",
            S(content), font, "null",
        };
        bodies.AddRange(extra);

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets) sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static byte[] Redact(byte[] pdf, float x, float y, float width, float height)
    {
        using var output = new MemoryStream();
        PdfRedactionEngine.Redact(new MemoryStream(pdf), output, new[]
        {
            new PdfRedactionArea { PageIndex = 0, X = x, Y = y, Width = width, Height = height },
        });
        return output.ToArray();
    }

    /// <summary>Every string a show operator of the file still shows, joined.</summary>
    private static string Shown(byte[] pdf)
    {
        var shown = new StringBuilder();
        using PdfDocument doc = PdfDocument.Open(new MemoryStream(pdf));
        foreach (CrossReferenceEntry e in doc.CrossReferenceTable.AllEntries)
        {
            if (e.Type == CrossReferenceEntry.EntryType.Free
                || !(doc.GetObject(e.ObjectNumber) is PdfStream stream)
                || !stream.TryGetDecodedData(1 << 24, out byte[] data, out _)) continue;
            foreach (ContentStreamOperation op in ContentStreamParser.Parse(data))
            {
                if (op.Operands.Length == 0) continue;
                if ((op.Operator == "Tj" || op.Operator == "'") && op.Operands[0] is PdfString one)
                    shown.Append(Encoding.Latin1.GetString(one.RawBytes));
                else if (op.Operator == "TJ" && op.Operands[0] is PdfArray many)
                    foreach (PdfObject item in many)
                        if (item is PdfString part) shown.Append(Encoding.Latin1.GetString(part.RawBytes));
            }
        }
        return shown.ToString();
    }

    public static IEnumerable<object[]> Placements() => new[]
    {
        // A Type 3 glyph paints where its procedure says: (10+96, 10+96) for 8000 8000 at size 12.
        Row("type 3 painting away from its origin", "BT /F1 12 Tf 10 10 Td (AB) Tj ET", Type3(), "", new[] { S("1000 0 d0 8000 8000 800 800 re f") }, 106, 106, 5, 5, "A", "B"),
        Row("type 3 painting below its origin", "BT /F1 12 Tf 80 180 Td (A) Tj 100 0 Td (B) Tj ET", Type3(), "", new[] { S("1000 0 d0 0 -8000 800 800 re f") }, 82, 86, 5, 5, "A", "B"),
        // cm in the procedure: 4000 + 5*200 is the square's far edge, so 48 to 60 at size 12.
        Row("type 3 under a matrix in its procedure", "BT /F1 12 Tf 10 10 Td (A) Tj 120 0 Td (B) Tj ET", Type3(), "", new[] { S("1000 0 d0 q 5 0 0 5 4000 4000 cm 0 0 200 200 re f Q") }, 60, 60, 5, 5, "A", "B"),
        Row("type 3 painting an inline image", "BT /F1 12 Tf 10 10 Td (A) Tj 120 0 Td (B) Tj ET", Type3(), "", new[] { S("1000 0 d0 800 0 0 800 3000 3000 cm BI /W 1 /H 1 /BPC 8 /CS /G ID \u0000 EI") }, 48, 48, 5, 5, "A", "B"),
        Row("type 3 stroking a wide line", "BT /F1 12 Tf 10 10 Td (A) Tj 150 0 Td (B) Tj ET", Type3(), "", new[] { S("1000 0 d0 1000 w 1 M 0 3000 m 100 3000 l S") }, 14, 44, 2, 2, "A", "B"),
        Row("type 3 drawing a named image", "BT /F1 12 Tf 10 10 Td (A) Tj 150 0 Td (B) Tj ET", Type3(more: "/Resources << /XObject << /I 8 0 R >> >>"), "",
            new[] { S("1000 0 d0 800 0 0 800 3000 3000 cm /I Do"), S("\u0000", "/Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray") }, 48, 48, 5, 5, "A", "B"),

        // Where its advance says it stands, though it paints elsewhere: a search finds it there.
        Row("type 3 at its text position", "BT /F1 12 Tf 10 10 Td (A) Tj 150 0 Td (B) Tj ET", Type3(), "", new[] { S("1000 0 d0 8000 8000 800 800 re f") }, 12, 12, 3, 3, "A", "B"),

        // Advances through the font matrix: 1000 x 0.01 x 12 is 120 points a glyph.
        Row("type 3 advancing through a large matrix", "BT /F1 12 Tf 10 60 Td (AB) Tj ET", Type3("[0.01 0 0 0.01 0 0]"), "", new[] { S(SquareGlyph) }, 135, 65, 5, 5, "B", "A"),
        // d0's 3000 outranks /Widths' 100: the second glyph is 36 points on.
        Row("type 3 advancing by d0", "BT /F1 12 Tf 10 60 Td (AB) Tj ET", Type3(widths: "[100 100]"), "", new[] { S("3000 0 d0 0 0 800 800 re f") }, 49, 62, 3, 3, "B", "A"),
        // A rotated matrix turns the square to x -96..0, y 0..96 about the origin.
        Row("type 3 under a rotated matrix", "BT /F1 12 Tf 80 40 Td (A) Tj 110 0 Td (B) Tj ET", Type3("[0 0.01 -0.01 0 0 0]"), "", new[] { S(SquareGlyph) }, 20, 100, 5, 5, "A", "B"),

        // Pen at 180, down 20 a glyph, drawn at the pen less the position vector (10, 17.6).
        Row("vertical second glyph", "BT /F1 20 Tf 100 180 Td <000100410002> Tj ET", Vertical, "", null, 95, 148, 10, 4, "\u0000A", "\u0000\u0002"),
        Row("vertical after a TJ number, which moves down", "BT /F1 20 Tf 100 180 Td [<0041> 500 <0042>] TJ ET", Vertical, "", null, 95, 128, 10, 3, "\u0000B", "\u0000A"),
        Row("vertical by the DW2 advance", "BT /F1 10 Tf 100 180 Td <00410042> Tj ET", Vertical.Replace("/DW 1000", "/DW 1000 /DW2 [880 -3000]"), "", null, 98, 142, 4, 4, "\u0000B", "\u0000A"),

        // The size a graphics state sets: Helvetica's A is 667, 20 points at size 30.
        Row("font and size from a graphics state", "/G0 gs BT 10 100 Td (AB) Tj ET", Helvetica, "/ExtGState << /G0 << /Font [5 0 R 30] >> >>", null, 45, 105, 3, 3, "B", "A"),
        // Q leaves the text matrices: B follows the four As at 42.
        Row("Q keeps the text matrix", "BT /F1 12 Tf 10 100 Td q (AAAA) Tj Q (B) Tj ET", Helvetica, "", null, 44, 102, 3, 3, "B", "A"),
        // And the line matrix: 100, less the Td's 30 inside q, less the leading's 14.
        Row("Q keeps the line matrix", "BT /F1 12 Tf 14 TL 10 100 Td (A) Tj q 0 -30 Td Q T* (B) Tj ET", Helvetica, "", null, 12, 58, 3, 3, "B", "A"),
        // Q puts the matrix back inside a glyph's procedure, whose square is at the origin.
        Row("type 3 restoring its matrix", "BT /F1 12 Tf 10 10 Td (A) Tj 150 0 Td (B) Tj ET", Type3(), "", new[] { S("1000 0 d0 q 100 0 0 100 0 0 cm Q 0 0 800 800 re f") }, 165, 12, 3, 3, "B", "A"),
        // Stroked in the procedure at the default miter of 10: 200 wide reaches 1000 units, 12 points.
        Row("type 3 stroke to its miter", "BT /F1 12 Tf 10 10 Td (A) Tj 150 0 Td (B) Tj ET", Type3(), "", new[] { S("1000 0 d0 200 w 0 3000 m 100 3000 l S") }, 14, 54, 2, 2, "A", "B"),
        Row("text before any Tf at the default size", "BT 10 100 Td (AB) Tj ET", Helvetica, "", null, 20, 102, 3, 3, "B", "A"),

        // A form starts from the state of the stream drawing it.
        Row("form using the font size it was drawn with", "BT /F1 30 Tf ET /X0 Do", Helvetica, "/XObject << /X0 7 0 R >>", new[] { S("BT 10 100 Td (AB) Tj ET", FormWithHelvetica) }, 35, 105, 5, 5, "B", "A"),
        Row("form using the rise it was drawn with", "BT /F1 12 Tf 60 Ts ET /X0 Do", Helvetica, "/XObject << /X0 7 0 R >>", new[] { S("BT 40 50 Td (A) Tj 100 0 Td (B) Tj ET", FormWithHelvetica) }, 42, 112, 3, 3, "A", "B"),
        Row("form using the scale it was drawn with", "BT /F1 12 Tf 300 Tz ET /X0 Do", Helvetica, "/XObject << /X0 7 0 R >>", new[] { S("BT 5 100 Td (AB) Tj ET", FormWithHelvetica) }, 33, 102, 3, 3, "B", "A"),
        // The form's text ends at 18, and the text of the page drawing it goes on from there.
        Row("text after a form in a text object", "BT /F1 12 Tf 10 100 Td /X0 Do (B) Tj ET", Helvetica, "/XObject << /X0 7 0 R >>", new[] { S("BT /F1 12 Tf 10 150 Td (A) Tj ET", FormWithHelvetica) }, 20, 150, 3, 3, "B", "A"),

    };

    private static object[] Row(string name, string content, string font, string resources, string[] extra, float x, float y, float w, float h, string gone, string kept)
        => new object[] { name, content, font, resources, extra ?? Array.Empty<string>(), x, y, w, h, gone, kept };

    [Theory]
    [MemberData(nameof(Placements))]
    public void A_region_on_a_glyph_takes_it_and_leaves_the_rest(string name, string content, string font, string resources, string[] extra,
                                                                 float x, float y, float w, float h, string gone, string kept)
    {
        Assert.NotNull(name);
        byte[] pdf = Pdf(content, font, resources, extra);
        Assert.Contains(gone, Shown(pdf), StringComparison.Ordinal);

        string left = Shown(Redact(pdf, x, y, w, h));

        Assert.DoesNotContain(gone, left, StringComparison.Ordinal);
        Assert.Contains(kept, left, StringComparison.Ordinal);
    }

    [Fact]
    public void An_advance_of_nothing_moves_nothing()
    {
        // A at 10, nothing wide; B at 10 to 22; C at 22 to 34. Half an em for the A would put C at 28 to 40.
        byte[] pdf = Pdf("BT /F1 12 Tf 10 100 Td (ABC) Tj ET",
                         "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /FirstChar 65 /LastChar 67 /Widths [0 1000 1000] >>");

        Assert.Contains("C", Shown(Redact(pdf, 36, 100, 3, 8)), StringComparison.Ordinal);
        Assert.DoesNotContain("C", Shown(Redact(pdf, 30, 100, 3, 8)), StringComparison.Ordinal);

        // It draws all the same: half an em of room is what a region on it finds it by.
        Assert.DoesNotContain("A", Shown(Redact(pdf, 12, 100, 2, 8)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("in place", SquareGlyph)]
    [InlineData("far away", "1000 0 d0 8000 8000 800 800 re f")]
    public void A_type_3_glyph_outside_the_region_stays(string name, string procedure)
    {
        Assert.NotNull(name);
        byte[] pdf = Pdf("BT /F1 12 Tf 10 10 Td (A) Tj ET", Type3(), "", S(procedure));

        Assert.Contains("A", Shown(Redact(pdf, 170, 10, 20, 20)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_type_3_stroke_reaches_no_further_than_its_miter_limit_says()
    {
        // 1000 wide at a limit of 1 reaches 6 points at size 12, from y 46; at the default limit of 10, 60.
        byte[] pdf = Pdf("BT /F1 12 Tf 10 10 Td (A) Tj ET", Type3(), "", S("1000 0 d0 1000 w 1 M 0 3000 m 100 3000 l S"));

        Assert.Contains("A", Shown(Redact(pdf, 12, 70, 3, 3)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a shading", "1000 0 d0 /S sh")]
    [InlineData("text", "1000 0 d0 BT /H 800 Tf 5000 5000 Td (x) Tj ET")]
    [InlineData("a form or a missing image", "1000 0 d0 /Nothing Do")]
    [InlineData("a stroke after a graphics state", "1000 0 d0 /G gs 0 0 m 10 0 l S")]
    [InlineData("a coordinate past a float's range", "1000 0 d0 99999999999999999999999999999999999999999.0 0 m 0 0 l f")]
    public void A_type_3_glyph_painting_what_cannot_be_placed_goes_with_any_region(string name, string procedure)
    {
        Assert.NotNull(name);
        byte[] pdf = Pdf("BT /F1 12 Tf 10 10 Td (A) Tj ET", Type3(), "", S(procedure));

        Assert.DoesNotContain("A", Shown(Redact(pdf, 170, 170, 20, 20)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_type_3_glyph_past_the_procedure_ceiling_goes_with_any_region_Hostile()
    {
        string over = "1000 0 d0 " + new string(' ', RedactionType3FontCeiling + 1) + " 0 0 800 800 re f";
        byte[] oversize = Pdf("BT /F1 12 Tf 10 10 Td (A) Tj ET", Type3(), "", S(over));
        Assert.DoesNotContain("A", Shown(Redact(oversize, 170, 170, 20, 20)), StringComparison.Ordinal);

        // Control: the same procedure one byte under the ceiling is read, and stays.
        string under = "1000 0 d0 " + new string(' ', RedactionType3FontCeiling - 40) + " 0 0 800 800 re f";
        byte[] allowed = Pdf("BT /F1 12 Tf 10 10 Td (A) Tj ET", Type3(), "", S(under));
        Assert.Contains("A", Shown(Redact(allowed, 170, 170, 20, 20)), StringComparison.Ordinal);
    }

    private const int RedactionType3FontCeiling = 1 << 20;

    [Fact]
    public void Glyph_procedures_past_a_redactions_allowance_go_with_any_region_Hostile()
    {
        // One stream of a megabyte of spaces, deflated to a few kilobytes, drawn by seventy codes: the
        // sixty-fourth megabyte spends the allowance, and each glyph after it is not read.
        byte[] raw = Encoding.ASCII.GetBytes("1000 0 d0 " + new string(' ', RedactionType3FontCeiling - 100) + " 0 0 800 800 re f");
        byte[] deflated = FlateEncodeFilter.Encode(raw);

        var names = new StringBuilder();
        var procs = new StringBuilder();
        var widths = new StringBuilder();
        var show = new StringBuilder();
        for (int code = 100; code < 170; code++)
        {
            names.Append("/g").Append(code).Append(' ');
            procs.Append("/g").Append(code).Append(" 7 0 R ");
            widths.Append("0 ");
            show.Append(code.ToString("X2"));
        }

        string font = $"<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000] /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs << {procs} >> " +
                      $"/Encoding << /Differences [100 {names}] >> /FirstChar 100 /LastChar 169 /Widths [{widths}] >>";
        string stream = $"<< /Filter /FlateDecode /Length {deflated.Length} >>\nstream\n{Encoding.Latin1.GetString(deflated)}\nendstream";

        byte[] pdf = Pdf($"BT /F1 12 Tf 10 10 Td <{show}> Tj ET", font, "", stream);
        string left = Shown(Redact(pdf, 170, 170, 20, 20));

        Assert.Contains("d", left, StringComparison.Ordinal);       // the first code stays: it was read, and paints elsewhere
        Assert.DoesNotContain("©", left, StringComparison.Ordinal); // the last goes: it could not be read
    }

    [Fact]
    public void A_refused_procedure_spends_half_the_allowance_Hostile()
    {
        // Thirty refusals halve it below the 200 bytes of the last procedure, which then cannot be
        // read; were they free, it would be read and stay.
        byte[] raw = Encoding.ASCII.GetBytes("1000 0 d0 " + new string(' ', RedactionType3FontCeiling + 100) + " 0 0 800 800 re f");
        byte[] deflated = FlateEncodeFilter.Encode(raw);

        var names = new StringBuilder();
        var procs = new StringBuilder("/last 8 0 R ");
        var widths = new StringBuilder();
        var show = new StringBuilder();
        for (int code = 100; code < 130; code++)
        {
            names.Append("/g").Append(code).Append(' ');
            procs.Append("/g").Append(code).Append(" 7 0 R ");
            widths.Append("0 ");
            show.Append(code.ToString("X2"));
        }

        string font = $"<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000] /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs << {procs} >> " +
                      $"/Encoding << /Differences [100 {names}/last] >> /FirstChar 100 /LastChar 130 /Widths [{widths}0] >>";
        string oversize = $"<< /Filter /FlateDecode /Length {deflated.Length} >>\nstream\n{Encoding.Latin1.GetString(deflated)}\nendstream";

        // 0x82 is /last: it paints in place, far from the region.
        byte[] pdf = Pdf($"BT /F1 12 Tf 10 10 Td <{show}82> Tj ET", font, "", oversize, S(SquareGlyph + new string(' ', 200)));
        string left = Shown(Redact(pdf, 170, 170, 20, 20));

        Assert.DoesNotContain("\u0082", left, StringComparison.Ordinal);

        // Control: alone, the same glyph is read, and stays.
        byte[] alone = Pdf("BT /F1 12 Tf 10 10 Td <82> Tj ET", font, "", oversize, S(SquareGlyph + new string(' ', 200)));
        Assert.Contains("\u0082", Shown(Redact(alone, 170, 170, 20, 20)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_vertical_runs_dropped_glyph_is_made_up_by_a_positive_number()
    {
        byte[] pdf = Pdf("BT /F1 20 Tf 100 180 Td <000100410002> Tj ET", Vertical);
        byte[] output = Redact(pdf, 95, 148, 10, 4);

        double? number = null;
        using (PdfDocument doc = PdfDocument.Open(new MemoryStream(output)))
        {

            foreach (CrossReferenceEntry e in doc.CrossReferenceTable.AllEntries)
                if (e.Type != CrossReferenceEntry.EntryType.Free
                    && doc.GetObject(e.ObjectNumber) is PdfStream stream
                    && stream.TryGetDecodedData(1 << 24, out byte[] data, out _))
                    foreach (ContentStreamOperation op in ContentStreamParser.Parse(data))
                        if (op.Operator == "TJ" && op.Operands[0] is PdfArray array)
                            number = array.OfType<PdfNumber>().Single().DoubleValue;
        }

        // One em of downward advance, in thousandths: a TJ number moves a vertical run on when it is positive.
        Assert.Equal(1000d, number);
    }

    public static IEnumerable<object[]> StatesAPageLeaves() => new[]
    {
        new object[] { "a matrix", "2 0 0 2 50 50 cm", "" },
        new object[] { "a clip", "0 0 1 1 re W n", "" },
        new object[] { "a clip on a path it never ended", "0 0 1 1 re W", "" },
        new object[] { "a clear fill", "/G gs", "/ExtGState << /G << /ca 0 >> >>" },
        new object[] { "a blend mode that hides black", "/G gs", "/ExtGState << /G << /BM /Screen >> >>" },
        new object[] { "saved states", "q q 3 0 0 3 0 0 cm", "" },
        new object[] { "a stray restore then a matrix", "Q Q 2 0 0 2 50 50 cm", "" },
        new object[] { "a text object", "BT /F1 12 Tf 150 150 Td", "" },
        new object[] { "a hidden layer", "/OC /L BDC", "/Properties << /L 7 0 R >>" },
    };

    [Theory]
    [MemberData(nameof(StatesAPageLeaves))]
    public void The_cover_box_stands_over_the_region_whatever_the_page_left(string name, string leftBehind, string resources)
    {
        Assert.NotNull(name);
        // The art is a path, which a redaction covers and does not remove; only the box hides it.
        string content = "1 0 0 rg 20 20 160 160 re f " + leftBehind;
        string[] extra = name == "a hidden layer"
            ? new[] { "<< /Type /OCG /Name (L) >>" }
            : Array.Empty<string>();
        byte[] pdf = HiddenLayer(Pdf(content, Helvetica, resources, extra), name == "a hidden layer");

        byte[] output = Redact(pdf, 40, 40, 40, 40);
        RenderedPage page = PdfPageRenderer.RenderPageToPixels(new MemoryStream(output), 0, 72);

        AssertColour(page, 60, 200 - 60, r: 0, g: 0, b: 0);       // the box
        AssertColour(page, 120, 200 - 120, r: 255, g: 0, b: 0);   // the art beside it
        AssertBoxStartsFromNothingLeftOpen(output);
    }

    /// <summary>
    /// What the file says, since the renderer here forgives what another may not: this one starts each
    /// page inside a saved state, so a page's stray Q takes nothing of its own away.
    /// </summary>
    private static void AssertBoxStartsFromNothingLeftOpen(byte[] output)
    {
        using PdfDocument doc = PdfDocument.Open(new MemoryStream(output));
        var ops = ContentStreamParser.Parse(doc.Pages[0].GetContentStreamData()).ToList();

        int box = ops.FindLastIndex(op => op.Operator == "q");
        Assert.True(box > 0);
        Assert.Equal("q", ops[0].Operator);

        int depth = 0, marked = 0;
        bool inText = false, inPath = false;
        for (int i = 0; i < box; i++)
        {
            switch (ops[i].Operator)
            {
                case "q": depth++; break;
                case "Q": depth--; Assert.True(depth >= 0, "a Q took the bracket away"); break;
                case "BT": inText = true; break;
                case "ET": inText = false; break;
                case "BMC": case "BDC": marked++; break;
                case "EMC": marked--; break;
                case "m": case "l": case "c": case "v": case "y": case "re": case "h": case "W": case "W*": inPath = true; break;
                case "f": case "F": case "f*": case "B": case "B*": case "b": case "b*": case "S": case "s": case "n": inPath = false; break;
            }
        }

        Assert.Equal(0, depth);
        Assert.Equal(0, marked);
        Assert.False(inText, "a text object left open");
        Assert.False(inPath, "a path left open");
    }

    private static byte[] HiddenLayer(byte[] pdf, bool hide)
    {
        if (!hide) return pdf;
        string text = Encoding.Latin1.GetString(pdf);
        // The catalog's own object stays where the table says: the padding makes room for nothing,
        // so the layer's switch is written over a later copy of the catalog.
        int start = text.LastIndexOf("startxref", StringComparison.Ordinal);
        int prev = int.Parse(text.Substring(start + 9).Trim().Split('\n')[0].Trim());
        string catalog = "1 0 obj\n<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [7 0 R] /D << /OFF [7 0 R] >> >> >>\nendobj\n";
        int at = pdf.Length;
        int table = at + catalog.Length;
        return Encoding.Latin1.GetBytes(text + catalog +
            $"xref\n1 1\n{at:D10} 00000 n \ntrailer\n<< /Size 8 /Root 1 0 R /Prev {prev} >>\nstartxref\n{table}\n%%EOF\n");
    }

    private static void AssertColour(RenderedPage page, int x, int y, byte r, byte g, byte b)
    {
        int bytes = page.Stride / page.Width;
        int at = y * page.Stride + x * bytes;
        Assert.True(Math.Abs(page.Pixels[at] - r) < 8 && Math.Abs(page.Pixels[at + 1] - g) < 8 && Math.Abs(page.Pixels[at + 2] - b) < 8,
            $"at ({x},{y}): {page.Pixels[at]},{page.Pixels[at + 1]},{page.Pixels[at + 2]}, expected {r},{g},{b}");
    }

    [Fact]
    public void Stroked_text_reaches_as_far_as_its_miter_join()
    {
        const string text = "BT 1 Tr 20 w /F1 12 Tf 60 100 Td (A) Tj ET";
        byte[] stroked = Pdf(text, Helvetica);
        // 20 wide at a miter limit of 10 reaches 100 points; the glyph's own box tops out near 111.
        Assert.DoesNotContain("A", Shown(Redact(stroked, 60, 150, 5, 5)), StringComparison.Ordinal);

        // Control: filled text does not reach, and a thin stroke with a low limit neither.
        byte[] filled = Pdf("BT 20 w /F1 12 Tf 60 100 Td (A) Tj ET", Helvetica);
        Assert.Contains("A", Shown(Redact(filled, 60, 150, 5, 5)), StringComparison.Ordinal);
        byte[] limited = Pdf("BT 1 Tr 10 w 1 M /F1 12 Tf 60 100 Td (A) Tj ET", Helvetica);
        Assert.Contains("A", Shown(Redact(limited, 60, 150, 5, 5)), StringComparison.Ordinal);
    }

    [Fact]
    public void Stroked_text_takes_its_line_width_from_a_graphics_state()
    {
        byte[] pdf = Pdf("/G gs BT 1 Tr /F1 12 Tf 60 100 Td (A) Tj ET", Helvetica, "/ExtGState << /G << /LW 20 /ML 10 >> >>");
        Assert.DoesNotContain("A", Shown(Redact(pdf, 60, 150, 5, 5)), StringComparison.Ordinal);

        byte[] limited = Pdf("/G gs BT 1 Tr /F1 12 Tf 60 100 Td (A) Tj ET", Helvetica, "/ExtGState << /G << /LW 20 /ML 1 >> >>");
        Assert.Contains("A", Shown(Redact(limited, 60, 150, 5, 5)), StringComparison.Ordinal);
    }

    [Fact]
    public void Q_restores_the_line_width_and_a_form_inherits_it()
    {
        byte[] restored = Pdf("q 20 w Q BT 1 Tr /F1 12 Tf 60 100 Td (A) Tj ET", Helvetica);
        Assert.Contains("A", Shown(Redact(restored, 60, 150, 5, 5)), StringComparison.Ordinal);

        byte[] inherited = Pdf("20 w 1 Tr /X0 Do", Helvetica, "/XObject << /X0 7 0 R >>", S("BT /F1 12 Tf 60 100 Td (A) Tj ET", FormWithHelvetica));
        Assert.DoesNotContain("A", Shown(Redact(inherited, 60, 150, 5, 5)), StringComparison.Ordinal);
    }
}
