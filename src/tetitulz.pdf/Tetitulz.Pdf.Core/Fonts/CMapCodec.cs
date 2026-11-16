// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// The <b>encoding</b> CMap of a composite (Type 0) font: it splits a show-string's bytes
    /// into character codes using the CMap's <c>codespacerange</c>s, and maps each code to a CID
    /// via <c>cidrange</c> / <c>cidchar</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is deliberately separate from <see cref="CMapParser"/>, which parses <c>/ToUnicode</c>
    /// (a code→Unicode aid for text extraction). Both renderers, and <c>PdfType0Font</c>, used to
    /// derive their code width from <c>/ToUnicode</c>, which is wrong on three counts:
    /// <c>/ToUnicode</c> is optional, its codespace may disagree with the encoding's, and it says
    /// nothing about code→CID. The normative source is the font's <c>/Encoding</c> entry
    /// (PDF §9.7.5), which is what this type models.
    /// </para>
    /// <para>
    /// <b>This is the only splitter.</b> The renderers reach it through
    /// <c>FontCache.GetEncodingCodec</c> and text extraction through
    /// <see cref="FromFontEncoding"/>; a second copy inside <c>PdfType0Font.Decode</c> kept the
    /// <c>/ToUnicode</c> rule for a year after the renderers were corrected, so a font whose two
    /// CMaps disagreed extracted text that was not the text that had been drawn.
    /// </para>
    /// <para>
    /// Codespace matching follows PDF §9.7.6.2: try byte lengths shortest-first and accept the
    /// first length whose bytes fall inside a declared range of that same length. This is what
    /// makes mixed-width CMaps (1-byte ASCII + 2-byte CJK, e.g. the RKSJ family) decode correctly
    /// — a fixed stride cannot represent them at all.
    /// </para>
    /// </remarks>
    internal sealed class CMapCodec
    {
        /// <summary>A code, the CID it maps to, and how many bytes it consumed.</summary>
        internal readonly struct Code
        {
            /// <summary>
            /// A code that a codespace range genuinely matched. This is the constructor simple
            /// fonts use, where every byte <i>is</i> a one-byte code by definition (§9.6.6.1).
            /// </summary>
            public Code(int value, int cid, int byteLength)
                : this(value, cid, byteLength, codespaceMatched: true)
            {
            }

            internal Code(int value, int cid, int byteLength, bool codespaceMatched, bool cidMapped = true)
            {
                CodespaceMatched = codespaceMatched;
                CidMapped = cidMapped;
                Value = value; Cid = cid; ByteLength = byteLength;
            }

            /// <summary>
            /// True when <see cref="Cid"/> is what the CMap's own mappings (<c>cidchar</c>,
            /// <c>cidrange</c>, or Identity) give the code. False when none maps it: ISO 32000-2
            /// §9.7.6.3 then selects a notdef mapping or CID 0 — the <c>.notdef</c> machinery — while
            /// <see cref="Cid"/> keeps the code's own value, which subset fonts that omit their
            /// <c>cidrange</c> table rely on to render (<see cref="ToCidOrOwnValue"/>).
            /// </summary>
            public bool CidMapped { get; }

            /// <summary>The raw character code as read from the string.</summary>
            public int Value { get; }

            /// <summary>The CID the encoding CMap maps <see cref="Value"/> to.</summary>
            public int Cid { get; }

            /// <summary>Bytes consumed — 1 for a single-byte code, 2 for Identity-H, etc.</summary>
            public int ByteLength { get; }

            /// <summary>
            /// True only for the <b>single-byte</b> code 32. PDF §9.3.3 restricts word spacing
            /// (<c>Tw</c>) to that case: it "shall not apply to occurrences of the byte value 32
            /// in multiple-byte codes".
            /// </summary>
            public bool IsWordSpaceCode => ByteLength == 1 && Value == 32 && CodespaceMatched;

            /// <summary>
            /// True when a codespace range of exactly <see cref="ByteLength"/> bytes matched these
            /// bytes — as opposed to the decoder having fallen back or run out of input.
            ///
            /// <para><b>Why <see cref="IsWordSpaceCode"/> needs this and <c>ByteLength == 1</c> is
            /// not enough.</b> §9.3.3 applies word spacing only to a font that <i>"defines code 32
            /// as a single-byte code"</i>. <c>ByteLength</c> answers a different question — how
            /// many bytes were consumed — and a decoder consumes one byte in two cases that are
            /// nothing like a one-byte code: a trailing byte with no room left for the two-byte
            /// code it belongs to, and a byte no codespace range matched at all. For a font whose
            /// only codespace is <c>&lt;0000&gt; &lt;FFFF&gt;</c>, <c>[00 41 20]</c> must not yield a
            /// final code with <c>len=1, Value=32</c> and therefore a word space — in a font that
            /// defines no single-byte code whatever.</para>
            /// </summary>
            internal bool CodespaceMatched { get; }
        }

        // Codespace ranges grouped by byte width (index 0 => 1 byte … index 3 => 4 bytes).
        private readonly List<(byte[] low, byte[] high)>[] _codespaces =
            new List<(byte[] low, byte[] high)>[4];

        // cidrange: [lo, hi] of a fixed byte width mapping linearly onto cidStart.
        private readonly List<(int lo, int hi, int cidStart, int nbytes)> _cidRanges =
            new List<(int, int, int, int)>();

        // cidchar: single code → CID. Key packs byte width so 1-byte 0x20 and 2-byte 0x0020 differ.
        private readonly Dictionary<long, int> _cidChars = new Dictionary<long, int>();

        /// <summary>
        /// <see cref="_cidRanges"/> grouped by code width (index = <c>nbytes − 1</c>) and sorted by
        /// <c>lo</c>, each range carrying its declaration order, so <see cref="ToCid"/> can
        /// binary-search instead of scanning every range for every character code. <c>null</c> when
        /// there are no ranges — see <see cref="BuildCidRangeIndex"/>.
        /// </summary>
        private (int lo, int hi, int cidStart, int order)[][] _cidRangeIndex;

        /// <summary>
        /// Per width, <c>_cidRangeIndexMaxHi[w][j]</c> is the largest <c>hi</c> among the sorted
        /// ranges <c>0..j</c>: the ranges that can still contain a code are exactly those at or
        /// before the last <c>lo ≤ code</c> whose prefix maximum reaches it, so the walk back from
        /// that point stops as soon as the prefix cannot.
        /// </summary>
        private int[][] _cidRangeIndexMaxHi;

        /// <summary>
        /// Whether <see cref="ToCid"/> is resolving through the sorted index rather than the linear
        /// scan — true for every CMap with a range; <c>false</c> is a correct,
        /// slower answer that an index-less codec gives.
        ///
        /// <para><b>This exists because the optimisation can disable itself silently.</b> Every
        /// answer is identical on both paths, by construction, so no behavioural test can tell them
        /// apart: a mutation removing the <c>Sort</c> in <see cref="BuildCidRangeIndex"/> makes the
        /// overlap check fire spuriously, abandons the index for every CMap, and leaves the whole
        /// suite green. That is the "feature non-functional behind passing tests" class this repo
        /// has shipped three times, so the liveness of the index is asserted directly instead of
        /// being inferred.</para>
        /// </summary>
        internal bool UsesCidRangeIndex => _cidRangeIndex != null;

        private int _fallbackLength = 1;

        /// <summary>Writing mode from the CMap's /WMode (0 horizontal, 1 vertical).</summary>
        public int WMode { get; private set; }

        /// <summary>True when this is Identity-H/V: 2-byte codes with CID == code.</summary>
        public bool IsIdentity { get; private set; }

        private CMapCodec() { }

        /// <summary>Identity-H (wmode 0) or Identity-V (wmode 1): two-byte codes, CID == code.</summary>
        public static CMapCodec Identity(int wMode)
        {
            var c = new CMapCodec { IsIdentity = true, WMode = wMode, _fallbackLength = 2 };
            c.AddCodespace(new byte[] { 0x00, 0x00 }, new byte[] { 0xFF, 0xFF });
            return c;
        }

        /// <summary>
        /// Resolves a predefined CMap by name. Identity-H/V are handled internally; every other
        /// name requires the blob to have been registered via
        /// <see cref="CMapParser.RegisterPredefinedCMap"/> (the Adobe CMap plugin does this).
        /// Returns null when the name is unknown, so callers can fall back.
        /// </summary>
        public static CMapCodec FromPredefinedName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (name == "Identity-H") return Identity(0);
            if (name == "Identity-V") return Identity(1);

            byte[] bytes = CMapParser.GetPredefinedBytes(name);
            if (bytes == null) return null;

            var codec = Parse(bytes);
            // Predefined vertical CMaps are named with a -V suffix; the blob normally also
            // carries /WMode 1, but the name is authoritative when it does not.
            if (name.EndsWith("-V", StringComparison.Ordinal)) codec.WMode = 1;
            return codec;
        }

        /// <summary>
        /// Reason the most recent <see cref="FromFontEncoding"/> call on <b>this thread</b> could
        /// not use the font's declared <c>/Encoding</c> in full, or <c>null</c> when it did. For an
        /// <c>/Encoding</c> it could not use at all the returned codec is the Identity fallback, so
        /// codes are being split two bytes at a time and treated as CIDs — which is right for the
        /// great majority of composite fonts and wrong for every CJK one that names a predefined
        /// CMap the host never registered. For an embedded CMap whose <c>usecmap</c> names nothing
        /// the stream's <c>/UseCMap</c> or the registry supplies, the codec is the stream's own
        /// mappings without the inherited ones, and the message names the CMap.
        /// </summary>
        /// <remarks>
        /// Backed by a <c>[ThreadStatic]</c> field and cleared at the start of every call, so a
        /// value read after building a font belongs to that font (see the Diagnostics Pattern
        /// section of CLAUDE.md). Reporting rather than throwing is required: <c>PdfFont.Create</c>
        /// builds fonts with no <c>try</c>/<c>catch</c>, so a throw would abort font loading for the
        /// whole document over one unreadable CMap.
        /// </remarks>
        public static string LastEncodingError => _lastEncodingError;

        [ThreadStatic]
        private static string _lastEncodingError;

        /// <summary>
        /// The encoding CMap of the composite font <paramref name="fontDict"/>, resolved from its
        /// <c>/Encoding</c> entry (§9.7.5): a predefined CMap name, or an embedded CMap stream.
        /// </summary>
        /// <remarks>
        /// Never returns null. A composite font with no usable <c>/Encoding</c> falls back to
        /// Identity-H — two-byte codes with CID == code — which is what the overwhelming majority
        /// of composite fonts declare, and is also the right answer when the dictionary handed in
        /// <i>is</i> the descendant CIDFont (which carries no <c>/Encoding</c> of its own).
        /// The fallback is reported through <see cref="LastEncodingError"/> whenever the font
        /// declared something that could not be honoured, because "no /Encoding" and "an /Encoding
        /// naming a CMap nobody registered" produce identical bytes and very different glyphs.
        /// </remarks>
        public static CMapCodec FromFontEncoding(PdfDictionary fontDict, PdfDocument document)
        {
            _lastEncodingError = null;
            if (fontDict == null) return Identity(0);

            var raw = fontDict[PdfName.Encoding];
            var encObj = document?.ResolveObject(raw) ?? raw;

            if (encObj is PdfName encName)
            {
                var byName = FromPredefinedName(encName.Value);
                if (byName != null) return byName;

                _lastEncodingError =
                    $"Predefined CMap '{encName.Value}' is not registered; assuming Identity " +
                    "(2-byte codes, CID == code). Register it via CMapParser.RegisterPredefinedCMap.";
                return Identity(encName.Value.EndsWith("-V", StringComparison.Ordinal) ? 1 : 0);
            }

            if (encObj is PdfStream encStream)
            {
                byte[] data = null;
                try { data = FontStreamBytes.CMap(encStream); }
                catch (Exception ex)
                {
                    _lastEncodingError =
                        "The embedded /Encoding CMap stream failed to decode; assuming Identity. " +
                        ex.GetType().Name + ": " + ex.Message;
                    return Identity(0);
                }

                if (data != null && data.Length > 0) return Parse(data, UseCMapParents(encStream, document));

                _lastEncodingError =
                    "The embedded /Encoding CMap stream decoded to nothing; assuming Identity.";
                return Identity(0);
            }

            // No /Encoding at all is normal for a descendant CIDFont, so it is not reported;
            // an /Encoding that is neither a name nor a stream is malformed and is.
            if (encObj != null)
                _lastEncodingError =
                    $"/Encoding is a {encObj.GetType().Name}, which is neither a predefined CMap " +
                    "name nor a CMap stream (§9.7.5); assuming Identity.";

            return Identity(0);
        }

        /// <summary>
        /// What a <c>usecmap</c> in an embedded CMap stream's program names: the stream's
        /// <c>/UseCMap</c> entry (§9.7.5.2, Table 120) — a stream, decoded under
        /// <see cref="FontStreamBytes.CMap"/>, whose own <c>/UseCMap</c> answers the next level, or a
        /// predefined CMap's name — before the registry, which <see cref="ResolveUsecmap"/> asks when
        /// this answers nothing. A stream that will not decode is reported and inherits nothing.
        /// </summary>
        private static Func<string, byte[]> UseCMapParents(PdfStream stream, PdfDocument document)
        {
            PdfStream current = stream;
            return name =>
            {
                if (current == null) return null;
                PdfObject raw = current[PdfName.UseCMap];
                PdfObject parent = document?.ResolveObject(raw) ?? raw;
                current = null;
                if (parent is PdfStream parentStream)
                {
                    current = parentStream;
                    try { return FontStreamBytes.CMap(parentStream); }
                    catch (Exception ex)
                    {
                        _lastEncodingError =
                            $"The /UseCMap stream behind '{name}' failed to decode, so its mappings were not " +
                            "inherited. " + ex.GetType().Name + ": " + ex.Message;
                        return null;
                    }
                }
                if (parent is PdfName parentName) return CMapParser.GetPredefinedBytes(parentName.Value);
                return null;
            };
        }

        /// <summary>Parses an embedded CMap stream (the /Encoding of a Type 0 font).</summary>
        public static CMapCodec Parse(byte[] cmapData) => Parse(cmapData, null);

        /// <summary>
        /// Parses a CMap program, a <c>usecmap</c> naming a CMap <paramref name="parents"/> answers
        /// with that CMap's program before a predefined one.
        /// </summary>
        internal static CMapCodec Parse(byte[] cmapData, Func<string, byte[]> parents)
        {
            var codec = new CMapCodec();
            if (cmapData == null || cmapData.Length == 0)
            {
                codec.AddCodespace(new byte[] { 0x00, 0x00 }, new byte[] { 0xFF, 0xFF });
                codec._fallbackLength = 2;
                return codec;
            }

            string text = Encoding.ASCII.GetString(cmapData);
            codec.ParseCodespaces(text);
            codec.ParseCidRanges(text);
            ParseCidChars(text, codec._cidChars);
            codec.ParseWMode(text);
            codec.ResolveUsecmap(text, parents, depth: 0);

            // A CMap with no codespacerange at all is malformed. Composite fonts are
            // overwhelmingly 2-byte, so assume that rather than mis-splitting into bytes.
            if (codec.TotalCodespaces() == 0)
            {
                codec.AddCodespace(new byte[] { 0x00, 0x00 }, new byte[] { 0xFF, 0xFF });
                codec._fallbackLength = 2;
            }

            codec.BuildCidRangeIndex();
            return codec;
        }

        /// <summary>
        /// Builds the per-width sorted index <see cref="ToCid"/> binary-searches.
        ///
        /// <para>Called at the end of <see cref="Parse(byte[], Func{string, byte[]})"/> — the only path that fills
        /// <see cref="_cidRanges"/>, which is written during parsing and read-only afterwards — and
        /// before each <c>usecmap</c> level is merged, whose characters yield to the ranges read so far.
        /// Eager rather than lazy so that a codec has no initialise-on-first-use step at all: today
        /// nothing shares one across threads (there is no codec cache — <see cref="FromPredefinedName"/>
        /// re-parses per font, and a <c>FontCache</c> belongs to a single render), but a lazy field
        /// would make that an invariant a future caller could break silently, and the index costs
        /// one sort of an already-parsed list.</para>
        ///
        /// <para><b>Why this matters.</b> <c>ToCid</c> ran a linear scan of every declared range for
        /// every character code. <c>UniCNS-UCS2-H</c> declares <b>16 418</b> of them, so showing one
        /// CJK page cost tens of millions of comparisons, on both the render and the extraction
        /// path.</para>
        ///
        /// <para><b>Overlapping ranges keep their declaration order.</b> The answer is the
        /// <i>first</i> match in the order the CMap declared them, so each sorted range carries its
        /// declaration index and the lookup takes, among the ranges containing the code, the
        /// earliest declared — abandoning the index for overlapping ranges would make every code
        /// scan the whole list, quadratic in page × CMap. For a non-overlapping set that walk is one
        /// comparison.</para>
        /// </summary>
        private void BuildCidRangeIndex()
        {
            if (_cidRanges.Count == 0) return;

            var byWidth = new List<(int lo, int hi, int cidStart, int order)>[4];
            for (int order = 0; order < _cidRanges.Count; order++)
            {
                var r = _cidRanges[order];
                (byWidth[r.nbytes - 1] ??= new List<(int lo, int hi, int cidStart, int order)>())
                    .Add((r.lo, r.hi, r.cidStart, order));
            }

            var index = new (int lo, int hi, int cidStart, int order)[4][];
            var maxHi = new int[4][];
            for (int w = 0; w < 4; w++)
            {
                var list = byWidth[w];
                if (list == null) continue;

                list.Sort((a, b) => a.lo != b.lo ? a.lo.CompareTo(b.lo) : a.order.CompareTo(b.order));

                var prefix = new int[list.Count];
                int running = int.MinValue;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].hi > running) running = list[i].hi;
                    prefix[i] = running;
                }

                index[w] = list.ToArray();
                maxHi[w] = prefix;
            }

            _cidRangeIndexMaxHi = maxHi;
            _cidRangeIndex = index;
        }

        /// <summary>
        /// Splits <paramref name="bytes"/> into codes. Always advances by at least one byte, so a
        /// string that matches no codespace still terminates.
        /// </summary>
        public List<Code> Decode(byte[] bytes)
        {
            var codes = new List<Code>(bytes == null ? 0 : bytes.Length);
            if (bytes == null) return codes;

            int i = 0;
            while (i < bytes.Length)
            {
                i += ReadCode(bytes, i, out Code code);
                codes.Add(code);
            }
            return codes;
        }

        /// <summary>
        /// The code starting at <paramref name="offset"/> in <paramref name="bytes"/>, and how many
        /// bytes it takes — at least one, so a caller stepping through a string always ends. What
        /// <see cref="Decode"/> does at each step, for a caller that walks a string without a list.
        /// </summary>
        public int ReadCode(byte[] bytes, int offset, out Code code)
        {
            // The matcher is pure: it answers 0 when no codespace range covers these bytes.
            // The FALLBACK lives here rather than inside it, because only here can the two be
            // told apart — and Code.CodespaceMatched exists precisely to carry that difference
            // through to §9.3.3's word-space rule.
            int len = MatchCodespaceLength(bytes, offset);
            bool matched = len > 0;

            if (!matched)
            {
                // Advance in code-sized steps rather than degenerating to single bytes. A fragment
                // at the end of the string — fewer bytes than a code — is one short unmatched code:
                // the matcher never answers past the bytes there are, and this is clamped to them.
                len = Math.Min(_fallbackLength, bytes.Length - offset);
                if (len <= 0) len = 1;
            }

            int value = 0;
            for (int k = 0; k < len; k++) value = (value << 8) | bytes[offset + k];

            int cid = ToCidOrOwnValue(value, len, out bool mapped);
            code = new Code(value, cid, len, matched, mapped);
            return len;
        }

        /// <summary>Maps a code of the given byte width to its CID (identity when unmapped).</summary>
        public int ToCid(int code, int byteLength) => ToCidOrOwnValue(code, byteLength, out _);

        /// <summary>
        /// <see cref="ToCid(int, int)"/>, saying whether a mapping gave the CID (<paramref name="mapped"/>)
        /// or the code fell back to its own value.
        /// </summary>
        internal int ToCidOrOwnValue(int code, int byteLength, out bool mapped)
        {
            mapped = true;
            if (IsIdentity) return code;

            if (_cidChars.TryGetValue(PackKey(code, byteLength), out int cid)) return cid;

            var index = _cidRangeIndex;
            if (index != null)
            {
                // Widths outside 1..4 are rejected at parse time, so no range can match one and
                // the scan below would fall through to the same answer.
                if (byteLength < 1 || byteLength > 4) { mapped = false; return code; }

                var ranges = index[byteLength - 1];
                if (ranges != null)
                {
                    int[] prefixMaxHi = _cidRangeIndexMaxHi[byteLength - 1];
                    // The last range whose lo is at or below the code; every range that can contain
                    // the code is at or before it.
                    int lo = 0, hi = ranges.Length - 1, last = -1;
                    while (lo <= hi)
                    {
                        int mid = lo + ((hi - lo) >> 1);
                        if (ranges[mid].lo <= code) { last = mid; lo = mid + 1; }
                        else hi = mid - 1;
                    }
                    // Walk back while some range at or before j still reaches the code; among the
                    // ones that contain it, the earliest declared wins, as the CMap's own order says.
                    int bestOrder = int.MaxValue, bestCid = code;
                    for (int j = last; j >= 0 && prefixMaxHi[j] >= code; j--)
                    {
                        var r = ranges[j];
                        if (code <= r.hi && r.order < bestOrder)
                        {
                            bestOrder = r.order;
                            bestCid = r.cidStart + (code - r.lo);
                        }
                    }
                    mapped = bestOrder != int.MaxValue;
                    return bestCid;
                }
                mapped = false;
                return code;
            }

            // No index (no ranges at all): the scan is trivially the definition.
            for (int i = 0; i < _cidRanges.Count; i++)
            {
                var r = _cidRanges[i];
                if (r.nbytes == byteLength && code >= r.lo && code <= r.hi)
                    return r.cidStart + (code - r.lo);
            }

            // Unmapped codes are CID 0 (notdef) per the CMap spec, but falling back to the code
            // keeps subset fonts that omit their cidrange table rendering something sensible.
            mapped = false;
            return code;
        }

        /// <summary>
        /// Byte length of the code starting at <paramref name="offset"/>, or 0 when nothing
        /// matches. Shortest declared width first, per PDF §9.7.6.2.
        /// </summary>
        private int MatchCodespaceLength(byte[] bytes, int offset)
        {
            int remaining = bytes.Length - offset;
            for (int len = 1; len <= 4; len++)
            {
                var ranges = _codespaces[len - 1];
                if (ranges == null || ranges.Count == 0) continue;
                if (len > remaining) break;

                for (int r = 0; r < ranges.Count; r++)
                {
                    var (low, high) = ranges[r];
                    bool inRange = true;
                    for (int k = 0; k < len; k++)
                    {
                        byte b = bytes[offset + k];
                        if (b < low[k] || b > high[k]) { inRange = false; break; }
                    }
                    if (inRange) return len;
                }
            }
            // No codespace matched. Answering 0 rather than a fallback width keeps this a pure
            // matcher: the caller applies the fallback and records that it did, which is what lets
            // a fallback byte be told from a genuine one-byte code. See Code.CodespaceMatched.
            return 0;
        }

        private static long PackKey(int code, int byteLength)
            => ((long)byteLength << 32) | (uint)code;

        private int TotalCodespaces()
        {
            int n = 0;
            for (int i = 0; i < _codespaces.Length; i++) n += _codespaces[i]?.Count ?? 0;
            return n;
        }

        private void AddCodespace(byte[] low, byte[] high)
        {
            if (low == null || high == null) return;
            if (low.Length != high.Length || low.Length < 1 || low.Length > 4) return;
            int idx = low.Length - 1;
            (_codespaces[idx] ??= new List<(byte[], byte[])>()).Add((low, high));
            if (low.Length < _fallbackLength || TotalCodespaces() == 1) _fallbackLength = low.Length;
        }

        // All three section readers take tokens from CMapParser.SectionTokens, which says why a line
        // is not the unit: several entries may share one, and one entry may span several.

        private void ParseCodespaces(string text)
        {
            string low = null;
            foreach (var (kind, token) in CMapParser.SectionTokens(text, "begincodespacerange", "endcodespacerange"))
            {
                if (kind != CMapParser.SectionTokenKind.Hex) { low = null; continue; }
                if (low == null) { low = token; continue; }
                AddCodespace(CMapParser.HexStringToBytesShared(low),
                             CMapParser.HexStringToBytesShared(token));
                low = null;
            }
        }

        private void ParseCidRanges(string text)
        {
            // "<lo> <hi> cid": the two hex strings nearest before each integer are the bounds.
            string low = null, high = null;
            foreach (var (kind, token) in CMapParser.SectionTokens(text, "begincidrange", "endcidrange"))
            {
                if (kind == CMapParser.SectionTokenKind.Hex)
                {
                    if (low == null) low = token;
                    else if (high == null) high = token;
                    else { low = high; high = token; }
                    continue;
                }

                if (kind == CMapParser.SectionTokenKind.Integer && high != null
                    && CMapParser.TryParseSectionInteger(token, out int cidStart))
                    AddCidRange(low, high, cidStart);
                low = high = null;
            }
        }

        private void AddCidRange(string lowHex, string highHex, int cidStart)
        {
            int nbytes = (lowHex.Length + 1) / 2;
            if (nbytes < 1 || nbytes > 4) return;
            int lo = CMapParser.ParseHexIntShared(lowHex);
            int hi = CMapParser.ParseHexIntShared(highHex);
            if (hi < lo) return;
            // A single entry may not describe more codes than the width allows.
            if ((long)hi - lo > PdfLimits.MaxCMapRangeSpan) return;
            _cidRanges.Add((lo, hi, cidStart, nbytes));
        }

        private static void ParseCidChars(string text, Dictionary<long, int> into)
        {
            // "<code> cid"; a code given twice takes the later CID.
            string code = null;
            foreach (var (kind, token) in CMapParser.SectionTokens(text, "begincidchar", "endcidchar"))
            {
                if (kind == CMapParser.SectionTokenKind.Hex) { code = token; continue; }

                if (kind == CMapParser.SectionTokenKind.Integer && code != null
                    && CMapParser.TryParseSectionInteger(token, out int cid))
                {
                    int nbytes = (code.Length + 1) / 2;
                    if (nbytes >= 1 && nbytes <= 4)
                        into[PackKey(CMapParser.ParseHexIntShared(code), nbytes)] = cid;
                }
                code = null;
            }
        }

        private void ParseWMode(string text)
        {
            int idx = text.IndexOf("/WMode", StringComparison.Ordinal);
            if (idx < 0) return;
            int i = idx + 6;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i < text.Length && text[i] == '1') WMode = 1;
        }

        /// <summary>
        /// Merges the codespaces and CID mappings of a CMap referenced via <c>usecmap</c>, under the
        /// mappings already read: ISO 32000-2 §9.7.5.3 has the referencing CMap give "only the
        /// character mappings that differ", so a referenced <c>cidchar</c> is taken only for a code
        /// nothing nearer maps — <see cref="ToCidOrOwnValue"/> would otherwise answer it before the
        /// nearer CMap's own range — and its ranges follow the nearer ones, which the first-declared
        /// rule then prefers. Bounded by <see cref="PdfLimits.MaxCMapUsecmapDepth"/> — the referenced
        /// blob is caller-registered and could otherwise self-reference into a StackOverflow.
        /// </summary>
        private void ResolveUsecmap(string text, Func<string, byte[]> parents, int depth)
        {
            if (depth >= PdfLimits.MaxCMapUsecmapDepth) return;

            int idx = text.IndexOf("usecmap", StringComparison.Ordinal);
            if (idx < 0) return;

            int end = idx - 1;
            while (end >= 0 && char.IsWhiteSpace(text[end])) end--;
            if (end < 0) return;
            int nameEnd = end + 1, nameStart = end;
            while (nameStart > 0 && text[nameStart - 1] != '/') nameStart--;
            if (nameStart == 0 || text[nameStart - 1] != '/' || nameEnd <= nameStart) return;

            string parentName = text.Substring(nameStart, nameEnd - nameStart);
            if (parentName == "Identity-H" || parentName == "Identity-V")
            {
                AddCodespace(new byte[] { 0x00, 0x00 }, new byte[] { 0xFF, 0xFF });
                // Identity maps every two-byte code to the CID of its value, so the codes the
                // referencing CMap leaves to it are mapped, not fallen back on — a range after its
                // own, which the first-declared rule keeps behind them. Without it every inherited
                // code read as unmapped, and a .notdef check reported each as a reference to glyph 0.
                _cidRanges.Add((0x0000, 0xFFFF, 0, 2));
                if (parentName == "Identity-V") WMode = 1;
                return;
            }

            byte[] parentBytes = parents?.Invoke(parentName) ?? CMapParser.GetPredefinedBytes(parentName);
            if (parentBytes == null)
            {
                _lastEncodingError ??=
                    $"usecmap names '{parentName}', which is neither the stream's /UseCMap nor a registered " +
                    "predefined CMap, so its mappings were not inherited.";
                return;
            }

            string parentText = Encoding.ASCII.GetString(parentBytes);
            ParseCodespaces(parentText);

            BuildCidRangeIndex();   // the ranges read so far, which the referenced characters yield to
            var parentChars = new Dictionary<long, int>();
            ParseCidChars(parentText, parentChars);
            foreach (var entry in parentChars)
            {
                ToCidOrOwnValue((int)(uint)entry.Key, (int)(entry.Key >> 32), out bool mapped);
                if (!mapped) _cidChars[entry.Key] = entry.Value;
            }

            ParseCidRanges(parentText);
            ResolveUsecmap(parentText, parents, depth + 1);
        }

    }
}
