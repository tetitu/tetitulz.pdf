// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Fonts.Type1;

namespace Tetitulz.Pdf.Plugins.PostScript.Emit
{
    /// <summary>
    /// A Type 1 font program lifted whole out of the PostScript source.
    ///
    /// <para><b>The bytes are passed through, not interpreted</b> — the same principle that makes
    /// the CJK CMap path work. A PDF <c>/FontFile</c> stream <i>is</i> a Type 1 font program: the
    /// clear-text header, the <c>eexec</c>-encrypted section and the zero trailer, exactly as they
    /// appear in the PostScript. So embedding needs no charstring decryption, no glyph outlines and
    /// no understanding of the font at all — only the three section lengths the format requires.</para>
    ///
    /// <para>This is what lifts the last script limit. Hebrew and Cyrillic have no glyphs in any
    /// standard-14 face, so a <c>/Differences</c> encoding alone leaves the reader nothing to draw;
    /// with the document's own font embedded, the glyphs the document shipped are the glyphs the
    /// reader uses.</para>
    /// </summary>
    internal sealed class PsEmbeddedFont
    {
        /// <summary>The <c>/FontName</c> declared in the clear-text header.</summary>
        public string FontName { get; init; }

        /// <summary>The whole program: header + encrypted section + trailer.</summary>
        public byte[] Program { get; init; } = Array.Empty<byte>();

        /// <summary>
        /// The Type 42 font this font is — a TrueType program from its dictionary's <c>/sfnts</c>,
        /// shown by glyph index through a Type0 font — or null for a captured Type 1 program, whose
        /// members above and below are the ones that apply.
        /// </summary>
        public PsType42Font TrueType { get; init; }

        /// <summary>The Type 0 CIDFont this font is — Type 1 charstrings by CID, carried as a CFF — or null.</summary>
        public PsCidType0Font CidType0 { get; init; }

        /// <summary>The program this font shows by glyph index — a Type 42 font, a Type 2 or a Type 0 CIDFont — or null.</summary>
        public IPsGlyphIndexedProgram GlyphIndexed => (IPsGlyphIndexedProgram)TrueType ?? CidType0;

        /// <summary>
        /// The CFF font set this font is the one font of — from a <c>/FontSet</c> resource's
        /// <c>StartData</c> (PLRM §5.8.1), embedded as a <c>/FontFile3</c> of <c>/Type1C</c> — or null.
        /// Its built-in encoding is <see cref="BuiltInEncoding"/>.
        /// </summary>
        public Core.Fonts.CffFontSet Cff { get; init; }

        private readonly Dictionary<int, double> _cidAdvances = new();

        /// <summary>Each CID a CID-keyed CFF font has shown or measured, with its advance in 1000-unit glyph space — the descendant's <c>/W</c>.</summary>
        public IReadOnlyDictionary<int, double> CidAdvances => _cidAdvances;

        /// <summary>
        /// The advance of the glyph a CID-keyed CFF font draws for <paramref name="cid"/>, in 1000-unit
        /// glyph space — its <c>.notdef</c>'s (CID 0) for a CID the font lacks, 0 when neither reads —
        /// recorded for <see cref="CidAdvances"/>.
        /// </summary>
        public double CidAdvance(int cid)
        {
            if (_cidAdvances.TryGetValue(cid, out double known)) return known;
            double advance = Cff.TryGetCidAdvance(cid, out double own) || Cff.TryGetCidAdvance(0, out own) ? own : 0;
            _cidAdvances[cid] = advance;
            return advance;
        }

        /// <summary>Whether the program's own widths can be read: a Type 1 program Core reads, or a CFF font.</summary>
        public bool CanMeasure => Cff != null || Outlines != null;

        /// <summary>
        /// The advance of the glyph named <paramref name="name"/> in 1000-unit glyph space — its
        /// <c>.notdef</c>'s for a name the program lacks, 0 when it has neither — by the program's own
        /// widths (<see cref="CanMeasure"/>).
        /// </summary>
        public double GlyphAdvance(string name)
        {
            if (Cff != null)
                return Cff.TryGetAdvance(name, out double advance) || Cff.TryGetAdvance(".notdef", out advance) ? advance : 0;
            var outlines = Outlines;
            return outlines != null && (outlines.TryGetGlyph(name, out var glyph) || outlines.TryGetGlyph(".notdef", out glyph))
                ? glyph.Advance
                : 0;
        }

