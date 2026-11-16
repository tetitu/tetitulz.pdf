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
        /// <summary>Produces a 2-up PDF (2 source pages per landscape sheet).</summary>
        public static void Create2Up(Stream input, Stream output, NupOptions options = null)
            => PdfNup.Create2Up(input, output, options);

        /// <summary>Produces a 4-up PDF (2ֳ—2 grid per sheet).</summary>
        public static void Create4Up(Stream input, Stream output, NupOptions options = null)
            => PdfNup.Create4Up(input, output, options);

        /// <summary>Produces a 6-up PDF (3ֳ—2 grid per sheet).</summary>
        public static void Create6Up(Stream input, Stream output, NupOptions options = null)
            => PdfNup.Create6Up(input, output, options);

        /// <summary>Produces a 9-up PDF (3ֳ—3 grid per sheet).</summary>
        public static void Create9Up(Stream input, Stream output, NupOptions options = null)
            => PdfNup.Create9Up(input, output, options);

        /// <summary>Produces an N-up PDF with a custom column/row grid.</summary>
        public static void CreateNup(Stream input, Stream output,
            int cols, int rows, NupOptions options = null)
            => PdfNup.CreateNup(input, output, cols, rows, options);
    }
}
