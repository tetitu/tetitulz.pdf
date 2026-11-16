// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Base.Layout;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Barcodes;
using Tetitulz.Pdf.Base.Scripting;
using Tetitulz.Pdf.Base.Signing;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base
{
    public static partial class PdfOperations
    {
        /// <summary>Generates a complete PDF file containing a barcode.</summary>
        public static byte[] GenerateBarcodePdf(string data, BarcodeType type,
            BarcodeOptions options = null)
            => PdfBarcodeWriter.GeneratePdf(data, type, options);

        /// <summary>Generates a PDF content stream snippet for a barcode at (x, y).</summary>
        public static string GenerateBarcodeContentStream(string data, BarcodeType type,
            float x, float y, BarcodeOptions options = null)
            => PdfBarcodeWriter.GenerateContentStream(data, type, x, y, options);
    }
}
