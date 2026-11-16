// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.IO.Compression;

namespace Tetitulz.Pdf.Plugins.Maintenance
{
    /// <summary>
    /// Options for PDF optimization.
    ///
    /// <para><b>Three safety classes.</b> The individual flags describe a mechanism, which does not
    /// tell a caller what they are risking. Every option below is labelled with one of:</para>
    /// <list type="bullet">
    ///   <item><b>Lossless</b> — nothing a renderer or a reader can observe changes.</item>
    ///   <item><b>Lossy</b> — information is preserved but pixels are not; a re-encoded image is
    ///     visibly different on line art.</item>
    ///   <item><b>Destructive</b> — content is removed from the document.</item>
    /// </list>
    /// <para>The <see cref="Lossless"/>, <see cref="Balanced"/> and <see cref="Aggressive"/> presets
    /// group them accordingly. <see cref="Default"/> is deliberately conservative and is <b>not</b>
    /// one of the three — its output stays byte-compatible for existing callers, so it enables no
    /// newer option.</para>
    /// </summary>
    public sealed class OptimizationOptions
    {
        /// <summary>
        /// <b>Lossless.</b> Compress uncompressed streams with FlateDecode. Default: true.
        /// </summary>
        public bool CompressStreams { get; set; } = true;

        /// <summary>
        /// Deflate compression level used when <see cref="CompressStreams"/> is true.
        /// Fastest = least CPU, Optimal = smaller, SmallestSize = smallest. Default: Optimal.
        /// </summary>
        public CompressionLevel FlateCompressionLevel { get; set; } = CompressionLevel.Optimal;

        /// <summary>
        /// <b>Lossless.</b> Re-compress streams that already have FlateDecode but may have been
        /// produced at a lower compression level. Only takes effect when <see cref="CompressStreams"/>
        /// is also true. Default: false.
        ///
        /// <para>The re-encode is trial-and-commit: the original bytes are kept whenever the
        /// re-compressed form is not smaller, and any <c>/DecodeParms</c> predictor is re-applied
        /// rather than discarded.</para>
        /// </summary>
        public bool RecompressExistingStreams { get; set; } = false;

        /// <summary>
        /// <b>Lossless.</b> Remove duplicate objects (identical content). Default: true.
        ///
        /// <para>Objects whose identity is structural rather than content-derived — page tree nodes,
        /// the catalog, and annotations — are never merged, because two byte-identical blank pages
        /// are still two pages.</para>
        /// </summary>
        public bool RemoveDuplicates { get; set; } = true;

        /// <summary>
        /// <b>Lossless.</b> Remove objects not referenced from the document catalog. Default: true.
        /// </summary>
        public bool RemoveUnusedObjects { get; set; } = true;

        /// <summary>
        /// <b>Lossless.</b> Remove <c>/XObject</c> resource entries that no content stream invokes
        /// with <c>Do</c>, so <see cref="RemoveUnusedObjects"/> then drops the images and forms
        /// they named. Default: false, because <see cref="Default"/> stays byte-compatible;
        /// <see cref="Lossless"/> and the presets built on it turn it on.
        ///
        /// <para>Conservative by construction. The evidence is the optimizer's one content walk:
        /// pages (with <c>/Resources</c> inherited from the page tree), form XObjects (a form
        /// without <c>/Resources</c> uses its invoker's), annotation appearance streams, tiling
        /// patterns and soft-mask groups. Nothing is removed anywhere when that walk was not
        /// complete — an unresolvable <c>Do</c>, a Type 3 font with glyph procedures, a content
        /// part that would not decode, a cycle, a depth or operator ceiling. An entry is removed
        /// only from a resource dictionary whose every holder was walked and which nothing else
        /// refers to, and an <c>/XObject</c> dictionary shared by several resource dictionaries
        /// keeps every name any of them invokes. Names compare as decoded (§7.3.5).</para>
        /// </summary>
        public bool RemoveUnusedXObjectResources { get; set; } = false;

