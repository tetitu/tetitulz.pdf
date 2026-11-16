// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SkiaSharp;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Fonts;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Plugins.Maintenance
{
    /// <summary>
    /// Optimizes PDF files by compressing streams, removing duplicates,
    /// pruning unreachable objects, and optionally stripping metadata.
    /// </summary>
    public static partial class PdfOptimizer
    {
        // Mutable stats accumulated during a single Optimize() call.
        internal sealed class OptimizationStats
        {
            public int ImagesExamined;
            public int ImagesOptimized;
            public int StreamsCompressed;
            public int ContentStreamsClean;
            public int ContentStreamsMerged;
            public int ObjectsPackedIntoStreams;
            public int BilevelImagesRecompressed;
            public int BilevelImagesRecompressedAsJbig2;
            public int ImagesPredicted;
            public int ImagesPalettized;
            public int XmpStreamsRemoved;
            public int UnusedXObjectResourcesRemoved;
            public int ImageStripsMerged;
            public int JpegImagesRecompressed;
            public int FontsSubset;
            public long FontBytesSaved;
            public int ToleratedFailures;
            public Dictionary<int, Exception> ImageFailures = new Dictionary<int, Exception>();
        }

        /// <summary>
        /// Records a tolerated failure. Every <c>catch</c> that carries on, every "some other filter
        /// — skip", every image whose colour space is not understood goes through here.
        ///
        /// <para>So a 2 % saving and "nine of your ten streams threw" are different results.
        /// <see cref="OptimizationResult.ToleratedFailures"/> is the count and
        /// <c>DocumentEditDiagnostics.LastError</c> is the most recent reason; a host wanting all of
        /// them subscribes to <c>DocumentEditDiagnostics.OnEvent</c>.</para>
        /// </summary>
        private static void Report(string operation, string detail, OptimizationStats stats,
            Exception ex = null)
        {
            stats.ToleratedFailures++;
            DocumentEditDiagnostics.Report(operation, detail, ex);
        }

        /// <summary>
        /// Follows an indirect reference through the object map.
        ///
        /// <para><b>Every dictionary read in this file must go through here.</b>
        /// <see cref="PdfDictionary"/>'s indexer does not resolve references, so
        /// <c>dict[PdfName.Filter] as PdfName</c> silently yields null whenever a producer wrote
        /// <c>/Filter 12 0 R</c> — indistinguishable from "there is no filter". §7.3.8.2 permits
        /// every value but <c>/Length</c> to be indirect, and reading an indirect <c>/Filter</c> as
        /// "no filter" would make the compressor deflate already-deflated bytes and declare a single
        /// <c>/FlateDecode</c> over doubly-compressed data. Unlike a renderer, a wrong answer here is
        /// written to disk.</para>
        ///
        /// <para><b>The hop loop itself lives in <see cref="ContentScanGraph.Deref"/></b>, the one copy
        /// in this assembly. This stays as a forwarder rather than being inlined at its call sites,
        /// because the paragraphs above are the reason those call sites exist.</para>
        /// </summary>
        private static PdfObject Deref(PdfObject obj, Dictionary<int, PdfObject> objects)
            => ContentScanGraph.Deref(obj, objects);

        private static int? DerefInteger(PdfDictionary dict, PdfName key,
            Dictionary<int, PdfObject> objects)
            => Deref(dict[key], objects) is PdfNumber number ? number.IntValue : (int?)null;

        private static PdfName DerefName(PdfDictionary dict, PdfName key,
            Dictionary<int, PdfObject> objects)
            => Deref(dict[key], objects) as PdfName;

        /// <summary>
        /// Resolves an image's sample component count from its <c>/ColorSpace</c>, following
        /// indirect references and handling the array forms.
        ///
        /// <para><b>Refuses rather than guesses.</b> A default of three-component 8-bpc RGB for every
        /// space it does not recognise would read an 8-bpc <c>/DeviceCMYK</c> image three bytes at a
        /// time — a channel-skewed, wrongly-coloured image that compresses well enough to be written
        /// back and counted as a saving.</para>
        /// </summary>
        private static bool TryGetComponentCount(PdfStream stream, Dictionary<int, PdfObject> objects,
            out int components, out string reason)
        {
            components = 0;
            reason = null;

            var cs = Deref(stream[PdfName.ColorSpace], objects);

            if (cs == null)
            {
                // §8.9.5.1: an image mask has no colour space and one bit per sample. It is not a
                // failure, simply not something this pass handles.
                reason = "the image has no /ColorSpace (an image mask, or a malformed dictionary)";
                return false;
            }

            if (cs is PdfName name)
            {
                components = PdfColorSpaceNames.ComponentCount(name.Value, 0);
                if (components > 0) return true;
                reason = "colour space /" + name.Value + " has no fixed component count";
                return false;
            }

            if (cs is PdfArray array && array.Count > 0
                && Deref(array[0], objects) is PdfName family)
            {
                switch (family.Value)
                {
                    case PdfColorSpaceNames.ICCBased:
                        if (array.Count > 1 && Deref(array[1], objects) is PdfStream profile
                            && DerefInteger(profile, PdfName.N, objects) is int n && n > 0)
                        {
                            components = n;
                            return true;
                        }
                        reason = "an /ICCBased colour space whose /N could not be read";
                        return false;

                    case PdfColorSpaceNames.DeviceN:
                        if (array.Count > 1 && Deref(array[1], objects) is PdfArray colorants
                            && colorants.Count > 0)
                        {
                            components = colorants.Count;
                            return true;
                        }
                        reason = "a /DeviceN colour space whose colorant array could not be read";
                        return false;

                    default:
                        components = PdfColorSpaceNames.ComponentCount(family.Value, 0);
                        if (components > 0) return true;
                        reason = "colour space /" + family.Value + " has no fixed component count";
                        return false;
                }
            }

            reason = "the /ColorSpace is neither a name nor a recognised array";
            return false;
        }

        /// <summary>
        /// Optimizes a PDF file and writes the result to a new file.
        /// </summary>
        public static OptimizationResult Optimize(string inputPath, string outputPath,
            OptimizationOptions options = null)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            if (outputPath == null) throw new ArgumentNullException(nameof(outputPath));

            using (var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                return OptimizeCore(input, output, options);
            }
        }

        /// <summary>
        /// Optimizes a PDF from an input stream and writes the result to an output stream.
        /// </summary>
        public static OptimizationResult Optimize(Stream input, Stream output,
            OptimizationOptions options = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));

            return OptimizeCore(input, output, options);
        }

        /// <summary>
        /// The one optimization run. The input's size is measured inside, after
        /// <c>EnsureSeekable</c>, and that is the size <c>NeverGrow</c> compares against.
        /// </summary>
        private static OptimizationResult OptimizeCore(Stream input, Stream output,
            OptimizationOptions options)
        {
            options = options ?? OptimizationOptions.Default;

            // Reset at the START of the operation, on every path. Per-thread storage is not
            // per-document scope: a slot written by an earlier call on this thread would otherwise
            // be read as a diagnosis of this one.
            DocumentEditDiagnostics.Clear();

            var stats = new OptimizationStats();

            // Normalise the input. Sizes have to be exact even for a stream that cannot report its
            // length, so BytesSaved and CompressionRatio answer "did this help?" truthfully.
            // NeverGrow also needs the original bytes to hand back.
            Stream source = PdfHelpers.EnsureSeekable(input);
            source.Position = 0;
            long inputBytes = source.Length;

            // NeverGrow needs the original bytes to hand back, and the fallback's whole promise is
            // that what it hands back IS the input. A Stream whose Length over-reports — one
            // truncated by another process between the two calls, or a shim over a chunked source —
            // leaves the shortfall as the zero bytes `new byte[]` supplied, and the fallback would
            // then write a NUL-padded file while reporting FellBackToInput and a byte-identical
            // size. Refusing the fallback is the honest answer: the optimized bytes are the only
            // ones this call can vouch for.
            byte[] originalBytes = null;
            bool neverGrow = options.NeverGrow;
            if (neverGrow)
            {
                originalBytes = new byte[inputBytes];
                int read = Core.IO.StreamBytes.Fill(source, originalBytes, 0, originalBytes.Length);
                source.Position = 0;

                if (read != originalBytes.Length)
                {
                    Report("Optimize",
                        "The input stream reported " + inputBytes + " bytes but delivered " + read +
                        "; the retained copy is not the input, so the NeverGrow fallback was " +
                        "disabled for this call and the optimized bytes were written instead.", stats);
                    originalBytes = null;
                    neverGrow = false;
                }
            }

            // With NeverGrow the optimized form has to be measured before it is committed, so it is
            // built in memory. Without it the bytes go straight out through a counting wrapper —
            // buffering an output nobody is going to reject is pure cost.
            var buffer = neverGrow ? new MemoryStream() : null;
            var counting = buffer == null ? new CountingStream(output) : null;
            Stream sink = (Stream)buffer ?? counting;

            OptimizationResult result;

            using (var doc = PdfDocument.Open(source))
            {
                // Fail closed on encrypted input: optimization renumbers objects while the
                // /Encrypt dictionary is carried over unchanged, and per-object keys are
                // derived from the object number — the output would not be decryptable.
                if (doc.IsEncrypted)
                    throw new PdfEncryptedDocumentException(
                        "Encrypted PDFs are not supported by PdfOptimizer. Run the operation through " +
                        "Tetitulz.Pdf.Encryption.PdfEncryptedRoundTrip.Run, which decrypts, optimizes and " +
                        "re-encrypts in one step; or decrypt manually with PdfDecryptor, optimize, then " +
                        "re-encrypt. Note that either route rewrites every byte, so it cannot preserve a " +
                        "signature or the owner password.");

                // Fail closed on a page tree that yields no page. The rewrite keeps what the
                // trailer reaches, so a tree this reader cannot follow would come out as an empty
                // document: smaller than the input, so NeverGrow would pass it as a saving of up to
                // 99.95 %. An empty file is an optimisation of nothing.
                if (doc.Pages.Count == 0)
                    throw new PdfException(
                        "The document's page tree yields no page, so optimizing it would write a " +
                        "document with nothing in it. Either the file has no pages or its page tree " +
                        "could not be read; nothing was written.");

                var parser = doc.Parser;
                var version = doc.Version;

                // Step 1: Collect all objects from the cross-reference table.
                //
                // The onError overload, so a partial parse is reported: an object that fails to
                // parse is otherwise simply absent, every reference to it is rewritten to null by
                // RemapReferences, and the result is a *smaller* file reported as a success. A
                // document we could not fully read is not a document we can safely prune.
                var allObjects = new Dictionary<int, PdfObject>();
                var unparsable = new List<int>();
                foreach (var kvp in parser.EnumerateAllObjects(
                    (objNum, ex) => unparsable.Add(objNum)))
                {
                    if (allObjects.Count >= options.MaxObjectCount)
                        throw new PdfException(
                            "The document declares more than OptimizationOptions.MaxObjectCount (" +
                            options.MaxObjectCount.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                            ") indirect objects. Optimizing a document that was only partly read would " +
                            "prune objects that are in fact referenced; raise the ceiling to proceed.");

                    allObjects[kvp.Key] = kvp.Value;
                }

                if (unparsable.Count > 0)
                {
                    // Reported rather than thrown: a lightly damaged file is still worth optimizing,
                    // but the caller must be able to find out that references to these objects were
                    // rewritten to null rather than preserved.
                    Report("Optimize.Load",
                        unparsable.Count + " object(s) could not be parsed and were dropped; every " +
                        "reference to them becomes null in the output. Object numbers: " +
                        string.Join(", ", unparsable.ConvertAll(n =>
                            n.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
                        stats);
                }

                // A whole-file rewrite renumbers every object, so a signature's /ByteRange names
                // offsets that no longer exist: the output looks signed and verifies as corrupt.
                // Every other whole-file rewriter in this repo refuses on the same grounds.
                if (!options.AllowSignatureInvalidation
                    && PdfSignatureDetection.HasSignature(doc.Trailer,
                        n => allObjects.TryGetValue(n, out var o) ? o : null))
                {
                    throw new InvalidOperationException(
                        "The document carries a digital signature. Optimizing rewrites every object, " +
                        "so the signature's /ByteRange would name offsets that no longer exist and the " +
                        "output would verify as corrupt rather than merely modified. Set " +
                        "OptimizationOptions.AllowSignatureInvalidation to proceed anyway.");
                }

                // Step 1a: Build the output trailer NOW, before reachability.
                //
                // This ordering matters for privacy: reachability computed from the ORIGINAL trailer,
                // with /Info dropped afterwards, would keep the document information dictionary —
                // author, title, producer, dates — reachable and write it into the "cleaned" output
                // as an orphan, while the missing trailer key made the output look right.
                var trailerDict = BuildOutputTrailer(doc.Trailer);
                if (options.RemoveMetadata)
                {
                    trailerDict.Remove(PdfName.Info);
                }

                // Step 1a2: Merge runs of image strips into one image — before the image passes,
                // which then see one image where a printer driver wrote a band per row, and the
                // downsampler measures the whole picture rather than each sliver.
                if (options.MergeImageStrips)
                {
                    try
                    {
                        stats.ImageStripsMerged = MergeImageStrips(allObjects, trailerDict, options, stats);
                    }
                    catch (Exception ex)
                    {
                        Report("Optimize.MergeImageStrips", ex.Message, stats, ex);
                    }
                }

                // Step 1b: Optimize images (re-encode, resize, change format/quality).
                if (options.ImageOptimization != null && options.ImageOptimization.Enabled)
                {
                    OptimizeImages(allObjects, trailerDict, options.ImageOptimization, options, stats);
                }

                // Step 1c: Remove annotations and their appearance streams.
                if (options.RemoveAnnotations)
                {
                    RemoveAnnotations(allObjects, trailerDict);
                }

                // Step 1d: Remove tagged-PDF structure trees.
                if (options.RemoveDocumentStructure)
                {
                    RemoveDocumentStructure(allObjects, trailerDict, stats);
                }

                // Step 1e: Clean content streams (remove empty q…Q pairs).
                if (options.CleanContentStreams)
                {
                    stats.ContentStreamsClean = CleanContentStreams(allObjects, trailerDict, options, stats);
                }

                // Step 1f: Concatenate multi-stream /Contents into a single stream.
                if (options.ConcatenateContentStreams)
                {
                    stats.ContentStreamsMerged =
                        ConcatenateContentStreams(allObjects, trailerDict, options, stats);
                }

                // Step 1g: Remove every XMP metadata stream, not only the catalog's.
                if (options.RemoveXmpMetadata)
                {
                    stats.XmpStreamsRemoved = RemoveXmpMetadata(allObjects, trailerDict);
                }

                // Step 1h: Remove embedded page thumbnail images.
                if (options.RemoveThumbnails)
                {
                    RemoveThumbnails(allObjects, trailerDict);
                }

                // Step 1h1: Remove /XObject resource entries no content stream invokes, so step 4
                // drops the images and forms they named. Before the image passes, which then do not
                // spend work on images nothing draws.
                if (options.RemoveUnusedXObjectResources)
                {
                    try
                    {
                        stats.UnusedXObjectResourcesRemoved = UnusedXObjectResources.Remove(
                            allObjects, trailerDict, options, out string unusedRefusal);
                        if (unusedRefusal != null)
                            Report("Optimize.RemoveUnusedXObjectResources", unusedRefusal, stats);
                    }
                    catch (Exception ex)
                    {
                        Report("Optimize.RemoveUnusedXObjectResources", ex.Message, stats, ex);
                    }
                }

                // Step 1h2: Re-express small-alphabet images over a palette.
                //
                // Before the predictor and the CCITT passes on purpose: it changes an image's
                // component count and bit depth, and both of those passes read those keys. Running
                // it first means they measure the image the caller will actually ship, and the
                // predictor's "must beat plain Flate" rule then correctly declines on the index
                // data, which is small-alphabet by construction.
                if (options.ConvertImagesToIndexed)
                {
                    ConvertImagesToIndexed(allObjects, options, stats);
                }

                // Step 1i: Re-encode lossless image streams with PNG predictor.
                if (options.UsePngPredictorForImages)
                {
                    ApplyPngPredictorToImages(allObjects, options, stats);
                }

                // Step 1i2: Re-encode bilevel images with CCITT Group 4, JBIG2, or whichever is shorter.
                if (options.UseCcittForBilevelImages || options.UseJbig2ForBilevelImages)
                {
                    RecompressBilevelImages(allObjects, options, stats);
                }

                // Step 1j: Reduce embedded font programs to the glyphs actually drawn.
                if (options.SubsetEmbeddedFonts)
                {
                    SubsetFonts(allObjects, trailerDict, options, stats);
                }

                // Step 2: Compress/re-encode streams.
                if (options.CompressStreams || options.ReplaceAsciiFilters || options.ReplaceLzwWithFlate)
                {
                    CompressStreams(allObjects, options, stats);
                }

                // Step 2b: Re-fit each baseline JPEG's Huffman tables to its own statistics.
                //
                // After the image re-encoder (step 1b) on purpose: when that pass writes a JPEG it
                // does so through Skia, which emits the ISO Annex K stock tables — so its output is
                // exactly the input this pass improves. And after step 2, which is the one pass that
                // peels an ASCII layer: a JPEG filtered [/ASCII85Decode /DCTDecode] is a bare
                // /DCTDecode by now, so it is reached on this same pass through the document.
                if (options.OptimizeJpegHuffmanTables)
                {
                    RecompressJpegImages(allObjects, stats);
                }

                // Step 3: Remove duplicate objects and build a dedup mapping.
                var dedupMap = new Dictionary<int, int>(); // old obj# -> canonical obj#
                if (options.RemoveDuplicates)
                {
                    DeduplicateObjects(allObjects, trailerDict, dedupMap);
                }

                // Step 4: Determine the set of reachable objects, from the OUTPUT trailer.
                HashSet<int> reachable = null;
                if (options.RemoveUnusedObjects)
                {
                    reachable = FindReachableObjects(trailerDict, allObjects, dedupMap);
                }

                // Step 6: Build the final list of objects to write, filtering by
                // reachability and applying dedup mapping.
                var objectsToWrite = new Dictionary<int, PdfObject>();
                int duplicatesDropped = 0;
                int unreachableDropped = 0;
                foreach (var kvp in allObjects)
                {
                    int objNum = kvp.Key;

                    // Skip objects that were deduplicated away.
                    if (dedupMap.TryGetValue(objNum, out int canonical) && canonical != objNum)
                    {
                        duplicatesDropped++;
                        continue;
                    }

                    // Skip unreachable objects when pruning is enabled.
                    if (reachable != null && !reachable.Contains(objNum))
                    {
                        unreachableDropped++;
                        continue;
                    }

                    objectsToWrite[objNum] = kvp.Value;
                }

                int objectsRemoved = duplicatesDropped + unreachableDropped;

                // Step 7: Remap object numbers to a sequential range starting at 1.
                var remapTable = new Dictionary<int, int>();
                int nextObjNum = 1;
                foreach (int oldNum in objectsToWrite.Keys.OrderBy(k => k))
                {
                    remapTable[oldNum] = nextObjNum++;
                }

                // Also map dedup redirections through the remap table.
                foreach (var kvp in dedupMap)
                {
                    if (!remapTable.ContainsKey(kvp.Key) && remapTable.ContainsKey(kvp.Value))
                    {
                        remapTable[kvp.Key] = remapTable[kvp.Value];
                    }
                }

                // Step 8: Rewrite all references inside objects to use new numbers.
                var finalObjects = new List<(int newNum, PdfObject obj)>();
                foreach (var kvp in objectsToWrite.OrderBy(x => x.Key))
                {
                    int newNum = remapTable[kvp.Key];
                    var rewritten = RemapReferences(kvp.Value, remapTable, dedupMap);
                    finalObjects.Add((newNum, rewritten));
                }

                // Rewrite trailer references.
                var finalTrailer = RemapTrailer(trailerDict, remapTable, dedupMap);
                finalTrailer.Set(PdfName.Size, new PdfNumber(nextObjNum));

                // Step 9: Write the optimized PDF.
                if (options.UseObjectStreams)
                {
                    stats.ObjectsPackedIntoStreams = PdfCompressedFileWriter.Write(
                        sink,
                        RaiseForObjectStreams(version),
                        finalObjects,
                        finalTrailer,
                        new PdfCompressedWriteOptions
                        {
                            CompressionLevel = options.FlateCompressionLevel,
                        });
                }
                else
                {
                    var writer = new PdfWriter(sink);
                    writer.WriteHeader(version);

                    var xrefEntries = new List<(int objNum, long offset)>();

                    foreach (var (num, obj) in finalObjects)
                    {
                        long offset = writer.WriteIndirectObject(num, 0, obj);
                        xrefEntries.Add((num, offset));
                    }

                    long xrefOffset = writer.WriteXrefTable(xrefEntries);
                    writer.WriteTrailer(finalTrailer, xrefOffset);
                }

                long optimizedBytes = buffer != null ? buffer.Length : counting.BytesWritten;
                bool fellBack = false;

                if (buffer != null)
                {
                    // NeverGrow: hand back whichever is smaller. An operation called "optimize" that
                    // returns a bigger file has not optimized anything, and without object streams
                    // that is the normal outcome for an input which already used them — they are
                    // unpacked on read and written back out individually.
                    if (optimizedBytes >= inputBytes)
                    {
                        output.Write(originalBytes, 0, originalBytes.Length);
                        optimizedBytes = inputBytes;
                        fellBack = true;
                    }
                    else
                    {
                        buffer.Position = 0;
                        buffer.CopyTo(output);
                    }
                }

                result = new OptimizationResult
                {
                    InputBytes = inputBytes,
                    OutputBytes = optimizedBytes,
                    FellBackToInput = fellBack,
                    ImagesExamined = stats.ImagesExamined,
                    ImagesOptimized = stats.ImagesOptimized,
                    DuplicatesRemoved = duplicatesDropped,
                    UnreachableObjectsRemoved = unreachableDropped,
                    ObjectsRemoved = objectsRemoved,
                    StreamsCompressed = stats.StreamsCompressed,
                    ContentStreamsClean = stats.ContentStreamsClean,
                    ContentStreamsMerged = stats.ContentStreamsMerged,
                    ObjectsPackedIntoStreams = stats.ObjectsPackedIntoStreams,
                    BilevelImagesRecompressed = stats.BilevelImagesRecompressed,
                    BilevelImagesRecompressedAsJbig2 = stats.BilevelImagesRecompressedAsJbig2,
                    ImagesPredicted = stats.ImagesPredicted,
                    ImagesPalettized = stats.ImagesPalettized,
                    XmpStreamsRemoved = stats.XmpStreamsRemoved,
                    UnusedXObjectResourcesRemoved = stats.UnusedXObjectResourcesRemoved,
                    ImageStripsMerged = stats.ImageStripsMerged,
                    JpegImagesRecompressed = stats.JpegImagesRecompressed,
                    FontsSubset = stats.FontsSubset,
                    FontBytesSaved = stats.FontBytesSaved,
                    ToleratedFailures = stats.ToleratedFailures,
                    ImageFailures = stats.ImageFailures
                };
            }

            buffer?.Dispose();
            return result;
        }

        /// <summary>
        /// Object streams and cross-reference streams were introduced in PDF 1.5 (§7.5.7), so a file
        /// using them must not claim to be older — a 1.4 reader would find neither the objects nor
        /// the cross-reference table.
        /// </summary>
        private static PdfVersion RaiseForObjectStreams(PdfVersion version)
        {
            var minimum = new PdfVersion(1, 5);
            return version < minimum ? minimum : version;
        }

        /// <summary>
        /// Counts the bytes written through to an underlying stream, so the result's
        /// <see cref="OptimizationResult.OutputBytes"/> is exact even when the caller's stream
        /// cannot report a position.
        /// </summary>
        private sealed class CountingStream : Stream
        {
            private readonly Stream _inner;

            internal CountingStream(Stream inner) { _inner = inner; }

            internal long BytesWritten { get; private set; }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => BytesWritten;

            public override long Position
            {
                get => BytesWritten;
                set => throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                _inner.Write(buffer, offset, count);
                BytesWritten += count;
            }

            public override void WriteByte(byte value)
            {
                _inner.WriteByte(value);
                BytesWritten++;
            }

            public override void Flush() => _inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        // -------------------------------------------------------------------
        //  Stream compression
        // -------------------------------------------------------------------

        private static void CompressStreams(Dictionary<int, PdfObject> objects,
            OptimizationOptions opts, OptimizationStats stats)
        {
            var level = opts.FlateCompressionLevel;
            foreach (var kvp in objects)
            {
                if (!(kvp.Value is PdfStream stream))
                    continue;

                if (stream.RawData == null || stream.RawData.Length == 0)
                    continue;

                // ISO 19005-1 §6.7.2 requires a PDF/A document's XMP packet to be locatable without
                // decoding it, so compressing this one stream de-conforms an otherwise valid file —
                // and it happened under DEFAULT options. PdfWriter already exempts it from
                // encryption by name for exactly the same reason.
                if (DerefName(stream, PdfName.Type, objects) is PdfName streamType
                    && streamType.Value == "Metadata")
                {
                    continue;
                }

                var chain = GetFilterChain(stream, objects);
                if (chain == null)
                {
                    // A /Filter we could not read as a name or an array of names. Treating that as
                    // "no filter" is what turned an indirect /Filter into a double-deflate; the only
                    // safe reading of an unknown filter shape is "leave this stream alone".
                    Report("Optimize.CompressStreams",
                        "Object " + kvp.Key + " has a /Filter that is neither a name nor an array of " +
                        "names; the stream was left untouched rather than risk re-encoding it.", stats);
                    continue;
                }

                // Peel the ASCII transport layer, if the caller asked and there is one.
                if (opts.ReplaceAsciiFilters && chain.Count > 0 && PdfFilterNames.IsAscii(chain[0]))
                {
                    if (!TryPeelLeadingFilter(stream, chain, objects, opts, stats, kvp.Key))
                        continue;
                }

                // LZW → Flate: peel the LZW layer here and let the re-encode below put Flate back.
                if (opts.ReplaceLzwWithFlate && chain.Count > 0 && PdfFilterNames.IsLzw(chain[0]))
                {
                    if (!TryPeelLeadingFilter(stream, chain, objects, opts, stats, kvp.Key))
                        continue;
                }

                if (!opts.CompressStreams)
                    continue;

                // Anything left below the top layer that is a codec means the bytes are an encoded
                // image, not samples. Re-Flating them would at best waste time and at worst — if the
                // /Filter were then rewritten to a bare /FlateDecode — declare a JPEG bitstream as
                // raw samples.
                if (chain.Count > 1 || (chain.Count == 1 && PdfFilterNames.IsImageCodec(chain[0])))
                {
                    continue;
                }

                bool alreadyFlate = chain.Count == 1 && PdfFilterNames.IsFlate(chain[0]);

                if (alreadyFlate && !opts.RecompressExistingStreams)
                    continue;

                if (!alreadyFlate && chain.Count != 0)
                    continue;   // an unfamiliar single filter — leave it alone

                // Trial-and-commit: /Filter is changed only once the re-encode has won, so a stream
                // that does not compress keeps its encoding, /DecodeParms predictor included.
                byte[] originalRaw = stream.RawData;
                PdfObject originalFilter = stream[PdfName.Filter];
                PdfObject originalParms = stream[PdfName.DecodeParms];

                byte[] decoded;
                if (alreadyFlate)
                {
                    if (!TryDecodeBounded(stream, objects, opts, out decoded, out string reason))
                    {
                        Report("Optimize.CompressStreams",
                            "Object " + kvp.Key + " could not be decoded for re-compression (" + reason +
                            "); the original bytes were kept.", stats);
                        continue;
                    }
                }
                else
                {
                    decoded = originalRaw;
                }

                byte[] compressed = FlateEncodeFilter.Encode(decoded, level);

                // Compare against what is actually in the file today, not against the decoded size:
                // re-compressing a Flate stream only wins if it beats the ENCODED original.
                if (compressed == null || compressed.Length >= originalRaw.Length)
                {
                    stream.RawData = originalRaw;
                    if (originalFilter != null) stream.Set(PdfName.Filter, originalFilter);
                    if (originalParms != null) stream.Set(PdfName.DecodeParms, originalParms);
                    continue;
                }

                stream.RawData = compressed;
                stream.Set(PdfName.Filter, PdfName.FlateDecode);
                stream.Set(PdfName.Length, new PdfNumber(compressed.Length));
                stream.Remove(PdfName.DecodeParms);
                stats.StreamsCompressed++;
            }
        }

        /// <summary>
        /// Decodes exactly the first filter of <paramref name="chain"/> and rewrites the stream's
        /// <c>/Filter</c> and <c>/DecodeParms</c> to the remaining layers.
        ///
        /// <para><b>Peeling one layer is the entire point.</b> Removing the whole <c>/Filter</c>
        /// array after decoding its first layer would leave, for <c>/Filter [/ASCII85Decode
        /// /DCTDecode]</c> — the standard shape for a JPEG in a PostScript-derived PDF — the JPEG
        /// bitstream in the file with no filter at all while <c>/Width</c>, <c>/Height</c> and
        /// <c>/ColorSpace</c> still describe raw samples: noise in every reader, and damage that
        /// cannot be undone.</para>
        /// </summary>
        /// <returns>False when the layer could not be decoded; the stream is then left untouched.</returns>
        private static bool TryPeelLeadingFilter(PdfStream stream, List<string> chain,
            Dictionary<int, PdfObject> objects, OptimizationOptions opts, OptimizationStats stats,
            int objectNumber)
        {
            var parms = GetDecodeParmsChain(stream, objects, chain.Count);

            byte[] peeled;
            try
            {
                peeled = FilterPipeline.Decode(
                    stream.RawData,
                    new PdfName(chain[0]),
                    parms[0],
                    opts.MaxStreamDecodeBytes);
            }
            catch (Exception ex)
            {
                Report("Optimize.PeelFilter",
                    "Object " + objectNumber + ": the " + chain[0] + " layer could not be decoded; the " +
                    "stream was left untouched.", stats, ex);
                return false;
            }

            if (peeled == null)
            {
                Report("Optimize.PeelFilter",
                    "Object " + objectNumber + ": decoding the " + chain[0] + " layer produced nothing; " +
                    "the stream was left untouched.", stats);
                return false;
            }

            chain.RemoveAt(0);
            parms.RemoveAt(0);

            stream.RawData = peeled;
            stream.Set(PdfName.Length, new PdfNumber(peeled.Length));
            WriteFilterChain(stream, chain, parms);
            return true;
        }

        /// <summary>
        /// Whether <paramref name="stream"/> is an image XObject carrying <b>raw samples this
        /// optimizer may decode and re-encode</b> — an empty filter chain, or exactly one
        /// <c>FlateDecode</c>.
        /// </summary>
        /// <remarks>
        /// Anything else is a codec payload that must be passed through: DCT and JPX by design,
        /// JBIG2 because it already beats anything this pass would produce, CCITT because it is
        /// already the target. A chain of more than one filter is refused outright rather than
        /// peeled, and a <c>null</c> chain means the <c>/Filter</c> entry could not be read, which
        /// is not the same as absent.
        ///
        /// <para>Every chain shape: null rejects, empty accepts, one Flate accepts, one non-Flate
        /// rejects, more than one rejects.</para>
        ///
        /// <para><b>CompressStreams is deliberately NOT a caller.</b> It reaches the same accept
        /// set after peeling, but it runs over ALL streams rather than images, it mutates the chain
        /// rather than testing it, and it carries the <c>RecompressExistingStreams</c> arm. Folding
        /// it in would change what it does, not just where the rule lives.</para>
        /// </remarks>
        private static bool CarriesReEncodableSamples(
            PdfStream stream, Dictionary<int, PdfObject> objects)
        {
            var subtype = DerefName(stream, PdfName.Subtype, objects);
            if (subtype == null || subtype.Value != "Image") return false;

            var chain = GetFilterChain(stream, objects);
            if (chain == null || chain.Count > 1) return false;
            if (chain.Count == 1 && !PdfFilterNames.IsFlate(chain[0])) return false;
            return true;
        }

        /// <summary>
        /// Reads <c>/Filter</c> as a list of filter names, following indirect references — both for
        /// the entry itself and for each element of an array.
        /// </summary>
        /// <returns>
        /// An empty list when the stream has no filter; <c>null</c> when <c>/Filter</c> is present
        /// but is not a name or an array of names, which the caller must treat as "do not touch"
        /// rather than as "no filter".
        /// </returns>
        private static List<string> GetFilterChain(PdfStream stream, Dictionary<int, PdfObject> objects)
        {
            var filterObj = Deref(stream[PdfName.Filter], objects);
            var chain = new List<string>();

            if (filterObj == null || filterObj is PdfNull) return chain;
            if (filterObj is PdfName name) { chain.Add(name.Value); return chain; }

            if (filterObj is PdfArray array)
            {
                for (int i = 0; i < array.Count; i++)
                {
                    if (!(Deref(array[i], objects) is PdfName entry)) return null;
                    chain.Add(entry.Value);
                }
                return chain;
            }

            return null;
        }

        /// <summary>
        /// Reads <c>/DecodeParms</c> aligned one-to-one with the filter chain, padding with nulls.
        /// A single dictionary against a single filter, an array against an array, and a null entry
        /// wherever a filter takes no parameters (§7.4.1).
        /// </summary>
        private static List<PdfDictionary> GetDecodeParmsChain(PdfStream stream,
            Dictionary<int, PdfObject> objects, int filterCount)
        {
            var result = new List<PdfDictionary>(filterCount);
            var parmsObj = Deref(stream[PdfName.DecodeParms], objects);

            if (parmsObj is PdfArray array)
            {
                for (int i = 0; i < filterCount; i++)
                    result.Add(i < array.Count ? Deref(array[i], objects) as PdfDictionary : null);
            }
            else
            {
                var single = parmsObj as PdfDictionary;
                for (int i = 0; i < filterCount; i++)
                    result.Add(i == 0 ? single : null);
            }

            return result;
        }

        /// <summary>
        /// Writes a filter chain back onto a stream, collapsing a single-entry chain to a bare name
        /// and dropping both keys entirely when nothing is left.
        /// </summary>
        private static void WriteFilterChain(PdfStream stream, List<string> chain,
            List<PdfDictionary> parms)
        {
            if (chain.Count == 0)
            {
                stream.Remove(PdfName.Filter);
                stream.Remove(PdfName.DecodeParms);
                return;
            }

            if (chain.Count == 1)
            {
                stream.Set(PdfName.Filter, new PdfName(chain[0]));
                if (parms.Count > 0 && parms[0] != null) stream.Set(PdfName.DecodeParms, parms[0]);
                else stream.Remove(PdfName.DecodeParms);
                return;
            }

            var filterArray = new PdfArray();
            foreach (string entry in chain) filterArray.Add(new PdfName(entry));
            stream.Set(PdfName.Filter, filterArray);

            bool anyParms = false;
            var parmsArray = new PdfArray();
            foreach (var entry in parms)
            {
                if (entry != null) anyParms = true;
                parmsArray.Add(entry ?? (PdfObject)PdfNull.Instance);
            }

            if (anyParms) stream.Set(PdfName.DecodeParms, parmsArray);
            else stream.Remove(PdfName.DecodeParms);
        }

        /// <summary>
        /// Decodes a stream through the caller's ceiling, never the unbounded
        /// <c>GetDecodedData()</c>: a refusal must cost nothing and return no data, so a partial
        /// buffer can never be mistaken for a complete decode.
        /// </summary>
        /// <remarks>
        /// Uncached: the optimizer decodes each stream once a pass and holds the whole document, so a
        /// memo on every stream it decoded but did not rewrite would hold the sum of the decodes until
        /// the run ended — a page of small Flate bombs, each under the ceiling, multiplying it.
        /// </remarks>
        internal static bool TryDecodeBounded(PdfStream stream, Dictionary<int, PdfObject> objects,
            OptimizationOptions opts, out byte[] decoded, out string reason)
            => TryDecodeBounded(stream, opts.MaxStreamDecodeBytes, out decoded, out reason);

        /// <summary>
        /// <see cref="TryDecodeBounded(PdfStream, Dictionary{int, PdfObject}, OptimizationOptions, out byte[], out string)"/>
        /// under a ceiling smaller than the per-stream one: what is left of a page's whole content.
        /// </summary>
        private static bool TryDecodeBounded(PdfStream stream, long maxBytes, out byte[] decoded, out string reason)
        {
            decoded = null;
            reason = null;

            try
            {
                if (stream.TryGetDecodedDataUncached(maxBytes, out decoded, out reason))
                    return true;

                reason = reason ?? "over OptimizationOptions.MaxStreamDecodeBytes";
                decoded = null;
                return false;
            }
            catch (Exception ex)
            {
                reason = ex.GetType().Name + ": " + ex.Message;
                decoded = null;
                return false;
            }
        }

        // -------------------------------------------------------------------
        //  Annotation removal
        // -------------------------------------------------------------------

        private static void RemoveAnnotations(Dictionary<int, PdfObject> objects, PdfDictionary trailer)
        {
            // What an annotation, or the form its widgets made, holds stays reachable by routes other
            // than /Annots — a structure element's object reference, a widget's /Parent, the catalog's
            // /AcroForm — so each is scrubbed where it stands, and the form leaves the catalog.
            var said = new HashSet<object>(PdfReferenceEquality.Instance);
            foreach (var kvp in objects)
            {
                if (!(kvp.Value is PdfDictionary dict))
                    continue;

                // Deref: a page commonly writes /Annots as an indirect reference, and reading the
                // indexer directly would make the option silently remove nothing for those documents.
                if (Deref(dict[PdfName.Annots], objects) is PdfArray annots)
                {
                    for (int i = 0; i < annots.Count; i++)
                        if (Deref(annots[i], objects) is PdfDictionary annot) said.Add(annot);
                    dict.Remove(PdfName.Annots);
                }
            }

            if (Deref(trailer[PdfName.Root], objects) is PdfDictionary catalog
                && Deref(catalog[PdfName.AcroForm], objects) is PdfDictionary acroForm)
            {
                var pending = new Stack<PdfObject>();
                if (Deref(acroForm[PdfName.Fields], objects) is PdfArray fields)
                    for (int i = 0; i < fields.Count; i++) pending.Push(fields[i]);
                while (pending.Count > 0)
                {
                    if (!(Deref(pending.Pop(), objects) is PdfDictionary node) || !said.Add(node)) continue;
                    if (Deref(node[PdfName.Kids], objects) is PdfArray kids)
                        for (int i = 0; i < kids.Count; i++) pending.Push(kids[i]);
                }
                catalog.Remove(PdfName.AcroForm);
            }

            foreach (PdfDictionary annot in said)
                foreach (PdfName key in AnnotationContentKeys) annot.Remove(key);
        }

        /// <summary>
        /// What an annotation or a field says, shows or leads to: its value, its appearance, its words and
        /// author, its attached file, its action and destination.
        /// </summary>
        private static readonly PdfName[] AnnotationContentKeys =
        {
            PdfName.V, PdfName.DV, PdfName.RV, PdfName.AP, PdfName.AS, PdfName.Contents, PdfName.RC,
            PdfName.Subj, PdfName.T, PdfName.TU, PdfName.MK, PdfName.Opt, PdfName.FS, PdfName.A, PdfName.Dest,
        };

        // -------------------------------------------------------------------
        //  Document structure removal
        // -------------------------------------------------------------------

        private static void RemoveDocumentStructure(
            Dictionary<int, PdfObject> objects,
            PdfDictionary trailer,
            OptimizationStats stats)
        {
            if (!(Deref(trailer[PdfName.Root], objects) is PdfDictionary catalog))
                return;

            catalog.Remove(PdfName.MarkInfo);
            catalog.Remove(PdfName.StructTreeRoot);
            catalog.Remove(PdfName.Lang);

            // A document whose XMP claims pdfuaid:part now has no structure tree to back the claim.
            // Leaving it would make the file assert PDF/UA-1 conformance it cannot possibly meet —
            // whether or not the caller also set the unrelated RemoveXmpMetadata.
            if (Deref(catalog[PdfName.Metadata], objects) is PdfStream metadata)
            {
                StripPdfUaIdentifier(metadata, stats);
            }
        }

        /// <summary>
        /// Removes the <c>pdfuaid:part</c> assertion from an XMP packet, leaving the rest of the
        /// metadata intact. Text-level editing is the right tool here: the packet is XML whose exact
        /// serialisation matters to other tools, so re-emitting it from a parse would change far
        /// more than the one claim being retracted.
        /// </summary>
        /// <remarks>
        /// <para><b>The packet is decoded, and read in the encoding it is written in</b>: read raw as
        /// UTF-8, a Flate-filtered packet or a UTF-16 one (the XMP specification's other encodings)
        /// would keep its claim with nothing reported. The decode
        /// is held to the XMP packet ceiling every reader asks
        /// (<see cref="Base.Operations.Metadata.PdfDeclarationsWriter.MaxExistingPacketBytes"/>), and the text is read strictly: a
        /// lenient read would replace what it could not decode, and the replacements would be written back.
        /// The stripped packet is written in the same encoding, its byte-order mark kept, unfiltered.</para>
        /// </remarks>
        internal static void StripPdfUaIdentifier(PdfStream metadata, OptimizationStats stats)
        {
            const string Unchanged = ", so a pdfuaid:part claim (if any) is still in the file while the " +
                                     "structure tree it asserts has been removed.";

            if (!metadata.TryGetDecodedDataUncached(Base.Operations.Metadata.PdfDeclarationsWriter.MaxExistingPacketBytes,
                    out byte[] raw, out string why))
            {
                Report("Optimize.RemoveDocumentStructure",
                    "The XMP packet could not be decoded (" + why + ")" + Unchanged, stats);
                return;
            }
            if (raw == null || raw.Length == 0) return;

            System.Text.Encoding encoding = XmpPacketEncoding(raw, out int bom);
            string xmp;
            try { xmp = encoding.GetString(raw, bom, raw.Length - bom); }
            catch (Exception ex)
            {
                Report("Optimize.RemoveDocumentStructure",
                    "The XMP packet could not be read as " + encoding.WebName + Unchanged, stats, ex);
                return;
            }

            string stripped = WithoutPdfUaPartClaim(xmp);

            if (ReferenceEquals(stripped, xmp) || stripped == xmp) return;

            byte[] text = encoding.GetBytes(stripped);
            byte[] updated = new byte[bom + text.Length];
            Buffer.BlockCopy(raw, 0, updated, 0, bom);
            Buffer.BlockCopy(text, 0, updated, bom, text.Length);
            metadata.RawData = updated;
            metadata.Remove(PdfName.Filter);
            metadata.Remove(PdfName.DecodeParms);
            metadata.Remove(PdfName.DL);
            metadata.Set(PdfName.Length, new PdfNumber(updated.Length));
        }

        /// <summary>
        /// The encoding an XMP packet is written in — UTF-8, or UTF-16 in either byte order, by its
        /// byte-order mark or, without one, by where the first character's zero byte falls — and the
        /// mark's length. Strict: an invalid sequence throws rather than becomes U+FFFD.
        /// </summary>
        private static System.Text.Encoding XmpPacketEncoding(byte[] raw, out int bom)
        {
            bom = 0;
            if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF) bom = 3;
            else if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF) { bom = 2; return new System.Text.UnicodeEncoding(true, false, true); }
            else if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE) { bom = 2; return new System.Text.UnicodeEncoding(false, false, true); }
            else if (raw.Length >= 2 && raw[0] == 0 && raw[1] != 0) return new System.Text.UnicodeEncoding(true, false, true);
            else if (raw.Length >= 2 && raw[0] != 0 && raw[1] == 0) return new System.Text.UnicodeEncoding(false, false, true);
            return new System.Text.UTF8Encoding(false, true);
        }

        /// <summary>
        /// <paramref name="xmp"/> with every <c>pdfuaid:part</c> claim removed. The element form goes
        /// from the spaces and tabs before it through its close tag and the white space after it; the
        /// attribute form goes from the spaces and tabs before it through its closing quote.
        /// </summary>
        /// <remarks>
        /// One forward scan, with the meaning of the regular expression
        /// <c>[ \t]*&lt;pdfuaid:part&gt;.*?&lt;/pdfuaid:part&gt;\s*|[ \t]*pdfuaid:part\s*=\s*"[^"]*"</c>
        /// (Singleline) — which would scan to the end of the packet for every opener without a close,
        /// so a packet of 20 000 of them (300 KB, from the file) would take 57 s. Here an opener with
        /// no close means no later opener has one either, so that form is not searched again.
        /// </remarks>
        internal static string WithoutPdfUaPartClaim(string xmp)
        {
            const string Name = "pdfuaid:part";
            const string Open = "<pdfuaid:part>";
            const string Close = "</pdfuaid:part>";

            System.Text.StringBuilder sb = null;
            int copied = 0, pos = 0;
            // An opener with no close means no later opener has one: the element form is not sought
            // again. (An attribute needs no such flag. With no closing quote after its own, no later
            // attribute has an opening quote, so each later one fails before any search.)
            bool elementsCanClose = true;
            while (pos < xmp.Length)
            {
                int at = xmp.IndexOf(Name, pos, StringComparison.Ordinal);
                if (at < 0) break;

                int start = -1, end = -1;
                if (elementsCanClose && at > 0 && xmp[at - 1] == '<'
                    && string.CompareOrdinal(xmp, at - 1, Open, 0, Open.Length) == 0)
                {
                    int close = xmp.IndexOf(Close, at - 1 + Open.Length, StringComparison.Ordinal);
                    if (close < 0) elementsCanClose = false;
                    else
                    {
                        start = at - 1;
                        end = close + Close.Length;
                        while (end < xmp.Length && char.IsWhiteSpace(xmp[end])) end++;
                    }
                }
                if (start < 0)
                {
                    int i = at + Name.Length;
                    while (i < xmp.Length && char.IsWhiteSpace(xmp[i])) i++;
                    if (i < xmp.Length && xmp[i] == '=')
                    {
                        i++;
                        while (i < xmp.Length && char.IsWhiteSpace(xmp[i])) i++;
                        if (i < xmp.Length && xmp[i] == '"')
                        {
                            int quote = xmp.IndexOf('"', i + 1);
                            if (quote >= 0) { start = at; end = quote + 1; }
                        }
                    }
                }

                if (start < 0) { pos = at + Name.Length; continue; }

                // The spaces and tabs before the claim go with it, never past what was already kept.
                while (start > copied && (xmp[start - 1] == ' ' || xmp[start - 1] == '\t')) start--;
                (sb ??= new System.Text.StringBuilder(xmp.Length)).Append(xmp, copied, start - copied);
                copied = pos = end;
            }

            if (sb == null) return xmp;
            sb.Append(xmp, copied, xmp.Length - copied);
            return sb.ToString();
        }

        // -------------------------------------------------------------------
        //  XMP metadata removal
        // -------------------------------------------------------------------

        /// <summary>
        /// Removes every XMP packet in the document, not just the catalog's.
        ///
        /// <para><b>Every packet, because the catalog's is almost never where the bytes are.</b>
        /// §14.3.2 lets <i>any</i> object carry a <c>/Metadata</c> stream, and Adobe's tools attach one
        /// per placed asset. Measured on a 22 MB corpus document: 214 packets totalling
        /// <b>9 679 007</b> bytes — 45 % of the whole optimized file — of which the catalog's was
        /// <b>3 439</b>.</para>
        ///
        /// <para>Removing the key is all this does; the packets then become unreachable and
        /// <see cref="OptimizationOptions.RemoveUnusedObjects"/> drops them, which is why the saving
        /// needs that option too. Some of the owners in that document are dictionaries whose only
        /// key is <c>/Metadata</c> — they survive as empty dictionaries costing a few bytes each
        /// rather than being pruned, because nothing here can prove what else points at them.</para>
        ///
        /// <para><b>Destructive.</b> Document-level XMP is what carries a PDF/A or PDF/UA conformance
        /// claim; object-level XMP carries asset provenance, rights and edit history. This option is
        /// in the <see cref="OptimizationOptions.Aggressive"/> preset only.</para>
        /// </summary>
        /// <returns>The number of <c>/Metadata</c> entries removed.</returns>
        private static int RemoveXmpMetadata(
            Dictionary<int, PdfObject> objects,
            PdfDictionary trailer)
        {
            int removed = 0;

            if (Deref(trailer[PdfName.Root], objects) is PdfDictionary catalog
                && catalog[PdfName.Metadata] != null)
            {
                catalog.Remove(PdfName.Metadata);
                removed++;
            }

            foreach (var kvp in objects)
            {
                // A stream is a dictionary here, and an image or a form may carry its own packet.
                if (!(kvp.Value is PdfDictionary dict)) continue;
                if (dict[PdfName.Metadata] == null) continue;

                // Never strip the key off an XMP packet itself: a /Metadata stream that happened to
                // carry a /Metadata entry is not what this is looking for, and removing it would be
                // editing the thing being deleted.
                if ((Deref(dict[PdfName.Type], objects) as PdfName)?.Value == "Metadata") continue;

                dict.Remove(PdfName.Metadata);
                removed++;
            }

            return removed;
        }

        // -------------------------------------------------------------------
        //  Thumbnail removal
        // -------------------------------------------------------------------

        private static void RemoveThumbnails(
            Dictionary<int, PdfObject> objects,
            PdfDictionary trailer)
        {
            foreach (var entry in CollectPages(objects, trailer))
                entry.Page.Remove(PdfName.Thumb);
        }

        // -------------------------------------------------------------------
        //  Content-stream concatenation
        // -------------------------------------------------------------------

        /// <summary>
        /// Merges a page's multiple <c>/Contents</c> streams into one, so the compressor sees a
        /// larger input.
        ///
        /// <para><b>Sharing is the whole difficulty.</b> Overwriting <c>arr[0]</c> in place and
        /// deleting <c>arr[1..]</c> from the object map would corrupt every page that shares a
        /// fragment: two pages sharing one would both draw the union of both, a page whose sole
        /// <c>/Contents</c> is another page's fragment would get a null, and a fragment consumed by an
        /// earlier page would vanish from the later one. Merging allocates a <b>new</b> object and
        /// deletes nothing — <c>FindReachableObjects</c> prunes the originals.</para>
        ///
        /// <para><b>A deliberate non-caller of <c>Core.Document.PdfPageContent.Concatenate</c>.</b>
        /// That one is lenient — a part it cannot decode is skipped and the rest is kept — which is
        /// right for a reader and wrong here: this method <i>writes</i> the page's new content, so
        /// keeping what it could read would silently drop the rest. It refuses instead, and names
        /// the object that failed.</para>
        ///
        /// <para><b>The separator rule is the same, and a test pins it:</b>
        /// <c>PdfOptimizerSharedRuleTests.ConcatenateContentStreams_JoinsPartsByteForByteAsCoreSharedReaderDoes</c>
        /// compares the merged bytes with what <c>PdfPageContent.Concatenate</c> makes of the same
        /// page. A third such writer gets a shared refusing form rather than a third loop.</para>
        /// </summary>
        /// <returns>The number of pages whose content streams were merged.</returns>
        private static int ConcatenateContentStreams(
            Dictionary<int, PdfObject> objects,
            PdfDictionary trailer,
            OptimizationOptions options,
            OptimizationStats stats)
        {
            var uses = CountContentStreamUses(objects, trailer);
            int nextObjectNumber = 1;
            foreach (int num in objects.Keys)
                if (num >= nextObjectNumber) nextObjectNumber = num + 1;

            int merged = 0;

            foreach (var entry in CollectPages(objects, trailer))
            {
                PdfDictionary page = entry.Page;

                if (!(Deref(page[PdfName.Contents], objects) is PdfArray arr) || arr.Count <= 1)
                    continue;

                // Any fragment drawn by another page too? Then merging would change what that page
                // draws. Leave this page alone and say so.
                bool shared = false;
                for (int i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is PdfReference r && uses.TryGetValue(r.ReferenceObjectNumber, out int n) && n > 1)
                    {
                        shared = true;
                        break;
                    }
                }

                if (shared)
                {
                    Report("Optimize.ConcatenateContentStreams",
                        "Page " + PageName(entry.ObjectNumber) + " shares at least one /Contents stream with another " +
                        "page, so its fragments were left separate; merging them would have changed " +
                        "what the other page draws.", stats);
                    continue;
                }

                // Concatenate the decoded fragments, separated by a newline — §7.8.2 treats the
                // array as a single stream, but a token may not span the join, so the separator is
                // required rather than cosmetic.
                var parts = new List<(int Number, PdfStream Stream)>();
                foreach (var item in arr)
                {
                    if (!(item is PdfReference cRef)) continue;
                    if (!objects.TryGetValue(cRef.ReferenceObjectNumber, out var cObj)) continue;
                    if (cObj is PdfStream cStream) parts.Add((cRef.ReferenceObjectNumber, cStream));
                }

                // A fragment we could not read means we cannot reproduce the page: merging what we
                // did read would silently drop the rest.
                byte[] combinedBytes = JoinContentParts(parts, options, out int failed, out string reason);
                if (reason != null)
                {
                    Report("Optimize.ConcatenateContentStreams", failed >= 0
                        ? "Object " + failed + " could not be decoded (" + reason + "); page " +
                          PageName(entry.ObjectNumber) + " was left unmerged."
                        : "Page " + PageName(entry.ObjectNumber) + " was left unmerged: " + reason + ".", stats);
                    continue;
                }
                if (combinedBytes == null) continue;
                byte[] compressed = FlateEncodeFilter.Encode(combinedBytes, options.FlateCompressionLevel);

                var mergedStream = new PdfStream();
                if (compressed != null && compressed.Length < combinedBytes.Length)
                {
                    mergedStream.RawData = compressed;
                    mergedStream.Set(PdfName.Filter, PdfName.FlateDecode);
                    mergedStream.Set(PdfName.Length, new PdfNumber(compressed.Length));
                }
                else
                {
                    mergedStream.RawData = combinedBytes;
                    mergedStream.Set(PdfName.Length, new PdfNumber(combinedBytes.Length));
                }

                int newNum = nextObjectNumber++;
                objects[newNum] = mergedStream;
                page.Set(PdfName.Contents, new PdfReference(newNum, 0));
                merged++;
            }

            return merged;
        }

        // -------------------------------------------------------------------
        //  Page-number collector (shared by thumbnail removal, content concat,
        //  and CleanContentStreams)
        // -------------------------------------------------------------------

        /// <summary>
        /// Every leaf page of the document, with the object number that named it (<c>-1</c> for a
        /// page written directly inside its parent's <c>/Kids</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>One page-tree walk in this assembly, and this is not it —
        /// <see cref="PageTreeWalk"/> is</b>, so a directly-written page-tree node is walked: §7.7.2
        /// Table 28 says <c>/Pages</c> "shall be an indirect reference", and Core reads the malformed
        /// spelling anyway — the house rule <see cref="PageTreeShape"/> honours one key further
        /// in.</para>
        ///
        /// <para>The four callers all wanted the page <i>dictionary</i>; only
        /// <c>ConcatenateContentStreams</c> also wants the number, and only to name the page in a
        /// diagnostic — which is why the number is carried rather than looked up again.</para>
        /// </remarks>
        private static IEnumerable<PageTreeWalk.PageEntry> CollectPages(
            Dictionary<int, PdfObject> objects,
            PdfDictionary trailer)
        {
            if (!(Deref(trailer[PdfName.Root], objects) is PdfDictionary catalog))
                return System.Array.Empty<PageTreeWalk.PageEntry>();

            return PageTreeWalk.Enumerate(objects, catalog);
        }

        /// <summary>
        /// How a diagnostic names a page. A page written directly inside its parent's <c>/Kids</c>
        /// has no object number, and printing <c>-1</c> would name a slot the reader could go and
        /// look for.
        /// </summary>
        private static string PageName(int objectNumber)
            => objectNumber >= 0
                ? "object " + objectNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "written directly in its parent's /Kids (no object number)";

        /// <summary>
        /// A page's content parts decoded and joined (§7.8.2: a token may not span the join, so each
        /// part is followed by a newline), for a pass that writes the page's new content from them.
        /// Returns <c>null</c> with <paramref name="reason"/> set on a refusal — a part that does not
        /// decode (<paramref name="failedObject"/> names it), or the parts together decoding past
        /// <see cref="OptimizationOptions.MaxStreamDecodeBytes"/> (<paramref name="failedObject"/> -1) —
        /// and <c>null</c> with no reason when nothing decoded to a byte.
        /// </summary>
        /// <remarks>
        /// The whole is held to the per-stream ceiling, and each part is decoded under what is left of
        /// it: the joined bytes become one stream, and a page of many small Flate bombs, each under the
        /// ceiling, would otherwise hold all of them at once before anything was compared.
        /// </remarks>
        internal static byte[] JoinContentParts(List<(int Number, PdfStream Stream)> parts,
            OptimizationOptions options, out int failedObject, out string reason)
        {
            failedObject = -1;
            reason = null;
            long left = options.MaxStreamDecodeBytes;
            using var combined = new MemoryStream();

            foreach (var (number, stream) in parts)
            {
                if (!TryDecodeBounded(stream, left, out byte[] decoded, out string why))
                {
                    failedObject = number;
                    reason = left < options.MaxStreamDecodeBytes
                        ? why + "; a page's content parts are held together to " + JoinedCeiling(options)
                        : why;
                    return null;
                }

                if (decoded == null || decoded.Length == 0) continue;
                if (decoded.LongLength + 1 > left)
                {
                    reason = "its content parts together decode past " + JoinedCeiling(options);
                    return null;
                }
                combined.Write(decoded, 0, decoded.Length);
                combined.WriteByte((byte)'\n');
                left -= decoded.LongLength + 1;
            }

            return combined.Length == 0 ? null : combined.ToArray();
        }

        private static string JoinedCeiling(OptimizationOptions options)
            => "OptimizationOptions.MaxStreamDecodeBytes (" +
               options.MaxStreamDecodeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + " bytes)";

        /// <summary>
        /// Counts how many pages reference each content stream, so a stream drawn by more than one
        /// page is never rewritten in place.
        /// </summary>
        private static Dictionary<int, int> CountContentStreamUses(
            Dictionary<int, PdfObject> objects, PdfDictionary trailer)
        {
            var counts = new Dictionary<int, int>();

            foreach (var pageEntry in CollectPages(objects, trailer))
            {
                PdfDictionary page = pageEntry.Page;

                var contents = Deref(page[PdfName.Contents], objects);
                if (page[PdfName.Contents] is PdfReference direct && !(contents is PdfArray))
                {
                    Bump(counts, direct.ReferenceObjectNumber);
                }
                else if (contents is PdfArray array)
                {
                    for (int i = 0; i < array.Count; i++)
                        if (array[i] is PdfReference entry)
                            Bump(counts, entry.ReferenceObjectNumber);
                }
            }

            return counts;
        }

        private static void Bump(Dictionary<int, int> counts, int key)
        {
            counts.TryGetValue(key, out int existing);
            counts[key] = existing + 1;
        }

        // -------------------------------------------------------------------
        //  Content-stream cleanup (remove empty q…Q pairs)
        // -------------------------------------------------------------------

        private static int CleanContentStreams(
            Dictionary<int, PdfObject> objects,
            PdfDictionary trailer,
            OptimizationOptions options,
            OptimizationStats stats)
        {
            var contentStreamNums = CollectContentStreamObjectNumbers(objects, trailer);
            int changed = 0;

            foreach (int num in contentStreamNums)
            {
                if (!objects.TryGetValue(num, out var obj) || !(obj is PdfStream stream))
                    continue;

                if (!TryDecodeBounded(stream, objects, options, out byte[] decoded, out string reason))
                {
                    Report("Optimize.CleanContentStreams",
                        "Object " + num + " could not be decoded (" + reason + "); it was left as it was.",
                        stats);
                    continue;
                }

                if (decoded == null || decoded.Length == 0)
                    continue;

                byte[] cleaned = RemoveEmptyQQPairs(decoded);
                if (cleaned == null)
                {
                    Report("Optimize.CleanContentStreams",
                        "Object " + num + " contains an inline image (BI…ID…EI); its q…Q pairs were left " +
                        "alone because the pixel payload cannot be told from operators without computing " +
                        "the payload length exactly.", stats);
                    continue;
                }

                if (cleaned.Length == decoded.Length)
                    continue;

                // Re-encode as FlateDecode.
                byte[] compressed = FlateEncodeFilter.Encode(cleaned, options.FlateCompressionLevel);
                stream.RawData = compressed;
                stream.Set(PdfName.Filter, PdfName.FlateDecode);
                stream.Set(PdfName.Length, new PdfNumber(compressed.Length));
                stream.Remove(PdfName.DecodeParms);
                changed++;
            }

            return changed;
        }

        private static HashSet<int> CollectContentStreamObjectNumbers(
            Dictionary<int, PdfObject> objects,
            PdfDictionary trailer)
        {
            var result = new HashSet<int>();
            foreach (var entry in CollectPages(objects, trailer))
            {
                PdfDictionary page = entry.Page;

                // Resolve BEFORE branching on the shape. /Contents 5 0 R where object 5 is an
                // ARRAY is a legal spelling of a multi-part page, and testing `is PdfReference`
                // first would collect the ARRAY's object number as though it were a stream, so
                // CleanContentStreams would skip every such page while reporting a successful run.
                //
                // CountContentStreamReferences, ~90 lines above, reads it the same way, as does
                // PdfPageContentAppender.CopyExistingContents, which carries the full reasoning; a
                // shared home for the three needs a resolver delegate, because this assembly
                // resolves through its own object map rather than a PdfDocument.
                var contents = Deref(page[PdfName.Contents], objects);
                if (contents is PdfArray arr)
                {
                    for (int i = 0; i < arr.Count; i++)
                        if (arr[i] is PdfReference aRef)
                            result.Add(aRef.ReferenceObjectNumber);
                }
                else if (page[PdfName.Contents] is PdfReference cRef)
                {
                    result.Add(cRef.ReferenceObjectNumber);
                }
            }
            return result;
        }

        // Removes empty q…Q pairs from raw content-stream bytes in a single O(n) pass
        // using a stack. Handles arbitrary nesting depth without re-scanning.
        /// <summary>
        /// Removes empty <c>q…Q</c> pairs from raw content-stream bytes.
        /// </summary>
        /// <returns>
        /// The cleaned bytes, or <c>null</c> when the stream contains an inline image and therefore
        /// cannot be tokenised safely — see <see cref="TokenizeContentStream"/>. A null result means
        /// "leave this stream exactly as it was", never "nothing to remove".
        /// </returns>
        /// <remarks>
        /// Streamed: each item goes straight into one output buffer as the scanner reads it, and an
        /// empty pair truncates the buffer back to its <c>q</c>. A pair is at least <c>q</c>, white
        /// space and <c>Q</c> and becomes one space, so the output never outgrows the input. A list of
        /// items beside the stream, and a second list for the output, would cost several times the
        /// stream's length for a pass that writes most of it back unchanged.
        /// </remarks>
        internal static byte[] RemoveEmptyQQPairs(byte[] data)
        {
            // qStack[k]    = position in `output` where the k-th unmatched 'q' was written.
            // contentFlags = whether any non-whitespace content was seen in that q-level.
            var qStack      = new Stack<int>();
            var contentFlags = new Stack<bool>();
            var output       = new byte[data.Length];
            int length       = 0;
            bool removed     = false;

            void Write(ContentStreamItem item)
            {
                item.CopyTo(output, length);
                length += item.Length;
            }

            void MarkContent()
            {
                // Any non-whitespace drawing token marks the current q-level as non-empty.
                if (contentFlags.Count > 0)
                {
                    contentFlags.Pop();
                    contentFlags.Push(true);
                }
            }

            bool scanned = ScanContentStream(data, item =>
            {
                if (item.IsOperator('q'))
                {
                    qStack.Push(length);
                    contentFlags.Push(false);
                    Write(item);
                }
                else if (item.IsOperator('Q'))
                {
                    if (qStack.Count == 0)
                    {
                        Write(item);
                    }
                    else
                    {
                        bool hadContent = contentFlags.Pop();
                        int qAt         = qStack.Pop();

                        if (!hadContent)
                        {
                            // Empty pair: truncate output back to before the 'q', skip 'Q'.
                            length = qAt;
                            Write(ContentStreamItem.Space);
                            removed = true;
                        }
                        else
                        {
                            Write(item);
                            // Propagate: the enclosing level also has content now.
                            MarkContent();
                        }
                    }
                }
                else
                {
                    if (!item.IsWhitespace) MarkContent();
                    Write(item);
                }
            });

            if (!scanned) return null;
            if (!removed) return data;

            var result = new byte[length];
            Buffer.BlockCopy(output, 0, result, 0, length);
            return result;
        }

        // -------------------------------------------------------------------
        //  Content-stream tokenizer (minimal: handles strings, hex, comments)
        // -------------------------------------------------------------------

        /// <summary>
        /// A token or white-space run of a content stream: a view of the bytes it was read from, never
        /// a copy. We only need to distinguish whitespace, the single-character operators <c>q</c> and
        /// <c>Q</c>, and everything else (drawing content, which prevents a pair's removal).
        /// </summary>
        /// <remarks>
        /// A view, because copying every token and every white-space run into its own array would cost a
        /// stream's length again in small arrays, each with its object header, for a pass that writes
        /// most of them back unchanged.
        /// </remarks>
        private readonly struct ContentStreamItem
        {
            private readonly byte[] _source;
            private readonly int _start;
            private readonly int _length;
            private readonly bool _isWhitespace;

            private ContentStreamItem(byte[] source, int start, int length, bool isWhitespace)
            {
                _source = source;
                _start = start;
                _length = length;
                _isWhitespace = isWhitespace;
            }

            public bool IsWhitespace => _isWhitespace;

            public int Length => _length;

            public byte this[int index] => _source[_start + index];

            public bool IsOperator(char ch)
            {
                // A single-character operator: the data is exactly one byte and it equals ch,
                // provided it is not whitespace (which would mean it's part of a token boundary).
                return !_isWhitespace && _length == 1 && _source[_start] == (byte)ch;
            }

            /// <summary>Whether the bytes are exactly <paramref name="ascii"/>, without decoding them.</summary>
            public bool Is(string ascii)
            {
                if (_length != ascii.Length) return false;
                for (int i = 0; i < _length; i++)
                    if (_source[_start + i] != ascii[i]) return false;
                return true;
            }

            /// <summary>The bytes from <paramref name="offset"/> on, as ASCII text.</summary>
            public string Ascii(int offset = 0)
                => System.Text.Encoding.ASCII.GetString(_source, _start + offset, _length - offset);

            public void CopyTo(byte[] destination, int at)
                => Buffer.BlockCopy(_source, _start, destination, at, _length);

            public static ContentStreamItem Whitespace(byte[] source, int start, int end)
                => new ContentStreamItem(source, start, end - start, true);

            public static ContentStreamItem Token(byte[] source, int start, int end)
                => new ContentStreamItem(source, start, end - start, false);

            /// <summary>A token of its own bytes — a group the strip merge writes in.</summary>
            public static ContentStreamItem Token(byte[] data) => new ContentStreamItem(data, 0, data.Length, false);

            public static readonly ContentStreamItem Space = Whitespace(new byte[] { (byte)' ' }, 0, 1);
        }

        /// <summary>
        /// The content stream's tokens and white-space runs as a list (<see cref="ScanContentStream"/>),
        /// or <c>null</c> when it contains an inline image.
        /// </summary>
        private static List<ContentStreamItem> TokenizeContentStream(byte[] data)
        {
            var items = new List<ContentStreamItem>();
            return ScanContentStream(data, items.Add) ? items : null;
        }

        /// <summary>
        /// Splits content-stream bytes into tokens and whitespace runs, handing each to
        /// <paramref name="emit"/> in order as a view of <paramref name="data"/>.
        ///
        /// <para><b>Returns <c>false</c> when the stream contains an inline image</b>, part of the way
        /// through: what was emitted before it is to be discarded. Between
        /// <c>ID</c> and <c>EI</c> lies raw pixel data, and this tokenizer has no state for it: a
        /// <c>0x71</c> or <c>0x51</c> byte in the payload is delimited by PDF whitespace like any
        /// other single-character token, so it would be read as a <c>q</c> or <c>Q</c> operator and
        /// <see cref="RemoveEmptyQQPairs"/> would delete a run of pixels — the image a sample short,
        /// every following byte shifted, and a reader computing the payload length from
        /// <c>/W</c>·<c>/H</c>·<c>/BPC</c> mis-locating <c>EI</c> and parsing the rest of the page as
        /// garbage. A <c>0x25</c> byte would enter the comment branch and a <c>0x28</c> the
        /// literal-string branch, with the same effect.</para>
        ///
        /// <para>Computing the payload length exactly is possible but belongs with the code that
        /// already does it; refusing the stream mirrors what the auto-tagger does with a page whose
        /// inline-image length it cannot compute — skip it, never force it.</para>
        /// </summary>
        private static bool ScanContentStream(byte[] data, Action<ContentStreamItem> emit)
        {
            int pos = 0;
            int len = data.Length;

            while (pos < len)
            {
                byte b = data[pos];

                // 'BI' begins an inline image; everything after it is unsafe to tokenise here.
                if (b == (byte)'B' && pos + 1 < len && data[pos + 1] == (byte)'I'
                    && (pos + 2 >= len || IsPdfWhitespace(data[pos + 2]) || IsPdfDelimiter(data[pos + 2]))
                    && (pos == 0 || IsPdfWhitespace(data[pos - 1]) || IsPdfDelimiter(data[pos - 1])))
                {
                    return false;
                }

                if (IsPdfWhitespace(b))
                {
                    int start = pos;
                    while (pos < len && IsPdfWhitespace(data[pos])) pos++;
                    emit(ContentStreamItem.Whitespace(data, start, pos));
                    continue;
                }

                if (b == (byte)'%')
                {
                    // Comment: skip to end of line, emit as whitespace token.
                    int start = pos;
                    while (pos < len && data[pos] != (byte)'\r' && data[pos] != (byte)'\n') pos++;
                    emit(ContentStreamItem.Whitespace(data, start, pos));
                    continue;
                }

                if (b == (byte)'(')
                {
                    // Literal string — balanced parens, backslash escapes.
                    int start = pos;
                    pos++; // consume '('
                    int depth = 1;
                    while (pos < len && depth > 0)
                    {
                        byte c = data[pos++];
                        if (c == (byte)'\\') { if (pos < len) pos++; } // skip escaped char
                        else if (c == (byte)'(') depth++;
                        else if (c == (byte)')') depth--;
                    }
                    emit(ContentStreamItem.Token(data, start, pos));
                    continue;
                }

                if (b == (byte)'<')
                {
                    if (pos + 1 < len && data[pos + 1] == (byte)'<')
                    {
                        // Dictionary: scan for matching >>
                        int start = pos;
                        pos += 2;
                        int depth = 1;
                        while (pos + 1 < len && depth > 0)
                        {
                            if (data[pos] == (byte)'<' && data[pos + 1] == (byte)'<') { depth++; pos += 2; }
                            else if (data[pos] == (byte)'>' && data[pos + 1] == (byte)'>') { depth--; pos += 2; }
                            else pos++;
                        }
                        emit(ContentStreamItem.Token(data, start, pos));
                    }
                    else
                    {
                        // Hex string.
                        int start = pos;
                        pos++; // consume '<'
                        while (pos < len && data[pos] != (byte)'>') pos++;
                        if (pos < len) pos++; // consume '>'
                        emit(ContentStreamItem.Token(data, start, pos));
                    }
                    continue;
                }

                if (b == (byte)'[' || b == (byte)']')
                {
                    emit(ContentStreamItem.Token(data, pos, pos + 1));
                    pos++;
                    continue;
                }

                if (b == (byte)'/')
                {
                    // PDF name token: starts with '/', followed by non-whitespace/non-delimiter chars.
                    int start = pos++;
                    while (pos < len && !IsPdfWhitespace(data[pos]) && !IsPdfDelimiter(data[pos]))
                        pos++;
                    emit(ContentStreamItem.Token(data, start, pos));
                    continue;
                }

                // Regular token: read until whitespace or delimiter.
                {
                    int start = pos;
                    while (pos < len && !IsPdfWhitespace(data[pos]) && !IsPdfDelimiter(data[pos]))
                        pos++;
                    // Safety: if pos didn't advance (stray delimiter char), skip it to prevent
                    // an infinite loop on characters like ')', '>', '{', '}'.
                    if (pos == start) pos++;
                    emit(ContentStreamItem.Token(data, start, pos));
                }
            }

            return true;
        }

        // The two least-typed members, NUL and form feed, are pinned to observable behaviour by
        // PdfOptimizerSharedRuleTests.CleanContentStreams_TreatsNulAndFormFeedAsTokenSeparatorsLikeCore:
        // drop either and `q<sep>Q` tokenises as one three-byte token instead of two operators.
        // §7.2.3 Tables 1 and 2, in Core.Constants.PdfSyntax — one implementation for the plugins
        // that scan raw PDF bytes. PsScanner keeps its own on purpose: those are PLRM §3.1.
        private static bool IsPdfWhitespace(byte b) => Core.Constants.PdfSyntax.IsWhitespace(b);

        private static bool IsPdfDelimiter(byte b) => Core.Constants.PdfSyntax.IsDelimiter(b);

        private static byte[] SerializeItems(List<ContentStreamItem> items)
        {
            long totalLen = 0;
            foreach (var item in items)
                totalLen += item.Length;

            var result = new byte[totalLen];
            int pos = 0;
            foreach (var item in items)
            {
                if (item.Length > 0)
                {
                    item.CopyTo(result, pos);
                    pos += item.Length;
                }
            }
            return result;
        }

        // -------------------------------------------------------------------
        //  Deduplication
        // -------------------------------------------------------------------

        /// <summary>
        /// Merges objects with identical content, iterating to a fixed point.
        ///
        /// <para><i>Identity.</i> Byte-identical objects whose identity is <b>structural rather than
        /// content-derived</b> are never merged: three blank inserted pages — exactly what
        /// <c>PdfOperations.InsertBlankPage</c> emits — serialise identically, and merged they would
        /// make <c>/Kids [3 0 R 3 0 R 3 0 R]</c> while <c>/Count</c> stays 3: a document that
        /// disagrees with itself, cannot carry per-page <c>/Annots</c>, and reopens as a single
        /// page.</para>
        ///
        /// <para><i>Fixed point.</i> Hashing raw serialisations once would mean two objects that become
        /// identical only <b>after</b> their children are merged never merge themselves — two font
        /// descriptors pointing at two identical font files, say. Canonicalising every reference
        /// through the map built so far and repeating converges on those.</para>
        /// </summary>
        private static void DeduplicateObjects(
            Dictionary<int, PdfObject> objects,
            PdfDictionary trailer,
            Dictionary<int, int> dedupMap)
        {
            var excluded = CollectIdentityBearingObjects(objects, trailer);

            foreach (int num in objects.Keys)
                dedupMap[num] = num;

            LastUnchangedSubtreeCount = default;

            // Bounded: each round must merge something or the loop ends, so the cap is a backstop
            // against a hash collision cycle rather than the normal exit.
            const int MaxRounds = 8;

            // One hasher and one buffer for the whole pass rather than a pair per object per
            // round. See ComputeObjectHash for why.
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var hashBuffer = new MemoryStream();

            for (int round = 0; round < MaxRounds; round++)
            {
                var hashToCanonical = new Dictionary<string, int>();
                bool changed = false;

                foreach (var kvp in objects.OrderBy(x => x.Key))
                {
                    if (excluded.Contains(kvp.Key)) continue;

                    // Already merged away in an earlier round.
                    if (dedupMap[kvp.Key] != kvp.Key) continue;

                    string hash = ComputeObjectHash(kvp.Value, dedupMap, hasher, hashBuffer);
                    if (hash == null) continue;

                    if (hashToCanonical.TryGetValue(hash, out int existing))
                    {
                        dedupMap[kvp.Key] = existing;
                        changed = true;
                    }
                    else
                    {
                        hashToCanonical[hash] = kvp.Key;
                    }
                }

                if (!changed) break;
            }
        }

        /// <summary>
        /// Object numbers that must never be merged, however identical their bytes: the catalog,
        /// every page-tree node, every annotation, every optional-content group, every structure
        /// element and every form field. Two blank pages are still two pages, and two identical
        /// widgets on different pages are still two widgets.
        /// </summary>
        /// <remarks>
        /// <para><b>The test is "does something refer to this object <i>as itself</i>", not "is it
        /// big".</b> An optional-content group's state is not in the group: <c>/OCProperties /D</c>
        /// lists it under <c>/ON</c> or <c>/OFF</c> by reference. Two groups both named "One", one
        /// hidden and one shown, are byte-identical once their <c>/Intent</c> and <c>/Usage</c>
        /// children merge — and merging them would hide the shown one's content. A
        /// structure element is listed by its parent's <c>/K</c> and by <c>/ParentTree</c>, and a
        /// field by its parent's <c>/Kids</c> and <c>/AcroForm /Fields</c>; merging two of either
        /// would produce the <c>/Kids [3 0 R 3 0 R]</c> contradiction the page exclusion prevents.</para>
        /// <para><b>Content-only objects are deliberately not here.</b> An optional-content
        /// membership dictionary (<c>/OCMD</c>) is decided by what it holds, so two identical ones
        /// behave identically and merge safely. Outline items carry their own <c>/Prev</c> and
        /// <c>/Next</c>, so two can only be equal in a file whose list is already broken.</para>
        /// </remarks>
        private static HashSet<int> CollectIdentityBearingObjects(
            Dictionary<int, PdfObject> objects, PdfDictionary trailer)
        {
            var excluded = new HashSet<int>();

            foreach (var kvp in objects)
            {
                if (!(kvp.Value is PdfDictionary dict)) continue;

                // /Type is required on an optional-content group (ISO 32000-1 Table 98), so the
                // type finds every group, including one a damaged /OCGs inventory leaves out.
                var type = DerefName(dict, PdfName.Type, objects);
                if (type != null
                    && (type.Value == "Page" || type.Value == "Pages" || type.Value == "Catalog"
                        || type.Value == "Annot" || type.Value == PdfName.OCG.Value))
                {
                    excluded.Add(kvp.Key);
                }

                // Anything an /Annots array points at, whether or not it declares /Type /Annot.
                if (Deref(dict[PdfName.Annots], objects) is PdfArray annots)
                {
                    for (int i = 0; i < annots.Count; i++)
                        if (annots[i] is PdfReference annotRef)
                            excluded.Add(annotRef.ReferenceObjectNumber);
                }
            }

            if (Deref(trailer[PdfName.Root], objects) is PdfDictionary catalog
                && trailer[PdfName.Root] is PdfReference rootRef)
            {
                excluded.Add(rootRef.ReferenceObjectNumber);
                if (catalog[PdfName.Pages] is PdfReference pagesRef)
                    excluded.Add(pagesRef.ReferenceObjectNumber);

                // Neither of these declares itself: /Type is optional on a structure element
                // (Table 323) and absent from a field, so both are found by their trees.
                ExcludeReachedThrough(catalog[PdfName.StructTreeRoot], PdfName.K, objects, excluded);
                if (Deref(catalog[PdfName.AcroForm], objects) is PdfDictionary acroForm)
                    ExcludeReachedThrough(acroForm[PdfName.Fields], PdfName.Kids, objects, excluded);
            }

            return excluded;
        }

        /// <summary>
        /// Adds to <paramref name="excluded"/> every indirect object reached from
        /// <paramref name="start"/> through array members and the dictionary key
        /// <paramref name="edge"/> — a structure tree through <c>/K</c>, a field tree through
        /// <c>/Kids</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>Bounded by the object map, not by a ceiling.</b> Each indirect object is entered
        /// once, so a <c>/K</c> that cycles back to an ancestor ends the branch rather than the
        /// process, and the walk is iterative, so a chain as deep as the file is long cannot
        /// overflow the stack. A ceiling would be worse than none here: a walk stopped early leaves
        /// the rest of the tree mergeable, which is the defect this exists to prevent.</para>
        /// </remarks>
        private static void ExcludeReachedThrough(PdfObject start, PdfName edge,
            Dictionary<int, PdfObject> objects, HashSet<int> excluded)
        {
            if (start == null) return;

            var entered = new HashSet<int>();
            var pending = new Stack<PdfObject>();
            pending.Push(start);

            while (pending.Count > 0)
            {
                PdfObject item = pending.Pop();
                if (item is PdfReference reference)
                {
                    int number = reference.ReferenceObjectNumber;
                    if (!entered.Add(number) || !objects.TryGetValue(number, out item)) continue;
                    excluded.Add(number);
                }

                if (item is PdfArray array)
                {
                    for (int i = 0; i < array.Count; i++) pending.Push(array[i]);
                }
                else if (item is PdfDictionary dict && dict[edge] is PdfObject next)
                {
                    pending.Push(next);
                }
            }
        }

        /// <summary>
        /// A content hash of <paramref name="obj"/> with its references canonicalised through
        /// <paramref name="dedupMap"/>, for the duplicate-merge table.
        /// </summary>
        /// <remarks>
        /// <para><b>The hasher and the buffer are passed in because this runs per object per
        /// round.</b> A fresh <c>SHA256</c> and <c>MemoryStream</c> per call, with the serialised
        /// object copied again through <c>ms.ToArray()</c>, would copy every byte of every duplicate
        /// stream twice per round — measured on 50 pages of ~100 KB content streams at <b>11.4 ms of
        /// a 16.1 ms optimize call</b> (183 us with 1 KB streams, which is why a small-stream fixture
        /// hides it).</para>
        /// <para><c>IncrementalHash</c> lets the dictionary bytes and the raw stream data be fed
        /// in separately, so the raw data is never copied at all: it is hashed straight from the
        /// object. <c>GetBuffer</c> rather than <c>ToArray</c> for the same reason.</para>
        /// <para>The key is base64 rather than <c>BitConverter.ToString</c>, which would produce a
        /// 95-character dashed string per object per round for a 32-byte digest.</para>
        /// </remarks>
        private static string ComputeObjectHash(PdfObject obj, Dictionary<int, int> dedupMap,
            IncrementalHash hasher, MemoryStream buffer)
        {
            buffer.SetLength(0);

            var canonical = CanonicaliseForHash(obj, dedupMap, 0);
            var tempWriter = new PdfWriter(buffer);
            tempWriter.WriteObject(canonical);

            hasher.AppendData(buffer.GetBuffer(), 0, (int)buffer.Length);

            if (obj is PdfStream stream && stream.RawData != null)
                hasher.AppendData(stream.RawData, 0, stream.RawData.Length);

            return Convert.ToBase64String(hasher.GetHashAndReset());
        }

        /// <summary>
        /// Counts subtrees <see cref="CanonicaliseForHash"/> returned <b>without allocating</b>
        /// because nothing in them needed rewriting. Reset at the start of each dedupe pass.
        /// </summary>
        /// <remarks>
        /// <para><b>A liveness probe, and it is not optional.</b> The fast path and the slow path
        /// produce the same hash by construction, so no assertion about the *answer* can tell
        /// whether it is running — deleting it would leave the whole suite green while restoring the
        /// per-object, per-round deep clone this exists to remove. Same reason
        /// <c>CMapCodec.UsesCidRangeIndex</c> exists.</para>
        /// <para><c>[ThreadStatic]</c> and reset to <c>default</c> at the top of the pass, per
        /// CLAUDE.md's rule for every <c>Last*</c> slot: a plain static would be read across a
        /// parallel run, and <c>DiagnosticSlotResetTests</c> requires a neutral-value assignment it
        /// can see — it does not recognise a bare <c>0</c>.</para>
        /// </remarks>
        [System.ThreadStatic]
        internal static int LastUnchangedSubtreeCount;

        /// <summary>
        /// True when a subtree contains anything <see cref="CanonicaliseForHash"/> would rewrite.
        /// </summary>
        /// <remarks>
        /// <para><b>Generation numbers are why this is not just "does the number change".</b>
        /// <c>dedupMap</c> is seeded with <c>dedupMap[num] = num</c> for <i>every</i> object, and
        /// the reference arm rewrites to <c>new PdfReference(canonical, <b>0</b>)</c> — so today
        /// every reference in a hashed object has its generation <b>normalised to zero</b>, whether
        /// or not its object number moved. Skipping the copy on the strength of
        /// <c>canonical == num</c> alone would preserve a non-zero generation, change the hash, and
        /// stop two objects that differ only there from merging. The obvious form of this
        /// optimisation is wrong, and no test asserting the *answer* on ordinary input would have
        /// said so, because generation numbers are almost always zero.</para>
        /// <para>Walks without allocating, so the common case — a subtree with nothing to rewrite —
        /// costs a traversal instead of a full deep clone.</para>
        /// </remarks>
        private static bool NeedsCanonicalisation(PdfObject obj, Dictionary<int, int> dedupMap,
            int depth)
        {
            if (depth > MaxRemapDepth) return false;

            switch (obj)
            {
                case PdfReference reference:
                    if (!dedupMap.TryGetValue(reference.ReferenceObjectNumber, out int canonical))
                        return false;
                    return canonical != reference.ReferenceObjectNumber
                        || reference.ReferenceGenerationNumber != 0;

                // A stream is a dictionary here: its data is hashed beside the canonical form,
                // never through it.
                case PdfDictionary dict:
                    string previousKey = null;
                    foreach (var kvp in dict)
                    {
                        // Keys out of byte order: the copy re-inserts them in order (see
                        // CanonicaliseForHash), so the subtree is not unchanged.
                        if (previousKey != null && string.CompareOrdinal(previousKey, kvp.Key.Value) > 0)
                            return true;
                        previousKey = kvp.Key.Value;
                        if (NeedsCanonicalisation(kvp.Value, dedupMap, depth + 1)) return true;
                    }
                    return false;

                case PdfArray array:
                    for (int i = 0; i < array.Count; i++)
                        if (NeedsCanonicalisation(array[i], dedupMap, depth + 1)) return true;
                    return false;

                default:
                    return false;
            }
        }

        private static PdfObject CanonicaliseForHash(PdfObject obj, Dictionary<int, int> dedupMap,
            int depth)
        {
            if (depth > MaxRemapDepth) return obj;

            // Nothing below this node moves, so hand back the node itself. Every container arm
            // below allocates unconditionally, so without this the duplicate merge would deep-clone
            // every object every round, only to throw the clone away once serialised.
            if (!NeedsCanonicalisation(obj, dedupMap, depth))
            {
                LastUnchangedSubtreeCount++;
                return obj;
            }

            if (obj is PdfReference reference)
            {
                return dedupMap.TryGetValue(reference.ReferenceObjectNumber, out int canonical)
                    ? new PdfReference(canonical, 0)
                    : obj;
            }

            // Keys re-inserted in byte order: a dictionary's entries are a set (ISO 32000-1
            // §7.3.7), so two dictionaries holding the same entries in different orders are the
            // same object and must hash the same. The hash is the whole equality test — nothing
            // compares the two objects afterwards — so the order is decided here. A fresh copy
            // has had nothing removed, so it enumerates in insertion order.
            if (obj is PdfDictionary dict)
            {
                var copy = obj is PdfStream ? new PdfStream() : new PdfDictionary(dict.Count);
                foreach (var kvp in dict.OrderBy(entry => entry.Key.Value, StringComparer.Ordinal))
                    copy.Set(kvp.Key, CanonicaliseForHash(kvp.Value, dedupMap, depth + 1));
                return copy;
            }

            if (obj is PdfArray array)
            {
                var copy = new PdfArray();
                for (int i = 0; i < array.Count; i++)
                    copy.Add(CanonicaliseForHash(array[i], dedupMap, depth + 1));
                return copy;
            }

            return obj;
        }

        // -------------------------------------------------------------------
        //  Reachability analysis
        // -------------------------------------------------------------------

        private static HashSet<int> FindReachableObjects(
            PdfDictionary trailer,
            Dictionary<int, PdfObject> allObjects,
            Dictionary<int, int> dedupMap)
        {
            var visited = new HashSet<int>();
            var stack = new Stack<PdfObject>();

            foreach (var kvp in trailer)
            {
                stack.Push(kvp.Value);
            }

            while (stack.Count > 0)
            {
                var current = stack.Pop();

                if (current is PdfReference reference)
                {
                    int targetNum = reference.ReferenceObjectNumber;

                    if (dedupMap.TryGetValue(targetNum, out int canonical))
                        targetNum = canonical;

                    if (!visited.Add(targetNum))
                        continue;

                    if (allObjects.TryGetValue(targetNum, out var resolved))
                    {
                        stack.Push(resolved);
                    }
                    continue;
                }

                if (current is PdfDictionary dict)
                {
                    foreach (var entry in dict)
                    {
                        stack.Push(entry.Value);
                    }
                    continue;
                }

                if (current is PdfArray array)
                {
                    for (int i = 0; i < array.Count; i++)
                    {
                        stack.Push(array[i]);
                    }
                }
            }

            return visited;
        }

        // -------------------------------------------------------------------
        //  Reference remapping
        // -------------------------------------------------------------------

        private static int MaxRemapDepth => PdfLimits.MaxObjectGraphDepth;

        private static PdfObject RemapReferences(
            PdfObject obj,
            Dictionary<int, int> remapTable,
            Dictionary<int, int> dedupMap,
            int depth = 0)
        {
            if (depth > MaxRemapDepth) return obj;
            if (obj is PdfReference reference)
            {
                int target = reference.ReferenceObjectNumber;

                if (dedupMap.TryGetValue(target, out int canonical))
                    target = canonical;

                if (remapTable.TryGetValue(target, out int newNum))
                    return new PdfReference(newNum, 0);

                return PdfNull.Instance;
            }

            if (obj is PdfStream stream)
            {
                var newStream = new PdfStream();
                foreach (var kvp in stream)
                {
                    newStream.Set(kvp.Key, RemapReferences(kvp.Value, remapTable, dedupMap, depth + 1));
                }
                newStream.RawData = stream.RawData;

                if (newStream.RawData != null)
                {
                    newStream.Set(PdfName.Length, new PdfNumber(newStream.RawData.Length));
                }

                return newStream;
            }

            if (obj is PdfDictionary dict)
            {
                var newDict = new PdfDictionary();
                foreach (var kvp in dict)
                {
                    newDict.Set(kvp.Key, RemapReferences(kvp.Value, remapTable, dedupMap, depth + 1));
                }
                return newDict;
            }

            if (obj is PdfArray array)
            {
                var newArray = new PdfArray();
                for (int i = 0; i < array.Count; i++)
                {
                    newArray.Add(RemapReferences(array[i], remapTable, dedupMap, depth + 1));
                }
                return newArray;
            }

            return obj;
        }

        private static PdfDictionary RemapTrailer(
            PdfDictionary trailer,
            Dictionary<int, int> remapTable,
            Dictionary<int, int> dedupMap)
        {
            var result = new PdfDictionary();
            foreach (var kvp in trailer)
            {
                result.Set(kvp.Key, RemapReferences(kvp.Value, remapTable, dedupMap));
            }
            return result;
        }

        /// <summary>
        /// Builds the output trailer as a <b>whitelist</b> of the keys a freshly written file may
        /// carry, rather than a copy of the input's.
        ///
        /// <para><b>Why a whitelist.</b> Cloning every key and correcting only <c>/Size</c> would
        /// produce a file that contradicts itself:</para>
        /// <list type="bullet">
        ///   <item>A hybrid-reference input carries <c>/XRefStm</c>, whose offset points at
        ///     arbitrary content in the new file — a reader honouring it builds a wrong object table
        ///     or rejects the document.</item>
        ///   <item><c>PdfParser</c> returns the cross-reference <b>stream dictionary</b> as the
        ///     trailer for a PDF 1.5+ input, so a copy would emit a classic <c>trailer &lt;&lt;…&gt;&gt;</c>
        ///     carrying <c>/Type /XRef</c>, <c>/W</c>, <c>/Index</c>, <c>/Filter</c>,
        ///     <c>/DecodeParms</c> and <c>/Length</c> in front of a classic <c>xref</c> table —
        ///     a combination §7.5.5 does not permit.</item>
        /// </list>
        /// <para>The sibling writer in this assembly, <c>PdfLinearizer.BuildFirstPageTrailer</c>,
        /// follows the same rule.</para>
        ///
        /// <para><b>/ID is preserved verbatim, deliberately.</b> §14.4 says the second element
        /// should change when a file is modified, but deriving a new one from content the writer has
        /// not produced yet would either be a fabricated value or make output non-reproducible.
        /// Byte-reproducible builds are worth more here than a changed-since-save marker, and
        /// nothing in this library reads it.</para>
        /// </summary>
        private static PdfDictionary BuildOutputTrailer(PdfDictionary source)
        {
            var trailer = new PdfDictionary();
            if (source == null) return trailer;

            if (source[PdfName.Root] is PdfObject root) trailer.Set(PdfName.Root, root);
            if (source[PdfName.Info] is PdfObject info) trailer.Set(PdfName.Info, info);
            if (source[PdfName.ID] is PdfObject id) trailer.Set(PdfName.ID, id);

            return trailer;
        }

        // -------------------------------------------------------------------
        //  Image optimization
        // -------------------------------------------------------------------

        private static void OptimizeImages(Dictionary<int, PdfObject> objects, PdfDictionary trailer,
            ImageOptimizationOptions imgOptions, OptimizationOptions options, OptimizationStats stats)
        {
            var smaskObjectNumbers = new HashSet<int>();
            foreach (var kvp in objects)
            {
                if (kvp.Value is PdfStream stream)
                {
                    if (stream[PdfName.SMask] is PdfReference smaskRef)
                        smaskObjectNumbers.Add(smaskRef.ReferenceObjectNumber);
                    if (stream[PdfName.Mask] is PdfReference maskRef)
                        smaskObjectNumbers.Add(maskRef.ReferenceObjectNumber);
                }
            }

            // How large each image is actually drawn on the page. Without this, TargetDpi has
            // nothing to compare against — see ImagePlacementScanner.
            Dictionary<int, double> placements = null;
            if (imgOptions.TargetDpi > 0)
            {
                placements = ImagePlacementScanner.Scan(objects, trailer, options,
                    out string placementReason);

                // The reason is honoured: the scanner documents its map as advisory once it is set,
                // and a paint site the scan never reached could have been at any size, so a partial
                // map is not a basis for a lossy resample in either direction — the posture
                // SubsetFonts takes on FontGlyphUsageScanner.Reason.
                // MaxWidth/MaxHeight need no placement evidence and are untouched.
                if (placementReason != null)
                {
                    Report("Optimize.Images",
                        "No image was downsampled to TargetDpi: the placement scan was incomplete (" +
                        placementReason + "). Resampling against the placements it did find would " +
                        "discard samples a paint site the scan never reached still needs.", stats);
                    placements = null;
                }
            }

            // NOTE: the incomplete-scan case is handled above, where the map is both REPORTED and
            // DROPPED. An earlier revision only reported it here and carried on downsampling, which
            // is the weaker half: the scanner's contract makes the map advisory once `reason` is
            // set, so a placement measured from part of the evidence is not a basis for a lossy
            // resample in either direction.
            foreach (var kvp in objects.ToList())
            {
                if (!(kvp.Value is PdfStream stream))
                    continue;

                var subtype = DerefName(stream, PdfName.Subtype, objects);
                if (subtype == null || subtype.Value != "Image")
                    continue;

                if (stream.RawData == null || stream.RawData.Length < imgOptions.MinImageSizeBytes)
                    continue;

                int origWidth = DerefInteger(stream, PdfName.Width, objects) ?? 0;
                int origHeight = DerefInteger(stream, PdfName.Height, objects) ?? 0;
                if (origWidth <= 0 || origHeight <= 0)
                    continue;

                // A swatch: nothing to gain that is worth a lossy generation.
                if (imgOptions.IsBelowMinimumSide(origWidth, origHeight))
                    continue;

                stats.ImagesExamined++;

                bool hasTransparency = stream[PdfName.SMask] != null
                    || stream[PdfName.Mask] != null
                    || stream[PdfName.SMaskInData] != null;
                bool isSMask = smaskObjectNumbers.Contains(kvp.Key);

                // A colour-key /Mask is an array of sample ranges expressed in the image's ORIGINAL
                // colour space (§8.9.6.4). Re-encoding changes the samples those ranges refer to, so
                // the mask would then key on values that no longer mean anything.
                if (Deref(stream[PdfName.Mask], objects) is PdfArray)
                {
                    Report("Optimize.Images",
                        "Object " + kvp.Key + " has a colour-key /Mask, whose sample ranges are stated " +
                        "in the image's original colour space; re-encoding would leave them keying on " +
                        "values that no longer exist, so the image was left as it was.", stats);
                    continue;
                }

                // §11.6.5.3: a soft mask carrying /Matte matches its parent's /Width and /Height, and
                // /Matte has one entry per parent colour component. Resampling the parent alone, or
                // turning it grey, breaks both; the mask has no placement of its own to resample by.
                if (Deref(stream[PdfName.SMask], objects) is PdfStream softMask && softMask[PdfName.Matte] != null)
                {
                    Report("Optimize.Images",
                        "Object " + kvp.Key + "'s soft mask carries /Matte, which binds the mask's size and the " +
                        "image's component count to the image (§11.6.5.3); re-encoding the image would leave them " +
                        "disagreeing, so it was left as it was.", stats);
                    continue;
                }
                if (isSMask && stream[PdfName.Matte] != null)
                {
                    Report("Optimize.Images",
                        "Object " + kvp.Key + " is a soft mask carrying /Matte, whose size must match its image's " +
                        "(§11.6.5.3); it was left as it was.", stats);
                    continue;
                }

                if (imgOptions.SkipAlreadyOptimized && ShouldSkipWellCompressed(
                        stream, objects, origWidth, origHeight, imgOptions, placements, kvp.Key))
                {
                    continue;
                }

                int beforeLen = stream.RawData.Length;
                try
                {
                    OptimizeSingleImage(stream, origWidth, origHeight, imgOptions, options, objects,
                        stats, kvp.Key, placements, hasTransparency || isSMask);

                    if (stream.RawData.Length < beforeLen)
                        stats.ImagesOptimized++;
                }
                catch (Exception ex)
                {
                    stats.ImageFailures[kvp.Key] = ex;
                    Report("Optimize.Images", "Object " + kvp.Key + " threw while being re-encoded.",
                        stats, ex);
                }
            }
        }

        /// <summary>
        /// Decides whether an already-compressed image is worth re-encoding.
        ///
        /// <para>A bytes-per-pixel floor would skip almost every JPEG a real producer emits — a
        /// quality-75 JPEG is around 0.2–0.5 bytes per pixel. The question that matters is whether
        /// re-encoding could help: it can if the image is drawn smaller
        /// than its pixel count (so downsampling wins) or if it is stored far more expensively than
        /// the requested quality implies.</para>
        /// </summary>
        private static bool ShouldSkipWellCompressed(PdfStream stream,
            Dictionary<int, PdfObject> objects, int width, int height,
            ImageOptimizationOptions imgOptions, Dictionary<int, double> placements, int objectNumber)
        {
            var chain = GetFilterChain(stream, objects);
            string filter = chain != null && chain.Count > 0 ? chain[0] : null;
            // JPEG 2000 too: a codestream is already compressed, and the bytes-per-pixel budget
            // below asks the same question of it that it asks of a JPEG.
            bool isLossy = filter == PdfFilterNames.DctDecode || filter == PdfFilterNames.DctAbbrev
                || filter == PdfFilterNames.JpxDecode;
            if (!isLossy) return false;

            // Downsampling is still on the table, so this image is worth visiting.
            if (imgOptions.MaxWidth > 0 && width > imgOptions.MaxWidth) return false;
            if (imgOptions.MaxHeight > 0 && height > imgOptions.MaxHeight) return false;

            if (placements != null
                && placements.TryGetValue(objectNumber, out double effectiveDpi)
                && imgOptions.DownsamplesAt(effectiveDpi))
            {
                return false;
            }

            // Nothing to gain from geometry, so judge the encoding itself. A JPEG already at or
            // below roughly the bytes-per-pixel its target quality implies will not get smaller
            // without visibly degrading, and re-encoding it costs a generation of quality for
            // nothing.
            double bytesPerPixel = (double)stream.RawData.Length / ((double)width * height);
            double budget = 0.05 + (imgOptions.Quality / 100.0) * 0.55;
            return bytesPerPixel <= budget;
        }

        /// <summary>
        /// JPEGs this thread decoded below their dictionary's <c>/Width</c> for a resample target, for
        /// tests: the reduced decode's liveness, since its output has the same size either way. A
        /// counter read as a difference across one call, not a slot an operation resets.
        /// </summary>
        [ThreadStatic] internal static int ReducedJpegDecodes;

        /// <summary>
        /// The size an image is resampled to, from its dictionary's <c>/Width</c> and <c>/Height</c>:
        /// down to <c>TargetDpi</c> at the resolution it is painted at, then within <c>MaxWidth</c> and
        /// <c>MaxHeight</c>. False, with the size unchanged, when nothing asks for a resample.
        /// </summary>
        private static bool ResampleTarget(int origWidth, int origHeight, ImageOptimizationOptions imgOptions,
            Dictionary<int, double> placements, int objectNumber, out int newWidth, out int newHeight)
        {
            newWidth = origWidth;
            newHeight = origHeight;
            bool needsResize = false;

            // Downsample against the resolution the image is actually PAINTED at, which is a
            // property of the CTM at the `Do` that draws it, not of any key on the image
            // dictionary. An image nothing draws has no placement and is left alone.
            if (placements != null
                && placements.TryGetValue(objectNumber, out double effectiveDpi)
                && imgOptions.DownsamplesAt(effectiveDpi))
            {
                double scaleFactor = imgOptions.TargetDpi / effectiveDpi;
                if (scaleFactor < 1.0)
                {
                    newWidth = Math.Max(1, SaturatingCast.ToInt(Math.Round(origWidth * scaleFactor)));
                    newHeight = Math.Max(1, SaturatingCast.ToInt(Math.Round(origHeight * scaleFactor)));
                    needsResize = true;
                }
            }

            if (imgOptions.MaxWidth > 0 && newWidth > imgOptions.MaxWidth)
            {
                double scale = (double)imgOptions.MaxWidth / newWidth;
                newWidth = imgOptions.MaxWidth;
                newHeight = Math.Max(1, SaturatingCast.ToInt(newHeight * scale));
                needsResize = true;
            }

            if (imgOptions.MaxHeight > 0 && newHeight > imgOptions.MaxHeight)
            {
                double scale = (double)imgOptions.MaxHeight / newHeight;
                newHeight = imgOptions.MaxHeight;
                newWidth = Math.Max(1, SaturatingCast.ToInt(newWidth * scale));
                needsResize = true;
            }
            return needsResize;
        }

        private static void OptimizeSingleImage(PdfStream stream, int origWidth, int origHeight,
            ImageOptimizationOptions imgOptions, OptimizationOptions options,
            Dictionary<int, PdfObject> objects, OptimizationStats stats, int objectNumber,
            Dictionary<int, double> placements, bool preserveTransparency = false)
        {
            // The target is known before the decode, so a JPEG is decoded no larger than it needs to
            // be for it: decoded in full and resampled afterwards, a 2900² frame drawn at 24 pt costs
            // 85.7 ms and 57.7 MB (OptimizeBenchmarks).
            bool resizes = ResampleTarget(origWidth, origHeight, imgOptions, placements, objectNumber,
                out int targetWidth, out int targetHeight);
            SKBitmap bitmap = DecodeImageToBitmap(stream, origWidth, origHeight, objects, options,
                out string colorSpace, out string decodeReason,
                resizes ? targetWidth : 0, resizes ? targetHeight : 0);
            if (bitmap == null)
            {
                Report("Optimize.Images",
                    "Object " + objectNumber + " was left unchanged because " + decodeReason + ".", stats);
                return;
            }

            SKBitmap grayscaleBitmap = null;
            try
            {
                var targetFormat = imgOptions.TargetFormat;
                if (targetFormat == ImageFormat.Original)
                    targetFormat = DetectOriginalFormat(stream, objects);

                // A soft mask folded into the JPEG 2000 file as its opacity channel: the one lossy
                // target an image with transparency may keep, because image and mask are then one
                // codestream and cannot misregister. Decided now; the mask is decoded once the
                // working size is known, and a mask that will not decode falls back to Flate.
                PdfStream foldedMask = null;
                if (targetFormat == ImageFormat.Jpeg2000 && imgOptions.FoldSoftMaskIntoJpeg2000 && preserveTransparency)
                {
                    foldedMask = FoldableSoftMask(stream, objects, out string notFolded);
                    if (foldedMask == null && notFolded != null)
                        Report("Optimize.Images", "Object " + objectNumber + "'s soft mask was not folded into " +
                            "its JPEG 2000 re-encode because " + notFolded + "; it was kept lossless.", stats);
                }

                if (preserveTransparency && foldedMask == null
                    && (targetFormat == ImageFormat.Jpeg || targetFormat == ImageFormat.WebP || targetFormat == ImageFormat.Jpeg2000))
                    targetFormat = ImageFormat.Png;

                if (imgOptions.DownsampleMonochromeImages)
                {
                    var srcChain = GetFilterChain(stream, objects);
                    string srcFilter = srcChain != null && srcChain.Count > 0 ? srcChain[0] : null;
                    if (srcFilter == PdfFilterNames.CcittFaxDecode
                        || srcFilter == PdfFilterNames.CcittFaxAbbrev
                        || srcFilter == PdfFilterNames.Jbig2Decode)
                    {
                        targetFormat = ImageFormat.Png;
                    }
                }

                bool isGray = colorSpace == PdfColorSpaceNames.DeviceGray;

                if (imgOptions.ConvertToGrayscale && !isGray)
                {
                    grayscaleBitmap = ConvertToGrayscale(bitmap);
                    bitmap.Dispose();
                    bitmap = grayscaleBitmap;
                    grayscaleBitmap = null;
                    isGray = true;
                }

                int newWidth = targetWidth;
                int newHeight = targetHeight;
                bool needsResize = resizes;

                // The target comes from /Width and /Height; the samples are the bitmap's. A target past
                // them would upscale, and a 2 KB JPEG declaring a huge height would drive a
                // multi-gigabyte Resize before any limit was asked. Never upscale, and never past the
                // limits a decode is held to.
                if (needsResize && (newWidth > bitmap.Width || newHeight > bitmap.Height
                    || newWidth > MaxReencodeSide || newHeight > MaxReencodeSide
                    || (long)newWidth * newHeight > MaxReencodePixels))
                {
                    Report("Optimize.Images",
                        "Object " + objectNumber + " was left unchanged because its resample target " + newWidth + " x " +
                        newHeight + " is larger than its " + bitmap.Width + " x " + bitmap.Height +
                        " samples or the re-encode limits.", stats);
                    return;
                }

                SKBitmap workingBitmap = bitmap;
                SKBitmap resizedBitmap = null;
                if (needsResize)
                {
                    resizedBitmap = bitmap.Resize(new SKImageInfo(newWidth, newHeight),
                        new SKSamplingOptions(SKCubicResampler.Mitchell));
                    if (resizedBitmap == null)
                        return;
                    workingBitmap = resizedBitmap;
                }

                // The samples about to be written are the bitmap's, so the dictionary must describe
                // the bitmap. For a raw-sample image the two agree — the decoder checks the buffer
                // against /Width·/Height exactly — but a DCT stream is decoded by Skia from its own
                // SOF header, and a JPEG whose frame size disagrees with the dictionary is a real
                // shape of malformed file: its pixels under the dictionary's /Width would make a
                // stream every reader mis-strides.
                if (workingBitmap.Width != newWidth || workingBitmap.Height != newHeight)
                {
                    newWidth = workingBitmap.Width;
                    newHeight = workingBitmap.Height;
                    needsResize = true;     // so /Width and /Height are rewritten to match
                }

                // The folded mask at the working size: §11.6.5.3 lets a soft mask have its own
                // dimensions, mapped onto the image's unit square, so it is resampled to the samples
                // it now sits beside.
                byte[] opacity = null;
                if (foldedMask != null)
                {
                    opacity = FoldedOpacity(foldedMask, newWidth, newHeight, objects, options, out string notFolded);
                    if (opacity == null)
                    {
                        Report("Optimize.Images", "Object " + objectNumber + "'s soft mask was not folded into " +
                            "its JPEG 2000 re-encode because " + notFolded + "; it was kept lossless.", stats);
                        foldedMask = null;
                        targetFormat = ImageFormat.Png;   // kept lossless, as an unfolded mask is
                    }
                }

                try
                {
                    byte[] encodedData;
                    PdfName pdfFilter;
                    PdfName pdfColorSpace;
                    bool predicted = false;

                    switch (targetFormat)
                    {
                        // WebP and GIF are not PDF stream filters — ISO 32000-1 has no such
                        // encodings, so both have always been stored as the nearest thing that is
                        // one. Sharing the arm makes the substitution visible in the code rather
                        // than hidden in two identical-looking cases.
                        case ImageFormat.Jpeg:
                        case ImageFormat.WebP:
                            // A grey image goes out as a one-component JPEG. The working bitmap is
                            // RGBA, which Skia encodes as three components whatever the channels hold,
                            // and the /DeviceGray written below over such a frame made every reader
                            // that strides by the dictionary shear the image (corpus file issue5475, GAPS
                            // §19h item 4); this library's own renderer reads the frame's count and
                            // never saw it.
                            if (isGray)
                            {
                                using SKBitmap grey = workingBitmap.Copy(SKColorType.Gray8);
                                encodedData = grey == null ? null : EncodeToSkiaFormat(grey, SKEncodedImageFormat.Jpeg, imgOptions.Quality);
                            }
                            else
                                encodedData = EncodeToSkiaFormat(workingBitmap, SKEncodedImageFormat.Jpeg, imgOptions.Quality);
                            pdfFilter = PdfName.DCTDecode;
                            pdfColorSpace = isGray ? PdfName.DeviceGray : PdfName.DeviceRGB;
                            break;

                        case ImageFormat.Png:
                        case ImageFormat.Gif:
                            encodedData = EncodeToFlatePixels(workingBitmap, newWidth, newHeight,
                                isGray, imgOptions.PngCompressionLevel, out pdfColorSpace,
                                out predicted);
                            pdfFilter = PdfName.FlateDecode;
                            break;

                        // The in-house JPEG 2000 encoder, through Core's public facade —
                        // this plugin is outside Core's InternalsVisibleTo — as a JP2 file whose
                        // colour box agrees with the /ColorSpace written below.
                        case ImageFormat.Jpeg2000:
                        {
                            byte[] colour = isGray ? ExtractGrayPixels(workingBitmap) : ExtractRgbPixels(workingBitmap);
                            int colourComponents = isGray ? PdfColorSpaceNames.GrayComponents : PdfColorSpaceNames.RgbComponents;
                            encodedData = opacity == null
                                ? JpxImageEncoding.Encode(colour, newWidth, newHeight, colourComponents, 8,
                                    new JpxEncodingOptions { Lossless = false, Quality = imgOptions.Quality })
                                : JpxImageEncoding.Encode(WithOpacity(colour, colourComponents, opacity),
                                    newWidth, newHeight, colourComponents + 1, 8,
                                    new JpxEncodingOptions { Lossless = false, Quality = imgOptions.Quality, OpacityChannel = true });
                            pdfFilter = PdfName.JPXDecode;
                            pdfColorSpace = isGray ? PdfName.DeviceGray : PdfName.DeviceRGB;
                            break;
                        }

                        default:
                            return;
                    }

                    if (encodedData == null || encodedData.Length == 0)
                        return;

                    if (encodedData.Length < stream.RawData.Length)
                    {
                        stream.RawData = encodedData;
                        stream.Set(PdfName.Length, new PdfNumber(encodedData.Length));
                        stream.Set(PdfName.Filter, pdfFilter);
                        stream.Remove(PdfName.DecodeParms);

                        // The lossless arm now offers Flate the samples through a PNG predictor and
                        // keeps whichever is smaller, so it can leave /DecodeParms behind — which
                        // the unconditional Remove above would otherwise have dropped, leaving a
                        // predicted stream declared unpredicted. The colour space here is always
                        // 8-bpc DeviceGray or DeviceRGB because that is what this arm just wrote.
                        if (predicted)
                        {
                            stream.Set(PdfName.DecodeParms,
                                BuildPredictorParms(isGray ? PdfColorSpaceNames.GrayComponents
                                                           : PdfColorSpaceNames.RgbComponents,
                                    8, newWidth));
                        }

                        if (needsResize)
                        {
                            stream.Set(PdfName.Width, new PdfNumber(newWidth));
                            stream.Set(PdfName.Height, new PdfNumber(newHeight));
                        }

                        stream.Set(PdfName.ColorSpace, pdfColorSpace);
                        stream.Set(PdfName.BitsPerComponent, new PdfNumber(8));

                        // /Decode maps samples in the ORIGINAL space and arity (§8.9.5.2). After a
                        // re-encode it describes data that no longer exists — a [1 0] on a gray
                        // image would invert the result, and a 4-entry array on a now-3-component
                        // image is simply malformed. The default mapping is what the new samples
                        // want, and the default is "no /Decode".
                        stream.Remove(PdfName.Decode);

                        // The mask now travels in the codestream: §7.4.9's /SMaskInData 1 says so,
                        // and the separate /SMask goes, since Table 87 lets a reader take either and
                        // only one of them is current.
                        if (opacity != null)
                        {
                            stream.Remove(PdfName.SMask);
                            stream.Set(PdfName.SMaskInData, new PdfNumber(1));
                        }
                    }
                }
                finally
                {
                    resizedBitmap?.Dispose();
                }
            }
            finally
            {
                bitmap?.Dispose();
                grayscaleBitmap?.Dispose();
            }
        }

        /// <summary>
        /// The soft mask an image's JPEG 2000 re-encode may carry as its opacity channel, or
        /// <c>null</c> — with <paramref name="reason"/> saying why when the image has a mask that
        /// cannot be folded, and <c>null</c> when it has none to fold (an image that is itself a mask).
        /// </summary>
        private static PdfStream FoldableSoftMask(PdfStream image, Dictionary<int, PdfObject> objects, out string reason)
        {
            reason = null;
            if (!(Deref(image[PdfName.SMask], objects) is PdfStream mask)) return null;

            if (image[PdfName.Mask] != null)
                reason = "the image also has a /Mask, which one opacity channel cannot carry beside it";
            else if ((DerefInteger(image, PdfName.SMaskInData, objects) ?? 0) != 0)
                reason = "the image already declares /SMaskInData";
            else if (mask[PdfName.Matte] != null)
                reason = "the mask carries /Matte: its image's colour is premultiplied (§11.6.5.3), " +
                         "which /SMaskInData 1 does not describe";
            return reason == null ? mask : null;
        }

        /// <summary>
        /// The folded mask's samples as an 8-bit opacity plane at the working size, or <c>null</c>
        /// with <paramref name="reason"/> when the mask will not decode.
        /// </summary>
        private static byte[] FoldedOpacity(PdfStream mask, int width, int height,
            Dictionary<int, PdfObject> objects, OptimizationOptions options, out string reason)
        {
            int maskWidth = DerefInteger(mask, PdfName.Width, objects) ?? 0;
            int maskHeight = DerefInteger(mask, PdfName.Height, objects) ?? 0;
            if (maskWidth <= 0 || maskHeight <= 0) { reason = "the mask has no usable size"; return null; }

            SKBitmap decoded = DecodeImageToBitmap(mask, maskWidth, maskHeight, objects, options, out _, out string why);
            if (decoded == null) { reason = "the mask could not be decoded (" + why + ")"; return null; }

            SKBitmap sized = decoded;
            try
            {
                if (decoded.Width != width || decoded.Height != height)
                {
                    sized = decoded.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
                    if (sized == null) { reason = "the mask could not be resampled to the image's size"; return null; }
                }
                reason = null;
                return ExtractGrayPixels(sized);
            }
            finally
            {
                if (!ReferenceEquals(sized, decoded)) sized?.Dispose();
                decoded.Dispose();
            }
        }

        /// <summary>Colour samples of <paramref name="components"/> per pixel with the opacity plane interleaved after them.</summary>
        private static byte[] WithOpacity(byte[] colour, int components, byte[] opacity)
        {
            var interleaved = new byte[opacity.Length * (components + 1)];
            for (int p = 0, c = 0, o = 0; p < opacity.Length; p++)
            {
                for (int k = 0; k < components; k++) interleaved[o++] = colour[c++];
                interleaved[o++] = opacity[p];
            }
            return interleaved;
        }

        /// <summary>
        /// Decodes an image XObject to a bitmap, or returns null with a reason.
        ///
        /// <para><b>This method refuses everything it does not genuinely understand.</b> A DeviceRGB
        /// default for any space other than the bare name <c>/DeviceGray</c>, read as 8 bits per
        /// component and one or three channels, would read an 8-bpc DeviceCMYK image as RGB with a
        /// one-channel stride error and write it back declared <c>/DeviceRGB</c>.</para>
        /// </summary>
        // targetWidth/targetHeight: the size the caller resamples to, or 0. A JPEG is then decoded at
        // the smallest DCT scale that covers it (JpegSamples.TryDecode); every other kind in full.
        private static SKBitmap DecodeImageToBitmap(PdfStream stream, int width, int height,
            Dictionary<int, PdfObject> objects, OptimizationOptions options,
            out string colorSpace, out string reason, int targetWidth = 0, int targetHeight = 0)
        {
            colorSpace = PdfColorSpaceNames.DeviceRGB;
            reason = null;

            var chain = GetFilterChain(stream, objects);
            if (chain == null)
            {
                reason = "its /Filter is neither a name nor an array of names";
                return null;
            }

            string filterName = chain.Count > 0 ? chain[0] : null;

            if (filterName == PdfFilterNames.DctDecode || filterName == PdfFilterNames.DctAbbrev)
            {
                if ((Deref(stream[PdfName.ImageMask], objects) as PdfBoolean)?.Value == true)
                {
                    reason = "it is a JPEG stencil mask (/ImageMask), whose one-bit samples the re-encoders do not write";
                    return null;
                }
                // The JPEG's samples through Base's one decode of a DCT image (JpegSamples), taken when
                // the codec's colours are the image's — a device space, or none — the frame is grey or
                // RGB, and no /Decode maps the samples. Otherwise an Adobe CMYK JPEG would come back
                // near-negative, an RGB one with /Decode [1 0 1 0 1 0] would lose the array without its
                // inversion, and a grey one would grow to three channels.
                if (!Tetitulz.Pdf.Base.Images.JpegSamples.CodecColoursAreTheImages(WithResolvedColorSpace(stream, objects), null, stream.RawData))
                {
                    reason = "its JPEG's samples are coloured by its colour space (ICC, Indexed, spot or CIE), which a re-encode in a device space would lose";
                    return null;
                }
                if (HasNonIdentityDecode(stream, objects))
                {
                    reason = "it has a /Decode array that maps its samples, which the re-encoded samples would lose";
                    return null;
                }
                // Refused from the frame header, before a four-component frame costs two decodes.
                if (Tetitulz.Pdf.Base.Images.JpegSamples.FrameComponents(stream.RawData) == PdfColorSpaceNames.CmykComponents)
                {
                    reason = "it is a CMYK JPEG, whose inks a re-encode in a device RGB space would misrepresent";
                    return null;
                }
                byte[] samples = Tetitulz.Pdf.Base.Images.JpegSamples.TryDecode(stream.RawData, MaxReencodeSide, MaxReencodePixels,
                    targetWidth, targetHeight, out int jpegWidth, out int jpegHeight, out int jpegComponents);
                if (samples == null)
                {
                    reason = "the JPEG bitstream could not be decoded";
                    return null;
                }
                colorSpace = jpegComponents == PdfColorSpaceNames.GrayComponents ? PdfColorSpaceNames.DeviceGray : PdfColorSpaceNames.DeviceRGB;
                if (jpegWidth < width) ReducedJpegDecodes++;
                return LoadBitmapFromRawPixels(samples, jpegWidth, jpegHeight, jpegComponents, out reason);
            }

            if (filterName == PdfFilterNames.JpxDecode)
            {
                // Decoded by Core's own JPEG 2000 decoder through its public facade —
                // this plugin is outside Core's InternalsVisibleTo — under the dictionary's facts
                // (ISO 32000-2 §7.4.9), and taken when the samples are 8-bit grey or RGB, which is
                // what the re-encoders below write. Like the JPEG arm, only a lone /JPXDecode is
                // read from RawData; a wrapped one falls to the raw path, whose exact length check
                // refuses the still-compressed bytes.
                if (chain.Count != 1)
                {
                    reason = "its JPEG 2000 codestream sits under other filters";
                    return null;
                }
                // Refused before a sample is decoded: a stencil
                // mask's bits are not a grey image; an Indexed image's samples are its palette's
                // indices, which a re-encode would paint as colour; and a /Decode array maps
                // samples the re-encoded stream no longer has (the Flate/raw arm below refuses the
                // same array; the DCT arm keeps its APP14 assumption, where Skia has already
                // applied the inversion the array describes).
                JpxImageDictionaryFacts jpxFacts = JpxFacts(stream, objects);
                if (jpxFacts.ImageMask)
                {
                    reason = "it is a JPEG 2000 stencil mask (/ImageMask), whose one-bit samples the re-encoders do not write";
                    return null;
                }
                if (jpxFacts.ColourSpaceIsIndexed)
                {
                    reason = "its JPEG 2000 samples are indices into the dictionary's /Indexed palette, which a re-encode would paint as colour";
                    return null;
                }
                if (HasNonIdentityDecode(stream, objects))
                {
                    reason = "it has a /Decode array that maps its samples, which the re-encoded samples would lose";
                    return null;
                }
                if (!JpxImageDecoding.TryDecode(stream.RawData, jpxFacts, out JpxImagePixels jpx, out string why))
                {
                    reason = "its JPEG 2000 codestream could not be decoded (" + why + ")";
                    return null;
                }
                bool jpxGray = jpx.ColourModel == JpxColourModel.Gray
                    || (jpx.ColourModel == JpxColourModel.ImageDictionary && jpx.ColourComponents == PdfColorSpaceNames.GrayComponents);
                bool jpxRgb = jpx.ColourModel == JpxColourModel.Rgb
                    || (jpx.ColourModel == JpxColourModel.ImageDictionary && jpx.ColourComponents == PdfColorSpaceNames.RgbComponents);
                if (!jpxGray && !jpxRgb)
                {
                    reason = "its JPEG 2000 samples are " + jpx.ColourModel + " with " + jpx.ColourComponents
                           + " component(s), and only 8-bit grey and RGB are re-encoded";
                    return null;
                }
                bool jpxDeviceColour = IsDeviceColour(stream, objects, out string jpxFamily);
                if (jpx.IccProfile != null || !jpxDeviceColour)
                {
                    reason = jpx.IccProfile != null
                        ? "its JPEG 2000 samples are coloured by the file's own ICC profile, which a re-encode in /DeviceGray or /DeviceRGB would lose"
                        : "its colour space /" + jpxFamily + " colours its samples itself (a spot or DeviceN tint, a CIE or ICC space), which a re-encode in /DeviceGray or /DeviceRGB would lose";
                    return null;
                }
                if (jpx.Opacity != null)
                {
                    reason = "its JPEG 2000 codestream carries the opacity channel /SMaskInData names, which a re-encode would drop";
                    return null;
                }
                colorSpace = jpxGray ? PdfColorSpaceNames.DeviceGray : PdfColorSpaceNames.DeviceRGB;
                return LoadBitmapFromRawPixels(jpx.Colour, jpx.Width, jpx.Height,
                    jpxGray ? PdfColorSpaceNames.GrayComponents : PdfColorSpaceNames.RgbComponents, out reason);
            }

            // Raw samples. Both the bit depth and the component count have to come from the
            // dictionary, and anything other than 8-bpc gray or RGB is refused rather than
            // reinterpreted.
            int bpc = DerefInteger(stream, PdfName.BitsPerComponent, objects) ?? 8;
            if (bpc != 8)
            {
                reason = "it is " + bpc + " bits per component and only 8 is handled";
                return null;
            }

            // An /Indexed image's samples are its palette's indices, refused as the JPEG 2000 arm
            // refuses them: TryGetComponentCount gives the space one component, and the indices would
            // go out as /DeviceGray — blue cover art in grey.
            if (IsIndexedColorSpace(stream, objects))
            {
                reason = "its samples are indices into its /Indexed palette, which a re-encode would paint as grey";
                return null;
            }
            if (!IsDeviceColour(stream, objects, out string rawFamily))
            {
                reason = "its colour space /" + rawFamily + " colours its samples itself (a spot or DeviceN tint, a CIE or ICC space), which a re-encode in /DeviceGray or /DeviceRGB would lose";
                return null;
            }

            if (!TryGetComponentCount(stream, objects, out int components, out string csReason))
            {
                reason = csReason;
                return null;
            }

            if (components != PdfColorSpaceNames.GrayComponents
                && components != PdfColorSpaceNames.RgbComponents)
            {
                reason = "it has " + components + " colour components and only 1 and 3 are handled";
                return null;
            }

            // Left alone: re-encoding the samples as they are and removing the array afterwards
            // would turn a [1 0] on a grey image out inverted.
            if (HasNonIdentityDecode(stream, objects))
            {
                reason = "it has a /Decode array that maps its samples, which the re-encoded samples would lose";
                return null;
            }

            colorSpace = components == PdfColorSpaceNames.GrayComponents
                ? PdfColorSpaceNames.DeviceGray
                : PdfColorSpaceNames.DeviceRGB;

            if (!TryDecodeBounded(stream, objects, options, out byte[] pixelData, out string decodeReason))
            {
                reason = "it could not be decoded (" + decodeReason + ")";
                return null;
            }

            if (pixelData == null)
            {
                reason = "decoding produced no data";
                return null;
            }

            return LoadBitmapFromRawPixels(pixelData, width, height, components, out reason);
        }

        /// <summary>
        /// What the image dictionary tells the JPEG 2000 decoder (ISO 32000-2 §7.4.9): the
        /// <c>/ColorSpace</c>'s component count when it has one this pass can read (0 when absent,
        /// so the file's own colour governs), whether that space is Indexed, <c>/SMaskInData</c>,
        /// and <c>/ImageMask</c> — the facts as they are, so the arm above can refuse an Indexed
        /// image and a stencil by what the dictionary says rather than by a guess.
        /// </summary>
        private static JpxImageDictionaryFacts JpxFacts(PdfStream stream, Dictionary<int, PdfObject> objects)
        {
            bool imageMask = (Deref(stream[PdfName.ImageMask], objects) as PdfBoolean)?.Value == true;
            PdfObject cs = Deref(stream[PdfName.ColorSpace], objects);
            bool indexed = IsIndexedColorSpace(stream, objects);
            int components = 0;
            if (indexed) components = 1;
            else if (cs != null && TryGetComponentCount(stream, objects, out int declared, out _))
                components = declared;
            int smaskInData = DerefInteger(stream, PdfName.SMaskInData, objects) ?? 0;
            return new JpxImageDictionaryFacts(components, indexed, smaskInData, imageMask);
        }

        /// <summary>
        /// True when the image's colour is what a re-encode writes: no <c>/ColorSpace</c>,
        /// <c>/DeviceGray</c> or <c>/DeviceRGB</c>; <paramref name="family"/> names any other. The raw
        /// and JPEG 2000 arms ask it (the JPEG arm asks <c>JpegSamples.CodecColoursAreTheImages</c>,
        /// which also reads the frame): writing <c>/DeviceGray</c> or <c>/DeviceRGB</c> over any space
        /// of one or three components would turn a <c>/Separation</c> tint negative, paint <c>/Lab</c>
        /// bytes as RGB, and drop a calibration or ICC profile.
        /// </summary>
        private static bool IsDeviceColour(PdfStream stream, Dictionary<int, PdfObject> objects, out string family)
        {
            PdfObject cs = Deref(stream[PdfName.ColorSpace], objects);
            family = cs is PdfName named ? named.Value
                : cs is PdfArray array && array.Count > 0 && Deref(array[0], objects) is PdfName first ? first.Value
                : null;
            if (cs == null) return true;
            return family == PdfColorSpaceNames.DeviceGray || family == PdfColorSpaceNames.DeviceRGB;
        }

        /// <summary>True when the image's <c>/ColorSpace</c> is an <c>/Indexed</c> array (§8.6.6.3), whose samples are palette indices — the one question the JPEG 2000 arm and the raw arm both ask before re-encoding.</summary>
        private static bool IsIndexedColorSpace(PdfStream stream, Dictionary<int, PdfObject> objects)
            => Deref(stream[PdfName.ColorSpace], objects) is PdfArray array && array.Count > 0
               && Deref(array[0], objects) is PdfName family
               && (family.Value == PdfColorSpaceNames.Indexed || family.Value == PdfColorSpaceNames.IndexedAbbrev);

        /// <summary>
        /// The image dictionary with its <c>/ColorSpace</c> resolved through the object map — the
        /// entry itself and an array's elements — for <see cref="Tetitulz.Pdf.Base.Images.JpegSamples"/>,
        /// which reads a dictionary as it stands when it has no document to resolve references with.
        /// </summary>
        private static PdfDictionary WithResolvedColorSpace(PdfStream stream, Dictionary<int, PdfObject> objects)
        {
            PdfObject cs = Deref(stream[PdfName.ColorSpace], objects);
            if (cs is PdfArray array)
            {
                var resolved = new PdfArray();
                for (int i = 0; i < array.Count; i++)
                {
                    PdfObject element = Deref(array[i], objects);
                    // An ICC profile's /N resolved: read raw, an indirect one would fall back to three.
                    if (element is PdfDictionary profile && profile[PdfName.N] is PdfReference)
                    {
                        PdfDictionary withArity = PdfDictionary.CloneShallow(profile);
                        withArity.Set(PdfName.N, Deref(profile[PdfName.N], objects));
                        element = withArity;
                    }
                    resolved.Add(element);
                }
                cs = resolved;
            }
            PdfDictionary copy = PdfDictionary.CloneShallow(stream);
            if (cs != null) copy.Set(PdfName.ColorSpace, cs);
            return copy;
        }

        /// <summary>The longest side and the most pixels <see cref="LoadBitmapFromRawPixels"/> accepts — what a JPEG is decoded under.</summary>
        private const int MaxReencodeSide = 65535;
        private const long MaxReencodePixels = 64_000_000L;

        /// <summary>True when the image has a <c>/Decode</c> array that is not the identity mapping (a pair other than <c>0 1</c>).</summary>
        private static bool HasNonIdentityDecode(PdfStream stream, Dictionary<int, PdfObject> objects)
        {
            if (Deref(stream[PdfName.Decode], objects) is not PdfArray decode || decode.Count == 0) return false;
            for (int i = 0; i < decode.Count; i++)
            {
                double v = (Deref(decode[i], objects) as PdfNumber)?.DoubleValue ?? double.NaN;
                if (v != (i % 2 == 0 ? 0d : 1d)) return true;
            }
            return false;
        }

        private static SKBitmap LoadBitmapFromRawPixels(byte[] pixelData, int width, int height,
            int channels, out string reason)
        {
            reason = null;

            if (width <= 0 || height <= 0) { reason = "its dimensions are not positive"; return null; }
            if (width > MaxReencodeSide || height > MaxReencodeSide) { reason = "its dimensions exceed " + MaxReencodeSide; return null; }
            if ((long)width * height > MaxReencodePixels) { reason = "it exceeds 64 megapixels"; return null; }

            bool isGray = channels == PdfColorSpaceNames.GrayComponents;
            long expected = (long)width * height * channels;
            if (expected > int.MaxValue) { reason = "its sample buffer would exceed 2 GB"; return null; }
            int expectedSize = (int)expected;

            // Exact, not "at least". A buffer bigger than the declared geometry means the two
            // disagree, and reading the first expectedSize bytes of it silently discards the rest —
            // the way a CMYK image would turn into a skewed RGB one.
            if (pixelData.Length != expectedSize)
            {
                reason = "it decodes to " + pixelData.Length + " bytes but its dimensions and colour "
                       + "space describe " + expectedSize;
                return null;
            }

            var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);

            // Build the RGBA pixel buffer in one pass then bulk-copy into the bitmap.
            // This is dramatically faster than SetPixel which has per-call overhead.
            int totalPixels = width * height;
            var rgba = new byte[totalPixels * 4];
            int src = 0, dst = 0;

            if (isGray)
            {
                for (int i = 0; i < totalPixels; i++)
                {
                    byte g = pixelData[src++];
                    rgba[dst++] = g;
                    rgba[dst++] = g;
                    rgba[dst++] = g;
                    rgba[dst++] = 255;
                }
            }
            else
            {
                for (int i = 0; i < totalPixels; i++)
                {
                    rgba[dst++] = pixelData[src++]; // R
                    rgba[dst++] = pixelData[src++]; // G
                    rgba[dst++] = pixelData[src++]; // B
                    rgba[dst++] = 255;              // A
                }
            }

            Marshal.Copy(rgba, 0, bitmap.GetPixels(), rgba.Length);
            return bitmap;
        }

        private static SKBitmap ConvertToGrayscale(SKBitmap source)
        {
            var grayscale = new SKBitmap(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
            using (var canvas = new SKCanvas(grayscale))
            using (var paint = new SKPaint())
            {
                // ITU-R BT.601 (GAPS.md §2b) — every row is the same luminance sum, so the
                // filter writes the grey level into all three channels.
                const float lr = PdfColorSpaceNames.LumaRedF,
                            lg = PdfColorSpaceNames.LumaGreenF,
                            lb = PdfColorSpaceNames.LumaBlueF;
                var colorMatrix = new float[]
                {
                    lr, lg, lb, 0, 0,
                    lr, lg, lb, 0, 0,
                    lr, lg, lb, 0, 0,
                    0,  0,  0,  1, 0
                };
                paint.ColorFilter = SKColorFilter.CreateColorMatrix(colorMatrix);
                canvas.DrawBitmap(source, 0, 0, SKSamplingOptions.Default, paint);
            }
            return grayscale;
        }

        private static byte[] EncodeToSkiaFormat(SKBitmap bitmap, SKEncodedImageFormat format, int quality)
        {
            using (var image = SKImage.FromBitmap(bitmap))
            using (var data = image.Encode(format, quality))
            {
                if (data == null)
                    return null;
                return data.ToArray();
            }
        }

        /// <summary>
        /// Reads a whole bitmap as tightly packed, unpremultiplied RGBA8888 in one call.
        ///
        /// <para><b>One call, not width × height of them.</b> Walking every pixel through
        /// <c>SKBitmap.GetPixel</c> would cost a managed→native transition, a bounds check and an
        /// unpremultiply <i>per pixel</i> — twelve million of them on a 4000 × 3000 photograph — for
        /// work Skia does in a single strided copy, as <c>LoadBitmapFromRawPixels</c> on the load side
        /// builds its buffer in one pass and bulk-copies.</para>
        ///
        /// <para><c>SKPixmap.ReadPixels</c> converts colour type and alpha on the way out, so the
        /// bytes are the same ones <c>GetPixel</c> reported whatever layout the source bitmap is in
        /// — a JPEG Skia decoded to the platform's BGRA included. Reading
        /// <c>SKBitmap.GetPixelSpan</c> directly would be one copy cheaper and is the wrong trade:
        /// it hands back whatever layout the bitmap happens to hold, so the caller would have to
        /// re-implement the swizzle and the unpremultiply, and get them right on every platform.
        /// </para>
        /// </summary>
        /// <returns><c>null</c> when Skia declines the read; callers fall back to the per-pixel path.</returns>
        private static byte[] ReadRgbaPixels(SKBitmap bitmap)
        {
            var info = new SKImageInfo(bitmap.Width, bitmap.Height,
                SKColorType.Rgba8888, SKAlphaType.Unpremul);

            long total = (long)info.Width * info.Height * 4;
            if (total <= 0 || total > int.MaxValue) return null;

            var buffer = new byte[(int)total];

            using (var pixmap = bitmap.PeekPixels())
            {
                if (pixmap == null) return null;

                var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    if (!pixmap.ReadPixels(info, handle.AddrOfPinnedObject(), info.Width * 4))
                        return null;
                }
                finally
                {
                    handle.Free();
                }
            }

            return buffer;
        }

        private static byte[] ExtractRgbPixels(SKBitmap bitmap)
        {
            var pixels = new byte[bitmap.Width * bitmap.Height * 3];

            byte[] rgba = ReadRgbaPixels(bitmap);
            if (rgba != null)
            {
                int src = 0, dst = 0;
                while (dst < pixels.Length)
                {
                    pixels[dst++] = rgba[src++];    // R
                    pixels[dst++] = rgba[src++];    // G
                    pixels[dst++] = rgba[src++];    // B
                    src++;                          // A — the PDF stream carries none
                }
                return pixels;
            }

            int offset = 0;
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    var px = bitmap.GetPixel(x, y);
                    pixels[offset++] = px.Red;
                    pixels[offset++] = px.Green;
                    pixels[offset++] = px.Blue;
                }
            }
            return pixels;
        }

        /// <summary>
        /// Flattens a bitmap to one 8-bit grey sample per pixel.
        ///
        /// <para>ITU-R BT.601 in <b>double</b> arithmetic with a truncating cast — see GAPS.md §2b
        /// for why the double constants and not the float ones (<c>(double)0.299f != 0.299</c>), and
        /// note that the truncation is deliberate rather than overlooked: rounding here would change
        /// every grey sample the optimizer produces, for no gain.</para>
        /// <para><b>A neutral pixel is its own grey.</b> A grey image or soft mask reaches here as
        /// R = G = B, and the weighted sum of three equal values is not always that value in binary
        /// floating point: truncated, 65 of the 256 levels (1, 2, 4, 128, 254 …) would come out one
        /// lower, and the PNG and Flate targets — documented lossless — would change a grey image's
        /// samples and every soft mask's alpha. Such a pixel is copied.</para>
        /// </summary>
        private static byte[] ExtractGrayPixels(SKBitmap bitmap)
        {
            var pixels = new byte[bitmap.Width * bitmap.Height];

            byte[] rgba = ReadRgbaPixels(bitmap);
            if (rgba != null)
            {
                int src = 0;
                for (int i = 0; i < pixels.Length; i++)
                {
                    pixels[i] = Luma(rgba[src], rgba[src + 1], rgba[src + 2]);
                    src += 4;
                }
                return pixels;
            }

            int offset = 0;
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    var px = bitmap.GetPixel(x, y);
                    pixels[offset++] = Luma(px.Red, px.Green, px.Blue);
                }
            }
            return pixels;
        }

        /// <summary>BT.601 grey of one pixel, truncated as this path always wrote it — and a neutral pixel exactly its value.</summary>
        private static byte Luma(byte r, byte g, byte b)
            => r == g && g == b
                ? r
                : SaturatingCast.ToByte(PdfColorSpaceNames.LumaRed * r + PdfColorSpaceNames.LumaGreen * g + PdfColorSpaceNames.LumaBlue * b);

        // -------------------------------------------------------------------
        //  CCITT Group 4 for bilevel images
        // -------------------------------------------------------------------

        /// <summary>
        /// Re-encodes 1-bit-per-pixel single-component images with CCITT Group 4.
        ///
        /// <para><b>Every pixel is preserved exactly</b>: <c>CcittFaxEncoder</c> round-trips against
        /// <c>CcittFaxDecodeFilter</c> for the same <c>/Columns</c>, <c>/Rows</c> and
        /// <c>/BlackIs1</c>. Because the sample values survive unchanged, <c>/Decode</c>,
        /// <c>/ImageMask</c> and the colour space stay valid and are left alone — this pass changes
        /// how the samples are stored, never what they mean.</para>
        ///
        /// <para><b>It is not byte-for-byte.</b> When
        /// <c>/Width</c> is not a multiple of eight, the last byte of every row carries padding bits
        /// past the final pixel; §8.9.3 makes those not sample data, and the round trip regenerates
        /// them as white rather than reproducing them. No reader can observe the difference — bits
        /// beyond <c>/Columns</c> are ignored — but a caller diffing decoded buffers can.
        /// <c>Optimize_PreservesEveryPixelButNotThePaddingBitsAtARaggedWidth</c> pins both halves.</para>
        ///
        /// <para>G4 is not universally smaller. It models runs along a scanline, so it wins on
        /// scanned text and line art and loses on dithered photographs, where it can more than
        /// double the data. Every candidate is therefore encoded and committed only if it beats
        /// what is already in the file.</para>
        ///
        /// <para><b>JBIG2 beside it</b> (<see cref="OptimizationOptions.UseJbig2ForBilevelImages"/>):
        /// <c>Jbig2EncodeFilter.EncodeWithSymbols</c> codes each distinct connected component once and
        /// places every occurrence — exact matching, so as lossless as G4 — and on a typed page is a
        /// thirtieth of G4's size. With both passes on, each image takes the shorter of the two that
        /// beat its current bytes. T.88's bitmap has 1 for black where a PDF sample of 0 is, and the
        /// <c>/JBIG2Decode</c> filter's output is PDF samples (as this library's decoder and every
        /// reader produce them), so the samples are inverted on the way in and come back as they were,
        /// whatever <c>/Decode</c> or <c>/ImageMask</c> make of them.</para>
        /// </summary>
        private static void RecompressBilevelImages(
            Dictionary<int, PdfObject> objects, OptimizationOptions options, OptimizationStats stats)
        {
            foreach (var kvp in objects)
            {
                if (!(kvp.Value is PdfStream stream)) continue;

                // One predicate, CarriesReEncodableSamples: the /Subtype /Image gate and the
                // empty-or-single-Flate filter test.
                if (!CarriesReEncodableSamples(stream, objects)) continue;

                bool isImageMask = (Deref(stream[PdfName.ImageMask], objects) as PdfBoolean)?.Value == true;
                int bpc = DerefInteger(stream, PdfName.BitsPerComponent, objects) ?? (isImageMask ? 1 : 0);
                if (bpc != 1) continue;

                // An image mask has no colour space and is one component by definition (§8.9.6.2);
                // anything else has to prove it is single-component before its samples can be read
                // as a bilevel raster.
                if (!isImageMask)
                {
                    if (!TryGetComponentCount(stream, objects, out int components, out _)) continue;
                    if (components != 1) continue;
                }

                int width = DerefInteger(stream, PdfName.Width, objects) ?? 0;
                int height = DerefInteger(stream, PdfName.Height, objects) ?? 0;
                if (width <= 0 || height <= 0) continue;

                // The encoder refuses these outright; checking here keeps a legal-but-huge image a
                // skip rather than an exception out of Optimize().
                if (width > CcittFaxEncoder.MaxEncodeColumns) continue;
                if (height > CcittFaxEncoder.MaxEncodeRows) continue;
                if ((long)width * height > CcittFaxEncoder.MaxEncodePixels) continue;

                if (!TryDecodeBounded(stream, objects, options, out byte[] samples, out string reason))
                {
                    Report("Optimize.Ccitt",
                        "Object " + kvp.Key + " could not be decoded (" + reason + ").", stats);
                    continue;
                }
                if (samples == null) continue;

                // Exact, not "at least": a buffer that does not match the declared geometry is not
                // the raster the dictionary describes, and encoding the first rows of it would
                // silently discard the rest.
                long stride = (width + 7) / 8;
                if (samples.LongLength != stride * height)
                {
                    Report("Optimize.Ccitt",
                        "Object " + kvp.Key + " decodes to " + samples.LongLength + " bytes but its " +
                        "/Width and /Height describe " + (stride * height) + "; it was left alone.", stats);
                    continue;
                }

                byte[] g4 = options.UseCcittForBilevelImages
                    ? CcittFaxEncoder.Encode(samples, width, height, blackIs1: false)
                    : null;

                int current = stream.RawData?.Length ?? int.MaxValue;

                byte[] jbig2 = null;
                if (options.UseJbig2ForBilevelImages)
                {
                    var t88 = new byte[samples.Length];
                    for (int i = 0; i < t88.Length; i++) t88[i] = (byte)~samples[i];
                    jbig2 = Jbig2EncodeFilter.EncodeWithSymbols(t88, width, height, (int)stride);

                    // Where the symbol form does not pay much — a dithered picture is thousands of
                    // small components of many shapes — the generic region may: it costs a second,
                    // slower pass over the pixels, so it is tried only when the symbol form has not
                    // already cut the current bytes to a quarter, which a page of text does and a
                    // picture does not.
                    if (jbig2 != null && (long)jbig2.Length * 4 >= current)
                    {
                        byte[] generic = Jbig2EncodeFilter.Encode(t88, width, height, (int)stride);
                        if (generic != null && generic.Length < jbig2.Length) jbig2 = generic;
                    }
                }

                bool takeJbig2 = jbig2 != null && jbig2.Length < current && (g4 == null || jbig2.Length < g4.Length);
                bool takeG4 = !takeJbig2 && g4 != null && g4.Length < current;
                if (!takeJbig2 && !takeG4) continue;

                if (takeJbig2)
                {
                    stream.RawData = jbig2;
                    stream.Set(PdfName.Filter, PdfName.JBIG2Decode);
                    stream.Set(PdfName.Length, new PdfNumber(jbig2.Length));
                    stream.Remove(PdfName.DecodeParms);               // no globals, nothing to declare
                    stats.BilevelImagesRecompressedAsJbig2++;
                }
                else
                {
                    stream.RawData = g4;
                    stream.Set(PdfName.Filter, PdfName.CCITTFaxDecode);
                    stream.Set(PdfName.Length, new PdfNumber(g4.Length));

                    var parms = new PdfDictionary();
                    parms.Set(PdfName.K, new PdfNumber(-1));          // G4 (T.6)
                    parms.Set(PdfName.Columns, new PdfNumber(width));
                    parms.Set(PdfName.Rows, new PdfNumber(height));
                    parms.Set(PdfName.BlackIs1, PdfBoolean.False);
                    stream.Set(PdfName.DecodeParms, parms);
                }

                stats.BilevelImagesRecompressed++;
            }
        }

        // -------------------------------------------------------------------
        //  Font subsetting
        // -------------------------------------------------------------------

        /// <summary>
        /// Reduces embedded TrueType font programs to the glyphs the document actually draws.
        ///
        /// <para>The two halves are deliberately separate types.
        /// <see cref="FontGlyphUsageScanner"/> decides <i>which glyphs are drawn</i> and fails
        /// closed on any uncertainty; <see cref="TrueTypeGlyphPruner"/> rewrites the font program
        /// <i>without renumbering glyphs</i>, so <c>/CIDToGIDMap</c>, <c>/W</c> and <c>hmtx</c>
        /// remain correct untouched. Read both class comments before changing either — a subsetter
        /// that gets this wrong produces a file that is still valid PDF and draws blanks.</para>
        ///
        /// <para>An incomplete scan disables subsetting for the <b>whole document</b> rather than
        /// for the fonts that happened to fail: an unresolvable font name could have belonged to any
        /// program in the file, so there is no safe per-font fallback.</para>
        /// </summary>
        private static void SubsetFonts(
            Dictionary<int, PdfObject> objects, PdfDictionary trailer, OptimizationOptions options,
            OptimizationStats stats)
        {
            var usage = FontGlyphUsageScanner.Scan(objects, trailer, options);

            if (usage.Reason != null)
            {
                Report("Optimize.SubsetFonts",
                    "No font was subset: the glyph-usage scan was incomplete (" + usage.Reason +
                    "). Pruning on partial evidence would delete glyphs the document draws.", stats);
                return;
            }

            foreach (var entry in usage.UsedGlyphs)
            {
                int number = entry.Key;
                HashSet<int> glyphs = entry.Value;

                if (usage.Blocked.Contains(number))
                {
                    Report("Optimize.SubsetFonts",
                        "Object " + number + " is also reached by a font whose character codes " +
                        "could not be mapped to glyph IDs; the program was left alone.", stats);
                    continue;
                }

                // The font was selected but never drew anything. That is likelier to be a hole in
                // the scan than a genuinely unused program, and the saving would be marginal.
                if (glyphs.Count == 0) continue;

                if (!objects.TryGetValue(number, out PdfObject candidate)) continue;
                if (!(candidate is PdfStream program)) continue;

                if (!TryDecodeBounded(program, objects, options, out byte[] fontBytes, out string decodeReason))
                {
                    Report("Optimize.SubsetFonts",
                        "Object " + number + " could not be decoded (" + decodeReason + ").", stats);
                    continue;
                }
                if (fontBytes == null || fontBytes.Length == 0) continue;

                bool isCff = usage.CidKeyedPrograms.Contains(number);
                byte[] pruned = isCff
                    ? BlankCffCharstrings(fontBytes, glyphs, out string pruneReason)
                    : TrueTypeGlyphPruner.Prune(fontBytes, glyphs, out pruneReason);
                if (pruned == null)
                {
                    Report("Optimize.SubsetFonts", "Object " + number + ": " + pruneReason + ".", stats);
                    continue;
                }

                byte[] compressed = FlateEncodeFilter.Encode(pruned, options.FlateCompressionLevel);

                // Never commit a result that is not smaller than what is already in the file.
                //
                // This is the ONLY size test on the CFF path and it has to be: blanking overwrites
                // charstrings in place, so `pruned.Length == fontBytes.Length` always, and a test
                // comparing decoded lengths would reject every correct CFF subset. What shrinks is
                // the compressed stream, which is exactly what this line measures.
                int storedLength = program.RawData?.Length ?? int.MaxValue;
                if (compressed.Length >= storedLength) continue;

                stats.FontBytesSaved += storedLength - compressed.Length;
                program.RawData = compressed;
                program.Set(PdfName.Filter, PdfName.FlateDecode);
                program.Set(PdfName.Length, new PdfNumber(compressed.Length));

                // /Length1 is the decoded length of a FontFile2 (Table 127) and is now stale. A
                // FontFile3 has no /Length1 at all — the key is defined for FontFile and FontFile2
                // only — so writing one would add a key the spec does not give that stream, over a
                // value no reader would consult.
                if (!isCff) program.Set(PdfName.Length1, new PdfNumber(pruned.Length));

                // The filter chain was replaced wholesale, so any parameters describing the old one
                // would be applied to the new bytes.
                program.Remove(PdfName.DecodeParms);

                stats.FontsSubset++;
            }
        }

        /// <summary>
        /// Subsets a <c>CIDFontType0</c>'s CFF program to <paramref name="cids"/> by blanking every
        /// other charstring, or returns <c>null</c> with <paramref name="reason"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>Two indirections, and getting either wrong is silent.</b> The scanner records raw
        /// <b>CIDs</b> for this font shape, because turning a CID into a glyph index needs the CFF
        /// charset and that lives inside the program. So the CIDs are resolved first
        /// (<see cref="PdfFontSubsetter.TryMapCidsToCffGlyphs"/>) and only then handed to the
        /// blanker. Passing the CIDs straight through works perfectly on the many fonts whose
        /// charset happens to be the identity and destroys every other one.</para>
        /// <para><b>Blanking rather than rebuilding is doubly right here.</b> An already-embedded
        /// font's glyph indices are load-bearing — <c>/W</c> and the content stream's CIDs both
        /// address them — so nothing may renumber. Blanking cannot: it changes charstring
        /// <i>contents</i> and no offset at all. That is the same property that made
        /// <see cref="TrueTypeGlyphPruner"/> keep glyph numbering, reached by a different mechanism.</para>
        /// </remarks>
        private static byte[] BlankCffCharstrings(byte[] fontBytes, HashSet<int> cids, out string reason)
        {
            if (!PdfFontSubsetter.TryMapCidsToCffGlyphs(fontBytes, cids, out int[] gids, out reason))
            {
                reason = "its CIDs could not be resolved to glyph ids (" + reason + ")";
                return null;
            }

            return PdfFontSubsetter.TrySubsetCff(fontBytes, gids, out byte[] subset, out reason)
                ? subset
                : null;
        }

        // -------------------------------------------------------------------
        //  PNG predictor for lossless image streams
        // -------------------------------------------------------------------

        // -------------------------------------------------------------------
        //  JPEG entropy re-coding
        // -------------------------------------------------------------------

        /// <summary>
        /// Re-fits each baseline JPEG's Huffman tables to its own symbol statistics
        /// (<see cref="Jpeg.JpegLosslessOptimizer"/>).
        ///
        /// <para><b>Nothing but the stream bytes changes, and that is the whole design.</b> The
        /// image dictionary is left exactly as it was — <c>/Width</c>, <c>/Height</c>,
        /// <c>/ColorSpace</c>, <c>/Decode</c>, <c>/SMask</c>, <c>/BitsPerComponent</c> — because the
        /// rebuilt JPEG has the same dimensions, the same component IDs and sampling factors, and
        /// the same Adobe APP14 transform marker, all copied through verbatim. A PDF's colour
        /// interpretation of a DCT stream is written against those, so a pass that rewrote them
        /// would change how the image is painted even though its pixels were preserved.</para>
        ///
        /// <para>Only a bare <c>/DCTDecode</c> is taken. A stream filtered
        /// <c>[/ASCII85Decode /DCTDecode]</c> has to be peeled first, and
        /// <see cref="OptimizationOptions.ReplaceAsciiFilters"/> (step 2, <c>CompressStreams</c>) is
        /// the pass that does that — this one runs after it in the same call, so such a stream is
        /// reached on the same pass through the document rather than needing a second one.</para>
        ///
        /// <para>Failures are silent by design. Most JPEGs are simply not improvable — they already
        /// carry fitted tables — and a diagnostic that fires on ordinary, well-produced input is one
        /// a caller learns to ignore.</para>
        /// </summary>
        private static void RecompressJpegImages(Dictionary<int, PdfObject> objects,
            OptimizationStats stats)
        {
            foreach (var kvp in objects)
            {
                if (!(kvp.Value is PdfStream stream)) continue;

                var subtype = DerefName(stream, PdfName.Subtype, objects);
                if (subtype == null || subtype.Value != "Image") continue;

                var chain = GetFilterChain(stream, objects);
                if (chain == null || chain.Count != 1) continue;
                if (chain[0] != PdfFilterNames.DctDecode && chain[0] != PdfFilterNames.DctAbbrev)
                    continue;

                byte[] jpeg = stream.RawData;
                if (jpeg == null || jpeg.Length == 0) continue;

                byte[] rebuilt = Jpeg.JpegLosslessOptimizer.TryOptimize(jpeg, out _);
                if (rebuilt == null || rebuilt.Length >= jpeg.Length) continue;

                stream.RawData = rebuilt;
                stream.Set(PdfName.Length, new PdfNumber(rebuilt.Length));

                stats.JpegImagesRecompressed++;
            }
        }

        // -------------------------------------------------------------------
        //  Palette (/Indexed) conversion
        // -------------------------------------------------------------------

        /// <summary>
        /// Re-expresses images that use at most 256 distinct colours as <c>/Indexed</c> over a
        /// palette — one index per pixel instead of three or four components, packed to the width
        /// the palette actually needs.
        ///
        /// <para><b>Exactly lossless.</b> Every distinct colour becomes a palette entry, so a
        /// pixel's colour survives byte for byte; see <see cref="PdfIndexedImageConverter"/> for why
        /// this must never be allowed to become a colour reduction.</para>
        ///
        /// <para><b>The refusals are where the correctness is.</b> Four of them exist because the
        /// keys involved are stated in terms of the image's <i>current</i> colour space and sample
        /// values, so conversion would leave them describing something that no longer exists:</para>
        /// <list type="bullet">
        ///   <item>A colour-key <c>/Mask</c> array (§8.9.6.4) names sample ranges in the source
        ///     space; after conversion those ranges would key on palette <i>indices</i>.</item>
        ///   <item>A <c>/Decode</c> array (§8.9.5.2) is stated in the source space and arity; an
        ///     <c>/Indexed</c> image's default is <c>[0 2^bpc−1]</c>.</item>
        ///   <item>An image serving as another image's <c>/SMask</c> must be greyscale (§11.6.5.3).
        ///     This is why the mask set is collected before the loop, as <see cref="OptimizeImages"/>
        ///     does.</item>
        ///   <item>An already-<c>/Indexed</c> or <c>/Pattern</c> space cannot be an
        ///     <c>/Indexed</c> base (§8.6.6.3).</item>
        /// </list>
        ///
        /// <para>Everything else is a plain non-applicability — a photograph, a JPEG, a 16-bit
        /// image — and is passed over in silence rather than reported, because there is nothing the
        /// caller could do about it and a diagnostic that fires on ordinary input is one they learn
        /// to ignore.</para>
        /// </summary>
        private static void ConvertImagesToIndexed(Dictionary<int, PdfObject> objects,
            OptimizationOptions options, OptimizationStats stats)
        {
            // An image reached as a soft mask or a stencil mask is not free to change colour space.
            var maskObjectNumbers = new HashSet<int>();
            foreach (var kvp in objects)
            {
                if (!(kvp.Value is PdfStream stream)) continue;
                if (stream[PdfName.SMask] is PdfReference smaskRef)
                    maskObjectNumbers.Add(smaskRef.ReferenceObjectNumber);
                if (stream[PdfName.Mask] is PdfReference maskRef)
                    maskObjectNumbers.Add(maskRef.ReferenceObjectNumber);
            }

            foreach (var kvp in objects)
            {
                if (!(kvp.Value is PdfStream stream)) continue;
                if (maskObjectNumbers.Contains(kvp.Key)) continue;

                // One predicate, CarriesReEncodableSamples: the /Subtype /Image gate and the
                // empty-or-single-Flate filter test.
                if (!CarriesReEncodableSamples(stream, objects)) continue;

                // A stencil mask has no colour space to replace (§8.9.6.2).
                if ((Deref(stream[PdfName.ImageMask], objects) as PdfBoolean)?.Value == true) continue;

                if ((DerefInteger(stream, PdfName.BitsPerComponent, objects) ?? 8) != 8) continue;

                // Both of these describe the samples that are about to stop existing.
                if (Deref(stream[PdfName.Mask], objects) is PdfArray) continue;
                if (Deref(stream[PdfName.Decode], objects) is PdfArray) continue;

                var baseSpace = stream[PdfName.ColorSpace];
                if (!IsIndexableBase(baseSpace, objects)) continue;
                if (!TryGetComponentCount(stream, objects, out int components, out _)) continue;
                if (components < 1 || components > 4) continue;

                int imageWidth = DerefInteger(stream, PdfName.Width, objects) ?? 0;
                int imageHeight = DerefInteger(stream, PdfName.Height, objects) ?? 0;
                if (imageWidth <= 0 || imageHeight <= 0) continue;

                long expectedSamples = (long)imageWidth * imageHeight * components;
                if (expectedSamples <= 0 || expectedSamples > options.MaxStreamDecodeBytes) continue;

                if (!TryDecodeBounded(stream, objects, options, out byte[] samples, out string decodeReason))
                {
                    Report("Optimize.IndexedImages",
                        "Object " + kvp.Key + " could not be decoded (" + decodeReason + ").", stats);
                    continue;
                }

                if (samples == null || samples.LongLength != expectedSamples) continue;

                var built = PdfIndexedImageConverter.TryBuild(samples, imageWidth, imageHeight, components);
                if (built == null) continue;                 // more than 256 colours: not a palette image

                // Convert only when an index is genuinely narrower than the pixel it replaces.
                //
                // Without this, every 8-bpc greyscale image in every document converts, because a
                // one-byte sample cannot hold more than 256 distinct values and so *always* fits a
                // palette. The index is then also eight bits: identical storage, plus a 256-entry
                // lookup table. It still passed the size comparison below, because renumbering the
                // greys in order of first appearance happens to deflate a little better — a real
                // saving, but one bought by turning a DeviceGray image into an Indexed one for a
                // couple of per cent. A two-tone greyscale scan is the case worth having, and it
                // survives: two entries pack to one bit, an eightfold win, and the CCITT pass
                // cannot reach it because that requires the image to be one bit already.
                if (built.BitsPerComponent >= components * 8) continue;

                byte[] indexed = FlateEncodeFilter.Encode(built.Indices, options.FlateCompressionLevel);
                if (indexed == null) continue;

                // The palette travels with the image, in the image dictionary, which is written
                // uncompressed — so it has to be paid for in full: two characters per byte for the
                // hex string, plus the surrounding [/Indexed base hival …] array.
                long newSize = indexed.Length + built.Palette.Length * 2L + IndexedArrayOverhead;

                // Measure against the best the OTHER passes would have reached, not against the
                // bytes sitting in the stream right now. CompressStreams runs after this one and
                // re-deflates an image's samples at the caller's level, so comparing against the
                // current bytes credits this pass with a saving the next pass would have delivered
                // anyway — and then charges the palette on top. Measured on a corpus document whose
                // two images converted against the current bytes: the image streams 2 349 bytes
                // smaller and the file 170 bytes LARGER, both conversions marginal against the stale
                // baseline and neither against the real one. The predictor pass follows the same rule.
                byte[] unindexed = FlateEncodeFilter.Encode(samples, options.FlateCompressionLevel);
                long baseline = stream.RawData?.Length ?? int.MaxValue;
                if (unindexed != null && unindexed.Length < baseline) baseline = unindexed.Length;

                if (newSize >= baseline) continue;

                var space = new PdfArray();
                space.Add(PdfName.Indexed);
                space.Add(baseSpace);                        // kept as written, so an indirect ICC profile stays indirect
                space.Add(new PdfNumber(built.PaletteEntries - 1));
                space.Add(new PdfString(built.Palette, isHex: true));

                stream.RawData = indexed;
                stream.Set(PdfName.Filter, PdfName.FlateDecode);
                stream.Set(PdfName.Length, new PdfNumber(indexed.Length));
                stream.Set(PdfName.ColorSpace, space);
                stream.Set(PdfName.BitsPerComponent, new PdfNumber(built.BitsPerComponent));
                stream.Remove(PdfName.DecodeParms);

                stats.ImagesPalettized++;
            }
        }

        /// <summary>
        /// The bytes an <c>[/Indexed base hival &lt;…&gt;]</c> array costs beyond the palette's own
        /// hex digits. Deliberately generous: a comparison that under-counts the new form is how a
        /// pass reports a saving while growing the file.
        /// </summary>
        private const int IndexedArrayOverhead = 48;

        /// <summary>
        /// §8.6.6.3: an <c>/Indexed</c> space's base may be any colour space except <c>/Pattern</c>
        /// and another <c>/Indexed</c>.
        /// </summary>
        private static bool IsIndexableBase(PdfObject colorSpace, Dictionary<int, PdfObject> objects)
        {
            var resolved = Deref(colorSpace, objects);

            if (resolved is PdfName name)
                return name.Value != PdfColorSpaceNames.Pattern
                    && name.Value != PdfColorSpaceNames.Indexed;

            if (resolved is PdfArray array && array.Count > 0
                && Deref(array[0], objects) is PdfName family)
            {
                return family.Value != PdfColorSpaceNames.Pattern
                    && family.Value != PdfColorSpaceNames.Indexed;
            }

            return false;
        }

        /// <summary>
        /// Re-encodes lossless image streams with a PNG scanline predictor, which costs Flate
        /// nothing and typically buys 10–30 % on continuous-tone and ruled content.
        ///
        /// <para><b>Every row and every depth.</b> <see cref="PngPredictorEncoder"/> picks the best of
        /// the five PNG filters per row (<i>Up</i> alone predicts well only down a column), and the
        /// pass takes 1-, 2-, 4-, 8- and 16-bit images and <c>/ImageMask</c> stencils — which have no
        /// <c>/ColorSpace</c> by definition (§8.9.6.2) and are often the largest bitonal streams in a
        /// scanned document.</para>
        ///
        /// <para>Trial-and-commit throughout: the predicted form is written only when it beats both
        /// what is in the file today and plain Flate of the same samples.</para>
        /// </summary>
        private static void ApplyPngPredictorToImages(
            Dictionary<int, PdfObject> objects, OptimizationOptions options, OptimizationStats stats)
        {
            var level = options.FlateCompressionLevel;

            foreach (var kvp in objects)
            {
                if (!(kvp.Value is PdfStream stream)) continue;
                // One predicate, CarriesReEncodableSamples: the /Subtype /Image gate and the
                // empty-or-single-Flate filter test.
                if (!CarriesReEncodableSamples(stream, objects)) continue;

                // Skip if predictor already applied.
                var dp = Deref(stream[PdfName.DecodeParms], objects) as PdfDictionary;
                if (dp != null)
                {
                    var pred = Deref(dp[PdfName.Predictor], objects) as PdfNumber;
                    if (pred != null && pred.IntValue >= 10) continue;
                }

                int width = DerefInteger(stream, PdfName.Width, objects) ?? 0;
                int height = DerefInteger(stream, PdfName.Height, objects) ?? 0;
                if (width <= 0 || height <= 0) continue;

                // Hostile or merely wrong dimensions must not reach the arithmetic below: rowSize*height
                // in int would wrap for /Width 30000 /Height 30000 /DeviceRGB, passing the "is the
                // buffer big enough" guard and throwing OverflowException out of Optimize.
                if (width > 65535 || height > 65535) continue;

                // §8.9.6.2: a stencil mask carries one bit per sample and has no colour space at
                // all, so asking for a component count is the wrong question rather than a failure.
                bool isImageMask = (Deref(stream[PdfName.ImageMask], objects) as PdfBoolean)?.Value == true;

                int bpc;
                int colors;
                if (isImageMask)
                {
                    bpc = 1;
                    colors = 1;
                }
                else
                {
                    bpc = DerefInteger(stream, PdfName.BitsPerComponent, objects) ?? 8;

                    // The component count has to come from the colour space, not from a guess:
                    // guessing 3 for everything that is not DeviceGray would truncate every 8-bpc
                    // DeviceCMYK image to three quarters of its samples, and count the loss as a saving.
                    if (!TryGetComponentCount(stream, objects, out colors, out string csReason))
                    {
                        Report("Optimize.PngPredictor",
                            "Object " + kvp.Key + ": " + csReason + "; the predictor was not applied.",
                            stats);
                        continue;
                    }
                }

                if (!PngPredictorEncoder.IsSupportedBitDepth(bpc)) continue;

                long rowSizeLong = PngPredictorEncoder.RowBytes(width, colors, bpc);
                if (rowSizeLong <= 0) continue;
                long expectedLong = rowSizeLong * height;
                if (expectedLong > int.MaxValue || (rowSizeLong + 1) * height > int.MaxValue) continue;

                if (!TryDecodeBounded(stream, objects, options, out byte[] rawPixels, out string reason))
                {
                    Report("Optimize.PngPredictor",
                        "Object " + kvp.Key + " could not be decoded (" + reason + ").", stats);
                    continue;
                }

                if (rawPixels == null) continue;
                int rowSize = (int)rowSizeLong;

                // Exact, not "at least". A longer buffer means the declared geometry does not
                // describe the data, and predicting the first rowSize*height bytes of it silently
                // discards the rest.
                if (rawPixels.Length != rowSize * height)
                {
                    Report("Optimize.PngPredictor",
                        "Object " + kvp.Key + " decodes to " + rawPixels.Length + " bytes but its " +
                        "/Width, /Height, /BitsPerComponent and /ColorSpace describe " +
                        (rowSize * height) + "; the predictor was not applied.", stats);
                    continue;
                }

                byte[] filtered = PngPredictorEncoder.Encode(rawPixels, width, colors, bpc, height);
                if (filtered == null) continue;

                byte[] compressed = FlateEncodeFilter.Encode(filtered, level);
                if (compressed == null || compressed.Length >= (stream.RawData?.Length ?? int.MaxValue))
                    continue;

                // Beating what is stored is not the same as being worth doing, and the difference is
                // measurable: across a 14-document sample, 142 lossless images predicted to
                // 8.08 MB against 7.16 MB for the same samples through plain Flate — prediction lost
                // by 13 %. It loses whenever the samples are drawn from a small alphabet, because
                // differencing an alphabet of k values yields up to 2k−1, and the Huffman stage
                // pays for every one of them. Indexed images, screen shots and flat business
                // graphics are all that shape, and they are most of the lossless images in a real
                // document.
                //
                // Committing whenever an uncompressed original is beaten would make such an image
                // ~13 % larger for a caller who enables this option alone. The pass declines instead;
                // CompressStreams, on by default, packs the stream plainly.
                byte[] unpredicted = FlateEncodeFilter.Encode(rawPixels, level);
                if (unpredicted != null && unpredicted.Length <= compressed.Length) continue;

                stream.RawData = compressed;
                stream.Set(PdfName.Filter, PdfName.FlateDecode);
                stream.Set(PdfName.Length, new PdfNumber(compressed.Length));

                // Set /DecodeParms so the reader knows to un-predict on decode.
                stream.Set(PdfName.DecodeParms, BuildPredictorParms(colors, bpc, width));

                stats.ImagesPredicted++;
            }
        }

        /// <summary>
        /// The <c>/DecodeParms</c> a PNG-predicted Flate stream needs (§7.4.4.4, Table 10). One
        /// builder for both sites that write one — the predictor pass and the image re-encoder —
        /// because a <c>/Colors</c> or <c>/Columns</c> that disagrees with the samples decodes to a
        /// smear rather than to an error.
        /// </summary>
        private static PdfDictionary BuildPredictorParms(int colors, int bitsPerComponent, int columns)
        {
            var parms = new PdfDictionary();
            parms.Set(PdfName.Predictor, new PdfNumber(PngPredictorEncoder.PngOptimum));
            parms.Set(PdfName.Colors, new PdfNumber(colors));
            parms.Set(PdfName.BitsPerComponent, new PdfNumber(bitsPerComponent));
            parms.Set(PdfName.Columns, new PdfNumber(columns));
            return parms;
        }

        /// <summary>
        /// Encodes a bitmap as the PDF equivalent of a PNG: raw 8-bpc samples through Flate,
        /// offered to the deflater both plain and through a PNG scanline predictor.
        ///
        /// <para><b>The predictor is what makes this arm a real PNG:</b> plain Flate of raw samples is
        /// the one thing a PNG encoder never does — the format
        /// exists because a photograph's neighbouring pixels are nearly equal and their differences
        /// are nearly zero. Both forms are produced and the smaller is returned, so content that
        /// does not predict is unaffected apart from one extra deflate.</para>
        /// </summary>
        /// <param name="predicted">
        /// True when the returned bytes are predictor-filtered, which the caller must declare in
        /// <c>/DecodeParms</c>. Getting this back to the caller is load-bearing: a predicted stream
        /// written without those parameters decodes to a smear rather than to an error.
        /// </param>
        /// <param name="bitmap">The decoded image to re-encode.</param>
        /// <param name="width">Output width in samples.</param>
        /// <param name="height">Output height in samples.</param>
        /// <param name="grayscale">
        /// True to emit one component per pixel rather than three, which the caller has already
        /// decided is lossless for this image.
        /// </param>
        /// <param name="level">Deflate level to compress the sample bytes at.</param>
        /// <param name="pdfColorSpace">
        /// The colour space the emitted samples are in — <c>/DeviceGray</c> or <c>/DeviceRGB</c>,
        /// following <paramref name="grayscale"/>.
        /// </param>
        private static byte[] EncodeToFlatePixels(SKBitmap bitmap, int width, int height,
            bool grayscale, CompressionLevel level, out PdfName pdfColorSpace, out bool predicted)
        {
            predicted = false;

            byte[] rawPixels;
            int colors;
            if (grayscale)
            {
                pdfColorSpace = PdfName.DeviceGray;
                colors = PdfColorSpaceNames.GrayComponents;
                rawPixels = ExtractGrayPixels(bitmap);
            }
            else
            {
                pdfColorSpace = PdfName.DeviceRGB;
                colors = PdfColorSpaceNames.RgbComponents;
                rawPixels = ExtractRgbPixels(bitmap);
            }

            byte[] plain = FlateEncodeFilter.Encode(rawPixels, level);

            byte[] filtered = PngPredictorEncoder.Encode(rawPixels, width, colors, 8, height);
            if (filtered != null)
            {
                byte[] predictedBytes = FlateEncodeFilter.Encode(filtered, level);
                if (predictedBytes != null && plain != null && predictedBytes.Length < plain.Length)
                {
                    predicted = true;
                    return predictedBytes;
                }
            }

            return plain;
        }

        private static ImageFormat DetectOriginalFormat(PdfStream stream,
            Dictionary<int, PdfObject> objects)
        {
            var chain = GetFilterChain(stream, objects);
            string filterName = chain != null && chain.Count > 0 ? chain[0] : null;

            switch (filterName)
            {
                case PdfFilterNames.DctDecode:
                case PdfFilterNames.DctAbbrev:
                    return ImageFormat.Jpeg;
                // A JPEG 2000 image stays one: lossy, re-encoded at the requested quality by the
                // in-house encoder.
                case PdfFilterNames.JpxDecode:
                    return ImageFormat.Jpeg2000;
                default:
                    return ImageFormat.Png;
            }
        }
    }
}
