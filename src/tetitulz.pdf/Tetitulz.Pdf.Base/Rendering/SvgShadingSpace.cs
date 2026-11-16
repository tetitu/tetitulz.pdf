// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Functions;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Util;
using HashSetInt = System.Collections.Generic.HashSet<int>;
using SKPath = SkiaSharp.SKPath;
using SKColor = SkiaSharp.SKColor;
using SKFont = SkiaSharp.SKFont;
using SKTypeface = SkiaSharp.SKTypeface;
using SKPoint = SkiaSharp.SKPoint;
using SKPathVerb = SkiaSharp.SKPathVerb;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// The colour space of a shading, resolved once so each vertex evaluation does not re-read
    /// the dictionary. Was nested two levels deep, inside SvgContentWriter.
    /// </summary>
    internal sealed class SvgShadingSpace
    {
        private readonly string _family;
        private readonly PdfDictionary _tintFn;
        private readonly PdfDocument _doc;
        /// <summary>
        /// The colour-space object whose ICC profile (if any) applies to the components
        /// finally converted — the space itself when there is no tint transform, the
        /// <c>alternateSpace</c> when there is. Mirrors <c>ShadingSpace._spaceObj</c>.
        /// </summary>
        private readonly PdfObject _spaceObj;

        /// <summary>
        /// The rendering intent in force when the shading was invoked, captured because a shading
        /// samples its colour long after the graphics state that chose the intent is out of reach
        /// here. §8.7.4.3 evaluates a shading under the state current at the <c>sh</c> or pattern
        /// paint, so the intent belongs to the construction, not to the sample.
        /// </summary>
        private readonly IccRenderingIntent _intent;

        private bool _tintGaveUp;
        private readonly SKColor[] _palette;
        private readonly int _paletteBaseComponents;

        public SvgShadingSpace(string family, PdfDictionary tintFn, PdfDocument doc,
                               PdfObject spaceObj = null,
                               IccRenderingIntent intent = IccRenderingIntents.Default)
        {
            _family = family; _tintFn = tintFn; _doc = doc;
            _spaceObj = spaceObj; _intent = intent;
        }

        /// <summary>As the raster ShadingSpace: an <c>/Indexed</c> palette for colours that arrive as an index.</summary>
        public SvgShadingSpace(string family, PdfDictionary tintFn, PdfDocument doc, PdfObject spaceObj,
                               IccRenderingIntent intent, SKColor[] palette, int paletteBaseComponents)
            : this(family, tintFn, doc, spaceObj, intent)
        {
            _palette = palette; _paletteBaseComponents = paletteBaseComponents;
        }

        public string ToCss(float[] comps)
        {
            if (IndexedPalette.ShadingColor(_palette, _paletteBaseComponents, comps, out SKColor indexed))
                return PdfColor.ToCssRgb(indexed);
            if (_tintFn != null && !_tintGaveUp && comps.Length > 0)
            {
                try
                {
                    float[] alt = PdfFunctionEvaluator.Evaluate(_tintFn, comps, _doc.ResolveObject);
                    if (alt != null && alt.Length > 0) comps = alt;
                }
                catch (Exception ex)
                {
                    // Once only: a gradient samples its colour SvgGradientStopCount times.
                    _tintGaveUp = true;
                    RenderDiagnostics.Report(RenderDiagnosticKind.MeshShaderFailed,
                        "SVG shading tint transform failed; remaining stops use raw components", ex);
                }
            }

            // One entry point with the raster ShadingSpace: ResolveAlternateColor answers an
            // [/ICCBased …] space and the CIE spaces' parameter dictionaries alike, resolving their
            // entries through the document. This answered CalGray, CalRGB and Lab itself, with no
            // document to resolve against, so an indirect /Gamma, /Matrix or /WhitePoint was read
            // here as absent while the raster path honoured it.
            return PdfColor.ToCssRgb(
                IccColorTransform.ResolveAlternateColor(_doc, _spaceObj, comps, _family, _intent));
        }
    }
}
