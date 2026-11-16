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
        /// <summary>Returns true if the PDF is a Portfolio (collection).</summary>
        public static bool IsPortfolio(string inputPath)
            => PdfPortfolioManager.IsPortfolio(inputPath);

        /// <summary>Returns true if the PDF stream is a Portfolio (collection).</summary>
        public static bool IsPortfolio(Stream input)
            => IsPortfolio(input, null);

        /// <remarks>
        /// Kept as a distinct overload rather than widened with an optional <c>password</c>
        /// parameter: C# bakes optional-argument defaults in at the call site, so widening a shipped
        /// signature is a <b>binary</b> break for an already-compiled consumer. See "Password
        /// overloads are additive, never widened" on <c>PdfOperations.WithDecrypted</c>.
        /// </remarks>
        /// <param name="input">The PDF stream to read.</param>
        /// <param name="password">The user password; ignored for an unencrypted document.</param>
        public static bool IsPortfolio(Stream input, string password)
            => WithDecrypted(input, password, PdfPortfolioManager.IsPortfolio);

        /// <summary>Lists all items in a PDF Portfolio.</summary>
        public static IReadOnlyList<PortfolioItem> ListPortfolioItems(string inputPath)
            => PdfPortfolioManager.ListItems(inputPath);

        /// <summary>Lists all items in a PDF Portfolio stream.</summary>
        public static IReadOnlyList<PortfolioItem> ListPortfolioItems(Stream input)
            => ListPortfolioItems(input, null);

        /// <remarks>
        /// Kept as a distinct overload rather than widened with an optional <c>password</c>
        /// parameter: C# bakes optional-argument defaults in at the call site, so widening a shipped
        /// signature is a <b>binary</b> break for an already-compiled consumer. See "Password
        /// overloads are additive, never widened" on <c>PdfOperations.WithDecrypted</c>.
        /// </remarks>
        /// <param name="input">The PDF stream to read.</param>
        /// <param name="password">The user password; ignored for an unencrypted document.</param>
        public static IReadOnlyList<PortfolioItem> ListPortfolioItems(Stream input, string password)
            => WithDecrypted(input, password, PdfPortfolioManager.ListItems);
    }
}
