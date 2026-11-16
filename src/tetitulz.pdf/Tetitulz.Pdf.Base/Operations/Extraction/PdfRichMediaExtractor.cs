// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Base.Rendering;

namespace Tetitulz.Pdf.Base.Operations.Extraction
{
    /// <summary>Information about a rich media annotation.</summary>
    public sealed class RichMediaInfo
    {
        /// <summary>Zero-based page index where the annotation resides.</summary>
        public int PageIndex { get; init; }

        /// <summary>Annotation subtype: "RichMedia", "Movie", or "Sound".</summary>
        public string Subtype { get; init; }

        /// <summary>MIME type of the embedded data, if known.</summary>
        public string ContentType { get; init; }

        /// <summary>
        /// Byte length of the embedded data, or 0 when it was not measured — see
        /// <see cref="DataLengthUnavailableReason"/>, which is the only way to tell an empty
        /// asset from an unmeasured one.
        /// </summary>
        public int DataLength { get; init; }

        /// <summary>
        /// Why <see cref="DataLength"/> is not the asset's true decoded length, or <c>null</c>
        /// when it is exact. Set when the stream passed the listing ceiling or would not decode.
        /// </summary>
        public string DataLengthUnavailableReason { get; init; }

        /// <summary>Description from /Contents, if present.</summary>
        public string Description { get; init; }
    }

    /// <summary>
    /// Lists and extracts rich media annotations (Video/Audio) embedded in a PDF.
    /// </summary>
    public static class PdfRichMediaExtractor
    {
        /// <summary>
        /// The per-asset decode ceiling <see cref="ListRichMedia(Stream)"/> works under:
        /// 64&#160;MiB. Listing is a metadata query and does not fully inflate every embedded asset
        /// just to read <c>.Length</c> off the result; the explicit
        /// <see cref="ExtractMedia(Stream,int)"/> is uncapped beyond Core's own limit.
        /// </summary>
        public const long DefaultMaxListedStreamBytes = 64L * 1024 * 1024;

        [ThreadStatic] private static string _lastError;

        /// <summary>
        /// Why the most recent call on this thread returned no bytes, or could not measure an
        /// asset — <c>null</c> when it succeeded. This is what distinguishes "this subtype
        /// carries no readable stream" from "this asset is genuinely empty", which
        /// <see cref="ExtractMedia(Stream,int)"/> otherwise reports identically as
        /// <c>byte[0]</c>. Reset at the start of every public entry point.
        /// </summary>
        public static string LastError => _lastError;

        /// <summary>Lists all rich media annotations in a PDF file.</summary>
        public static IReadOnlyList<RichMediaInfo> ListRichMedia(string inputPath)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var fs = File.OpenRead(inputPath);
            return ListRichMedia(fs);
        }

        /// <summary>Lists all rich media annotations in a PDF stream.</summary>
        public static IReadOnlyList<RichMediaInfo> ListRichMedia(Stream input)
            => ListRichMedia(input, DefaultMaxListedStreamBytes);

