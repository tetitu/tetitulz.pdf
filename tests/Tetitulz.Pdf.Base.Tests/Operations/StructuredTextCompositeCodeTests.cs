// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfStructuredTextExtractor"/> reads a composite font's string a code at a time by the
/// font's encoding CMap: <c>&lt;41420041&gt;</c> under Identity-H is two two-byte codes, Z (a 700-unit
/// glyph) and A, the second placed 7 points after the first. Read a byte a code, it is four codes —
/// A, B, nothing, A — which is why the first code's two bytes are both letters: a code whose high
/// byte is zero reads the same either way, its zero decoding to nothing. No structured test showed a composite font before this one, so the extractor's
/// splitter could be swapped for a byte stride with every test green.
/// </summary>
public class StructuredTextCompositeCodeTests
{
    [Fact]
    public void AnIdentityHString_IsReadAsTwoByteCodes()
    {
        var chars = PdfStructuredTextExtractor.Extract(new MemoryStream(Pdf()))[0]
            .Lines.SelectMany(l => l.Words).SelectMany(w => w.Chars).ToList();

        Assert.Equal(new[] { "Z", "A" }, chars.Select(c => c.Char));
        Assert.Equal(100f, chars[0].X, 2);
        Assert.Equal(107f, chars[1].X, 2);   // 700/1000 of 10 points along
    }

    private static byte[] Pdf()
    {
        const string content = "BT /F0 10 Tf 100 700 Td <41420041> Tj ET";
        const string cmap = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n"
            + "/CMapName /Test def 1 begincodespacerange <0000> <FFFF> endcodespacerange\n"
            + "2 beginbfchar <4142> <005A> <0041> <0041> endbfchar\n"
            + "endcmap CMapName currentdict /CMap defineresource pop end end";
        var objs = new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 800] /Contents 4 0 R "
                + "/Resources << /Font << /F0 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type0 /BaseFont /Test /Encoding /Identity-H "
                + "/DescendantFonts [6 0 R] /ToUnicode 8 0 R >>\nendobj\n",
            "6 0 obj\n<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Test "
                + "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
                + "/FontDescriptor 7 0 R /DW 1000 /W [65 [500 500] 16706 [700]] >>\nendobj\n",
            "7 0 obj\n<< /Type /FontDescriptor /FontName /Test /Flags 4 /ItalicAngle 0 "
                + "/Ascent 880 /Descent -120 /CapHeight 700 /StemV 80 "
                + "/FontBBox [0 -120 1000 880] >>\nendobj\n",
            $"8 0 obj\n<< /Length {cmap.Length} >>\nstream\n{cmap}\nendstream\nendobj\n",
        };

        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.ASCII.GetBytes(s));
        Emit("%PDF-1.7\n");
        foreach (var o in objs) { offsets.Add(acc.Count); Emit(o); }
        int xref = acc.Count;
        Emit($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }
}