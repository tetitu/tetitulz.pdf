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
        /// <summary>Renders a rectangular tile of a PDF page from a file.</summary>
        public static byte[] RenderPageTile(string inputPath, int pageIndex,
            float clipX, float clipY, float clipW, float clipH, int dpi = DefaultRenderDpi,
            Rendering.PdfRenderOptions renderOptions = null)
            => Rendering.PdfPageRenderer.RenderPageTile(inputPath, pageIndex, clipX, clipY, clipW, clipH, dpi, renderOptions);

        /// <summary>Renders a rectangular tile of a PDF page from a stream.</summary>
        public static byte[] RenderPageTile(Stream input, int pageIndex,
            float clipX, float clipY, float clipW, float clipH, int dpi = DefaultRenderDpi,
            Rendering.PdfRenderOptions renderOptions = null)
            => Rendering.PdfPageRenderer.RenderPageTile(input, pageIndex, clipX, clipY, clipW, clipH, dpi, renderOptions);

        /// <summary>Renders a rectangular tile of a PDF page from a file as JPEG.</summary>
        public static byte[] RenderPageTileToJpg(string inputPath, int pageIndex,
            float clipX, float clipY, float clipW, float clipH,
            int dpi = DefaultRenderDpi, int quality = Rendering.PdfPageRenderer.DefaultJpegQuality,
            Rendering.PdfRenderOptions renderOptions = null)
            => Rendering.PdfPageRenderer.RenderPageTileAsJpg(inputPath, pageIndex,
                clipX, clipY, clipW, clipH, dpi, quality, renderOptions);

        /// <summary>Renders a rectangular tile of a PDF page from a stream as JPEG.</summary>
        public static byte[] RenderPageTileToJpg(Stream input, int pageIndex,
            float clipX, float clipY, float clipW, float clipH,
            int dpi = DefaultRenderDpi, int quality = Rendering.PdfPageRenderer.DefaultJpegQuality,
            Rendering.PdfRenderOptions renderOptions = null)
            => Rendering.PdfPageRenderer.RenderPageTileAsJpg(input, pageIndex,
                clipX, clipY, clipW, clipH, dpi, quality, renderOptions);
    }
}
