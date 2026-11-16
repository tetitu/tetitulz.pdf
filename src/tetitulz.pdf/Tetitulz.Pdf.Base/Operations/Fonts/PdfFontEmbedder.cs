// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Metadata;

namespace Tetitulz.Pdf.Base.Operations.Fonts
{
    /// <summary>
    /// Embeds a TrueType or OpenType (CFF) font into an existing PDF document (gaps 11.2 / 11.3).
    ///
    /// <para>
    /// The embedded font is added as an indirect object set and registered in the specified
    /// page's /Resources /Font dictionary. The returned resource name can be used directly
    /// in PDF content stream operators (e.g. <c>/F1 12 Tf</c>).
    /// </para>
    ///
    /// <para>
    /// TrueType metrics are extracted by parsing the minimal required tables:
    /// <c>head</c>, <c>hhea</c>, <c>name</c> (PostScript name), <c>cmap</c> (format 4),
    /// and <c>hmtx</c> (advance widths). The /Widths array covers characters 32–255
    /// using WinAnsiEncoding.
    /// </para>
    ///
    /// <para>
    /// OpenType fonts with CFF outlines (<c>CFF </c> table present) are embedded as
    /// /FontFile3 with /Subtype /OpenType. TrueType fonts are embedded as /FontFile2.
    /// </para>
    /// </summary>
    public static class PdfFontEmbedder
    {
        // ----------------------------------------------------------------
        //  Public API
        // ----------------------------------------------------------------

        /// <summary>
        /// Embeds a TrueType/OpenType font into all pages of the document.
        /// The font is added to each page's /Resources /Font dictionary.
        /// </summary>
        /// <param name="input">Source PDF stream.</param>
        /// <param name="output">Output PDF stream.</param>
        /// <param name="fontBytes">Raw TTF/OTF font file bytes.</param>
        /// <param name="resourceName">
        ///   The PDF font resource name to use (e.g. "F1"). Must be unique within the page's /Font dict.
        ///   Use this name in content streams: <c>/{resourceName} 12 Tf</c>.
        /// </param>
        /// <exception cref="ArgumentException">
        /// A page already has a font resource named <paramref name="resourceName"/>.
        /// </exception>
        /// <exception cref="Core.PdfEncryptedDocumentException">The document is encrypted.</exception>
        /// <exception cref="Core.PdfException">A page has no object number to revise.</exception>
        public static void EmbedFont(Stream input, Stream output, byte[] fontBytes, string resourceName)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (fontBytes == null || fontBytes.Length == 0) throw new ArgumentNullException(nameof(fontBytes));
            if (string.IsNullOrEmpty(resourceName)) throw new ArgumentNullException(nameof(resourceName));
            RefuseCollection(fontBytes);

            var metrics = TrueTypeMetrics.Parse(fontBytes);

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);

            // Refused before anything is built. An encrypted document loads no catalog, so there
            // were no pages to update: the font objects went into the file, in clear text where the
            // reader expects ciphertext, attached to nothing, and the call returned normally.
            if (doc.IsEncrypted)
                throw new Core.PdfEncryptedDocumentException(
                    "Cannot embed a font into an encrypted document: the font program and its " +
                    "dictionaries would be written in clear text where the reader expects per-object " +
                    "ciphertext, and this assembly cannot encrypt. Decrypt first " +
                    "(Tetitulz.Pdf.Encryption.PdfDecryptor), embed, then re-encrypt — or use " +
                    "Tetitulz.Pdf.Encryption.PdfEncryptedRoundTrip.Run.");

            int maxObj = doc.CrossReferenceTable.GetMaxObjectNumber();
            int nextObjNum = maxObj + 1;
            var objects = new List<IndirectObject>();

            // Build the font objects once — shared across all pages.
            var (fontRef, fontObjects) = BuildFontObjects(fontBytes, metrics, ref nextObjNum);
            objects.AddRange(fontObjects);

