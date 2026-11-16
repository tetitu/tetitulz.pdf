// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// An inline image's colour space as a colour space rather than a resource name (ISO 32000-2
    /// §8.9.7: the <c>/ColorSpace</c> entry "may be … the name of a colour space in the
    /// <c>ColorSpace</c> subdictionary of the current resource dictionary").
    /// </summary>
    /// <remarks>
    /// <para><b>Why it exists.</b> Both renderers expanded the device abbreviations and nothing
    /// else, so <c>BI /CS /CS0 …</c> reached the image decode as an unknown family: a spot image
    /// named that way painted nothing where the spot belongs. An <c>/Indexed</c> space's
    /// base may be such a name too (the same clause), and is resolved the same way.</para>
    ///
    /// <para><b>The parsed operand is not changed.</b> A content stream's parsed operations are
    /// cached, and the same stream can be drawn under different resources; the answer is a copy.</para>
    ///
    /// <para>A leaf, shared by the raster interpreter and the SVG writer.</para>
    /// </remarks>
    internal static class InlineImageColorSpace
    {
        /// <summary>
        /// <paramref name="image"/> itself, or a copy whose <c>/ColorSpace</c> — or an
        /// <c>/Indexed</c> space's base — names a resource and is replaced by its value. Call after
        /// the abbreviations are expanded.
        /// </summary>
        internal static PdfStream Resolve(PdfStream image, PdfDictionary resources, PdfDocument doc)
        {
            if (image == null || resources == null || doc == null) return image;
            PdfObject cs = image[PdfName.ColorSpace];

            PdfObject replacement = null;
            if (cs is PdfName name)
                replacement = Lookup(name, resources, doc);
            else if (cs is PdfArray arr && arr.Count >= 2 && arr[0] is PdfName head
                     && PdfColorSpaceNames.Expand(head.Value) == PdfColorSpaceNames.Indexed
                     && arr[1] is PdfName baseName && Lookup(baseName, resources, doc) is PdfObject resolvedBase)
            {
                var copy = new PdfArray();
                for (int i = 0; i < arr.Count; i++) copy.Add(i == 1 ? resolvedBase : arr[i]);
                replacement = copy;
            }
            if (replacement == null) return image;

            var resolved = (PdfStream)PdfDictionary.CloneShallow(image);
            resolved.Set(PdfName.ColorSpace, replacement);
            return resolved;
        }

        /// <summary>The one resource lookup, shared with the Pattern space's underlying space (<see cref="ColorSpaceResources"/>).</summary>
        private static PdfObject Lookup(PdfName name, PdfDictionary resources, PdfDocument doc)
            => ColorSpaceResources.Lookup(name, resources, doc);
    }
}
