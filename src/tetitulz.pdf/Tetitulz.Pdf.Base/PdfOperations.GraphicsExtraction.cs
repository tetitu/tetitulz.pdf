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
        /// Extracts horizontal rulings, vertical rulings, and image bounding boxes
        /// from every page of a PDF file by parsing the graphics content stream.
        /// </summary>
        public static IReadOnlyList<PdfPageGraphics> ExtractGraphics(string inputPath)
            => PdfGraphicsExtractor.Extract(inputPath);

        /// <summary>
        /// Extracts horizontal rulings, vertical rulings, and image bounding boxes
        /// from every page of a PDF stream.
        /// </summary>
        public static IReadOnlyList<PdfPageGraphics> ExtractGraphics(Stream input)
            => ExtractGraphics(input, (string)null);

        /// <remarks>
        /// Kept as a distinct overload rather than widened with an optional <c>password</c>
        /// parameter: C# bakes optional-argument defaults in at the call site, so widening a shipped
        /// signature is a <b>binary</b> break for an already-compiled consumer. See "Password
        /// overloads are additive, never widened" on <c>PdfOperations.WithDecrypted</c>.
        /// </remarks>
        /// <param name="input">The PDF stream to read.</param>
        /// <param name="password">The user password; ignored for an unencrypted document.</param>
        public static IReadOnlyList<PdfPageGraphics> ExtractGraphics(Stream input, string password)
            => WithDecrypted(input, password, PdfGraphicsExtractor.Extract);

        /// <summary>
        /// Extracts graphics elements from a single page (zero-based index).
        /// </summary>
        public static PdfPageGraphics ExtractGraphics(Stream input, int pageIndex)
            => ExtractGraphics(input, pageIndex, null);

        /// <remarks>
        /// Kept as a distinct overload rather than widened with an optional <c>password</c>
        /// parameter: C# bakes optional-argument defaults in at the call site, so widening a shipped
        /// signature is a <b>binary</b> break for an already-compiled consumer. See "Password
        /// overloads are additive, never widened" on <c>PdfOperations.WithDecrypted</c>.
        /// </remarks>
        /// <param name="input">The PDF stream to read.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="password">The user password; ignored for an unencrypted document.</param>
        public static PdfPageGraphics ExtractGraphics(Stream input, int pageIndex, string password)
            => WithDecrypted(input, password, s => PdfGraphicsExtractor.ExtractPage(s, pageIndex));
    }
}
