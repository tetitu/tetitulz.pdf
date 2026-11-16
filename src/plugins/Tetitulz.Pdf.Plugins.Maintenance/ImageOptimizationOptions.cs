// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.IO.Compression;

namespace Tetitulz.Pdf.Plugins.Maintenance
{
    /// <summary>
    /// Options for optimizing images within a PDF.
    /// </summary>
    public sealed class ImageOptimizationOptions
    {
        /// <summary>Whether to optimize images. Default: false.</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>Target image format. Default: Original (keep existing format).</summary>
        public ImageFormat TargetFormat { get; set; } = ImageFormat.Original;

        /// <summary>
        /// With <see cref="TargetFormat"/> <see cref="ImageFormat.Jpeg2000"/>, an image whose
        /// transparency is a plain <c>/SMask</c> is re-encoded with that mask as the JPEG 2000
        /// file's own opacity channel (<c>/SMaskInData 1</c>, ISO 32000-2 §7.4.9) instead of being
        /// kept lossless. Default: false.
        /// </summary>
        /// <remarks>
        /// <para>Off, an image with transparency is never re-encoded lossily — a JPEG 2000 target
        /// falls back to Flate for it, because a separate lossy re-encode of image and mask would
        /// misregister the two. Folding the mask into the codestream removes that objection: one
        /// file, one geometry, the opacity channel beside the colour.</para>
        /// <para>Off by default because <c>/SMaskInData</c> is a PDF 1.5 feature a viewer may not
        /// honour, where a separate <c>/SMask</c> is read by all. A mask carrying <c>/Matte</c>
        /// (premultiplied colour, §11.6.5.3), an image with a colour-key or stencil <c>/Mask</c>, and
        /// one already declaring <c>/SMaskInData</c> are not folded, and each is reported.</para>
        /// </remarks>
        public bool FoldSoftMaskIntoJpeg2000 { get; set; }

        /// <summary>
        /// JPEG quality (1-100). Lower values produce smaller files.
        /// Only applies when TargetFormat is Jpeg or WebP. Default: 75.
        /// </summary>
        public int Quality { get; set; } = 75;

        /// <summary>
        /// Target DPI for image resolution downscaling.
        /// Set to 0 to keep original resolution. Default: 0 (no change).
        /// </summary>
        public int TargetDpi { get; set; } = 0;

        /// <summary>
        /// The effective resolution, as painted, above which an image is downsampled to
        /// <see cref="TargetDpi"/>. Default: 0 — anything above <see cref="TargetDpi"/> is
        /// downsampled, as a value at or below <see cref="TargetDpi"/> also means.
        /// </summary>
        /// <remarks>
        /// A threshold above the target leaves an image drawn a little finer than the target
        /// alone: resampling 210 ppi to 200 costs a generation of quality for almost no bytes.
        /// The <see cref="OptimizationOptions.Balanced"/> and
        /// <see cref="OptimizationOptions.Aggressive"/> presets set it to 1.5 × the target.
        /// </remarks>
        public int DownsampleAboveDpi { get; set; } = 0;

        /// <summary>
        /// An image whose width and height are both below this many pixels is left as it is.
        /// Default: 0 (off).
        /// </summary>
        /// <remarks>
        /// A small swatch gains little from a lossy re-encode and loses much: a 32 × 32 image is
        /// sixteen 8 × 8 JPEG blocks, every one an edge. The <see cref="OptimizationOptions.Balanced"/>
        /// and <see cref="OptimizationOptions.Aggressive"/> presets set it to 64.
        /// </remarks>
        public int MinImageSide { get; set; } = 0;

        /// <summary>
        /// True when an image painted at <paramref name="effectiveDpi"/> is to be downsampled:
        /// above <see cref="TargetDpi"/>, and above <see cref="DownsampleAboveDpi"/> when that is
        /// the higher. The one decision, for the resample and the already-compressed skip alike.
        /// </summary>
        internal bool DownsamplesAt(double effectiveDpi)
            => TargetDpi > 0 && effectiveDpi > System.Math.Max(TargetDpi, DownsampleAboveDpi);

        /// <summary>True when <see cref="MinImageSide"/> leaves a <paramref name="width"/> ×
        /// <paramref name="height"/> image as it is.</summary>
        internal bool IsBelowMinimumSide(int width, int height)
            => MinImageSide > 0 && width < MinImageSide && height < MinImageSide;

        /// <summary>
        /// Maximum image width in pixels. Set to 0 for no limit. Default: 0.
        /// </summary>
        public int MaxWidth { get; set; } = 0;

        /// <summary>
        /// Maximum image height in pixels. Set to 0 for no limit. Default: 0.
        /// </summary>
        public int MaxHeight { get; set; } = 0;

        /// <summary>
        /// Deflate compression level applied when encoding lossless images. Default: Optimal.
        /// </summary>
        public CompressionLevel PngCompressionLevel { get; set; } = CompressionLevel.Optimal;

        /// <summary>Whether to convert color images to grayscale. Default: false.</summary>
        public bool ConvertToGrayscale { get; set; } = false;

        /// <summary>
        /// Minimum image size in bytes to be considered for optimization. Default: 1024 (1 KB).
        /// </summary>
        public int MinImageSizeBytes { get; set; } = 1024;

        /// <summary>
        /// When true, skip re-encoding JPEG images that appear already well-compressed.
        /// Default: true.
        /// </summary>
        public bool SkipAlreadyOptimized { get; set; } = true;

        /// <summary>
        /// When true, monochrome images whose pixel data can be decoded are converted to
        /// 8-bit grayscale and re-encoded as FlateDecode. Default: false.
        /// </summary>
        public bool DownsampleMonochromeImages { get; set; } = false;

        /// <summary>Default image optimization options (disabled).</summary>
        public static ImageOptimizationOptions Default => new ImageOptimizationOptions();

        /// <summary>Preset: Aggressive compression for web/screen viewing.</summary>
        public static ImageOptimizationOptions WebPreset => new ImageOptimizationOptions
        {
            Enabled = true,
            TargetFormat = ImageFormat.Jpeg,
            Quality = 60,
            TargetDpi = 150,
            MaxWidth = 1920
        };

        /// <summary>Preset: Balanced compression for general use.</summary>
        public static ImageOptimizationOptions BalancedPreset => new ImageOptimizationOptions
        {
            Enabled = true,
            TargetFormat = ImageFormat.Jpeg,
            Quality = 75,
            TargetDpi = 200
        };

        /// <summary>Preset: High quality for print output.</summary>
        public static ImageOptimizationOptions PrintPreset => new ImageOptimizationOptions
        {
            Enabled = true,
            TargetFormat = ImageFormat.Jpeg,
            Quality = 90,
            TargetDpi = 300
        };
    }
}
