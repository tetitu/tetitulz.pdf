// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Operations.Extraction
{
    /// <summary>Information about a 3D annotation embedded in a PDF.</summary>
    public sealed class Pdf3DInfo
    {
        /// <summary>Zero-based page index where the annotation resides.</summary>
        public int PageIndex { get; init; }

        /// <summary>3D stream subtype: "U3D" or "PRC".</summary>
        public string StreamType { get; init; }

        /// <summary>
        /// Byte length of the 3D stream data, or 0 when it was not measured — see
        /// <see cref="DataLengthUnavailableReason"/>, which is the only way to tell an empty
        /// stream from an unmeasured one.
        /// </summary>
        public int DataLength { get; init; }

        /// <summary>
        /// Why <see cref="DataLength"/> is not the stream's true decoded length, or <c>null</c>
        /// when it is exact. Set when the stream passed the listing ceiling or would not decode.
        /// </summary>
        public string DataLengthUnavailableReason { get; init; }
    }

    /// <summary>
    /// Lists and extracts 3D annotations (U3D/PRC) embedded in a PDF.
    /// </summary>
    public static class Pdf3DExtractor
    {
        /// <summary>
        /// The per-stream decode ceiling <see cref="List3DAnnotations(Stream)"/> works under:
        /// 64&#160;MiB.
        /// </summary>
        /// <remarks>
        /// Listing is the cheap "what is in this file?" query a caller runs first; fully inflating
        /// every /3DD stream just to read <c>.Length</c> would let ten Flate-bomb annotations of
        /// ~1&#160;KB each allocate tens of gigabytes from a metadata call, so it is capped. Pass a different ceiling to
        /// <see cref="List3DAnnotations(Stream,long)"/>; the explicit
        /// <see cref="Extract3DStream(Stream,int)"/> — where the caller has actually asked for the
        /// bytes — is uncapped beyond Core's own <c>PdfLimits.MaxDecompressedBytes</c>.
        /// </remarks>
        public const long DefaultMaxListedStreamBytes = 64L * 1024 * 1024;

        [ThreadStatic] private static string _lastError;

        /// <summary>
        /// Why the most recent call on this thread could not measure or extract a 3D stream, or
        /// <c>null</c> when everything it touched decoded. Reset at the start of every public
        /// entry point.
        /// </summary>
        public static string LastError => _lastError;

        /// <summary>Lists all 3D annotations in a PDF file.</summary>
        public static IReadOnlyList<Pdf3DInfo> List3DAnnotations(string inputPath)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var fs = File.OpenRead(inputPath);
            return List3DAnnotations(fs);
        }

        /// <summary>Lists all 3D annotations in a PDF stream.</summary>
        public static IReadOnlyList<Pdf3DInfo> List3DAnnotations(Stream input)
            => List3DAnnotations(input, DefaultMaxListedStreamBytes);

        /// <summary>
        /// Lists all 3D annotations in a PDF stream, measuring each /3DD stream under an explicit
        /// per-stream decode ceiling. A stream past the ceiling is reported with
        /// <see cref="Pdf3DInfo.DataLengthUnavailableReason"/> set rather than inflated.
        /// </summary>
        public static IReadOnlyList<Pdf3DInfo> List3DAnnotations(Stream input, long maxStreamBytes)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (maxStreamBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxStreamBytes));
            _lastError = null;
            using var doc = PdfDocument.Open(input);
            EncryptedRead.Refuse(doc, "the 3D annotations", "PdfOperations.List3DAnnotations(Stream, string password)");
            return Collect3D(doc, maxStreamBytes);
        }

        /// <summary>Extracts the raw decoded bytes of a 3D stream by annotation index.</summary>
        public static byte[] Extract3DStream(string inputPath, int annotationIndex)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var fs = File.OpenRead(inputPath);
            return Extract3DStream(fs, annotationIndex);
        }

        /// <summary>Extracts the raw decoded bytes of a 3D stream by annotation index.</summary>
        public static byte[] Extract3DStream(Stream input, int annotationIndex)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            _lastError = null;
            using var doc = PdfDocument.Open(input);
            EncryptedRead.Refuse(doc, "the 3D annotations", "PdfOperations.Extract3DStream(Stream, int, string password)");

            int count = 0;
            // The walk, the resolve and the subtype gate are shared with Collect3D below and with
            // PdfRichMediaExtractor's pair — see AnnotationSubtypeWalk for the drift that motivated it.
            foreach (var (_, annotDict, _) in
                     AnnotationSubtypeWalk.BySubtype(doc, new AnnotationWalkReport(m => _lastError ??= m), PdfAnnotationSubtypes.ThreeD))
            {
                if (count == annotationIndex)
                {
                    var stream3D = Get3DStream(annotDict, doc, out string noStream);
                    if (stream3D == null)
                    {
                        _lastError = $"The 3D annotation at index {annotationIndex}: {noStream}.";
                        return Array.Empty<byte>();
                    }
                    // Uncapped here by design: the caller asked for the bytes, so Core's own
                    // PdfLimits.MaxDecompressedBytes is the only ceiling that applies.
                    return stream3D.GetDecodedData() ?? Array.Empty<byte>();
                }
                count++;
            }

            throw new ArgumentOutOfRangeException(nameof(annotationIndex),
                $"No 3D annotation at index {annotationIndex}.");
        }

        // -------------------------------------------------------------------
        //  Implementation
        // -------------------------------------------------------------------

        private static IReadOnlyList<Pdf3DInfo> Collect3D(PdfDocument doc, long maxStreamBytes)
        {
            var result = new List<Pdf3DInfo>();
            var lengths = new DecodedLengths(maxStreamBytes);

            // An annotation whose /Subtype will not resolve is dropped from the listing by the
            // shared walk, so a caller cannot tell "this file has no 3D content" from "the
            // annotation saying so was unreadable".
            foreach (var (pageIdx, annotDict, _) in
                     AnnotationSubtypeWalk.BySubtype(doc, new AnnotationWalkReport(m => _lastError ??= m), PdfAnnotationSubtypes.ThreeD))
            {
                var stream3D = Get3DStream(annotDict, doc, out string noStream);
                string streamType = null;
                int dataLength = 0;
                string unavailable = noStream;

                if (stream3D != null)
                {
                    // Resolved, like the two annotation gates above. This one does not drop the
                    // annotation — it reports StreamType null, so a /3DD stream declaring
                    // /Subtype 12 0 R is listed as a 3D annotation of unknown kind while the
                    // file states U3D or PRC plainly.
                    var stName = PdfResolve.Name(doc, stream3D, PdfName.Subtype);
                    streamType = stName?.Value;

                    // Bounded, and it refuses rather than truncates: TryGetDecodedData hands
                    // back no bytes at all when the ceiling is hit, so a refusal can never be
                    // mistaken for a short stream that decoded completely. It also turns a
                    // corrupt filter chain into a reason string instead of letting a raw
                    // filter exception escape a "List" API mid-enumeration. Measured once a stream
                    // (DecodedLengths): a refused decode is not cached by the stream itself.
                    dataLength = lengths.Of(stream3D, "the 3D stream could not be decoded", out unavailable);
                }

                if (unavailable != null)
                    _lastError = $"Page {pageIdx}: {unavailable}.";

                result.Add(new Pdf3DInfo
                {
                    PageIndex  = pageIdx,
                    StreamType = streamType,
                    DataLength = dataLength,
                    DataLengthUnavailableReason = unavailable
                });
            }

            return result.AsReadOnly();
        }

        /// <summary>
        /// The annotation's 3D stream: its <c>/3DD</c> directly, or, when that is a 3D reference
        /// dictionary (§13.6.2, <c>/Type /3DRef</c>), the reference's own <c>/3DD</c>, followed once.
        /// <paramref name="unavailable"/> says why there is none, so a listing does not report a
        /// stream it never found as a measured empty one.
        /// </summary>
        private static PdfStream Get3DStream(PdfDictionary annotDict, PdfDocument doc, out string unavailable)
        {
            unavailable = null;
            var threeDdObj = doc.ResolveObject(annotDict[PdfName.ThreeDD]);
            if (threeDdObj is PdfStream direct) return direct;

            if (threeDdObj is PdfDictionary reference
                && PdfResolve.Name(doc, reference, PdfName.Type)?.Value == PdfName.ThreeDRef.Value)
            {
                if (doc.ResolveObject(reference[PdfName.ThreeDD]) is PdfStream referenced) return referenced;
                unavailable = "the /3DD 3D reference dictionary names no /3DD stream";
                return null;
            }

            unavailable = threeDdObj == null || threeDdObj is PdfNull
                ? "the annotation has no /3DD stream"
                : "the annotation's /3DD is neither a stream nor a 3D reference dictionary";
            return null;
        }
    }
}
