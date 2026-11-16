// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;

namespace Tetitulz.Pdf.Base.Encryption
{
    public interface IPdfDecryptor
    {
        void Decrypt(string inputPath, string outputPath, string password);
        void Decrypt(Stream input, Stream output, string password);
    }
}
