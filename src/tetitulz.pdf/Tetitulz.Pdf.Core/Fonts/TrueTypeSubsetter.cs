// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// Subsets a TrueType font to only the glyph ids actually used, for embedding a
    /// composite (CIDFontType2) font at a fraction of the full-font size. Takes glyph
    /// ids directly (the caller already mapped Unicode→GID), drops the unneeded cmap,
    /// and returns the original-GID→new-GID map so the caller can build a
    /// <c>/CIDToGIDMap</c> stream (keeping the content stream's original glyph ids as CIDs).
    /// TrueType outlines only (a <c>glyf</c> table); returns null for CFF/OTTO.
    /// </summary>
    internal static class TrueTypeSubsetter
    {
        [ThreadStatic] private static string _lastDecline;

        /// <summary>
        /// Why the most recent <see cref="Subset"/> <b>on this thread</b> returned nothing, or null
        /// when it produced a subset.
        /// </summary>
        /// <remarks>
        /// A <c>(null, null)</c> return means "embed the whole program", and the callers do exactly
        /// that — but it covers both "this font has no <c>glyf</c> table" and "this font's licence
        /// forbids subsetting", and the second is something a caller may need to explain to someone.
        /// Set on every declining path, cleared on every succeeding one.
        /// </remarks>
        internal static string LastDeclineReason => _lastDecline;

        public static (byte[] subset, Dictionary<int, int> oldToNew) Subset(byte[] src, IEnumerable<ushort> usedGids)
        {
            _lastDecline = null;
            try
            {
                var result = new Engine(src).Build(usedGids, out string decline);
                _lastDecline = decline;
                return result;
            }
            catch (Exception ex)
            {
                _lastDecline = "the TrueType subsetter threw (" + ex.Message + ")";
                return (null, null);
            }
        }

        private sealed class Engine
        {
            private readonly byte[] _src;
            private readonly Dictionary<string, (uint off, uint len)> _tables =
                new Dictionary<string, (uint, uint)>(StringComparer.Ordinal);
            private byte[] _glyf;
            private long[] _loca;
            private int _numGlyphs, _indexToLoc, _numberOfHMetrics;
            private ushort[] _adv;
            private short[] _lsb;
            private List<int> _selected;
            private Dictionary<int, int> _oldToNew;

            internal Engine(byte[] src)
            {
                _src = src;

                // A 'ttcf' header puts majorVersion (1) where numTables goes, so without this the
                // loop below reads one table named by four bytes of a face offset. It happens to
                // end at "no glyf table" here, which is the right refusal reached for the wrong
                // reason; SfntTableDirectory owns the signature test the whole tree shares.
                if (SfntTableDirectory.IsCollection(src))
                    throw new NotSupportedException(SfntTableDirectory.CollectionRefusal + ".");

                var r = new R(src, 0);
                r.U32(); int n = r.U16(); r.Skip(6);
                for (int i = 0; i < n; i++)
                {
                    string tag = r.Tag(); r.U32(); uint off = r.U32(); uint len = r.U32();

                    // A table's offset and length are read straight out of the directory and are
                    // therefore whatever the file says. Unchecked, `_glyf = new byte[gLen]` in
                    // ParseLoca allocates up to 4 GiB from a crafted length BEFORE the Array.Copy
                    // that would have failed - the allocation is the attack, and a blanket catch
                    // one frame up only sees it afterwards. Every other table is read through
                    // `new R(_src, (int)off)`, where an out-of-range offset also truncates through
                    // the (int) cast.
                    //
                    // Out-of-range tables are dropped here and named in Build's decline, so the
                    // return-null contract is kept: a caller that hands this a font it cannot
                    // handle still gets "embed the whole program", which is correct and lossless.
                    if (off > (uint)src.Length || len > (uint)src.Length - off)
                    {
                        _outOfRange.Add(tag);
                        continue;
                    }

                    _tables[tag] = (off, len);
                }
            }

            /// <summary>Tags whose directory entry does not fit inside the file.</summary>
            private readonly List<string> _outOfRange = new List<string>();

            /// <param name="usedGids">The glyphs to keep.</param>
            /// <param name="decline">Why nothing was produced, or null on success.</param>
            /// <remarks>
            /// <b>The <c>OS/2</c> <c>fsType</c> "no subsetting" bit is honoured by declining, not by
            /// a second code path.</b> Honouring it means embedding the WHOLE program, and that is
            /// precisely what a declining return already produces at every call site: the caller
            /// falls back to the full font <i>and</i> writes no <c>ABCDEF+</c> subset tag, since that
            /// tag is a claim that only some of the glyphs are present. The check reads the table
            /// directory this engine has already walked, so it costs no extra read.
            /// </remarks>
            internal (byte[], Dictionary<int, int>) Build(IEnumerable<ushort> usedGids, out string decline)
            {
                decline = null;

                if (_outOfRange.Count > 0)
                {
                    decline = "the font's table directory places " +
                              string.Join(", ", _outOfRange) + " outside the file";
                    return (null, null);
                }

                if (_tables.TryGetValue("OS/2", out var os2))
                {
                    var licence = FontEmbeddingLicense.FromOs2Table(_src, os2.off);
                    if (licence.SubsettingProhibited)
                    {
                        decline = licence.Describe();
                        return (null, null);
                    }
                }

                if (!_tables.ContainsKey("glyf") || !_tables.ContainsKey("loca"))
                {
                    decline = "the font has no glyf/loca outlines to subset";
                    return (null, null);
                }
                ParseHead(); ParseMaxp(); ParseHhea(); ParseLoca(); ParseHmtx();

                // A directory that fits the file still says nothing about the values INSIDE loca.
                // Those index glyf, and `Components` reaches them as `new R(_glyf, (int)s)` - so an
                // offset past the end of glyf truncates through the (int) cast and reads a
                // perfectly valid but completely different glyph rather than failing. §5.3.4 makes
                // loca monotonically non-decreasing and its last entry the length of glyf, so both
                // are checkable, and violating either means the file is crafted or damaged.
                if (!LocaIsWellFormed(out string locaFault))
                {
                    decline = locaFault;
                    return (null, null);
                }

                var needed = new HashSet<int> { 0 };
                foreach (var g in usedGids) if (g < _numGlyphs) needed.Add(g);
                var q = new Queue<int>(needed);
                while (q.Count > 0)
                    foreach (int comp in Components(q.Dequeue()))
                        if (needed.Add(comp)) q.Enqueue(comp);

                _selected = needed.Where(g => g < _numGlyphs).OrderBy(g => g).ToList();
                _oldToNew = new Dictionary<int, int>();
                for (int i = 0; i < _selected.Count; i++) _oldToNew[_selected[i]] = i;

                return (Assemble(), _oldToNew);
            }

            private void ParseHead() { var r = new R(_src, (int)_tables["head"].off); r.Skip(18); r.U16(); r.Skip(30); _indexToLoc = r.I16(); }
            private void ParseMaxp() { var r = new R(_src, (int)_tables["maxp"].off); r.Skip(4); _numGlyphs = r.U16(); }
            private void ParseHhea() { var r = new R(_src, (int)_tables["hhea"].off); r.Skip(34); _numberOfHMetrics = r.U16(); }

            private void ParseLoca()
            {
                var (off, _) = _tables["loca"];
                _loca = new long[_numGlyphs + 1];
                var r = new R(_src, (int)off);
                if (_indexToLoc == 0) for (int i = 0; i <= _numGlyphs; i++) _loca[i] = r.U16() * 2L;
                else for (int i = 0; i <= _numGlyphs; i++) _loca[i] = r.U32();
                var (gOff, gLen) = _tables["glyf"];
                _glyf = new byte[gLen];
                Array.Copy(_src, (int)gOff, _glyf, 0, (int)gLen);
            }

            /// <summary>
            /// Whether every <c>loca</c> entry is a usable index into <c>glyf</c>.
            /// </summary>
            /// <remarks>
            /// <b>Non-decreasing, and ending no later than the end of <c>glyf</c>.</b> A glyph's
            /// data is <c>loca[i]</c> to <c>loca[i+1]</c>, so a decreasing pair is a negative
            /// length and an entry past the table is a read into whatever follows. The last entry
            /// is allowed to be short of <c>glyf</c>'s end — real fonts pad — but never beyond it.
            /// </remarks>
            private bool LocaIsWellFormed(out string fault)
            {
                fault = null;
                long limit = _glyf.LongLength;

                for (int i = 0; i <= _numGlyphs; i++)
                {
                    if (_loca[i] > limit)
                    {
                        fault = $"the font's loca table points glyph {i} at offset {_loca[i]}, " +
                                $"past the {limit}-byte glyf table";
                        return false;
                    }

                    if (i > 0 && _loca[i] < _loca[i - 1])
                    {
                        fault = $"the font's loca table decreases at glyph {i} " +
                                $"({_loca[i - 1]} then {_loca[i]}), so that glyph has a negative length";
                        return false;
                    }
                }

                return true;
            }

            private void ParseHmtx()
            {
                var r = new R(_src, (int)_tables["hmtx"].off);
                _adv = new ushort[_numGlyphs]; _lsb = new short[_numGlyphs];
                ushort last = 0;
                for (int i = 0; i < _numGlyphs; i++)
                {
                    if (i < _numberOfHMetrics) { last = (ushort)r.U16(); _adv[i] = last; _lsb[i] = (short)r.I16(); }
                    else { _adv[i] = last; _lsb[i] = (short)r.I16(); }
                }
            }

            private IEnumerable<int> Components(int glyph)
            {
                if (glyph >= _numGlyphs) yield break;
                long s = _loca[glyph], e = _loca[glyph + 1];
                if (e <= s) yield break;
                // Bounded by the glyph's own end (CompositeGlyph's remarks), so a malformed record that
                // keeps MORE_COMPONENTS set cannot read on into the next glyph.
                foreach (int at in CompositeGlyph.ComponentIndexOffsets(_glyf, (int)s, (int)e))
                    yield return CompositeGlyph.ReadIndex(_glyf, at);
            }

            private byte[] Assemble()
            {
                int newN = _selected.Count;
                var glyfMs = new MemoryStream();
                var newLoca = new long[newN + 1];
                for (int ni = 0; ni < newN; ni++)
                {
                    newLoca[ni] = glyfMs.Length;
                    int old = _selected[ni];
                    long s = _loca[old], e = _loca[old + 1];
                    if (e > s)
                    {
                        var gb = new byte[e - s];
                        Array.Copy(_glyf, (int)s, gb, 0, gb.Length);
                        RemapComposite(gb);
                        glyfMs.Write(gb, 0, gb.Length);
                        while (glyfMs.Length % 4 != 0) glyfMs.WriteByte(0);
                    }
                }
                newLoca[newN] = glyfMs.Length;
                byte[] glyf = glyfMs.ToArray();
                int locaFmt = newLoca[newN] > 0x1FFFE ? 1 : 0;

                var hmtxMs = new MemoryStream(); var hw = new W(hmtxMs);
                for (int ni = 0; ni < newN; ni++)
                {
                    int old = _selected[ni];
                    hw.U16(_adv[Math.Min(old, _adv.Length - 1)]);
                    hw.I16(_lsb[Math.Min(old, _lsb.Length - 1)]);
                }

                var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal)
                {
                    ["glyf"] = glyf,
                    ["loca"] = BuildLoca(newLoca, locaFmt),
                    ["hmtx"] = hmtxMs.ToArray(),
                    ["head"] = Patch("head", h => WriteI16(h, 50, (short)locaFmt)),
                    ["hhea"] = Patch("hhea", h => WriteU16(h, 34, (ushort)newN)),
                    ["maxp"] = Patch("maxp", h => WriteU16(h, 4, (ushort)newN)),
                };
                foreach (var tag in new[] { "OS/2", "cvt ", "fpgm", "prep", "name", "post" })
                    if (_tables.TryGetValue(tag, out var t))
                    {
                        var b = new byte[t.len]; Array.Copy(_src, (int)t.off, b, 0, (int)t.len); tables[tag] = b;
                    }

                return WriteSfnt(tables);
            }

            private void RemapComposite(byte[] g)
            {
                foreach (int at in CompositeGlyph.ComponentIndexOffsets(g, 0, g.Length))
                {
                    int oldId = CompositeGlyph.ReadIndex(g, at);
                    CompositeGlyph.WriteIndex(g, at, _oldToNew.TryGetValue(oldId, out int nid) ? nid : 0);
                }
            }

            private byte[] Patch(string tag, Action<byte[]> patch)
            {
                var t = _tables[tag]; var b = new byte[t.len];
                Array.Copy(_src, (int)t.off, b, 0, (int)t.len);
                if (tag == "head") WriteU32(b, 8, 0); // checkSumAdjustment reset
                patch(b);
                return b;
            }

            private static byte[] BuildLoca(long[] offs, int fmt)
            {
                var ms = new MemoryStream(); var w = new W(ms);
                if (fmt == 0) foreach (var o in offs) w.U16((ushort)(o / 2));
                else foreach (var o in offs) w.U32((uint)o);
                return ms.ToArray();
            }

            /// <summary>
            /// Lays the subsetted tables out as a TrueType container, through
            /// <see cref="SfntBuilder"/> — the single sfnt assembler.
            /// </summary>
            /// <remarks>
            /// The builder orders the tags ordinally, derives the directory hints by integer
            /// arithmetic and takes the sfnt version from its caller, so the CFF path shares it.
            /// <c>Patch</c> already zeroes <c>head.checkSumAdjustment</c>; the builder zeroes it again
            /// before summing, so it is belt-and-braces rather than a dependency.
            /// </remarks>
            private static byte[] WriteSfnt(Dictionary<string, byte[]> tables)
            {
                var list = new List<(string tag, byte[] data)>(tables.Count);
                foreach (var kvp in tables) list.Add((kvp.Key, kvp.Value));
                return SfntBuilder.Build(SfntBuilder.VersionTrueType, list);
            }

            private static int ReadU16(byte[] d, int p) => IO.BigEndian.U16(d, p);
            private static void WriteU16(byte[] d, int p, ushort v) => IO.BigEndian.Put16(d, p, v);
            private static void WriteI16(byte[] d, int p, short v) => WriteU16(d, p, (ushort)v);
            private static void WriteU32(byte[] d, int p, uint v) => IO.BigEndian.Put32(d, p, v);
        }

        private sealed class R
        {
            private readonly byte[] _d; private int _p;
            public R(byte[] d, int p) { _d = d; _p = p; }
            public uint U32() { uint v = IO.BigEndian.U32(_d, _p); _p += 4; return v; }
            public int U16() { int v = IO.BigEndian.U16(_d, _p); _p += 2; return v; }
            public short I16() { short v = (short)((_d[_p] << 8) | _d[_p + 1]); _p += 2; return v; }
            public void Skip(int n) => _p += n;
            public string Tag() { var s = Encoding.ASCII.GetString(_d, _p, 4); _p += 4; return s; }
        }

        private sealed class W
        {
            private readonly Stream _s; public W(Stream s) { _s = s; }
            public void U16(ushort v) { _s.WriteByte((byte)(v >> 8)); _s.WriteByte((byte)v); }
            public void I16(short v) => U16((ushort)v);
            public void U32(uint v) { _s.WriteByte((byte)(v >> 24)); _s.WriteByte((byte)(v >> 16)); _s.WriteByte((byte)(v >> 8)); _s.WriteByte((byte)v); }
        }
    }
}
