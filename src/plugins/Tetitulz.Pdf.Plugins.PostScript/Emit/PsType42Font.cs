// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;

namespace Tetitulz.Pdf.Plugins.PostScript.Emit
{
    /// <summary>
    /// A Type 42 font (Adobe Technical Note 5012): a TrueType program carried in a PostScript font
    /// dictionary as the strings of <c>/sfnts</c>, its glyphs reached by name through
    /// <c>/CharStrings</c> (name to glyph index).
    ///
    /// <para><b>The program is joined once, at <c>definefont</c>; its glyphs are taken as they are
    /// used.</b> A driver that downloads glyphs incrementally — the Windows PostScript driver, Wine's —
    /// writes <c>/sfnts</c> without <c>glyf</c>, <c>loca</c> or <c>hmtx</c> and adds each glyph later
    /// as its raw <c>glyf</c> bytes under <c>/GlyphDirectory</c> (glyph index to string) and its
    /// <c>[lsb wx]</c> under <c>/Metrics</c> (name to metrics), often inside a page's
    /// <c>save</c>/<c>restore</c>. So nothing is read from those dictionaries ahead of time: each glyph
    /// is copied out the first time something shows, measures or outlines it, and a <c>restore</c>
    /// that rolls the dictionary back cannot take it from the program. The program a PDF reader gets
    /// is assembled from what was taken when the resources are built (<see cref="Program"/>).</para>
    ///
    /// <para><b>An odd-length <c>/sfnts</c> string's last byte is padding</b> and is dropped — §4.2 of
    /// the note; another reader drops it when it is zero, which a conforming producer's always is.</para>
    ///
    /// <para><b>A Type 2 CIDFont's <c>/MetricsCount</c></b> (TN 5012 §5.7: 0, 2 or 4) puts that many
    /// 16-bit values, high byte first, before each <c>/GlyphDirectory</c> string's glyph description:
    /// at 2 the horizontal advance and left side bearing, at 4 the vertical advance and top side bearing
    /// first. The prefix is taken off; the horizontal pair is the glyph's advance after <c>/Metrics</c>
    /// and its <c>hmtx</c> record, and the vertical pair is not read (vertical text keeps its metric
    /// boxes).</para>
    ///
    /// <para><b>Refused by name rather than guessed at:</b> a <c>/MetricsCount</c> other than 0, 2 or 4,
    /// or a non-zero one on a Type 42 font that is no Type 2 CIDFont (the note defines it for those
    /// alone), and a program without <c>head</c>, <c>hhea</c> or
    /// <c>maxp</c>, or with neither <c>glyf</c> outlines nor a <c>/GlyphDirectory</c>. The font then
    /// falls back to the standard face its name maps to, reported.</para>
    /// </summary>
    internal sealed class PsType42Font : IPsGlyphIndexedProgram
    {
        /// <summary>A TrueType program: a <c>CIDFontType2</c> with a <c>/FontFile2</c>.</summary>
        public bool IsCff => false;

        private const string GlyphDirectoryKey = "GlyphDirectory";

        private readonly Dictionary<string, byte[]> _tables;
        private readonly bool _hasOutlines;      // the program's own glyf and loca
        private readonly bool _hasHmtx;
        private readonly int _numberOfHMetrics;

        // A Type 2 CIDFont's /MetricsCount (TN 5012 §5.7): 0, or the 16-bit values — 2 horizontal, 4
        // vertical then horizontal — before each /GlyphDirectory string's glyph description.
        private int _metricsCount;

        // What was used, taken as it was used.
        private readonly SortedDictionary<int, byte[]> _glyphs = new();
        private readonly Dictionary<int, (int Advance, short SideBearing)> _prefixMetrics = new();
        private readonly Dictionary<int, double> _advances = new();
        private readonly Dictionary<int, string> _texts = new();

        // A Type 2 CIDFont's glyph index table (PLRM Table 5.17): CIDCount entries of GDBytes bytes,
        // high-order byte first. Null for a Type 42 base font, whose glyphs are reached by name.
        private byte[] _cidMap;
        private int _gdBytes;
        private int _cidCount;

