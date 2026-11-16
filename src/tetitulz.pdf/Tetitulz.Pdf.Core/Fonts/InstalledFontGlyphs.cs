// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.IO;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// The text of a TrueType program's glyphs, recovered from the installed font the program was
    /// taken from. A print driver that downloads a font as glyphs alone — the Microsoft PS Class
    /// Driver writes a Type 2 CIDFont under a made-up collection, with no <c>cmap</c>, no <c>post</c>
    /// and no glyph names — keeps the installed font's glyph numbers, its <c>head</c> and each glyph's
    /// bytes, so the font it came from can be found on the host and its <c>cmap</c> read backwards.
    ///
    /// <para><b>Found by evidence, never by name.</b> A candidate is an installed face whose
    /// <c>head</c> agrees with the program's in every field a copy keeps — all but
    /// <c>checkSumAdjustment</c>, <c>indexToLocFormat</c> and <c>glyphDataFormat</c>, which a program
    /// assembled again recomputes — and a glyph's text is taken from a candidate only when that face's
    /// glyph of the same number has the same bytes (a few zero bytes of padding aside). A font whose
    /// installed copy differs, or that is not installed, gives no text.</para>
    ///
    /// <para>The installed fonts are indexed by their <c>head</c> once a process — only each file's
    /// table directory and <c>head</c> are read — and again when the search path changes
    /// (<see cref="FontSearchPath"/>). A face is read whole only when it is a candidate, under
    /// <see cref="PdfLimits.MaxFontProgramBytes"/>. Thread-safe.</para>
    /// </summary>
    public sealed class InstalledFontGlyphs
    {
        private const int HeadLength = 54;
        private const int MaxFacesPerCollection = 256;
        private const int MaxTables = 512;
        private const int MaxLoadedFaces = 16;

        private static readonly object Gate = new object();
        private static IReadOnlyList<string> _indexedFiles;
        private static Dictionary<string, List<Face>> _index;
        private static readonly Dictionary<Face, LoadedFace> Loaded = new Dictionary<Face, LoadedFace>();

        private readonly List<Face> _candidates;

        private InstalledFontGlyphs(List<Face> candidates) => _candidates = candidates;

        /// <summary>How many installed faces agree with the program's <c>head</c>.</summary>
        public int CandidateCount => _candidates.Count;

        /// <summary>
        /// The installed faces <paramref name="head"/> — a program's <c>head</c> table — could have
        /// been taken from, or null when none is installed.
        /// </summary>
        public static InstalledFontGlyphs TryIdentify(byte[] head)
        {
            if (head == null || head.Length < HeadLength) return null;
            string signature = Signature(head, 0);
            lock (Gate)
            {
                EnsureIndex();
                return _index.TryGetValue(signature, out List<Face> faces) ? new InstalledFontGlyphs(new List<Face>(faces)) : null;
            }
        }

        /// <summary>
        /// The text glyph <paramref name="glyph"/> stands for — the lowest code point the installed
        /// face's <c>cmap</c> maps to it, private-use and control characters left out — when the
        /// installed glyph's bytes are <paramref name="glyphData"/>; null otherwise.
        /// </summary>
        public string TextOf(int glyph, byte[] glyphData)
        {
            if (glyph < 0 || glyphData == null) return null;
            lock (Gate)
            {
                for (int k = 0; k < _candidates.Count; k++)
                {
                    LoadedFace face = Load(_candidates[k]);
                    if (face == null || !face.HasGlyph(glyph, glyphData)) continue;
                    if (k > 0)
                    {
                        Face found = _candidates[k];
                        _candidates.RemoveAt(k);
                        _candidates.Insert(0, found);
                    }
                    return face.TextOf(glyph);
                }
            }
            return null;
        }

        /// <summary>
        /// Glyph <paramref name="glyph"/>'s description in a program's own <c>glyf</c>, located by its
        /// <c>loca</c> (<paramref name="indexToLocFormat"/> 0: short, else long) — the bytes
        /// <see cref="TextOf"/> compares — or null where <c>loca</c> does not locate it in <c>glyf</c>.
        /// </summary>
        public static byte[] GlyphOf(byte[] loca, byte[] glyf, int indexToLocFormat, int glyph)
        {
            if (loca == null || glyf == null || glyph < 0) return null;
            int entry = indexToLocFormat == 0 ? 2 : 4;
            if ((long)(glyph + 2) * entry > loca.Length) return null;
            long start = EmbeddedGlyphSets.ReadLoca(loca, glyph * entry, indexToLocFormat);
            long end = EmbeddedGlyphSets.ReadLoca(loca, (glyph + 1) * entry, indexToLocFormat);
            if (start > end || end > glyf.Length) return null;
            var bytes = new byte[end - start];
            Array.Copy(glyf, start, bytes, 0, bytes.Length);
            return bytes;
        }

        // ------------------------------------------------------------------
        //  The index: each installed face by its head
        // ------------------------------------------------------------------

        /// <summary>The <c>head</c> fields a copy of the program keeps: bytes 0–7 and 12–49.</summary>
        private static string Signature(byte[] data, int at)
        {
            var hex = new char[2 * (8 + 38)];
            int n = 0;
            for (int k = 0; k < 50; k++)
            {
                if (k >= 8 && k < 12) continue;
                byte b = data[at + k];
                hex[n++] = "0123456789ABCDEF"[b >> 4];
                hex[n++] = "0123456789ABCDEF"[b & 15];
            }
            return new string(hex);
        }

        private static void EnsureIndex()
        {
            IReadOnlyList<string> files = FontFileLocator.EnumerateFontFiles(includeOpenType: false);
            if (_index != null && ReferenceEquals(files, _indexedFiles)) return;
            var index = new Dictionary<string, List<Face>>(StringComparer.Ordinal);
            foreach (string path in files)
                foreach (var (offset, signature) in Heads(path))
                {
                    if (!index.TryGetValue(signature, out List<Face> faces)) index[signature] = faces = new List<Face>();
                    faces.Add(new Face(path, offset));
                }
            _index = index;
            _indexedFiles = files;
            Loaded.Clear();
        }

        /// <summary>
        /// Each face's table directory offset and <c>head</c> signature in <paramref name="path"/>: one
        /// for a font, one a face for a collection. A file this process cannot read gives none — an
        /// installed file is the host's, not the document's to report.
        /// </summary>
        private static List<(int Offset, string Signature)> Heads(string path)
        {
            var heads = new List<(int, string)>();
            try
            {
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    byte[] header = ReadAt(file, 0, 12);
                    if (header == null) return heads;
                    if (BigEndian.U32(header, 0) == SfntTableDirectory.VersionCollection)
                    {
                        long faces = BigEndian.U32(header, 8);
                        if (faces < 1 || faces > MaxFacesPerCollection) return heads;
                        byte[] offsets = ReadAt(file, 12, (int)faces * 4);
                        if (offsets == null) return heads;
                        for (int k = 0; k < faces; k++)
                        {
                            long offset = BigEndian.U32(offsets, 4 * k);
                            string signature = offset <= int.MaxValue ? HeadSignature(file, (int)offset) : null;
                            if (signature != null) heads.Add(((int)offset, signature));
                        }
                    }
                    else
                    {
                        string signature = HeadSignature(file, 0);
                        if (signature != null) heads.Add((0, signature));
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return heads;
        }

        private static string HeadSignature(FileStream file, int directory)
        {
            byte[] header = ReadAt(file, directory, 12);
            if (header == null) return null;
            uint version = BigEndian.U32(header, 0);
            if (version != 0x00010000u && version != SfntTableDirectory.VersionAppleTrueType) return null;
            int tables = BigEndian.U16(header, 4);
            if (tables < 1 || tables > MaxTables) return null;
            byte[] records = ReadAt(file, directory + 12L, tables * 16);
            if (records == null) return null;
            for (int k = 0; k < tables; k++)
            {
                int r = 16 * k;
                if (records[r] != 'h' || records[r + 1] != 'e' || records[r + 2] != 'a' || records[r + 3] != 'd') continue;
                long offset = BigEndian.U32(records, r + 8);
                if (BigEndian.U32(records, r + 12) < HeadLength) return null;
                byte[] head = ReadAt(file, offset, HeadLength);
                return head == null ? null : Signature(head, 0);
            }
            return null;
        }

        private static byte[] ReadAt(FileStream file, long offset, int count)
        {
            if (offset < 0 || offset + count > file.Length) return null;
            file.Position = offset;
            var bytes = new byte[count];
            return StreamBytes.Fill(file, bytes, 0, count) == count ? bytes : null;
        }

        // ------------------------------------------------------------------
        //  A candidate face, read whole
        // ------------------------------------------------------------------

        private static LoadedFace Load(Face face)
        {
            if (Loaded.TryGetValue(face, out LoadedFace loaded)) return loaded;
            if (Loaded.Count >= MaxLoadedFaces) Loaded.Clear();
            loaded = LoadedFace.TryRead(face);
            Loaded[face] = loaded;
            return loaded;
        }

        private readonly struct Face : IEquatable<Face>
        {
            public Face(string path, int directory)
            {
                Path = path;
                Directory = directory;
            }

            public string Path { get; }

            /// <summary>The face's table directory: 0 for a font file, the collection's offset for a face of one.</summary>
            public int Directory { get; }

            public bool Equals(Face other) => Directory == other.Directory && string.Equals(Path, other.Path, StringComparison.Ordinal);
            public override bool Equals(object obj) => obj is Face other && Equals(other);
            public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Path) * 31 + Directory;
        }

        private sealed class LoadedFace
        {
            private readonly byte[] _font;
            private readonly int _loca;
            private readonly int _glyf;
            private readonly int _glyfLength;
            private readonly int _indexToLoc;
            private readonly int _glyphs;
            private readonly TrueTypeFontProgram _program;
            private Dictionary<int, int> _codePoints;

            private LoadedFace(byte[] font, int loca, int glyf, int glyfLength, int indexToLoc, int glyphs, TrueTypeFontProgram program)
            {
                _font = font;
                _loca = loca;
                _glyf = glyf;
                _glyfLength = glyfLength;
                _indexToLoc = indexToLoc;
                _glyphs = glyphs;
                _program = program;
            }

            /// <summary>The face as a font of its own — a collection's face assembled from its tables — or null when it cannot be read.</summary>
            public static LoadedFace TryRead(Face face)
            {
                byte[] file;
                try
                {
                    var info = new FileInfo(face.Path);
                    if (info.Length > PdfLimits.MaxFontProgramBytes) return null;
                    file = File.ReadAllBytes(face.Path);
                }
                catch (IOException) { return null; }
                catch (UnauthorizedAccessException) { return null; }

                byte[] font = face.Directory == 0 ? file : Standalone(file, face.Directory);
                if (font == null || !SfntTableDirectory.TryRead(font, out IReadOnlyList<SfntTableDirectory.TableRecord> tables, out _))
                    return null;
                SfntTableDirectory.TableRecord? head = null, maxp = null, loca = null, glyf = null;
                foreach (SfntTableDirectory.TableRecord t in tables)
                {
                    if (t.Tag == "head") head = t;
                    else if (t.Tag == "maxp") maxp = t;
                    else if (t.Tag == "loca") loca = t;
                    else if (t.Tag == "glyf") glyf = t;
                }
                if (head == null || maxp == null || loca == null || glyf == null || head.Value.Length < HeadLength || maxp.Value.Length < 6)
                    return null;
                int indexToLoc = (short)BigEndian.U16(font, head.Value.Offset + 50);
                int glyphs = BigEndian.U16(font, maxp.Value.Offset + 4);
                if ((long)(glyphs + 1) * (indexToLoc == 0 ? 2 : 4) > loca.Value.Length) return null;
                TrueTypeFontProgram program = TrueTypeFontProgram.Parse(font);
                if (program == null) return null;
                return new LoadedFace(font, loca.Value.Offset, glyf.Value.Offset, glyf.Value.Length, indexToLoc, glyphs, program);
            }

            /// <summary>A collection's face as a font: its tables, whose offsets are the file's, laid out afresh.</summary>
            private static byte[] Standalone(byte[] file, int directory)
            {
                if (directory + 12L > file.Length) return null;
                int count = BigEndian.U16(file, directory + 4);
                if (count < 1 || count > MaxTables || directory + 12L + 16L * count > file.Length) return null;
                var tables = new List<(string, byte[])>();
                for (int k = 0; k < count; k++)
                {
                    int r = directory + 12 + 16 * k;
                    long offset = BigEndian.U32(file, r + 8);
                    long length = BigEndian.U32(file, r + 12);
                    if (offset + length > file.Length) return null;
                    var payload = new byte[length];
                    Array.Copy(file, offset, payload, 0, length);
                    tables.Add((Text.Latin1Text.Decode(file, r, 4), payload));
                }
                return SfntBuilder.Build(BigEndian.U32(file, directory), tables);
            }

            /// <summary>Whether the face's glyph <paramref name="glyph"/> is <paramref name="data"/>, up to three zero bytes of padding on either side.</summary>
            public bool HasGlyph(int glyph, byte[] data)
            {
                if (glyph >= _glyphs) return false;
                int entry = _indexToLoc == 0 ? 2 : 4;
                long start = EmbeddedGlyphSets.ReadLoca(_font, _loca + glyph * entry, _indexToLoc);
                long end = EmbeddedGlyphSets.ReadLoca(_font, _loca + (glyph + 1) * entry, _indexToLoc);
                if (start > end || end > _glyfLength) return false;
                int installed = (int)(end - start);
                int common = Math.Min(installed, data.Length);
                if (Math.Abs(installed - data.Length) > 3) return false;
                int at = _glyf + (int)start;
                for (int k = 0; k < common; k++)
                    if (_font[at + k] != data[k]) return false;
                for (int k = common; k < installed; k++)
                    if (_font[at + k] != 0) return false;
                for (int k = common; k < data.Length; k++)
                    if (data[k] != 0) return false;
                return true;
            }

            public string TextOf(int glyph)
            {
                if (_codePoints == null)
                {
                    _codePoints = new Dictionary<int, int>();
                    foreach (KeyValuePair<int, int> mapping in _program.CodePointGlyphs)
                    {
                        if (!IsText(mapping.Key)) continue;
                        if (!_codePoints.TryGetValue(mapping.Value, out int known) || mapping.Key < known)
                            _codePoints[mapping.Value] = mapping.Key;
                    }
                }
                return _codePoints.TryGetValue(glyph, out int codePoint) ? TextFor(codePoint) : null;
            }

            /// <summary>
            /// The text a code point stands for. A presentation form — the glyph a driver shaped, which
            /// the font's <c>cmap</c> reaches only through it — is its letters: a Hebrew letter and
            /// point, a lam-alef, an Arabic letter's initial, medial or final form (Unicode's
            /// compatibility decomposition), so the text searches as typed.
            /// </summary>
            private static string TextFor(int codePoint)
            {
                string letters = Text.HebrewPresentationForms.Decomposition(codePoint) ?? Text.ArabicLigatures.Decomposition(codePoint);
                if (letters != null) return letters;
                string text = char.ConvertFromUtf32(codePoint);
                if ((codePoint >= 0xFB50 && codePoint <= 0xFDFF) || (codePoint >= 0xFE70 && codePoint <= 0xFEFF))
                {
                    string compatible = text.Normalize(System.Text.NormalizationForm.FormKC);
                    if (compatible.Length > 0) return compatible;
                }
                return text;
            }

            /// <summary>A code point text can be read as: not a control character, a surrogate or a private-use one.</summary>
            private static bool IsText(int codePoint)
                => codePoint >= 0x20 && !(codePoint >= 0x7F && codePoint <= 0x9F)
                   && !(codePoint >= 0xD800 && codePoint <= 0xF8FF) && codePoint < 0xF0000 && codePoint <= 0x10FFFF;
        }
    }
}
