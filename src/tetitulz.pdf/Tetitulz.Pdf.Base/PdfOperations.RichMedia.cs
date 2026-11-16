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
        /// <summary>Lists all rich media annotations in a PDF file.</summary>
        /// <param name="inputPath">Path to the PDF file.</param>
        public static IReadOnlyList<RichMediaInfo> ListRichMedia(string inputPath)
            => PdfRichMediaExtractor.ListRichMedia(inputPath);

        /// <summary>Lists rich-media annotations in an <b>encrypted</b> file.</summary>
        /// <param name="password">User or owner password.</param>
        /// <param name="inputPath">Path to the encrypted PDF file.</param>
        public static IReadOnlyList<RichMediaInfo> ListRichMedia(string inputPath, string password)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using FileStream file = File.OpenRead(inputPath);
            return ListRichMedia(file, password);
        }

        /// <summary>Lists all rich media annotations in a PDF stream.</summary>
        public static IReadOnlyList<RichMediaInfo> ListRichMedia(Stream input)
            => PdfRichMediaExtractor.ListRichMedia(input);

        /// <summary>Lists rich-media annotations in an <b>encrypted</b> document.</summary>
        /// <param name="password">User or owner password.</param>
        /// <remarks>
        /// A separate overload with a <b>required</b> password: appending <c>password = null</c> to
        /// the signature above is a binary break, and the shipped overload keeps its original call
        /// path so its behaviour is untouched. Where the original ends in optional parameters the
        /// password sits <i>before</i> them — a required parameter cannot follow an optional one,
        /// and that placement is also what keeps the pair unambiguous at every arity.
        /// </remarks>
        /// <param name="input">Stream holding the encrypted PDF.</param>
        public static IReadOnlyList<RichMediaInfo> ListRichMedia(Stream input, string password)
            => WithDecrypted(input, password, PdfRichMediaExtractor.ListRichMedia);

        /// <summary>Extracts the raw media bytes of a rich media annotation by index.</summary>
        public static byte[] ExtractRichMedia(string inputPath, int annotationIndex)
            => PdfRichMediaExtractor.ExtractMedia(inputPath, annotationIndex);

        /// <summary>Extracts a rich-media payload from an <b>encrypted</b> document.</summary>
        /// <param name="password">User or owner password.</param>
        /// <remarks>
        /// A separate overload with a <b>required</b> password: appending <c>password = null</c> to
        /// the signature above is a binary break, and the shipped overload keeps its original call
        /// path so its behaviour is untouched. Where the original ends in optional parameters the
        /// password sits <i>before</i> them — a required parameter cannot follow an optional one,
        /// and that placement is also what keeps the pair unambiguous at every arity.
        /// </remarks>
        /// <param name="input">Stream holding the encrypted PDF.</param>
        /// <param name="annotationIndex">
        /// Zero-based index into the list <see cref="ListRichMedia(Stream,string)"/> returns.
        /// </param>
        public static byte[] ExtractRichMedia(Stream input, int annotationIndex, string password)
            => WithDecrypted(input, password,
                s => PdfRichMediaExtractor.ExtractMedia(s, annotationIndex));

        /// <summary>Extracts a rich-media payload from an <b>encrypted</b> file.</summary>
        /// <param name="password">User or owner password.</param>
        /// <param name="inputPath">Path to the encrypted PDF file.</param>
        /// <param name="annotationIndex">
        /// Zero-based index into the list <see cref="ListRichMedia(string,string)"/> returns.
        /// </param>
        public static byte[] ExtractRichMedia(string inputPath, int annotationIndex, string password)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using FileStream file = File.OpenRead(inputPath);
            return ExtractRichMedia(file, annotationIndex, password);
        }

        /// <summary>Extracts the raw media bytes of a rich media annotation by index.</summary>
        public static byte[] ExtractRichMedia(Stream input, int annotationIndex)
            => PdfRichMediaExtractor.ExtractMedia(input, annotationIndex);
    }
}