        private PsType42Font(Dictionary<string, byte[]> tables, int unitsPerEm, int numGlyphs,
                             bool hasOutlines, bool hasHmtx, int numberOfHMetrics)
        {
            _tables = tables;
            UnitsPerEm = unitsPerEm;
            NumGlyphs = numGlyphs;
            _hasOutlines = hasOutlines;
            _hasHmtx = hasHmtx;
            _numberOfHMetrics = numberOfHMetrics;
        }

        /// <summary>The program's <c>head.unitsPerEm</c>.</summary>
        public int UnitsPerEm { get; }

        /// <summary>The program's <c>maxp.numGlyphs</c>: every glyph index is below it.</summary>
        public int NumGlyphs { get; }

        /// <summary>Changes each time a glyph or a width is taken: the program built for one count is the program for it.</summary>
        public int Version { get; private set; }

        /// <summary>Each glyph used, its advance in the 1000-unit glyph space a PDF font dictionary uses.</summary>
        public IReadOnlyDictionary<int, double> Advances => _advances;

        /// <summary>
        /// Each glyph used whose text is known, the text it stands for — the character its glyph name
        /// denotes, or its CID's in the character collection — for the <c>/ToUnicode</c>.
        /// </summary>
        public IReadOnlyDictionary<int, string> Texts => _texts;

        /// <summary>Whether this is a Type 2 CIDFont's program, its glyphs reached by CID through <c>/CIDMap</c>.</summary>
        public bool IsCidKeyed => _cidMap != null;

        /// <summary>
        /// Whether <paramref name="font"/> is a Type 2 CIDFont as it is defined (PLRM §5.11.3:
        /// <c>/CIDFontType 2</c>, <c>/FontType</c> 42 then, 11 after).
        /// </summary>
        public static bool IsCidFontType2(PsDict font)
            => font != null && font.TryGetName("CIDFontType", out var t) && t.IsNumber && t.AsInt() == 2;

        /// <summary>
        /// A Type 2 CIDFont's program: its <c>/sfnts</c> read as a Type 42 font's are, and its
        /// <c>/CIDMap</c> (a string, or an array of strings each a multiple of <c>/GDBytes</c> long)
        /// joined and charged — or null with <paramref name="reason"/>. A map shorter than
        /// <c>/CIDCount</c> × <c>/GDBytes</c> leaves the CIDs past its end undefined, as CIDs past
        /// <c>/CIDCount</c> are.
        /// </summary>
        public static PsType42Font TryReadCid(PsDict font, Action<long> charge, out string reason)
        {
            if (!font.TryGetName("GDBytes", out var gdBytes) || gdBytes.Type != PsType.Integer
                || gdBytes.AsInt() < 1 || gdBytes.AsInt() > 4)
            {
                reason = "its /GDBytes is not an integer from 1 to 4";
                return null;
            }
            if (!font.TryGetName("CIDCount", out var cidCount) || cidCount.Type != PsType.Integer || cidCount.AsInt() < 1)
            {
                reason = "its /CIDCount is not a positive integer";
                return null;
            }
            if (!font.TryGetName("CIDMap", out var cidMap) || !TryJoin(cidMap, charge, out byte[] map))
            {
                reason = "its /CIDMap is not a string or an array of strings";
                return null;
            }

            PsType42Font program = TryRead(font, charge, out reason);
            if (program == null) return null;
            program._cidMap = map;
            program._gdBytes = gdBytes.AsInt();
            program._cidCount = cidCount.AsInt();
            return program;
        }

        /// <summary>A string, or an array of strings, as one array of bytes — charged before it is made.</summary>
        internal static bool TryJoin(PsObject value, Action<long> charge, out byte[] joined)
        {
            joined = null;
            if (value.Type == PsType.String)
            {
                charge(value.AsString().Length);
                joined = value.AsString().ToArray();
                return true;
            }
            if (value.Type != PsType.Array) return false;
            PsArray parts = value.AsArray();
            long total = 0;
            for (int k = 0; k < parts.Length; k++)
            {
                if (parts[k].Type != PsType.String) return false;
                total += parts[k].AsString().Length;
            }
            if (total > int.MaxValue) return false;
            charge(total);
            joined = new byte[total];
            int at = 0;
            for (int k = 0; k < parts.Length; k++)
            {
                PsString part = parts[k].AsString();
                Array.Copy(part.Bytes, part.Offset, joined, at, part.Length);
                at += part.Length;
            }
            return true;
        }

