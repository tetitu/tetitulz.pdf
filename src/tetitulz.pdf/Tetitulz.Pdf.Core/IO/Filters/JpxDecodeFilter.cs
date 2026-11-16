// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.IO.Filters
{
    /// <summary>
    /// Handles the JPXDecode (JPEG 2000) stream filter per ISO 32000 §7.4.9.
    /// A PDF JPXDecode stream contains a self-describing JP2 or J2C bitstream.
    /// JPEG 2000 decoding requires a codec not available in the BCL; this filter
    /// returns the raw bytes unchanged so that a registered image-decode plugin
    /// (a host's own; the library ships none) can decode the image when required.
    /// </summary>
    public sealed class JpxDecodeFilter : StreamFilter
    {
        public override byte[] Decode(byte[] data, PdfDictionary parameters)
        {
            // Return raw JPEG 2000 bitstream unchanged.
            return data;
        }
    }
}
