// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;

namespace Tetitulz.Pdf.Plugins.Maintenance
{
    /// <summary>
    /// Statistics returned by <see cref="PdfOptimizer.Optimize(System.IO.Stream, System.IO.Stream, OptimizationOptions)"/>
    /// describing what was done.
    ///
    /// <para><b>Sizes are always measured.</b> The optimizer buffers the input to a seekable stream
    /// and counts the bytes it writes, so <see cref="InputBytes"/> and <see cref="OutputBytes"/> are
    /// exact even for a stream that cannot report its length, and <see cref="BytesSaved"/> and
    /// <see cref="CompressionRatio"/> answer "did this help?" truthfully.</para>
    ///
    /// <para><b>Counters distinguish "examined" from "improved".</b> A pass that examined eleven
    /// images and improved none is not the same as a document with no images. Check <see cref="ToleratedFailures"/> before reading a small saving as
    /// "there was nothing to do" — and see <c>DocumentEditDiagnostics.LastError</c> for the reason.</para>
    /// </summary>
    public sealed class OptimizationResult
    {
        /// <summary>Size of the input PDF in bytes. Always exact.</summary>
        public long InputBytes { get; init; }

        /// <summary>Size of the optimized output PDF in bytes. Always exact.</summary>
        public long OutputBytes { get; init; }

        /// <summary>
        /// Bytes saved (<see cref="InputBytes"/> − <see cref="OutputBytes"/>). Negative when
        /// optimization increased the size, which is possible unless
        /// <see cref="OptimizationOptions.NeverGrow"/> is set.
        /// </summary>
        public long BytesSaved => InputBytes - OutputBytes;

        /// <summary>Output/input ratio (1.0 means no change; 0.5 means 50 % smaller).</summary>
        public double CompressionRatio => InputBytes == 0 ? 1.0 : (double)OutputBytes / InputBytes;

        /// <summary>
        /// True when <see cref="OptimizationOptions.NeverGrow"/> was set and the optimized form came
        /// out no smaller than the input, so the input was copied through unchanged. Every other
        /// counter then describes work that was done and then discarded.
        /// </summary>
        public bool FellBackToInput { get; init; }

        /// <summary>Number of images that were examined for re-encoding.</summary>
        public int ImagesExamined { get; init; }

        /// <summary>Number of images that were re-encoded to a smaller representation.</summary>
        public int ImagesOptimized { get; init; }

        /// <summary>Number of duplicate objects that were removed.</summary>
        public int DuplicatesRemoved { get; init; }

        /// <summary>
        /// Number of <c>/XObject</c> resource entries removed by
        /// <see cref="OptimizationOptions.RemoveUnusedXObjectResources"/> because no content stream
        /// invokes them. The objects they named are counted in <see cref="UnreachableObjectsRemoved"/>
        /// once <see cref="OptimizationOptions.RemoveUnusedObjects"/> drops them.
        /// </summary>
        public int UnusedXObjectResourcesRemoved { get; init; }

        /// <summary>
        /// Runs of image strips merged into one image under
        /// <see cref="OptimizationOptions.MergeImageStrips"/>: each is one new image object and one
        /// <c>Do</c> where four or more stood.
        /// </summary>
        public int ImageStripsMerged { get; init; }

        /// <summary>Number of objects that were unreachable from the trailer and pruned.</summary>
        public int UnreachableObjectsRemoved { get; init; }

        /// <summary>
        /// Total objects removed — <see cref="UnreachableObjectsRemoved"/> plus
        /// <see cref="DuplicatesRemoved"/>. The two components are also reported separately because
        /// this sum counts a deduplicated object once even though it is both.
        /// </summary>
        public int ObjectsRemoved { get; init; }

        /// <summary>Number of streams that had FlateDecode compression applied or reapplied.</summary>
        public int StreamsCompressed { get; init; }

        /// <summary>Number of page content streams from which empty q…Q pairs were removed.</summary>
        public int ContentStreamsClean { get; init; }