        /// <summary>Whether <paramref name="font"/> declares <c>/FontType 42</c>.</summary>
        public static bool IsType42(PsDict font)
            => font != null && font.TryGetName("FontType", out var t) && t.IsNumber && t.AsInt() == 42;

        /// <summary>
        /// The program <paramref name="font"/>'s <c>/sfnts</c> carries, or null with
        /// <paramref name="reason"/> when it is none this converter can carry. The joined copy is
        /// charged through <paramref name="charge"/> before it is made.
        /// </summary>
        public static PsType42Font TryRead(PsDict font, Action<long> charge, out string reason)
        {
            reason = null;
            if (!font.TryGetName("sfnts", out var sfnts) || sfnts.Type != PsType.Array)
            {
                reason = "it has no /sfnts array";
                return null;
            }

            PsArray strings = sfnts.AsArray();
            long total = 0;
            for (int k = 0; k < strings.Length; k++)
            {
                if (strings[k].Type != PsType.String)
                {
                    reason = "an /sfnts entry is not a string";
                    return null;
                }
                total += ProgramBytes(strings[k].AsString());
            }
            if (total < 12 || total > int.MaxValue)
            {
                reason = "its /sfnts strings hold no sfnt";
                return null;
            }
            charge(total);

            var data = new byte[total];
            int at = 0;
            for (int k = 0; k < strings.Length; k++)
            {
                PsString s = strings[k].AsString();
                int take = ProgramBytes(s);
                Array.Copy(s.Bytes, s.Offset, data, at, take);
                at += take;
            }

            if (!SfntTableDirectory.TryRead(data, out IReadOnlyList<SfntTableDirectory.TableRecord> records, out string directory))
            {
                reason = "its table directory was refused: " + directory;
                return null;
            }

            var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (SfntTableDirectory.TableRecord record in records)
            {
                // gdir is the note's placeholder for /GlyphDirectory: a table of no bytes.
                if (record.Length == 0 || record.Tag == "gdir") continue;
                tables[record.Tag] = SfntTableDirectory.Payload(data, record);
            }

            if (!tables.TryGetValue("head", out byte[] head) || head.Length < 54
                || !tables.TryGetValue("hhea", out byte[] hhea) || hhea.Length < 36
                || !tables.TryGetValue("maxp", out byte[] maxp) || maxp.Length < 6)
            {
                reason = "its program has no head, hhea or maxp table";
                return null;
            }

            int unitsPerEm = BigEndian.U16(head, 18);
            int numGlyphs = BigEndian.U16(maxp, 4);
            if (unitsPerEm == 0 || numGlyphs == 0)
            {
                reason = "its program declares no units per em or no glyphs";
                return null;
            }

            bool hasOutlines = tables.ContainsKey("glyf") && tables.ContainsKey("loca");
            bool hasDirectory = font.TryGetName(GlyphDirectoryKey, out var directoryEntry)
                                && directoryEntry.Type is PsType.Dictionary or PsType.Array;
            if (!hasOutlines && !hasDirectory)
            {
                reason = "its program has no glyf outlines and the font no /GlyphDirectory";
                return null;
            }
            int metricsCount = 0;
            if (!hasOutlines && font.TryGetName("MetricsCount", out var declared))
            {
                if (declared.Type != PsType.Integer || declared.AsInt() is not (0 or 2 or 4))
                {
                    reason = "its /MetricsCount is not 0, 2 or 4 (TN 5012 §5.7)";
                    return null;
                }
                metricsCount = declared.AsInt();
                if (metricsCount != 0 && !IsCidFontType2(font))
                {
                    reason = "its /GlyphDirectory strings carry a /MetricsCount prefix, which TN 5012 §5.7 defines for a Type 2 CIDFont only";
                    return null;
                }
            }

            int numberOfHMetrics = BigEndian.U16(hhea, 34);
            bool hasHmtx = tables.TryGetValue("hmtx", out byte[] hmtx)
                           && numberOfHMetrics > 0 && hmtx.Length >= numberOfHMetrics * 4;

            return new PsType42Font(tables, unitsPerEm, numGlyphs, hasOutlines, hasHmtx, numberOfHMetrics) { _metricsCount = metricsCount };
        }

