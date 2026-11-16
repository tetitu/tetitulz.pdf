// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core;

namespace Tetitulz.Pdf.DocxToPdf
{
    public class DocxToPdfException : PdfException
    {
        public DocxToPdfException(string message) : base(message) { }
        public DocxToPdfException(string message, Exception innerException)
            : base(message, innerException) { }
    }
}
