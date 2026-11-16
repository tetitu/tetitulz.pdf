// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Base.Encryption;

namespace Tetitulz.Pdf.Encryption
{
    public sealed class PdfDecryptorService : IPdfDecryptor
    {
        public void Decrypt(string inputPath, string outputPath, string password)
            => PdfDecryptor.Decrypt(inputPath, outputPath, password);

        public void Decrypt(Stream input, Stream output, string password)
            => PdfDecryptor.Decrypt(input, output, password);
    }
}
