// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Operations.Metadata;

namespace Tetitulz.Pdf.Base.Operations.Forms
{
    /// <summary>
    /// Flattens AcroForm fields: renders each widget's /AP/N appearance stream
    /// onto the page as static content, then removes the form fields and /AcroForm
    /// from the document via an incremental PDF update.
    /// </summary>
    public static class PdfFormFlattener
    {
        /// <summary>
        /// Flattens all form fields in the PDF, with the default appearance-generation settings.
        /// </summary>
        /// <param name="input">Source PDF stream.</param>
        /// <param name="output">Output PDF stream.</param>
        /// <remarks>
        /// An appearance the document's <c>/NeedAppearances</c> flag has marked stale is regenerated
        /// rather than stamped blank (§12.7.3.3). The overload taking <see cref="FormFillOptions"/> is how a
        /// caller supplies a font for values outside a single-byte encoding, or turns the
        /// regeneration off.
        /// </remarks>
        public static void Flatten(Stream input, Stream output) => Flatten(input, output, null);

        /// <summary>
        /// Flattens all form fields, regenerating any appearance the document's
        /// <c>/NeedAppearances</c> flag has marked stale (§12.7.3.3).
        /// </summary>
        /// <param name="input">Source PDF stream.</param>
        /// <param name="output">Output PDF stream.</param>
        /// <param name="options">
        /// Appearance-generation settings — chiefly <see cref="FormFillOptions.FontProvider"/>, which
        /// a value outside a single-byte encoding needs. <see langword="null"/> takes the defaults,
        /// and <see cref="FormFillOptions.GenerateAppearances"/> set to <c>false</c> stamps whatever
        /// the file carries.
        /// </param>
        /// <remarks>
        /// <para><b>Why a flattener has to care about the flag at all.</b> §12.7.3.3 lets a producer
        /// write <c>/V</c>, set <c>/NeedAppearances</c> and leave the blank template's <c>/AP</c>
        /// alone, delegating the drawing to the viewer. Stamping that appearance bakes an
        /// <b>empty field</b> into the page and then removes the <c>/AcroForm</c> that said the value
        /// was there — so unlike the render-time case, which is repeatable, this one destroys the
        /// only remaining evidence of what the field said.</para>
        ///
        /// <para>The staleness decision is <c>AnnotationFallbackModel.WidgetAppearanceIsStale</c>,
        /// the same one both renderers ask, so a page cannot flatten to something other than what it
        /// renders as. What differs is the repair: a renderer <i>draws</i> the value through its
        /// synthetic fallback, while a flattener must <i>write</i> a real appearance stream, which is
        /// <see cref="FormAppearance"/> — the generator <see cref="PdfFormFiller"/> already uses, so
        /// a flattened field and a filled one are drawn by one implementation.</para>
        ///
        /// <para><b>A failed regeneration keeps the file's own appearance</b> rather than dropping
        /// the widget, and says so through <c>DocumentEditDiagnostics</c>: the commonest cause is a
        /// value needing glyphs no licensed font was supplied for, and a blank stamp is still better
        /// than a hole. The reason is reported in every such case, never swallowed.</para>
        /// </remarks>
        public static void Flatten(Stream input, Stream output, FormFillOptions options)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);

            // Checked BEFORE the AcroForm, for the reason PdfFormFiller states at its own guard:
            // an encrypted document loads no catalog, so its AcroForm always reads as absent and
            // the copy-through below would silently return the input untouched — a workflow that
            // flattens a filled form before archiving it would archive a still-editable one and be
            // told nothing. Fail closed with the reason instead.
            if (doc.IsEncrypted)
                throw new Core.PdfEncryptedDocumentException(
                    "Cannot flatten an encrypted document here: the stamped appearance content " +
                    "would be written in clear text where the reader expects per-object " +
                    "ciphertext, and this assembly cannot decrypt. Decrypt first with " +
                    "Tetitulz.Pdf.Encryption.PdfDecryptor, or run the flatten inside " +
                    "Tetitulz.Pdf.Encryption.PdfEncryptedRoundTrip.Run, which decrypts, operates " +
                    "and re-encrypts.");

