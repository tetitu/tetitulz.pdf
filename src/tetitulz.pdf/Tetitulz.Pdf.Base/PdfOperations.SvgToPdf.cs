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
        /// <summary>Converts an SVG <b>file</b> to a PDF byte array.</summary>
        /// <param name="svgPath">Path to an SVG file on disk.</param>
        /// <remarks>
        /// <para><b>This overload opens the file.</b> <c>SvgToPdfConverter</c> has two
        /// <c>string</c>-shaped entry points — <c>Convert(Stream)</c> and
        /// <c>Convert(string svgXml)</c>, the latter taking SVG <i>markup</i> — so forwarding
        /// <paramref name="svgPath"/> to <c>Convert(string)</c> would hand the parser the path
        /// itself, and fail with <c>XmlException: Data at the root level is invalid.</c> — a message
        /// that blames the caller's SVG for a file never read.</para>
        /// <para>Nothing caught it because the one test covering this facade passed markup, not a
        /// path: it asserted the facade matches the direct converter, which was true, and could not
        /// see that the facade was forwarding to the wrong overload of it.</para>
        /// <para><b>Callers passing SVG markup to this overload must switch to
        /// <see cref="SvgToPdfConverter.Convert(string, SvgConversionOptions)"/></b>, which is
        /// public and is what that string was reaching anyway. A <c>string</c> beside a
        /// <c>Stream</c> means a path everywhere else in this facade
        /// (<c>RenderPageToPng</c>, <c>GetRenderSize</c>, …) and now means one here too.</para>
        /// </remarks>
        /// <param name="options">
        /// Conversion options, or <see langword="null"/> for the defaults.
        /// </param>
        public static byte[] ConvertSvgToPdf(string svgPath, SvgConversionOptions options = null)
        {
            if (svgPath == null) throw new ArgumentNullException(nameof(svgPath));
            using var fs = File.OpenRead(svgPath);
            return SvgToPdfConverter.Convert(fs, options);
        }

        /// <summary>Converts an SVG stream to a PDF byte array.</summary>
        public static byte[] ConvertSvgToPdf(Stream svgStream, SvgConversionOptions options = null)
            => SvgToPdfConverter.Convert(svgStream, options);
    }
}
