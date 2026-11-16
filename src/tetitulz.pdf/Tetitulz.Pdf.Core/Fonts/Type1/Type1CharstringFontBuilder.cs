// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.Fonts.Type1
{
    /// <summary>
    /// A CFF font built glyph by glyph from Type 1 charstrings: each charstring decrypted (Adobe
    /// Type 1 Font Format §7.2, key 4330), run by Core's Type 1 interpreter into its outline and
    /// advance, and written as a Type 2 charstring by the same emitter <c>Type1ToCffConverter</c>
    /// uses; the font a name-keyed CFF, its glyphs in the order added.
    /// </summary>
    /// <remarks>
    /// <para><b>Public</b> because <c>Plugins.PostScript</c> embeds a Type 0 CIDFont (PLRM §5.11.3)
    /// through it — each CID it shows a glyph — and the interpreter and the CFF writer are internal.
    /// Under a <c>CIDFontType0</c> a CFF whose Top DICT uses no CIDFont operators takes its CIDs as
    /// glyph indices (ISO 32000-2 §9.7.4.2), so the glyph's index here is the code its text shows.</para>
    /// <para><b>A <c>seac</c> composite is refused</b> (an empty glyph is added in its place): its
    /// components are named through StandardEncoding, which a font built this way does not hold.</para>
    /// </remarks>
    public sealed class Type1CharstringFontBuilder
    {
        private readonly string _name;
        private readonly double _fontMatrixScale;
        private readonly List<string> _names = new List<string>();
        private readonly List<Type1CharstringInterpreter.Glyph> _glyphs = new List<Type1CharstringInterpreter.Glyph>();

        /// <summary>A builder for a font named <paramref name="name"/> whose glyph space maps to text space by <paramref name="fontMatrixScale"/>.</summary>
        /// <param name="name">The name written into the CFF's Name INDEX.</param>
        /// <param name="fontMatrixScale">The glyph-to-text scale; 0.001 for the usual 1000-unit em.</param>
        public Type1CharstringFontBuilder(string name, double fontMatrixScale = 0.001)
        {
            _name = name;
            _fontMatrixScale = fontMatrixScale;
        }

        /// <summary>How many glyphs have been added: the index the next one gets.</summary>
        public int Count => _glyphs.Count;

        /// <summary>
        /// <paramref name="length"/> encrypted charstring bytes from <paramref name="start"/>
        /// decrypted, the first <paramref name="lenIV"/> dropped — as they are for a
        /// <paramref name="lenIV"/> of −1 — or null when the range is outside the data.
        /// </summary>
        /// <param name="data">The bytes.</param>
        /// <param name="start">The first encrypted byte.</param>
        /// <param name="length">How many bytes.</param>
        /// <param name="lenIV">The Private dictionary's <c>lenIV</c>; 4 by default, −1 for none.</param>
        public static byte[] Decrypt(byte[] data, int start, int length, int lenIV)
            => Type1Parser.CsDecrypt(data, start, length, lenIV);

        /// <summary>
        /// Adds the glyph <paramref name="charstring"/> (decrypted) draws with
        /// <paramref name="subrs"/> (decrypted) as its subroutines, and answers its outline and
        /// advance in glyph space — or adds an empty glyph and answers null when the charstring
        /// cannot be run, is a <c>seac</c> composite, or declares no width.
        /// </summary>
        /// <param name="charstring">The decrypted charstring, or null for an empty glyph.</param>
        /// <param name="subrs">The decrypted subroutines it may call.</param>
        public GlyphOutline Add(byte[] charstring, IReadOnlyList<byte[]> subrs) => Add(charstring, subrs, 1);

        /// <summary>
        /// Adds the glyph <paramref name="charstring"/> draws, as <see cref="Add(byte[], IReadOnlyList{byte[]})"/>
        /// does, from a glyph space <paramref name="scale"/> times the font's: each point and the advance
        /// multiplied by it and rounded to the nearest unit of the font's glyph space, so a font whose
        /// glyphs come from glyph spaces of different sizes (a CIDFont's FDs) is written in one. A glyph
        /// with a point past <see cref="MaxCoordinate"/> is refused as the others are.
        /// </summary>
        /// <param name="charstring">The decrypted charstring, or null for an empty glyph.</param>
        /// <param name="subrs">The decrypted subroutines it may call.</param>
        /// <param name="scale">The charstring's glyph-space unit in the font's units; finite and positive.</param>
        public GlyphOutline Add(byte[] charstring, IReadOnlyList<byte[]> subrs, double scale)
        {
            Util.NumericArguments.RequirePositive(scale, nameof(scale));
            Type1CharstringInterpreter.Glyph run = charstring == null ? null : Type1CharstringInterpreter.Run(charstring, subrs);
            if (run != null && scale != 1) run = Scaled(run, scale);
            bool usable = run != null && run.HasWidth && !run.IsComposite && Fits(run);
            _names.Add(_glyphs.Count == 0 ? ".notdef" : "g" + _glyphs.Count);
            _glyphs.Add(usable ? run : new Type1CharstringInterpreter.Glyph());
            return usable ? Type1Outlines.ToOutline(run) : null;
        }

        /// <summary>
        /// The largest coordinate, either way, a glyph added here may reach: half a Type 2 operand's
        /// range (CFF §4.1 and Appendix B, ±32767 as an integer or as 16.16 Fixed), so the difference
        /// between any two of its points — what a charstring writes — is one operand.
        /// </summary>
        public const int MaxCoordinate = 16383;

        private static bool Fits(Type1CharstringInterpreter.Glyph glyph)
        {
            if (!(System.Math.Abs(glyph.Width) <= MaxCoordinate)) return false;
            foreach (Type1CharstringInterpreter.Segment s in glyph.Segments)
            {
                if (!(System.Math.Abs(s.X1) <= MaxCoordinate && System.Math.Abs(s.Y1) <= MaxCoordinate)) return false;
                if (s.Kind == Type1CharstringInterpreter.SegmentKind.CurveTo
                    && !(System.Math.Abs(s.X2) <= MaxCoordinate && System.Math.Abs(s.Y2) <= MaxCoordinate
                         && System.Math.Abs(s.X3) <= MaxCoordinate && System.Math.Abs(s.Y3) <= MaxCoordinate))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// <paramref name="glyph"/> with every point and its advance multiplied by
        /// <paramref name="scale"/> and rounded: the writer takes each point's difference from the last
        /// one written unrounded, so a fractional point would carry its rounding into every later one.
        /// </summary>
        private static Type1CharstringInterpreter.Glyph Scaled(Type1CharstringInterpreter.Glyph glyph, double scale)
        {
            var scaled = new Type1CharstringInterpreter.Glyph
            {
                Width = Round(glyph.Width * scale),
                HasWidth = glyph.HasWidth,
                IsComposite = glyph.IsComposite,   // refused, so its accent's placement is not carried
            };
            foreach (Type1CharstringInterpreter.Segment s in glyph.Segments)
                scaled.Segments.Add(new Type1CharstringInterpreter.Segment
                {
                    Kind = s.Kind,
                    X1 = Round(s.X1 * scale), Y1 = Round(s.Y1 * scale),
                    X2 = Round(s.X2 * scale), Y2 = Round(s.Y2 * scale),
                    X3 = Round(s.X3 * scale), Y3 = Round(s.Y3 * scale),
                });
            return scaled;
        }

        private static double Round(double v) => System.Math.Round(v, System.MidpointRounding.AwayFromZero);

        /// <summary>The CFF of the glyphs added, or null with <paramref name="reason"/> when it cannot be written or none was added.</summary>
        /// <param name="reason">Why no font was written; null when one was.</param>
        public byte[] Build(out string reason)
        {
            if (_glyphs.Count == 0)
            {
                reason = "no glyph was added";
                return null;
            }
            return Type1ToCffConverter.Assemble(_name, _names, _glyphs, _fontMatrixScale, out reason);
        }
    }
}