            if (doc.AcroForm == null)
            {
                // Nothing to flatten — just copy through. Reachable only for a genuinely
                // form-less document now that encryption is refused above.
                ms.Position = 0;
                ms.CopyTo(output);
                return;
            }

            // Refused before anything is flattened: over a direct /Root the widgets were stamped and
            // /AcroForm left in the catalog, listing fields whose widgets were gone.
            int catalogObjNum = CatalogObject.NumberOrRefuse(doc.Trailer, "the flattened form");

            int maxObjNum = doc.CrossReferenceTable.GetMaxObjectNumber();
            int nextObjNum = maxObjNum + 1;
            var objects = new List<IndirectObject>();

            // Collect widget annotations grouped by page.
            var widgetsByPage = CollectWidgets(doc);

            // §12.7.3.3 — document-scoped, resolved once, through the same reader both renderers
            // use, so the flattened page cannot disagree with the rendered one about the flag.
            var effectiveOptions = options ?? new FormFillOptions();
            bool needAppearances = effectiveOptions.GenerateAppearances
                                   && AnnotationFallbackModel.FormNeedsAppearances(doc);

            // For each page that has widgets, stamp the /AP/N stream and remove the /Annots entry.
            // One q/Q pair for the whole revision, shared by every page it brackets.
            var isolation = new Documents.PdfPageContentAppender.StateIsolation();
            var xobjects = new StampedXObjects(doc);
            foreach (var page in doc.Pages)
            {
                if (!widgetsByPage.TryGetValue(page.PageIndex, out var widgets) || widgets.Count == 0)
                    continue;

                StampWidgetsOnPage(page, widgets, doc, ref nextObjNum, objects,
                                   needAppearances, effectiveOptions, isolation, xobjects);
            }
            xobjects.Write(objects);

            // Update the catalog to remove /AcroForm.
            RemoveAcroForm(doc, catalogObjNum, objects);