        /// <summary>
        /// Merges a run of four or more image strips a page draws edge to edge into one image: a
        /// printer driver's band per row — <c>q a 0 0 d e f cm /Im Do Q</c> repeated, each strip
        /// upright, as wide as the last and stacked directly on it, every one an 8-bit
        /// <c>/DeviceGray</c> or <c>/DeviceRGB</c> image under a lossless filter with no mask,
        /// <c>/Decode</c> or optional content, drawn at the scale of the others — becomes one
        /// Flate-encoded image of their stacked samples and one <c>Do</c>, so every later image
        /// pass sees the picture rather than its slivers. Lossless: the samples are the strips'
        /// own. A run that differs in any of those is left as it is. Off by default; on in
        /// <see cref="Lossless"/>, <see cref="Balanced"/> and <see cref="Aggressive"/>.
        /// </summary>
        public bool MergeImageStrips { get; set; } = false;

        /// <summary>
        /// <b>Destructive.</b> Remove the trailer's document information dictionary (<c>/Info</c>) —
        /// title, author, producer, creation date and any custom keys. Default: false.
        ///
        /// <para>This is <i>not</i> a superset of <see cref="RemoveXmpMetadata"/>; the two remove
        /// different things and a document commonly carries both. Set both to strip all metadata.</para>
        /// </summary>
        public bool RemoveMetadata { get; set; } = false;

        /// <summary>
        /// <b>Destructive.</b> Remove all annotations (<c>/Annots</c> arrays and their
        /// <c>/AP</c> appearance streams) and the form (<c>/AcroForm</c>). This discards form field
        /// values, comments and link targets — scrubbed from an annotation or a field another route,
        /// such as the structure tree, still reaches. Default: false. <b>No preset enables this.</b>
        /// </summary>
        public bool RemoveAnnotations { get; set; } = false;

        /// <summary>
        /// <b>Destructive.</b> Remove tagged-PDF structure (<c>/MarkInfo</c>, <c>/StructTreeRoot</c>,
        /// <c>/Lang</c>). This destroys PDF/UA conformance and the document's reading order.
        /// Default: false. <b>No preset enables this.</b>
        ///
        /// <para>When the document's XMP claims <c>pdfuaid:part</c>, that claim is stripped too —
        /// a document asserting PDF/UA with no structure tree would be lying about itself.</para>
        /// </summary>
        public bool RemoveDocumentStructure { get; set; } = false;

        /// <summary>
        /// <b>Lossless.</b> Remove empty <c>q…Q</c> graphics-state save/restore pairs from page
        /// content streams. Default: false.
        ///
        /// <para>A stream containing an inline image (<c>BI…ID…EI</c>) whose payload length cannot
        /// be determined exactly is left untouched rather than risk mis-parsing pixel data as
        /// operators.</para>
        /// </summary>
        public bool CleanContentStreams { get; set; } = false;

        /// <summary>
        /// <b>Lossless.</b> Merge multiple <c>/Contents</c> streams for a page into a single stream
        /// so the compressor sees a larger input and achieves a better ratio. Default: false.
        ///
        /// <para>A page sharing any of its content streams with another page is skipped, because
        /// merging would change what the other page draws.</para>
        /// </summary>
        public bool ConcatenateContentStreams { get; set; } = false;

        /// <summary>
        /// <b>Destructive.</b> Remove <b>every</b> XMP metadata stream — the document catalog's and
        /// every object-level one. Default: false. See <see cref="RemoveMetadata"/> for the
        /// trailer's separate <c>/Info</c> dictionary.
        ///
        /// <para><b>Every packet, because the catalog's is almost never where the bytes are.</b>
        /// §14.3.2 lets any object carry a <c>/Metadata</c> stream and Adobe's tools attach one per
        /// placed asset: a 22 MB corpus document held 214 packets totalling 9 679 007 bytes — 45 % of
        /// the optimized file — of which the catalog's was 3 439.</para>
        ///
        /// <para>The saving needs <see cref="RemoveUnusedObjects"/> as well: this removes the
        /// references, and that pass drops the now-unreachable packets.</para>
        ///
        /// <para>Document-level XMP carries a PDF/A or PDF/UA conformance claim and object-level XMP
        /// carries asset provenance and rights, so this is in <see cref="Aggressive"/> only.</para>
        /// </summary>
        public bool RemoveXmpMetadata { get; set; } = false;

        /// <summary>
        /// <b>Lossy.</b> Remove embedded page-thumbnail images (<c>/Thumb</c> from each page
        /// dictionary). Viewers generate thumbnails on demand, so nothing is lost but the
        /// producer's own rendering. Default: false.
        /// </summary>
        public bool RemoveThumbnails { get; set; } = false;

