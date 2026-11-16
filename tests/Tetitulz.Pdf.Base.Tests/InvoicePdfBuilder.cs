// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Builds minimal in-memory PDFs for RTL / invoice extraction tests.  Each glyph is placed at an
/// explicit (x, y) via its own Tm so character X positions are exact.  Hebrew letters are encoded
/// through a ToUnicode CMap (bytes 0x01..0x1B map to U+05D0..U+05EA, alef..tav); ASCII characters
/// are emitted as literal Helvetica WinAnsi text and decode normally.
/// </summary>
internal sealed class InvoicePdfBuilder
{
    private const char HebrewFirst = (char)0x05D0; // alef
    private const char HebrewLast  = (char)0x05EA; // tav
    private const int ShekelByte = 0x1C;            // the code after tav's, mapped to U+20AA
    private readonly List<string> _ops = new();
    private readonly bool _winAnsi;
    private int _rotate;

    public InvoicePdfBuilder() { }

    /// <param name="winAnsi">
    /// Give the font <c>/Encoding /WinAnsiEncoding</c>, so a Latin-1 byte decodes as the Latin-1
    /// character — without it the standard font's implicit base is StandardEncoding, which puts
    /// other glyphs at 0xE0–0xFF.
    /// </param>
    public InvoicePdfBuilder(bool winAnsi) { _winAnsi = winAnsi; }

    /// <summary>Gives the page a <c>/Rotate</c> of <paramref name="degrees"/>, clockwise.</summary>
    public InvoicePdfBuilder Rotated(int degrees)
    {
        _rotate = degrees;
        return this;
    }

    /// <summary>Builds a Hebrew string from Unicode code points (keeps test source ASCII).</summary>
    public static string Hb(params int[] codePoints)
    {
        var sb = new StringBuilder(codePoints.Length);
        foreach (int cp in codePoints) sb.Append((char)cp);
        return sb.ToString();
    }

    /// <summary>Places one Hebrew letter at (x, y).</summary>
    public InvoicePdfBuilder Heb(char hebrew, int x, int y)
    {
        int b = hebrew - HebrewFirst + 1;
        _ops.Add($"1 0 0 1 {x} {y} Tm <{b:X2}> Tj");
        return this;
    }

    /// <summary>Places a run of Hebrew letters left-to-right starting at x, stepping by <paramref name="step"/>.</summary>
    public InvoicePdfBuilder HebRun(string hebrew, int x, int y, int step = 7)
    {
        foreach (char c in hebrew) { Heb(c, x, y); x += step; }
        return this;
    }

    /// <summary>Places a run of Hebrew letters right-to-left (visual RTL layout) ending at xRight.</summary>
    public InvoicePdfBuilder HebRunRtl(string hebrew, int xRight, int y, int step = 7)
    {
        // Logical-first letter goes to the right-most X.
        int x = xRight;
        foreach (char c in hebrew) { Heb(c, x, y); x -= step; }
        return this;
    }

    /// <summary>Places the shekel sign (U+20AA) at (x, y), through the same ToUnicode CMap.</summary>
    public InvoicePdfBuilder Shekel(int x, int y)
    {
        _ops.Add($"1 0 0 1 {x} {y} Tm <{ShekelByte:X2}> Tj");
        return this;
    }

    /// <summary>
    /// Wraps what <paramref name="run"/> places in a §14.8.2.3.3 <c>/ReversedChars</c> sequence, as
    /// Chromium wraps every right-to-left run: the glyphs drawn left to right, each decoding to its
    /// logical character.
    /// </summary>
    public InvoicePdfBuilder ReversedChars(Action<InvoicePdfBuilder> run)
    {
        _ops.Add("/ReversedChars BMC");
        run(this);
        _ops.Add("EMC");
        return this;
    }

    /// <summary>Sets the font size of what is placed after it.</summary>
    public InvoicePdfBuilder Size(float points)
    {
        _ops.Add($"/F1 {points.ToString(CultureInfo.InvariantCulture)} Tf");
        return this;
    }

    /// <summary>Places one ASCII character at (x, y).</summary>
    public InvoicePdfBuilder Ascii(char c, int x, int y)
    {
        _ops.Add($"1 0 0 1 {x} {y} Tm ({Escape(c)}) Tj");
        return this;
    }

    /// <summary>Places an ASCII run left-to-right starting at x, stepping by <paramref name="step"/>.</summary>
    public InvoicePdfBuilder AsciiRun(string text, int x, int y, int step = 6)
    {
        foreach (char c in text) { Ascii(c, x, y); x += step; }
        return this;
    }

