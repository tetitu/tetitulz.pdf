// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// A colour space named from the current resources (§8.6.3) — the <c>ColorSpace</c>
    /// subdictionary's entry — wherever a space may be given by name: an inline image's
    /// <c>/ColorSpace</c> or <c>/Indexed</c> base (§8.9.7), a Pattern space's underlying space
    /// (§8.6.6.2). A leaf, for both renderers.
    /// </summary>
    internal static class ColorSpaceResources
    {
        /// <summary>
        /// The resource <paramref name="name"/> stands for, or null — always for the reserved names
        /// (the device families, their abbreviations and <c>/Pattern</c>, which no resource may
        /// shadow, §8.6.3) and for a name the resources do not define.
        /// </summary>
        internal static PdfObject Lookup(PdfName name, PdfDictionary resources, PdfDocument doc)
        {
            if (name == null || resources == null || doc == null) return null;
            switch (PdfColorSpaceNames.Expand(name.Value))
            {
                case PdfColorSpaceNames.DeviceGray:
                case PdfColorSpaceNames.DeviceRGB:
                case PdfColorSpaceNames.DeviceCMYK:
                case PdfColorSpaceNames.Pattern:
                    return null;
            }
            if (doc.ResolveObject(resources[PdfName.ColorSpace]) is not PdfDictionary spaces) return null;
            return doc.ResolveObject(spaces[name]);
        }

        /// <summary><paramref name="space"/> itself, or — when it is a name the resources define — the space it names.</summary>
        internal static PdfObject Resolve(PdfObject space, PdfDictionary resources, PdfDocument doc) =>
            space is PdfName name && Lookup(name, resources, doc) is PdfObject named ? named : space;
    }

    /// <summary>
    /// The colour an uncoloured pattern's cells are painted in: the <c>scn</c> components in the
    /// Pattern space's underlying space (§8.7.3.3), converted as a solid colour in that space is —
    /// a <c>/Separation</c> or <c>/DeviceN</c> space through its tint transform and alternate, an
    /// ICC or CIE space through its parameters. A leaf, for both renderers.
    /// </summary>
    /// <remarks>
    /// Converting by the space's family name alone, a spot-coloured pattern would paint white
    /// (raster) or grey (SVG) where the spot belongs, and an underlying space named from the
    /// resources would not be looked up at all.
    /// </remarks>
    internal static class PatternUnderlyingColor
    {
        internal static SkiaSharp.SKColor ToColor(PdfDocument doc, PdfObject baseSpace, float[] comps, IccRenderingIntent intent)
        {
            var rcs = PdfColor.ResolveSpace(baseSpace, doc);
            PdfArray spot = NamedSpotColors.Substitute(doc, rcs.Array);   // a spot the named-colour profile names
            if ((rcs.Family == PdfColorSpaceNames.Separation || rcs.Family == PdfColorSpaceNames.DeviceN)
                && spot != null && spot.Count > 3
                && doc?.ResolveObject(spot[3]) is PdfDictionary tintTransform)
            {
                PdfObject alternate = spot[2];
                float[] alt = Core.Functions.PdfFunctionEvaluator.Evaluate(tintTransform, comps, doc.ResolveObject);
                return IccColorTransform.ResolveAlternateColor(doc, alternate, alt, PdfColor.ResolveSpace(alternate, doc).DeviceFamily, intent);
            }
            return IccColorTransform.ResolveAlternateColor(doc, baseSpace, comps, rcs.DeviceFamily, intent);
        }
    }
}