        /// <summary>
        /// The one font of <paramref name="set"/>, its built-in encoding read from the set (codes 0 to
        /// 255 that name a glyph).
        /// </summary>
        public static PsEmbeddedFont FromCff(Core.Fonts.CffFontSet set)
        {
            var encoding = new Dictionary<int, string>();
            for (int code = 0; code < 256; code++)
                if (set.BuiltInGlyphName(code) is string name && name != ".notdef") encoding[code] = name;
            return new PsEmbeddedFont
            {
                FontName = set.FontNames.Count > 0 ? set.FontNames[0] : null,
                Cff = set,
                BuiltInEncoding = encoding.Count > 0 ? encoding : null,
            };
        }

        /// <summary>Clear-text header length, through the newline after <c>eexec</c>.</summary>
        public int Length1 { get; init; }

        /// <summary>Encrypted section length.</summary>
        public int Length2 { get; init; }

        /// <summary>Trailer length — the 512 zeros and <c>cleartomark</c>.</summary>
        public int Length3 { get; init; }

        /// <summary>
        /// True when the capture stopped at a real terminator rather than at the end of the buffer.
        /// See <c>PsEmbeddedFontBytes.TrailerFound</c>, and <see cref="IsWellFormed"/> for why the
        /// lengths alone cannot tell the two apart.
        /// </summary>
        public bool TrailerFound { get; init; }

        // No /FontMatrix property, deliberately: the emitted PDF font has no step that agrees with
        // it. A PDF font dictionary has nowhere to restate a Type 1 /FontMatrix, and it does not
        // need one. The reader parses the same program we passed through byte for byte and takes
        // the matrix from there, so the header's copy is redundant by construction.

        /// <summary>
        /// The <c>/Encoding</c> the header declares as code to glyph name, or null when it uses
        /// <c>StandardEncoding</c> or builds the array in a way this does not read.
        ///
        /// <para>This is the encoding that matters for a non-Latin font: the document's own font
        /// carries both the glyphs <i>and</i> the mapping from its codepage to them.</para>
        /// </summary>
        public IReadOnlyDictionary<int, string> BuiltInEncoding { get; init; }

        /// <summary>
        /// True when the program is one a reader can actually build a font from: a non-empty
        /// clear-text header, a non-empty encrypted section, a non-negative trailer, the three
        /// lengths within the captured bytes, <b>and</b> a capture that stopped at a real
        /// terminator.
        ///
        /// <para><b>The last clause is what the lengths cannot express.</b> The predicate is not
        /// "the lengths add up to the program and none is zero": it uses <c>&lt;=</c> rather than
        /// <c>==</c> and permits <c>Length3 == 0</c>, and both of those are correct — Table 127 makes a zero-length fixed-content portion legal, and a
        /// captured program may carry trailing bytes. What is <i>not</i> correct is that a scan
        /// which ran off the end of the buffer produces exactly the same three numbers as a program
        /// that legitimately omits the trailer: <c>trailerStart</c> stays -1, <c>encryptedEnd</c>
        /// becomes the limit, and <c>Length3</c> is 0 either way. So on the lengths alone a
        /// truncated download — <c>currentfile eexec</c> followed by 200 bytes of binary and EOF —
        /// would pass the gate that exists to reject it, be embedded verbatim and reported as a
        /// success, and the reader would fail to build the font and draw nothing. <see cref="TrailerFound"/> carries the
        /// distinction out of the scanner.</para>
        /// </summary>
        public bool IsWellFormed =>
            Program.Length > 0 &&
            Length1 > 0 && Length2 > 0 && Length3 >= 0 &&
            Length1 + Length2 + Length3 <= Program.Length &&
            TrailerFound;

        /// <summary>
        /// The program's glyphs — each charstring run into its outline and advance by Core's
        /// <see cref="Type1Outlines"/> — read the first time something asks, or null when the program
        /// is not well formed or not one Core can read. What lets <c>charpath</c> append a glyph's real
        /// outline, a string be measured by the program's own widths, and the font dictionary carry the
        /// <c>/Widths</c> Table 111 requires.
        /// </summary>
        public Type1Outlines Outlines
        {
            get
            {
                if (_outlinesRead) return _outlines;
                _outlinesRead = true;
                // Only a well-formed program is registered (PostScriptConversion refuses the rest at
                // capture), so this reads one whose lengths are already known to agree.
                _outlines = Type1Outlines.TryRead(Program, Length1, Length2, out _);
                return _outlines;
            }
        }

