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
        /// <summary>Lists all 3D annotations in a PDF file.</summary>
        public static IReadOnlyList<Pdf3DInfo> List3DAnnotations(string inputPath)
            => Pdf3DExtractor.List3DAnnotations(inputPath);

        /// <summary>Lists all 3D annotations in a PDF stream.</summary>
        /// <remarks>
        /// Kept as a distinct overload rather than widened with an optional <c>password</c>
        /// parameter: C# bakes optional-argument defaults in at the call site, so widening a shipped
        /// signature is a <b>binary</b> break for an already-compiled consumer. See "Password
        /// overloads are additive, never widened" on <c>PdfOperations.WithDecrypted</c>.
        /// </remarks>
        public static IReadOnlyList<Pdf3DInfo> List3DAnnotations(Stream input)
            => List3DAnnotations(input, null);

        /// <summary>
        /// Lists all 3D annotations in a PDF stream, decrypting first when the document is encrypted
        /// and a decryptor is registered.
        /// </summary>
        /// <param name="input">The PDF stream to read.</param>
        /// <param name="password">The user password; ignored for an unencrypted document.</param>
        public static IReadOnlyList<Pdf3DInfo> List3DAnnotations(Stream input, string password)
            => WithDecrypted(input, password, Pdf3DExtractor.List3DAnnotations);

        /// <summary>Extracts the raw U3D/PRC stream bytes of a 3D annotation.</summary>
        public static byte[] Extract3DStream(string inputPath, int annotationIndex)
            => Pdf3DExtractor.Extract3DStream(inputPath, annotationIndex);

        /// <summary>Extracts the raw U3D/PRC stream bytes of a 3D annotation.</summary>
        /// <remarks>
        /// Kept as a distinct overload rather than widened with an optional <c>password</c>
        /// parameter — see the binary-compatibility note on
        /// <see cref="List3DAnnotations(Stream)"/>.
        /// </remarks>
        public static byte[] Extract3DStream(Stream input, int annotationIndex)
            => Extract3DStream(input, annotationIndex, null);

        /// <summary>
        /// Extracts the raw U3D/PRC stream bytes of a 3D annotation, decrypting first when the
        /// document is encrypted and a decryptor is registered.
        /// </summary>
        /// <param name="input">The PDF stream to read.</param>
        /// <param name="annotationIndex">Index into the document's 3D annotations.</param>
        /// <param name="password">The user password; ignored for an unencrypted document.</param>
        public static byte[] Extract3DStream(Stream input, int annotationIndex, string password)
            => WithDecrypted(input, password, s => Pdf3DExtractor.Extract3DStream(s, annotationIndex));
    }
}
