// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// A composite (Type 0) font. Its show-string bytes are split into character codes by the
    /// <b>/Encoding</b> CMap (§9.7.5), which is also the only thing that knows each code's CID.
    /// </summary>
    /// <remarks>
    /// <para>One splitter, <see cref="CMapCodec"/>, for this class and both renderers. A second,
    /// driven by <c>/ToUnicode</c>'s codespace ranges, would split a font whose two CMaps disagree —
    /// a 1-byte encoding codespace under a 2-byte <c>/ToUnicode</c>, or any mixed-width CMap — one
    /// way for extraction and the other for drawing, so the extracted text would not be the text
    /// drawn; see <c>CompositeCodeSplittingTests</c>.</para>
    ///
    /// <para><b>A code's value and its CID are different numbers.</b> <c>/ToUnicode</c> is keyed by
    /// the <i>value</i>; <c>/W</c>, <c>/CIDToGIDMap</c> and the CID-collection CMaps are keyed by
    /// the <i>CID</i>. They coincide only under Identity-H, which is why conflating them survives
    /// almost every fixture.</para>
    /// </remarks>
    internal sealed class PdfType0Font : PdfFont
    {
        private readonly Dictionary<int, string> _toUnicode;
        private readonly CMapCodec _codec;
        private readonly Dictionary<int, int> _cidToGid;
        private readonly bool _isIdentityCidToGid;
        // CID font width tables
        private readonly double _defaultWidth;   // /DW (default 1000)
        private readonly Dictionary<int, double> _cidWidths; // from /W array
        // Adobe standard CID ordering (e.g. "Japan1", "GB1", "CNS1", "Korea1") for predefined CMap lookup
        private readonly string _cidOrdering;

        /// <param name="cmapMaxCodeBytes">
        /// <b>Ignored.</b> Retained so existing callers still compile. The code width comes from
        /// the font's <c>/Encoding</c> CMap (§9.7.5) and never from <c>/ToUnicode</c>, so passing
        /// <c>1</c> here no longer forces single-byte splitting — declare a one-byte
        /// <c>codespacerange</c> in <c>/Encoding</c> instead, which is what a file that really is
        /// single-byte does.
        /// </param>
        /// <param name="fontDict">The Type 0 font dictionary.</param>
        /// <param name="document">The document, for resolving indirect references.</param>
        /// <param name="toUnicode">
        /// Parsed <c>/ToUnicode</c> CMap, for extraction only. It never decides how bytes split into
        /// codes — that is <c>/Encoding</c>'s job (§9.7.5), and conflating the two is the defect
        /// <c>CMapCodec</c> exists to prevent.
        /// </param>
        public PdfType0Font(PdfDictionary fontDict, PdfDocument document,
            Dictionary<int, string> toUnicode, int cmapMaxCodeBytes = 2)
            : this(fontDict, document, toUnicode, (CMapCodec)null)
        {
        }

        /// <param name="encodingCodec">
        /// The font's /Encoding CMap. Null resolves it from <paramref name="fontDict"/>.
        /// </param>
        /// <param name="fontDict">The Type 0 font dictionary.</param>
        /// <param name="document">The document, for resolving indirect references.</param>
        /// <param name="toUnicode">
        /// Parsed <c>/ToUnicode</c> CMap, for extraction only — never for code splitting (§9.7.5).
        /// </param>
        internal PdfType0Font(PdfDictionary fontDict, PdfDocument document,
            Dictionary<int, string> toUnicode, CMapCodec encodingCodec)
        {
            _toUnicode = toUnicode;
            _codec = encodingCodec ?? CMapCodec.FromFontEncoding(fontDict, document);

            BaseFont = ResolvedName(fontDict, PdfName.BaseFont, document)?.Value;

            // Parse descendant CID font for CIDToGIDMap and width tables
            _cidToGid = null;
            _defaultWidth = 1000; // PDF spec default for /DW
            _cidWidths = new Dictionary<int, double>();
            var descendantsObj = fontDict[PdfName.DescendantFonts];
            if (descendantsObj != null)
            {
                var descendants = document.ResolveObject(descendantsObj) as PdfArray;
                if (descendants != null && descendants.Count > 0)
                {
                    var cidFont = document.ResolveObject(descendants[0]) as PdfDictionary;
                    if (cidFont != null)
                    {
                        // CIDToGIDMap — PDF spec §9.7.4.2: for CIDFontType2, absent == /Identity.
                        var cidSubtype = ResolvedName(cidFont, PdfName.Subtype, document)?.Value;
                        var gidMapObj = cidFont[PdfName.CIDToGIDMap];
                        if (gidMapObj != null)
                        {
                            var resolved = document.ResolveObject(gidMapObj);
                            if (resolved is PdfStream gidStream)
                            {
                                byte[] mapData = gidStream.GetDecodedData();
                                if (mapData != null && mapData.Length >= 2)
                                {
                                    // Bound the map to the addressable CID space (2 bytes x 64K).
                                    // /CIDToGIDMap is an untrusted compressed stream: a ~500 KB
                                    // PDF can decompress to hundreds of MB here, and every
                                    // non-zero pair became a Dictionary entry (~40 bytes each),
                                    // so the dictionary — not the byte array — was the OOM.
                                    // Anything past CID 0xFFFF is unreachable by a 2-byte CID
                                    // anyway, so this discards nothing usable.
                                    int limit = Math.Min(mapData.Length, PdfLimits.MaxCidToGidMapBytes);
                                    _cidToGid = new Dictionary<int, int>(limit / 2);
                                    for (int i = 0; i + 1 < limit; i += 2)
                                    {
                                        int gid = (mapData[i] << 8) | mapData[i + 1];
                                        if (gid != 0)
                                            _cidToGid[i / 2] = gid;
                                    }
                                }
                            }
                            else if (resolved is PdfName cidToGidName &&
                                     string.Equals(cidToGidName.Value, PdfName.Identity.Value,
                                         System.StringComparison.OrdinalIgnoreCase))
                            {
                                _isIdentityCidToGid = true;
                            }
                        }
                        else if (cidSubtype == PdfName.CIDFontType2.Value)
                        {
                            // Absent CIDToGIDMap defaults to /Identity for TrueType CID fonts.
                            _isIdentityCidToGid = true;
                        }

                        // /DW — default glyph width
                        var dwObj = Resolved(cidFont, PdfName.DW, document) as PdfNumber;
                        if (dwObj != null) _defaultWidth = dwObj.DoubleValue;

                        // /W — per-CID width overrides
                        var wArr = cidFont[PdfName.W] as PdfArray;
                        if (wArr == null)
                        {
                            var wRef = cidFont[PdfName.W];
                            if (wRef != null) wArr = document.ResolveObject(wRef) as PdfArray;
                        }
                        if (wArr != null)
                            ParseWArray(wArr, _cidWidths, document.ResolveObject);

                        // /DW2 and /W2 — the vertical metrics (§9.7.4.3, Table 117), for a font in
                        // vertical writing mode; the elements resolved, as /W's are.
                        if (document.ResolveObject(cidFont[PdfName.DW2]) is PdfArray dw2 && dw2.Count >= 2
                            && document.ResolveObject(dw2[1]) is PdfNumber w1y)
                            _defaultVerticalAdvance = Math.Abs(w1y.DoubleValue) / PdfUnits.GlyphSpaceUnitsPerEm;
                        if (document.ResolveObject(cidFont[PdfName.W2]) is PdfArray w2)
                            _verticalMetrics = CidVerticalMetrics.Index.Read(w2, document.ResolveObject);

                        // /CIDSystemInfo — read /Ordering for predefined CMap fallback
                        _cidOrdering = ReadCidOrdering(cidFont, document);
                    }
                }
            }

            // For the CIDFontType0/CIDFontType2 direct case (when this dict IS the CIDFont),
            // also try reading CIDSystemInfo from the font dict itself.
            if (_cidOrdering == null)
                _cidOrdering = ReadCidOrdering(fontDict, document);
        }

        private static string ReadCidOrdering(PdfDictionary dict, PdfDocument document)
        {
            var obj = dict[PdfName.CIDSystemInfo];
            if (obj == null) return null;
            var cs = obj as PdfDictionary ?? document.ResolveObject(obj) as PdfDictionary;
            if (cs == null) return null;
            var orderingObj = Resolved(cs, PdfName.Ordering, document);
            if (orderingObj is PdfString pstr) return pstr.GetText();
            if (orderingObj is PdfName pname) return pname.Value;
            return null;
        }

        public override IReadOnlyDictionary<int, int> GetCidToGid() => _cidToGid;
        public override bool IsIdentityCidToGid => _isIdentityCidToGid;

        /// <inheritdoc/>
        /// <remarks>
        /// <paramref name="cid"/> is a <b>CID</b>, not a character code: <c>/W</c> is indexed by CID
        /// (§9.7.4.3). Pass <see cref="CMapCodec.Code.Cid"/>, never <see cref="CMapCodec.Code.Value"/>.
        /// </remarks>
        public override bool HasExplicitWidth(int cid) => _cidWidths.ContainsKey(cid);

        /// <summary>
        /// Every CID <c>/W</c> gives an explicit width, in no particular order; every other CID takes
        /// <c>/DW</c>. Bounded by <see cref="PdfLimits.MaxCidWidthEntries"/>, like the parse.
        /// </summary>
        internal IEnumerable<int> ExplicitWidthCids => _cidWidths.Keys;

        /// <summary>True when the font's <c>/Encoding</c> CMap selects vertical writing (<c>/WMode 1</c>, §9.7.4.3).</summary>
        internal bool IsVerticalWritingMode => _codec != null && _codec.WMode == 1;

        // The vertical displacement's magnitude in em units: /DW2's second number, else Table 117's −1000.
        private double _defaultVerticalAdvance = 1.0;
        private CidVerticalMetrics.Index _verticalMetrics;

        /// <summary>
        /// The font's <c>/W2</c> metrics, read once when the font is made, or null when it has none —
        /// for a reader that needs the position vector too (the renderers' font cache), so one font's
        /// array is read once however many readers ask.
        /// </summary>
        internal CidVerticalMetrics.Index VerticalMetrics => _verticalMetrics;

        /// <summary>
        /// The downward displacement <c>w1</c> of <paramref name="cid"/> in vertical writing, as a
        /// positive magnitude in em units: its <c>/W2</c> entry's, else <c>/DW2</c>'s, else one em
        /// (§9.7.4.3, Table 117). The text extractor steps a vertical run by it, as the renderers do.
        /// </summary>
        internal double VerticalAdvanceEm(int cid)
        {
            if (_verticalMetrics != null && _verticalMetrics.TryFind(cid, out float w1y, out _, out _))
                return Math.Abs(w1y) / PdfUnits.GlyphSpaceUnitsPerEm;
            return _defaultVerticalAdvance;
        }

        /// <summary>
        /// Reason the most recent <c>/W</c> parse on <b>this thread</b> stopped early, or
        /// <c>null</c> when it ran to completion. Non-null means the width table is
        /// <b>partial</b>: the array asked for more entries than
        /// <see cref="PdfLimits.MaxCidWidthEntries"/> allows, so the CIDs past that point fall
        /// back to <c>/DW</c> and text through this font will be laid out at the default width.
        /// </summary>
        /// <remarks>
        /// <para>Cleared at the start of every <c>/W</c> parse, so a value read after constructing
        /// a font always belongs to that font and never to a previous document's (see the
        /// Diagnostics Pattern section of CLAUDE.md). Backed by a <c>[ThreadStatic]</c> field —
        /// read it on the thread that built the font.</para>
        /// <para>Returning a partial table rather than throwing mirrors
        /// <see cref="CMapParser.LastBudgetError"/>: <c>PdfFont.Create</c> builds this font with no
        /// <c>try</c>/<c>catch</c>, so a throw would abort font loading for the whole document over
        /// one malformed <c>/W</c>.</para>
        /// </remarks>
        internal static string LastWidthBudgetError => _lastWidthBudgetError;

        [System.ThreadStatic]
        private static string _lastWidthBudgetError;

        /// <summary>
        /// Parses the /W array from a CIDFont dictionary.
        /// Format 1: c [w1 w2 w3 ...] — individual widths for CIDs c, c+1, c+2, ...
        /// Format 2: c1 c2 w — same width w for the range [c1..c2]
        /// </summary>
        /// <remarks>
        /// Bounded by ONE <see cref="WorkBudget"/> charged per entry written, not per array
        /// element: the per-range 0x10000 clamp below bounds a single <c>c1 c2 w</c> triple, and
        /// the number of triples is bounded only by the array length, so the two multiply. That is
        /// the "per-loop caps do not compose" case <see cref="WorkBudget"/> exists for.
        ///
        /// <para><b>Elements resolve through <paramref name="resolve"/></b> (§7.3.10 covers array
        /// elements): the whole <c>/W</c> and each <c>c 9 0 R</c> run or referenced
        /// width inside it, never skipped as "not a number", which would drop every CID it covers to
        /// <c>/DW</c>. Sharing one referenced run between entries
        /// cannot escape the budget, which is charged per width written.</para>
        /// </remarks>
        /// <param name="w">The <c>/W</c> array.</param>
        /// <param name="widths">Receives CID → width.</param>
        /// <param name="resolve">Follows an indirect element; <see langword="null"/> reads elements as stored.</param>
        internal static void ParseWArray(PdfArray w, Dictionary<int, double> widths,
                                         Func<PdfObject, PdfObject> resolve = null)
        {
            _lastWidthBudgetError = null;
            var budget = new WorkBudget(PdfLimits.MaxCidWidthEntries, "CIDFont /W parse");

            try
            {
                ParseWArrayCore(w, widths, budget,
                                resolve == null ? (Func<PdfObject, PdfObject>)(o => o) : o => resolve(o) ?? o);
            }
            catch (WorkBudgetExceededException ex)
            {
                // Keep what was parsed and report it, rather than throwing into font construction.
                _lastWidthBudgetError = ex.Message;
            }
        }

        private static void ParseWArrayCore(PdfArray w, Dictionary<int, double> widths, WorkBudget budget,
                                            Func<PdfObject, PdfObject> resolve)
        {
            int i = 0;
            while (i < w.Count)
            {
                if (resolve(w[i]) is not PdfNumber firstNum) { i++; continue; }
                int c1 = firstNum.IntValue;

                if (i + 1 >= w.Count) break;
                var second = resolve(w[i + 1]);

                if (second is PdfArray arr)
                {
                    // Format 1: c [w1 w2 ...]
                    for (int j = 0; j < arr.Count; j++)
                    {
                        if (resolve(arr[j]) is PdfNumber wn)
                        {
                            budget.Tick();
                            widths[c1 + j] = wn.DoubleValue;
                        }
                    }
                    i += 2;
                }
                else if (second is PdfNumber c2Num && i + 2 < w.Count && resolve(w[i + 2]) is PdfNumber wNum)
                {
                    // Format 2: c1 c2 w
                    int c2 = c2Num.IntValue;
                    double width = wNum.DoubleValue;
                    // Clamp the span: a crafted /W entry like [0 2000000000 1000] would
                    // otherwise allocate ~2 billion dictionary entries and OOM.  A single
                    // CID range never legitimately exceeds the 0x10000 code-point window
                    // (mirrors the MaxRange clamp in CMapParser).
                    if (c2 > c1 + 0x10000) c2 = c1 + 0x10000;
                    for (int cid = c1; cid <= c2; cid++)
                    {
                        budget.Tick();
                        widths[cid] = width;
                    }
                    i += 3;
                }
                else { i++; }
            }
        }

        /// <summary>
        /// The advance width of a <b>CID</b> in glyph-space units, from <c>/W</c> and falling back
        /// to <c>/DW</c> (§9.7.4.3). <paramref name="cid"/> is not a character code — pass
        /// <see cref="CMapCodec.Code.Cid"/>.
        /// </summary>
        public override double GetWidth(int cid)
        {
            if (_cidWidths.TryGetValue(cid, out double w))
                return w;
            return _defaultWidth;
        }

        /// <inheritdoc/>
        public override IReadOnlyList<CMapCodec.Code> SplitCodes(byte[] bytes) => _codec.Decode(bytes);

        /// <inheritdoc/>
        public override int NextCode(byte[] bytes, int offset, out CMapCodec.Code code)
            => _codec.ReadCode(bytes, offset, out code);

        public override string Decode(byte[] bytes)
        {
            var sb = new StringBuilder();
            var codes = _codec.Decode(bytes);
            for (int i = 0; i < codes.Count; i++)
                AppendDecoded(sb, codes[i]);
            return sb.ToString();
        }

        /// <summary>Codes whose text this font remembers; past it a code is decoded every time.</summary>
        /// <remarks>
        /// A real composite font shows a few hundred to a few thousand distinct codes; a hostile one
        /// can show two billion, and a cache with no ceiling would hold a string for each. The
        /// ceiling bounds the memory, not the answer.
        /// </remarks>
        internal const int MaxCachedCodes = 4096;

        private Dictionary<long, string> _decodedByCode;

        /// <summary>Codes remembered so far — for tests.</summary>
        internal int CachedCodeCount
        {
            get { var cache = _decodedByCode; if (cache == null) return 0; lock (cache) return cache.Count; }
        }

        /// <summary>
        /// A code's text, remembered per code up to <see cref="MaxCachedCodes"/> (see
        /// <see cref="PdfFont.Decode(CMapCodec.Code)"/>). Keyed on the value <i>and</i> the length:
        /// a one-byte 0x41 and a two-byte 0x0041 are different codes under one codespace.
        /// </summary>
        public override string Decode(CMapCodec.Code code)
        {
            long key = ((long)code.ByteLength << 32) | (uint)code.Value;
            Dictionary<long, string> cache = _decodedByCode;
            if (cache != null)
            {
                lock (cache)
                    if (cache.TryGetValue(key, out string remembered)) return remembered;
            }

            var sb = new StringBuilder(2);
            AppendDecoded(sb, code);
            string text = sb.ToString();

            // The font may be shared by the threads of a batch render; the dictionary is guarded, and
            // a second thread creating a second dictionary loses only the first's entries.
            cache ??= _decodedByCode ??= new Dictionary<long, string>();
            lock (cache)
                if (cache.Count < MaxCachedCodes) cache[key] = text;
            return text;
        }

        private void AppendDecoded(StringBuilder sb, CMapCodec.Code code)
        {
            // /ToUnicode is keyed by the character CODE, not by the CID (§9.10.3). The two are
            // equal only under Identity-H; keying this by the CID silently changes extracted text
            // for every other encoding.
            if (_toUnicode != null && _toUnicode.TryGetValue(code.Value, out string uStr))
            {
                sb.Append(uStr);
                return;
            }

            // For standard Adobe CID orderings (Japan1, GB1, CNS1, Korea1), consult the collection's
            // registered Unicode map before falling back to identity. CID values from these
            // collections are NOT Unicode code points; identity-mapping them produces gibberish
            // (CID 0x094E → U+094E Devanagari instead of the kanji). The lookup takes the CID — the
            // one place in this method that does — and is the one the PostScript converter's
            // charpath asks too (CidCollectionText.TextOf).
            if (_cidOrdering != null && CidCollectionText.TextOf(_cidOrdering, code.Cid) is { } predefined)
            {
                sb.Append(predefined);
                return;
            }

            // Fall back: treat the CODE as a Unicode code point (identity mapping).
            // Correct for Identity-H/V fonts whose PDF generator used Unicode char codes
            // directly (a common pattern in modern producers' output) — which is a statement
            // about the code the producer wrote, not about the CID it resolves to, so this stays
            // on Value even though the branch above is on Cid.
            // Use <= 0xFFFF so the full BMP round-trips, U+FFFF included.
            if (code.Value > 0 && code.Value <= 0xFFFF)
                sb.Append((char)code.Value);
        }
    }
}
