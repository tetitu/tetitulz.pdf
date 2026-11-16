// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Fonts.Type1;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;

namespace Tetitulz.Pdf.Plugins.PostScript.Emit
{
    /// <summary>
    /// A Type 0 CIDFont (PLRM §5.11.3, Table 5.13): Type 1 charstrings by CID in the binary data
    /// <c>StartData</c> reads (<c>/GlyphData</c>), each found through the offset table at
    /// <c>/CIDMapOffset</c> — an FD index of <c>/FDBytes</c> and an offset of <c>/GDBytes</c> a CID,
    /// high byte first, the charstring ending where the next CID's begins — or through
    /// <c>/GlyphDirectory</c> (an FD index then the charstring), and run with the subroutines of its FD's
    /// Private dictionary: a <c>/Subrs</c> array, or <c>/SubrMapOffset</c>, <c>/SDBytes</c> and
    /// <c>/SubrCount</c> into the data, each subroutine ending where the next begins; both decrypted
    /// under the FD's <c>/lenIV</c>, 4 when it has none. The lengths, the default and the decryption are
    /// as two other implementations read them alike.
    ///
    /// <para><b>Carried as a CFF a PDF can embed.</b> PDF holds no Type 1 CIDFont program; each CID the
    /// text uses is run into its outline by Core's Type 1 interpreter and added to a name-keyed CFF
    /// (<see cref="Type1CharstringFontBuilder"/>) as the next glyph, CID 0 first — and under a
    /// <c>CIDFontType0</c> such a CFF takes its CIDs as glyph indices (ISO 32000-2 §9.7.4.2), so the
    /// glyph's index is the code the text shows. Hints do not travel; the outlines are exact.</para>
    ///
    /// <para><b>One CFF has one font matrix</b>, and FDs may scale the glyph space differently: the CFF
    /// takes the finest FD's glyph space, and a glyph run under a coarser FD is added scaled into it,
    /// each point rounded to its units (<see cref="Type1CharstringFontBuilder.Add(byte[], IReadOnlyList{byte[]}, double)"/>).</para>
    ///
    /// <para><b>Refused by name:</b> a matrix that is not a uniform scale, and offsets, counts or byte
    /// widths outside the data. A CID past <c>/CIDCount</c> or whose charstring is missing draws as
    /// CID 0; one whose charstring will not run, is a <c>seac</c> composite, or scaled reaches past
    /// <see cref="Type1CharstringFontBuilder.MaxCoordinate"/>, as an empty glyph, said once. A
    /// <c>seac</c> composite names its components through StandardEncoding's glyph names, which a
    /// CIDFont does not have: two other implementations refuse it alike.</para>
    /// </summary>
    internal sealed class PsCidType0Font : IPsGlyphIndexedProgram
    {
        private readonly byte[] _data;
        private readonly int _cidCount, _gdBytes, _fdBytes, _cidMapOffset;
        private readonly int[] _lenIV;
        private readonly IReadOnlyList<byte[]>[] _subrs;
        private readonly double[] _fdFactors;           // the font's glyph-space units per unit of each FD's
        private readonly double _unitsPerGlyphUnit;     // 1000-unit glyph space per glyph-space unit
        private readonly Dictionary<int, int> _fdOfGlyph = new();
        private readonly (double, double, double, double, double, double) _metrics;
        private readonly Type1CharstringFontBuilder _builder;

        private readonly Dictionary<int, int> _glyphOfCid = new();
        private readonly Dictionary<int, double> _advances = new();
        private readonly Dictionary<int, string> _texts = new();
        private readonly Dictionary<int, GlyphOutline> _outlines = new();
        private bool _refusedGlyphReported;