        /// <summary>How many of an <c>/sfnts</c> string's bytes are the program's: an odd length's last byte is padding (TN 5012 §4.2).</summary>
        private static int ProgramBytes(PsString s) => s.Length & ~1;

        /// <summary>
        /// The glyph <paramref name="name"/> selects in <paramref name="font"/> — through its
        /// <c>/CharStrings</c>, <c>.notdef</c>'s (else glyph 0) for a name it lacks — and its advance in
        /// 1000-unit glyph space, taking the glyph and its width the first time it is used.
        /// <paramref name="font"/> is the dictionary in use: a scaled copy shares the original's
        /// <c>/CharStrings</c>, <c>/GlyphDirectory</c> and <c>/Metrics</c>, and a driver writes into them
        /// after <c>definefont</c>.
        /// </summary>
        public int Glyph(PsDict font, string name, Action<long> charge, out double advance)
        {
            int glyph = GlyphIndex(font, name, out string reached);
            string text = PdfGlyphNames.TryGetUnicode(reached, out char c) ? c.ToString() : null;
            return Use(font, glyph, PsObject.LitName(reached), "'" + reached + "'", text, charge, out advance);
        }

        /// <summary>
        /// The glyph a Type 2 CIDFont draws for <paramref name="cid"/> — its <c>/CIDMap</c> entry, the
        /// undefined glyph 0 for a CID outside <c>/CIDCount</c> or the map, or for an index the program
        /// does not hold — and its advance, from <c>/Metrics</c> keyed by the CID (PLRM Table 5.17), else
        /// <c>hmtx</c>; taken the first time it is used, as <see cref="Glyph"/> takes one.
        /// <paramref name="text"/> is the CID's text in the collection, or null.
        /// </summary>
        public int GlyphOfCid(PsDict font, int cid, string text, Action<long> charge, out double advance)
        {
            int glyph = 0;
            long at = (long)cid * _gdBytes;
            if (cid >= 0 && cid < _cidCount && at + _gdBytes <= _cidMap.Length)
            {
                // In a long: four bytes of the file's are not a sign.
                long index = 0;
                for (int k = 0; k < _gdBytes; k++) index = (index << 8) | _cidMap[at + k];
                if (index < NumGlyphs) glyph = (int)index;
            }
            return Use(font, glyph, PsObject.Int(cid), "CID " + cid, text, charge, out advance);
        }

        /// <summary>
        /// <paramref name="glyph"/> used: its glyph data taken, its advance the first time read (by
        /// <paramref name="metricsKey"/> in <c>/Metrics</c>), its text the first time known.
        /// </summary>
        private int Use(PsDict font, int glyph, PsObject metricsKey, string label, string text,
                        Action<long> charge, out double advance)
        {
            Take(font, glyph, 0, charge);
            if (!_advances.TryGetValue(glyph, out advance))
            {
                advance = AdvanceOf(font, metricsKey, label, glyph);
                _advances[glyph] = advance;
                Version++;
            }
            if (!_texts.ContainsKey(glyph) && !_textless.Contains(glyph))
            {
                text ??= InstalledText(glyph);
                if (text != null) _texts[glyph] = text;
                else
                {
                    _textless.Add(glyph);
                    if (!_textlessReported)
                    {
                        _textlessReported = true;
                        PostScriptDiagnostics.Report("PsType42Font",
                            $"glyph {glyph} of a Type 42 font stands for no known text — no glyph name, no character collection, " +
                            "and no installed font holds the same glyph — so it is drawn but cannot be extracted. Reported once a font.");
                    }
                }
            }
            return glyph;
        }

