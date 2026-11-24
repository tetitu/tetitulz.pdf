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
internal sealed partial class SvgContentWriter
    {
        private void OpBmc(ContentStreamOperation op)
        {
            string tag = op.Operands.Length > 0 ? (op.Operands[0] as PdfName)?.Value ?? "MC" : "MC";
            _sb.Append($"<g data-mc=\"{XmlEsc(tag)}\">\n");
            _gs.MarkedContentGroupCount++; _unclosedGroups++;
        }

        private void OpBdc(ContentStreamOperation op)
        {
            string tag = op.Operands.Length > 0 ? (op.Operands[0] as PdfName)?.Value ?? "MC" : "MC";

            // OCG visibility check: BDC /OC <properties>
            // No shortcut on an empty hidden set — every group ON still hides what an OCMD shows
            // only while groups are off (ContentStreamInterpreter.IsOcEntryHidden).
            if (tag == "OC" && op.Operands.Length > 1)
            {
                // The RAW entry, not a resolved dictionary: an OCG is identified by its object
                // number, and resolving first throws that away.
                PdfObject entry = op.Operands[1];
                if (entry is PdfName propName)
                {
                    var propsDict = _doc.ResolveObject(_resources?[PdfName.Properties]) as PdfDictionary;
                    entry = propsDict?[propName];
                }

                // §8.11.3.2: hidden content is not drawn, but its graphics state operators apply
                // and its text position moves — so the section is written, in a group a viewer
                // does not display, and its operators run as any other's do; a reader that
                // skipped them left the pen where the hidden text began. A definition the section
                // writes (a glyph's outline, a mask, a pattern) stays referenceable from a
                // displayed group: SVG renders a referenced element wherever it stands.
                if (entry != null && IsOcMarkedContentHidden(entry))
                {
                    _sb.Append($"<g data-mc=\"{XmlEsc(tag)}\" display=\"none\">\n");
                    _gs.MarkedContentGroupCount++; _unclosedGroups++;
                    return;
                }
            }

            _sb.Append($"<g data-mc=\"{XmlEsc(tag)}\">\n");
            _gs.MarkedContentGroupCount++; _unclosedGroups++;
        }

        /// <summary>
        /// Whether a <c>BDC /OC</c> properties entry — an OCG reference or an OCMD — resolves
        /// to hidden.
        ///
        /// <para><b>The untyped case is why this delegates.</b> Switching on <c>/Type</c> and, finding
        /// neither <c>OCG</c> nor <c>OCMD</c>, looking for an <c>/OC</c> key a properties entry does
        /// not have, an OCMD written <b>without</b> <c>/Type</c> (which producers routinely omit)
        /// would never be suppressed, while the raster
        /// path treated a dictionary carrying <c>/OCGs</c> or <c>/VE</c> as an OCMD and hid it.
        /// <see cref="ContentStreamInterpreter.IsOcEntryHidden"/> is that rule, and the
        /// <c>/VE</c> evaluation both sides already shared.</para>
        /// </summary>
        private bool IsOcMarkedContentHidden(PdfObject entry)
        {
            // The entry itself and nothing more, as the raster BDC handler reads it. A fallback here
            // also hid a properties dictionary carrying its own /OC key — not the shape §8.11.3.2
            // describes, and one the raster path painted, so the two renderers disagreed on it.
            return ContentStreamInterpreter.IsOcEntryHidden(entry, HiddenOcgs, _doc);
        }

        private void OpEmc()
        {
            if (_gs.MarkedContentGroupCount > 0)
            {
                _sb.Append("</g>\n");
                _gs.MarkedContentGroupCount--;
                _unclosedGroups--;
            }
        }

        // ---- Blend mode name mapping ----

        /// <summary>
        /// The CSS <c>mix-blend-mode</c> keyword for a PDF blend-mode name, or <see langword="null"/>
        /// for Normal, its alias and anything unrecognised. Internal because
        /// <see cref="PdfSvgRenderer"/> puts an annotation's <c>/BM</c> on the annotation group
        /// through it — one translation table, not a second copy beside the annotation loop.
        /// </summary>
        internal static string PdfBlendModeToSvg(string pdfMode) => pdfMode switch
        {
            PdfBlendModes.Multiply   => "multiply",
            PdfBlendModes.Screen     => "screen",
            PdfBlendModes.Overlay    => "overlay",
            PdfBlendModes.Darken     => "darken",
            PdfBlendModes.Lighten    => "lighten",
            PdfBlendModes.ColorDodge => "color-dodge",
            PdfBlendModes.ColorBurn  => "color-burn",
            PdfBlendModes.HardLight  => "hard-light",
            PdfBlendModes.SoftLight  => "soft-light",
            PdfBlendModes.Difference => "difference",
            PdfBlendModes.Exclusion  => "exclusion",
            PdfBlendModes.Hue        => "hue",
            PdfBlendModes.Saturation => "saturation",
            PdfBlendModes.Color      => "color",
            PdfBlendModes.Luminosity => "luminosity",
            _                        => null
        };

        // ---- Text ----
    }
}