    public byte[] Build()
    {
        var cs = new StringBuilder();
        cs.Append("BT /F1 10 Tf\n");
        foreach (var op in _ops) cs.Append(op).Append('\n');
        cs.Append("ET\n");
        return BuildRawPdf(cs.ToString(), BuildCmap(), _winAnsi, _rotate);
    }

    // A Latin-1 character is written as its WinAnsi byte in octal: the content stream is ASCII,
    // and Helvetica's WinAnsiEncoding agrees with Latin-1 from 0xA0 up.
    private static string Escape(char c)
        => c switch
        {
            '(' => "\\(", ')' => "\\)", '\\' => "\\\\",
            _ when c >= 0xA0 && c <= 0xFF => "\\" + Convert.ToString(c, 8).PadLeft(3, '0'),
            _ => c.ToString(),
        };

    private static string BuildCmap()
    {
        var sb = new StringBuilder();
        sb.Append(@"
/CIDInit /ProcSet findresource begin
12 dict begin
begincmap
/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def
/CMapName /Test-Heb def
/CMapType 2 def
");
        int count = HebrewLast - HebrewFirst + 1;
        sb.Append(count + 1).Append(" beginbfchar\n");
        for (int i = 0; i < count; i++)
        {
            int b = i + 1;
            int u = HebrewFirst + i;
            sb.Append($"<{b:X2}> <{u:X4}>\n");
        }
        sb.Append($"<{ShekelByte:X2}> <20AA>\n");
        sb.Append("endbfchar\nendcmap\nCMap currentdict end end");
        return sb.ToString();
    }

    // Minimal single-page PDF with one Helvetica font carrying a ToUnicode CMap.
    private static byte[] BuildRawPdf(string contentStream, string cmap, bool winAnsi, int rotate)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int nextObj = 1;
        int catalogObj = nextObj++;
        int pagesObj   = nextObj++;
        int pageObj    = nextObj++;
        int contentObj = nextObj++;
        int fontObj    = nextObj++;
        int cmapObj    = nextObj++;

        var offsets = new int[nextObj];

        offsets[catalogObj] = sb.Length;
        sb.AppendFormat(CultureInfo.InvariantCulture,
            "{0} 0 obj\n<< /Type /Catalog /Pages {1} 0 R >>\nendobj\n", catalogObj, pagesObj);

        offsets[pagesObj] = sb.Length;
        sb.AppendFormat(CultureInfo.InvariantCulture,
            "{0} 0 obj\n<< /Type /Pages /Kids [{1} 0 R] /Count 1 >>\nendobj\n", pagesObj, pageObj);

        offsets[pageObj] = sb.Length;
        sb.AppendFormat(CultureInfo.InvariantCulture,
            "{0} 0 obj\n<< /Type /Page /Parent {1} 0 R /MediaBox [0 0 612 792]{4}" +
            " /Contents {2} 0 R /Resources << /Font << /F1 {3} 0 R >> >> >>\nendobj\n",
            pageObj, pagesObj, contentObj, fontObj, rotate != 0 ? " /Rotate " + rotate : "");

        offsets[contentObj] = sb.Length;
        sb.AppendFormat(CultureInfo.InvariantCulture,
            "{0} 0 obj\n<< /Length {1} >>\nstream\n{2}\nendstream\nendobj\n",
            contentObj, contentStream.Length, contentStream);

        offsets[fontObj] = sb.Length;
        sb.AppendFormat(CultureInfo.InvariantCulture,
            "{0} 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /ToUnicode {1} 0 R{2} >>\nendobj\n",
            fontObj, cmapObj, winAnsi ? " /Encoding /WinAnsiEncoding" : "");

        offsets[cmapObj] = sb.Length;
        sb.AppendFormat(CultureInfo.InvariantCulture,
            "{0} 0 obj\n<< /Length {1} >>\nstream\n{2}\nendstream\nendobj\n",
            cmapObj, cmap.Length, cmap);

        int xref = sb.Length;
        sb.AppendFormat(CultureInfo.InvariantCulture, "xref\n0 {0}\n", nextObj);
        sb.Append("0000000000 65535 f \n");
        for (int i = 1; i < nextObj; i++)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offsets[i]);
        sb.AppendFormat(CultureInfo.InvariantCulture,
            "trailer\n<< /Size {0} /Root {1} 0 R >>\nstartxref\n{2}\n%%EOF\n", nextObj, catalogObj, xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
