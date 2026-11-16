// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// Plain extraction reads a right-to-left line in the order its strings lie on the page, not the
/// order the content stream draws them: a producer that draws a line's pieces out of their places — a
/// list marker after its item, at the item's right — read back misordered though the page was right.
/// </summary>
/// <remarks>
/// The font maps a, b, c to alef, bet, gimel. "ג. אב" — a marker, then a word — lies on the page as
/// "בא" at the left (x 100) and ".ג" at the right (x 200), each string's glyphs in visual order, as a
/// shaped run is drawn. Every glyph is 500 units wide, so at 12 pt "ba" ends at 112.
/// </remarks>
public sealed class PlainExtractionDrawnOrderTests
{
    private const string Expected = "\u05D2. \u05D0\u05D1";

    /// <summary>The marker drawn first, at the right: read in the page's order.</summary>
    [Fact]
    public void ARightToLeftLineDrawnOutOfOrder_ReadsInThePagesOrder()
        => Assert.Equal(Expected, Extract("BT /F1 12 Tf 1 0 0 1 200 700 Tm (.c) Tj 1 0 0 1 100 700 Tm (ba) Tj ET"));

    /// <summary>The control: the same line drawn left to right reads the same, as it always did.</summary>
    [Fact]
    public void TheSameLineDrawnInOrder_ReadsTheSame()
        => Assert.Equal(Expected, Extract("BT /F1 12 Tf 1 0 0 1 100 700 Tm (ba) Tj 1 0 0 1 200 700 Tm (.c) Tj ET"));

    /// <summary>
    /// A left-to-right line drawn out of order is left as drawn: bidi does not read it, and nor does this —
    /// alone on its page, and below a right-to-left line, where the page holds right-to-left text and only
    /// the line's own test leaves it.
    /// </summary>
    [Fact]
    public void ALeftToRightLineDrawnOutOfOrder_IsLeftAsItWas()
    {
        Assert.Equal("WorldHello", Extract("BT /F1 12 Tf 1 0 0 1 200 700 Tm (World) Tj 1 0 0 1 100 700 Tm (Hello) Tj ET"));
        string page = Extract("BT /F1 12 Tf 1 0 0 1 100 720 Tm (ba) Tj "
            + "1 0 0 1 200 700 Tm (World) Tj 1 0 0 1 100 700 Tm (Hello) Tj ET");
        Assert.Equal("WorldHello", page.Split('\n').Last());
        Assert.Contains("\u05D0", page);   // potency: the right-to-left line is there
    }

    /// <summary>
    /// A string starting less than half an em before the one drawn ahead of it is not out of order:
    /// ".c" at 100, then "ba" at 96, four points back of a 12-point em, reads in drawn order — as the
    /// one string ".cba" does — where a sort would have put "ba" first.
    /// </summary>
    [Fact]
    public void AStringALittleBehindTheLast_IsNotOutOfOrder()
        => Assert.Equal(Extract("BT /F1 12 Tf 1 0 0 1 100 700 Tm (.cba) Tj ET"),
            Extract("BT /F1 12 Tf 1 0 0 1 100 700 Tm (.c) Tj 1 0 0 1 96 700 Tm (ba) Tj ET"));

    /// <summary>A turned line is not ordered by x — it does not run along x — so it reads as drawn, as before.</summary>
    [Fact]
    public void ARotatedLine_IsLeftAsDrawn()
        => Assert.Equal(Extract("BT /F1 12 Tf 0 1 -1 0 200 700 Tm (.cba) Tj ET"),
            Extract("BT /F1 12 Tf 0 1 -1 0 200 700 Tm (.c) Tj 0 1 -1 0 100 700 Tm (ba) Tj ET"));

    /// <summary>
    /// A line holding a form's text, which no shown string of the page accounts for, is left as drawn:
    /// ordered, it would lose the form's text. The control is the same line without the form.
    /// </summary>
    [Fact]
    public void ALineHoldingAFormsText_IsLeftAsDrawn()
    {
        const string form = "BT /F1 12 Tf 1 0 0 1 400 700 Tm (c) Tj ET";
        string text = Extract("/Fm1 Do BT /F1 12 Tf 1 0 0 1 200 700 Tm (.c) Tj 1 0 0 1 100 700 Tm (ba) Tj ET", form);
        Assert.Contains("\u05D2", text.Replace(".\u05D2", ""));   // the form's gimel survives beside the marker's
        Assert.NotEqual(Expected, text);
    }

