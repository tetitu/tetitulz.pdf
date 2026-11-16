// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Whether an annotation is drawn at all for the target being rendered: §12.5.3's flags, and the
    /// stricter rule §12.7.5.5 states for a signature field's widget. One implementation, reached
    /// from both renderers' annotation loops.
    /// </summary>
    /// <remarks>
    /// <para><b>The flag block was written out twice</b>, verbatim, in <c>PdfPageRenderer</c> and
    /// <c>PdfSvgRenderer</c>, and the signature rule was in neither. §12.7.5.5 (ISO 32000-2, lines
    /// 45287–45290 of <c>docs/iso/iso32000-2.txt</c>): "PDF processors shall also treat signatures
    /// as not visible if either the Hidden bit or the NoView bit of the F entry is true." Table
    /// 167's NoView (38668–38671) says an annotation so flagged "may be printed (depending on the
    /// setting of the Print flag)", and <see cref="PdfAnnotationFlags.IsHiddenInPrint"/> implements
    /// exactly that — so a signature widget flagged <c>NoView | Print</c> was printed by both
    /// renderers. The signature rule is stated without the screen/print split and for the one field
    /// type it names; the specific rule wins for that field, and only for it.</para>
    ///
    /// <para>The field type is inheritable (Table 220) and is read through the same climb the
    /// fallback and the stale-appearance predicate use —
    /// <see cref="AnnotationFallbackModel.ResolveWidgetEntry"/> — so the render path still has one
    /// <c>/FT</c> reader.</para>
    ///
    /// <para>A leaf by construction: no static fields, so no type initializer.</para>
    /// </remarks>
    internal static class AnnotationVisibility
    {
        /// <summary>
        /// <see langword="true"/> when the annotation must not be drawn under <paramref name="options"/>:
        /// outside its <see cref="PdfRenderOptions.Annotations"/> scope, or hidden by its flags —
        /// under <see cref="RenderIntent.Print"/> §12.5.3's print rule (Hidden, or Print clear, and an
        /// absent <c>/F</c> is 0, so nothing prints), else its screen rule, Hidden or NoView.
        /// </summary>
        internal static bool IsHidden(PdfDictionary annot, PdfDocument doc, PdfRenderOptions options)
        {
            options ??= PdfRenderOptions.Default;
            if (options.Annotations == AnnotationRendering.None) return true;
            if (options.Annotations == AnnotationRendering.FormFieldsOnly
                && PdfResolve.Name(doc, annot, PdfName.Subtype)?.Value != PdfAnnotationSubtypes.Widget)
                return true;
            return IsHidden(annot, doc, options.Intent == RenderIntent.Print);
        }

        private static bool IsHidden(PdfDictionary annot, PdfDocument doc, bool printing)
        {
            PdfNumber flagsNum = PdfResolve.Number(doc, annot, PdfName.F);
            if (flagsNum == null)
            {
                // No /F at all means every flag is clear, including /Print — so nothing prints.
                // §12.5.3 gives /F a default of 0 and makes no exception for absence.
                return printing;
            }

            // The one /F reader. A /F that is not an integer reads as 0 — the §12.5.3 default,
            // exactly as though it were absent.
            int flags = PdfAnnotationFlags.FromNumber(flagsNum) ?? 0;
            if (printing ? PdfAnnotationFlags.IsHiddenInPrint(flags)
                         : PdfAnnotationFlags.IsHiddenOnScreen(flags))
                return true;

            // §12.7.5.5: a signature is not visible under NoView on any target. Hidden is already
            // covered above for both targets, so only NoView on a print target reaches here — and
            // the /FT climb is paid only then.
            return printing
                && (flags & PdfAnnotationFlags.NoView) != 0
                && IsSignatureWidget(annot, doc);
        }

        private static bool IsSignatureWidget(PdfDictionary annot, PdfDocument doc)
        {
            if (PdfResolve.Name(doc, annot, PdfName.Subtype)?.Value != PdfAnnotationSubtypes.Widget)
                return false;

            var fieldType = AnnotationFallbackModel.ResolveWidgetEntry(
                annot, doc, PdfName.FT, o => o is PdfName) as PdfName;
            return fieldType?.Value == PdfFieldTypes.Signature;
        }
    }
}
