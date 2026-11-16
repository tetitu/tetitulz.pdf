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
using Tetitulz.Pdf.Base.Operations.Documents;
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
        /// <summary>
        /// Compares two PDF files text-wise and returns a diff result with inserted,
        /// deleted, and unchanged lines per page.
        /// </summary>
        public static PdfDiffResult ComparePdfText(string leftPath, string rightPath)
            => PdfComparer.CompareText(leftPath, rightPath);

        /// <summary>
        /// Compares two PDF streams text-wise and returns a diff result.
        /// </summary>
        public static PdfDiffResult ComparePdfText(Stream left, Stream right)
            => PdfComparer.CompareText(left, right);
    }
}
