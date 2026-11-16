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
        /// Reads /UR3 Usage Rights extensions from all signature fields in the PDF file.
        /// Returns one <see cref="Operations.Metadata.UsageRightsEntry"/> per /UR3 SigRef found.
        /// Returns an empty list when no /UR3 signatures are present.
        /// </summary>
        public static IReadOnlyList<Operations.Metadata.UsageRightsEntry> ReadUsageRights(string pdfPath)
            => Operations.Metadata.PdfUsageRightsReader.Read(pdfPath);

        /// <summary>
        /// Reads /UR3 Usage Rights extensions from all signature fields in the PDF stream.
        /// </summary>
        public static IReadOnlyList<Operations.Metadata.UsageRightsEntry> ReadUsageRights(Stream input)
            => ReadUsageRights(input, null);

        /// <remarks>
        /// Kept as a distinct overload rather than widened with an optional <c>password</c>
        /// parameter: C# bakes optional-argument defaults in at the call site, so widening a shipped
        /// signature is a <b>binary</b> break for an already-compiled consumer. See "Password
        /// overloads are additive, never widened" on <c>PdfOperations.WithDecrypted</c>.
        /// </remarks>
        /// <param name="input">The PDF stream to read.</param>
        /// <param name="password">The user password; ignored for an unencrypted document.</param>
        public static IReadOnlyList<Operations.Metadata.UsageRightsEntry> ReadUsageRights(
            Stream input, string password)
            => WithDecrypted(input, password, Operations.Metadata.PdfUsageRightsReader.Read);
    }
}
