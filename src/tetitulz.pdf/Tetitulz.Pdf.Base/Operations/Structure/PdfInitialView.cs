// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base.Operations.Structure
{
    /// <summary>
    /// §12.2 Table 150 viewer preferences to write. Every member is nullable and
    /// <see langword="null"/> means <b>leave whatever the document has</b> — including leaving it
    /// absent.
    /// </summary>
    /// <remarks>
    /// A separate type from the read-side <see cref="PdfViewerPreferences"/> on purpose: that one is
    /// get-only with an <c>internal</c> constructor, so a caller cannot mutate what it read, and
    /// widening it would make "the value in the file" and "the value I want" the same object.
    /// </remarks>
    public sealed class ViewerPreferenceSettings
    {
        /// <summary>Hide the viewer's tool bars.</summary>
        public bool? HideToolbar { get; set; }
        /// <summary>Hide the viewer's menu bar.</summary>
        public bool? HideMenubar { get; set; }
        /// <summary>Hide the viewer's UI elements other than the page.</summary>
        public bool? HideWindowUI { get; set; }
        /// <summary>Resize the window to the first displayed page.</summary>
        public bool? FitWindow { get; set; }
        /// <summary>Centre the window on the screen.</summary>
        public bool? CenterWindow { get; set; }
        /// <summary>Show the document title rather than the file name in the title bar.</summary>
        public bool? DisplayDocTitle { get; set; }
        /// <summary>Pick the paper tray by page size.</summary>
        public bool? PickTrayByPDFSize { get; set; }

        /// <summary>Page mode on leaving full-screen: <c>UseNone</c>, <c>UseOutlines</c>, <c>UseThumbs</c>, <c>UseOC</c>.</summary>
        public string NonFullScreenPageMode { get; set; }

        /// <summary>
        /// Predominant reading order: <c>L2R</c> or <c>R2L</c>.
        /// </summary>
        /// <remarks>
        /// <b><c>R2L</c> is what a Hebrew or Arabic document wants</b>, and it affects page
        /// <i>layout</i> only — which side a two-up spread starts on and which way page-forward
        /// goes. It does not reorder text, and it must not be confused with the bidi resolution
        /// <c>Core.Text.UnicodeBidi</c> performs inside a line.
        /// </remarks>
        public string Direction { get; set; }

        /// <summary>Page box to display: <c>MediaBox</c>, <c>CropBox</c>, <c>BleedBox</c>, <c>TrimBox</c>, <c>ArtBox</c>.</summary>
        public string ViewArea { get; set; }
        /// <summary>Page box to clip the display to.</summary>
        public string ViewClip { get; set; }
        /// <summary>Page box to print.</summary>
        public string PrintArea { get; set; }
        /// <summary>Page box to clip printing to.</summary>
        public string PrintClip { get; set; }
        /// <summary>Print scaling: <c>None</c> or <c>AppDefault</c>.</summary>
        public string PrintScaling { get; set; }
        /// <summary>Duplex handling: <c>Simplex</c>, <c>DuplexFlipShortEdge</c>, <c>DuplexFlipLongEdge</c>.</summary>
        public string Duplex { get; set; }
        /// <summary>Default number of copies. Must be positive.</summary>
        public int? NumCopies { get; set; }
    }

    /// <summary>How a document opens: §7.7.2 catalog entries plus §12.2 viewer preferences.</summary>
    public sealed class InitialViewSettings
    {
        /// <summary>
        /// §7.7.2 <c>/PageMode</c>: <c>UseNone</c>, <c>UseOutlines</c>, <c>UseThumbs</c>,
        /// <c>FullScreen</c>, <c>UseOC</c>, <c>UseAttachments</c>.
        /// </summary>
        public string PageMode { get; set; }

        /// <summary>
        /// §7.7.2 <c>/PageLayout</c>: <c>SinglePage</c>, <c>OneColumn</c>, <c>TwoColumnLeft</c>,
        /// <c>TwoColumnRight</c>, <c>TwoPageLeft</c>, <c>TwoPageRight</c>.
        /// </summary>
        public string PageLayout { get; set; }

        /// <summary>
        /// Zero-based page to open at. Writes <c>/OpenAction</c> as an <c>/XYZ</c> destination.
        /// Null leaves any existing <c>/OpenAction</c> alone.
        /// </summary>
        public int? OpenAtPageIndex { get; set; }

        /// <summary>Left edge of the opening view, in points. Null means "keep the viewer's".</summary>
        public double? OpenAtLeft { get; set; }
        /// <summary>Top edge of the opening view, in points. Null means "keep the viewer's".</summary>
        public double? OpenAtTop { get; set; }
        /// <summary>Opening zoom (1.0 = 100%). Null means "keep the viewer's".</summary>
        public double? OpenAtZoom { get; set; }

        /// <summary>Viewer preferences to write, or null to leave them alone.</summary>
        public ViewerPreferenceSettings ViewerPreferences { get; set; }

        /// <summary>
        /// Replace an existing <c>/OpenAction</c> that is an <b>action dictionary</b> rather than a
        /// destination.
        /// </summary>
        /// <remarks>
        /// Defaults to <see langword="false"/> and the write is <b>refused</b> instead, because an
        /// action can be a JavaScript entry point or a launch action that the document depends on,
        /// and replacing it with "go to page 3" would silently remove behaviour a caller did not
        /// know was there. An existing <i>destination</i> is replaced without asking: that is the
        /// same kind of thing being written.
        /// </remarks>
        public bool ReplaceExistingOpenAction { get; set; }
    }

    /// <summary>What <see cref="PdfInitialView.SetInitialView"/> did.</summary>
    public sealed class InitialViewResult
    {
        internal InitialViewResult(IReadOnlyList<string> keysWritten, IReadOnlyList<string> warnings)
        {
            KeysWritten = keysWritten;
            Warnings = warnings;
        }

        /// <summary>The catalog keys the revision actually carries, e.g. <c>/PageMode</c>.</summary>
        public IReadOnlyList<string> KeysWritten { get; }

        /// <summary>What was asked for and not done, each with its reason. Empty on a clean write.</summary>
        public IReadOnlyList<string> Warnings { get; }
    }

    /// <summary>
    /// Authors how a document opens — <c>/PageMode</c>, <c>/PageLayout</c>, <c>/OpenAction</c> and
    /// <c>/ViewerPreferences</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>All four were readable and none was writable.</b> <c>PdfViewerPreferences</c> is
    /// get-only with an <c>internal</c> constructor, <c>PdfCatalog</c> exposes no <c>/PageMode</c>,
    /// <c>/PageLayout</c> or <c>/OpenAction</c> at all, and the one viewer-preference key anything
    /// in the tree ever wrote was <c>/DisplayDocTitle</c> — as a side effect of setting an
    /// accessibility title, to satisfy PDF/UA-1 UA04. A caller wanting any of the rest had to reach
    /// <c>doc.Catalog.Dictionary</c> and hand-roll an incremental update.</para>
    ///
    /// <para><b>Names are validated against §7.7.2 / §12.2 and a wrong one is refused, not
    /// written.</b> These are enumerated name values: a viewer that does not recognise one falls
    /// back to its default, so a typo produces a document that simply ignores the setting — the
    /// silent no-op this library keeps finding elsewhere. The valid sets are small and closed, so
    /// there is no cost to checking.</para>
    /// </remarks>
    public static class PdfInitialView
    {
        /// <summary>Reads the current initial-view settings.</summary>
        /// <param name="input">The PDF to read. Not disposed.</param>
        /// <returns>Never null; members are null where the document says nothing.</returns>
        public static InitialViewSettings GetInitialView(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            EncryptedRead.Refuse(doc, "the initial view", null);

            var settings = new InitialViewSettings();
            var catalog = doc.Catalog?.Dictionary;
            if (catalog == null) return settings;

            settings.PageMode = (doc.ResolveObject(catalog[PdfName.PageMode]) as PdfName)?.Value;
            settings.PageLayout = (doc.ResolveObject(catalog[PdfName.PageLayout]) as PdfName)?.Value;

            var prefs = doc.Catalog.GetViewerPreferences();
            if (prefs != null)
            {
                settings.ViewerPreferences = new ViewerPreferenceSettings
                {
                    HideToolbar = prefs.HideToolbar,
                    HideMenubar = prefs.HideMenubar,
                    HideWindowUI = prefs.HideWindowUI,
                    FitWindow = prefs.FitWindow,
                    CenterWindow = prefs.CenterWindow,
                    DisplayDocTitle = prefs.DisplayDocTitle,
                    PickTrayByPDFSize = prefs.PickTrayByPDFSize,
                    NonFullScreenPageMode = prefs.NonFullScreenPageMode,
                    Direction = prefs.Direction,
                    ViewArea = prefs.ViewArea,
                    ViewClip = prefs.ViewClip,
                    PrintArea = prefs.PrintArea,
                    PrintClip = prefs.PrintClip,
                    PrintScaling = prefs.PrintScaling,
                    Duplex = prefs.Duplex,
                    NumCopies = prefs.NumCopies,
                };
            }

            // /OpenAction as an /XYZ destination: [pageRef /XYZ left top zoom]. An action
            // dictionary is deliberately not decoded into these fields — it is not a destination,
            // and reporting one as "opens at page 0" would be a lie the writer would then act on.
            if (doc.ResolveObject(catalog[PdfName.OpenAction]) is PdfArray dest && dest.Count >= 1)
            {
                // The one §12.3.2.2 page lookup. A private copy here matched a page reference only,
                // and without that lookup's refusal of object 0 — the clause it exists for.
                settings.OpenAtPageIndex = PdfDestinationPage.IndexFromDestArray(doc, dest);
                // Only /XYZ carries a left, a top and a zoom (§12.3.2.2 Table 151). /FitH, /FitBH and
                // /FitR name a top, read through the one destination reader; no other form names a
                // zoom — a /FitR's right and bottom read as a top and a zoom turned it into an extreme
                // /XYZ on the round trip through SetInitialView.
                if (dest.Count >= 2 && doc.ResolveObject(dest[1]) is PdfName form && form.Equals(PdfName.XYZ))
                {
                    if (dest.Count >= 5)
                    {
                        settings.OpenAtLeft = (doc.ResolveObject(dest[2]) as PdfNumber)?.DoubleValue;
                        settings.OpenAtTop  = (doc.ResolveObject(dest[3]) as PdfNumber)?.DoubleValue;
                        settings.OpenAtZoom = (doc.ResolveObject(dest[4]) as PdfNumber)?.DoubleValue;
                    }
                }
                else
                {
                    settings.OpenAtTop = PdfDestinationPage.TopFromDestArray(doc, dest);
                }
            }

            return settings;
        }

        /// <summary>Writes the initial-view settings as one incremental revision.</summary>
        /// <param name="input">Source PDF. Not disposed.</param>
        /// <param name="output">Receives the original bytes plus one appended revision.</param>
        /// <param name="settings">What to write. Null members are left alone.</param>
        /// <exception cref="Core.PdfException">
        /// A name value is not one §7.7.2 or §12.2 defines;
        /// <see cref="ViewerPreferenceSettings.NumCopies"/> is not positive; the page index is out
        /// of range; or an existing <c>/OpenAction</c> is an action dictionary and
        /// <see cref="InitialViewSettings.ReplaceExistingOpenAction"/> is false.
        /// </exception>
        public static InitialViewResult SetInitialView(Stream input, Stream output, InitialViewSettings settings)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            EncryptedRevision.Refuse(doc, "set the initial view of", "the revised catalog");

            int catalogObjNum = CatalogObject.NumberOrRefuse(doc.Trailer, "the revised catalog");
            if (doc.Catalog == null)
                throw new Core.PdfException("The document's catalog cannot be read.");

            var written = new List<string>();
            var warnings = new List<string>();
            var objects = new List<IndirectObject>();
            var catalog = doc.Catalog.Dictionary;
            var updated = PdfPageContentAppender.CloneDictionary(catalog);

            if (settings.PageMode != null)
            {
                RequireName("PageMode", settings.PageMode, PageModes);
                updated.Set(PdfName.PageMode, new PdfName(settings.PageMode));
                written.Add("/PageMode");
            }

            if (settings.PageLayout != null)
            {
                RequireName("PageLayout", settings.PageLayout, PageLayouts);
                updated.Set(PdfName.PageLayout, new PdfName(settings.PageLayout));
                written.Add("/PageLayout");
            }

            if (settings.OpenAtPageIndex.HasValue)
            {
                int pageIndex = settings.OpenAtPageIndex.Value;
                if (pageIndex < 0 || pageIndex >= doc.Pages.Count)
                    throw new ArgumentOutOfRangeException(nameof(settings),
                        "OpenAtPageIndex " + pageIndex + " is outside the document's " +
                        doc.Pages.Count + " page(s).");

                // §12.3.2.2: an /OpenAction may be a destination OR an action dictionary. Replacing
                // an action is refused by default — it can be a JavaScript or launch entry point the
                // document depends on, and "go to page 3" would silently remove it.
                if (doc.ResolveObject(catalog[PdfName.OpenAction]) is PdfDictionary existingAction &&
                    !settings.ReplaceExistingOpenAction)
                {
                    string kind = (doc.ResolveObject(existingAction[PdfName.S]) as PdfName)?.Value ?? "unknown";
                    throw new Core.PdfException(
                        "The document's /OpenAction is an action dictionary (/S /" + kind + "), not a " +
                        "destination. Replacing it would remove behaviour the document may depend on; " +
                        "set InitialViewSettings.ReplaceExistingOpenAction to do it anyway.");
                }

                updated.Set(PdfName.OpenAction, XyzDestination.Build(
                    XyzDestination.PageOf(doc, pageIndex), settings.OpenAtLeft, settings.OpenAtTop, settings.OpenAtZoom));
                written.Add("/OpenAction");
            }

            var prefs = settings.ViewerPreferences;
            if (prefs != null)
            {
                if (MergeViewerPreferences(doc, catalog, prefs, objects, updated) > 0) written.Add("/ViewerPreferences");
                else warnings.Add("ViewerPreferences was supplied but every member was null, so nothing was written.");
            }

            if (written.Count == 0)
                warnings.Add("Nothing was written: every setting was null.");

            updated.ObjectNumber = catalogObjNum;
            updated.GenerationNumber = 0;
            objects.Add(new IndirectObject(catalogObjNum, 0, updated));

            PdfPageContentAppender.AppendRevision(
                ms, output, doc, objects, doc.CrossReferenceTable.GetMaxObjectNumber() + 1);

            return new InitialViewResult(written, warnings);
        }

        // ----------------------------------------------------------------

        /// <summary>
        /// Merges the requested preferences into the document's existing <c>/ViewerPreferences</c>,
        /// preserving keys this API has no property for.
        /// </summary>
        /// <remarks>
        /// <b>Cloned, not rebuilt</b> — the same rule <c>WriteMetadata</c>'s <c>/Info</c> had to
        /// learn: §12.2 Table 150 has entries this type does not model (<c>/PrintPageRange</c>,
        /// <c>/Enforce</c>), and rebuilding would delete them. When the entry is an indirect
        /// reference the referenced object is superseded in place, so a dictionary shared with
        /// anything else keeps its identity.
        /// </remarks>
        /// <returns>How many keys were written; nothing is staged when it is 0.</returns>
        private static int MergeViewerPreferences(
            PdfDocument doc, PdfDictionary catalog, ViewerPreferenceSettings prefs,
            List<IndirectObject> objects, PdfDictionary updatedCatalog)
        {
            PdfObject existingObj = catalog[PdfName.ViewerPreferences];
            var existing = doc.ResolveObject(existingObj) as PdfDictionary;
            var dict = existing == null
                ? new PdfDictionary()
                : PdfDictionary.CloneShallow(existing);

            int keysWritten = 0;
            keysWritten += SetBool(dict, "HideToolbar", prefs.HideToolbar);
            keysWritten += SetBool(dict, "HideMenubar", prefs.HideMenubar);
            keysWritten += SetBool(dict, "HideWindowUI", prefs.HideWindowUI);
            keysWritten += SetBool(dict, "FitWindow", prefs.FitWindow);
            keysWritten += SetBool(dict, "CenterWindow", prefs.CenterWindow);
            keysWritten += SetBool(dict, "DisplayDocTitle", prefs.DisplayDocTitle);
            keysWritten += SetBool(dict, "PickTrayByPDFSize", prefs.PickTrayByPDFSize);

            keysWritten += SetName(dict, "NonFullScreenPageMode", prefs.NonFullScreenPageMode, NonFullScreenPageModes);
            keysWritten += SetName(dict, "Direction", prefs.Direction, Directions);
            keysWritten += SetPageBox(dict, PdfName.ViewArea, prefs.ViewArea);
            keysWritten += SetPageBox(dict, PdfName.ViewClip, prefs.ViewClip);
            keysWritten += SetPageBox(dict, PdfName.PrintArea, prefs.PrintArea);
            keysWritten += SetPageBox(dict, PdfName.PrintClip, prefs.PrintClip);
            keysWritten += SetName(dict, "PrintScaling", prefs.PrintScaling, PrintScalings);
            keysWritten += SetName(dict, "Duplex", prefs.Duplex, Duplexes);

            if (prefs.NumCopies.HasValue)
            {
                if (prefs.NumCopies.Value < 1)
                    throw new Core.PdfException(
                        "/NumCopies must be a positive integer (§12.2 Table 150); " +
                        prefs.NumCopies.Value + " was requested.");
                dict.Set(PdfName.NumCopies, new PdfNumber(prefs.NumCopies.Value));
                keysWritten++;
            }

            if (keysWritten == 0) return 0;

            if (existingObj is PdfReference prefsRef)
            {
                dict.ObjectNumber = prefsRef.ReferenceObjectNumber;
                dict.GenerationNumber = 0;
                objects.Add(new IndirectObject(prefsRef.ReferenceObjectNumber, 0, dict));
            }
            else
            {
                updatedCatalog.Set(PdfName.ViewerPreferences, dict);
            }

            return keysWritten;
        }

        /// <summary>
        /// Writes a page-box name — <c>/ViewArea</c> and its three siblings — through
        /// <see cref="PdfPageBoxes"/>, the one table of the five box names, which this class used
        /// to keep a second copy of.
        /// </summary>
        private static int SetPageBox(PdfDictionary dict, PdfName key, string value)
        {
            if (value == null) return 0;
            if (!PdfPageBoxes.TryParse(value, out PdfPageBoxKind kind))
                throw new Core.PdfException(
                    "/" + key.Value + " must be one of MediaBox, CropBox, BleedBox, TrimBox, ArtBox " +
                    "(PDF names are case-sensitive, §7.3.5); \"" + value + "\" is not.");
            dict.Set(key, PdfPageBoxes.ToName(kind));
            return 1;
        }

        private static int SetBool(PdfDictionary dict, string key, bool? value)
        {
            if (!value.HasValue) return 0;
            dict.Set(new PdfName(key), value.Value ? PdfBoolean.True : PdfBoolean.False);
            return 1;
        }

        private static int SetName(PdfDictionary dict, string key, string value, string[] valid)
        {
            if (value == null) return 0;
            RequireName(key, value, valid);
            dict.Set(new PdfName(key), new PdfName(value));
            return 1;
        }

        /// <summary>
        /// Refuses a name outside the enumerated set §7.7.2 / §12.2 defines.
        /// </summary>
        /// <remarks>
        /// A viewer that does not recognise one of these falls back to its default, so a typo yields
        /// a document that silently ignores the setting — and a caller with no way to tell that from
        /// a viewer that simply does not honour it. The sets are small and closed; checking costs
        /// nothing and turns a silent no-op into an exception naming the valid values.
        /// </remarks>
        private static void RequireName(string key, string value, string[] valid)
        {
            for (int i = 0; i < valid.Length; i++)
                if (string.Equals(valid[i], value, StringComparison.Ordinal)) return;

            throw new Core.PdfException(
                "/" + key + " must be one of " + string.Join(", ", valid) +
                " (PDF names are case-sensitive, §7.3.5); \"" + value + "\" is not.");
        }

        // The enumerated name values, from ISO 32000-1 §7.7.2 Table 28 and §12.2 Table 150.
        private static readonly string[] PageModes =
            { "UseNone", "UseOutlines", "UseThumbs", "FullScreen", "UseOC", "UseAttachments" };

        private static readonly string[] PageLayouts =
            { "SinglePage", "OneColumn", "TwoColumnLeft", "TwoColumnRight", "TwoPageLeft", "TwoPageRight" };

        /// <summary>§12.2: FullScreen and UseAttachments are not permitted here.</summary>
        private static readonly string[] NonFullScreenPageModes =
            { "UseNone", "UseOutlines", "UseThumbs", "UseOC" };

        private static readonly string[] Directions = { "L2R", "R2L" };

        private static readonly string[] PrintScalings = { "None", "AppDefault" };

        private static readonly string[] Duplexes =
            { "Simplex", "DuplexFlipShortEdge", "DuplexFlipLongEdge" };
    }
}
