// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Base.Operations.Metadata;

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    /// <summary>
    /// Page-level manipulation via incremental PDF updates:
    /// rotate, insert blank, and delete pages.
    /// </summary>
    public static class PdfPageManipulator
    {
        // ----------------------------------------------------------------
        //  Rotate
        // ----------------------------------------------------------------

        /// <summary>
        /// Sets the /Rotate entry on the specified page (0-based index).
        /// </summary>
        /// <param name="input">Readable stream containing the source PDF.</param>
        /// <param name="output">Writable stream for the updated PDF.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="degrees">Rotation angle; must be 0, 90, 180, or 270.</param>
        public static void RotatePage(Stream input, Stream output, int pageIndex, int degrees)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (degrees != 0 && degrees != 90 && degrees != 180 && degrees != 270)
                throw new ArgumentOutOfRangeException(nameof(degrees), "Must be 0, 90, 180, or 270.");

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            EncryptedRevision.Refuse(doc, "rotate a page of", "the revised page");

            ValidatePageIndex(doc, pageIndex);
            var page = doc.Pages[pageIndex];
            int pageObjNum = page.Dictionary.ObjectNumber;
            if (pageObjNum <= 0)
                throw new Core.PdfException("Page object has no object number; cannot update via incremental write.");

            int maxObjNum = doc.CrossReferenceTable.GetMaxObjectNumber();

            var updatedPage = CloneDictionary(page.Dictionary);
            updatedPage.Set(PdfName.Rotate, new PdfNumber(degrees));
            updatedPage.ObjectNumber = pageObjNum;
            updatedPage.GenerationNumber = 0;

            var objects = new List<IndirectObject>
            {
                new IndirectObject(pageObjNum, 0, updatedPage)
            };

            var trailer = BuildTrailer(doc.Trailer, maxObjNum + 1);
            ms.Position = 0;
            IncrementalUpdateWriter.WriteUpdate(
                ms, output, objects, trailer,
                doc.Parser.StartXrefOffset,
                trailer.GetInteger(PdfName.Size) ?? (maxObjNum + 1));
        }

        // ----------------------------------------------------------------
        //  Delete
        // ----------------------------------------------------------------

        /// <summary>
        /// Removes the specified page (0-based) from the PDF.
        /// </summary>
        /// <remarks>
        /// <b>Not a deletion of the bytes.</b> This appends a revision, so a signature over the earlier
        /// ones survives and the page's content stays in the file, recoverable from the revision before;
        /// its outline items and field entries stay reachable in this one. To hand out fewer pages,
        /// extract the ones to keep (<c>Plugins.PageEdits.PdfSplitter.ExtractPages</c>), which writes
        /// what they reach and nothing else.
        /// </remarks>
        /// <param name="input">Readable stream containing the source PDF.</param>
        /// <param name="output">Writable stream for the updated PDF.</param>
        /// <param name="pageIndex">Zero-based page index of the page to delete.</param>
        public static void DeletePage(Stream input, Stream output, int pageIndex)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            EncryptedRevision.Refuse(doc, "delete a page of", "the revised page tree");

            if (doc.Pages.Count <= 1)
                throw new Core.PdfException("Cannot delete the only page in a PDF document.");

            ValidatePageIndex(doc, pageIndex);
            var page = doc.Pages[pageIndex];
            int pageObjNum = page.Dictionary.ObjectNumber;
            if (pageObjNum <= 0)
                throw new Core.PdfException("Page object has no object number; cannot update via incremental write.");

            int maxObjNum = doc.CrossReferenceTable.GetMaxObjectNumber();
            var objects = new List<IndirectObject>();

            // Walk up the /Parent chain, removing this page from the direct parent's
            // /Kids and decrementing /Count on every ancestor node.
            int childObjNum = pageObjNum;
            PdfObject parentPtr = page.Dictionary[PdfName.Parent];

            // Bounded as InheritsMediaBox bounds the same climb: a /Parent ring (the root naming
            // itself) went round for ever, a cloned node and a new /Kids array each turn.
            var climbed = new HashSet<int>();
            while (parentPtr is PdfReference parentRef)
            {
                if (!climbed.Add(parentRef.ReferenceObjectNumber) || climbed.Count > PdfLimits.MaxPageTreeDepth)
                    throw new Core.PdfException(
                        "The page tree's /Parent chain above page " + (pageIndex + 1) + " is a ring or deeper than " +
                        PdfLimits.MaxPageTreeDepth + " levels, so the page's ancestors cannot be updated. No changes were written.");
                var parentDict = doc.ResolveObject(parentRef) as PdfDictionary;
                if (parentDict == null) break;

                var updatedParent = CloneDictionary(parentDict);
                // Both reads below resolve, and their null paths corrupt the page tree in two
                // different ways rather than merely failing:
                //
                //   /Count  unresolved fell to 0 and this method then WRITES `count - 1`, i.e.
                //           /Count -1 on a /Pages node. Readers navigate by /Count.
                //   /Kids   unresolved skipped the removal block below entirely, so the clone kept
                //           the original array — the page stayed listed while /Count was decremented
                //           anyway. The tree then claims one fewer page than it lists.
                //
                // §7.3.10 permits both spellings, and a /Pages node written by a tool that shares
                // its /Kids array is the ordinary way to get one.
                int count = (doc.ResolveObject(parentDict[PdfName.Count]) as PdfNumber)?.IntValue ?? 0;

                // Remove the child from /Kids only for the direct parent.
                var kids = doc.ResolveObject(parentDict[PdfName.Kids]) as PdfArray;
                if (kids != null)
                {
                    var newKids = new PdfArray();
                    bool removed = false;
                    for (int i = 0; i < kids.Count; i++)
                    {
                        if (!removed && kids[i] is PdfReference kr
                            && kr.ReferenceObjectNumber == childObjNum)
                        {
                            removed = true;
                        }
                        else
                        {
                            newKids.Add(kids[i]);
                        }
                    }
                    updatedParent.Set(PdfName.Kids, newKids);
                }

                updatedParent.Set(PdfName.Count, new PdfNumber(count - 1));
                updatedParent.ObjectNumber = parentRef.ReferenceObjectNumber;
                updatedParent.GenerationNumber = 0;

                objects.Add(new IndirectObject(parentRef.ReferenceObjectNumber, 0, updatedParent));

                // Ascend to grandparent.
                childObjNum = parentRef.ReferenceObjectNumber;
                parentPtr = parentDict[PdfName.Parent];
            }

            var trailer = BuildTrailer(doc.Trailer, maxObjNum + 1);
            ms.Position = 0;
            IncrementalUpdateWriter.WriteUpdate(
                ms, output, objects, trailer,
                doc.Parser.StartXrefOffset,
                trailer.GetInteger(PdfName.Size) ?? (maxObjNum + 1));
        }

        // ----------------------------------------------------------------
        //  Insert blank page
        // ----------------------------------------------------------------

        /// <summary>
        /// Inserts a blank page at the specified position (0-based).
        /// </summary>
        /// <param name="input">Readable stream containing the source PDF.</param>
        /// <param name="output">Writable stream for the updated PDF.</param>
        /// <param name="pageIndex">
        ///   Zero-based index at which to insert. Use <c>doc.Pages.Count</c> to append.
        /// </param>
        /// <param name="widthPt">Page width in PDF points (1 pt = 1/72 inch).</param>
        /// <param name="heightPt">Page height in PDF points.</param>
        public static void InsertBlankPage(
            Stream input, Stream output,
            int pageIndex,
            double widthPt, double heightPt)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            // NaN passes `<= 0` and infinity passes it too; PdfRealFormat writes both as 0, so the
            // page would have been written with a zero-length side and the call would have succeeded.
            NumericArguments.RequirePositive(widthPt, nameof(widthPt));
            NumericArguments.RequirePositive(heightPt, nameof(heightPt));

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            EncryptedRevision.Refuse(doc, "insert a page into", "the new page and the revised page tree");

            int pageCount = doc.Pages.Count;
            if (pageIndex < 0 || pageIndex > pageCount)
                throw new ArgumentOutOfRangeException(nameof(pageIndex),
                    $"pageIndex must be between 0 and {pageCount} (inclusive).");

            int maxObjNum = doc.CrossReferenceTable.GetMaxObjectNumber();
            int newPageObjNum = ++maxObjNum;

            var pagesRef = doc.Catalog.Dictionary[PdfName.Pages] as PdfReference;
            if (pagesRef == null)
                throw new Core.PdfException("PDF catalog has no /Pages reference.");
            var pagesDict = doc.Catalog.PageTreeRoot;
            if (pagesDict == null)
                throw new Core.PdfException("Cannot resolve /Pages tree root.");

            // The node the new page joins is the one holding the page now at pageIndex (or, for an
            // append, the last page), so a tree of any depth keeps the position promised: the root's
            // /Kids are intermediate nodes in a deep tree, and a page put among them lands wherever
            // the node order puts it. An empty tree has only the root to join.
            int neighbourIndex = pageIndex < pageCount ? pageIndex : pageCount - 1;
            PdfDictionary neighbour = neighbourIndex >= 0 ? doc.Pages[neighbourIndex].Dictionary : null;
            int parentObjNum = (neighbour?[PdfName.Parent] as PdfReference)?.ReferenceObjectNumber
                               ?? pagesRef.ReferenceObjectNumber;
            if (neighbour != null && neighbour.ObjectNumber <= 0)
                throw new Core.PdfException("Page object has no object number; cannot update via incremental write.");

            // Build the new blank page dictionary.
            var mediaBox = new PdfArray();
            mediaBox.Add(new PdfNumber(0));
            mediaBox.Add(new PdfNumber(0));
            mediaBox.Add(new PdfNumber(widthPt));
            mediaBox.Add(new PdfNumber(heightPt));

            var newPageDict = new PdfDictionary();
            newPageDict.Set(PdfName.Type, PdfName.Page);
            newPageDict.Set(PdfName.Parent, ExistingObjectGeneration.ReferenceTo(doc.CrossReferenceTable, parentObjNum));
            newPageDict.Set(PdfName.MediaBox, mediaBox);
            // ISO 32000-1 Table 30: /Resources is required, though it may be inherited. A page tree
            // with nothing to inherit left this page without one, and a structure checker repaired it. The page
            // draws nothing, so an empty dictionary is the whole answer and shadows nothing it uses.
            newPageDict.Set(PdfName.Resources, new PdfDictionary());
            newPageDict.ObjectNumber = newPageObjNum;
            newPageDict.GenerationNumber = 0;

            var objects = new List<IndirectObject> { new IndirectObject(newPageObjNum, 0, newPageDict) };

            // The parent gets the page in its /Kids, before the neighbour (after it for an append);
            // it and every ancestor get /Count + 1. The climb is DeletePage's in reverse, under the
            // same ring and depth guard; a parent whose /Kids does not list the neighbour (a page
            // whose /Parent names the wrong node) is refused, since the page would be written into
            // no position at all.
            int childObjNum = neighbour?.ObjectNumber ?? 0;
            int nodeObjNum = parentObjNum;
            var climbed = new HashSet<int>();
            bool direct = true;
            while (nodeObjNum > 0)
            {
                if (!climbed.Add(nodeObjNum) || climbed.Count > PdfLimits.MaxPageTreeDepth)
                    throw new Core.PdfException(
                        "The page tree's /Parent chain above page " + (neighbourIndex + 1) + " is a ring or deeper than " +
                        PdfLimits.MaxPageTreeDepth + " levels, so the page's ancestors cannot be updated. No changes were written.");
                var nodeDict = doc.ResolveObject(new PdfReference(nodeObjNum, 0)) as PdfDictionary;
                if (nodeDict == null)
                    throw new Core.PdfException($"The page tree node {nodeObjNum} cannot be read, so the page cannot be inserted. No changes were written.");

                var updatedNode = CloneDictionary(nodeDict);
                if (direct)
                {
                    var kids = doc.ResolveObject(nodeDict[PdfName.Kids]) as PdfArray;
                    var newKids = new PdfArray();
                    bool placed = false;
                    if (kids != null)
                    {
                        for (int i = 0; i < kids.Count; i++)
                        {
                            bool isNeighbour = kids[i] is PdfReference kr && kr.ReferenceObjectNumber == childObjNum;
                            if (isNeighbour && pageIndex < pageCount && !placed)
                            {
                                newKids.Add(new PdfReference(newPageObjNum, 0));
                                placed = true;
                            }
                            newKids.Add(kids[i]);
                            if (isNeighbour && pageIndex == pageCount && !placed)
                            {
                                newKids.Add(new PdfReference(newPageObjNum, 0));
                                placed = true;
                            }
                        }
                    }
                    if (!placed)
                    {
                        if (neighbour != null)
                            throw new Core.PdfException(
                                $"Page {neighbourIndex + 1}'s /Parent node does not list it among its /Kids, so there is no position to insert at. No changes were written.");
                        newKids.Add(new PdfReference(newPageObjNum, 0));
                    }
                    updatedNode.Set(PdfName.Kids, newKids);
                    direct = false;
                }

                int count = (doc.ResolveObject(nodeDict[PdfName.Count]) as PdfNumber)?.IntValue ?? 0;
                updatedNode.Set(PdfName.Count, new PdfNumber(count + 1));
                updatedNode.ObjectNumber = nodeObjNum;
                updatedNode.GenerationNumber = ExistingObjectGeneration.Of(doc.CrossReferenceTable, nodeObjNum);
                objects.Add(new IndirectObject(nodeObjNum, updatedNode.GenerationNumber, updatedNode));

                if (nodeObjNum == pagesRef.ReferenceObjectNumber) break;
                childObjNum = nodeObjNum;
                nodeObjNum = (nodeDict[PdfName.Parent] as PdfReference)?.ReferenceObjectNumber ?? 0;
            }

            int newSize = maxObjNum + 1;
            var trailer = BuildTrailer(doc.Trailer, newSize);
            trailer.Set(PdfName.Size, new PdfNumber(newSize));
            ms.Position = 0;
            IncrementalUpdateWriter.WriteUpdate(
                ms, output, objects, trailer,
                doc.Parser.StartXrefOffset,
                newSize);
        }

        // ----------------------------------------------------------------
        //  Helpers
        // ----------------------------------------------------------------


        /// <summary>
        /// Sets the /CropBox on a page to crop it to the given rectangle.
        /// Coordinates are in PDF points from the page origin.
        /// </summary>
        /// <remarks>
        /// One line over
        /// <see cref="SetPageBox(Stream, Stream, IReadOnlyList{int}, PdfPageBoxKind, double, double, double, double)"/>,
        /// so there is a single box-writing implementation. Its published signature is unchanged.
        /// </remarks>
        public static void CropPage(Stream input, Stream output,
            int pageIndex, double x, double y, double width, double height)
            => SetPageBox(input, output, new[] { pageIndex },
                          PdfPageBoxKind.CropBox, x, y, width, height);

        /// <summary>
        /// Sets a page's §12.4.4 presentation transition (<c>/Trans</c>) and its advance timing
        /// (<c>/Dur</c>).
        /// </summary>
        /// <param name="input">Source PDF. Not disposed.</param>
        /// <param name="output">Receives the original bytes plus one appended revision.</param>
        /// <param name="pageIndices">Zero-based page indices. Duplicates are ignored.</param>
        /// <param name="transition">
        /// The transition to write, or <see langword="null"/> to <b>remove</b> any the page has.
        /// </param>
        /// <param name="displayDuration">
        /// Seconds the page is shown before advancing automatically (<c>/Dur</c>), or
        /// <see langword="null"/> to leave it untouched. Pass a negative value to remove it.
        /// </param>
        /// <exception cref="Core.PdfException">
        /// A style, dimension, motion or direction value is not one §12.4.4 Table 161 defines.
        /// </exception>
        /// <remarks>
        /// <para><b>Here rather than with the initial-view writer, and that is the point.</b>
        /// <c>/Trans</c> and <c>/Dur</c> are <i>page</i> attributes; <c>/PageMode</c>,
        /// <c>/PageLayout</c>, <c>/OpenAction</c> and <c>/ViewerPreferences</c> are catalog ones.
        /// Putting them together would have made "how the document opens" and "how one page
        /// animates" a single API for no reason beyond both having been unwritten.</para>
        ///
        /// <para><b>Names are validated, not written blind</b> — for the reason
        /// <c>PdfInitialView</c> states: these are enumerated values, so a viewer that does not
        /// recognise one falls back to its default and a typo becomes a silent no-op.</para>
        ///
        /// <para><b>The two are written together as a convenience, not because one needs the
        /// other.</b> §12.4.4 plays <c>/Trans</c> whenever the presentation advances to the page —
        /// including on a click — and <c>/Dur</c> is the auto-advance timer that makes it advance by
        /// itself. A transition with no <c>/Dur</c> is perfectly visible; an unattended slideshow is
        /// what needs both.</para>
        /// </remarks>
        public static void SetPageTransition(
            Stream input, Stream output, IReadOnlyList<int> pageIndices,
            PageTransitionSettings transition, double? displayDuration = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (pageIndices == null) throw new ArgumentNullException(nameof(pageIndices));

            PdfDictionary transDict = transition == null ? null : BuildTransition(transition);

            WriteBoxRevision(input, output, pageIndices, _ => updated =>
            {
                if (transDict == null) updated.Remove(PdfName.Trans);
                else updated.Set(PdfName.Trans, transDict);

                if (!displayDuration.HasValue) return;
                if (displayDuration.Value < 0) updated.Remove(PdfName.Dur);
                else updated.Set(PdfName.Dur, new PdfNumber(displayDuration.Value));
            });
        }

        /// <summary>Sets a page's transition. See the multi-page overload for the contract.</summary>
        /// <param name="input">Source PDF. Not disposed.</param>
        /// <param name="output">Receives the original bytes plus one appended revision.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="transition">The transition, or null to remove one.</param>
        /// <param name="displayDuration">Seconds before auto-advance, or null to leave it.</param>
        public static void SetPageTransition(
            Stream input, Stream output, int pageIndex,
            PageTransitionSettings transition, double? displayDuration = null)
            => SetPageTransition(input, output, new[] { pageIndex }, transition, displayDuration);

        /// <summary>Builds a §12.4.4 Table 161 transition dictionary, validating every name.</summary>
        private static PdfDictionary BuildTransition(PageTransitionSettings settings)
        {
            var dict = new PdfDictionary();
            dict.Set(PdfName.Type, PdfName.Trans);

            RequireTransitionName("S", settings.Style, TransitionStyles);
            dict.Set(PdfName.S, new PdfName(settings.Style));

            if (settings.Duration.HasValue)
            {
                if (settings.Duration.Value < 0)
                    throw new Core.PdfException(
                        "A transition's /D (duration) must not be negative; " +
                        settings.Duration.Value + " was requested.");
                dict.Set(PdfName.D, new PdfNumber(settings.Duration.Value));
            }

            if (settings.Dimension != null)
            {
                RequireTransitionName("Dm", settings.Dimension, TransitionDimensions);
                dict.Set(PdfName.Dm, new PdfName(settings.Dimension));
            }

            if (settings.Motion != null)
            {
                RequireTransitionName("M", settings.Motion, TransitionMotions);
                dict.Set(PdfName.M, new PdfName(settings.Motion));
            }

            if (settings.Direction.HasValue)
            {
                // /Di is an ANGLE in degrees, from a closed set — not a free number. §12.4.4 also
                // allows the name /None for Fly, which is why the property is an int? and None has
                // its own flag rather than being spelled as a magic angle.
                int di = settings.Direction.Value;
                bool valid = false;
                for (int i = 0; i < TransitionAngles.Length; i++) if (TransitionAngles[i] == di) valid = true;
                if (!valid)
                    throw new Core.PdfException(
                        "/Di must be one of " + string.Join(", ", TransitionAngles) +
                        " degrees (§12.4.4 Table 161); " + di + " is not.");
                dict.Set(PdfName.Di, new PdfNumber(di));
            }
            else if (settings.DirectionIsNone)
            {
                dict.Set(PdfName.Di, PdfName.None);
            }

            if (settings.Scale.HasValue) dict.Set(PdfName.SS, new PdfNumber(settings.Scale.Value));
            if (settings.IsOpaque.HasValue) dict.Set(PdfName.B, new PdfBoolean(settings.IsOpaque.Value));

            return dict;
        }

        private static void RequireTransitionName(string key, string value, string[] valid)
        {
            if (value == null)
                throw new Core.PdfException("/" + key + " must not be null.");

            for (int i = 0; i < valid.Length; i++)
                if (string.Equals(valid[i], value, StringComparison.Ordinal)) return;

            throw new Core.PdfException(
                "/" + key + " must be one of " + string.Join(", ", valid) +
                " (§12.4.4 Table 161; PDF names are case-sensitive); \"" + value + "\" is not.");
        }

        /// <summary>§12.4.4 Table 161 transition styles.</summary>
        private static readonly string[] TransitionStyles =
        {
            "Split", "Blinds", "Box", "Wipe", "Dissolve", "Glitter", "R",
            "Fly", "Push", "Cover", "Uncover", "Fade",
        };

        private static readonly string[] TransitionDimensions = { "H", "V" };
        private static readonly string[] TransitionMotions = { "I", "O" };

        /// <summary>The angles §12.4.4 permits for <c>/Di</c>.</summary>
        private static readonly int[] TransitionAngles = { 0, 90, 180, 270, 315 };

        /// <summary>
        /// Sets one of the five page boundary boxes (§14.11.2) on a single page.
        /// </summary>
        /// <param name="input">Source PDF. Not disposed.</param>
        /// <param name="output">Receives the original bytes plus one appended revision.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="box">Which box to write.</param>
        /// <param name="x">Left edge, in points from the page origin.</param>
        /// <param name="y">Bottom edge.</param>
        /// <param name="width">Width; may be negative, and is normalised.</param>
        /// <param name="height">Height; may be negative, and is normalised.</param>
        public static void SetPageBox(Stream input, Stream output, int pageIndex,
            PdfPageBoxKind box, double x, double y, double width, double height)
            => SetPageBox(input, output, new[] { pageIndex }, box, x, y, width, height);

        /// <summary>
        /// Sets one boundary box to the same rectangle on several pages, as <b>one</b> incremental
        /// revision — the prepress case ("give every page a 3 mm bleed") that a per-page call would
        /// turn into N stacked revisions.
        /// </summary>
        /// <param name="input">Source PDF. Not disposed.</param>
        /// <param name="output">Receives the original bytes plus one appended revision.</param>
        /// <param name="pageIndices">Zero-based page indices. Duplicates are ignored.</param>
        /// <param name="box">Which box to write.</param>
        /// <param name="x">Left edge, in points from the page origin.</param>
        /// <param name="y">Bottom edge.</param>
        /// <param name="width">Width; may be negative, and is normalised.</param>
        /// <param name="height">Height; may be negative, and is normalised.</param>
        /// <exception cref="Core.PdfException">
        /// The rectangle is empty; or §14.11.2 containment fails — a sub-box outside the page's
        /// <c>/MediaBox</c>, or a <c>/MediaBox</c> that would no longer contain a sub-box the page
        /// already carries.
        /// </exception>
        /// <remarks>
        /// <para><b>Containment is enforced rather than trusted, and that is not pedantry.</b>
        /// §14.11.2 requires the four sub-boxes to lie within the <c>/MediaBox</c>, and
        /// <c>PdfPageRenderer</c> silently intersects them on read — falling back to the whole
        /// <c>/MediaBox</c> when the two are disjoint. So storing an out-of-bounds rectangle produces
        /// a file that renders <i>exactly as if the call had never happened</i>: a silent no-op, the
        /// failure mode this repository keeps finding. Refusing is the only outcome a caller can act
        /// on.</para>
        ///
        /// <para><b>§7.9.5 lets either corner pair come first</b>, so the rectangle is normalised to
        /// (min, min, max, max) before it is written and before it is compared — a caller passing a
        /// negative width gets the rectangle they meant rather than a containment refusal.</para>
        ///
        /// <para><b>Existing boxes are read off the page dictionary, never off
        /// <c>PdfPage.TrimBox</c> and friends</b>, which fall back to <c>/CropBox</c> and are
        /// therefore never null. Asking the property "does this page have a trim box?" answers yes
        /// for every page in every document.</para>
        /// </remarks>
        public static void SetPageBox(Stream input, Stream output,
            IReadOnlyList<int> pageIndices, PdfPageBoxKind box,
            double x, double y, double width, double height)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (pageIndices == null) throw new ArgumentNullException(nameof(pageIndices));
            // A NaN side makes both Min and Max NaN, so the emptiness and containment checks below
            // all compare false and a degenerate box is written as a success.
            NumericArguments.RequireFinite(x, nameof(x));
            NumericArguments.RequireFinite(y, nameof(y));
            NumericArguments.RequireFinite(width, nameof(width));
            NumericArguments.RequireFinite(height, nameof(height));

            double x0 = Math.Min(x, x + width), x1 = Math.Max(x, x + width);
            double y0 = Math.Min(y, y + height), y1 = Math.Max(y, y + height);
            if (x1 - x0 <= 0 || y1 - y0 <= 0)
                throw new Core.PdfException(
                    "A page box must have a non-zero width and height; " +
                    $"[{x0} {y0} {x1} {y1}] is empty.");

            WriteBoxRevision(input, output, pageIndices, page =>
            {
                EnsureContainment(page, box, x0, y0, x1, y1);

                var rect = new PdfArray();
                rect.Add(new PdfNumber(x0));
                rect.Add(new PdfNumber(y0));
                rect.Add(new PdfNumber(x1));
                rect.Add(new PdfNumber(y1));
                return updated => updated.Set(PdfPageBoxes.ToName(box), rect);
            });
        }

        /// <summary>
        /// Removes a boundary box entry from a page, so it reverts to its default — a sub-box to the
        /// <c>/CropBox</c>, and the <c>/MediaBox</c> to whatever a <c>/Pages</c> ancestor inherits it.
        /// </summary>
        /// <param name="input">Source PDF. Not disposed.</param>
        /// <param name="output">Receives the original bytes plus one appended revision.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="box">Which box to remove.</param>
        /// <exception cref="Core.PdfException">
        /// Removing the <c>/MediaBox</c> when no ancestor supplies one — §7.7.3.3 makes it required,
        /// and the result would be a page no reader can lay out.
        /// </exception>
        /// <remarks>
        /// Removing a box a page does not carry is a no-op that still appends a revision, so the
        /// output is always a valid document with the requested state.
        /// </remarks>
        public static void RemovePageBox(Stream input, Stream output, int pageIndex, PdfPageBoxKind box)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));

            WriteBoxRevision(input, output, new[] { pageIndex }, page =>
            {
                if (box == PdfPageBoxKind.MediaBox && !InheritsMediaBox(page))
                    throw new Core.PdfException(
                        "Refusing to remove /MediaBox: §7.7.3.3 makes it required and inheritable, " +
                        "and no /Pages ancestor of this page supplies one, so the page would have " +
                        "no defined size.");

                return updated => updated.Remove(PdfPageBoxes.ToName(box));
            });
        }

        // ----------------------------------------------------------------
        //  Page-box internals
        // ----------------------------------------------------------------

        /// <summary>
        /// Clones each named page, applies <paramref name="plan"/>'s edit, and appends them all as
        /// <b>one</b> incremental revision. The plan is built per page before anything is written, so
        /// a refusal on page 7 leaves the caller's output stream untouched rather than half-written.
        /// </summary>
        private static void WriteBoxRevision(
            Stream input, Stream output, IReadOnlyList<int> pageIndices,
            Func<PdfPage, Action<PdfDictionary>> plan)
        {
            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            EncryptedRevision.Refuse(doc, "change the page boxes of", "the revised pages");

            var seen = new HashSet<int>();
            var objects = new List<Core.Writing.IndirectObject>();

            foreach (int pageIndex in pageIndices)
            {
                ValidatePageIndex(doc, pageIndex);
                if (!seen.Add(pageIndex)) continue;

                var page = doc.Pages[pageIndex];
                int pageObjNum = page.Dictionary.ObjectNumber;
                if (pageObjNum <= 0)
                    throw new Core.PdfException("Cannot identify page object number.");

                var edit = plan(page);          // may throw — before any byte is written
                var updated = CloneDictionary(page.Dictionary);
                edit(updated);
                updated.ObjectNumber = pageObjNum;
                updated.GenerationNumber = 0;
                objects.Add(new Core.Writing.IndirectObject(pageObjNum, 0, updated));
            }

            int newSize = doc.CrossReferenceTable.GetMaxObjectNumber() + 1;
            Core.Writing.IncrementalUpdateWriter.WriteUpdate(
                ms, output, objects, BuildTrailer(doc.Trailer, newSize),
                doc.Parser.StartXrefOffset, newSize);
        }

        /// <summary>§14.11.2 — the four sub-boxes shall lie within the <c>/MediaBox</c>.</summary>
        private static void EnsureContainment(
            PdfPage page, PdfPageBoxKind box, double x0, double y0, double x1, double y1)
        {
            if (PdfPageBoxes.IsContainedInMediaBox(box))
            {
                if (!TryNormalisedRect(page.MediaBox, out var m)) return;   // no usable /MediaBox to test against
                if (x0 < m.x0 - Tolerance || y0 < m.y0 - Tolerance ||
                    x1 > m.x1 + Tolerance || y1 > m.y1 + Tolerance)
                    throw new Core.PdfException(
                        $"§14.11.2: the /{PdfPageBoxes.ToName(box).Value} must lie within the " +
                        $"/MediaBox. [{x0} {y0} {x1} {y1}] is not inside " +
                        $"[{m.x0} {m.y0} {m.x1} {m.y1}]. A renderer intersects the two silently, so " +
                        "storing this would produce a file that draws as though the call never " +
                        "happened.");
                return;
            }

            // Writing the /MediaBox itself: it must still contain every sub-box the page carries.
            // Read from the DICTIONARY — PdfPage.TrimBox and friends fall back to /CropBox and are
            // never null, so the property cannot answer "does this page have one?".
            foreach (PdfPageBoxKind sub in SubBoxes)
            {
                var stored = page.Dictionary[PdfPageBoxes.ToName(sub)] as PdfArray;
                if (!TryNormalisedRect(stored, out var s)) continue;
                if (s.x0 < x0 - Tolerance || s.y0 < y0 - Tolerance ||
                    s.x1 > x1 + Tolerance || s.y1 > y1 + Tolerance)
                    throw new Core.PdfException(
                        $"§14.11.2: the new /MediaBox [{x0} {y0} {x1} {y1}] would not contain this " +
                        $"page's /{PdfPageBoxes.ToName(sub).Value} [{s.x0} {s.y0} {s.x1} {s.y1}]. " +
                        "Remove or move that box first.");
            }
        }

        /// <summary>The four boxes §14.11.2 constrains, in Table 30 order.</summary>
        private static PdfPageBoxKind[] SubBoxes => new[]
        {
            PdfPageBoxKind.CropBox, PdfPageBoxKind.BleedBox,
            PdfPageBoxKind.TrimBox, PdfPageBoxKind.ArtBox,
        };

        /// <summary>
        /// Containment slack, in points. A rectangle is written with full <c>double</c> precision and
        /// read back through <c>PdfNumber</c>, so an exactly-coincident edge must not fail the test
        /// it just passed; a thousandth of a point is far below any meaningful prepress tolerance.
        /// </summary>
        private const double Tolerance = 1e-3;

        /// <summary>A four-number rectangle, normalised per §7.9.5 (either corner pair may come first).</summary>
        private static bool TryNormalisedRect(PdfArray rect, out (double x0, double y0, double x1, double y1) r)
        {
            r = default;
            if (rect == null || rect.Count < 4) return false;

            var v = new double[4];
            for (int i = 0; i < 4; i++)
            {
                if (!(rect[i] is PdfNumber n)) return false;
                v[i] = n.DoubleValue;
            }
            r = (Math.Min(v[0], v[2]), Math.Min(v[1], v[3]),
                 Math.Max(v[0], v[2]), Math.Max(v[1], v[3]));
            return r.x1 > r.x0 && r.y1 > r.y0;
        }

        /// <summary>
        /// Whether a <c>/Pages</c> ancestor supplies a <c>/MediaBox</c> this page would fall back to
        /// (§7.7.3.4 makes it one of the four inheritable attributes).
        /// </summary>
        /// <remarks>
        /// Bounded by <see cref="PdfLimits.MaxPageTreeDepth"/> and by a visited set: a <c>/Parent</c>
        /// chain is caller-controlled and a ring would otherwise spin here.
        /// <para>The depth cap alone already bounds a ring and gives the same answer (a ring that
        /// held a <c>/MediaBox</c> returns on its first visit), so removing the visited set is an
        /// equivalent mutation: it only ends a ring after its length instead of after the cap.</para>
        /// </remarks>
        private static bool InheritsMediaBox(PdfPage page)
        {
            var doc = page.Document;
            var node = doc?.ResolveObject(page.Dictionary[PdfName.Parent]) as PdfDictionary;
            var visited = new HashSet<PdfDictionary>(Core.Objects.PdfReferenceEquality.Instance);

            for (int depth = 0; node != null && depth < PdfLimits.MaxPageTreeDepth; depth++)
            {
                if (!visited.Add(node)) return false;
                if (doc.ResolveObject(node[PdfName.MediaBox]) is PdfArray) return true;
                node = doc.ResolveObject(node[PdfName.Parent]) as PdfDictionary;
            }
            return false;
        }

        // One implementation, in PdfHelpers.ValidatePageIndex — three byte-identical copies
        // existed, including the interpolated message. nameof resolves to the same literal
        // there, so the exception ParamName is unchanged.
        private static void ValidatePageIndex(PdfDocument doc, int pageIndex)
            => PdfHelpers.ValidatePageIndex(doc, pageIndex);

        // One implementation, in Core.Objects.PdfDictionary.CloneShallow.
        private static PdfDictionary CloneDictionary(PdfDictionary source) => PdfDictionary.CloneShallow(source);

        // One implementation, in Core.Writing.PdfTrailerBuilder.
        private static PdfDictionary BuildTrailer(PdfDictionary original, int newSize)
            => PdfTrailerBuilder.ForUpdate(original, newSize);
    }
}

