// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Fonts.Cff;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Fonts.Type1
{
    /// <summary>
    /// Converts a parsed Type 1 font to a bare CFF, so a Type 1 program can be subset and embedded
    /// through the same path as an OpenType-CFF one.
    /// </summary>
    /// <remarks>
    /// <para><b>Through geometry, not through a translation of operators.</b>
    /// <see cref="Type1CharstringInterpreter"/> runs each charstring to an outline and this emits a
    /// Type 2 charstring from that outline. Translating operator for operator would have to carry
    /// <c>callothersubr</c>'s flex and hint-replacement protocols into a vocabulary that has neither
    /// in the same shape; running them turns flex into the two curves it always was.</para>
    ///
    /// <para><b>Hints are not carried across, and that is a stated cost.</b> Type 1 declares stems
    /// with <c>hstem</c>/<c>vstem</c> and replaces them mid-glyph through <c>othersubr 3</c>; Type 2
    /// declares them all up front and switches with <c>hintmask</c>, which needs the complete stem
    /// list before any outline is emitted. The outlines this produces are exact — the rendering on
    /// a hinting rasteriser at small sizes is not as good as the original's. A correct unhinted
    /// glyph is a far better trade than a refusal, which is what this replaces.</para>
    ///
    /// <para><b>Composites survive as composites.</b> A Type 1 <c>seac</c> becomes Type 2's
    /// four-argument <c>endchar</c>, which is the same construction under a different name and
    /// takes the same standard-encoding codes — so an accented glyph stays one glyph referring to
    /// two rather than being flattened into a copy of their outlines.</para>
    ///
    /// <para><b>Every charstring uses only <c>rmoveto</c>, <c>rlineto</c> and <c>rrcurveto</c>.</b>
    /// Type 2's <c>hlineto</c> family encodes the same paths in fewer bytes, and choosing between
    /// them is where an emitter gets subtly wrong output that still parses. The result is larger
    /// than a tuned encoder's and is the same shape; Flate recovers most of the difference.</para>
    /// </remarks>
    internal static class Type1ToCffConverter
    {
        /// <summary>Why the last conversion failed, or <c>null</c>. Per-thread.</summary>
        [ThreadStatic] private static string _lastError;

        internal static string LastError => _lastError;

        /// <summary>
        /// Converts <paramref name="font"/>, or answers <c>null</c> with <see cref="LastError"/> set.
        /// </summary>
        /// <param name="font">The parsed Type 1 program.</param>
        /// <param name="postScriptName">The name written into the CFF's Name INDEX.</param>
        internal static byte[] Convert(Type1Font font, string postScriptName)
        {
            _lastError = null;

            if (font == null || font.CharStrings == null || font.CharStrings.Count == 0)
            {
                _lastError = "the Type 1 program declares no charstrings";
                return null;
            }

            // .notdef first, then every other name in ordinal order. The charset makes the order
            // semantically irrelevant, so a deterministic one is chosen: the same input produces
            // the same bytes, which is what makes a converted font diffable.
            var names = new List<string> { ".notdef" };
            var rest = new List<string>();
            foreach (KeyValuePair<string, byte[]> entry in font.CharStrings)
                if (!string.Equals(entry.Key, ".notdef", StringComparison.Ordinal))
                    rest.Add(entry.Key);
            rest.Sort(StringComparer.Ordinal);
            names.AddRange(rest);

            var glyphs = new List<Type1CharstringInterpreter.Glyph>(names.Count);
            foreach (string name in names)
            {
                font.CharStrings.TryGetValue(name, out byte[] charstring);

                // A .notdef the font does not define is still a glyph the CFF must have: index 0
                // is where a reader lands for anything it cannot map, and an absent one is a font
                // that cannot answer.
                Type1CharstringInterpreter.Glyph glyph = charstring == null
                    ? new Type1CharstringInterpreter.Glyph()
                    : Type1CharstringInterpreter.Run(charstring, font.Subrs);

                if (glyph == null)
                {
                    _lastError = "the charstring for '" + name + "' could not be interpreted";
                    return null;
                }
                glyphs.Add(glyph);
            }

            byte[] cff = Assemble(postScriptName, names, glyphs, font.FontMatrixScale, out string error);
            _lastError = error;
            return cff;
        }

        /// <summary>
        /// Interpreted glyphs, named <paramref name="names"/> in the same order, as a name-keyed CFF —
        /// glyph 0 first — its box the outlines', its font matrix <paramref name="fontMatrixScale"/>
        /// where that is not the 0.001 default; or null with <paramref name="error"/>. What
        /// <see cref="Convert"/> and <see cref="Type1CharstringFontBuilder"/> both write through.
        /// </summary>
        internal static byte[] Assemble(string postScriptName, IReadOnlyList<string> names,
                                        IReadOnlyList<Type1CharstringInterpreter.Glyph> glyphs,
                                        double fontMatrixScale, out string error)
        {
            error = null;
            var built = new CffFontWriter.Font
            {
                Name = string.IsNullOrEmpty(postScriptName) ? "Converted" : postScriptName,
            };

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            bool anyPoint = false;

            for (int k = 0; k < glyphs.Count; k++)
            {
                Type1CharstringInterpreter.Glyph glyph = glyphs[k];
                foreach (Type1CharstringInterpreter.Segment seg in glyph.Segments)
                {
                    Extend(seg.X1, seg.Y1, ref minX, ref minY, ref maxX, ref maxY, ref anyPoint);
                    if (seg.Kind != Type1CharstringInterpreter.SegmentKind.CurveTo) continue;
                    Extend(seg.X2, seg.Y2, ref minX, ref minY, ref maxX, ref maxY, ref anyPoint);
                    Extend(seg.X3, seg.Y3, ref minX, ref minY, ref maxX, ref maxY, ref anyPoint);
                }

                built.GlyphNames.Add(names[k]);
                built.CharStrings.Add(Emit(glyph));
            }

            built.FontBBox = anyPoint
                ? new[] { Math.Floor(minX), Math.Floor(minY), Math.Ceiling(maxX), Math.Ceiling(maxY) }
                : new double[] { 0, 0, 0, 0 };

            // A Type 1 font matrix other than the 0.001 default has to travel: it is what makes a
            // 2048-unit design space read at the right size, and CFF's default would shrink it.
            if (Math.Abs(fontMatrixScale - 0.001f) > 1e-9)
                built.FontMatrix = new double[] { fontMatrixScale, 0, 0, fontMatrixScale, 0, 0 };

            try
            {
                return CffFontWriter.Build(built);
            }
            catch (Exception ex)
            {
                error = "the CFF could not be assembled: " + ex.Message;
                return null;
            }
        }

        private static void Extend(double x, double y, ref double minX, ref double minY,
                                   ref double maxX, ref double maxY, ref bool any)
        {
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
            any = true;
        }

        /// <summary>One glyph's outline as a Type 2 charstring.</summary>
        /// <remarks>
        /// <b>The width is a prefix, not an operator</b> (CFF §4.3): an odd leading argument on the
        /// first stem, move or <c>endchar</c> is the advance, relative to <c>nominalWidthX</c>. This
        /// writer sets both <c>nominalWidthX</c> and <c>defaultWidthX</c> to zero, so the prefix is
        /// the width itself and a zero-width glyph correctly omits it.
        /// </remarks>
        private static byte[] Emit(Type1CharstringInterpreter.Glyph glyph)
        {
            using var cs = new MemoryStream();

            double width = glyph.HasWidth ? glyph.Width : 0;
            bool widthPending = Math.Abs(width) > 1e-9;

            if (glyph.IsComposite)
            {
                // Type 2's four-argument endchar is seac by another name, and takes the same
                // standard-encoding codes — but not the same adx. Type 1 places the accent's origin
                // at adx − asb + the composite's sbx (the renderer's Type1CharstringEvaluator.OpSeac,
                // measured against a font engine); Type 2 has no sidebearings and places it at adx. The
                // raw adx put every accent off by sbx − asb: the base's sidebearing less the
                // accent's, routinely a hundred units.
                if (widthPending) { WriteNumber(cs, Round(width)); widthPending = false; }
                WriteNumber(cs, Round(glyph.AccentX - glyph.AccentSidebearing + glyph.SidebearingX));
                WriteNumber(cs, Round(glyph.AccentY));
                WriteNumber(cs, glyph.BaseCharCode);
                WriteNumber(cs, glyph.AccentCharCode);
                cs.WriteByte(14);
                return cs.ToArray();
            }

            double x = 0, y = 0;

            foreach (Type1CharstringInterpreter.Segment seg in glyph.Segments)
            {
                switch (seg.Kind)
                {
                    case Type1CharstringInterpreter.SegmentKind.MoveTo:
                        if (widthPending) { WriteNumber(cs, Round(width)); widthPending = false; }
                        WriteNumber(cs, Round(seg.X1 - x));
                        WriteNumber(cs, Round(seg.Y1 - y));
                        cs.WriteByte(21);                        // rmoveto
                        x = seg.X1; y = seg.Y1;
                        break;

                    case Type1CharstringInterpreter.SegmentKind.LineTo:
                        WriteNumber(cs, Round(seg.X1 - x));
                        WriteNumber(cs, Round(seg.Y1 - y));
                        cs.WriteByte(5);                         // rlineto
                        x = seg.X1; y = seg.Y1;
                        break;

                    case Type1CharstringInterpreter.SegmentKind.CurveTo:
                        WriteNumber(cs, Round(seg.X1 - x));
                        WriteNumber(cs, Round(seg.Y1 - y));
                        WriteNumber(cs, Round(seg.X2 - seg.X1));
                        WriteNumber(cs, Round(seg.Y2 - seg.Y1));
                        WriteNumber(cs, Round(seg.X3 - seg.X2));
                        WriteNumber(cs, Round(seg.Y3 - seg.Y2));
                        cs.WriteByte(8);                         // rrcurveto
                        x = seg.X3; y = seg.Y3;
                        break;
                }
            }

            // A glyph with no outline at all still carries its width, which is what a space is.
            if (widthPending) WriteNumber(cs, Round(width));
            cs.WriteByte(14);                                    // endchar
            return cs.ToArray();
        }

        private static int Round(double v) => SaturatingCast.ToInt(Math.Round(v, MidpointRounding.AwayFromZero));

        /// <summary>A Type 2 charstring operand (CFF §4.1 and Appendix B).</summary>
        private static void WriteNumber(Stream s, int v)
        {
            if (v >= -107 && v <= 107) { s.WriteByte((byte)(v + 139)); return; }

            if (v >= 108 && v <= 1131)
            {
                int a = v - 108;
                s.WriteByte((byte)((a >> 8) + 247));
                s.WriteByte((byte)a);
                return;
            }
            if (v >= -1131 && v <= -108)
            {
                int a = -v - 108;
                s.WriteByte((byte)((a >> 8) + 251));
                s.WriteByte((byte)a);
                return;
            }
            if (v >= short.MinValue && v <= short.MaxValue)
            {
                s.WriteByte(28);
                s.WriteByte((byte)(v >> 8));
                s.WriteByte((byte)v);
                return;
            }

            // 16.16 fixed. A coordinate this large is not a real design space, but the encoding
            // exists and refusing here would turn an odd font into an unconvertible one.
            s.WriteByte(255);
            long fixed1616 = (long)v << 16;
            s.WriteByte((byte)(fixed1616 >> 24));
            s.WriteByte((byte)(fixed1616 >> 16));
            s.WriteByte((byte)(fixed1616 >> 8));
            s.WriteByte((byte)fixed1616);
        }
    }
}