        private readonly HashSet<int> _textless = new();
        private bool _textlessReported;
        private InstalledFontGlyphs _installed;
        private bool _installedSought;

        /// <summary>
        /// The text of <paramref name="glyph"/> from the installed font this program was taken from
        /// (<see cref="InstalledFontGlyphs"/>: the same <c>head</c>, the same glyph bytes) — how a
        /// driver's font that carries glyphs alone, as the Microsoft PS Class Driver's does, keeps its
        /// text — or null.
        /// </summary>
        private string InstalledText(int glyph)
        {
            if (!_installedSought)
            {
                _installedSought = true;
                _installed = InstalledFontGlyphs.TryIdentify(_tables["head"]);
            }
            byte[] bytes = _installed == null ? null : GlyphBytes(glyph);
            return bytes == null ? null : _installed.TextOf(glyph, bytes);
        }

        /// <summary>The glyph's description: the <c>/GlyphDirectory</c> string taken for it, or the program's own <c>glyf</c> entry.</summary>
        private byte[] GlyphBytes(int glyph)
        {
            if (!_hasOutlines) return _glyphs.TryGetValue(glyph, out byte[] taken) ? taken : null;
            return InstalledFontGlyphs.GlyphOf(_tables["loca"], _tables["glyf"], (short)BigEndian.U16(_tables["head"], 50), glyph);
        }

        private int GlyphIndex(PsDict font, string name, out string reached)
        {
            reached = name;
            if (TryCharString(font, name, out int glyph)) return glyph;
            reached = ".notdef";
            return TryCharString(font, ".notdef", out glyph) ? glyph : 0;
        }

        private bool TryCharString(PsDict font, string name, out int glyph)
        {
            glyph = 0;
            if (name == null || !font.TryGetName("CharStrings", out var charStrings) || charStrings.Type != PsType.Dictionary)
                return false;
            if (!charStrings.AsDict().TryGetName(name, out var index) || index.Type != PsType.Integer)
                return false;
            glyph = index.AsInt();
            return glyph >= 0 && glyph < NumGlyphs;
        }

        /// <summary>
        /// The advance of <paramref name="glyph"/>: the <c>/Metrics</c> entry under
        /// <paramref name="key"/> — its name, or a CIDFont's CID (PLRM §5.9.2: <c>wx</c>, <c>[sbx wx]</c>
        /// or <c>[sbx sby wx wy]</c>, in the em the font matrix maps) — else the horizontal advance its
        /// <c>/GlyphDirectory</c> string's <c>/MetricsCount</c> prefix carried, else the program's
        /// <c>hmtx</c>, else nothing, said.
        /// </summary>
        private double AdvanceOf(PsDict font, PsObject key, string label, int glyph)
        {
            if (font.TryGetName("Metrics", out var metrics) && metrics.Type == PsType.Dictionary
                && metrics.AsDict().TryGet(key, out var entry))
            {
                double? wx = entry.IsNumber ? entry.Num
                    : entry.Type == PsType.Array && entry.AsArray() is { Length: 2 or 4 } array && array[array.Length == 2 ? 1 : 2].IsNumber
                        ? array[array.Length == 2 ? 1 : 2].Num
                        : null;
                if (wx is double em && !double.IsNaN(em) && !double.IsInfinity(em))
                    return em * PdfUnits.GlyphSpaceUnitsPerEm;
            }

            if (_prefixMetrics.TryGetValue(glyph, out var prefix))
                return prefix.Advance * (double)PdfUnits.GlyphSpaceUnitsPerEm / UnitsPerEm;

            if (_hasHmtx)
            {
                int record = Math.Min(glyph, _numberOfHMetrics - 1);
                return BigEndian.U16(_tables["hmtx"], record * 4) * (double)PdfUnits.GlyphSpaceUnitsPerEm / UnitsPerEm;
            }

            PostScriptDiagnostics.Report("PsType42Font",
                $"glyph {label} of a Type 42 font has no /Metrics entry and the program no hmtx; it advances by nothing");
            return 0;
        }