        /// <summary>
        /// <b>Lossless.</b> Strip ASCII encoding outer filters (ASCII85Decode, ASCIIHexDecode) from
        /// streams, re-encoding the underlying data with FlateDecode. The output PDF is always
        /// binary so ASCII encoding is never needed. Default: false.
        ///
        /// <para>Only the ASCII layer is peeled. A stream written as
        /// <c>/Filter [/ASCII85Decode /DCTDecode]</c> keeps its <c>/DCTDecode</c>.</para>
        /// </summary>
        public bool ReplaceAsciiFilters { get; set; } = false;

        /// <summary>
        /// <b>Lossless.</b> Replace LZWDecode-compressed streams with FlateDecode, which typically
        /// achieves better compression. Default: false.
        /// </summary>
        public bool ReplaceLzwWithFlate { get; set; } = false;

        /// <summary>
        /// <b>Lossless.</b> Apply the PNG Up scanline predictor to raw-pixel FlateDecode image
        /// streams before compression. Achieves 10–30% better compression on lossless images.
        /// Default: false.
        /// </summary>
        public bool UsePngPredictorForImages { get; set; } = false;

        /// <summary>
        /// <b>Lossless.</b> Re-derive each baseline JPEG's Huffman tables from its own symbol
        /// statistics and re-code the scan under them — what <c>jpegtran -optimize</c> does.
        /// Default: false.
        ///
        /// <para><b>No coefficient is touched, so no pixel can change</b>: the scan is read as a
        /// sequence of Huffman symbols and written back as the same sequence under better codes.
        /// Nothing is dequantised, inverse-transformed or rounded, because nothing is decoded that
        /// far. Each rebuilt image is then decoded again and checked against the original's symbol
        /// stream before it is accepted, so a transcoder bug surfaces as "no saving" rather than as
        /// a damaged image.</para>
        ///
        /// <para>It pays because most encoders ship the ISO/IEC 10918-1 Annex K <i>example</i>
        /// tables, fitted to no image in particular: 364 of 1 360 JPEGs in a 244 MB corpus carried
        /// them, and re-coding measured <b>6.0 %</b> off the JPEG bytes with every image verified
        /// pixel-identical. <c>DCTDecode</c> is ~46 % of a typical optimized document and nothing
        /// else here can touch it.</para>
        ///
        /// <para>Progressive scans, arithmetic coding, 12-bit samples and multi-scan files are
        /// refused rather than half-handled.</para>
        /// </summary>
        public bool OptimizeJpegHuffmanTables { get; set; } = false;

        /// <summary>
        /// <b>Lossless.</b> Re-express an image that uses at most 256 distinct colours as
        /// <c>/Indexed</c> over a palette, packing the index to the narrowest of 1, 2, 4 or 8 bits
        /// that addresses it. Default: false.
        ///
        /// <para><b>Not a colour reduction.</b> Every distinct colour in the image becomes one
        /// palette entry, so each pixel keeps the exact bytes it had; an image needing a 257th
        /// colour is left alone rather than quantised. What changes is that a pixel costs one index
        /// instead of three or four components — 22–57 % per image on the screen shots, charts and
        /// flat graphics this finds, and nothing at all on a photograph, which is refused at the
        /// 256-colour ceiling.</para>
        ///
        /// <para>Refused, deliberately, for an image carrying a colour-key <c>/Mask</c> or a
        /// <c>/Decode</c> array — both are stated in the sample values that are about to change —
        /// and for one serving as another image's <c>/SMask</c> or <c>/Mask</c>, which §11.6.5.3
        /// requires to stay greyscale.</para>
        /// </summary>
        public bool ConvertImagesToIndexed { get; set; } = false;

        /// <summary>
        /// <b>Lossless.</b> Re-encode bilevel (1-bit-per-pixel, single-component) images with CCITT
        /// Group 4 (<c>/CCITTFaxDecode</c>, <c>/K -1</c>), which beats Flate substantially on scanned
        /// text and line art. Default: false.
        ///
        /// <para>Lossless: G4 is a lossless coder, <b>every pixel</b> is preserved exactly, and the
        /// image dictionary's <c>/Decode</c> and colour space are left untouched — so this changes
        /// how the samples are stored and nothing about what they mean. It is not <i>byte</i>-exact
        /// at a width that is not a multiple of eight: the padding bits past the last pixel of a row
        /// are not sample data (§8.9.3) and come back as white. An image is re-encoded only
        /// when the result is genuinely smaller; a photograph dithered to one bit can encode
        /// <i>larger</i> under G4 than under Flate, and that case is skipped rather than committed.
        /// </para>
        /// </summary>
        public bool UseCcittForBilevelImages { get; set; } = false;