        /// <summary>Number of pages whose multiple <c>/Contents</c> streams were merged into one.</summary>
        public int ContentStreamsMerged { get; init; }

        /// <summary>Number of objects that were packed into an object stream (<c>/ObjStm</c>).</summary>
        public int ObjectsPackedIntoStreams { get; init; }

        /// <summary>
        /// Number of bilevel images re-encoded from Flate (or raw) to CCITT Group 4 or JBIG2. Bit-exact:
        /// the samples are unchanged, only their storage.
        /// </summary>
        public int BilevelImagesRecompressed { get; init; }

        /// <summary>
        /// Of <see cref="BilevelImagesRecompressed"/>, the images that took JBIG2
        /// (<see cref="OptimizationOptions.UseJbig2ForBilevelImages"/>) — the shorter of the two where
        /// both passes were on.
        /// </summary>
        public int BilevelImagesRecompressedAsJbig2 { get; init; }

        /// <summary>
        /// Number of image streams re-encoded with a PNG scanline predictor
        /// (<see cref="OptimizationOptions.UsePngPredictorForImages"/>). Lossless: the predictor
        /// changes how the samples are stored, never what they mean, and a stream is rewritten only
        /// when the predicted form beats both what is stored and plain Flate — so a zero here on an
        /// image-heavy document means the samples did not predict, not that the pass was skipped.
        /// </summary>
        public int ImagesPredicted { get; init; }

        /// <summary>
        /// Number of images re-expressed as <c>/Indexed</c> over a palette
        /// (<see cref="OptimizationOptions.ConvertImagesToIndexed"/>). Lossless: every distinct
        /// colour became a palette entry, so no pixel changed value. A zero on an image-heavy
        /// document means the images are photographic — more than 256 distinct colours — not that
        /// the pass was skipped.
        /// </summary>
        public int ImagesPalettized { get; init; }

        /// <summary>
        /// Number of <c>/Metadata</c> entries removed by
        /// <see cref="OptimizationOptions.RemoveXmpMetadata"/> — the catalog's <b>and</b> every
        /// object-level one. The count exists because the two differ by orders of magnitude on real
        /// files: a 22 MB corpus document carried 214 packets totalling 9.7 MB, of which the
        /// catalog's was 3 439 bytes. The bytes only come back if
        /// <see cref="OptimizationOptions.RemoveUnusedObjects"/> is also on, since this removes the
        /// reference and leaves the pruning to that pass.
        /// </summary>
        public int XmpStreamsRemoved { get; init; }

        /// <summary>
        /// Number of baseline JPEGs whose Huffman tables were re-fitted to their own statistics
        /// (<see cref="OptimizationOptions.OptimizeJpegHuffmanTables"/>). Exactly lossless — the
        /// rebuilt image is verified to decode to the same symbol stream before it is accepted, so
        /// no pixel can differ. A zero on a photographic document means the JPEGs already carry
        /// fitted tables, not that the pass was skipped.
        /// </summary>
        public int JpegImagesRecompressed { get; init; }

        /// <summary>Number of embedded font programs that were reduced to the glyphs actually used.</summary>
        public int FontsSubset { get; init; }

        /// <summary>Bytes saved by font subsetting alone.</summary>
        public long FontBytesSaved { get; init; }

        /// <summary>
        /// Number of failures that were tolerated and skipped — an undecodable stream, an image
        /// whose colour space is not understood, a font that could not be proven complete. Non-zero
        /// means part of the document was left unoptimized on purpose; the most recent reason is on
        /// <c>Tetitulz.Pdf.Base.Operations.DocumentEditDiagnostics.LastError</c>.
        /// </summary>
        public int ToleratedFailures { get; init; }

        /// <summary>
        /// Image optimization failures keyed by PDF object number.
        /// Empty when all images optimized successfully or image optimization was disabled.
        /// </summary>
        public IReadOnlyDictionary<int, System.Exception> ImageFailures { get; init; }
            = new Dictionary<int, System.Exception>();
    }
}
