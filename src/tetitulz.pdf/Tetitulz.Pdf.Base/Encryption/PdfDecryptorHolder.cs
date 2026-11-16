// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Base.Encryption
{
    /// <summary>
    /// Static registry for the active <see cref="IPdfDecryptor"/> implementation.
    /// Set by calling <c>PdfEncryptionOperations.Register()</c> at startup when
    /// the Encryption assembly is loaded. When null, encrypted PDFs cannot be
    /// transparently decrypted by the renderer.
    /// </summary>
    public static class PdfDecryptorHolder
    {
        public static IPdfDecryptor Current { get; set; }
    }
}
