// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Fonts.Cff;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// A CFF font set (Adobe Technical Note #5176) as a reader outside Core needs it: the font names
    /// its Name INDEX gives, whether its first font is CID-keyed, each glyph's advance and outline by
    /// name or CID, and the glyph its built-in encoding gives a code — read through Core's own CFF
    /// parser and Type 2 evaluator.
    /// </summary>
    /// <remarks>
    /// <b>Public</b> because <c>Plugins.PostScript</c> reads a <c>/FontSet</c> resource's binary data
    /// (PLRM §5.8.1, <c>StartData</c>) through it, and the CFF parser's types are internal: this
    /// exposes the answers the plugin needs, not the parser.
    /// </remarks>
    public sealed class CffFontSet
    {
        private readonly CffFont _font;
        private readonly Dictionary<int, string> _nameByGid;

        private CffFontSet(byte[] data, CffFont font, IReadOnlyList<string> names)
        {
            Data = data;
            _font = font;
            FontNames = names;
            _nameByGid = new Dictionary<int, string>();
            if (font.GlyphNameToGid != null)
                foreach (var kv in font.GlyphNameToGid)
                    if (!_nameByGid.ContainsKey(kv.Value)) _nameByGid[kv.Value] = kv.Key;
        }

        /// <summary>The font set's bytes, as given.</summary>
        public byte[] Data { get; }

        /// <summary>The names of the fonts the set holds, in its Name INDEX's order.</summary>
        public IReadOnlyList<string> FontNames { get; }

        /// <summary>Whether the (first) font is CID-keyed: its Top DICT carries <c>ROS</c>.</summary>
        public bool IsCidKeyed => _font.IsCidKeyed;

        /// <summary>A CID-keyed font's character collection registry (<c>ROS</c>), or null.</summary>
        public string Registry { get; private set; }

        /// <summary>A CID-keyed font's character collection ordering (<c>ROS</c>), or null.</summary>
        public string Ordering { get; private set; }

        /// <summary>A CID-keyed font's character collection supplement (<c>ROS</c>).</summary>
        public int Supplement { get; private set; }

        /// <summary>
        /// The advance of the glyph a CID-keyed font draws for <paramref name="cid"/> — through its
        /// charset — in the 1000-unit glyph space a PDF font dictionary uses; false when the font is not
        /// CID-keyed, has no glyph for the CID, or its width cannot be read.
        /// </summary>
        /// <param name="cid">The CID.</param>
        /// <param name="advance">The advance, or 0.</param>
        public bool TryGetCidAdvance(int cid, out double advance)
        {
            advance = 0;
            return _font.IsCidKeyed && _font.CharCodeToGid.TryGetValue(cid, out int gid) && _font.TryGetAdvance(gid, out advance);
        }

        /// <summary>
        /// The set in <paramref name="data"/>, or null with <paramref name="reason"/> when it is no CFF
        /// font set this library reads (the parser's own reason, or a Name INDEX that will not read).
        /// </summary>
        /// <param name="data">The font set's bytes.</param>
        /// <param name="reason">Why the set was refused; null when it was read.</param>
        public static CffFontSet TryRead(byte[] data, out string reason)
        {
            reason = null;
            if (data == null || data.Length < 4 || data[2] < 4)
            {
                reason = "the data is no CFF header";
                return null;
            }
            if (!CffParser.TryLocateIndex(data, data[2], out int[] starts, out int[] lengths, out _) || starts.Length == 0)
            {
                reason = "its Name INDEX cannot be read";
                return null;
            }
            var names = new List<string>(starts.Length);
            for (int k = 0; k < starts.Length; k++)
                if (starts[k] >= 0) names.Add(Text.Latin1Text.Decode(data, starts[k], lengths[k]));

            CffFont font = CffParser.Parse(data);
            if (font == null)
            {
                reason = "the CFF parser refused it (" + CffParser.LastFailureReason + ")";
                return null;
            }
            var set = new CffFontSet(data, font, names);
            if (font.IsCidKeyed)
            {
                if (!CffParser.TryReadRos(data, out string registry, out string ordering, out int supplement))
                {
                    reason = "its ROS cannot be read";
                    return null;
                }
                set.Registry = registry;
                set.Ordering = ordering;
                set.Supplement = supplement;
            }
            return set;
        }

        /// <summary>
        /// The names of the fonts the set in <paramref name="data"/> holds, by their place in its Name
        /// INDEX — null for an entry deleted (its first byte 0) or outside the data — or null with
        /// <paramref name="reason"/> when the header or the Name INDEX will not read.
        /// </summary>
        /// <param name="data">The font set's bytes.</param>
        /// <param name="reason">Why no names were read; null when they were.</param>
        public static IReadOnlyList<string> TryReadFontNames(byte[] data, out string reason)
            => CffParser.TryReadFontNames(data, out reason);

        /// <summary>
        /// The font at <paramref name="fontIndex"/> of the set in <paramref name="data"/> (its place in
        /// <see cref="TryReadFontNames"/>) as a set of its own, its <see cref="Data"/> a CFF holding that
        /// font alone — an embedded CFF in PDF holds exactly one font (ISO 32000-2 §9.9) — or null with
        /// <paramref name="reason"/>: a deleted entry, a synthetic font, or a font that will not read.
        /// A set of one font is read as <see cref="TryRead(byte[], out string)"/> reads it.
        /// </summary>
        /// <param name="data">The font set's bytes.</param>
        /// <param name="fontIndex">The font's place in the set's Name INDEX.</param>
        /// <param name="reason">Why the font was refused; null when it was read.</param>
        public static CffFontSet TryRead(byte[] data, int fontIndex, out string reason)
        {
            byte[] single = CffParser.TryExtractFont(data, fontIndex, out reason);
            return single == null ? null : TryRead(single, out reason);
        }

        /// <summary>
        /// The advance of the glyph named <paramref name="glyphName"/> in the 1000-unit glyph space a
        /// PDF font dictionary uses; false when the font has no glyph of that name or its width cannot
        /// be read.
        /// </summary>
        /// <param name="glyphName">The glyph's name in the font's charset.</param>
        /// <param name="advance">The advance, or 0.</param>
        public bool TryGetAdvance(string glyphName, out double advance)
        {
            advance = 0;
            return glyphName != null && _font.GlyphNameToGid != null
                   && _font.GlyphNameToGid.TryGetValue(glyphName, out int gid)
                   && _font.TryGetAdvance(gid, out advance);
        }

        /// <summary>The name of the glyph the font's built-in encoding gives <paramref name="code"/>, or null for none.</summary>
        /// <param name="code">A character code, 0 to 255.</param>
        public string BuiltInGlyphName(int code)
            => _font.CharCodeToGid.TryGetValue(code, out int gid) && _nameByGid.TryGetValue(gid, out string name) ? name : null;

        /// <summary>
        /// The outline of the glyph named <paramref name="glyphName"/>, in the 1000-unit glyph space a
        /// PDF font dictionary uses, its advance that space's; null when the font has no glyph of that
        /// name or its charstring cannot be run to its end. See <see cref="CidOutline"/> for the cost.
        /// </summary>
        /// <param name="glyphName">The glyph's name in the font's charset.</param>
        public GlyphOutline Outline(string glyphName)
            => glyphName != null && _font.GlyphNameToGid != null && _font.GlyphNameToGid.TryGetValue(glyphName, out int gid)
                ? OutlineOf(gid)
                : null;

        /// <summary>
        /// The outline of the glyph a CID-keyed font draws for <paramref name="cid"/> — through its
        /// charset — as <see cref="Outline"/> gives one; null when the font is not CID-keyed or has no
        /// glyph for the CID.
        /// </summary>
        /// <remarks>
        /// Each glyph's charstring is run once, by the renderers' Type 2 evaluator, and its outline
        /// kept; the runs spend the font's own charstring budget
        /// (<see cref="Constants.PdfLimits.MaxCharstringStepsPerFont"/>, which its width reads share),
        /// and a glyph that budget stops short has no outline.
        /// </remarks>
        /// <param name="cid">The CID.</param>
        public GlyphOutline CidOutline(int cid)
            => _font.IsCidKeyed && _font.CharCodeToGid.TryGetValue(cid, out int gid) ? OutlineOf(gid) : null;

        private GlyphOutline OutlineOf(int gid)
        {
            if (_outlines.TryGetValue(gid, out GlyphOutline known)) return known;
            double scale = _font.FontMatrixScale * 1000.0;
            var sink = new GlyphOutlineSink(scale);
            Type2Charstrings.Result run = Type2Charstrings.Evaluate(_font, gid, sink, _font.ScanSteps);
            GlyphOutline outline = run.CutByBudget ? null : sink.ToOutline(run.AdvanceWidth * scale);
            _outlines[gid] = outline;
            return outline;
        }

        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, GlyphOutline> _outlines =
            new System.Collections.Concurrent.ConcurrentDictionary<int, GlyphOutline>();
    }
}
