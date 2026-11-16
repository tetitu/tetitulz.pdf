// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Fonts
{
    internal abstract class PdfFont
    {
        public string BaseFont { get; protected set; }

        /// <summary>
        /// Whether this is a Type 3 font, whose <c>/Widths</c> are in its own glyph space and reach
        /// text space through <see cref="Type3Matrix"/> (§9.6.5), not through 1000 units an em.
        /// </summary>
        public bool IsType3 { get; internal set; }

        /// <summary>A Type 3 font's <c>/FontMatrix</c> (<see cref="Type3FontMatrix"/>); null for any other font.</summary>
        public float[] Type3Matrix { get; internal set; }

        /// <summary>
        /// Why the most recent <see cref="Create"/> on this thread read the font without its
        /// <c>/ToUnicode</c> CMap — the stream did not decode, or decoded past
        /// <see cref="Constants.PdfLimits.MaxCMapStreamBytes"/> — or <c>null</c> when it read the CMap or
        /// the font names none. Cleared at the start of every <see cref="Create"/>.
        /// </summary>
        internal static string LastToUnicodeError => _lastToUnicodeError;

        [ThreadStatic]
        private static string _lastToUnicodeError;

        // ------------------------------------------------------------------
        //  Resolving dictionary reads, shared by every font class
        // ------------------------------------------------------------------

        /// <summary>
        /// Reads <paramref name="key"/> and follows an indirect reference (§7.3.10).
        /// </summary>
        /// <remarks>
        /// <para><b>Why the font classes need this and the plain getters will not do.</b>
        /// <c>PdfDictionary</c>'s typed getters test the <i>raw</i> stored value's type, so a legal
        /// <c>/FirstChar 12 0 R</c> reads as <b>absent</b>. In a font dictionary that is rarely
        /// benign, because the fallbacks are not neutral sentinels:</para>
        /// <list type="bullet">
        ///   <item><description><c>/FirstChar ?? 0</c> is the <b>array origin</b>. <c>GetWidth</c>
        ///   computes <c>charCode - _firstChar</c>, so a font whose real <c>/FirstChar</c> is 32
        ///   returns a width 32 entries early — <b>a real width belonging to a different
        ///   glyph</b> — and <c>HasExplicitWidth</c> returns <see langword="true"/> for it, so no
        ///   consumer can detect the substitution. Both renderers and the text extractor read
        ///   it, and the error accumulates along the line.</description></item>
        ///   <item><description><c>/DW ?? 1000</c> is the spec default, returned for every CID
        ///   <c>/W</c> does not cover.</description></item>
        /// </list>
        ///
        /// <para>The tell was already in the code: <c>/Widths</c> is read with an explicit
        /// <c>ResolveObject</c> fallback on the line <i>after</i> <c>/FirstChar</c>, and
        /// <c>/W</c> two lines after <c>/DW</c>. The resolve had been added where it was noticed
        /// missing and not to its neighbour.</para>
        /// </remarks>
        private protected static PdfObject Resolved(PdfDictionary dict, PdfName key, PdfDocument document)
        {
            var raw = dict?[key];
            return document?.ResolveObject(raw) ?? raw;
        }

        /// <summary>The integer at <paramref name="key"/>, following an indirect reference.</summary>
        private protected static int? ResolvedInt(PdfDictionary dict, PdfName key, PdfDocument document)
            => Resolved(dict, key, document) is PdfNumber n ? n.IntValue : (int?)null;

        /// <summary>The number at <paramref name="key"/>, following an indirect reference.</summary>
        private protected static double? ResolvedDouble(PdfDictionary dict, PdfName key, PdfDocument document)
            => Resolved(dict, key, document) is PdfNumber n ? n.DoubleValue : (double?)null;

        /// <summary>The name at <paramref name="key"/>, following an indirect reference.</summary>
        private protected static PdfName ResolvedName(PdfDictionary dict, PdfName key, PdfDocument document)
            => Resolved(dict, key, document) as PdfName;

        /// <summary>The array at <paramref name="key"/>, following an indirect reference.</summary>
        private protected static PdfArray ResolvedArray(PdfDictionary dict, PdfName key, PdfDocument document)
            => Resolved(dict, key, document) as PdfArray;

        public abstract string Decode(byte[] bytes);

        /// <summary>
        /// The text one character code decodes to — what <see cref="Decode(byte[])"/> answers for
        /// the code's own bytes — for a caller that already has the code from
        /// <see cref="SplitCodes"/> and would otherwise copy its bytes out to decode them.
        /// </summary>
        /// <remarks>
        /// The extractors decode a shown string one code at a time (a glyph's text comes from its
        /// own bytes), and did so by copying each code's bytes into a fresh array and building a
        /// <c>StringBuilder</c> per code: on a hundred-thousand-show document that was a third of
        /// plain extraction's allocation. A font answers from a per-code cache where it can — a
        /// simple font's 256 codes, a composite font's codes up to a ceiling — and the text is the
        /// same string instance each time. The base answers by rebuilding the bytes, which is exact:
        /// <see cref="CMapCodec.Code.Value"/> is the code's bytes big-endian in its length.
        /// </remarks>
        public virtual string Decode(CMapCodec.Code code) => Decode(CodeBytes(code));

        /// <summary>The code's bytes: <see cref="CMapCodec.Code.Value"/> big-endian in <see cref="CMapCodec.Code.ByteLength"/> (1–4).</summary>
        internal static byte[] CodeBytes(CMapCodec.Code code)
        {
            int length = Math.Min(Math.Max(code.ByteLength, 1), 4);
            var bytes = new byte[length];
            for (int k = 0; k < length; k++) bytes[k] = (byte)(code.Value >> (8 * (length - 1 - k)));
            return bytes;
        }

        /// <summary>
        /// Splits a show-string into character codes. One code per byte for a simple font (§9.6.6:
        /// its codes are single bytes by definition); a composite font overrides this with its
        /// <c>/Encoding</c> CMap, which is the only thing that can split a mixed-width string.
        /// </summary>
        /// <remarks>
        /// This exists so that callers that need per-glyph work — advancing the text matrix, in
        /// particular — do not have to ask <c>is PdfType0Font</c> and then re-implement the split.
        /// The extractor did exactly that, with a hardcoded 2-byte stride, which is why a
        /// mixed-width CMap laid text out on the wrong positions. For a simple font
        /// <see cref="CMapCodec.Code.Cid"/> equals <see cref="CMapCodec.Code.Value"/>, so a caller
        /// keyed on <c>Cid</c> is correct for both kinds.
        /// </remarks>
        public virtual IReadOnlyList<CMapCodec.Code> SplitCodes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return Array.Empty<CMapCodec.Code>();

            var codes = new CMapCodec.Code[bytes.Length];
            for (int i = 0; i < bytes.Length; i++)
                codes[i] = new CMapCodec.Code(bytes[i], bytes[i], 1);
            return codes;
        }

        /// <summary>
        /// The code <see cref="SplitCodes"/> would give at <paramref name="offset"/>, and how many
        /// bytes it takes (at least one) — for a caller walking a shown string a code at a time
        /// without a list per string. A simple font's codes are its bytes.
        /// </summary>
        public virtual int NextCode(byte[] bytes, int offset, out CMapCodec.Code code)
        {
            code = new CMapCodec.Code(bytes[offset], bytes[offset], 1);
            return 1;
        }

        /// <summary>
        /// Returns the advance width of a character code in thousandths of a unit of text space
        /// (i.e., 1000 = 1 em). Used for accurate text positioning and space detection.
        /// Returns 0 if width is unknown.
        /// </summary>
        public virtual double GetWidth(int charCode) => 0;

        /// <summary>
        /// Returns true when the font has an explicit width entry for the given char code
        /// (i.e., GetWidth returning 0 means genuinely zero-advance, not missing data).
        /// Used to distinguish combining marks (explicit 0-width) from glyphs with no /Widths data.
        /// </summary>
        public virtual bool HasExplicitWidth(int charCode) => false;

        /// <summary>
        /// Returns the CID-to-GID map for TrueType CIDFonts that include a /CIDToGIDMap stream.
        /// The key is the CID (character identifier) and the value is the GID (glyph index) in
        /// the embedded TrueType font. Returns null when no CIDToGIDMap is present.
        /// </summary>
        public virtual IReadOnlyDictionary<int, int> GetCidToGid() => null;

        /// <summary>
        /// True when the CIDFont's /CIDToGIDMap is the name /Identity — charCode == CID == GID.
        /// </summary>
        public virtual bool IsIdentityCidToGid => false;

        /// <summary>
        /// Builds the <see cref="PdfFont"/> for a font dictionary.
        /// </summary>
        /// <param name="fontDict">
        /// The font dictionary. <c>null</c> is tolerated and yields a <c>FallbackFont</c>, because
        /// a page can legitimately name a font resource that is not there.
        /// </param>
        /// <param name="document">
        /// Required <b>when <paramref name="fontDict"/> is not null</b>. Used to follow the
        /// indirect references a font dictionary is full of — <c>/Widths</c>, <c>/Encoding</c>,
        /// <c>/ToUnicode</c>, <c>/DescendantFonts</c>.
        /// </param>
        /// <exception cref="System.ArgumentNullException">
        /// <paramref name="fontDict"/> is non-null and <paramref name="document"/> is null.
        /// </exception>
        /// <remarks>
        /// <para><b>Why this refuses instead of degrading, when the helpers beside it do not.</b>
        /// <see cref="Resolved(PdfDictionary,PdfName,PdfDocument)"/> and its siblings are
        /// null-tolerant, which reads as an invitation to pass no document — but the subclass
        /// constructors dereference <paramref name="document"/> unconditionally at eleven sites,
        /// so tolerating it here would only move the <see cref="System.NullReferenceException"/> a
        /// few frames deeper and make it harder to attribute.</para>
        ///
        /// <para><b>The order of the two checks is the whole design.</b>
        /// <c>Create(null, null)</c> is legitimate and stays legitimate: a page may name a font
        /// resource that is not there, the answer is a fallback font, and that path never touches
        /// the document. Requiring the document first would refuse a call that cannot fail.</para>
        /// </remarks>
        public static PdfFont Create(PdfDictionary fontDict, PdfDocument document)
        {
            _lastToUnicodeError = null;

            // No dictionary: a fallback font, and the document is genuinely not needed.
            if (fontDict == null) return new FallbackFont();

            if (document == null)
                throw new System.ArgumentNullException(
                    nameof(document),
                    "A font dictionary is resolved against its document; pass the PdfDocument the "
                    + "dictionary came from. (A null fontDict needs no document and yields a "
                    + "fallback font.)");

            var resolved = document.ResolveObject(fontDict) as PdfDictionary ?? fontDict;
            var subtype = Resolved(resolved, PdfName.Subtype, document) as PdfName;

            if (subtype == null)
                return new FallbackFont();

            // Load the ToUnicode CMap if present. Its code→Unicode mapping is all that is taken
            // from it: its codespace ranges are NOT how a composite font's bytes are split — that
            // is /Encoding's job (§9.7.5), and the two are allowed to disagree. Reading the split
            // from here is the defect CMapCodec.FromFontEncoding exists to end.
            Dictionary<int, string> toUnicode = null;
            var toUnicodeObj = resolved[PdfName.ToUnicode];
            if (toUnicodeObj != null)
            {
                var toUnicodeStream = document.ResolveObject(toUnicodeObj) as PdfStream;
                if (toUnicodeStream != null)
                {
                    // A CMap that does not decode, or decodes past its ceiling, leaves the font
                    // without one: the glyphs still draw and measure by the font's own encoding,
                    // and one malformed CMap does not abort every font of the document.
                    byte[] cmapData = null;
                    try { cmapData = FontStreamBytes.CMap(toUnicodeStream); }
                    catch (PdfFilterException ex) { _lastToUnicodeError = ex.Message; }
                    if (cmapData != null && cmapData.Length > 0)
                        toUnicode = CMapParser.Parse(cmapData);
                }
            }

            switch (subtype.Value)
            {
                case "Type1":
                case "MMType1":
                    return new PdfType1Font(resolved, document, toUnicode);
                case "TrueType":
                    return new PdfTrueTypeFont(resolved, document, toUnicode);
                case "Type3":
                    // Type3 fonts define each glyph as a content stream. For text extraction,
                    // they work like Type1 with /Encoding+/Differences and optional /ToUnicode —
                    // except that their widths are in the font's own glyph space (GlyphWidth.TextSpace).
                    return new PdfType1Font(resolved, document, toUnicode)
                    {
                        IsType3 = true,
                        Type3Matrix = Type3FontMatrix.Of(resolved, document.ResolveObject, out _),
                    };
                case "Type0":
                case "CIDFontType0":
                case "CIDFontType2":
                    // §9.7.5: the /Encoding CMap splits the bytes and names each code's CID. A
                    // descendant CIDFont reached directly carries no /Encoding, and
                    // FromFontEncoding answers Identity-H for it — the 2-byte default.
                    return new PdfType0Font(resolved, document, toUnicode,
                        CMapCodec.FromFontEncoding(resolved, document));
                default:
                    return new FallbackFont();
            }
        }
    }

    internal class FallbackFont : PdfFont
    {
        // Windows-1252 maps bytes 0–127 identically to ASCII, and 128–255 to the Latin
        // supplement + printable characters used by virtually all Western PDF generators.
        private static readonly Encoding _win1252 = GetSafeEncoding();

        /// <summary>
        /// Reason the most recent <see cref="GetSafeEncoding"/> call fell back
        /// to ISO-8859-1.  <c>null</c> when CP-1252 was loaded successfully.
        /// Useful for diagnosing mojibake on systems where the codepage
        /// provider can't be registered (very rare; mostly trimmed AOT builds).
        /// </summary>
        public static string Win1252FallbackReason { get; private set; }

        private static Encoding GetSafeEncoding()
        {
            try { Win1252FallbackReason = null; return Encoding.GetEncoding(1252); }
            catch (Exception firstEx)
            {
                // .NET Core on some platforms doesn't have codepage 1252 in the
                // default provider; register it and try again.
                try
                {
                    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                    Win1252FallbackReason = null;
                    return Encoding.GetEncoding(1252);
                }
                catch (Exception secondEx)
                {
                    Win1252FallbackReason =
                        "CP-1252 unavailable; using ISO-8859-1 fallback. " +
                        "First attempt: " + firstEx.GetType().Name + ": " + firstEx.Message +
                        " | Second attempt: " + secondEx.GetType().Name + ": " + secondEx.Message;
                    // Not Encoding.GetEncoding("iso-8859-1"): on the one host where CP-1252 is
                    // unavailable (a .NET Core netstandard2.0 host with no CodePages provider) that
                    // call throws too, so the fallback-of-a-fallback would have failed exactly where
                    // it was needed. Latin1Encoding is the repository's own, and cannot (GAPS.md §21).
                    return new Text.Latin1Encoding();
                }
            }
        }

        public override string Decode(byte[] bytes) => _win1252.GetString(bytes);

        private string[] _decodedByCode;

        /// <summary>A one-byte code's text, decoded once per code (see <see cref="PdfFont.Decode(CMapCodec.Code)"/>).</summary>
        public override string Decode(CMapCodec.Code code)
        {
            if (code.ByteLength != 1 || code.Value < 0 || code.Value > 255) return base.Decode(code);
            string[] cache = _decodedByCode ??= new string[256];
            return cache[code.Value] ??= _win1252.GetString(new[] { (byte)code.Value });
        }
    }
}
