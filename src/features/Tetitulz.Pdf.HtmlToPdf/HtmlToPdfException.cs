// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core;

namespace Tetitulz.Pdf.HtmlToPdf
{
    public class HtmlToPdfException : PdfException
    {
        public HtmlToPdfException(string message) : base(message) { }
        public HtmlToPdfException(string message, Exception innerException)
            : base(message, innerException) { }
    }
}
