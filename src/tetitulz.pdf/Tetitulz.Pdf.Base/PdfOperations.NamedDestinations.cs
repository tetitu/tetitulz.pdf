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
using Tetitulz.Pdf.Base.Operations.Structure;
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
        /// <summary>Returns all named destinations in the document.</summary>
        /// <remarks>
        /// Kept as a distinct overload rather than widened with an optional <c>password</c>
        /// parameter: C# bakes optional-argument defaults in at the call site, so widening a shipped
        /// signature is a <b>binary</b> break for an already-compiled consumer. See "Password
        /// overloads are additive, never widened" on <c>PdfOperations.WithDecrypted</c>.
        /// </remarks>
        public static IReadOnlyList<NamedDestination> ListDestinations(Stream input)
            => ListDestinations(input, null);

        /// <summary>
        /// Returns all named destinations in the document, decrypting first when it is encrypted and
        /// a decryptor is registered.
        /// </summary>
        /// <param name="input">The PDF stream to read.</param>
        /// <param name="password">The user password; ignored for an unencrypted document.</param>
        public static IReadOnlyList<NamedDestination> ListDestinations(Stream input, string password)
            => WithDecrypted(input, password, PdfNamedDestinations.ListDestinations);

        /// <summary>Adds or replaces a named destination pointing to a page.</summary>
        public static void SetDestination(Stream input, Stream output,
            string name, int pageIndex,
            double? left = null, double? top = null, double? zoom = null)
            => PdfNamedDestinations.SetDestination(input, output, name, pageIndex, left, top, zoom);

        /// <summary>Removes a named destination by name.</summary>
        public static bool RemoveDestination(Stream input, Stream output, string name)
            => PdfNamedDestinations.RemoveDestination(input, output, name);
    }
}