            // Add font resource to every page.
            for (int pi = 0; pi < doc.Pages.Count; pi++)
            {
                var page = doc.Pages[pi];
                var updatedPage = PdfPageContentAppender.CloneDictionary(page.Dictionary);

                // Clone or create /Resources and /Font sub-dict — refusing either when the parser
                // could not read it: rebuilt from nothing, the page was left with only the new font,
                // and its own fonts and images went.
                RefuseUnreadable(doc, page.Dictionary[PdfName.Resources], pi, "/Resources");
                var existingRes = page.Resources;
                PdfDictionary resourcesDict = existingRes != null
                    ? PdfPageContentAppender.CloneDictionary(existingRes)
                    : new PdfDictionary();

                PdfDictionary fontDict;
                var fontDictObj = resourcesDict[PdfName.Font];
                RefuseUnreadable(doc, fontDictObj, pi, "/Font");
                if (fontDictObj != null)
                    fontDict = (doc.ResolveObject(fontDictObj) as PdfDictionary
                               ?? fontDictObj as PdfDictionary
                               ?? new PdfDictionary()).Clone();
                else
                    fontDict = new PdfDictionary();

                // The name must be free on every page: set over an existing entry, it would repoint
                // that page's text in the old font at this one, silently re-rendering it in a font
                // it was never drawn with.
                var name = new PdfName(resourceName);
                if (fontDict[name] != null)
                    throw new ArgumentException(
                        $"Page {pi} already has a font resource named /{resourceName}; embedding under " +
                        "that name would re-render its existing text in the new font. Choose a name no " +
                        "page uses.",
                        nameof(resourceName));

                // A page with no object number cannot be superseded: written as object 0, the
                // revision's entry would name the head of the free list.
                int pageObjNum = page.Dictionary.ObjectNumber;
                if (pageObjNum <= 0)
                    throw new Core.PdfException(
                        $"Page {pi} has no object number, so it cannot be updated by an incremental write.");

                fontDict.Set(name, fontRef);
                resourcesDict.Set(PdfName.Font, fontDict);
                updatedPage.Set(PdfName.Resources, resourcesDict);
                objects.Add(new IndirectObject(pageObjNum, 0, updatedPage));
            }

