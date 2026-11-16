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
using Tetitulz.Pdf.Base.Operations.Extraction;
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
        /// Detects vertical-whitespace column zones for a page layout.
        /// Returned zones are ordered left-to-right, each as (X, Y, Width, Height)
        /// in PDF user-space coordinates.
        /// </summary>
        public static IReadOnlyList<(float X, float Y, float Width, float Height)> DetectColumns(
            PdfPageTextLayout layout,
            float minRiverWidth     = 10f,
            float minHeightFraction = 0.5f)
            => Operations.Extraction.PdfLayoutAnalyzer.DetectColumns(layout, minRiverWidth, minHeightFraction);
    }
}
