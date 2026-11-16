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
using Tetitulz.Pdf.Base.Operations.Documents;
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
        /// <summary>Sets the /Rotate entry on the specified page (0-based index).</summary>
        public static void RotatePage(Stream input, Stream output, int pageIndex, int degrees)
            => PdfPageManipulator.RotatePage(input, output, pageIndex, degrees);

        /// <summary>Removes the specified page (0-based) from the PDF.</summary>
        public static void DeletePage(Stream input, Stream output, int pageIndex)
            => PdfPageManipulator.DeletePage(input, output, pageIndex);

        /// <summary>Inserts a blank page at the specified position (0-based).</summary>
        public static void InsertBlankPage(Stream input, Stream output,
            int pageIndex,
            double widthPt = Core.Constants.PdfPageSizes.A4Width,
            double heightPt = Core.Constants.PdfPageSizes.A4Height)
            => PdfPageManipulator.InsertBlankPage(input, output, pageIndex, widthPt, heightPt);

        /// <summary>
        /// Sets one of the five page boundary boxes (§14.11.2) on one page.
        /// </summary>
        /// <param name="input">Source PDF. Not disposed.</param>
        /// <param name="output">Receives the original bytes plus one appended revision.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="box">Which box to write.</param>
        /// <param name="x">Left edge, in points from the page origin.</param>
        /// <param name="y">Bottom edge.</param>
        /// <param name="width">Width.</param>
        /// <param name="height">Height.</param>
        /// <remarks>
        /// Refuses a sub-box outside the page's <c>/MediaBox</c> rather than storing it: a renderer
        /// intersects the two silently, so an out-of-bounds rectangle would produce a file that
        /// draws as though the call never happened.
        /// </remarks>
        public static void SetPageBox(Stream input, Stream output, int pageIndex,
            Core.Constants.PdfPageBoxKind box, double x, double y, double width, double height)
            => PdfPageManipulator.SetPageBox(input, output, pageIndex, box, x, y, width, height);

        /// <summary>
        /// Sets one boundary box to the same rectangle on several pages, as one revision — the
        /// prepress "give every page a bleed" case.
        /// </summary>
        /// <param name="input">Source PDF. Not disposed.</param>
        /// <param name="output">Receives the original bytes plus one appended revision.</param>
        /// <param name="pageIndices">Zero-based page indices.</param>
        /// <param name="box">Which box to write.</param>
        /// <param name="x">Left edge, in points from the page origin.</param>
        /// <param name="y">Bottom edge.</param>
        /// <param name="width">Width.</param>
        /// <param name="height">Height.</param>
        public static void SetPageBoxes(Stream input, Stream output,
            System.Collections.Generic.IReadOnlyList<int> pageIndices,
            Core.Constants.PdfPageBoxKind box, double x, double y, double width, double height)
            => PdfPageManipulator.SetPageBox(input, output, pageIndices, box, x, y, width, height);

        /// <summary>
        /// Writes how the document opens: <c>/PageMode</c>, <c>/PageLayout</c>, <c>/OpenAction</c>
        /// and <c>/ViewerPreferences</c> (§7.7.2, §12.2).
        /// </summary>
        /// <param name="input">Source PDF. Not disposed.</param>
        /// <param name="output">Receives the original bytes plus one appended revision.</param>
        /// <param name="settings">What to write; null members are left alone.</param>
        /// <remarks>
        /// <para>All four were readable and none was writable — <c>PdfViewerPreferences</c> is
        /// get-only, and the single viewer-preference key anything in this library ever wrote was
        /// <c>/DisplayDocTitle</c>, as a side effect of setting an accessibility title.</para>
        /// <para><b>For a Hebrew or Arabic document, set
        /// <c>ViewerPreferences.Direction = "R2L"</c></b> — it governs page layout, not text order.</para>
        /// </remarks>
        public static Operations.Structure.InitialViewResult SetInitialView(
            Stream input, Stream output, Operations.Structure.InitialViewSettings settings)
            => Operations.Structure.PdfInitialView.SetInitialView(input, output, settings);

        /// <summary>Reads how the document opens.</summary>
        /// <param name="input">The PDF to read. Not disposed.</param>
        /// <param name="password">User or owner password, or null.</param>
        /// <remarks>
        /// The password overload exists because <c>FacadePasswordCoverageTests</c> requires every
        /// read-only façade entry point to have one: without it an encrypted document loads no
        /// catalog and this answers "the document sets nothing", which is indistinguishable from a
        /// document that really sets nothing.
        /// </remarks>
        public static Operations.Structure.InitialViewSettings GetInitialView(
            Stream input, string password = null)
            => WithDecrypted(input, password, Operations.Structure.PdfInitialView.GetInitialView);

        /// <summary>
        /// Sets a page's §12.4.4 presentation transition and its auto-advance time.
        /// </summary>
        /// <param name="input">Source PDF. Not disposed.</param>
        /// <param name="output">Receives the original bytes plus one appended revision.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="transition">The transition, or null to remove any the page has.</param>
        /// <param name="displayDuration">
        /// Seconds before the page auto-advances (<c>/Dur</c>), null to leave it, negative to remove it.
        /// </param>
        /// <remarks>
        /// The two are written together as a convenience, not because one needs the other. §12.4.4
        /// plays <c>/Trans</c> whenever the presentation advances to the page, including on a click;
        /// <c>/Dur</c> is the auto-advance timer that makes it advance by itself. A transition with
        /// no <c>/Dur</c> is perfectly visible — an unattended slideshow is what needs both.
        /// </remarks>
        public static void SetPageTransition(
            Stream input, Stream output, int pageIndex,
            Operations.Documents.PageTransitionSettings transition,
            double? displayDuration = null)
            => Operations.Documents.PdfPageManipulator.SetPageTransition(
                   input, output, pageIndex, transition, displayDuration);

        /// <summary>Sets the same transition on several pages, as one revision.</summary>
        /// <param name="input">Source PDF. Not disposed.</param>
        /// <param name="output">Receives the original bytes plus one appended revision.</param>
        /// <param name="pageIndices">Zero-based page indices.</param>
        /// <param name="transition">The transition, or null to remove any those pages have.</param>
        /// <param name="displayDuration">Seconds before auto-advance, or null to leave it.</param>
        public static void SetPageTransitions(
            Stream input, Stream output,
            System.Collections.Generic.IReadOnlyList<int> pageIndices,
            Operations.Documents.PageTransitionSettings transition,
            double? displayDuration = null)
            => Operations.Documents.PdfPageManipulator.SetPageTransition(
                   input, output, pageIndices, transition, displayDuration);

        /// <summary>Removes a boundary box entry so the page reverts to its default.</summary>
        /// <param name="input">Source PDF. Not disposed.</param>
        /// <param name="output">Receives the original bytes plus one appended revision.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="box">Which box to remove.</param>
        public static void RemovePageBox(Stream input, Stream output, int pageIndex,
            Core.Constants.PdfPageBoxKind box)
            => PdfPageManipulator.RemovePageBox(input, output, pageIndex, box);
    }
}
