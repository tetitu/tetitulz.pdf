// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.Fonts.Type1
{
    /// <summary>
    /// A Type 1 font program's glyphs (Adobe Type 1 Font Format): each charstring run — subroutines,
    /// flex and <c>seac</c> accents included, hints dropped — into its outline and its
    /// <c>hsbw</c>/<c>sbw</c> advance, in glyph space (a caller applies the font's matrix). What a PostScript interpreter needs to append a glyph's path (<c>charpath</c>) or measure
    /// a string, and a PDF writer the <c>/Widths</c> of the program it embeds.
    /// </summary>
    public sealed class Type1Outlines
    {
        private readonly Type1Font _font;
        private readonly Dictionary<string, GlyphOutline> _glyphs = new(System.StringComparer.Ordinal);

        private Type1Outlines(Type1Font font) => _font = font;

        /// <summary>
        /// The glyphs of the program in <paramref name="program"/> — its clear-text part
        /// <paramref name="length1"/> bytes long, its eexec part <paramref name="length2"/> — or null,
        /// with <paramref name="reason"/>, when it is not one this library can read.
        /// </summary>
        /// <param name="program">The program's bytes, as a <c>/FontFile</c> stream holds them.</param>
        /// <param name="length1">The clear-text part's length.</param>
        /// <param name="length2">The eexec part's length.</param>
        /// <param name="reason">Why the program could not be read, or null.</param>
        public static Type1Outlines TryRead(byte[] program, int length1, int length2, out string reason)
        {
            reason = null;
            Type1Font font = Type1Parser.Parse(program, length1, length2);
            if (font == null)
            {
                reason = "the program's charstrings could not be read";
                return null;
            }
            return new Type1Outlines(font);
        }

        /// <summary>
        /// The glyph named <paramref name="glyphName"/>, run once and kept; false when the program has
        /// no such glyph, its charstring cannot be run, or it declares no width.
        /// </summary>
        /// <param name="glyphName">The glyph's name in the program's <c>/CharStrings</c>.</param>
        /// <param name="glyph">The glyph, or null.</param>
        public bool TryGetGlyph(string glyphName, out GlyphOutline glyph)
        {
            glyph = null;
            if (glyphName == null) return false;
            if (_glyphs.TryGetValue(glyphName, out glyph)) return glyph != null;
            glyph = Build(glyphName, 0);
            _glyphs[glyphName] = glyph;
            return glyph != null;
        }

        private GlyphOutline Build(string glyphName, int depth)
        {
            if (depth > 1 || _font.CharStrings == null || !_font.CharStrings.TryGetValue(glyphName, out byte[] charstring)) return null;
            Type1CharstringInterpreter.Glyph run = Type1CharstringInterpreter.Run(charstring, _font.Subrs);
            if (run == null || !run.HasWidth) return null;

            var segments = new List<GlyphOutlineSegment>();
            if (run.IsComposite)
            {
                // seac: the base glyph as it is, the accent with its origin at adx − asb + the
                // composite's sbx (as the renderer and the CFF converter place it).
                GlyphOutline baseGlyph = Build(AdobeStandardEncodingNames.Get(run.BaseCharCode), depth + 1);
                GlyphOutline accent = Build(AdobeStandardEncodingNames.Get(run.AccentCharCode), depth + 1);
                if (baseGlyph == null || accent == null) return null;
                segments.AddRange(baseGlyph.Segments);
                double dx = run.AccentX - run.AccentSidebearing + run.SidebearingX, dy = run.AccentY;
                foreach (GlyphOutlineSegment s in accent.Segments)
                    segments.Add(new GlyphOutlineSegment(s.Kind, s.X1 + dx, s.Y1 + dy, s.X2 + dx, s.Y2 + dy, s.X3 + dx, s.Y3 + dy));
                return new GlyphOutline(segments, run.Width);
            }

            return ToOutline(run);
        }

        /// <summary>
        /// A glyph the interpreter ran, not a <c>seac</c> composite, as an outline in glyph space —
        /// every subpath closed — advancing by its <c>hsbw</c> width.
        /// </summary>
        internal static GlyphOutline ToOutline(Type1CharstringInterpreter.Glyph run)
        {
            var segments = new List<GlyphOutlineSegment>();
            bool open = false;
            foreach (Type1CharstringInterpreter.Segment s in run.Segments)
            {
                switch (s.Kind)
                {
                    case Type1CharstringInterpreter.SegmentKind.MoveTo:
                        if (open) segments.Add(new GlyphOutlineSegment(GlyphOutlineSegmentKind.ClosePath, 0, 0, 0, 0, 0, 0));
                        segments.Add(new GlyphOutlineSegment(GlyphOutlineSegmentKind.MoveTo, 0, 0, 0, 0, s.X1, s.Y1));   // a move and a line keep their point in X1, Y1
                        open = true;
                        break;
                    case Type1CharstringInterpreter.SegmentKind.LineTo:
                        segments.Add(new GlyphOutlineSegment(GlyphOutlineSegmentKind.LineTo, 0, 0, 0, 0, s.X1, s.Y1));
                        break;
                    default:
                        segments.Add(new GlyphOutlineSegment(GlyphOutlineSegmentKind.CurveTo, s.X1, s.Y1, s.X2, s.Y2, s.X3, s.Y3));
                        break;
                }
            }
            if (open) segments.Add(new GlyphOutlineSegment(GlyphOutlineSegmentKind.ClosePath, 0, 0, 0, 0, 0, 0));
            return new GlyphOutline(segments, run.Width);
        }
    }
}
