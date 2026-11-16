// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// Which annotations PDF/UA treats as content a reader is presented with — the ones that
    /// belong in the structure tree and need a description.
    /// </summary>
    /// <remarks>
    /// <para><b>One predicate for three readers of the same rule</b>: the PDF/UA-1 checker's UA27,
    /// the deep checker's annotation walk (UA22, UA23), and the auto-tagger deciding what to put in
    /// the tree. Each carried the rule verbatim under a comment saying it "must stay identical" to
    /// the others — which is a duplication report, and the reason a producer and a checker could
    /// otherwise come to disagree: the tagger skipping something the checker then reports is
    /// remediation manufacturing a violation. Public, because the Accessibility feature reads it
    /// across the sanctioned feature → plugin edge, as it reads <see cref="PdfUaXmpIdentifier"/>.</para>
    ///
    /// <para>Excluded:</para>
    /// <list type="bullet">
    ///   <item><description><c>/Popup</c> — the pop-up of another annotation, never
    ///     independently navigable.</description></item>
    ///   <item><description><c>/PrinterMark</c> — ISO 14289-1 treats printer's marks as artifacts
    ///     and exempts them from structure-tree inclusion and from the description
    ///     requirement.</description></item>
    ///   <item><description>Hidden (bit 2) or NoView (bit 6) — presented to nobody.</description></item>
    /// </list>
    ///
    /// <para><b>A check may narrow it further, and says so where it does</b>: UA27 also leaves out
    /// <c>/Widget</c>, whose description is a field's <c>/TU</c> and is UA08's to judge.</para>
    /// </remarks>
    public static class PdfUaAnnotationScope
    {
        /// <summary>
        /// Whether an annotation of <paramref name="subtype"/>, with the flag word
        /// <paramref name="flags"/> (<c>PdfAnnotationFlags.FromNumber</c>; <c>null</c> for none),
        /// is presented to a reader as content.
        /// </summary>
        public static bool IsPresentedToReader(string subtype, int? flags)
        {
            if (subtype == PdfAnnotationSubtypes.Popup || subtype == PdfAnnotationSubtypes.PrinterMark)
                return false;
            return !(flags.HasValue && PdfAnnotationFlags.IsHiddenOnScreen(flags.Value));
        }

        /// <summary>
        /// <see cref="IsPresentedToReader(string, int?)"/>, and the annotation's <c>/Rect</c> is not
        /// wholly outside its page's CropBox — ISO 14289-1 §7.18.1: "The requirements of this clause
        /// shall not apply to annotations whose hidden flag is set or whose rectangle is outside the
        /// CropBox or whose Subtype is Popup" (the profile's <c>isOutsideCropBox</c>, test files
        /// 7.18.1-t02-pass-d and -h).
        /// </summary>
        /// <param name="subtype">The annotation's <c>/Subtype</c>.</param>
        /// <param name="flags">Its flag word, <c>null</c> for none.</param>
        /// <param name="rect">Its <c>/Rect</c>, resolved; <c>null</c> when unknown.</param>
        /// <param name="cropBox">Its page's CropBox, inherited and defaulted; <c>null</c> when unknown.</param>
        public static bool IsPresentedToReader(string subtype, int? flags, PdfArray rect, PdfArray cropBox)
            => IsPresentedToReader(subtype, flags) && !IsOutsideCropBox(rect, cropBox);

        /// <summary>
        /// <see cref="IsPresentedToReader(string, int?, PdfArray, PdfArray)"/>, each element of the
        /// two boxes read through <paramref name="resolve"/>: a <c>/Rect</c> may hold indirect
        /// numbers (the <c>PdfResolve</c> rule for an array a dictionary holds), and read raw they
        /// are no numbers, so the CropBox exemption could not be read and the annotation stayed in scope.
        /// </summary>
        /// <param name="subtype">The annotation's <c>/Subtype</c>.</param>
        /// <param name="flags">Its flag word, <c>null</c> for none.</param>
        /// <param name="rect">Its <c>/Rect</c>; <c>null</c> when unknown.</param>
        /// <param name="cropBox">Its page's CropBox, inherited and defaulted; <c>null</c> when unknown.</param>
        /// <param name="resolve">The document's resolver, <c>doc.ResolveObject</c>; null reads the elements as they are.</param>
        public static bool IsPresentedToReader(string subtype, int? flags, PdfArray rect, PdfArray cropBox,
            Func<PdfObject, PdfObject> resolve)
            => IsPresentedToReader(subtype, flags) && !IsOutsideCropBox(rect, cropBox, resolve);

        /// <summary>
        /// Whether <paramref name="rect"/> lies wholly outside <paramref name="cropBox"/>, both
        /// normalised (§7.9.5 lets either corner come first). An edge touching the box is inside, so
        /// a zero-size rectangle within the page is presented; an array that is not four numbers
        /// answers <see langword="false"/>, keeping the annotation in scope.
        /// </summary>
        public static bool IsOutsideCropBox(PdfArray rect, PdfArray cropBox) => IsOutsideCropBox(rect, cropBox, null);

        /// <summary><see cref="IsOutsideCropBox(PdfArray, PdfArray)"/>, each element read through <paramref name="resolve"/> when one is given.</summary>
        public static bool IsOutsideCropBox(PdfArray rect, PdfArray cropBox, Func<PdfObject, PdfObject> resolve)
        {
            if (!TryCorners(rect, resolve, out double rx1, out double ry1, out double rx2, out double ry2)
                || !TryCorners(cropBox, resolve, out double cx1, out double cy1, out double cx2, out double cy2))
                return false;
            return rx1 > cx2 || rx2 < cx1 || ry1 > cy2 || ry2 < cy1;
        }

        private static bool TryCorners(PdfArray box, Func<PdfObject, PdfObject> resolve,
            out double x1, out double y1, out double x2, out double y2)
        {
            x1 = y1 = x2 = y2 = 0;
            if (box == null || box.Count < 4) return false;
            PdfObject Element(int i) => resolve != null ? resolve(box[i]) : box[i];
            if (!(Element(0) is PdfNumber a) || !(Element(1) is PdfNumber b) || !(Element(2) is PdfNumber c) || !(Element(3) is PdfNumber d))
                return false;
            x1 = System.Math.Min(a.DoubleValue, c.DoubleValue);
            x2 = System.Math.Max(a.DoubleValue, c.DoubleValue);
            y1 = System.Math.Min(b.DoubleValue, d.DoubleValue);
            y2 = System.Math.Max(b.DoubleValue, d.DoubleValue);
            return !double.IsNaN(x1) && !double.IsNaN(x2) && !double.IsNaN(y1) && !double.IsNaN(y2);
        }
    }
}