        /// <summary>
        /// Copies <paramref name="glyph"/>'s <c>/GlyphDirectory</c> string, and those of the glyphs a
        /// composite names, the first time each is used. A program with its own <c>glyf</c> has nothing
        /// to take.
        /// </summary>
        private void Take(PsDict font, int glyph, int depth, Action<long> charge)
        {
            if (_hasOutlines || _glyphs.ContainsKey(glyph) || depth > PdfLimits.MaxCompositeGlyphDepth) return;
            if (!font.TryGetName(GlyphDirectoryKey, out var directory)) return;

            PsObject entry = PsObject.Null;
            if (directory.Type == PsType.Dictionary) directory.AsDict().TryGet(PsObject.Int(glyph), out entry);
            else if (directory.Type == PsType.Array && glyph < directory.AsArray().Length) entry = directory.AsArray()[glyph];
            if (entry.Type != PsType.String) return;   // not downloaded (yet): a later use takes it

            PsString data = entry.AsString();
            int prefix = 2 * _metricsCount;   // TN 5012 §5.7: the description begins 2 × MetricsCount bytes in
            if (data.Length < prefix)
            {
                if (!_shortPrefixReported)
                {
                    _shortPrefixReported = true;
                    PostScriptDiagnostics.Report("PsType42Font",
                        $"glyph {glyph}'s /GlyphDirectory string is {data.Length} bytes, short of the {prefix} its /MetricsCount puts before the glyph; it is not taken. Reported once.");
                }
                return;
            }
            charge(data.Length);
            byte[] all = data.ToArray();
            if (prefix > 0)
            {
                // The horizontal pair is the last four bytes of the prefix: alone at 2, after the
                // vertical pair at 4 — copied from the TrueType data, high byte first.
                _prefixMetrics[glyph] = (BigEndian.U16(all, prefix - 4), (short)BigEndian.U16(all, prefix - 2));
            }
            byte[] bytes = all;
            if (prefix > 0)
            {
                bytes = new byte[all.Length - prefix];
                Array.Copy(all, prefix, bytes, 0, bytes.Length);
            }
            _glyphs[glyph] = bytes;
            Version++;

            if (bytes.Length >= 10 && (short)BigEndian.U16(bytes, 0) < 0)
                foreach (CompositeGlyph.Component component in CompositeGlyph.Components(bytes, 0, bytes.Length))
                    if (component.GlyphIndex < NumGlyphs) Take(font, component.GlyphIndex, depth + 1, charge);
        }

        private bool _shortPrefixReported;

        private TrueTypeOutlines _outlines;
        private int _outlinesVersion = -1;

        /// <summary>
        /// <paramref name="glyph"/>'s outline in 1000-unit glyph space, advancing by
        /// <paramref name="advance"/>, or null when the program cannot be read for it — the program
        /// assembled from what was taken so far, rebuilt only when that grew.
        /// </summary>
        public GlyphOutline Outline(int glyph, double advance, Action<long> charge)
        {
            if (_outlines == null || _outlinesVersion != Version)
            {
                byte[] program = Program();
                charge(program.Length);
                _outlines = TrueTypeOutlines.TryRead(TrueTypeFontProgram.Parse(program), out _);
                _outlinesVersion = Version;
            }
            return _outlines != null && _outlines.TryGetGlyph(glyph, out GlyphOutline outline)
                ? new GlyphOutline(outline.Segments, advance)
                : null;
        }

