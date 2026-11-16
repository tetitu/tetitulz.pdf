// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;

namespace Tetitulz.Pdf.DocxToPdf
{
    public sealed class DocxToPdfConverterService : IDocxToPdfConverter
    {
        public void Convert(string inputPath, string outputPath, DocxToPdfOptions options = null)
            => DocxToPdfConverter.Convert(inputPath, outputPath, options);

        public void Convert(Stream input, Stream output, DocxToPdfOptions options = null)
            => DocxToPdfConverter.Convert(input, output, options);

        public byte[] ConvertToBytes(string inputPath, DocxToPdfOptions options = null)
            => DocxToPdfConverter.ConvertToBytes(inputPath, options);

        public byte[] ConvertToBytes(byte[] docxData, DocxToPdfOptions options = null)
            => DocxToPdfConverter.ConvertToBytes(docxData, options);
    }
}