        /// <summary>
        /// Lists all rich media annotations in a PDF stream, measuring each embedded asset under
        /// an explicit per-asset decode ceiling. An asset past the ceiling is reported with
        /// <see cref="RichMediaInfo.DataLengthUnavailableReason"/> set rather than inflated.
        /// </summary>
        public static IReadOnlyList<RichMediaInfo> ListRichMedia(Stream input, long maxStreamBytes)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (maxStreamBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxStreamBytes));
            _lastError = null;
            using var doc = PdfDocument.Open(input);
            EncryptedRead.Refuse(doc, "the rich media annotations", "PdfOperations.ListRichMedia(Stream, string password)");
            return CollectRichMedia(doc, maxStreamBytes);
        }

        /// <summary>Extracts a rich media asset by index to a byte array.</summary>
        public static byte[] ExtractMedia(string inputPath, int annotationIndex)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var fs = File.OpenRead(inputPath);
            return ExtractMedia(fs, annotationIndex);
        }

        /// <summary>
        /// Extracts a rich media asset by index to a byte array. An empty result is always
        /// explained by <see cref="LastError"/>.
        /// </summary>
        public static byte[] ExtractMedia(Stream input, int annotationIndex)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            _lastError = null;
            using var doc = PdfDocument.Open(input);
            EncryptedRead.Refuse(doc, "the rich media annotations", "PdfOperations.ExtractMedia(Stream, int, string password)");

            int count = 0;
            foreach (var (pageIdx, annotDict, sub) in AnnotationSubtypeWalk.BySubtype(
                         doc, new AnnotationWalkReport(m => _lastError ??= m), PdfAnnotationSubtypes.RichMedia, PdfAnnotationSubtypes.Movie,
                         PdfAnnotationSubtypes.Sound))
            {
                if (count == annotationIndex)
                    return ExtractMediaBytes(annotDict, sub, doc);

                count++;
            }

            throw new ArgumentOutOfRangeException(nameof(annotationIndex),
                $"No rich media annotation at index {annotationIndex}.");
        }

        // -------------------------------------------------------------------
        //  Implementation
        // -------------------------------------------------------------------

        private static IReadOnlyList<RichMediaInfo> CollectRichMedia(PdfDocument doc, long maxStreamBytes)
        {
            var result = new List<RichMediaInfo>();
            var lengths = new DecodedLengths(maxStreamBytes);

            foreach (var (pageIdx, annotDict, sub) in AnnotationSubtypeWalk.BySubtype(
                         doc, new AnnotationWalkReport(m => _lastError ??= m), PdfAnnotationSubtypes.RichMedia, PdfAnnotationSubtypes.Movie,
                         PdfAnnotationSubtypes.Sound))
            {
                string description = null;
                var contentsObj = doc.ResolveObject(annotDict[PdfName.Contents]);
                if (contentsObj is PdfString cs) description = cs.GetText();

                int dataLength = 0;
                var mediaStream = FindMediaStream(annotDict, sub, doc,
                    out string contentType, out string unavailable);

                if (mediaStream != null)
                {
                    // Bounded, and it refuses rather than truncates — a listing must never
                    // pay to inflate an attacker-sized asset it only wants the size of. Measured once
                    // a stream (DecodedLengths).
                    dataLength = lengths.Of(mediaStream, "the asset stream could not be decoded", out unavailable);
                }

                if (unavailable != null)
                    _lastError = $"Page {pageIdx} ({sub}): {unavailable}.";

                result.Add(new RichMediaInfo
                {
                    PageIndex   = pageIdx,
                    Subtype     = sub,
                    ContentType = contentType,
                    DataLength  = dataLength,
                    DataLengthUnavailableReason = unavailable,
                    Description = description
                });
            }

            return result.AsReadOnly();
        }

        private static byte[] ExtractMediaBytes(PdfDictionary annotDict,
            string subtype, PdfDocument doc)
        {
            var st = FindMediaStream(annotDict, subtype, doc, out _, out string why);
            if (st == null)
            {
                _lastError = why ?? $"No embedded stream was found for the {subtype} annotation.";
                return Array.Empty<byte>();
            }

            // Uncapped: the caller asked for the bytes. A decode failure is reported rather
            // than thrown, so an empty return is always explained by LastError.
            try
            {
                return st.GetDecodedData() ?? Array.Empty<byte>();
            }
            catch (Exception ex)
            {
                _lastError = $"The {subtype} asset stream could not be decoded: {ex.Message}";
                return Array.Empty<byte>();
            }
        }

        /// <summary>
        /// Locates the single stream carrying an annotation's media payload.
        /// </summary>
        /// <remarks>
        /// <para><b>One implementation, so listing and extraction cannot disagree on which
        /// subtypes they cover.</b> They already had: both filters admitted
        /// <c>/Sound</c> and neither branch handled it, so a Sound annotation was listed with
        /// <c>DataLength 0</c> and <c>ContentType null</c> and extracted as <c>byte[0]</c> —
        /// the same values a genuinely empty asset and a malformed dictionary produce.</para>
        ///
        /// <para>A Sound annotation's audio is its own <c>/Sound</c> entry, a sound object
        /// (§12.5.6.16 / §13.3), which is a stream — not a file specification like /Movie's.</para>
        ///
        /// <para><paramref name="why"/> is set only when no stream was found, and says which
        /// link in the chain was missing rather than leaving the caller a bare empty array.</para>
        /// </remarks>
        private static PdfStream FindMediaStream(PdfDictionary annotDict, string subtype,
            PdfDocument doc, out string contentType, out string why)
        {
            contentType = null;
            why = null;

            if (subtype == PdfAnnotationSubtypes.RichMedia)
            {
                // ISO 32000-2 Table 334: /Assets is a name tree whose values are file specifications,
                // the payload under /EF /F — walked as a tree (PdfNameTree), its first embedded file
                // the asset; a value that is a bare stream, which some producers write, is taken as it is.
                var rmContent = doc.ResolveObject(annotDict[new PdfName("RichMediaContent")]);
                if (rmContent is PdfDictionary rmContentDict
                    && doc.ResolveObject(rmContentDict[new PdfName("Assets")]) is PdfDictionary assetsDict)
                {
                    var pairs = new List<KeyValuePair<PdfObject, PdfObject>>();
                    int budget = PdfLimits.MaxNameTreeEntries;
                    PdfNameTree.TryFlatten(assetsDict, doc, pairs, ref budget);
                    foreach (var pair in pairs)
                    {
                        var value = doc.ResolveObject(pair.Value);
                        PdfStream st = value as PdfStream;
                        if (st == null && value is PdfDictionary fileSpec
                            && doc.ResolveObject(fileSpec[PdfName.EF]) is PdfDictionary ef
                            && doc.ResolveObject(ef[PdfName.F]) is PdfStream embedded)
                            st = embedded;
                        if (st == null) continue;

                        // An embedded file's /Subtype is its MIME type (Table 44); a producer's
                        // /Params /MIMEType is read when it has none.
                        contentType = PdfResolve.Name(doc, st, PdfName.Subtype)?.Value;
                        if (contentType == null
                            && doc.ResolveObject(st[PdfName.Params]) is PdfDictionary paramsDict
                            && doc.ResolveObject(paramsDict[new PdfName("MIMEType")]) is PdfString mime)
                            contentType = mime.GetText();
                        return st;
                    }
                }
                why = "the /RichMediaContent /Assets name tree names no embedded file (a file specification with /EF /F) and no stream";
                return null;
            }

            if (subtype == PdfAnnotationSubtypes.Movie)
            {
                // /Movie -> /F (file spec) -> /EF /F (the embedded file stream)
                if (doc.ResolveObject(annotDict[new PdfName("Movie")]) is PdfDictionary movieDict
                    && doc.ResolveObject(movieDict[PdfName.F]) is PdfDictionary fileSpec
                    && doc.ResolveObject(fileSpec[PdfName.EF]) is PdfDictionary efDict
                    && doc.ResolveObject(efDict[PdfName.F]) is PdfStream st)
                    return st;

                why = "the /Movie file specification carries no /EF embedded file stream " +
                      "(an external movie file is referenced, not embedded)";
                return null;
            }

            if (subtype == PdfAnnotationSubtypes.Sound)
            {
                // ContentType stays null: a sound object states its sample encoding in /E
                // ("Raw", "Signed", "muLaw", "ALaw" — §13.3 Table 322), which is not a MIME
                // type, and this property is documented as one.
                if (doc.ResolveObject(annotDict[new PdfName("Sound")]) is PdfStream st)
                    return st;

                why = "the /Sound annotation has no /Sound stream";
                return null;
            }

            why = $"the {subtype} annotation subtype carries no extractable media stream";
            return null;
        }
    }
}