        private PsCidType0Font(string name, byte[] data, int cidCount, int gdBytes, int fdBytes, int cidMapOffset,
                               int[] lenIV, IReadOnlyList<byte[]>[] subrs, double[] fdFactors, double scale,
                               (double, double, double, double, double, double) metrics)
        {
            _data = data;
            _cidCount = cidCount;
            _gdBytes = gdBytes;
            _fdBytes = fdBytes;
            _cidMapOffset = cidMapOffset;
            _lenIV = lenIV;
            _subrs = subrs;
            _fdFactors = fdFactors;
            _unitsPerGlyphUnit = scale * 1000;
            _metrics = metrics;
            _builder = new Type1CharstringFontBuilder(name, scale);
        }

        /// <summary>A CFF program: a <c>CIDFontType0</c> with a <c>/FontFile3</c>.</summary>
        public bool IsCff => true;

        public IReadOnlyDictionary<int, double> Advances => _advances;

        public IReadOnlyDictionary<int, string> Texts => _texts;

        /// <summary>Whether <paramref name="font"/> declares <c>/CIDFontType 0</c>.</summary>
        public static bool IsCidFontType0(PsDict font)
            => font != null && font.TryGetName("CIDFontType", out var t) && t.IsNumber && t.AsInt() == 0;

        /// <summary>
        /// The program <paramref name="font"/> — a Type 0 CIDFont as <c>StartData</c> completed it —
        /// carries, or null with <paramref name="reason"/>; the data and every subroutine copied out are
        /// charged through <paramref name="charge"/>.
        /// </summary>
        public static PsCidType0Font TryRead(string name, PsDict font, Action<long> charge, out string reason)
        {
            reason = null;
            if (!Int(font, "CIDCount", out int cidCount) || cidCount < 1) { reason = "its /CIDCount is not a positive integer"; return null; }
            if (!Int(font, "GDBytes", out int gdBytes) || gdBytes < 1 || gdBytes > 4) { reason = "its /GDBytes is not an integer from 1 to 4"; return null; }
            if (!Int(font, "FDBytes", out int fdBytes) || fdBytes < 0 || fdBytes > 4) { reason = "its /FDBytes is not an integer from 0 to 4"; return null; }
            if (!Int(font, "CIDMapOffset", out int cidMapOffset) || cidMapOffset < 0) { reason = "its /CIDMapOffset is not a non-negative integer"; return null; }

            byte[] data = Array.Empty<byte>();
            if (font.TryGetName("GlyphData", out var glyphData) && !PsType42Font.TryJoin(glyphData, charge, out data))
            {
                reason = "its /GlyphData is not a string or an array of strings";
                return null;
            }

            if (!font.TryGetName("FDArray", out var fdArray) || fdArray.Type != PsType.Array || fdArray.AsArray().Length == 0)
            {
                reason = "it has no /FDArray";
                return null;
            }

            // Each FD's glyph space reaches text space through its matrix and the CIDFont's own where it
            // has one (PLRM: absent, defineresource supplies [.001 …] and scales each FD's by 1000, the
            // product the same). One CFF has one matrix: the finest FD's, every other FD's glyphs
            // written in its units.
            double top = font.TryGetName("FontMatrix", out var topMatrix) ? UniformScale(topMatrix) : 1;
            PsArray fds = fdArray.AsArray();
            var lenIV = new int[fds.Length];
            var subrs = new IReadOnlyList<byte[]>[fds.Length];
            var fdScales = new double[fds.Length];
            double scale = double.PositiveInfinity;
            for (int k = 0; k < fds.Length; k++)
            {
                if (fds[k].Type != PsType.Dictionary) { reason = "an /FDArray entry is not a dictionary"; return null; }
                PsDict fd = fds[k].AsDict();
                double fdScale = (fd.TryGetName("FontMatrix", out var fdMatrix) ? UniformScale(fdMatrix) : 0.001) * top;
                if (!(fdScale > 0) || double.IsInfinity(fdScale)) { reason = "an FD's /FontMatrix is not a uniform scale"; return null; }
                fdScales[k] = fdScale;
                scale = Math.Min(scale, fdScale);

                PsDict priv = fd.TryGetName("Private", out var p) && p.Type == PsType.Dictionary ? p.AsDict() : new PsDict();
                lenIV[k] = Int(priv, "lenIV", out int iv) ? iv : 4;
                subrs[k] = ReadSubrs(priv, data, lenIV[k], charge, out reason);
                if (subrs[k] == null) return null;
            }
            var fdFactors = new double[fds.Length];
            for (int k = 0; k < fds.Length; k++)
            {
                fdFactors[k] = fdScales[k] / scale;
                if (double.IsInfinity(fdFactors[k]))
                {
                    reason = "its FDs' matrices differ by more than a number counts the finest FD's units in";
                    return null;
                }
            }

            double units = scale * 1000;
            var box = font.TryGetName("FontBBox", out var bbox) && bbox.Type == PsType.Array && bbox.AsArray().Length == 4
                      && bbox.AsArray()[0].IsNumber && bbox.AsArray()[1].IsNumber && bbox.AsArray()[2].IsNumber && bbox.AsArray()[3].IsNumber
                ? (bbox.AsArray()[0].Num * units, bbox.AsArray()[1].Num * units, bbox.AsArray()[2].Num * units, bbox.AsArray()[3].Num * units)
                : (-200.0, -250.0, 1200.0, 1000.0);
            var metrics = (box.Item1, box.Item2, box.Item3, box.Item4, box.Item4, box.Item2);

            var program = new PsCidType0Font(name, data, cidCount, gdBytes, fdBytes, cidMapOffset, lenIV, subrs, fdFactors, scale, metrics);
            program.Take(font, 0, charge);   // CID 0 is glyph 0, where a reader lands for what it cannot map
            return program;
        }

