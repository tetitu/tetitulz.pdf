// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Base.Images
{
    /// <summary>
    /// Information about an extracted image from a PDF.
    /// </summary>
    public sealed class PdfImageInfo
    {
        /// <summary>Page number the image was found on (1-based).</summary>
        public int PageNumber { get; set; }

        /// <summary>Image width in pixels.</summary>
        public int Width { get; set; }

        /// <summary>Image height in pixels.</summary>
        public int Height { get; set; }

        /// <summary>Bits per component (typically 8).</summary>
        public int BitsPerComponent { get; set; }

        /// <summary>Color space name (DeviceRGB, DeviceGray, etc.).</summary>
        public string ColorSpace { get; set; }

        /// <summary>Path to the extracted image file.</summary>
        public string FilePath { get; set; }

        /// <summary>Image format (jpeg, png, etc.).</summary>
        public string Format { get; set; }

        /// <summary>Size of the extracted image in bytes.</summary>
        public long FileSize { get; set; }
    }
}
