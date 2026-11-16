// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base.Operations.Documents;

namespace Tetitulz.Pdf.Base
{
    public static partial class PdfOperations
    {
        /// <summary>
        /// Every transparency construct each page reaches — the pages
        /// <see cref="FlattenTransparency"/> would flatten, and why.
        /// </summary>
        /// <param name="input">The PDF to scan. Not disposed.</param>
        /// <param name="password">The document's password when it is encrypted; null otherwise.</param>
        public static IReadOnlyList<TransparencyFinding> FindTransparency(Stream input, string password = null)
            => WithDecrypted(input, password, PdfTransparencyFlattener.FindTransparency);

        /// <summary>
        /// Writes <paramref name="input"/> with every page that uses transparency replaced by an opaque
        /// image of itself, its text kept as an invisible layer — what PDF/A-1 and PDF/X-1a/X-3 need
        /// before they can be met. See <see cref="PdfTransparencyFlattener"/> for what a flattened page
        /// keeps and what it gives up.
        /// </summary>
        /// <param name="input">The PDF to flatten. Not disposed.</param>
        /// <param name="output">Where the result is written. Not disposed.</param>
        /// <param name="options">Resolution, colour space and text policy; null uses the defaults.</param>
        public static TransparencyFlattenResult FlattenTransparency(Stream input, Stream output,
            TransparencyFlattenOptions options = null)
            => PdfTransparencyFlattener.Flatten(input, output, options);
    }
}