        /// <summary>
        /// The TrueType program a PDF reader is given: the <c>/sfnts</c> tables, and — where the
        /// program had none — a <c>glyf</c> and long <c>loca</c> from the glyphs taken (an absent one
        /// empty) and an <c>hmtx</c> from the advances used (one record a glyph, <c>hhea</c> saying so),
        /// assembled by Core's <see cref="SfntBuilder"/>.
        /// </summary>
        public byte[] Program()
        {
            var tables = new List<(string, byte[])>();
            foreach (var kv in _tables)
            {
                if (!_hasOutlines && (kv.Key == "glyf" || kv.Key == "loca")) continue;
                if (!_hasHmtx && kv.Key == "hmtx") continue;
                if (kv.Key == "head" && !_hasOutlines) continue;
                if (kv.Key == "hhea" && !_hasHmtx) continue;
                tables.Add((kv.Key, kv.Value));
            }

            if (!_hasOutlines)
            {
                byte[] head = (byte[])_tables["head"].Clone();
                BigEndian.Put16(head, 50, 1);              // indexToLocFormat: long
                tables.Add(("head", head));

                long glyfLength = 0;
                foreach (byte[] g in _glyphs.Values) glyfLength += (g.Length + 3) & ~3;
                var glyf = new byte[glyfLength];
                var loca = new byte[(NumGlyphs + 1) * 4];
                uint offset = 0;
                for (int glyph = 0; glyph < NumGlyphs; glyph++)
                {
                    BigEndian.Put32(loca, glyph * 4, offset);
                    if (_glyphs.TryGetValue(glyph, out byte[] bytes))
                    {
                        Array.Copy(bytes, 0, glyf, offset, bytes.Length);
                        offset += (uint)((bytes.Length + 3) & ~3);
                    }
                }
                BigEndian.Put32(loca, NumGlyphs * 4, offset);
                tables.Add(("glyf", glyf));
                tables.Add(("loca", loca));
            }

            if (!_hasHmtx)
            {
                byte[] hhea = (byte[])_tables["hhea"].Clone();
                BigEndian.Put16(hhea, 34, (ushort)NumGlyphs);
                tables.Add(("hhea", hhea));

                var hmtx = new byte[NumGlyphs * 4];
                foreach (var kv in _advances)
                {
                    double units = Math.Round(kv.Value * UnitsPerEm / PdfUnits.GlyphSpaceUnitsPerEm);
                    BigEndian.Put16(hmtx, kv.Key * 4, (ushort)Core.Util.SaturatingCast.ToInt(Math.Max(0, Math.Min(ushort.MaxValue, units))));
                    if (_prefixMetrics.TryGetValue(kv.Key, out var prefix))
                        BigEndian.Put16(hmtx, kv.Key * 4 + 2, (ushort)prefix.SideBearing);   // the program's own, from the prefix
                    else if (_glyphs.TryGetValue(kv.Key, out byte[] bytes) && bytes.Length >= 4)
                        BigEndian.Put16(hmtx, kv.Key * 4 + 2, (ushort)BigEndian.U16(bytes, 2));   // xMin as the side bearing
                }
                tables.Add(("hmtx", hmtx));
            }

            return SfntBuilder.Build(SfntBuilder.VersionTrueType, tables);
        }

        private Core.Objects.PdfStream _stream;
        private int _streamVersion = -1;

        /// <summary>
        /// The <c>/FontFile2</c> stream of <see cref="Program"/> — one instance until a glyph or a width
        /// is taken, so every resource dictionary built in between names the same object and the
        /// document writer's identity memo writes it once.
        /// </summary>
        public Core.Objects.PdfStream ProgramStream()
        {
            if (_stream != null && _streamVersion == Version) return _stream;
            byte[] program = Program();
            _stream = new Core.Objects.PdfStream { RawData = program };
            _stream[Core.Objects.PdfName.Length1] = new Core.Objects.PdfNumber(program.Length);
            _streamVersion = Version;
            return _stream;
        }

        /// <summary>The program's box, ascent and descent in 1000-unit glyph space, for the font descriptor.</summary>
        public (double XMin, double YMin, double XMax, double YMax, double Ascent, double Descent) DescriptorMetrics()
        {
            byte[] head = _tables["head"], hhea = _tables["hhea"];
            double scale = PdfUnits.GlyphSpaceUnitsPerEm / (double)UnitsPerEm;
            return ((short)BigEndian.U16(head, 36) * scale, (short)BigEndian.U16(head, 38) * scale,
                    (short)BigEndian.U16(head, 40) * scale, (short)BigEndian.U16(head, 42) * scale,
                    (short)BigEndian.U16(hhea, 4) * scale, (short)BigEndian.U16(hhea, 6) * scale);
        }
    }
}