            int newSize = nextObjNum;
            PdfPageContentAppender.AppendRevision(ms, output, doc, objects, newSize);
        }

        private static void RefuseUnreadable(PdfDocument doc, PdfObject value, int pageIndex, string what)
        {
            EntryRead.Resolve(doc.ResolveObject, value, out string unreadable);
            if (unreadable != null)
                throw new Core.PdfException(
                    $"Page {pageIndex}'s {what} could not be read ({unreadable}), so embedding the font would " +
                    "replace it with one holding only the new font. No changes were written.");
        }

        /// <summary>
        /// Parses a TrueType/OpenType font file and returns its metrics without modifying any PDF.
        /// </summary>
        public static TrueTypeMetrics GetFontMetrics(byte[] fontBytes)
        {
            if (fontBytes == null || fontBytes.Length == 0) throw new ArgumentNullException(nameof(fontBytes));
            RefuseCollection(fontBytes);
            return TrueTypeMetrics.Parse(fontBytes);
        }

        /// <summary>
        /// Refuses a TrueType Collection by name rather than letting the directory reader make
        /// nonsense of its header.
        /// </summary>
        /// <remarks>
        /// <para>A <c>ttcf</c> header puts <c>majorVersion</c> (1) exactly where an sfnt keeps
        /// <c>numTables</c>, and the first face's u32 offset where the first record's tag goes. The
        /// bounds-checked reader below therefore does not fault: it finds one table with a nonsense
        /// name at a nonsense offset, locates no <c>head</c>, <c>hhea</c>, <c>maxp</c> or
        /// <c>hmtx</c>, and returns <see cref="TrueTypeMetrics"/> with every field at its default.
        /// <c>EmbedFont</c> then wrote that out as <c>/Ascent 0 /Descent 0 /FontBBox [0 0 0 0]</c>
        /// with every <c>/Widths</c> entry 500, and put <b>the whole collection</b> in
        /// <c>/FontFile2</c>, which §9.9 defines as a single TrueType font program. Valid-looking
        /// PDF, unusable font, no exception and no diagnostic.</para>
        ///
        /// <para>That is reachable rather than theoretical: <c>Core.Fonts.FontFileLocator</c> — this
        /// library's own font search — returns <c>*.ttc</c> alongside <c>*.ttf</c>, and a collection
        /// is the normal shipping form for CJK faces on Windows and macOS. Six readers in the tree
        /// already refused one by name; these two, behind the public API, did not.</para>
        /// </remarks>
        /// <exception cref="NotSupportedException">The bytes are a TrueType Collection.</exception>
        private static void RefuseCollection(byte[] fontBytes)
        {
            if (Core.Fonts.SfntTableDirectory.IsCollection(fontBytes))
                throw new NotSupportedException(Core.Fonts.SfntTableDirectory.CollectionRefusal + ".");
        }

        // ----------------------------------------------------------------
        //  Font object builder
        // ----------------------------------------------------------------

        private static (PdfReference fontRef, List<IndirectObject> objects)
            BuildFontObjects(byte[] fontBytes, TrueTypeMetrics metrics, ref int nextObjNum)
        {
            var objects = new List<IndirectObject>();

            bool isCff = metrics.HasCffTable;

            // ---- /FontFile2 or /FontFile3 stream ----
            int fontFileObjNum = nextObjNum++;
            var fontFileStream = new PdfStream();
            if (isCff)
            {
                fontFileStream.Set(PdfName.Subtype, PdfName.OpenType);
                fontFileStream.Set(PdfName.Filter, PdfName.FlateDecode);
                byte[] compressed = Compress(fontBytes);
                fontFileStream.RawData = compressed;
                fontFileStream.Set(PdfName.Length, new PdfNumber(compressed.Length));
                fontFileStream.Set(PdfName.Length1, new PdfNumber(fontBytes.Length));
            }
            else
            {
                fontFileStream.Set(PdfName.Filter, PdfName.FlateDecode);
                byte[] compressed = Compress(fontBytes);
                fontFileStream.RawData = compressed;
                fontFileStream.Set(PdfName.Length, new PdfNumber(compressed.Length));
                fontFileStream.Set(PdfName.Length1, new PdfNumber(fontBytes.Length));
            }
            fontFileStream.ObjectNumber = fontFileObjNum;
            fontFileStream.GenerationNumber = 0;
            objects.Add(new IndirectObject(fontFileObjNum, 0, fontFileStream));

            // ---- /FontDescriptor ----
            int fdObjNum = nextObjNum++;
            var fdDict = new PdfDictionary();
            fdDict.Set(PdfName.Type, PdfName.FontDescriptor);
            fdDict.Set(PdfName.FontName, new PdfName(metrics.PostScriptName ?? "Unknown"));
            fdDict.Set(PdfName.Flags, new PdfNumber(metrics.Flags));
            var bboxArr = new PdfArray();
            bboxArr.Add(new PdfNumber(metrics.BBoxXMin));
            bboxArr.Add(new PdfNumber(metrics.BBoxYMin));
            bboxArr.Add(new PdfNumber(metrics.BBoxXMax));
            bboxArr.Add(new PdfNumber(metrics.BBoxYMax));
            fdDict.Set(PdfName.FontBBox, bboxArr);
            fdDict.Set(PdfName.ItalicAngle, new PdfNumber(metrics.ItalicAngle));
            fdDict.Set(PdfName.Ascent, new PdfNumber(metrics.Ascent));
            fdDict.Set(PdfName.Descent, new PdfNumber(metrics.Descent));
            fdDict.Set(PdfName.CapHeight, new PdfNumber(metrics.CapHeight));
            fdDict.Set(PdfName.StemV, new PdfNumber(metrics.StemV));

            if (isCff)
                fdDict.Set(PdfName.FontFile3, new PdfReference(fontFileObjNum, 0));
            else
                fdDict.Set(PdfName.FontFile2, new PdfReference(fontFileObjNum, 0));

            fdDict.ObjectNumber = fdObjNum;
            fdDict.GenerationNumber = 0;
            objects.Add(new IndirectObject(fdObjNum, 0, fdDict));

            // ---- /Widths array (WinAnsi range 32-255) ----
            //
            // The font dictionary declares /WinAnsiEncoding, so entry i is the advance for
            // *WinAnsi code* 32+i — which is not the same as Unicode code point 32+i. CP1252 puts
            // the typographic punctuation in 0x80-0x9F, where Latin-1 has C1 controls: 0x92 is
            // U+2019 (right single quote), 0x97 is U+2014 (em dash), 0x80 is U+20AC (euro).
            // Passing the raw code to GetWidth, which treats it as a Unicode code point, looked up
            // characters no cmap maps and returned the .notdef advance instead, so every line
            // containing a smart quote was mis-spaced — and with a zero .notdef advance the
            // following glyphs overprinted it.
            var widthsArr = new PdfArray();
            for (int ch = 32; ch <= 255; ch++)
                widthsArr.Add(new PdfNumber(metrics.GetWidth(StandardEncodings.GetWinAnsiChar(ch))));

            // ---- /Font dict ----
            int fontObjNum = nextObjNum++;
            var fontDict = new PdfDictionary();
            fontDict.Set(PdfName.Type, PdfName.Font);
            fontDict.Set(PdfName.Subtype, PdfName.TrueType);
            fontDict.Set(PdfName.BaseFont, new PdfName(metrics.PostScriptName ?? "Unknown"));
            fontDict.Set(PdfName.FirstChar, new PdfNumber(32));
            fontDict.Set(PdfName.LastChar, new PdfNumber(255));
            fontDict.Set(PdfName.Widths, widthsArr);
            fontDict.Set(PdfName.Encoding, PdfName.WinAnsiEncoding);
            fontDict.Set(PdfName.FontDescriptor, new PdfReference(fdObjNum, 0));
            fontDict.ObjectNumber = fontObjNum;
            fontDict.GenerationNumber = 0;
            objects.Add(new IndirectObject(fontObjNum, 0, fontDict));

            return (new PdfReference(fontObjNum, 0), objects);
        }

        // One Flate encoder, Core.IO.Filters.FlateEncodeFilter. This file carried a zlib framer
        // of its own behind a Compress() whose first DeflateStream wrote nothing at all.
        private static byte[] Compress(byte[] data) => FlateEncodeFilter.Encode(data);
    }

    // ----------------------------------------------------------------
    //  PdfDictionary clone helper (if not already accessible)
    // ----------------------------------------------------------------

    internal static class PdfDictionaryExtensions
    {
        internal static PdfDictionary Clone(this PdfDictionary source)
            => PdfPageContentAppender.CloneDictionary(source);
    }

    // ----------------------------------------------------------------
    //  TrueType font metrics parser
    // ----------------------------------------------------------------

    /// <summary>
    /// Parsed metrics from a TrueType or OpenType font file.
    /// Used to build the PDF /FontDescriptor and /Widths array.
    /// </summary>
    public sealed class TrueTypeMetrics
    {
        /// <summary>PostScript name (name ID 6 in the name table).</summary>
        public string PostScriptName { get; internal set; }
        /// <summary>Family name (name ID 1).</summary>
        public string FamilyName { get; internal set; }
        /// <summary>Units per em (from head table).</summary>
        public int UnitsPerEm { get; internal set; } = 1000;
        /// <summary>Typographic ascent scaled to 1000-unit space.</summary>
        public int Ascent { get; internal set; }
        /// <summary>Typographic descent scaled to 1000-unit space (negative).</summary>
        public int Descent { get; internal set; }
        /// <summary>Cap-height scaled to 1000-unit space (approximated if OS/2 absent).</summary>
        public int CapHeight { get; internal set; }
        /// <summary>Italic angle (degrees) from post table, or 0.</summary>
        public double ItalicAngle { get; internal set; }
        /// <summary>Approximate dominant stem width (derived from weight class).</summary>
        public int StemV { get; internal set; } = 80;
        /// <summary>PDF /Flags bitmask (ISO 32000 §9.8.2).</summary>
        public int Flags { get; internal set; } = 32; // Nonsymbolic
        /// <summary>Font bounding box in 1000-unit space.</summary>
        public int BBoxXMin { get; internal set; }
        /// <summary>Font bounding box in 1000-unit space.</summary>
        public int BBoxYMin { get; internal set; }
        /// <summary>Font bounding box in 1000-unit space.</summary>
        public int BBoxXMax { get; internal set; }
        /// <summary>Font bounding box in 1000-unit space.</summary>
        public int BBoxYMax { get; internal set; }
        /// <summary>Whether the font has a CFF table (OpenType CFF).</summary>
        public bool HasCffTable { get; internal set; }

        // Glyph-ID → advance width (in font units).
        private int[] _glyphWidths = Array.Empty<int>();
        // Unicode codepoint → glyph ID (from cmap).
        private readonly Dictionary<int, int> _cmap = new Dictionary<int, int>();

        internal void SetGlyphWidths(int[] widths) => _glyphWidths = widths;
        internal void SetCmapEntry(int codepoint, int glyphId) => _cmap[codepoint] = glyphId;

        /// <summary>
        /// Returns the advance width in 1000-unit PDF space for the given <b>Unicode code
        /// point</b>.
        /// </summary>
        /// <remarks>
        /// <para><b>Two different fallbacks, in this order.</b> A code point the font's cmap does
        /// not map takes the advance of glyph 0 (<c>.notdef</c>); 500 is used only when the font
        /// supplied no <c>hmtx</c> widths at all. For a font whose <c>.notdef</c> advance is 0 —
        /// common, and legal — every unmapped code gets 0, so a viewer honouring <c>/Widths</c>
        /// advances nothing and the following glyphs overprint. That is the font's own answer for
        /// a character it does not have, and substituting an invented 500 would be worse.</para>
        /// <para>Callers writing a <c>/Widths</c> array for a font declared
        /// <c>/WinAnsiEncoding</c> must map the code through
        /// <see cref="StandardEncodings.GetWinAnsiChar"/> first — WinAnsi and Latin-1 differ across
        /// 0x80-0x9F.</para>
        /// </remarks>
        public int GetWidth(int charCode)
        {
            // charCode is a Latin-1 value (0-255).
            // First map via Unicode (charCode IS the Unicode codepoint for Latin-1).
            if (_cmap.TryGetValue(charCode, out int gid))
            {
                if (gid >= 0 && gid < _glyphWidths.Length)
                    return Scale(_glyphWidths[gid]);
            }
            // Fallback to .notdef width (glyph 0).
            if (_glyphWidths.Length > 0)
                return Scale(_glyphWidths[0]);
            return 500;
        }

        private int Scale(int fontUnits) =>
            UnitsPerEm > 0 ? SaturatingCast.ToInt(Math.Round(fontUnits * PdfUnits.GlyphSpaceUnitsPerEm / UnitsPerEm)) : fontUnits;

        // ----------------------------------------------------------------
        //  TTF/OTF parser
        // ----------------------------------------------------------------

        /// <summary>Bytes per sfnt table-directory record: tag(4) + checksum(4) + offset(4) + length(4).</summary>
        private const int TableDirectoryEntryBytes = 16;

        internal static TrueTypeMetrics Parse(byte[] data)
        {
            var m = new TrueTypeMetrics();
            if (data == null || data.Length < 12) return m;

            var r = new TtfReader(data);

            // Offset table.
            uint sfVersion = r.ReadUInt32();
            // Supported: 0x00010000 (TrueType), 0x4F54544F ('OTTO' = CFF OpenType), 0x74727565 ('true').
            bool isCff = sfVersion == 0x4F54544F; // 'OTTO'
            m.HasCffTable = isCff;

            int numTables = r.ReadUInt16();
            r.Skip(6); // searchRange, entrySelector, rangeShift

            // Read table directory. numTables comes straight from the file, so a 40-byte font can
            // claim 1000 entries; stop at the buffer's end and keep whatever was really there
            // rather than reading zeros (or faulting, before the reader was bounds-checked).
            // An entry whose offset+length falls outside the file is dropped, so no later Seek
            // lands outside it.
            var tables = new Dictionary<string, (uint offset, uint length)>(StringComparer.Ordinal);
            for (int i = 0; i < numTables; i++)
            {
                if (!r.CanRead(TableDirectoryEntryBytes)) break;
                string tag = r.ReadTag();
                r.Skip(4); // checksum
                uint offset = r.ReadUInt32();
                uint length = r.ReadUInt32();
                if (string.IsNullOrEmpty(tag)) continue;
                if (offset > data.Length) continue;
                // Clamped rather than dropped, so a font with one sloppy length still yields the
                // metrics it really has; every read is bounds-checked anyway.
                if ((long)offset + length > data.Length) length = (uint)(data.Length - offset);
                tables[tag] = (offset, length);
            }

            m.HasCffTable = tables.ContainsKey("CFF ");

            int upm = 1000;

            // ---- head table ----
            if (tables.TryGetValue("head", out var headEntry))
            {
                r.Seek(headEntry.offset);
                r.Skip(18); // version, revision, checkSumAdjustment, magicNumber, flags
                upm = r.ReadUInt16(); // unitsPerEm
                m.UnitsPerEm = upm;
                r.Skip(16); // created, modified
                short xMin = r.ReadInt16();
                short yMin = r.ReadInt16();
                short xMax = r.ReadInt16();
                short yMax = r.ReadInt16();
                m.BBoxXMin = Scale(xMin, upm);
                m.BBoxYMin = Scale(yMin, upm);
                m.BBoxXMax = Scale(xMax, upm);
                m.BBoxYMax = Scale(yMax, upm);
            }

            // ---- hhea table ----
            if (tables.TryGetValue("hhea", out var hheaEntry))
            {
                r.Seek(hheaEntry.offset);
                r.Skip(4); // majorVersion + minorVersion
                short ascender = r.ReadInt16();
                short descender = r.ReadInt16();
                // After descender (table offset 8) exactly 13 int16 fields precede
                // numberOfHMetrics: lineGap, advanceWidthMax, minLeftSideBearing,
                // minRightSideBearing, xMaxExtent, caretSlopeRise, caretSlopeRun,
                // caretOffset, 4x reserved, metricDataFormat = 26 bytes. That puts
                // numberOfHMetrics at table offset 34, and hhea is 36 bytes long.
                // Offset 38 would be two bytes PAST the end of the table. PdfFontSubsetter.ParseHhea
                // reads offset 34 too.
                r.Skip(26);
                int numberOfHMetrics = r.ReadUInt16();

                m.Ascent = Scale(ascender, upm);
                m.Descent = Scale(descender, upm);
                m.CapHeight = SaturatingCast.ToInt(m.Ascent * 0.72); // reasonable approximation

                // ---- hmtx table ---- (requires numberOfHMetrics from hhea)
                if (tables.TryGetValue("hmtx", out var hmtxEntry))
                {
                    // Total glyph count from maxp.
                    int numGlyphs = numberOfHMetrics; // lower bound
                    if (tables.TryGetValue("maxp", out var maxpEntry))
                    {
                        r.Seek(maxpEntry.offset + 4);
                        numGlyphs = r.ReadUInt16();
                    }

                    // numberOfHMetrics and numGlyphs both come straight out of the file, so a
                    // malformed or hostile font can name far more entries than hmtx actually
                    // holds. TtfReader indexes its buffer unchecked, so an unbounded loop here
                    // throws IndexOutOfRangeException on untrusted input. Clamp to what the
                    // hmtx table and the buffer can really supply; a short table is a defect in
                    // the font, not a reason to fault the process.
                    long hmtxAvailable = Math.Min(
                        (long)hmtxEntry.length,
                        Math.Max(0L, data.Length - (long)hmtxEntry.offset));
                    int readableMetrics = (int)Math.Min(numberOfHMetrics, hmtxAvailable / 4);

                    var widths = new int[numGlyphs];
                    r.Seek(hmtxEntry.offset);
                    int lastAW = 0;
                    for (int i = 0; i < readableMetrics; i++)
                    {
                        lastAW = r.ReadUInt16();
                        r.Skip(2); // lsb
                        if (i < numGlyphs) widths[i] = lastAW;
                    }
                    // Remaining glyphs share the last advance width (the hmtx tail carries
                    // left-side bearings only, which these metrics do not use).
                    for (int i = readableMetrics; i < numGlyphs; i++)
                        widths[i] = lastAW;
                    m.SetGlyphWidths(widths);
                }
            }

            // ---- OS/2 table ---- (CapHeight, weight class → StemV, flags)
            if (tables.TryGetValue("OS/2", out var os2Entry))
            {
                r.Seek(os2Entry.offset);
                int version = r.ReadUInt16();
                r.Skip(2); // xAvgCharWidth
                int weightClass = r.ReadUInt16();
                r.Skip(2); // widthClass
                int fsType = r.ReadUInt16();
                // Skip to sTypoAscender (offset 68).
                r.Skip(62); // panose(10) + unicodeRange(16) + achVendID(4) + fsSelection(2) + ... actually let me count
                // Total offsets so far: 2(version) + 2(xAvg) + 2(weight) + 2(width) + 2(fsType) = 10 bytes read
                // We need to get to sTypoAscender at offset 68 from table start.
                // After reading 10 bytes from start, we need to skip 58 more bytes.
                // Actually let me re-count from table start more carefully.
                // version(2)+xAvgCharWidth(2)+usWeightClass(2)+usWidthClass(2)+fsType(2) = 10 read
                // Then: ySubscriptXSize(2)+ySubscriptYSize(2)+ySubscriptXOffset(2)+ySubscriptYOffset(2)
                //       +ySuperscriptXSize(2)+ySuperscriptYSize(2)+ySuperscriptXOffset(2)+ySuperscriptYOffset(2)
                //       +yStrikeoutSize(2)+yStrikeoutPos(2)+sFamilyClass(2)+panose(10)
                //       +ulUnicodeRange1(4)+ulUnicodeRange2(4)+ulUnicodeRange3(4)+ulUnicodeRange4(4)
                //       +achVendID(4)+fsSelection(2)+usFirstCharIndex(2)+usLastCharIndex(2) = 76 bytes
                // So sTypoAscender is at offset 10 + 76 = 86? This varies by version.
                // Simpler: use the hhea values already read, derive CapHeight from OS/2 if v2+.

                // Approximate StemV from weight class.
                m.StemV = SaturatingCast.ToInt(10.0 + 220.0 * (weightClass - 400) / 600.0);
                if (m.StemV < 10) m.StemV = 10;
                if (m.StemV > 250) m.StemV = 250;

                // Bold / Italic come from fsSelection (offset 62 from the OS/2
                // table start), NOT fsType (the embedding-permissions field read
                // above). fsSelection bit 0 = ITALIC, bit 5 (0x20) = BOLD.
                // PDF FontDescriptor /Flags (Table 121): Italic = 0x40,
                // ForceBold = 0x40000. Italic comes from fsSelection, not fsType.
                _ = fsType;
                r.Seek(os2Entry.offset + 62);
                int fsSelection = r.ReadUInt16();
                if ((fsSelection & 0x20) != 0) m.Flags |= 0x40000; // BOLD → ForceBold
                if ((fsSelection & 0x01) != 0) m.Flags |= 0x40;    // ITALIC
            }

            // ---- post table ---- (ItalicAngle, isFixedPitch)
            if (tables.TryGetValue("post", out var postEntry))
            {
                r.Seek(postEntry.offset);
                r.Skip(4); // version
                // italicAngle is a Fixed (16.16) value.
                int italicFixed = r.ReadInt32();
                m.ItalicAngle = italicFixed / 65536.0;

                r.Skip(2); // underlinePosition
                r.Skip(2); // underlineThickness
                int isFixedPitch = r.ReadInt32();
                if (isFixedPitch != 0) m.Flags |= 1; // FixedPitch flag
            }

            // ---- name and cmap tables ----
            //
            // Both come from Core's TrueTypeFontProgram — the one reader for each — rather than
            // from this type's own scans, which disagreed with it about which name record wins and
            // about which cmap subtables exist. Parsed once and shared.
            TrueTypeFontProgram program = TrueTypeFontProgram.Parse(data);
            if (program != null)
            {
                FillNames(program, m);
                FillCmap(program, m);
            }

            return m;
        }

        private static int Scale(int fontUnits, int upm) =>
            upm > 0 ? SaturatingCast.ToInt(Math.Round(fontUnits * PdfUnits.GlyphSpaceUnitsPerEm / upm)) : fontUnits;

        /// <summary>
        /// Fills <paramref name="m"/>'s family and PostScript names from the font's <c>name</c>
        /// table, through <see cref="TrueTypeFontProgram"/> — the one <c>name</c>-table reader.
        /// </summary>
        /// <remarks>
        /// <para><b>Not a first-wins scan, which picks the wrong record.</b> A
        /// <c>name</c> table carries one record per language and the conventional sort is platform,
        /// then encoding, then language — which puts <b>Macintosh records first</b>. Taking the first
        /// usable record and accepting <i>any</i> Macintosh language, a font whose Mac records are
        /// localised and whose English name lives on the Windows platform would yield the localised
        /// name. Measured on the shared fixture: <c>LocalisedFamily</c> where Core
        /// reads <c>EnglishFamily</c>. That name becomes <c>/FontName</c> in the descriptor and
        /// <c>/BaseFont</c> in the font dictionary.</para>
        ///
        /// <para><b>Nor Macintosh encodings it cannot decode.</b> Core rejects every Macintosh
        /// encoding but
        /// Roman (0), because the others are legacy CJK code pages that decode to mojibake. A
        /// mojibake <c>/BaseFont</c> is a PDF name object full of bytes needing <c>#xx</c> escaping,
        /// and it is what readers match on when substituting a missing font.</para>
        ///
        /// <para>Core's scoring — Windows/Unicode English, then Windows/Unicode any language, then
        /// Macintosh English, then any remaining Macintosh Roman — is pinned by
        /// <c>TrueTypeNameRecordSelectionTests</c>, including the case that stops it degenerating
        /// into "always take platform 3" (a Macintosh-only font must still report a name).</para>
        /// </remarks>
        private static void FillNames(TrueTypeFontProgram program, TrueTypeMetrics m)
        {
            if (!string.IsNullOrEmpty(program.FamilyName))
                m.FamilyName = program.FamilyName;

            // TrueTypeFontProgram substitutes its own placeholder when a font names itself nothing.
            // Leaving m.PostScriptName null in that case keeps this type's existing "Unknown"
            // fallback, so a nameless font's output is unchanged.
            if (!string.IsNullOrEmpty(program.PostScriptName)
                && program.PostScriptName != TrueTypeFontProgram.UnnamedPostScriptName)
            {
                m.PostScriptName = program.PostScriptName;
            }
        }

        /// <summary>
        /// Fills <paramref name="m"/>'s code-point → glyph map from the font's <c>cmap</c>, through
        /// <see cref="TrueTypeFontProgram"/> — the one cmap reader.
        /// </summary>
        /// <remarks>
        /// <para><b>Every subtable kind Core reads</b>, not three — <c>(3,1)</c>, <c>(0,3)</c> and
        /// <c>(0,4)</c>, the first match, format 4 alone — with nothing for the rest. Measured against a
        /// fixture with distinct per-glyph advances, a font whose only cmap is <c>(3,0)</c> Symbol,
        /// <c>(1,0)</c> Macintosh Roman or <c>(3,10)</c> Windows UCS-4 came out with
        /// <b>every advance set to <c>.notdef</c>'s</b> — the font embedded, the PDF valid, the
        /// glyphs drawn, and every width wrong.</para>
        ///
        /// <para><b>Both of those are ordinary fonts.</b> Symbol is what marlett, symbol, wingdings,
        /// webdings and most icon webfonts ship; <c>(3,10)</c> is normal for anything with astral
        /// coverage. Core's reader handles all of them, and so does this one, one folder away — a guard
        /// that stops one level short is the pattern CLAUDE.md records.</para>
        ///
        /// <para>Only the glyph <i>lookup</i> moves. Advances still come from this type's own
        /// <c>hmtx</c> read and <c>Scale</c>.</para>
        /// </remarks>
        private static void FillCmap(TrueTypeFontProgram program, TrueTypeMetrics m)
        {
            // Latin-1, which is what GetWidth is asked about…
            for (int cp = 0; cp <= 0xFF; cp++)
                if (TryGlyph(program, cp, out ushort gid))
                    m.SetCmapEntry(cp, gid);

            // …plus the ~27 code points WinAnsi puts in 0x80-0x9F, which are NOT Latin-1 and so
            // fall outside the sweep above. Without these, BuildFontObjects has no width for a
            // smart quote, an em dash or a euro sign — the characters real text is full of.
            //
            // Keyed by the CODE POINT, not the WinAnsi code. GetWidth is a Unicode lookup and
            // BuildFontObjects does the code → code-point translation when it lays out /Widths;
            // keying by the code here makes GetWidth(0x92) answer for U+2019, which
            // FontEmbedAndSubsetFindingsTests pins as .notdef. (Keying it wrong is what an earlier
            // draft of this method did, and that test caught it.)
            for (int code = 0x80; code <= 0x9F; code++)
            {
                int cp = StandardEncodings.GetWinAnsiChar(code);
                if (cp <= 0xFF) continue;   // already covered, or WinAnsi leaves the slot undefined
                if (program.TryGetGlyph(cp, out ushort gid) && gid != 0)
                    m.SetCmapEntry(cp, gid);
            }
        }

        /// <summary>
        /// A glyph for <paramref name="codePoint"/>, trying the Windows symbol PUA when the plain
        /// lookup finds nothing.
        /// </summary>
        /// <remarks>
        /// <b>A symbolic font's (3,0) cmap is conventionally populated at 0xF000 + code</b>, so a
        /// lookup for U+0041 in Symbol or Wingdings finds nothing and the caller falls back to
        /// .notdef's advance. That is not a refusal — it is a valid PDF in which EVERY glyph of a
        /// symbol font is laid out at one wrong width (symbol.ttf a flat 600 and wingdings a flat 500
        /// for every code point, where their own hmtx has distinct advances).
        ///
        /// <para><b>The read side already had this rule</b>, in
        /// <c>Core.Fonts.PdfTrueTypeFont</c>, comment and all — so the two halves of this library
        /// disagreed about what a symbol font's glyph for a byte is. The fold is applied HERE
        /// rather than inside <c>TrueTypeFontProgram.TryGetGlyph</c> on purpose:
        /// <c>SystemFontResolver.Covers</c> and other callers read that method to ask whether a
        /// font really covers a code point, and folding there would make every symbol font claim
        /// coverage of all of Latin-1.</para>
        /// </remarks>
        private static bool TryGlyph(TrueTypeFontProgram program, int codePoint, out ushort gid)
        {
            if (program.TryGetGlyph(codePoint, out gid) && gid != 0) return true;

            if (codePoint >= 0 && codePoint <= 0xFF
                && program.TryGetGlyph(0xF000 | codePoint, out gid) && gid != 0) return true;

            gid = 0;
            return false;
        }
    }

    // ----------------------------------------------------------------
    //  Minimal big-endian binary reader for TTF data
    // ----------------------------------------------------------------

    /// <summary>
    /// Big-endian reader over a font file, <b>bounds-checked on every read</b>.
    /// </summary>
    /// <remarks>
    /// <para><b>Why it reads zeros past the end instead of throwing.</b> Every count and offset
    /// this reader is steered by — <c>numTables</c>, a table's offset and length, a name record's
    /// <c>stringOffset</c>, a cmap <c>segCount</c> — comes straight out of the file, and the file
    /// may be truncated, corrupt or hostile. Unchecked indexing turned that into
    /// <c>IndexOutOfRangeException</c> (and, from <c>ReadBytes</c>, a negative-count
    /// <c>ArgumentOutOfRangeException</c> out of <c>Array.Copy</c>) escaping the public
    /// <c>GetFontMetrics</c>/<c>EmbedFont</c> API, neither of them a documented exception. A short
    /// or lying table is a defect in the font, and the honest response is metrics that are missing
    /// or approximate — <see cref="TrueTypeMetrics"/> already has sensible defaults for every
    /// field — not a fault in the caller's process.</para>
    /// <para><see cref="AtEnd"/> lets a loop stop rather than spin: a directory claiming 1000
    /// tables in a 40-byte file would otherwise read 1000 all-zero entries.</para>
    /// </remarks>
    internal sealed class TtfReader
    {
        private readonly byte[] _data;
        private int _pos;

        public TtfReader(byte[] data) { _data = data ?? Array.Empty<byte>(); _pos = 0; }

        public uint Position { get => (uint)_pos; }
        public uint Length   { get => (uint)_data.Length; }

        /// <summary>True once the cursor is at or past the end, or was seeked outside the buffer.</summary>
        public bool AtEnd => _pos < 0 || _pos >= _data.Length;

        /// <summary>True when <paramref name="count"/> more bytes are readable from the cursor.</summary>
        public bool CanRead(int count) => count >= 0 && _pos >= 0 && (long)_pos + count <= _data.Length;

        public void Seek(uint offset) => _pos = offset > int.MaxValue ? _data.Length : (int)offset;
        public void Skip(int bytes)   => _pos += bytes;

        public uint   ReadUInt32() { uint v = CanRead(4) ? BEU32(_pos) : 0u; _pos += 4; return v; }
        public int    ReadInt32()  { return (int)ReadUInt32(); }
        public int    ReadUInt16() { int  v = CanRead(2) ? BEU16(_pos) : 0;  _pos += 2; return v; }
        public short  ReadInt16()  { int v = ReadUInt16(); return (short)v; }

        public string ReadTag()
        {
            if (!CanRead(4)) { _pos += 4; return string.Empty; }
            char[] tag = { (char)_data[_pos], (char)_data[_pos+1],
                           (char)_data[_pos+2], (char)_data[_pos+3] };
            _pos += 4;
            return new string(tag);
        }

        public byte[] ReadBytes(int count)
        {
            if (count <= 0) return Array.Empty<byte>();
            var buf = new byte[count];
            // A cursor already past the end made `_data.Length - _pos` negative, and Array.Copy
            // throws on a negative count. Clamp both ends instead.
            int available = _pos < 0 || _pos >= _data.Length ? 0 : Math.Min(count, _data.Length - _pos);
            if (available > 0) Array.Copy(_data, _pos, buf, 0, available);
            _pos += count;
            return buf;
        }

        private uint  BEU32(int p) => ((uint)_data[p]<<24)|((uint)_data[p+1]<<16)|((uint)_data[p+2]<<8)|_data[p+3];
        private int   BEU16(int p) => (_data[p] << 8) | _data[p+1];
    }
}
