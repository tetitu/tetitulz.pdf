// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;

namespace Tetitulz.Pdf.DocxToPdf
{
    public interface IDocxToPdfConverter
    {
        void Convert(string inputPath, string outputPath, DocxToPdfOptions options = null);
        void Convert(Stream input, Stream output, DocxToPdfOptions options = null);
        byte[] ConvertToBytes(string inputPath, DocxToPdfOptions options = null);
        byte[] ConvertToBytes(byte[] docxData, DocxToPdfOptions options = null);
    }
}
