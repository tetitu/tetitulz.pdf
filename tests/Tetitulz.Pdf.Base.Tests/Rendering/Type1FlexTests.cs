// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Fonts.Type1;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering
{
    /// <summary>
    /// Regression tests for Type 1 Flex (OtherSubrs 0/1/2) and hint replacement
    /// (OtherSubr 3) handling in <see cref="Type1CharstringEvaluator"/>.
    ///
    /// Computer Modern (cmr10, used by every pdfTeX document) draws its serifs with
    /// Flex.  Before the fix, each of the seven flex <c>rmoveto</c>s started a new
    /// subpath — closing the glyph's outer contour prematurely and leaving the current
    /// point at the wrong place — so the capital "L" (and most serifed glyphs) rendered
    /// as a mangled blob.  These tests build the standard flex idiom by hand and assert
    /// that exactly one continuous contour is produced.
    /// </summary>
    public class Type1FlexTests
    {
        // ---- Type 1 charstring number / operator encoders (decrypted form) ----
        private static void Int(List<byte> cs, int v)
        {
            if (v >= -107 && v <= 107) { cs.Add((byte)(v + 139)); return; }
            if (v >= 108 && v <= 1131)
            {
                int x = v - 108;
                cs.Add((byte)(x / 256 + 247));
                cs.Add((byte)(x % 256));
                return;
            }
            if (v >= -1131 && v <= -108)
            {
                int x = -v - 108;
                cs.Add((byte)(x / 256 + 251));
                cs.Add((byte)(x % 256));
                return;
            }
            // 4-byte form (255 b3 b2 b1 b0)
            cs.Add(255);
            cs.Add((byte)((v >> 24) & 0xFF));
            cs.Add((byte)((v >> 16) & 0xFF));
            cs.Add((byte)((v >> 8) & 0xFF));
            cs.Add((byte)(v & 0xFF));
        }

        private const byte RLineto = 5, RMoveto = 21, CallSubr = 10, Hsbw = 13,
                           Endchar = 14, ClosePath = 9, Escape = 12, CallOtherSubr = 16,
                           Pop = 17, SetCurrentPoint = 33, Return = 11;

        // Standard flex / hint-replacement Subrs as emitted by Type 1 font tools.
        private static IReadOnlyList<byte[]> StandardSubrs()
        {
            // Subr 0: 3 0 callothersubr pop pop setcurrentpoint return
            var s0 = new List<byte>();
            Int(s0, 3); Int(s0, 0); s0.Add(Escape); s0.Add(CallOtherSubr);
            s0.Add(Escape); s0.Add(Pop); s0.Add(Escape); s0.Add(Pop);
            s0.Add(Escape); s0.Add(SetCurrentPoint); s0.Add(Return);

            // Subr 1: 0 1 callothersubr return
            var s1 = new List<byte>();
            Int(s1, 0); Int(s1, 1); s1.Add(Escape); s1.Add(CallOtherSubr); s1.Add(Return);

            // Subr 2: 0 2 callothersubr return
            var s2 = new List<byte>();
            Int(s2, 0); Int(s2, 2); s2.Add(Escape); s2.Add(CallOtherSubr); s2.Add(Return);

            // Subr 3: return
            var s3 = new List<byte> { Return };

            return new[] { s0.ToArray(), s1.ToArray(), s2.ToArray(), s3.ToArray() };
        }

        // Builds a glyph: a horizontal stroke whose end is replaced by a flex, then a
        // vertical stroke.  If flex is handled correctly the whole outline is ONE contour.
        private static byte[] FlexGlyph()
        {
            var cs = new List<byte>();
            // 0 1000 hsbw  → sidebearing 0, width 1000, current point (0,0)
            Int(cs, 0); Int(cs, 1000); cs.Add(Hsbw);
            // 100 100 rmoveto → start contour at (100,100)
            Int(cs, 100); Int(cs, 100); cs.Add(RMoveto);
            // 200 0 rlineto → (300,100)
            Int(cs, 200); Int(cs, 0); cs.Add(RLineto);

            // ---- Flex: begin, 7×(rmoveto + collect), end ----
            Int(cs, 1); cs.Add(CallSubr);                 // begin flex
            (int dx, int dy)[] deltas =
            {
                (10, 5),   // p0 reference  → (310,105)
                (10, 0),   // p1            → (320,105)
                (10, 0),   // p2            → (330,105)
                (10, 0),   // p3 (join)     → (340,105)
                (10, 0),   // p4            → (350,105)
                (10, 0),   // p5            → (360,105)
                (10, -5),  // p6 end        → (370,100)
            };
            foreach (var (dx, dy) in deltas)
            {
                Int(cs, dx); Int(cs, dy); cs.Add(RMoveto);
                Int(cs, 2); cs.Add(CallSubr);             // collect flex point
            }
            // flexHeight=50 endX=370 endY=100, then Subr 0 (end flex)
            Int(cs, 50); Int(cs, 370); Int(cs, 100);
            Int(cs, 0); cs.Add(CallSubr);

            // 0 100 rlineto → must continue from (370,100); goes to (370,200)
            Int(cs, 0); Int(cs, 100); cs.Add(RLineto);
            cs.Add(ClosePath);
            cs.Add(Endchar);
            return cs.ToArray();
        }

        private static int CountMoveVerbs(SKPath path)
        {
            int moves = 0;
            using var it = path.CreateRawIterator();
            var pts = new SKPoint[4];
            SKPathVerb verb;
            while ((verb = it.Next(pts)) != SKPathVerb.Done)
                if (verb == SKPathVerb.Move) moves++;
            return moves;
        }

        private static Type1Font BuildFont(byte[] glyph)
        {
            var enc = new string[256];
            for (int i = 0; i < 256; i++) enc[i] = ".notdef";
            enc[76] = "L"; // arbitrary mapping for the flex glyph
            var charStrings = new Dictionary<string, byte[]> { ["L"] = glyph };
            return new Type1Font(enc, charStrings, StandardSubrs(), 0.001f);
        }

        [Fact]
        public void Flex_ProducesSingleContour_NotSevenSpuriousSubpaths()
        {
            var font = BuildFont(FlexGlyph());
            var outline = Type1CharstringEvaluator.Evaluate(font, "L");

            Assert.NotNull(outline.Path);
            Assert.False(outline.Path.IsEmpty);

            // The pre-fix bug turned each flex rmoveto into a real MoveTo → 8 subpaths.
            // A correct flex yields a single continuous contour (one MoveTo).
            Assert.Equal(1, CountMoveVerbs(outline.Path));
        }

        [Fact]
        public void Flex_LeavesCurrentPointAtFlexEnd_SoFollowingStrokeIsConnected()
        {
            var font = BuildFont(FlexGlyph());
            var outline = Type1CharstringEvaluator.Evaluate(font, "L");

            var b = outline.Path.Bounds;
            // Outline spans x:[100,370] and y:[100,200]; the trailing vertical stroke
            // only reaches y=200 if the current point was correctly restored to the
            // flex endpoint (370,100) — the pre-fix bug left it elsewhere.
            Assert.InRange(b.Left, 99f, 101f);
            Assert.InRange(b.Right, 369f, 371f);
            Assert.InRange(b.Top, 99f, 106f);
            Assert.InRange(b.Bottom, 199f, 201f);
        }

        [Fact]
        public void Hsbw_AdvanceWidth_IsReported()
        {
            var font = BuildFont(FlexGlyph());
            var outline = Type1CharstringEvaluator.Evaluate(font, "L");
            Assert.Equal(1000f, outline.AdvanceWidth, 1);
        }
    }
}