        /// <summary>An FD's subroutines: its <c>/Subrs</c> strings, or the table its three entries locate in the data, each decrypted.</summary>
        private static IReadOnlyList<byte[]> ReadSubrs(PsDict priv, byte[] data, int lenIV, Action<long> charge, out string reason)
        {
            reason = null;
            var subrs = new List<byte[]>();
            if (priv.TryGetName("Subrs", out var array) && array.Type == PsType.Array)
            {
                foreach (PsObject s in Items(array.AsArray()))
                {
                    if (s.Type != PsType.String) { subrs.Add(Array.Empty<byte>()); continue; }
                    PsString text = s.AsString();
                    charge(text.Length);
                    subrs.Add(Type1CharstringFontBuilder.Decrypt(text.Bytes, text.Offset, text.Length, lenIV) ?? Array.Empty<byte>());
                }
                return subrs;
            }

            if (!Int(priv, "SubrCount", out int count) || count == 0) return subrs;
            if (!Int(priv, "SDBytes", out int sdBytes) || sdBytes < 1 || sdBytes > 4
                || !Int(priv, "SubrMapOffset", out int mapOffset) || count < 0 || mapOffset < 0
                || (long)mapOffset + (long)(count + 1) * sdBytes > data.Length)
            {
                reason = "an FD's /SubrMapOffset, /SDBytes or /SubrCount lies outside the data";
                return null;
            }
            for (int k = 0; k < count; k++)
            {
                long start = ReadOffset(data, mapOffset + (long)k * sdBytes, sdBytes);
                long end = ReadOffset(data, mapOffset + (long)(k + 1) * sdBytes, sdBytes);
                if (start < 0 || end > data.Length || end <= start) { subrs.Add(Array.Empty<byte>()); continue; }
                charge(end - start);
                subrs.Add(Type1CharstringFontBuilder.Decrypt(data, (int)start, (int)(end - start), lenIV) ?? Array.Empty<byte>());
            }
            return subrs;
        }

        public int GlyphOfCid(PsDict font, int cid, string text, Action<long> charge, out double advance)
        {
            int glyph = Take(font, cid >= 0 && cid < _cidCount ? cid : 0, charge);
            if (!_advances.TryGetValue(glyph, out advance))
            {
                advance = MetricsAdvance(font, cid, glyph) ?? (_outlines.TryGetValue(glyph, out GlyphOutline outline) ? outline.Advance : 0);
                _advances[glyph] = advance;
            }
            if (text != null && !_texts.ContainsKey(glyph)) _texts[glyph] = text;
            return glyph;
        }

