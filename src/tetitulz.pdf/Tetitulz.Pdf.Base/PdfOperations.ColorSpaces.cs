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
using Tetitulz.Pdf.Base.Operations.Metadata;
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
        /// <summary>Returns all colour spaces used in the document's page resources and image XObjects.</summary>
        /// <remarks>
        /// Kept as a distinct overload rather than widened with an optional <c>password</c>
        /// parameter: C# bakes optional-argument defaults in at the call site, so widening a shipped
        /// signature is a <b>binary</b> break for an already-compiled consumer. See "Password
        /// overloads are additive, never widened" on <c>PdfOperations.WithDecrypted</c>.
        /// </remarks>
        public static IReadOnlyList<ColorSpaceInfo> GetColorSpaces(Stream input)
            => GetColorSpaces(input, null);

        /// <summary>
        /// Returns all colour spaces used in the document's page resources and image XObjects,
        /// decrypting first when the document is encrypted and a decryptor is registered.
        /// </summary>
        /// <param name="input">The PDF stream to read.</param>
        /// <param name="password">The user password; ignored for an unencrypted document.</param>
        public static IReadOnlyList<ColorSpaceInfo> GetColorSpaces(Stream input, string password)
            => WithDecrypted(input, password, Operations.Metadata.PdfColorSpaceConverter.GetColorSpaces);

        /// <summary>Returns all colour spaces used in the document's page resources and image XObjects.</summary>
        public static IReadOnlyList<ColorSpaceInfo> GetColorSpaces(string pdfPath)
            => Operations.Metadata.PdfColorSpaceConverter.GetColorSpaces(pdfPath);

        /// <summary>Converts all DeviceRGB raster images to DeviceGray (ITU-R BT.601 luminance).</summary>
        /// <returns>Number of images converted.</returns>
        public static int ConvertRgbToGrayscale(Stream input, Stream output)
            => Operations.Metadata.PdfColorSpaceConverter.ConvertRgbToGrayscale(input, output);

        /// <summary>Converts all DeviceRGB raster images to DeviceCMYK.</summary>
        /// <returns>Number of images converted.</returns>
        public static int ConvertRgbToCmyk(Stream input, Stream output)
            => Operations.Metadata.PdfColorSpaceConverter.ConvertRgbToCmyk(input, output);

        /// <summary>
        /// Converts all DeviceRGB raster images to DeviceCMYK with an explicit Gray Component
        /// Replacement / Under Colour Removal factor.
        ///
        /// <para><b>This is the overload to prefer</b>, as
        /// <see cref="Operations.Metadata.PdfColorSpaceConverter"/> says in its own doc comment. The
        /// factor is the whole point of
        /// the conversion being configurable: 0 lays down no black at all and 1 is full GCR, and a
        /// press that has been characterised for one will visibly misprint the other, so a prepress
        /// caller cannot use the default and hope.</para>
        ///
        /// <para>Not a gamut-accurate ICC transform — it is sRGB linearisation, separable CMY, then K
        /// extraction. Images whose <c>/Filter</c> chain needs a codec (<c>/DCTDecode</c>,
        /// <c>/JPXDecode</c>, JBIG2, CCITT) are skipped rather than mangled, and anything discarded
        /// along the way is named on
        /// <see cref="Operations.Metadata.PdfColorSpaceConverter.LastConversionWarning"/> — read it,
        /// because a count alone cannot tell you that an image's colour-key transparency went with
        /// the colour space it was expressed in.</para>
        /// </summary>
        /// <param name="input">Stream holding the source PDF.</param>
        /// <param name="output">Receives the converted PDF.</param>
        /// <param name="gcrFactor">0 = no K channel, 1 = full Gray Component Replacement.</param>
        /// <returns>Number of images converted.</returns>
        public static int ConvertRgbToCmyk(Stream input, Stream output, double gcrFactor)
            => Operations.Metadata.PdfColorSpaceConverter.ConvertRgbToCmyk(input, output, gcrFactor);

        /// <summary>
        /// Converts the document's colour — content, images, shadings, palettes, spot alternates, patterns,
        /// transparency groups — into CMYK through an output profile, for a press workflow. See
        /// <see cref="Operations.Metadata.PdfColorSpaceConverter.ConvertToCmyk"/>.
        /// </summary>
        public static Operations.Metadata.ColourConversionResult ConvertToCmyk(Stream input, Stream output,
            Operations.Metadata.CmykConversionOptions options)
            => Operations.Metadata.PdfColorSpaceConverter.ConvertToCmyk(input, output, options);

        /// <summary>
        /// Converts the document's DeviceCMYK colour into RGB (an RGB output intent's values, sRGB by default),
        /// for PDF/A under an RGB intent. See <see cref="Operations.Metadata.PdfColorSpaceConverter.ConvertToRgb"/>.
        /// </summary>
        public static Operations.Metadata.ColourConversionResult ConvertToRgb(Stream input, Stream output,
            Operations.Metadata.RgbConversionOptions options = null)
            => Operations.Metadata.PdfColorSpaceConverter.ConvertToRgb(input, output, options);
    }
}
