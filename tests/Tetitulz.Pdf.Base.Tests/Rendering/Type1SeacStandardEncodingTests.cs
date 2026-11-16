// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Fonts.Type1;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A Type 1 <c>seac</c> names its base and accent components by <b>Adobe StandardEncoding</b>
/// character code, never through the font's own <c>/Encoding</c> (ATN #5002). This asserts the
/// resolution it performs, at the codes where StandardEncoding and every other encoding in the
/// tree give different answers.
///
/// <para><b>Why by measured geometry.</b> A wrong or missing name does not throw and does not
/// produce an empty result — the composite still returns a perfectly good path containing only its
/// base component. Nothing weaker than reading the outline's extent back can tell "the accent was
/// composed in" from "the accent was silently dropped", which is exactly how an accented glyph
/// loses its accent in a real document. Each glyph in the fixture is a square of a distinct size,
/// so the composite's width names which charstring was found.</para>
///
/// <para><b>What it pins that a Core table test cannot.</b> The table itself lives in
/// <c>Core.Fonts.AdobeStandardEncodingNames</c> and is asserted there. This is the other half: that
/// <c>Type1CharstringEvaluator</c> — one of the two Base sites that used to carry its own copy —
/// really reads it. Emptying the Core table leaves both Base sites compiling and every existing
/// Base font test green.</para>
/// </summary>
public class Type1SeacStandardEncodingTests
{
    // Charstring operators (ATN #5002 §6): the ones this fixture needs.
    private const byte Rlineto   = 5;
    private const byte Closepath = 9;
    private const byte Hsbw      = 13;
    private const byte Endchar   = 14;
    private const byte Rmoveto   = 21;
    private const byte Escape    = 12;
    private const byte SeacEsc   = 6;   // 12 6

    /// <summary>Type 1 charstring number encoding (identical to Type 2 below 255).</summary>
    private static void Num(List<byte> cs, int v)
    {
        if (v >= -107 && v <= 107) { cs.Add((byte)(v + 139)); return; }
        if (v >= 108 && v <= 1131)
        {
            int d = v - 108;
            cs.Add((byte)(247 + (d >> 8)));
            cs.Add((byte)(d & 0xFF));
            return;
        }
        if (v <= -108 && v >= -1131)
        {
            int d = -v - 108;
            cs.Add((byte)(251 + (d >> 8)));
            cs.Add((byte)(d & 0xFF));
            return;
        }
        cs.Add(255);
        cs.Add((byte)(v >> 24)); cs.Add((byte)(v >> 16));
        cs.Add((byte)(v >> 8));  cs.Add((byte)v);
    }

    /// <summary>An axis-aligned square of side <paramref name="side"/> with its corner at the origin.</summary>
    private static byte[] Square(int side)
    {
        var cs = new List<byte>();
        Num(cs, 0); Num(cs, 800); cs.Add(Hsbw);      // sbx = 0, wx = 800 → current point (0, 0)
        Num(cs, 0); Num(cs, 0);   cs.Add(Rmoveto);
        Num(cs, side); Num(cs, 0);     cs.Add(Rlineto);
        Num(cs, 0);    Num(cs, side);  cs.Add(Rlineto);
        Num(cs, -side); Num(cs, 0);    cs.Add(Rlineto);
        cs.Add(Closepath);
        cs.Add(Endchar);
        return cs.ToArray();
    }

    /// <summary>
    /// <c>asb adx ady bchar achar seac</c>. Both components are placed at the origin, so the
    /// composite's extent is the larger of the two squares — which is what makes the assertion a
    /// statement about <i>which glyph was found</i> rather than about accent placement.
    /// </summary>
    private static byte[] Composite(int bchar, int achar)
    {
        var cs = new List<byte>();
        Num(cs, 0); Num(cs, 900); cs.Add(Hsbw);
        Num(cs, 0);      // asb
        Num(cs, 0);      // adx
        Num(cs, 0);      // ady
        Num(cs, bchar);
        Num(cs, achar);
        cs.Add(Escape); cs.Add(SeacEsc);
        return cs.ToArray();
    }

    private const int BaseSide       = 100;   // "A"           — StandardEncoding 0x41
    private const int QuoterightSide = 600;   // "quoteright"  — StandardEncoding 0x27 (WinAnsi: quotesingle)
    private const int QuoteleftSide  = 700;   // "quoteleft"   — StandardEncoding 0x60 (WinAnsi/MacRoman: grave)
    private const int DieresisSide   = 500;   // "dieresis"    — StandardEncoding 0xC8

    private static Type1Font BuildFont()
    {
        // Deliberately named ONLY by their StandardEncoding names. A lookup through any other
        // encoding table finds nothing and the accent is silently dropped.
        var charStrings = new Dictionary<string, byte[]>
        {
            ["A"]          = Square(BaseSide),
            ["quoteright"] = Square(QuoterightSide),
            ["quoteleft"]  = Square(QuoteleftSide),
            ["dieresis"]   = Square(DieresisSide),

            ["c_dieresis"]   = Composite(0x41, 0xC8),
            ["c_quoteright"] = Composite(0x41, 0x27),
            ["c_quoteleft"]  = Composite(0x41, 0x60),
            ["c_undefined"]  = Composite(0x41, 0xB0),   // 0xB0 is undefined in StandardEncoding
        };

        return new Type1Font(new string[256], charStrings, new List<byte[]>());
    }

    private static SKRect Outline(string glyphName)
    {
        using SKPath path = Type1CharstringEvaluator.Evaluate(BuildFont(), glyphName).Path;
        return path.Bounds;
    }

    /// <summary>
    /// The base component alone, so the composite assertions below have a floor that is not zero:
    /// a composite measuring 100 wide is one whose accent was dropped, not one that drew nothing.
    /// </summary>
    [Fact]
    public void TheBaseComponentAloneMeasuresItsOwnSquare()
    {
        SKRect bounds = Outline("A");
        Assert.Equal(BaseSide, (double)bounds.Width, 1);
        Assert.Equal(BaseSide, (double)bounds.Height, 1);
    }

    [Theory]
    [InlineData("c_dieresis", DieresisSide)]        // 0xC8 → "dieresis"
    [InlineData("c_quoteright", QuoterightSide)]    // 0x27 → "quoteright", NOT WinAnsi's "quotesingle"
    [InlineData("c_quoteleft", QuoteleftSide)]      // 0x60 → "quoteleft",  NOT WinAnsi's "grave"
    public void SeacComposesTheAccentTheStandardEncodingCodeNames(string composite, int accentSide)
    {
        SKRect bounds = Outline(composite);

        // The accent square is larger than the base in every case, so the composite's extent is
        // the accent's — and it is the accent's only if the char code resolved to that glyph name.
        Assert.Equal(accentSide, (double)bounds.Width, 1);
        Assert.Equal(accentSide, (double)bounds.Height, 1);
    }

    /// <summary>
    /// 0xB0 is one of the holes in StandardEncoding's high range. The base still composes; the
    /// accent must not be invented from a neighbouring code.
    /// </summary>
    [Fact]
    public void SeacDropsAnAccentWhoseCodeStandardEncodingLeavesUndefined()
    {
        SKRect bounds = Outline("c_undefined");

        Assert.Equal(BaseSide, (double)bounds.Width, 1);
        Assert.Equal(BaseSide, (double)bounds.Height, 1);
    }
}
