// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Reads a <c>/BM</c> value — a graphics state parameter dictionary's (§8.4.5, Table 57) or an
    /// annotation dictionary's (§12.5.2, Table 166) — into the one blend-mode name it asks for.
    /// </summary>
    /// <remarks>
    /// <para><b>The array form has a rule, and both renderers keep it.</b> §11.6.3
    /// (ISO 32000-2, lines 33999–34001 of <c>docs/iso/iso32000-2.txt</c>): the value "shall be
    /// either a name object … or its value shall be an array of such names … If encountered, a PDF
    /// processor shall use the first blend mode in the array that it recognizes (or Normal if it
    /// recognizes none of them)". Taking element 0 would paint <c>[/Foo /Multiply]</c> — a producer's own
    /// mode with the standard fallback after it, which is what the array form exists to express —
    /// Normal. A deviation both renderers share is invisible to a raster-versus-SVG comparison, so
    /// the decision lives here, once,
    /// and is reached from both readers and from the annotation compositor
    /// (<see cref="AnnotationAppearance.BlendMode"/>).</para>
    ///
    /// <para><b>What "recognises" means is the constant list</b> in <see cref="PdfBlendModes"/>:
    /// the two standard tables plus <c>/Compatible</c>, the PDF 1.3 alias of Normal. The
    /// renderer-specific translations — <c>PdfRenderContext.MapBlendMode</c> to Skia and
    /// <c>SvgContentWriter.PdfBlendModeToSvg</c> to CSS — stay where they are: they map a recognised
    /// name to a target, and this decides which name.</para>
    ///
    /// <para>A leaf by construction: no static fields, so no type initializer and no initialization
    /// edge between the two renderers.</para>
    /// </remarks>
    internal static class PdfBlendModeEntry
    {
        /// <summary>
        /// Whether <paramref name="name"/> is one of the standard blend modes (Tables 134 and 135)
        /// or the <c>/Compatible</c> alias.
        /// </summary>
        internal static bool IsRecognised(string name) => PdfBlendModes.IsStandard(name);

        /// <summary>
        /// Whether a mode name means Normal compositing: absent, <c>/Normal</c>, or its PDF 1.3
        /// alias <c>/Compatible</c>.
        /// </summary>
        internal static bool IsNormal(string name)
            => string.IsNullOrEmpty(name)
               || name == PdfBlendModes.Normal
               || name == PdfBlendModes.Compatible;

        /// <summary>
        /// The blend mode a <c>/BM</c> value asks for: the name itself, or the first recognised name
        /// of an array; <see cref="PdfBlendModes.Normal"/> when a value is present but names nothing
        /// recognised; <see langword="null"/> when there is no <c>/BM</c> value at all, so a caller
        /// can leave its state alone.
        /// </summary>
        /// <param name="doc">
        /// Resolves the value and each array element — §7.3.10 lets either be an indirect reference,
        /// and an unresolved one reads as absent.
        /// </param>
        /// <param name="value">The stored <c>/BM</c> value, <c>dict[PdfName.BM]</c>, unresolved.</param>
        /// <remarks>
        /// A single unrecognised name also answers Normal. The specification's sentence covers the
        /// array case only, but both renderers already painted such a name as Normal — Skia's map
        /// defaults to <c>SrcOver</c> and the SVG map to no <c>mix-blend-mode</c> — so this states
        /// the established behaviour rather than choosing a new one.
        /// </remarks>
        internal static string FirstRecognised(PdfDocument doc, PdfObject value)
        {
            var resolved = PdfResolve.Value(doc, value);
            if (resolved is PdfName name)
                return IsRecognised(name.Value) ? name.Value : PdfBlendModes.Normal;

            if (resolved is PdfArray array)
            {
                for (int i = 0; i < array.Count; i++)
                {
                    if (PdfResolve.Name(doc, array[i]) is PdfName candidate && IsRecognised(candidate.Value))
                        return candidate.Value;
                }
                return PdfBlendModes.Normal;
            }

            return null;
        }
    }
}
