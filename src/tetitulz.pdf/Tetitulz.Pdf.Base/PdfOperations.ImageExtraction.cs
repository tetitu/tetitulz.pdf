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
        /// Extracts all images from a PDF and saves them to the output directory.
        /// </summary>
        public static List<PdfImageInfo> ExtractImages(string pdfPath, string outputDirectory)
        {
            return Images.PdfImageExtractor.ExtractImages(pdfPath, outputDirectory);
        }

        /// <summary>Extracts every image from an <b>encrypted</b> file to a directory.</summary>
        /// <param name="password">User or owner password.</param>
        /// <remarks>
        /// Added even though <c>FacadePasswordCoverageTests</c> already counted this name as
        /// covered by the stream overload: the guard matches per <i>name</i>, and a caller holding a
        /// path and a password would otherwise have to open the file themselves to use one.
        /// </remarks>
        /// <param name="pdfPath">Path to the encrypted PDF file.</param>
        /// <param name="outputDirectory">
        /// Directory the extracted image files are written to. Created if it does not exist.
        /// </param>
        public static List<PdfImageInfo> ExtractImages(
            string pdfPath, string outputDirectory, string password)
        {
            if (pdfPath == null) throw new ArgumentNullException(nameof(pdfPath));
            using FileStream file = File.OpenRead(pdfPath);
            return ExtractImages(file, outputDirectory, password);
        }

        /// <summary>
        /// Extracts all images from a PDF stream and saves them to the output directory.
        /// </summary>
        public static List<PdfImageInfo> ExtractImages(Stream input, string outputDirectory)
        {
            return Images.PdfImageExtractor.ExtractImages(input, outputDirectory);
        }

        /// <summary>Extracts every image from an <b>encrypted</b> document to a directory.</summary>
        /// <param name="password">User or owner password.</param>
        /// <remarks>
        /// A separate overload with a <b>required</b> password: appending <c>password = null</c> to
        /// the signature above is a binary break, and the shipped overload keeps its original call
        /// path so its behaviour is untouched. Where the original ends in optional parameters the
        /// password sits <i>before</i> them — a required parameter cannot follow an optional one.
        /// </remarks>
        /// <param name="input">Stream holding the encrypted PDF.</param>
        /// <param name="outputDirectory">
        /// Directory the extracted image files are written to. Created if it does not exist.
        /// </param>
        public static List<PdfImageInfo> ExtractImages(
            Stream input, string outputDirectory, string password)
            => WithDecrypted(input, password,
                s => Images.PdfImageExtractor.ExtractImages(s, outputDirectory));
    }
}