        /// <summary>
        /// The glyph <paramref name="cid"/> is — added to the CFF the first time it is used, its
        /// charstring found, decrypted and run — or 0 for a CID whose charstring is missing or unusable.
        /// </summary>
        private int Take(PsDict font, int cid, Action<long> charge)
        {
            if (_glyphOfCid.TryGetValue(cid, out int known)) return known;
            byte[] charstring = Charstring(font, cid, charge, out int fd);
            if (charstring == null && cid != 0)
            {
                _glyphOfCid[cid] = 0;
                return 0;
            }

            int glyph = _builder.Count;
            bool hasFd = fd >= 0 && fd < _subrs.Length;
            GlyphOutline outline = _builder.Add(charstring, hasFd ? _subrs[fd] : Array.Empty<byte[]>(), hasFd ? _fdFactors[fd] : 1);
            if (hasFd) _fdOfGlyph[glyph] = fd;
            if (outline == null && charstring != null)
            {
                if (!_refusedGlyphReported)
                {
                    _refusedGlyphReported = true;
                    PostScriptDiagnostics.Report("PsCidType0Font",
                        $"the charstring of CID {cid} could not be run, is a seac composite, or reaches past the coordinates a CFF "
                        + $"writes ({Type1CharstringFontBuilder.MaxCoordinate} units of the finest FD's glyph space); it draws as an empty glyph. Reported once.");
                }
            }
            else if (outline != null)
            {
                _outlines[glyph] = Scaled(outline);
            }
            _glyphOfCid[cid] = glyph;
            return glyph;
        }

        /// <summary>
        /// <paramref name="cid"/>'s charstring, decrypted, and its FD index — from <c>/GlyphDirectory</c>
        /// when the font has one, else through the offset table — or null when it has none.
        /// </summary>
        private byte[] Charstring(PsDict font, int cid, Action<long> charge, out int fd)
        {
            fd = -1;
            if (font.TryGetName("GlyphDirectory", out var directory) && directory.Type is PsType.Dictionary or PsType.Array)
            {
                PsObject entry = PsObject.Null;
                if (directory.Type == PsType.Dictionary) directory.AsDict().TryGet(PsObject.Int(cid), out entry);
                else if (cid < directory.AsArray().Length) entry = directory.AsArray()[cid];
                if (entry.Type != PsType.String || entry.AsString().Length <= _fdBytes) return null;
                PsString s = entry.AsString();
                long directoryFd = ReadOffset(s.ToArray(), 0, _fdBytes);   // in a long: four bytes of the file's are not a sign
                if (directoryFd >= _lenIV.Length) return null;
                fd = (int)directoryFd;
                charge(s.Length);
                return Type1CharstringFontBuilder.Decrypt(s.Bytes, s.Offset + _fdBytes, s.Length - _fdBytes, _lenIV[fd]);
            }

            int entryBytes = _fdBytes + _gdBytes;
            long at = _cidMapOffset + (long)cid * entryBytes;
            if (at + 2L * entryBytes > _data.Length) return null;
            long tableFd = ReadOffset(_data, at, _fdBytes);
            long start = ReadOffset(_data, at + _fdBytes, _gdBytes);
            long end = ReadOffset(_data, at + entryBytes + _fdBytes, _gdBytes);
            if (tableFd >= _lenIV.Length || end <= start || end > _data.Length) return null;   // a missing glyph
            fd = (int)tableFd;
            charge(end - start);
            return Type1CharstringFontBuilder.Decrypt(_data, (int)start, (int)(end - start), _lenIV[fd]);
        }

