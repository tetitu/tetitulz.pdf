// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.IO.Filters
{
    /// <summary>
    /// Handles the /Crypt stream filter (PDF 1.5+).
    ///
    /// When the decode parameters specify /Name /Identity (or no name), the stream
    /// data is not additionally encrypted and is returned unchanged.  For any other
    /// named crypt filter the data is likewise passed through unchanged — actual
    /// per-stream decryption is performed earlier by the document-level decryptor
    /// when it resolves the stream object; by the time the filter pipeline runs, the
    /// bytes are already plaintext.
    /// </summary>
    internal sealed class CryptDecodeFilter : StreamFilter
    {
        public override byte[] Decode(byte[] data, PdfDictionary parameters)
        {
            // The /Crypt filter entry in /DecodeParms may carry a /Name key.
            // /Identity (or absent) means "no additional encryption" — pass through.
            // Any other named crypt filter was applied at the object-resolution level;
            // by the filter-pipeline stage the bytes are already decrypted.
            return data;
        }
    }
}