        /// <summary>
        /// <b>Lossless.</b> Re-encode bilevel (1-bit-per-pixel, single-component) images as JBIG2
        /// (<c>/JBIG2Decode</c>) with a symbol dictionary: every distinct connected component once,
        /// every occurrence placed by its coordinates, anything too large to be a symbol as a generic
        /// region. Default: false.
        ///
        /// <para>Lossless as this library's CCITT pass is: two components share a symbol only when
        /// their bitmaps are identical, so <b>every pixel</b> is preserved exactly and the dictionary's
        /// <c>/Decode</c> and colour space are left untouched. Measured on an A4 page of 300-dpi typed
        /// text: Flate 146 KB, G4 308 KB, the JBIG2 generic region 60 KB, the symbol form 9 KB. With
        /// <see cref="UseCcittForBilevelImages"/> also on, each image takes whichever of G4 and JBIG2 is
        /// shorter; an image neither shrinks is left as it is. JBIG2 decoding is in every PDF 1.4
        /// reader, and in PDF/A from Part 1 on.</para>
        /// </summary>
        public bool UseJbig2ForBilevelImages { get; set; } = false;

        /// <summary>
        /// <b>Lossless.</b> Pack non-stream objects into object streams (<c>/ObjStm</c>) and write a
        /// cross-reference stream instead of a classic table (ISO 32000-1 §7.5.7–7.5.8).
        /// Default: false.
        ///
        /// <para>This is the largest structural saving available on object-heavy documents — tagged
        /// PDFs, forms and long page ranges — where thousands of small dictionaries are otherwise
        /// written as plain uncompressed ASCII under a 20-byte-per-object cross-reference table.
        /// It also stops the optimizer <i>inflating</i> an input that already used object streams,
        /// since those are unpacked on read and would otherwise be written back out individually.
        /// Enabling it raises the output's PDF version to 1.5, which is when object streams were
        /// introduced.</para>
        /// </summary>
        public bool UseObjectStreams { get; set; } = false;

        /// <summary>
        /// <b>Lossless.</b> Reduce embedded font programs to the glyphs the document actually
        /// draws. Default: false.
        ///
        /// <para>A font is only ever subsetted when the glyph set is <b>provably complete</b> —
        /// every content stream that can reach it decoded and tokenised without incident. Anything
        /// less and the font is left whole, with the reason reported: a subset built from a partial
        /// scan drops glyphs the document really shows, which is a worse defect than the size it
        /// saves.</para>
        /// </summary>
        public bool SubsetEmbeddedFonts { get; set; } = false;

        /// <summary>
        /// <b>Lossless.</b> Never return an output larger than the input: if every enabled pass
        /// together fails to make the file smaller, the input is copied through unchanged.
        /// Default: false.
        ///
        /// <para>Off by default only to keep <see cref="Default"/> byte-compatible with previous
        /// releases. Every preset turns it on, and new callers should.</para>
        /// </summary>
        public bool NeverGrow { get; set; } = false;

        /// <summary>
        /// Refuse to optimize a document carrying a digital signature. Default: false, meaning
        /// <b>refuse</b> — set to true to proceed and invalidate the signature.
        ///
        /// <para>Optimization renumbers every object and rewrites the whole file, so a signature's
        /// <c>/ByteRange</c> ends up naming offsets that no longer exist. The document still
        /// <i>looks</i> signed to a viewer while verifying as corrupt, which is the one outcome a
        /// reader must not be given silently. This matches <c>PdfXConverter</c> and
        /// <c>PdfEncryptedRoundTrip</c>, which take the same position.</para>
        /// </summary>
        public bool AllowSignatureInvalidation { get; set; } = false;

        /// <summary>
        /// Ceiling on the decoded size of any single stream the optimizer inflates, in bytes.
        /// Default: 64 MiB. A stream over the ceiling is left exactly as it was and the refusal is
        /// reported; it is never partially processed.
        /// </summary>
        public long MaxStreamDecodeBytes { get; set; } = 64L * 1024 * 1024;