        private Type1Outlines _outlines;
        private bool _outlinesRead;

        /// <summary>
        /// The glyph name <paramref name="code"/> selects: through <paramref name="encoding"/> — the
        /// font dictionary's — or, when the dictionary has none, the program's own array, a code
        /// either leaves unnamed being <c>.notdef</c>; through StandardEncoding when neither has an
        /// array (a program that declares <c>/Encoding StandardEncoding</c>).
        /// </summary>
        public string GlyphNameFor(int code, IReadOnlyDictionary<int, string> encoding)
        {
            IReadOnlyDictionary<int, string> names = encoding ?? BuiltInEncoding;
            if (names != null) return names.TryGetValue(code, out string name) ? name : ".notdef";
            return Interpreter.PsEncodings.StandardName(code) ?? ".notdef";
        }

        /// <summary>
        /// Reads what the clear-text header declares: the font name and the built-in encoding.
        ///
        /// <para>Scanned rather than parsed. The header is PostScript, but running it would mean
        /// re-entering the interpreter from inside the scanner, and both things needed are a single
        /// well-known line. Anything not found simply stays null, and the caller falls back — this
        /// never fails a conversion.</para>
        /// </summary>
        public static PsEmbeddedFont FromProgram(byte[] program, int length1, int length2, int length3,
                                                 bool trailerFound)
        {
            if (program == null || program.Length == 0) return null;

            string header = Latin1(program, 0, Math.Min(length1, program.Length));

            return new PsEmbeddedFont
            {
                Program = program,
                Length1 = length1,
                Length2 = length2,
                Length3 = length3,
                TrailerFound = trailerFound,
                FontName = ReadName(header, "/FontName"),
                BuiltInEncoding = ReadEncoding(header)
            };
        }

        /// <summary>The token after <paramref name="key"/>, stripped of a leading slash.</summary>
        private static string ReadName(string header, string key)
        {
            int at = header.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return null;

            int i = at + key.Length;
            while (i < header.Length && char.IsWhiteSpace(header[i])) i++;
            if (i < header.Length && header[i] == '/') i++;

            int start = i;
            while (i < header.Length && !char.IsWhiteSpace(header[i])) i++;
            return i > start ? header.Substring(start, i - start) : null;
        }

        /// <summary>
        /// The <c>dup &lt;code&gt; /&lt;glyph&gt; put</c> lines of a built-in encoding array.
        ///
        /// <para>A font that says <c>/Encoding StandardEncoding def</c> has no such lines and yields
        /// null, which is right: there is nothing to override.</para>
        /// </summary>
        private static Dictionary<int, string> ReadEncoding(string header)
        {
            int at = header.IndexOf("/Encoding", StringComparison.Ordinal);
            if (at < 0) return null;

            var map = new Dictionary<int, string>();
            int i = at;

            while (true)
            {
                i = header.IndexOf("dup ", i, StringComparison.Ordinal);
                if (i < 0) break;
                i += 4;

                while (i < header.Length && char.IsWhiteSpace(header[i])) i++;

                int numStart = i;
                while (i < header.Length && char.IsDigit(header[i])) i++;
                if (i == numStart) continue;

                if (!int.TryParse(header.Substring(numStart, i - numStart), out int code)) continue;

                while (i < header.Length && char.IsWhiteSpace(header[i])) i++;
                if (i >= header.Length || header[i] != '/') continue;
                i++;

                int nameStart = i;
                while (i < header.Length && !char.IsWhiteSpace(header[i])) i++;
                if (i == nameStart) continue;

                string glyph = header.Substring(nameStart, i - nameStart);
                if (code >= 0 && code < 256 && glyph != ".notdef") map[code] = glyph;

                // A 256-entry array is the normal size; more than that is a malformed header.
                if (map.Count > 256) break;
            }

            return map.Count > 0 ? map : null;
        }

        // One implementation, in Core.Text.Latin1Text — six byte-identical copies existed.
        private static string Latin1(byte[] bytes, int offset, int length)
            => Core.Text.Latin1Text.Decode(bytes, offset, length);
    }
}
