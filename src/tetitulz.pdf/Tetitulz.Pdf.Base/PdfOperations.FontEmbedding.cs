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
using Tetitulz.Pdf.Base.Operations.Fonts;
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
        /// Embeds a TrueType or OpenType font into all pages of the document and
        /// registers it under the given resource name.
        /// Use <paramref name="resourceName"/> in content streams: <c>/{resourceName} 12 Tf</c>.
        /// A name a page already uses is refused (<see cref="ArgumentException"/>), as is an
        /// encrypted document (<see cref="Core.PdfEncryptedDocumentException"/>).
        /// </summary>
        public static void EmbedFont(Stream input, Stream output, byte[] fontBytes, string resourceName)
            => PdfFontEmbedder.EmbedFont(input, output, fontBytes, resourceName);

        /// <summary>
        /// Parses a TrueType/OpenType font file and returns its metrics (name, ascent, descent,
        /// per-character advance widths, etc.) without modifying any PDF.
        /// </summary>
        public static TrueTypeMetrics GetFontMetrics(byte[] fontBytes)
            => PdfFontEmbedder.GetFontMetrics(fontBytes);

        /// <summary>
        /// Subsets a TrueType font to include only the glyphs needed for the given text.
        /// Returns a smaller TTF byte array suitable for embedding. Not supported for CFF/OpenType fonts.
        /// </summary>
        public static byte[] SubsetFont(byte[] fontBytes, string text)
            => Operations.Fonts.PdfFontSubsetter.Subset(fontBytes, text);

        /// <summary>
        /// Subsets a TrueType font to include only the glyphs needed for the given characters.
        /// Returns a smaller TTF byte array suitable for embedding.
        /// </summary>
        public static byte[] SubsetFont(byte[] fontBytes, IEnumerable<char> characters)
            => Operations.Fonts.PdfFontSubsetter.Subset(fontBytes, characters);

        /// <summary>
        /// Extracts every embedded font program in the PDF, <b>as files</b> a font tool can open.
        /// </summary>
        /// <param name="input">The PDF to read. Not disposed.</param>
        /// <param name="password">User or owner password, or null.</param>
        /// <remarks>
        /// <para>Not the same as the stream's decoded bytes: a <c>/FontFile</c> needs its PFB sector
        /// headers rebuilt and a bare CFF needs an OpenType envelope, or the result is a file nothing
        /// opens.</para>
        /// <para><b>Check <c>IsSubset</c> and <c>License</c> before doing anything with the
        /// bytes.</b> A subset is not the foundry's font, and extraction is the operation most likely
        /// to redistribute one. Read
        /// <c>Operations.Fonts.PdfFontExporter.LastError</c> to tell "no embedded fonts" from "some
        /// would not decode".</para>
        /// </remarks>
        public static IReadOnlyList<Operations.Fonts.PdfExtractedFont> ExtractFonts(
            Stream input, string password = null)
            => WithDecrypted(input, password, Operations.Fonts.PdfFontExporter.ExtractFonts);
    }
}
