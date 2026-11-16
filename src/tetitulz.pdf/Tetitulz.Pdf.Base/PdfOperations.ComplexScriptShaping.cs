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
        /// Applies script-specific positional shaping to a string.
        /// Arabic text is mapped to presentation forms (U+FE70–U+FEFF).
        /// Devanagari pre-base matras are reordered.
        /// Other scripts are returned unchanged.
        /// </summary>
        public static string ShapeText(string text)
            => Tetitulz.Pdf.Core.Text.OpenTypeShaper.Shape(text);

        /// <summary>
        /// Classifies a Unicode code point into its script block.
        /// </summary>
        public static Tetitulz.Pdf.Core.Text.Script ClassifyScript(int codePoint)
            => Tetitulz.Pdf.Core.Text.OpenTypeShaper.ClassifyScript(codePoint);

        /// <summary>
        /// Returns true if the script is right-to-left (Arabic, Hebrew).
        /// </summary>
        public static bool IsRightToLeft(Tetitulz.Pdf.Core.Text.Script script)
            => Tetitulz.Pdf.Core.Text.OpenTypeShaper.IsRightToLeft(script);
    }
}