        /// <summary>
        /// Ceiling on the number of indirect objects the optimizer will load. Default: 2 000 000.
        /// A document over the ceiling is refused rather than partially optimized, because pruning
        /// a document that was only partly read would delete objects that are in fact referenced.
        /// </summary>
        public int MaxObjectCount { get; set; } = 2_000_000;

        /// <summary>
        /// Image optimization settings. When <see cref="ImageOptimizationOptions.Enabled"/> is true,
        /// images in the PDF are re-encoded with the specified format, quality, and resolution.
        /// </summary>
        public ImageOptimizationOptions ImageOptimization { get; set; } = new ImageOptimizationOptions();

        /// <summary>
        /// The historical default configuration: Flate uncompressed streams, drop duplicates, prune
        /// unreachable objects. Kept byte-compatible for existing callers — prefer
        /// <see cref="Lossless"/>, which is what most callers actually want.
        /// </summary>
        public static OptimizationOptions Default => new OptimizationOptions();

        /// <summary>
        /// Every saving that cannot change what a renderer or reader observes.
        ///
        /// <para>Deliberately excluded even though they are individually safe:
        /// <see cref="RemoveThumbnails"/> (removes a producer's own rendering) and every
        /// image setting (re-encoding is lossy).</para>
        /// </summary>
        public static OptimizationOptions Lossless => new OptimizationOptions
        {
            CompressStreams = true,
#if NETSTANDARD2_0
            // CompressionLevel.SmallestSize is .NET 6+. netstandard2.0 tops out at Optimal, so a
            // .NET Framework host's "lossless" optimisation produces a slightly LARGER file than
            // .NET 10's — deflate level 6 rather than 9. A size difference, not a semantic one:
            // the same streams are compressed, losslessly, and every reader accepts both.
            //
            // Deliberately not "use Optimal on both": that would take compression away from every
            // existing .NET 10 caller to make two legs match on a number nobody promised.
            FlateCompressionLevel = CompressionLevel.Optimal,
#else
            FlateCompressionLevel = CompressionLevel.SmallestSize,
#endif
            RecompressExistingStreams = true,
            RemoveDuplicates = true,
            RemoveUnusedObjects = true,
            RemoveUnusedXObjectResources = true,
            MergeImageStrips = true,
            CleanContentStreams = true,
            ConcatenateContentStreams = true,
            ReplaceAsciiFilters = true,
            ReplaceLzwWithFlate = true,
            UsePngPredictorForImages = true,
            ConvertImagesToIndexed = true,
            OptimizeJpegHuffmanTables = true,
            UseCcittForBilevelImages = true,
            UseJbig2ForBilevelImages = true,
            UseObjectStreams = true,
            SubsetEmbeddedFonts = true,
            NeverGrow = true,
        };

        /// <summary>
        /// <see cref="Lossless"/> plus savings that preserve the document's information but not its
        /// exact appearance: thumbnails are dropped and images are re-encoded at a moderate quality.
        /// </summary>
        public static OptimizationOptions Balanced
        {
            get
            {
                var options = Lossless;
                options.RemoveThumbnails = true;
                options.ImageOptimization = WithPresetThresholds(ImageOptimizationOptions.BalancedPreset);
                return options;
            }
        }

        /// <summary>
        /// <see cref="Balanced"/> plus metadata removal and screen-resolution images.
        ///
        /// <para>Still does <b>not</b> enable <see cref="RemoveAnnotations"/> or
        /// <see cref="RemoveDocumentStructure"/>: those destroy form values, comments, links and
        /// accessibility, and no preset will ever turn them on for a caller who did not ask.</para>
        /// </summary>
        public static OptimizationOptions Aggressive
        {
            get
            {
                var options = Balanced;
                options.RemoveMetadata = true;
                options.RemoveXmpMetadata = true;
                options.ImageOptimization = WithPresetThresholds(ImageOptimizationOptions.WebPreset);
                return options;
            }
        }

        /// <summary>
        /// The presets' image thresholds: downsample only above 1.5 × the target resolution, and
        /// leave an image under 64 × 64 alone.
        /// </summary>
        private static ImageOptimizationOptions WithPresetThresholds(ImageOptimizationOptions images)
        {
            images.DownsampleAboveDpi = images.TargetDpi * 3 / 2;
            images.MinImageSide = 64;
            return images;
        }
    }
}
