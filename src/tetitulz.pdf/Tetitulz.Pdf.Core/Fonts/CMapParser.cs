// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Fonts
{
    internal sealed class CMapParser
    {
        /// <summary>
        /// Parses a ToUnicode CMap and returns the code→Unicode mapping.
        /// Also outputs <paramref name="maxCodeBytes"/>: 1 if all codespace ranges fit in a
        /// single byte, 2 otherwise (the usual codespacerange reading).
        /// </summary>
        public static Dictionary<int, string> Parse(byte[] cmapData, out int maxCodeBytes)
        {
            return Parse(cmapData, out maxCodeBytes, out _);
        }

        /// <summary>
        /// Name of the predefined CMap referenced via <c>usecmap</c>, if any.
        /// Recognised values: "Identity-H"/"Identity-V" (handled internally) and
        /// the Adobe predefined CMaps (Adobe-CNS1-UCS2, Adobe-GB1-UCS2,
        /// Adobe-Japan1-UCS2, Adobe-Korea1-UCS2, GB-EUC-H, …).  Callers can
        /// register a full CMap file via <see cref="RegisterPredefinedCMap"/>
        /// to provide proper CID→Unicode mapping for Asian CJK fonts.
        /// <para>
        /// Backed by a <c>[ThreadStatic]</c> field, so concurrent CMap parses on other
        /// threads do not overwrite it — read it on the thread that ran the parse
        /// (see the Diagnostics Pattern section of CLAUDE.md).
        /// </para>
        /// </summary>
        public static string LastUsecmapName
        {
            get => _lastUsecmapName;
            private set => _lastUsecmapName = value;
        }

        [System.ThreadStatic]
        private static string _lastUsecmapName;

        /// <summary>
        /// Reason the most recent <see cref="Parse(byte[])"/> on <b>this thread</b> stopped early,
        /// or <c>null</c> when it ran to completion. Non-null means the returned mapping is
        /// <b>partial</b>: the CMap declared more mappings than
        /// <see cref="PdfLimits.MaxCMapMappingWrites"/> allows, so the codes past that point are
        /// missing and text extraction through this font will show gaps.
        /// </summary>
        /// <remarks>
        /// <para>Cleared at the start of every outermost <c>Parse</c>, so a value read after a call
        /// always belongs to that call and never to a previous document's font (see the
        /// Diagnostics Pattern section of CLAUDE.md). Backed by a <c>[ThreadStatic]</c> field —
        /// read it on the thread that ran the parse.</para>
        /// <para>Returning a partial map rather than throwing is deliberate: <c>PdfFont.Create</c>
        /// feeds raw <c>/ToUnicode</c> bytes here with no <c>try</c>/<c>catch</c>, so a throw would
        /// abort font loading for the whole document over one malformed CMap.</para>
        /// </remarks>
        public static string LastBudgetError => _lastBudgetError;

        [System.ThreadStatic]
        private static string _lastBudgetError;

        // Both caches are process-wide and are written from two directions at once:
        // RegisterPredefinedCMap is public API a host calls at startup (and, for the CMap
        // plugin, 167 times in a row), while _parsedPredefined is filled lazily by
        // LookupPredefined during *text extraction* — which this library itself runs
        // concurrently (PdfPageRenderer uses Parallel.For). A plain Dictionary is not safe
        // for concurrent writes: two threads resizing it can lose entries, tear a bucket
        // chain, or spin forever. ConcurrentDictionary is the minimum correct storage here
        // and is available on netstandard2.0.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _predefinedCMaps =
            new System.Collections.Concurrent.ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<int, string>> _parsedPredefined =
            new System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<int, string>>(StringComparer.Ordinal);

        /// <summary>
        /// Returns true when a predefined CMap with the given name has been registered
        /// via <see cref="RegisterPredefinedCMap"/>.
        /// </summary>
        public static bool IsPredefinedCMapRegistered(string name)
            => !string.IsNullOrEmpty(name) && _predefinedCMaps.ContainsKey(name);

        /// <summary>
        /// Registers the binary contents of a predefined CMap (e.g. Adobe-Japan1-UCS2)
        /// so that streams using <c>usecmap</c> against that name will inherit its
        /// mappings.  CMap blobs are shipped by Adobe at
        /// https://github.com/adobe-type-tools/cmap-resources.  Caller is responsible
        /// for registering them once at startup; the library does not bundle them.
        /// </summary>
        public static void RegisterPredefinedCMap(string name, byte[] cmapBytes)
        {
            if (string.IsNullOrEmpty(name) || cmapBytes == null) return;
            _predefinedCMaps[name] = cmapBytes;
            _parsedPredefined.TryRemove(name, out _); // invalidate cached parse result
        }

        /// <summary>
        /// Looks up a CID value in a registered predefined CMap and returns the Unicode
        /// string it maps to, or <c>null</c> if the CMap is not registered or the CID has
        /// no mapping.  Results are cached after the first parse so subsequent calls are O(1).
        /// </summary>
        public static string LookupPredefined(string cmapName, int cid)
        {
            if (cmapName == null) return null;
            if (!_predefinedCMaps.TryGetValue(cmapName, out var bytes)) return null;

            if (!_parsedPredefined.TryGetValue(cmapName, out var mapping))
            {
                // Two threads may parse the same CMap concurrently; both produce an
                // equivalent mapping, so letting the last one win is harmless and avoids
                // holding a lock across the parse.
                mapping = Parse(bytes, out _, out _);
                _parsedPredefined[cmapName] = mapping;
            }

            return mapping.TryGetValue(cid, out string uStr) ? uStr : null;
        }

        // usecmap can chain (A → B → C …) or, with a hostile registration, cycle
        // (A → B → A) or self-reference (A → A).  RegisterPredefinedCMap is public and
        // takes arbitrary bytes, so an attacker can craft a self-referential CMap that
        // would StackOverflow.  Bound the resolution with a visited-set (cycle guard)
        // plus a hard depth cap (mirrors the Form-XObject guard in PdfTextExtractor).
        private const int MaxUsecmapDepth = 16;

        /// <summary>
        /// Characters the section scans (<see cref="SectionTokens"/>, <see cref="TryReadHexToken"/>)
        /// examined looking for a hex string's brackets since the outermost <c>Parse</c> began: the evidence that each search stops at its section's end, so a CMap of unclosed
        /// hex strings costs its length and not its length times its sections.
        /// </summary>
        internal static long LastScanCharactersExamined => _scanCharacters;

        [ThreadStatic] private static long _scanCharacters;

        /// <summary>
        /// The first <paramref name="c"/> in <paramref name="text"/> from <paramref name="start"/>
        /// up to <paramref name="limit"/> (exclusive), or -1: a bracket's search never leaves its
        /// section, since a bracket found past it is discarded anyway.
        /// </summary>
        private static int IndexWithin(string text, char c, int start, int limit)
        {
            if (start >= limit) return -1;
            int found = text.IndexOf(c, start, limit - start);
            _scanCharacters += (found < 0 ? limit : found + 1) - start;
            return found;
        }

        /// <summary>
        /// Parses a ToUnicode CMap with full codespace range extraction for variable-length decoding.
        /// </summary>
        public static Dictionary<int, string> Parse(byte[] cmapData, out int maxCodeBytes,
            out List<(byte[] low, byte[] high)> codespaceRanges)
        {
            return Parse(cmapData, out maxCodeBytes, out codespaceRanges,
                depth: 0, visited: null, budget: null);
        }

        private static Dictionary<int, string> Parse(byte[] cmapData, out int maxCodeBytes,
            out List<(byte[] low, byte[] high)> codespaceRanges, int depth, HashSet<string> visited,
            WorkBudget budget)
        {
            // Cleared at the start of the outermost parse only. The normal path assigns it
            // unconditionally, but the empty-input early return below happens first and would
            // leave the PREVIOUS CMap's usecmap name in place. Guarded on depth == 0 so a nested
            // usecmap parse still reports the name it resolved.
            if (depth == 0)
            {
                LastUsecmapName = null;
                _lastBudgetError = null;
                _scanCharacters = 0;
                // ONE budget for the whole parse, chained usecmap targets included: the per-line
                // 0x10000 clamp bounds a single range and the line count multiplies it. Created
                // here rather than per helper so the four range parsers cannot each get a fresh
                // allowance (the "per-loop caps do not compose" case WorkBudget exists for).
                budget = new WorkBudget(PdfLimits.MaxCMapMappingWrites, "CMap parse");
            }

            var mapping = new Dictionary<int, string>();
            codespaceRanges = new List<(byte[] low, byte[] high)>();
            maxCodeBytes = 2; // safe default for composite fonts
            if (cmapData == null || cmapData.Length == 0) return mapping;
            string text = Encoding.ASCII.GetString(cmapData);

            // Charged to the same budget as the four range parsers below, and guarded
            // SEPARATELY from them because its refusal has to discard more.
            //
            // This was the one section parser of five that took no budget at all. Its outer loop
            // over `begincodespacerange` sections is unbounded, its inner loop over the lines of a
            // section is unbounded, and every accepted line allocates two byte[] and appends to a
            // list this method RETURNS — so the cost is retained, not transient. The four parsers
            // below all tick.
            try
            {
                maxCodeBytes = ParseCodespaceRanges(text, codespaceRanges, budget);
            }
            catch (WorkBudgetExceededException ex)
            {
                // A TRUNCATED codespace list is worse than no list: the ranges decide how many
                // bytes each code consumes (§9.7.6.2), so a partial set silently mis-splits every
                // subsequent code rather than merely losing entries. The other four parsers can
                // hand back what they mapped because a missing MAPPING is a gap; a missing RANGE
                // changes the meaning of the bytes around it. So discard and fall back to the
                // documented safe default instead of returning a prefix.
                codespaceRanges.Clear();
                maxCodeBytes = 2;
                _lastBudgetError = ex.Message;
                return mapping;
            }

            // Encoding CMaps (Adobe CMap resources) use begincidchar/begincidrange with the format
            // <unicode> cid. They are read in reverse (cid→unicode), so that registering a file like
            // UniJIS-UTF16-H gives CID-to-Unicode lookup, into a map of their own: a bfchar or bfrange
            // entry, the producer's word on a code, is never displaced by one read backwards.
            var reversed = new Dictionary<int, string>();
            try
            {
                ParseBfChar(text, mapping, budget);
                ParseBfRange(text, mapping, budget);
                ParseCidCharReverse(text, reversed, budget);
                ParseCidRangeReverse(text, reversed, budget);
            }
            catch (WorkBudgetExceededException ex)
            {
                // Return what was mapped rather than throwing: PdfFont.Create feeds raw /ToUnicode
                // bytes straight in with no try/catch, so a throw here would abort font loading for
                // the whole document. The refusal is reported instead — a caller that sees gaps in
                // extracted text can tell "this CMap was truncated" from "this CMap had no entry".
                _lastBudgetError = ex.Message;
                MergeReversed(mapping, reversed);
                return mapping;
            }
            MergeReversed(mapping, reversed);

            // Parse 'usecmap' directive: /CMapName usecmap
            string usecmapName = ParseUsecmapName(text);
            LastUsecmapName = usecmapName;
            if (usecmapName != null)
            {
                if (usecmapName.StartsWith("Identity", StringComparison.OrdinalIgnoreCase))
                {
                    maxCodeBytes = 2;
                }
                else
                {
                    // Walk the usecmap chain.  If the predefined CMap has been registered
                    // we recursively parse it and merge its mappings + codespace ranges.
                    // Otherwise we apply an ASCII identity fallback so the most common
                    // characters (digits, English latin) at least round-trip.
                    //
                    // Cycle/depth guard: only recurse if we have NOT already visited this
                    // target name on the current chain and the depth cap is not exceeded.
                    // This stops self-referential / cyclic registrations from StackOverflowing.
                    var branchVisited = visited ?? new HashSet<string>(StringComparer.Ordinal);
                    if (depth < MaxUsecmapDepth
                        && branchVisited.Add(usecmapName)
                        && _predefinedCMaps.TryGetValue(usecmapName, out var refBytes))
                    {
                        var refMap = Parse(refBytes, out int refMaxBytes, out var refRanges,
                            depth + 1, branchVisited, budget);
                        if (refMaxBytes > maxCodeBytes) maxCodeBytes = refMaxBytes;
                        foreach (var kvp in refMap)
                            if (!mapping.ContainsKey(kvp.Key)) mapping[kvp.Key] = kvp.Value;
                        if (refRanges != null)
                            foreach (var r in refRanges) codespaceRanges.Add(r);
                    }
                    else if (IsKnownAdobePredefined(usecmapName))
                    {
                        // Apply ASCII identity for codes 0x20..0x7E (printable ASCII).
                        // Adobe predefined CMaps map these positions to U+0020..U+007E.
                        for (int c = 0x20; c <= 0x7E; c++)
                            if (!mapping.ContainsKey(c)) mapping[c] = ((char)c).ToString();
                        if (maxCodeBytes < 2) maxCodeBytes = 2;
                    }
                }
            }

            return mapping;
        }

        private static bool IsKnownAdobePredefined(string name)
        {
            return name.StartsWith("Adobe-", StringComparison.Ordinal) ||
                   name.StartsWith("GB-", StringComparison.Ordinal) ||
                   name.StartsWith("GBK-", StringComparison.Ordinal) ||
                   name.StartsWith("GBpc-", StringComparison.Ordinal) ||
                   name.StartsWith("UniGB-", StringComparison.Ordinal) ||
                   name.StartsWith("UniCNS-", StringComparison.Ordinal) ||
                   name.StartsWith("UniJIS-", StringComparison.Ordinal) ||
                   name.StartsWith("UniKS-", StringComparison.Ordinal) ||
                   name.StartsWith("ETen-", StringComparison.Ordinal) ||
                   name.StartsWith("HKscs-", StringComparison.Ordinal) ||
                   name.StartsWith("83pv-", StringComparison.Ordinal) ||
                   name.StartsWith("90pv-", StringComparison.Ordinal) ||
                   name.StartsWith("90ms-", StringComparison.Ordinal) ||
                   name.StartsWith("EUC-", StringComparison.Ordinal) ||
                   name.StartsWith("KS", StringComparison.Ordinal) ||
                   name.StartsWith("V", StringComparison.Ordinal) && name.Contains("-V") ||
                   name.StartsWith("H", StringComparison.Ordinal) && name.Contains("-H");
        }

        /// <summary>Overload that discards the codespace byte-width result.</summary>
        public static Dictionary<int, string> Parse(byte[] cmapData)
        {
            return Parse(cmapData, out _, out _);
        }

        /// <summary>
        /// Extracts the referenced CMap name from a 'usecmap' directive, or null if none.
        /// Format: /CMapName usecmap
        /// </summary>
        private static string ParseUsecmapName(string text)
        {
            int idx = text.IndexOf("usecmap", StringComparison.Ordinal);
            if (idx < 0) return null;
            // Walk backwards past whitespace to find the /Name token
            int end = idx - 1;
            while (end >= 0 && (text[end] == ' ' || text[end] == '\t' || text[end] == '\r' || text[end] == '\n'))
                end--;
            if (end < 0) return null;
            int nameEnd = end + 1;
            // Walk further back to find the start of the name (after '/')
            int nameStart = end;
            while (nameStart > 0 && text[nameStart - 1] != '/')
                nameStart--;
            // The token immediately before the name MUST be a '/' for it to be a valid
            // PDF name operand.  Reject anything else (e.g. a bare "Foo usecmap" with no
            // slash, where the backward walk would otherwise fall through to index 0 and
            // capture a non-name token).
            if (nameStart == 0 || text[nameStart - 1] != '/') return null;
            if (nameEnd <= nameStart) return null;
            return text.Substring(nameStart, nameEnd - nameStart);
        }

        /// <summary>
        /// Parses codespace ranges and returns max byte width.
        /// Also populates <paramref name="ranges"/> with the actual (low, high) byte pairs
        /// for variable-length code decoding.
        /// </summary>
        private static int ParseCodespaceRanges(
            string text, List<(byte[] low, byte[] high)> ranges, WorkBudget budget)
        {
            int max = 1;
            string low = null;
            foreach (var (kind, token) in SectionTokens(text, "begincodespacerange", "endcodespacerange"))
            {
                if (kind != SectionTokenKind.Hex) { low = null; continue; }
                if (low == null) { low = token; continue; }

                // Charged before the two allocations below, not after, so a hostile file
                // cannot buy an extra pair of arrays past the ceiling on every iteration.
                budget?.Tick();

                int byteLen = (low.Length + 1) / 2;
                if (byteLen > max) max = byteLen;
                int byteLen2 = (token.Length + 1) / 2;
                if (byteLen2 > max) max = byteLen2;

                // Extract actual byte arrays for the codespace range bounds.
                byte[] lowBytes = HexStringToBytes(low);
                byte[] highBytes = HexStringToBytes(token);
                if (ranges != null && lowBytes.Length == highBytes.Length)
                    ranges.Add((lowBytes, highBytes));
                low = null;
            }
            return max > 1 ? 2 : 1;
        }

        /// <summary>What a token inside a CMap section is.</summary>
        internal enum SectionTokenKind
        {
            /// <summary>A hex string. The token's text is its digits, without the angle brackets.</summary>
            Hex,
            /// <summary>A run of decimal digits.</summary>
            Integer,
            /// <summary>Anything else: a name, an operator, a bracket.</summary>
            Other,
        }

        /// <summary>
        /// Yields the tokens between every <paramref name="beginToken"/> and the
        /// <paramref name="endToken"/> after it, section after section.
        /// </summary>
        /// <remarks>
        /// <para><b>A CMap is PostScript, so a line break is white space like any other.</b> Read a
        /// line at a time, one entry each, a line holding several entries would keep the first
        /// entry's hex strings and the <i>last</i> entry's number, so
        /// <c>&lt;0041&gt; 34 &lt;0042&gt; 35</c> would map 0x41 to CID 35 and drop 0x42. Independent
        /// readers read a section as tokens.</para>
        ///
        /// <para>A hex string's white space is dropped, as it is in any PDF or PostScript hex
        /// string, so a token's length is its digit count. A hex string with no closing bracket
        /// before the section ends stops that section: nothing after it can be paired reliably.</para>
        /// </remarks>
        internal static IEnumerable<(SectionTokenKind Kind, string Text)> SectionTokens(
            string text, string beginToken, string endToken)
        {
            int pos = 0;
            while (true)
            {
                int start = text.IndexOf(beginToken, pos, StringComparison.Ordinal);
                if (start < 0) yield break;
                int end = text.IndexOf(endToken, start, StringComparison.Ordinal);
                if (end < 0) yield break;

                int i = start + beginToken.Length;
                while (i < end)
                {
                    char c = text[i];
                    if (char.IsWhiteSpace(c)) { i++; continue; }

                    if (c == '<')
                    {
                        int close = IndexWithin(text, '>', i + 1, end);
                        if (close < 0) break;
                        yield return (SectionTokenKind.Hex, HexDigits(text, i + 1, close));
                        i = close + 1;
                        continue;
                    }

                    int j = i + 1;
                    while (j < end && !char.IsWhiteSpace(text[j]) && text[j] != '<') j++;
                    string word = text.Substring(i, j - i);
                    yield return (IsDecimal(word) ? SectionTokenKind.Integer : SectionTokenKind.Other, word);
                    i = j;
                }

                pos = end + endToken.Length;
            }
        }

        /// <summary>A section token's integer value, or false when it does not fit an int.</summary>
        internal static bool TryParseSectionInteger(string token, out int value)
            => int.TryParse(token, System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out value);

        private static bool IsDecimal(string word)
        {
            foreach (char c in word)
                if (c < '0' || c > '9') return false;
            return word.Length > 0;
        }

        /// <summary>The characters of <paramref name="text"/> from <paramref name="start"/> up to
        /// <paramref name="end"/>, with white space removed.</summary>
        private static string HexDigits(string text, int start, int end)
        {
            int i = start;
            while (i < end && !char.IsWhiteSpace(text[i])) i++;
            if (i == end) return text.Substring(start, end - start);

            var sb = new StringBuilder(end - start);
            for (int k = start; k < end; k++)
                if (!char.IsWhiteSpace(text[k])) sb.Append(text[k]);
            return sb.ToString();
        }

        private static byte[] HexStringToBytes(string hex)
        {
            // Pad to even length
            if (hex.Length % 2 != 0) hex = "0" + hex;
            byte[] result = new byte[hex.Length / 2];
            for (int i = 0; i < result.Length; i++)
                result[i] = (byte)ParseHexInt(hex.Substring(i * 2, 2));
            return result;
        }

        private static void ParseBfChar(string text, Dictionary<int, string> mapping, WorkBudget budget)
        {
            // Format: <XXXX> <YYYY> or <XXXX> <YYYYYYYY>, any number to a line.
            string src = null;
            foreach (var (kind, token) in SectionTokens(text, "beginbfchar", "endbfchar"))
            {
                if (kind != SectionTokenKind.Hex) { src = null; continue; }
                if (src == null) { src = token; continue; }

                budget?.Tick();
                int srcCode = ParseHexInt(src);
                string destStr = HexToUnicodeString(token);
                if (destStr.Length > 0 || token.Length == 0)
                    mapping[srcCode] = destStr;          // <> maps the code to no text, and says so
                src = null;
            }
        }

        private static void ParseBfRange(string text, Dictionary<int, string> mapping, WorkBudget budget)
        {
            int pos = 0;
            while (true)
            {
                int start = text.IndexOf("beginbfrange", pos, StringComparison.Ordinal);
                if (start < 0) break;
                int end = text.IndexOf("endbfrange", start, StringComparison.Ordinal);
                if (end < 0) break;

                ParseBfRangeEntries(text, start + 12, end, mapping, budget);

                pos = end + 10;
            }
        }

        /// <summary>
        /// Walks bfrange entries by token position across the whole section, rather than
        /// splitting it into lines first. See GAPS.md §21.
        ///
        /// <para>
        /// <b>The bug this replaces.</b> A real-world CMap generator (confirmed: Cal/Leumi-Visa
        /// statement PDFs) writes the array form one element per line —
        /// <c>&lt;0000&gt; &lt;00ff&gt; [</c> on its own line, then dozens of <c>&lt;XXXX&gt;</c>
        /// lines, then a closing <c>]</c> possibly many lines later. The previous implementation
        /// split the section into lines first and handed each line to the array handler
        /// independently: the line containing the opening bracket has no <c>]</c> on it, so the
        /// handler's <c>line.IndexOf(']')</c> returned -1 and it returned immediately, mapping
        /// nothing for the entire range. Every code point in that range then had no ToUnicode
        /// entry, and text/table extraction fell back to raw character codes — which is why a
        /// well-formed Hebrew ToUnicode CMap produced garbled output instead of Hebrew text.
        /// Scanning by character position instead of by line finds the closing bracket wherever
        /// it actually is.
        /// </para>
        /// </summary>
        private static void ParseBfRangeEntries(string text, int sectionStart, int sectionEnd,
            Dictionary<int, string> mapping, WorkBudget budget)
        {
            int pos = sectionStart;
            while (pos < sectionEnd)
            {
                if (!TryReadHexToken(text, ref pos, sectionEnd, out string tok1)) break;
                if (!TryReadHexToken(text, ref pos, sectionEnd, out string tok2)) break;

                int startCode = ParseHexInt(tok1);
                int endCode = ParseHexInt(tok2);

                int scan = pos;
                while (scan < sectionEnd && char.IsWhiteSpace(text[scan])) scan++;

                // Format: <XXXX> <YYYY> <ZZZZ>
                // Or: <XXXX> <YYYY> [<AAAA> <BBBB> ... ]  (array elements may each be on their own line)
                if (scan < sectionEnd && text[scan] == '[')
                {
                    // Searched within the section only: an unbounded IndexOf would scan to the end
                    // of the CMap for every `[` that lacks its `]`, and the clamp below throw the
                    // answer away — quadratic in sections × CMap size. A missing
                    // closing bracket is malformed input, not a reason to read past this section.
                    int arrEnd = text.IndexOf(']', scan + 1, sectionEnd - scan - 1);
                    if (arrEnd < 0) arrEnd = sectionEnd;

                    var arrParts = ExtractHexParts(text.Substring(scan + 1, arrEnd - scan - 1));
                    for (int i = 0; i < arrParts.Count && startCode + i <= endCode; i++)
                    {
                        budget?.Tick();
                        string destStr = HexToUnicodeString(arrParts[i]);
                        if (destStr.Length > 0) mapping[startCode + i] = destStr;
                    }

                    pos = arrEnd + 1;
                    continue;
                }

                if (!TryReadHexToken(text, ref scan, sectionEnd, out string tok3))
                {
                    // Neither an array nor a third token follows — malformed entry. The remainder
                    // of this section can't be interpreted reliably, so stop here rather than
                    // reinterpret unrelated bytes as a new range (pos already < sectionEnd, so an
                    // unconditional retry from here would otherwise be guaranteed forward progress
                    // anyway, but not a meaningful one).
                    break;
                }
                pos = scan;

                // Clamp pathological ranges — CMaps in the wild never cover more than a single
                // 16-bit code point space; cap at 65536 entries per range to stop adversarial
                // CMaps from billions-of-entries DoS.
                // The span MUST be computed in long, after validating startCode.
                // `endCode - startCode` in int overflows for a crafted range such as
                // `<FFFFFFFF> <7FFFFFFF>`: ParseHexInt returns -1 and int.MaxValue,
                // their difference wraps to int.MinValue, the clamp below never fires,
                // and the loop then runs ~2.1 billion iterations building dictionary
                // entries. PdfFont feeds raw /ToUnicode bytes here with no try/catch
                // and no time budget, so one crafted font hangs the calling thread.
                const int MaxRangeLength = 0x10000;
                if (startCode < 0 || startCode > 0x10FFFF) continue;
                long span = (long)endCode - startCode;
                if (span < 0) continue;
                if (span > MaxRangeLength) span = MaxRangeLength;

                if (tok3.Length > 4)
                {
                    // Multi-char destination (e.g. ligature): only the last 16-bit
                    // unit increments; the leading units are constant (PDF spec §9.7.6.4).
                    string prefix = tok3.Substring(0, tok3.Length - 4);
                    int lastStart = ParseHexInt(tok3.Substring(tok3.Length - 4));
                    // Driven by an explicit counter so no int loop variable can wrap.
                    for (long k = 0; k <= span; k++)
                    {
                        budget?.Tick();
                        int code = (int)(startCode + k);
                        mapping[code] = HexToUnicodeString(
                            prefix + (lastStart + (int)k).ToString("X4"));
                    }
                }
                else
                {
                    int destStart = ParseHexInt(tok3);
                    for (long k = 0; k <= span; k++)
                    {
                        budget?.Tick();
                        int code = (int)(startCode + k);
                        int destCode = destStart + (code - startCode);
                        // Reject invalid code points: out-of-range or surrogate halves
                        // (0xD800–0xDFFF are illegal in UTF-32; char.ConvertFromUtf32 throws).
                        if (destCode < 0 || destCode > 0x10FFFF ||
                            (destCode >= 0xD800 && destCode <= 0xDFFF)) continue;
                        mapping[code] = char.ConvertFromUtf32(destCode);
                    }
                }
            }
        }

        /// <summary>
        /// Reads the next <c>&lt;HEX&gt;</c> token at or after <paramref name="pos"/>, bounded by
        /// <paramref name="limit"/> (exclusive). Advances <paramref name="pos"/> past the token's
        /// closing <c>&gt;</c> on success; leaves it unchanged on failure.
        /// </summary>
        private static bool TryReadHexToken(string text, ref int pos, int limit, out string token)
        {
            token = null;
            int lt = IndexWithin(text, '<', pos, limit);
            if (lt < 0) return false;
            int gt = IndexWithin(text, '>', lt + 1, limit);
            if (gt < 0) return false;
            token = HexDigits(text, lt + 1, gt);
            pos = gt + 1;
            return true;
        }

        /// <summary>
        /// Parses <c>begincidchar</c>/<c>endcidchar</c> blocks.
        /// Format: <c>&lt;unicode_hex&gt; cid_number</c>.
        /// Adds reversed entries (cid_number → unicode) to <paramref name="mapping"/>.
        /// Allows registering Adobe CMap resource files (e.g. UniJIS-UTF16-H) directly
        /// and getting correct CID→Unicode lookup from <see cref="LookupPredefined"/>.
        /// </summary>
        private static void ParseCidCharReverse(string text, Dictionary<int, string> mapping,
            WorkBudget budget)
        {
            string unicode = null;
            foreach (var (kind, token) in SectionTokens(text, "begincidchar", "endcidchar"))
            {
                if (kind == SectionTokenKind.Hex) { unicode = token; continue; }

                if (kind == SectionTokenKind.Integer && unicode != null
                    && TryParseSectionInteger(token, out int cid))
                {
                    budget?.Tick();
                    string uStr = HexToUnicodeString(unicode);
                    if (uStr.Length > 0) AddReversed(mapping, cid, uStr);
                }
                unicode = null;
            }
        }

        /// <summary>
        /// Parses <c>begincidrange</c>/<c>endcidrange</c> blocks.
        /// Format: <c>&lt;u_start&gt; &lt;u_end&gt; cid_start</c>.
        /// Adds reversed entries: for each offset i, adds (cid_start+i → u_start+i).
        /// </summary>
        private static void ParseCidRangeReverse(string text, Dictionary<int, string> mapping,
            WorkBudget budget)
        {
            // The two hex strings nearest before each integer are the entry's bounds.
            string low = null, high = null;
            foreach (var (kind, token) in SectionTokens(text, "begincidrange", "endcidrange"))
            {
                if (kind == SectionTokenKind.Hex)
                {
                    if (low == null) low = token;
                    else if (high == null) high = token;
                    else { low = high; high = token; }
                    continue;
                }

                if (kind == SectionTokenKind.Integer && high != null
                    && TryParseSectionInteger(token, out int cidStart))
                    ReverseCidRange(mapping, ParseHexInt(low), ParseHexInt(high), cidStart, budget);
                low = high = null;
            }
        }

        private static void ReverseCidRange(Dictionary<int, string> mapping, int uStart, int uEnd,
            int cidStart, WorkBudget budget)
        {
            // As in ParseBfRange: compute the span in long after validating uStart, or
            // `uEnd - uStart` wraps negative on a crafted range and the clamp never fires
            // (`uStart + i` in an int loop condition could overflow too).
            const int MaxRange = 0x10000;
            if (uStart < 0 || uStart > 0x10FFFF) return;
            long uSpan = (long)uEnd - uStart;
            if (uSpan < 0) return;
            if (uSpan > MaxRange) uSpan = MaxRange;

            for (long i = 0; i <= uSpan; i++)
            {
                budget?.Tick();
                int cid = cidStart + (int)i;
                int cp  = uStart + (int)i;
                if (cp < 0 || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF)) continue;
                AddReversed(mapping, cid, char.ConvertFromUtf32(cp));
            }
        }

        /// <summary>
        /// Records <paramref name="text"/> as the character <paramref name="cid"/> stands for, read off
        /// an encoding CMap backwards. Several characters may encode one CID — U+2F00, the Kangxi
        /// radical, and U+4E00, the ideograph, are both CID 1200 in Adobe-Japan1 — and the first met
        /// is kept unless a later one is its compatibility decomposition (NFKC), which is the character
        /// the collection's own Unicode map gives the CID; the radical comes first in code order, and
        /// keeping it would extract every such ideograph as a radical.
        /// </summary>
        private static void AddReversed(Dictionary<int, string> mapping, int cid, string text)
        {
            if (!mapping.TryGetValue(cid, out string known)) { mapping[cid] = text; return; }
            if (known == text) return;
            if (!IsNormalForm(known) && IsNormalForm(text) && string.Equals(Compatible(known), text, StringComparison.Ordinal))
                mapping[cid] = text;
        }

        /// <summary>The reversed entries, each where no bfchar or bfrange entry already names the CID.</summary>
        private static void MergeReversed(Dictionary<int, string> mapping, Dictionary<int, string> reversed)
        {
            foreach (var kv in reversed)
                if (!mapping.ContainsKey(kv.Key)) mapping[kv.Key] = kv.Value;
        }

        private static bool IsNormalForm(string s)
        {
            try { return s.IsNormalized(NormalizationForm.FormKC); }
            catch (ArgumentException) { return false; }   // a lone surrogate
        }

        private static string Compatible(string s)
        {
            try { return s.Normalize(NormalizationForm.FormKC); }
            catch (ArgumentException) { return s; }
        }

        /// <summary>Hex→int with the same overflow/garbage tolerance the CMap parser uses.</summary>
        internal static int ParseHexIntShared(string hex) => ParseHexInt(hex);

        /// <summary>Hex→bytes, preserving the token's declared byte width.</summary>
        internal static byte[] HexStringToBytesShared(string hex) => HexStringToBytes(hex);

        /// <summary>Raw bytes of a registered predefined CMap, or null.</summary>
        internal static byte[] GetPredefinedBytes(string name)
            => name != null && _predefinedCMaps.TryGetValue(name, out var b) ? b : null;

        private static List<string> ExtractHexParts(string text)
        {
            var parts = new List<string>();
            int i = 0;
            while (i < text.Length)
            {
                if (text[i] == '<')
                {
                    int close = text.IndexOf('>', i + 1);
                    if (close > i)
                    {
                        parts.Add(HexDigits(text, i + 1, close));
                        i = close + 1;
                    }
                    else break;
                }
                else
                {
                    i++;
                }
            }
            return parts;
        }

        private static int ParseHexInt(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return 0;
            // Total parse: a ToUnicode/CMap is untrusted and CMapParser.Parse has
            // no internal catch, so a token longer than 8 nibbles (int overflow)
            // or containing non-hex (FormatException) would otherwise crash font
            // loading / text extraction. >8 chars: keep the low 32 bits.
            if (hex.Length > 8) hex = hex.Substring(hex.Length - 8);
            return int.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out int v) ? v : 0;
        }

        private static string HexToUnicodeString(string hex)
        {
            // An empty destination, <>, maps the code to no text. It parsed as
            // hex 0 — U+0000, a NUL in the extracted text and, to PDF/UA's and PDF/A's value rule, a
            // forbidden value the producer never wrote (test 6-2-2-t02-pass-e).
            if (hex.Length == 0) return string.Empty;
            if (hex.Length <= 4)
            {
                int codePoint = ParseHexInt(hex);
                // Surrogate halves and out-of-range values are invalid in UTF-32.
                // Return empty rather than crashing or emitting U+FFFD diamond.
                if (codePoint < 0 || codePoint > 0x10FFFF ||
                    (codePoint >= 0xD800 && codePoint <= 0xDFFF))
                    return string.Empty;
                return char.ConvertFromUtf32(codePoint);
            }

            // Multi-byte: pairs of 2-byte (UTF-16) code units.
            // Handle surrogate pairs: if a high surrogate (D800-DBFF) is followed by
            // a low surrogate (DC00-DFFF), combine into a single code point.
            // The usual bfrange / bfchar reading.
            //
            // Loop bound is `idx + 4 <= hex.Length` (inclusive) so the final
            // 4-char unit is consumed.  The previous `< hex.Length` left a
            // trailing unit unprocessed when the hex string had an even unit
            // count, dropping characters such as the U+00AB in `<D800DC00AB>`.
            //
            // A trailing 1-3 nibble remainder (e.g. "004141" → "0041" + "41") was being
            // dropped entirely.  Left-pad the remainder to a full 4-nibble unit so its
            // numeric value is preserved ("41" → "0041"), mirroring HexStringToBytes,
            // which left-pads an odd 2-nibble case.
            int rem = hex.Length % 4;
            if (rem != 0)
                hex = hex.Substring(0, hex.Length - rem)
                      + new string('0', 4 - rem) + hex.Substring(hex.Length - rem);
            var sb = new StringBuilder();
            int idx = 0;
            while (idx + 4 <= hex.Length)
            {
                int unit = ParseHexInt(hex.Substring(idx, 4));
                idx += 4;

                // Check for UTF-16 surrogate pair
                if (unit >= 0xD800 && unit <= 0xDBFF && idx + 4 <= hex.Length)
                {
                    int low = ParseHexInt(hex.Substring(idx, 4));
                    if (low >= 0xDC00 && low <= 0xDFFF)
                    {
                        // Valid surrogate pair: combine into supplementary code point
                        int cp = 0x10000 + ((unit - 0xD800) << 10) + (low - 0xDC00);
                        sb.Append(char.ConvertFromUtf32(cp));
                        idx += 4;
                        continue;
                    }
                }

                // Unpaired surrogate or BMP code point — emit as-is.  Avoid
                // ConvertFromUtf32 for unpaired surrogates (it throws); cast
                // through char which produces an isolated UTF-16 unit.
                if (unit >= 0xD800 && unit <= 0xDFFF)
                    sb.Append((char)unit);
                else
                    sb.Append(char.ConvertFromUtf32(unit));
            }
            return sb.ToString();
        }
    }
}
