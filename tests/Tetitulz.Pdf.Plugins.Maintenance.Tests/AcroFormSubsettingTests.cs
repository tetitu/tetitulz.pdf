// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// A field's <b>value</b> lives in no content stream. §12.7.3.3 has the viewer regenerate a widget's
/// appearance from <c>/DA</c> and <c>/V</c> whenever <c>/NeedAppearances</c> is true — and a field
/// with a <c>/V</c> and no <c>/AP</c> is drawn that way whatever the flag says — so the glyphs the
/// reader will paint are invisible to a scanner that reads streams. Pruning on that evidence deletes
/// them, and the field paints blanks in a file that still validates.
///
/// <para>These tests assert the <b>font program in the output</b>, not that the call returned: a
/// subsetter that deleted every glyph also produces a valid, smaller PDF.</para>
/// </summary>
public class AcroFormSubsettingTests
{
    [Fact]
    public void NeedAppearancesForm_IsNotSubsetAndTheRefusalIsReported()
    {
        byte[] pdf = FormPdf(needAppearances: true, fieldValue: "(Muller)", widgetAppearance: true);
        byte[] before = FontProgram(pdf);

        DocumentEditDiagnostics.Clear();
        var result = Optimize(pdf, out byte[] optimized);

        Assert.Equal(0, result.FontsSubset);
        Assert.Equal(before, FontProgram(optimized));   // byte-identical: nothing was pruned
        Assert.NotNull(DocumentEditDiagnostics.LastError);
        Assert.Contains("NeedAppearances", DocumentEditDiagnostics.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void FieldWithAValueAndNoAppearance_IsNotSubsetAndTheRefusalIsReported()
    {
        // /NeedAppearances is absent, so the flag alone would not catch this: the field simply has
        // no /AP yet, which a viewer resolves the same way — by drawing /V through /DA.
        byte[] pdf = FormPdf(needAppearances: false, fieldValue: "(Muller)", widgetAppearance: false);
        byte[] before = FontProgram(pdf);

        DocumentEditDiagnostics.Clear();
        var result = Optimize(pdf, out byte[] optimized);

        Assert.Equal(0, result.FontsSubset);
        Assert.Equal(before, FontProgram(optimized));
        Assert.NotNull(DocumentEditDiagnostics.LastError);
        Assert.Contains("/AP", DocumentEditDiagnostics.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void FormWhoseFieldsAllHaveAppearances_IsStillSubset()
    {
        // The control, and it is what keeps the refusal from degenerating into "never subset a form
        // PDF": every widget carries an /AP, which ScanAnnotations already reads, so the evidence is
        // complete and the unused glyphs must still go.
        byte[] pdf = FormPdf(needAppearances: false, fieldValue: "(Muller)", widgetAppearance: true);

        var result = Optimize(pdf, out byte[] optimized);

        Assert.Equal(1, result.FontsSubset);
        long[] loca = ReadLoca(FontProgram(optimized));
        Assert.NotEqual(loca[1], loca[2]);   // CID 1 is drawn by the page
        Assert.Equal(loca[2], loca[3]);      // CID 2 is drawn by nothing
    }

    /// <summary>
    /// A widget whose <c>/AP /N</c> is a <b>dictionary of appearance states</b> — which is what
    /// every checkbox and radio button in every real form uses (§12.5.5) — must have each state's
    /// stream scanned, not just the streams reached directly.
    /// </summary>
    /// <remarks>
    /// <para><b>Found by mutation while merging the two content scanners' appearance walks.</b>
    /// Making the shared walk skip the states-dictionary branch left all 247 Maintenance tests
    /// green: every fixture in the suite used the <c>/AP &lt;&lt; /N 10 0 R &gt;&gt;</c> stream
    /// form, and none the states form.</para>
    ///
    /// <para>The consequence is glyph loss, not a missed optimisation. CID 2 here is drawn
    /// <i>only</i> by the "on" appearance state; a scanner that does not reach it concludes the
    /// glyph is unused, the subsetter drops its outline, and the checkbox paints a blank in a file
    /// that still validates — the same failure mode this class's other tests exist for, arriving by
    /// a different route.</para>
    /// </remarks>
    [Fact]
    public void GlyphDrawnOnlyByAnAppearanceStateSurvivesSubsetting()
    {
        byte[] pdf = CheckboxWithAppearanceStatesPdf();

        var result = Optimize(pdf, out byte[] optimized);

        // Nothing is pruned, and THAT is the evidence: both glyphs are in use, so the subsetter
        // correctly reports there is nothing to remove. Skip the appearance-states walk and CID 2
        // becomes prunable, so this count goes to 1 and the outline below disappears.
        Assert.Equal(0, result.FontsSubset);

        long[] loca = ReadLoca(FontProgram(optimized));
        Assert.NotEqual(loca[1], loca[2]);     // CID 1 — drawn by the page
        Assert.NotEqual(loca[2], loca[3]);     // CID 2 — drawn ONLY by the /Yes appearance state
    }

    [Fact]
    public void FieldWithNoValue_IsStillSubset()
    {
        // An empty field has nothing to regenerate, so it is not evidence of missing glyphs.
        byte[] pdf = FormPdf(needAppearances: false, fieldValue: null, widgetAppearance: false);

        var result = Optimize(pdf, out byte[] optimized);

        Assert.Equal(1, result.FontsSubset);
        long[] loca = ReadLoca(FontProgram(optimized));
        Assert.Equal(loca[2], loca[3]);
    }

    [Fact]
    public void CyclicFieldTree_IsRefusedWithoutRevisitingTheCycle()
    {
        // A field whose /Kids points back at itself, and a diamond besides. The walk must terminate
        // on the visited set rather than on the depth cap alone; the evidence is that Optimize
        // returns a result and reports the field-level refusal rather than the budget one.
        DocumentEditDiagnostics.Clear();
        var result = Optimize(CyclicFieldTreePdf(), out _);

        Assert.Equal(0, result.FontsSubset);
        Assert.NotNull(DocumentEditDiagnostics.LastError);
        Assert.DoesNotContain("budget", DocumentEditDiagnostics.LastError, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static OptimizationResult Optimize(byte[] input, out byte[] optimized)
    {
        using var i = new MemoryStream(input);
        using var o = new MemoryStream();
        var result = PdfOptimizer.Optimize(i, o,
            new OptimizationOptions { SubsetEmbeddedFonts = true });
        optimized = o.ToArray();
        return result;
    }

    /// <summary>The bytes of the one /FontFile2 stream in the file.</summary>
    private static byte[] FontProgram(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        foreach (var kvp in doc.Parser.EnumerateAllObjects())
            if (kvp.Value is PdfDictionary descriptor &&
                doc.ResolveObject(descriptor[PdfName.FontFile2]) is PdfStream program)
                return program.GetDecodedData();
        throw new InvalidOperationException("the fixture no longer carries a /FontFile2");
    }

    private static long[] ReadLoca(byte[] font)
    {
        int tableCount = (font[4] << 8) | font[5];
        int locaAt = -1, locaLen = 0, headAt = -1;
        for (int i = 0; i < tableCount; i++)
        {
            int rec = 12 + i * 16;
            string tag = Encoding.ASCII.GetString(font, rec, 4);
            int offset = (int)(((uint)font[rec + 8] << 24) | ((uint)font[rec + 9] << 16) |
                               ((uint)font[rec + 10] << 8) | font[rec + 11]);
            int length = (int)(((uint)font[rec + 12] << 24) | ((uint)font[rec + 13] << 16) |
                               ((uint)font[rec + 14] << 8) | font[rec + 15]);
            if (tag == "loca") { locaAt = offset; locaLen = length; }
            if (tag == "head") headAt = offset;
        }

        bool longFormat = ((font[headAt + 50] << 8) | font[headAt + 51]) == 1;
        int entries = longFormat ? locaLen / 4 : locaLen / 2;
        var loca = new long[entries];
        for (int i = 0; i < entries; i++)
            loca[i] = longFormat
                ? (long)(((uint)font[locaAt + i * 4] << 24) | ((uint)font[locaAt + i * 4 + 1] << 16) |
                         ((uint)font[locaAt + i * 4 + 2] << 8) | font[locaAt + i * 4 + 3])
                : (((font[locaAt + i * 2] << 8) | font[locaAt + i * 2 + 1]) * 2L);
        return loca;
    }

    // ------------------------------------------------------------------
    //  Fixtures
    // ------------------------------------------------------------------

    // The page draws CID 1 only. CID 2's outline is what a subset would drop — and what the field's
    // regenerated appearance would need.
    private const string PageContent = "BT /F1 12 Tf 100 700 Td <0001> Tj ET";
    private const string AppearanceContent = "BT /F1 12 Tf 0 0 Td <0001> Tj ET";

    private static byte[] FormPdf(bool needAppearances, string fieldValue, bool widgetAppearance)
    {
        string widget = "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /Rect [0 0 100 20] " +
                        "/DA (/F1 12 Tf 0 g) /P 3 0 R" +
                        (fieldValue != null ? " /V " + fieldValue : "") +
                        (widgetAppearance ? " /AP << /N 10 0 R >>" : "") + " >>";

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 9 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R >> >> /Annots [11 0 R] >>",
            StreamObject("", PageContent),
            "<< /Type /Font /Subtype /Type0 /BaseFont /Test /Encoding /Identity-H /DescendantFonts [6 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Test /CIDSystemInfo " +
            "<< /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 7 0 R " +
            "/CIDToGIDMap /Identity /DW 500 >>",
            "<< /Type /FontDescriptor /FontName /Test /Flags 4 /ItalicAngle 0 /Ascent 800 " +
            "/Descent -200 /CapHeight 700 /StemV 80 /FontBBox [0 -200 1000 800] /FontFile2 8 0 R >>",
            null,                                    // 8 — the font program, written as bytes
            "<< /Fields [11 0 R] /DR << /Font << /F1 5 0 R >> >> /DA (/F1 12 Tf 0 g)" +
            (needAppearances ? " /NeedAppearances true" : "") + " >>",
            StreamObject("/Type /XObject /Subtype /Form /BBox [0 0 100 20] " +
                         "/Resources << /Font << /F1 5 0 R >> >>", AppearanceContent),
            widget,
        };
        return Assemble(bodies, fontProgramObject: 8);
    }

    /// <summary>
    /// A checkbox widget whose <c>/AP /N</c> holds two appearance states. The <c>/Yes</c> state is
    /// the only thing in the document that draws CID 2.
    /// </summary>
    private static byte[] CheckboxWithAppearanceStatesPdf()
    {
        const string OnAppearance = "BT /F1 12 Tf 0 0 Td <0002> Tj ET";

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 9 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R >> >> /Annots [11 0 R] >>",
            StreamObject("", PageContent),
            "<< /Type /Font /Subtype /Type0 /BaseFont /Test /Encoding /Identity-H /DescendantFonts [6 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Test /CIDSystemInfo " +
            "<< /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 7 0 R " +
            "/CIDToGIDMap /Identity /DW 500 >>",
            "<< /Type /FontDescriptor /FontName /Test /Flags 4 /ItalicAngle 0 /Ascent 800 " +
            "/Descent -200 /CapHeight 700 /StemV 80 /FontBBox [0 -200 1000 800] /FontFile2 8 0 R >>",
            null,                                    // 8 — the font program, written as bytes
            "<< /Fields [11 0 R] /DR << /Font << /F1 5 0 R >> >> /DA (/F1 12 Tf 0 g) >>",
            // 10 — the /Off state, deliberately empty: a state that draws nothing must not be the
            // reason the walk reaches the next one.
            StreamObject("/Type /XObject /Subtype /Form /BBox [0 0 20 20] " +
                         "/Resources << /Font << /F1 5 0 R >> >>", ""),
            // 11 — the checkbox. /V and /AS name the on state, so this is an ordinary checked box.
            "<< /Type /Annot /Subtype /Widget /FT /Btn /T (Check) /Rect [0 0 20 20] " +
            "/DA (/F1 12 Tf 0 g) /P 3 0 R /V /Yes /AS /Yes " +
            "/AP << /N << /Off 10 0 R /Yes 12 0 R >> >> >>",
            // 12 — the on-state appearance, the document's only user of CID 2.
            StreamObject("/Type /XObject /Subtype /Form /BBox [0 0 20 20] " +
                         "/Resources << /Font << /F1 5 0 R >> >>", OnAppearance),
        };
        return Assemble(bodies, fontProgramObject: 8);
    }

    private static byte[] CyclicFieldTreePdf()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 9 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObject("", PageContent),
            "<< /Type /Font /Subtype /Type0 /BaseFont /Test /Encoding /Identity-H /DescendantFonts [6 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Test /CIDSystemInfo " +
            "<< /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 7 0 R " +
            "/CIDToGIDMap /Identity /DW 500 >>",
            "<< /Type /FontDescriptor /FontName /Test /Flags 4 /ItalicAngle 0 /Ascent 800 " +
            "/Descent -200 /CapHeight 700 /StemV 80 /FontBBox [0 -200 1000 800] /FontFile2 8 0 R >>",
            null,
            "<< /Fields [10 0 R] >>",
            // A parent whose /Kids names itself twice — a cycle AND a diamond — and a terminal
            // field with a value but no appearance, which is what the walk must still reach.
            "<< /FT /Tx /T (Root) /V (x) /Kids [10 0 R 10 0 R 11 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /T (Leaf) /V (Muller) /Rect [0 0 10 10] >>",
        };
        return Assemble(bodies, fontProgramObject: 8);
    }

    private static string StreamObject(string extraKeys, string content) =>
        $"<< {extraKeys} /Length {content.Length} >>\nstream\n{content}\nendstream";

    private static byte[] Assemble(List<string> bodies, int fontProgramObject)
    {
        byte[] fontProgram = MinimalCidFont();

        using var ms = new MemoryStream();
        void Write(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }

        Write("%PDF-1.4\n");
        var offsets = new long[bodies.Count + 1];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i + 1] = ms.Position;
            Write($"{i + 1} 0 obj\n");
            if (i + 1 == fontProgramObject)
            {
                Write($"<< /Length {fontProgram.Length} /Length1 {fontProgram.Length} >>\nstream\n");
                ms.Write(fontProgram, 0, fontProgram.Length);
                Write("\nendstream\n");
            }
            else
            {
                Write(bodies[i] + "\n");
            }
            Write("endobj\n");
        }

        int size = bodies.Count + 1;
        long xref = ms.Position;
        Write($"xref\n0 {size}\n0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Count; i++)
            Write(offsets[i].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Write($"trailer\n<< /Size {size} /Root 1 0 R >>\n");
        Write($"startxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>
    /// A four-glyph TrueType file: notdef, glyph 1 (drawn by the page), glyph 2 (drawn by nothing —
    /// the one a subset removes), and the terminating loca entry.
    /// </summary>
    private static byte[] MinimalCidFont()
    {
        var glyphs = new List<byte[]>
        {
            Array.Empty<byte>(),
            SimpleGlyph(16, 0x11),
            SimpleGlyph(16, 0x22),
        };

        var glyf = new MemoryStream();
        var loca = new byte[(glyphs.Count + 1) * 4];
        for (int i = 0; i < glyphs.Count; i++)
        {
            WriteU32(loca, i * 4, (uint)glyf.Length);
            glyf.Write(glyphs[i], 0, glyphs[i].Length);
        }
        WriteU32(loca, glyphs.Count * 4, (uint)glyf.Length);

        var head = new byte[54];
        WriteU32(head, 0, 0x00010000);
        WriteU16(head, 18, 1000);          // unitsPerEm
        WriteU16(head, 50, 1);             // indexToLocFormat = long

        var maxp = new byte[6];
        WriteU32(maxp, 0, 0x00010000);
        WriteU16(maxp, 4, glyphs.Count);

        return AssembleFont(new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["glyf"] = glyf.ToArray(),
            ["head"] = head,
            ["loca"] = loca,
            ["maxp"] = maxp,
        });
    }

    private static byte[] SimpleGlyph(int totalLength, byte fill)
    {
        var glyph = new byte[totalLength];
        WriteU16(glyph, 0, 1);             // numberOfContours
        for (int i = 10; i < totalLength; i++) glyph[i] = fill;
        return glyph;
    }

    private static byte[] AssembleFont(SortedDictionary<string, byte[]> tables)
    {
        int count = tables.Count;
        int directory = 12 + count * 16;
        int offset = directory;
        var offsets = new Dictionary<string, int>();
        foreach (var t in tables)
        {
            offsets[t.Key] = offset;
            offset += (t.Value.Length + 3) & ~3;
        }

        var font = new byte[offset];
        WriteU32(font, 0, 0x00010000);
        WriteU16(font, 4, count);

        int rec = 12;
        foreach (var t in tables)
        {
            Encoding.ASCII.GetBytes(t.Key).CopyTo(font, rec);
            WriteU32(font, rec + 8, (uint)offsets[t.Key]);
            WriteU32(font, rec + 12, (uint)t.Value.Length);
            t.Value.CopyTo(font, offsets[t.Key]);
            rec += 16;
        }
        return font;
    }

    private static void WriteU16(byte[] data, int offset, int value)
    {
        data[offset] = (byte)((value >> 8) & 0xFF);
        data[offset + 1] = (byte)(value & 0xFF);
    }

    private static void WriteU32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }
}
