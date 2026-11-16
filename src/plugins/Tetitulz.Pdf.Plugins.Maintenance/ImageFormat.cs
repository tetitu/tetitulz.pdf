// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

namespace Tetitulz.Pdf.Plugins.Maintenance
{
    /// <summary>
    /// Target image format for PDF image optimization.
    /// </summary>
    public enum ImageFormat
    {
        /// <summary>
        /// Keep original format (no re-encoding). Only resolution/quality changes applied where possible.
        /// </summary>
        Original = 0,

        /// <summary>
        /// JPEG - lossy compression with adjustable quality. Best for photos.
        /// </summary>
        Jpeg = 1,

        /// <summary>
        /// PNG - lossless compression. Best for graphics with sharp edges.
        /// </summary>
        Png = 2,

        /// <summary>
        /// GIF - limited to 256 colors. Best for simple graphics.
        /// </summary>
        Gif = 3,

        /// <summary>
        /// WebP - modern format with excellent compression. Supports lossy and lossless.
        /// Note: WebP images are stored as JPEG (DCTDecode) in PDF since PDF spec doesn't support WebP natively.
        /// </summary>
        WebP = 4,

        /// <summary>
        /// JPEG 2000 (<c>JPXDecode</c>) — the in-house encoder, lossy at <see cref="ImageOptimizationOptions.Quality"/>
        /// through the 9-7 wavelet. An image that is already <c>JPXDecode</c> stays one under
        /// <see cref="Original"/>.
        /// </summary>
        Jpeg2000 = 5
    }
}
