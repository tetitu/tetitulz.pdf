// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Functions;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Rendering
{
    internal sealed partial class ContentStreamInterpreter
    {
        /// <summary>
        /// <c>BDC</c>: the one decision rendering makes about marked content — whether an
        /// <c>/OC</c> section is hidden (§8.11.3.2). <c>BMC</c> and <c>EMC</c> outside a hidden
        /// section need nothing; the skip depth handles nesting inside one.
        /// </summary>
        /// <remarks>
        /// No marked-content stack is kept here: nothing in rendering reads one. The extractors keep
        /// their own, where the MCID is the point.
        /// </remarks>
        private void OpBdc(ContentStreamOperation op)
        {
            if (op.Operands.Length < 2 || (op.Operands[0] as PdfName)?.Value != "OC") return;

            // The RAW entry, not a resolved dictionary: for /OC the entry IS the OCG, and an OCG
            // is identified by object number, which resolving would discard. A name refers to
            // the /Properties resource.
            PdfObject propsEntry = op.Operands[1] is PdfName propsName
                ? ResolveResourceEntry(PdfName.Properties, propsName.Value)
                : op.Operands[1];

            // Paint nothing until the matching EMC; the state operators still run (Dispatch).
            if (IsOcEntryHidden(propsEntry, HiddenOcgs, _doc))
            {
                _ocSkipDepth = 1;
                _ctx.HiddenContentDepth++;
            }
        }

        // ---- Offscreen rendering helpers ----
    }
}