    /// <summary>
    /// A right-to-left line whose left end is Latin — a Hebrew word ending in a code, drawn "XY" then
    /// gimel, bet, alef — reads back right to left, the Hebrew first: its ends disagree and most of its
    /// letters are Hebrew. Asked of its leftmost strong character, as until 2026-10-22, it read left to
    /// right, the code first. The control: the same letters with Latin the more read left to right.
    /// </summary>
    [Fact]
    public void ARightToLeftLineWithLatinAtItsLeftEnd_ReadsRightToLeft()
    {
        Assert.Equal("אבג XY", Extract("BT /F1 12 Tf 1 0 0 1 100 700 Tm (XY cba) Tj ET"));
        Assert.Equal("WXYZ אבג", Extract("BT /F1 12 Tf 1 0 0 1 100 700 Tm (WXYZ cba) Tj ET"));
    }

    /// <summary>
    /// A pointed letter drawn a glyph a string, as the Microsoft PS Class Driver draws one: in visual
    /// order, the points before their letter (UAX #9's L3) and placed inside it — "בָּא" as alef at 100,
    /// qamats (1) at 109, dagesh (2) at 108 and bet at 106, the marker drawn first so the line is
    /// ordered by x. The points go with their letter: by starting x alone they fall after it, and read
    /// back before it.
    /// </summary>
    [Fact]
    public void PointsDrawnBeforeTheirLetter_StayWithIt()
        => Assert.Equal("\u05D2. \u05D1\u05BC\u05B8\u05D0", Extract("BT /F1 12 Tf 1 0 0 1 200 700 Tm (.c) Tj "
            + "1 0 0 1 100 700 Tm (a) Tj 1 0 0 1 109 700 Tm (1) Tj 1 0 0 1 108 700 Tm (2) Tj "
            + "1 0 0 1 106 700 Tm (b) Tj ET"));

    /// <summary>
    /// A point more than an em from the letter after it is not that letter's: qamats at 150, bet at 106,
    /// stays where its x puts it — a word gap after the letters, read back before them; at 90, a word
    /// gap before them, read back after. The control is the point at 109, which joins the bet.
    /// </summary>
    [Fact]
    public void APointFarFromTheNextLetter_IsOrderedByItsOwnPlace()
    {
        const string head = "BT /F1 12 Tf 1 0 0 1 200 700 Tm (.c) Tj 1 0 0 1 100 700 Tm (a) Tj ";
        Assert.Equal("\u05D2. \u05B8 \u05D1\u05D0", Extract(head + "1 0 0 1 150 700 Tm (1) Tj 1 0 0 1 106 700 Tm (b) Tj ET"));
        Assert.Equal("\u05D2. \u05D1\u05B8\u05D0", Extract(head + "1 0 0 1 109 700 Tm (1) Tj 1 0 0 1 106 700 Tm (b) Tj ET"));
        Assert.Equal("\u05D2. \u05D1\u05D0 \u05B8", Extract(head + "1 0 0 1 90 700 Tm (1) Tj 1 0 0 1 106 700 Tm (b) Tj ET"));
    }

    /// <summary>
    /// A space string drawn on the string before it — the driver's space at 101, inside alef's 100–106 —
    /// separates nothing: "בא" reads whole. The control: a space at 106, where alef ends, still parts
    /// the two letters — the bet at 107, less than a word gap away, so the space is the string's.
    /// </summary>
    [Fact]
    public void ASpaceDrawnOnTheStringBeforeIt_SeparatesNothing()
    {
        const string head = "BT /F1 12 Tf 1 0 0 1 200 700 Tm (.c) Tj 1 0 0 1 100 700 Tm (a) Tj ";
        Assert.Equal("\u05D2. \u05D1\u05D0", Extract(head + "1 0 0 1 101 700 Tm ( ) Tj 1 0 0 1 106 700 Tm (b) Tj ET"));
        Assert.Equal("\u05D2. \u05D1 \u05D0", Extract(head + "1 0 0 1 106 700 Tm ( ) Tj 1 0 0 1 107 700 Tm (b) Tj ET"));
    }

    private static string Extract(string content, string form = null)
    {
        string widths = string.Join(" ", Enumerable.Repeat("500", 95));
        const string cmap =
            "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /Heb def\n"
            + "1 begincodespacerange <00> <FF> endcodespacerange\n"
            + "5 beginbfchar <61> <05D0> <62> <05D1> <63> <05D2> <31> <05B8> <32> <05BC> endbfchar\n"
            + "endcmap CMapName currentdict /CMap defineresource pop end end";
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >>"
                + (form != null ? " /XObject << /Fm1 7 0 R >>" : "") + " >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            $"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /FirstChar 32 /LastChar 126 /Widths [{widths}] /ToUnicode 6 0 R >>",
            $"<< /Length {cmap.Length} >>\nstream\n{cmap}\nendstream",
        };
        if (form != null)
            bodies.Add($"<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Length {form.Length} >>\nstream\n{form}\nendstream");

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat("{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        using var doc = PdfDocument.Open(new MemoryStream(Encoding.Latin1.GetBytes(sb.ToString())));
        return PdfTextExtractor.ExtractText(doc.Pages[0]).Replace(Environment.NewLine, "\n").Trim();
    }
}