        /// <summary>
        /// The <c>/Metrics</c> width the CIDFont gives <paramref name="cid"/> (PLRM §5.9.2), in 1000-unit
        /// glyph space, or null. The width replaces the charstring's own, so it is in the glyph space of
        /// the FD <paramref name="glyph"/> was run under.
        /// </summary>
        private double? MetricsAdvance(PsDict font, int cid, int glyph)
        {
            if (!font.TryGetName("Metrics", out var metrics) || metrics.Type != PsType.Dictionary
                || !metrics.AsDict().TryGet(PsObject.Int(cid), out var entry))
                return null;
            double? wx = entry.IsNumber ? entry.Num
                : entry.Type == PsType.Array && entry.AsArray() is { Length: 2 or 4 } array && array[array.Length == 2 ? 1 : 2].IsNumber
                    ? array[array.Length == 2 ? 1 : 2].Num
                    : null;
            double units = _unitsPerGlyphUnit * (_fdOfGlyph.TryGetValue(glyph, out int fd) ? _fdFactors[fd] : 1);
            return wx is double w && !double.IsNaN(w) && !double.IsInfinity(w) ? w * units : null;
        }

        /// <summary>An outline in glyph space as one in the 1000-unit glyph space the plugin reads.</summary>
        private GlyphOutline Scaled(GlyphOutline outline)
        {
            if (Math.Abs(_unitsPerGlyphUnit - 1) < 1e-12) return outline;
            double f = _unitsPerGlyphUnit;
            var segments = new List<GlyphOutlineSegment>(outline.Segments.Count);
            foreach (GlyphOutlineSegment s in outline.Segments)
                segments.Add(new GlyphOutlineSegment(s.Kind, s.X1 * f, s.Y1 * f, s.X2 * f, s.Y2 * f, s.X3 * f, s.Y3 * f));
            return new GlyphOutline(segments, outline.Advance * f);
        }

        public GlyphOutline Outline(int glyph, double advance, Action<long> charge)
            => _outlines.TryGetValue(glyph, out GlyphOutline outline) ? new GlyphOutline(outline.Segments, advance) : null;

        private PdfStream _stream;
        private int _streamCount = -1;

        /// <summary>
        /// The <c>/FontFile3</c> of <c>/CIDFontType0C</c> holding the glyphs added so far — one instance
        /// until another is added — or null, said, when the CFF cannot be written.
        /// </summary>
        public PdfStream ProgramStream()
        {
            if (_stream != null && _streamCount == _builder.Count) return _stream;
            byte[] cff = _builder.Build(out string reason);
            if (cff == null)
            {
                PostScriptDiagnostics.Report("PsCidType0Font", "a Type 0 CIDFont's glyphs could not be written as a CFF: " + reason);
                return null;
            }
            _stream = new PdfStream { RawData = cff };
            _stream[PdfName.Subtype] = PdfName.CIDFontType0C;
            _streamCount = _builder.Count;
            return _stream;
        }

        public (double XMin, double YMin, double XMax, double YMax, double Ascent, double Descent) DescriptorMetrics() => _metrics;

        private static bool Int(PsDict dict, string key, out int value)
        {
            value = 0;
            if (!dict.TryGetName(key, out var v) || v.Type != PsType.Integer) return false;
            value = v.AsInt();
            return true;
        }

        /// <summary>The scale of a <c>[s 0 0 s 0 0]</c> matrix, NaN for any other.</summary>
        private static double UniformScale(PsObject matrix)
        {
            if (matrix.Type != PsType.Array || matrix.AsArray().Length != 6) return double.NaN;
            PsArray m = matrix.AsArray();
            for (int k = 0; k < 6; k++) if (!m[k].IsNumber) return double.NaN;
            return m[1].Num == 0 && m[2].Num == 0 && m[4].Num == 0 && m[5].Num == 0 && m[0].Num == m[3].Num ? m[0].Num : double.NaN;
        }

        private static long ReadOffset(byte[] data, long at, int bytes)
        {
            long value = 0;
            for (int k = 0; k < bytes; k++) value = (value << 8) | data[at + k];
            return value;
        }

        private static IEnumerable<PsObject> Items(PsArray array)
        {
            for (int k = 0; k < array.Length; k++) yield return array[k];
        }
    }
}