            int newSize = nextObjNum;
            PdfPageContentAppender.AppendRevision(ms, output, doc, objects, newSize);
        }

        // ----------------------------------------------------------------
        //  Widget collection
        // ----------------------------------------------------------------

        /// <summary>
        /// Returns a map of page index → list of widget annotation dictionaries.
        /// Widgets can appear either in a field's /Kids array (merged widget/field)
        /// or directly as annotations in the page's /Annots array.
        /// </summary>
        private static Dictionary<int, List<PdfDictionary>> CollectWidgets(PdfDocument doc)
        {
            var result = new Dictionary<int, List<PdfDictionary>>();

            // Through the one annotation walk: it resolves the entry and its /Subtype (GetName does
            // not follow a reference, so `/Subtype 12 0 R` read as absent and the widget was never
            // stamped while RemoveAcroForm still ran), and it bounds the entries the pages name
            // together — pages sharing one /Annots array stamped every widget on every page.
            var report = new Extraction.AnnotationWalkReport();
            foreach (var (pageIndex, annotObj, _) in Extraction.AnnotationSubtypeWalk.BySubtype(doc, report, PdfAnnotationSubtypes.Widget))
            {
                if (!result.TryGetValue(pageIndex, out var list)) result[pageIndex] = list = new List<PdfDictionary>();
                list.Add(annotObj);
            }
            if (report.Message is string cut)
                DocumentEditDiagnostics.Report("PdfFormFlattener.Flatten", cut + ", and their widgets were not flattened");

            return result;
        }

        // ----------------------------------------------------------------
        //  Stamp appearances onto page
        // ----------------------------------------------------------------

        /// <summary>
        /// Whether the page does not show <paramref name="widget"/> on screen: its <c>/F</c> sets
        /// Hidden or NoView (§12.5.3), or its <c>/OC</c> is off in the default configuration — the
        /// renderers' own two rules.
        /// </summary>
        /// <param name="widget">The widget annotation.</param>
        /// <param name="doc">Resolves the flags and the optional content.</param>
        /// <param name="hiddenOcgs">The document's hidden groups, built at the first widget with an <c>/OC</c>.</param>
        private static bool WidgetIsNotShown(PdfDictionary widget, PdfDocument doc, ref HashSet<int> hiddenOcgs)
        {
            var flagsNum = PdfResolve.Number(doc, widget, PdfName.F);
            if (flagsNum != null && PdfAnnotationFlags.IsHiddenOnScreen(PdfAnnotationFlags.FromNumber(flagsNum) ?? 0))
                return true;
            if (widget[PdfName.OC] == null) return false;
            hiddenOcgs ??= PdfPageRenderer.BuildHiddenOcgSet(doc);
            return ContentStreamInterpreter.IsAnnotationOcHidden(widget, hiddenOcgs, doc);
        }

        private static void StampWidgetsOnPage(
            PdfPage page,
            List<PdfDictionary> widgets,
            PdfDocument doc,
            ref int nextObjNum,
            List<IndirectObject> objects,
            bool needAppearances,
            FormFillOptions options,
            Documents.PdfPageContentAppender.StateIsolation isolation,
            StampedXObjects xobjects)
        {
            int pageObjNum = page.Dictionary.ObjectNumber;
            if (pageObjNum <= 0)
            {
                // §7.7.3.2 makes /Kids an array of indirect references, so a page written directly
                // breaks the rule — and an incremental update replaces objects by number, which this
                // page does not have. Its widgets cannot be stamped. Said rather than skipped in
                // silence, because RemoveAcroForm still runs: they stay on the page, interactive,
                // with no form behind them.
                DocumentEditDiagnostics.Report("PdfFormFlattener.Flatten",
                    "page " + (page.PageIndex + 1) + " is written directly in its parent's /Kids rather " +
                    "than as an indirect object, so an incremental update cannot replace it; its " +
                    widgets.Count + " widget(s) were not flattened and remain on the page with no /AcroForm");
                return;
            }

            // Widgets whose appearance really was baked into the page content, and widgets that show
            // nothing. Only these may be dropped from /Annots — see the comment on the rebuild below.
            var stamped = new HashSet<PdfDictionary>();

            // Preserve existing content stream(s). Shared with PdfPageContentAppender, which is
            // where the resolve-before-you-branch reasoning lives: this copy had the identical
            // defect, and an indirect /Contents ARRAY was nested rather than spliced, so flattening
            // a form onto such a page erased everything already drawn on it.
            var newContents = PdfPageContentAppender.CopyExistingContents(doc, page.Dictionary);

            // The page's stamped forms, registered once after the loop where the page reads its
            // XObjects (StampedXObjects) — copying the page's /XObject for every widget was widgets
            // × entries on one page.
            var forms = new PdfDictionary();
            var takenNames = xobjects.TakenNames(page);

            HashSet<int> hiddenOcgs = null;
            int notShown = 0;
            foreach (var widget in widgets)
            {
                // A widget the page does not show — §12.5.3's Hidden or NoView, or in optional content
                // the default configuration turns off — leaves with the form and is not stamped:
                // stamping would make content that renders invisible part of the page.
                if (WidgetIsNotShown(widget, doc, ref hiddenOcgs))
                {
                    stamped.Add(widget);
                    notShown++;
                    continue;
                }

                // Get /AP/N appearance stream reference.
                var apDict = widget[PdfName.AP] as PdfDictionary;
                if (apDict == null) apDict = doc.ResolveObject(widget[PdfName.AP]) as PdfDictionary;
                if (apDict == null) continue;

                // A state /AS names that /N does not carry draws nothing (a check box's optional off
                // appearance), so the widget leaves with nothing stamped.
                if (doc.ResolveObject(apDict[PdfName.N]) is PdfDictionary states && states is not PdfStream
                    && AnnotationAppearance.TryNamedState(doc, widget, states, out var namedState)
                    && namedState == null)
                {
                    stamped.Add(widget);
                    continue;
                }

                var apStream = AnnotationAppearanceStamp.ResolveNormalAppearance(
                    widget, apDict, doc, ref nextObjNum, objects, out var normalRef);
                if (apStream == null || normalRef == null) continue;

                // §12.7.3.3 — the appearance we just resolved may be the blank template's, with the
                // real value sitting in /V. Stamping it would bake an empty field into the page and
                // then delete the /AcroForm that said otherwise, so this is the one place the flag
                // is not merely a rendering hint.
                string staleReason = AnnotationFallbackModel.WidgetAppearanceIsStale(
                    widget, doc, apStream, needAppearances);
                if (staleReason != null)
                {
                    if (TryRegenerateAppearance(widget, doc, options, ref nextObjNum, objects,
                                                out var fresh, out string why))
                    {
                        // The stream itself, not `doc.ResolveObject(reference)`: it exists only in
                        // `objects` — the revision being staged — so the document's xref cannot
                        // resolve it yet. Resolving would have yielded null and silently left the
                        // OLD appearance's /BBox and /Matrix driving the §12.5.5 placement below,
                        // which is a wrong scale rather than a visible failure.
                        normalRef = new PdfReference(fresh.ObjectNumber, 0);
                        apStream = fresh;
                        DocumentEditDiagnostics.Report("PdfFormFlattener.Flatten", staleReason);
                    }
                    else
                    {
                        // Keeping the file's own appearance is the lesser error: a blank stamp still
                        // shows the field's box, where dropping the widget would leave a hole. Never
                        // silent — the commonest cause is a value whose glyphs need a font the
                        // caller has licensed and not supplied.
                        DocumentEditDiagnostics.Report(
                            "PdfFormFlattener.Flatten",
                            staleReason + ", but the appearance could not be regenerated (" + why +
                            "), so the document's own — blank — appearance was stamped");
                    }
                }

                // Get the widget's rectangle on the page (in page coordinates).
                // Resolved: a null here `continue`s before the widget reaches `stamped`, and the
                // /Annots rebuild keeps every unstamped widget while RemoveAcroForm still runs —
                // an interactive widget with no /AcroForm to give it meaning, in a document the
                // caller believes is frozen.
                var rect = PdfResolve.Array(doc, widget, PdfName.Rect);
                if (rect == null || rect.Count < 4) continue;

                // Each corner resolved, as TryRegenerateAppearance below reads the same /Rect: an
                // indirect one read as 0, the stamp was clipped to nothing, and the widget was
                // still removed as flattened.
                double rx1 = PdfResolve.ArrayDouble(doc, rect, 0);
                double ry1 = PdfResolve.ArrayDouble(doc, rect, 1);
                double rx2 = PdfResolve.ArrayDouble(doc, rect, 2);
                double ry2 = PdfResolve.ArrayDouble(doc, rect, 3);
                double rw = Math.Abs(rx2 - rx1);
                double rh = Math.Abs(ry2 - ry1);
                double rx = Math.Min(rx1, rx2);
                double ry = Math.Min(ry1, ry2);

                // §12.5.5 algorithm A — appearance space onto the widget /Rect. Shared with
                // PdfAnnotationEditor.Flatten through AnnotationAppearanceStamp, because two
                // flatteners that place a form differently is a divergence nothing would report.
                string xobjName = "FlatXO" + nextObjNum;
                while (!takenNames.Add(xobjName)) xobjName += "_";
                var sb = new StringBuilder(AnnotationAppearanceStamp.BuildPlacement(
                    doc, apStream, xobjName, rx, ry, rw, rh));

                byte[] wrapBytes = Core.Text.Latin1Text.Encode(sb.ToString());
                int wrapObjNum = nextObjNum++;
                var wrapStream = new PdfStream();
                wrapStream.RawData = wrapBytes;
                wrapStream.Set(PdfName.Length, new PdfNumber(wrapBytes.Length));
                wrapStream.ObjectNumber = wrapObjNum;
                wrapStream.GenerationNumber = 0;
                objects.Add(new IndirectObject(wrapObjNum, 0, wrapStream));

                // The page's own content is bracketed in q … Q before the first stamp, so the stamps
                // are placed in the page's initial graphics state rather than in whatever transform
                // the page's content leaves in force. Only once, and only when something is stamped.
                if (stamped.Count == 0)
                    isolation.Wrap(newContents, ref nextObjNum, objects);
                newContents.Add(new PdfReference(wrapObjNum, 0));

                forms.Set(new PdfName(xobjName), normalRef);
                stamped.Add(widget);
            }
            if (notShown > 0)
                DocumentEditDiagnostics.Report("PdfFormFlattener.Flatten",
                    "page " + (page.PageIndex + 1) + ": " + notShown + " widget(s) the page does not show " +
                    "(hidden, no-view, or in optional content that is off) were removed with the form, not stamped");

            // Clone and update the page dictionary.
            var updatedPage = PdfPageContentAppender.CloneDictionary(page.Dictionary);
            updatedPage.Set(PdfName.Contents, newContents);
            if (forms.Count > 0) xobjects.Register(page, updatedPage, forms);

            // Rebuild /Annots keeping everything that was NOT baked into the content.
            //
            // Dropping the key outright would delete every annotation on the page: the /Link to a
            // terms page, /Text notes, /Popup, /Stamp — none of which flattening a form has any
            // business removing — plus any widget the loop above could not stamp, erasing rather
            // than flattening it. Only a widget whose
            // appearance is now in the page's own content stream may go.
            var keptAnnots = new PdfArray();
            var originalAnnots = page.GetAnnotations();
            if (originalAnnots != null)
            {
                for (int i = 0; i < originalAnnots.Count; i++)
                {
                    var entry = originalAnnots[i];
                    var annotDict = doc.ResolveObject(entry) as PdfDictionary;
                    if (annotDict != null && stamped.Contains(annotDict)) continue;
                    keptAnnots.Add(entry);
                }
            }

            var cleanPage = new PdfDictionary();
            foreach (var kvp in updatedPage)
            {
                if (kvp.Key == PdfName.Annots) continue;
                cleanPage.Set(kvp.Key, kvp.Value);
            }
            // An empty array and an absent key mean the same thing (§12.5.2), so the key is dropped
            // when nothing survived — which keeps the all-widgets-stamped output as it was.
            if (keptAnnots.Count > 0)
                cleanPage.Set(PdfName.Annots, keptAnnots);
            cleanPage.ObjectNumber = pageObjNum;
            cleanPage.GenerationNumber = 0;
            objects.Add(new IndirectObject(pageObjNum, 0, cleanPage));
        }

        // ----------------------------------------------------------------
        //  §12.7.3.3 appearance regeneration
        // ----------------------------------------------------------------

        /// <summary>
        /// Builds a fresh <c>/AP</c> <c>/N</c> for a widget whose stored appearance is stale.
        /// </summary>
        /// <param name="widget">The widget annotation whose appearance is being replaced.</param>
        /// <param name="doc">The open document, for the inheritable climbs and the AcroForm.</param>
        /// <param name="options">Appearance-generation settings; never null at this call site.</param>
        /// <param name="nextObjNum">Next free object number; advanced by whatever is written.</param>
        /// <param name="objects">Receives the appearance stream and any font object it needs.</param>
        /// <param name="appearance">
        /// The new <c>/AP</c> <c>/N</c> stream, on success. Handed back as the object rather than as
        /// a reference because it lives only in <paramref name="objects"/> until the revision is
        /// written — the document cannot resolve it, and the caller needs its <c>/BBox</c>.
        /// </param>
        /// <param name="why">Why no appearance could be built; set only when this returns false.</param>
        /// <returns>True when <paramref name="appearance"/> was produced.</returns>
        /// <remarks>
        /// <para><b>Every input is read through an implementation that already exists.</b> The
        /// inheritable climbs (<c>/V</c>, <c>/DA</c>, <c>/Ff</c>, <c>/MaxLen</c>, <c>/Q</c>) go
        /// through <c>AnnotationFallbackModel.ResolveWidgetEntry</c>, which is the one
        /// <c>/Parent</c> walk in the rendering path; the drawing is
        /// <see cref="FormAppearance.Build"/>, which is what <see cref="PdfFormFiller"/> calls. This
        /// method therefore contains no rule of its own — only the assembly of arguments — which is
        /// deliberate: a second reading of "which <c>/DA</c> governs this widget" is exactly how a
        /// flattened field and a filled one come to be drawn differently.</para>
        ///
        /// <para><b>The <c>/DA</c> and <c>/Q</c> AcroForm fallbacks are inside
        /// <c>InheritedDefaultAppearance</c> and <c>InheritedQuadding</c>, not here.</b>
        /// <c>PdfFormFiller</c> and <c>PdfFormReader</c> reach the same fallback step
        /// (<c>DefaultAppearanceOrFormDefault</c>, <c>QuaddingOrFormDefault</c>); only the climb
        /// before it differs, because they walk a field's arrival chain and this walks a widget's
        /// <c>/Parent</c> chain. The filler reads <c>document.AcroForm.Dictionary</c>, the same resolved
        /// dictionary, so it needs no copy of its own.</para>
        /// </remarks>
        private static bool TryRegenerateAppearance(
            PdfDictionary widget,
            PdfDocument doc,
            FormFillOptions options,
            ref int nextObjNum,
            List<IndirectObject> objects,
            out PdfStream appearance,
            out string why)
        {
            appearance = null;
            why = null;

            // What the widget SHOWS — a password field's value masked (Table 229 bit 14). This one
            // matters most: flattening makes the appearance permanent page content.
            string value = AnnotationFallbackModel.DisplayedWidgetValue(widget, doc);
            var da = options.HonorDefaultAppearance
                ? FormAppearance.ParseDefaultAppearance(
                      AnnotationFallbackModel.InheritedDefaultAppearance(widget, doc))
                : null;

            int flags = (int)((AnnotationFallbackModel.ResolveWidgetEntry(
                widget, doc, PdfName.Ff, o => o is PdfNumber) as PdfNumber)?.LongValue ?? 0);
            int maxLen = (int)((AnnotationFallbackModel.ResolveWidgetEntry(
                widget, doc, PdfName.MaxLen, o => o is PdfNumber) as PdfNumber)?.LongValue ?? 0);

            // §12.7.4.3, through the one reading the filler uses. The field type is resolved up the
            // widget's /Parent chain, as WidgetAppearanceIsStale resolved it to get here: this copy
            // tested the flag bits alone, so a choice widget carrying them was laid out as a text
            // field the filler would not have treated as one.
            bool isText = (AnnotationFallbackModel.ResolveWidgetEntry(
                widget, doc, PdfName.FT, o => o is PdfName) as PdfName)?.Value == PdfFieldTypes.Text;
            var layout = FieldLayout.For(isText, flags, maxLen,
                AnnotationFallbackModel.InheritedQuadding(widget, doc) ?? PdfQuadding.Left);

            var rect = PdfResolve.Array(doc, widget, PdfName.Rect);
            double width = 0, height = 0;
            if (rect != null && rect.Count >= 4)
            {
                width = Math.Abs(PdfResolve.ArrayDouble(doc, rect, 2) - PdfResolve.ArrayDouble(doc, rect, 0));
                height = Math.Abs(PdfResolve.ArrayDouble(doc, rect, 3) - PdfResolve.ArrayDouble(doc, rect, 1));
            }

            var acroFormDict = doc.Catalog == null
                ? null
                : doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]) as PdfDictionary;

            int next = nextObjNum;
            var built = FormAppearance.Build(
                value, width, height, WidgetRotation.Read(widget, doc.ResolveObject),
                da, layout, doc, acroFormDict, options,
                ref next, objects, out why, out _);
            nextObjNum = next;

            if (built == null || built.ObjectNumber <= 0)
            {
                why ??= "the appearance builder produced no stream";
                return false;
            }

            appearance = built;
            return true;
        }

        // ----------------------------------------------------------------
        //  Remove /AcroForm from catalog
        // ----------------------------------------------------------------

        private static void RemoveAcroForm(
            PdfDocument doc,
            int catalogObjNum,
            List<IndirectObject> objects)
        {
            var catalogDict = doc.Catalog?.Dictionary;
            if (catalogDict == null) return;

            var updated = new PdfDictionary();
            foreach (var kvp in catalogDict)
            {
                if (kvp.Key == PdfName.AcroForm) continue;
                updated.Set(kvp.Key, kvp.Value);
            }
            updated.ObjectNumber = catalogObjNum;
            updated.GenerationNumber = 0;
            objects.Add(new IndirectObject(catalogObjNum, 0, updated));
        }

        private static string Fmt(double v) => PdfRealFormat.Format(v, "F4");
    }
}
