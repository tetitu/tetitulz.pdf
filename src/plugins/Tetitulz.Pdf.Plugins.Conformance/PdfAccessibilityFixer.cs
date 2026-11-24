// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// Options controlling which accessibility fixes <see cref="PdfAccessibilityFixer"/> applies.
    /// </summary>
    public sealed class AccessibilityFixOptions
    {
        /// <summary>Add /Lang entry to catalog if missing. Default language tag to use.</summary>
        public string DefaultLanguage { get; set; } = "en-US";

        /// <summary>Add /Lang to catalog when absent.</summary>
        public bool AddLanguage { get; set; } = true;

        /// <summary>Add /DisplayDocTitle = true to /ViewerPreferences.</summary>
        public bool SetDisplayDocTitle { get; set; } = true;

        /// <summary>Add /TU (tooltip) to form fields that are missing one.</summary>
        public bool AddMissingTooltips { get; set; } = true;

        /// <summary>
        /// Template for the /TU text written to fields that are missing one.
        /// Use <c>"{name}"</c> as a placeholder for the field's /T (partial name).
        /// Non-Latin-1 text (Hebrew, Arabic, CJK, …) is preserved: the value is written as a
        /// UTF-16BE text string with a U+FEFF byte-order mark per ISO 32000-1 §7.9.2.2.
        ///
        /// <para>
        /// <b>What /TU is, and why anything written here is only a placeholder.</b> /TU is the
        /// field's <i>accessible name</i> (ISO 32000-1 Table 220; ISO 14289-1 §7.18.4) — the words
        /// a screen reader speaks when the user tabs onto the field, and often the only description
        /// of it they ever receive. A real accessible name says what the user is being asked for
        /// ("Date of birth, day/month/year"). What this template can produce is a mechanical
        /// transform of the field's /T <i>partial name</i>, which is an internal identifier chosen
        /// by whoever built the form: it is frequently abbreviated ("dob3"), sometimes a raw GUID,
        /// and never guaranteed to mean anything to a human.
        /// </para>
        /// <para>
        /// So the distinction that matters is: a /TU written from this template makes the document
        /// <i>pass</i> the PDF/UA-1 UA08 check, but it does not make the field <i>usable</i>. Every
        /// tooltip the fixer fabricates is therefore reported in
        /// <see cref="AccessibilityFixResult.Warnings"/>, naming the field, so the placeholder is
        /// visible as work still owed to a human author rather than silently banked as a fix.
        /// A partial name usually carries at least a hint of meaning, which is why deriving from
        /// /T is the default; where there is no /T at all the derived text would be pure noise, so
        /// it is written only under
        /// <see cref="AddPlaceholderTooltipsForUnnamedFields"/>.
        /// </para>
        /// </summary>
        public string DefaultTooltipTemplate { get; set; } = "{name}";

        /// <summary>
        /// Write a synthetic <c>"Field&lt;n&gt;"</c> tooltip onto terminal fields that have no /T
        /// partial name to derive one from. <b>Default <see langword="false"/>.</b>
        ///
        /// <para>
        /// A /TU of <c>"Field3"</c> tells a screen-reader user nothing; all it does is silence the
        /// UA08 violation that was the only remaining signal that this field has no accessible
        /// name. Leaving the violation in place is the more useful outcome, so by default such a
        /// field is skipped and reported in <see cref="AccessibilityFixResult.Warnings"/> instead.
        /// </para>
        /// <para>
        /// Enable this only when a caller genuinely wants placeholders — for example when the
        /// output is going straight into a remediation tool that will overwrite every /TU anyway,
        /// and the presence of the key matters more than its content.
        /// </para>
        /// </summary>
        public bool AddPlaceholderTooltipsForUnnamedFields { get; set; }

        /// <summary>
        /// Write <c>/Tabs /S</c> (structure tab order) on pages that carry annotations but do
        /// not declare it.
        /// <para>
        /// <b>Note on the name.</b> The page tab-order key defined by ISO 32000-1 Table 30 is
        /// <c>/Tabs</c>, not <c>/TabOrder</c> — there is no <c>/TabOrder</c> key in the PDF
        /// specification and no viewer honours one. This property keeps its original name for
        /// binary compatibility, but the fixer writes <c>/Tabs</c>.
        /// </para>
        /// </summary>
        public bool FixTabOrder { get; set; } = true;
    }

    /// <summary>
    /// Applies automated accessibility fixes to a PDF to improve PDF/UA-1 compliance.
    ///
    /// <para>Fixes applied (controlled via <see cref="AccessibilityFixOptions"/>):</para>
    /// <list type="bullet">
    ///   <item>Add /Lang to the document catalog when missing.</item>
    ///   <item>Set /ViewerPreferences /DisplayDocTitle = true.</item>
    ///   <item>Add /TU (tooltip / accessible name) to <b>terminal</b> form fields that lack one.</item>
    ///   <item>Set <c>/Tabs /S</c> on annotated pages that are missing it (ISO 32000-1 Table 30).</item>
    /// </list>
    ///
    /// <para>
    /// <b>Tooltips are placeholders, and are reported as such.</b> /TU is the field's accessible
    /// name — what a screen reader speaks. No automated tool can author one, so every /TU this
    /// fixer writes is a mechanical transform of the field's /T partial name and every one of them
    /// is listed in <see cref="AccessibilityFixResult.Warnings"/> naming the field. Where a field
    /// has no /T at all, nothing is written by default: see
    /// <see cref="AccessibilityFixOptions.AddPlaceholderTooltipsForUnnamedFields"/>.
    /// </para>
    ///
    /// <para>
    /// <b>Agreement with the checker.</b> Which dictionaries count as fields, which of them are
    /// terminal, and how <c>/FT</c> is resolved through the inheritable <c>/Parent</c> chain
    /// (ISO 32000-1 Table 220) are not decided here at all: they come from
    /// <see cref="AcroFormFieldWalk"/>, the one traversal in this assembly, which
    /// <c>PdfUaChecker</c> also consumes. So a UA08 violation the checker reports is, by
    /// construction, one this fixer can close.
    /// </para>
    ///
    /// <para>
    /// This fixer works via incremental update: it appends a cross-reference section to the
    /// existing PDF bytes rather than rewriting the entire file. The input PDF is not modified.
    /// Serialisation is delegated to <see cref="PdfWriter"/> / <see cref="IncrementalUpdateWriter"/>
    /// so that string values round-trip byte-for-byte (a hand-rolled Latin-1 writer would
    /// replace every character above U+00FF with '?', destroying Hebrew and CJK tooltips).
    /// </para>
    ///
    /// <para>
    /// <b>Out of scope:</b> this fixer does not write the XMP <c>pdfuaid</c> identifier that
    /// PDF/UA-1 also requires. That lives in the <c>Tetitulz.Pdf.Accessibility</c> feature
    /// assembly (<c>Tetitulz.Pdf.Accessibility.Metadata.PdfUaXmpWriter.SetPdfUaIdentifier</c>);
    /// plugins must not reference feature assemblies, so run it as a separate step.
    /// </para>
    /// </summary>
    public static class PdfAccessibilityFixer
    {
        // The AcroForm field walk — including its cycle guard, depth cap and node budget — lives
        // in AcroFormFieldWalk, shared with PdfUaChecker. Do not re-derive any of it here.

        // [ThreadStatic] so concurrent fixes on a server host cannot read each other's
        // diagnostic (see CLAUDE.md "Diagnostics Pattern").
        [ThreadStatic] private static string _lastFixError;

        /// <summary>
        /// Describes the most recent recoverable problem this thread hit while fixing (an object
        /// whose number could not be determined, an unresolvable reference, …).
        /// <see langword="null"/> when the last run was clean. Anything recorded here that
        /// prevented a fix is also surfaced in <see cref="AccessibilityFixResult.Warnings"/>.
        /// </summary>
        public static string LastFixError => _lastFixError;

        private static void Report(string operation, string detail, Exception ex = null)
        {
            _lastFixError = ex == null
                ? operation + ": " + detail
                : operation + ": " + detail + " — " + ex.GetType().Name + ": " + ex.Message;
        }

        // ------------------------------------------------------------------
        //  Public API
        // ------------------------------------------------------------------

        /// <summary>
        /// Applies accessibility fixes to <paramref name="inputPath"/> and writes the result
        /// to <paramref name="outputPath"/>.
        /// </summary>
        public static AccessibilityFixResult Fix(
            string inputPath,
            string outputPath,
            AccessibilityFixOptions options = null)
        {
            if (inputPath  == null) throw new ArgumentNullException(nameof(inputPath));
            if (outputPath == null) throw new ArgumentNullException(nameof(outputPath));

            byte[] inputBytes = File.ReadAllBytes(inputPath);
            using var output = File.Create(outputPath);
            return Fix(new MemoryStream(inputBytes), output, options);
        }

        /// <summary>
        /// Applies accessibility fixes to the PDF in <paramref name="input"/> and writes
        /// the result to <paramref name="output"/>.
        /// </summary>
        public static AccessibilityFixResult Fix(
            Stream input,
            Stream output,
            AccessibilityFixOptions options = null)
        {
            if (input  == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            options = options ?? new AccessibilityFixOptions();
            _lastFixError = null;
            UnreadableObjects.Begin();

            // Read input into a byte array so we can both open the document and copy bytes.
            byte[] pdfBytes = ReadAll(input);
            using var ms  = new MemoryStream(pdfBytes);
            using var doc = PdfDocument.Open(ms);

            var fixLog   = new List<string>();
            var warnings = new List<string>();
            var updates  = new UpdateSet();

            if (doc.IsEncrypted)
            {
                // Objects would have to be re-encrypted with the document key to stay readable,
                // and the fixer has no password. Pass the bytes through untouched.
                output.Write(pdfBytes, 0, pdfBytes.Length);
                return new AccessibilityFixResult(fixLog,
                    new[] { "Document is encrypted; no fixes applied." });
            }

            var catalog = doc.Catalog?.Dictionary;
            if (catalog == null)
            {
                output.Write(pdfBytes, 0, pdfBytes.Length);
                warnings.Add("No document catalog found.");
                string catalogUnreadable = UnreadableObjects.Notice();
                if (catalogUnreadable != null) warnings.Add(catalogUnreadable);
                return new AccessibilityFixResult(fixLog, warnings);
            }

            // Locate the catalog object so the patched copy can replace it.
            bool haveCatalogId = TryGetObjectId(doc, catalog, out int catalogObjNum, out int catalogGen);
            if (!haveCatalogId)
            {
                Report("PdfAccessibilityFixer.Fix",
                    "the catalog's object number could not be determined");
                warnings.Add("The document catalog's object number could not be determined; " +
                             "catalog-level fixes (/Lang, /ViewerPreferences) were skipped.");
            }

            // Build a mutable copy of the catalog for patching.
            var newCatalog = CloneDictionary(catalog);
            bool catalogChanged = false;

            // Fix 1: /Lang
            //
            // The predicate has to be the checker's, not "is the key absent". PdfUaChecker raises
            // UA02 in two cases — /Lang missing, and /Lang present but empty or whitespace, which
            // is what a producer writes when a language field was left blank. Testing for absence
            // alone would leave `/Lang ()` neither repaired nor warned about, and re-running the
            // checker over the output would report the same UA02. This is the one fix that does
            // not go through AcroFormFieldWalk, so it asks the checker's question itself.
            if (haveCatalogId && options.AddLanguage && !HasNonEmptyLanguage(doc, catalog))
            {
                bool replaced = catalog[PdfName.Lang] != null;
                newCatalog[PdfName.Lang] = CreateTextString(options.DefaultLanguage);
                catalogChanged = true;
                fixLog.Add(replaced
                    ? $"Replaced an empty /Lang with \"{options.DefaultLanguage}\" in the catalog."
                    : $"Added /Lang = \"{options.DefaultLanguage}\" to catalog.");
            }

            // Fix 2: /ViewerPreferences /DisplayDocTitle
            if (options.SetDisplayDocTitle)
            {
                var vpObj = catalog[PdfName.ViewerPreferences];
                var vp = Resolve(doc, vpObj) as PdfDictionary;

                bool already = vp != null && Resolve(doc, vp[PdfName.DisplayDocTitle]) is PdfBoolean pb && pb.Value;
                if (!already)
                {
                    var newVp = vp != null ? CloneDictionary(vp) : new PdfDictionary();
                    newVp[PdfName.DisplayDocTitle] = new PdfBoolean(true);

                    // Only an indirectly-stored /ViewerPreferences can be revised on its own;
                    // a direct one has to ride along inside the catalog.
                    int vpObjNum = 0, vpGen = 0;
                    bool viaOwnObject = vp != null
                        && TryGetObjectId(doc, vp, out vpObjNum, out vpGen)
                        && vpObjNum != catalogObjNum;

                    if (viaOwnObject)
                    {
                        updates.Set(vpObjNum, vpGen, newVp);
                        fixLog.Add("Set /ViewerPreferences /DisplayDocTitle = true.");
                    }
                    else if (haveCatalogId)
                    {
                        newCatalog[PdfName.ViewerPreferences] = newVp;
                        catalogChanged = true;
                        fixLog.Add("Set /ViewerPreferences /DisplayDocTitle = true.");
                    }
                    else
                    {
                        warnings.Add("/ViewerPreferences /DisplayDocTitle could not be set: it is " +
                                     "stored directly in a catalog whose object number is unknown.");
                    }
                }
            }

            // Fix 3: form field tooltips
            if (options.AddMissingTooltips)
            {
                int added = FixFormTooltips(doc, catalog, options, updates, warnings);
                if (added > 0)
                    fixLog.Add($"Added /TU tooltip to {added} form field(s).");
            }

            // Fix 4: tab order on annotated pages
            if (options.FixTabOrder)
            {
                int fixedPages = FixTabs(doc, updates, warnings);
                if (fixedPages > 0)
                    fixLog.Add($"Set /Tabs /S on {fixedPages} annotated page(s).");
            }

            // Store patched catalog.
            if (haveCatalogId && catalogChanged)
                updates.Set(catalogObjNum, catalogGen, newCatalog);

            // Write output: original bytes + one appended incremental-update revision.
            WriteWithIncrementalUpdate(pdfBytes, updates, output, doc);

            // A fix decided on an object it could not read decided as if the object were absent.
            string unreadable = UnreadableObjects.Notice();
            if (unreadable != null) warnings.Add(unreadable);
            return new AccessibilityFixResult(fixLog, warnings);
        }

        // ------------------------------------------------------------------
        //  Form field tooltip fix
        // ------------------------------------------------------------------

        /// <summary>
        /// State carried while writing tooltips onto the fields <see cref="AcroFormFieldWalk"/>
        /// found. It holds no traversal state: the walk owns that.
        /// </summary>
        private sealed class TooltipFixContext
        {
            internal string Template;
            internal bool AllowUnnamedPlaceholders;
            internal UpdateSet Updates;
            internal List<string> Warnings;

            internal int Count;

            /// <summary>Per-field warnings already emitted, and those dropped once the cap was hit.</summary>
            internal int FieldWarningsEmitted;
            internal int FieldWarningsSuppressed;
        }

        /// <summary>
        /// A fabricated tooltip is worth one warning line each, up to this many. Past that the
        /// caller is told how many more there were: a 20 000-field form must not turn
        /// <see cref="AccessibilityFixResult.Warnings"/> into 20 000 strings nobody reads.
        /// </summary>
        private const int MaxFieldWarnings = 25;

        /// <summary>
        /// Writes /TU onto the <b>terminal</b> fields that lack one.
        /// <para>
        /// <b>Which fields those are is not decided here.</b> <see cref="AcroFormFieldWalk"/> owns
        /// the traversal — what counts as a field, which of them are terminal, how the inheritable
        /// <c>/FT</c> resolves through <c>/Parent</c>, and the guards that keep a hostile graph
        /// bounded — and <c>PdfUaChecker</c> consumes the very same walk to decide the document is
        /// non-conforming. That shared answer is what makes a UA08 violation the checker
        /// reports one this fixer can actually close. Everything below is about *writing* a tooltip, never about
        /// finding one.
        /// </para>
        /// </summary>
        private static int FixFormTooltips(
            PdfDocument doc, PdfDictionary catalog, AccessibilityFixOptions options,
            UpdateSet updates, List<string> warnings)
        {
            var walk = AcroFormFieldWalk.Walk(
                catalog,
                obj => Resolve(doc, obj),
                (PdfObject obj, out int objNum, out int gen) => TryGetObjectId(doc, obj, out objNum, out gen));

            var ctx = new TooltipFixContext
            {
                Template                 = options.DefaultTooltipTemplate ?? "{name}",
                AllowUnnamedPlaceholders = options.AddPlaceholderTooltipsForUnnamedFields,
                Updates                  = updates,
                Warnings                 = warnings,
            };

            for (int i = 0; i < walk.Fields.Count; i++)
            {
                if (walk.Fields[i].NeedsAccessibleName) AddTooltip(walk.Fields[i], ctx);
            }

            // Reported after the tooltips: the budget can only
            // run out at the end of the traversal, so every field it did reach is already fixed.
            if (walk.BudgetExhausted) ReportBudgetExhausted(ctx);

            if (ctx.FieldWarningsSuppressed > 0)
                warnings.Add($"{ctx.FieldWarningsSuppressed} further form-field tooltip warning(s) " +
                             $"were suppressed after the first {MaxFieldWarnings}.");

            return ctx.Count;
        }

        /// <summary>
        /// Writes a fabricated /TU onto one terminal field — and says so. Nothing this method can
        /// produce is an accessible name a human wrote; see
        /// <see cref="AccessibilityFixOptions.DefaultTooltipTemplate"/>.
        /// </summary>
        private static void AddTooltip(AcroFormField field, TooltipFixContext ctx)
        {
            if (!field.TryGetObjectId(out int objNum, out int gen))
            {
                Report("PdfAccessibilityFixer.FixFieldsTooltips",
                    "a form field's object number could not be determined");
                AddFieldWarning(ctx,
                    "A form field missing /TU could not be updated: it is not a " +
                    "top-level indirect object, so it has no object number to revise.");
                return;
            }

            string partialName = field.PartialName?.GetText();
            bool haveRealName = !string.IsNullOrWhiteSpace(partialName);

            if (!haveRealName)
            {
                if (!ctx.AllowUnnamedPlaceholders)
                {
                    // A /TU of "Field3" would pass UA08 and tell a screen-reader user nothing; the
                    // violation is the more useful artefact, so leave it standing and say why.
                    AddFieldWarning(ctx,
                        $"Form field (object {objNum}) has no /TU and no /T partial name to derive " +
                        "one from, so no tooltip was written: a synthetic name such as \"Field1\" " +
                        "would silence the PDF/UA-1 UA08 violation without giving a screen-reader " +
                        "user anything to hear. Author a real accessible name for this field, or " +
                        $"set {nameof(AccessibilityFixOptions)}." +
                        $"{nameof(AccessibilityFixOptions.AddPlaceholderTooltipsForUnnamedFields)} " +
                        "to write a placeholder anyway.");
                    return;
                }

                partialName = $"Field{ctx.Count + 1}";
            }

            string tooltip = ctx.Template.Replace("{name}", partialName);

            var newField = CloneDictionary(field.Dictionary);
            newField[PdfName.TU] = CreateTextString(tooltip);
            ctx.Updates.Set(objNum, gen, newField);
            ctx.Count++;

            AddFieldWarning(ctx, haveRealName
                ? $"Form field \"{partialName}\" (object {objNum}): /TU was generated from the " +
                  $"field's /T partial name and now reads \"{tooltip}\". This is a placeholder, not " +
                  "an accessible name a human wrote — a partial name is an internal identifier and " +
                  "may mean nothing to the user, who will hear it announced verbatim. Review it and " +
                  "replace it with wording that says what the field is asking for."
                : $"Form field (object {objNum}) has no /T partial name; /TU was set to the " +
                  $"synthetic placeholder \"{tooltip}\" because " +
                  $"{nameof(AccessibilityFixOptions.AddPlaceholderTooltipsForUnnamedFields)} is " +
                  "enabled. It carries no meaning at all: it satisfies the PDF/UA-1 UA08 check " +
                  "while leaving the user no better off. Replace it with a real accessible name.");
        }

        private static void AddFieldWarning(TooltipFixContext ctx, string warning)
        {
            if (ctx.FieldWarningsEmitted >= MaxFieldWarnings)
            {
                ctx.FieldWarningsSuppressed++;
                return;
            }

            ctx.FieldWarningsEmitted++;
            ctx.Warnings.Add(warning);
        }

        private static void ReportBudgetExhausted(TooltipFixContext ctx)
        {
            Report("PdfAccessibilityFixer.FixFieldsTooltips",
                $"field traversal stopped after {AcroFormFieldWalk.MaxFieldNodes} nodes");

            ctx.Warnings.Add(
                $"Form-field traversal stopped after {AcroFormFieldWalk.MaxFieldNodes} nodes; " +
                "some fields may not have received a /TU tooltip.");
        }

        // ------------------------------------------------------------------
        //  Tab order fix — ISO 32000-1 Table 30 names the key /Tabs
        // ------------------------------------------------------------------

        private static int FixTabs(PdfDocument doc, UpdateSet updates, List<string> warnings)
        {
            int count = 0;
            foreach (var page in doc.Pages)
            {
                var pageDict = page.Dictionary;
                if (pageDict == null) continue;

                var annots = Resolve(doc, pageDict[PdfName.Annots]) as PdfArray;
                if (annots == null || annots.Count == 0) continue;

                // Only the spec key counts as "already fixed". A page carrying the non-standard
                // /TabOrder /S is still
                // non-conforming — no reader honours that key — so it gets a real /Tabs /S.
                var tabs = Resolve(doc, pageDict[PdfName.Tabs]) as PdfName;
                if (tabs?.Value == "S") continue;

                if (!TryGetObjectId(doc, pageDict, out int objNum, out int gen))
                {
                    Report("PdfAccessibilityFixer.FixTabs",
                        "a page's object number could not be determined");
                    warnings.Add("A page carrying annotations could not receive /Tabs /S: its " +
                                 "object number could not be determined.");
                    continue;
                }

                var newPage = CloneDictionary(pageDict);
                newPage[PdfName.Tabs] = PdfName.S;
                updates.Set(objNum, gen, newPage);
                count++;
            }
            return count;
        }

        // ------------------------------------------------------------------
        //  Update set
        // ------------------------------------------------------------------

        /// <summary>
        /// The objects to be appended as one incremental-update revision, keyed by object
        /// number so a later fix revising the same object replaces (rather than duplicates) it.
        /// </summary>
        private sealed class UpdateSet
        {
            private readonly Dictionary<int, IndirectObject> _objects = new Dictionary<int, IndirectObject>();

            internal int Count => _objects.Count;

            internal void Set(int objectNumber, int generation, PdfObject obj)
                => _objects[objectNumber] = new IndirectObject(objectNumber, generation, obj);

            /// <summary>Object-number order — keeps the appended xref subsections contiguous.</summary>
            internal List<IndirectObject> ToOrderedList()
            {
                var list = new List<IndirectObject>(_objects.Values);
                list.Sort((a, b) => a.ObjectNumber.CompareTo(b.ObjectNumber));
                return list;
            }
        }

        // ------------------------------------------------------------------
        //  Writing
        // ------------------------------------------------------------------

        private static void WriteWithIncrementalUpdate(
            byte[] originalBytes,
            UpdateSet updates,
            Stream output,
            PdfDocument doc)
        {
            if (updates.Count == 0)
            {
                // Nothing changed — an empty revision would only grow the file.
                output.Write(originalBytes, 0, originalBytes.Length);
                return;
            }

            // /Size and /Prev are IncrementalUpdateWriter's to compute, from the Document it is
            // given: /Size as one past the highest object number in the file or the update, /Prev as
            // the parser's start-xref, verified against the bytes and re-scanned if stale.
            //
            // The format-aware overload appends the revision in the same cross-reference form as
            // the section it supersedes. Appending a classic table to a document whose section is
            // an xref *stream* produces a hybrid file: strict validators (independent ones) reject it
            // and a reader that only understands xref streams cannot follow the chain at all —
            // which is exactly the audience an accessibility fix is written for.
            IncrementalUpdateWriter.WriteUpdate(
                originalBytes,
                output,
                updates.ToOrderedList(),
                new IncrementalUpdateOptions
                {
                    Document = doc,
                    OriginalTrailer = BuildUpdateTrailer(doc),
                    DiagnosticSink = (operation, detail, ex) => Report(operation, detail, ex),
                });
        }

        /// <summary>
        /// Builds the trailer for the appended revision.
        /// <para>
        /// Only entries that are meaningful in a classic trailer are carried over. For a
        /// document whose cross-references live in an xref <i>stream</i> the parser merges that
        /// stream's dictionary into <see cref="PdfDocument.Trailer"/>, so copying it wholesale
        /// would emit /Type /XRef, /W, /Filter and a stale /Length into a plain trailer.
        /// </para>
        /// <para>
        /// /ID is preserved, because both PDF/UA and PDF/A require a file identifier and the previous
        /// hand-rolled writer dropped it; when the input has none, the writer synthesises one.
        /// </para>
        /// </summary>
        private static PdfDictionary BuildUpdateTrailer(PdfDocument doc)
        {
            var trailer = new PdfDictionary();
            var source = doc.Trailer;

            if (source != null)
            {
                CopyIfPresent(trailer, source, PdfName.Root);
                CopyIfPresent(trailer, source, PdfName.Info);
                CopyIfPresent(trailer, source, PdfName.ID);
            }

            // A missing /ID is not synthesised here: IncrementalUpdateWriter does it — SHA-256 of
            // the original bytes through PdfFileId.FromDigest, a deterministic fallback reported
            // through the DiagnosticSink where SHA-256 is unavailable. This class held a second
            // copy of both, with a second hand-rolled FNV fallback, under a remark that it matched
            // "both Core writers"; a comment asserting two implementations agree is the duplication.
            return trailer;
        }

        private static void CopyIfPresent(PdfDictionary target, PdfDictionary source, PdfName key)
        {
            var value = source[key];
            if (value != null) target.Set(key, value);
        }

        // ------------------------------------------------------------------
        //  Helpers
        // ------------------------------------------------------------------

        // One implementation, in Core.IO.StreamBytes. It rewinds a seekable stream before
        // reading, where a copy reading from the current position would see only the rest.
        private static byte[] ReadAll(Stream s) => StreamBytes.ReadAll(s);

        // One implementation, in Core.Objects.PdfDictionary.CloneShallow.
        /// <summary>
        /// The catalog declares a usable natural language: <c>/Lang</c> resolves to a string that
        /// carries actual characters. The mirror of <c>PdfUaChecker.CheckLanguage</c>'s two-case
        /// UA02 test, so checker and fixer classify the same catalog identically.
        /// </summary>
        private static bool HasNonEmptyLanguage(PdfDocument doc, PdfDictionary catalog)
        {
            var lang = Resolve(doc, catalog[PdfName.Lang]) as PdfString;
            if (lang == null || lang.RawBytes == null || lang.RawBytes.Length == 0) return false;
            return !string.IsNullOrWhiteSpace(lang.GetText());
        }

        private static PdfDictionary CloneDictionary(PdfDictionary src) => PdfDictionary.CloneShallow(src);

        private static PdfObject Resolve(PdfDocument doc, PdfObject obj)
            => UnreadableObjects.Resolve(doc, obj,
                ex => Report("PdfAccessibilityFixer.Resolve", "could not resolve indirect reference", ex));

        // "Is /TU present and non-blank?" is asked once, inside AcroFormFieldWalk, so that the
        // fixer and PdfUaChecker cannot answer it differently.

        /// <summary>
        /// Builds a PDF text string (ISO 32000-1 §7.9.2.2) that preserves the caller's text.
        ///
        /// <para><b>One implementation, in Core</b> — <see cref="PdfString(string)"/>, which is
        /// <c>public</c> and therefore reachable from a plugin (Core's <c>InternalsVisibleTo</c>
        /// names no plugin). That constructor is its own inverse under
        /// <see cref="PdfString.GetText"/>: a character PDFDocEncoding would reinterpret (0x18-0x1F,
        /// 0x80-0xA0) goes out as UTF-16BE with the mandatory <c>FE FF</c> marker, any other
        /// character ≤ U+00FF as one byte — a tooltip containing a tab, or <c>é</c>, is one byte per
        /// character (<c>Fix_HebrewTooltip_IsStoredAsAUtf16BeTextString</c> holds the UTF-16BE
        /// half).</para>
        /// </summary>
        private static PdfString CreateTextString(string text) => new PdfString(text ?? string.Empty);

        /// <summary>
        /// Reverse index (object instance → object number / generation) per open document,
        /// built at most once and dropped when the document is collected.
        ///
        /// <para>
        /// <b>Why it exists.</b> A fallback in <see cref="TryGetObjectId"/> walking the whole
        /// cross-reference table on every miss — and a miss is the case that never matches, so the
        /// walk would always run to completion — is O(fields × objects): an AcroForm with 10 000
        /// direct field dictionaries in a document of 100 000 objects would mean 10^9 lookups and
        /// force the entire object graph to materialise. Building the map once makes the same
        /// fallback O(objects) for the whole operation.
        /// </para>
        /// </summary>
        private static readonly ConditionalWeakTable<PdfDocument, Dictionary<PdfObject, int[]>> _objectIdCache =
            new ConditionalWeakTable<PdfDocument, Dictionary<PdfObject, int[]>>();

        /// <summary>
        /// Resolves the object number and generation of an already-parsed object.
        /// The parser stamps both onto every indirect object it materialises, so the common
        /// case is O(1); the reverse-index lookup is only a fallback for objects that reached
        /// us some other way.
        /// </summary>
        private static bool TryGetObjectId(PdfDocument doc, PdfObject obj, out int objectNumber, out int generation)
        {
            objectNumber = 0;
            generation = 0;
            if (obj == null) return false;

            if (obj.ObjectNumber > 0)
            {
                objectNumber = obj.ObjectNumber;
                generation = obj.GenerationNumber;
                return true;
            }

            if (doc == null) return false;

            Dictionary<PdfObject, int[]> map;
            try
            {
                map = _objectIdCache.GetValue(doc, BuildObjectIdMap);
            }
            catch (Exception ex)
            {
                Report("PdfAccessibilityFixer.TryGetObjectId", "the object index could not be built", ex);
                return false;
            }

            if (map == null || !map.TryGetValue(obj, out int[] id)) return false;

            objectNumber = id[0];
            generation   = id[1];
            return true;
        }

        private static Dictionary<PdfObject, int[]> BuildObjectIdMap(PdfDocument doc)
        {
            // Reference identity, not value equality: two structurally identical dictionaries are
            // different objects, and PdfDictionary does not override Equals anyway.
            var map = new Dictionary<PdfObject, int[]>(PdfReferenceEquality.Instance);

            try
            {
                var xref = doc.CrossReferenceTable;
                if (xref == null) return map;

                foreach (var entry in xref.AllEntries)
                {
                    if (entry.Type == CrossReferenceEntry.EntryType.Free) continue;
                    try
                    {
                        PdfObject resolved = doc.GetObject(entry.ObjectNumber);
                        if (resolved is PdfNull)
                        {
                            // The null object is one shared instance, so it cannot be a key; an
                            // unreadable object (a null with a parse error) is reported as the
                            // thrown case below is, since its remediations will not find it either.
                            if (PdfParser.LastObjectParseError is string reason)
                                Report("PdfAccessibilityFixer.BuildObjectIdMap",
                                    $"object {entry.ObjectNumber} could not be read while indexing: {reason}");
                        }
                        else if (resolved != null)
                            map[resolved] = new[] { entry.ObjectNumber, entry.GenerationNumber };
                    }
                    catch (Exception ex)
                    {
                        Report("PdfAccessibilityFixer.BuildObjectIdMap",
                            $"object {entry.ObjectNumber} could not be loaded while indexing", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                Report("PdfAccessibilityFixer.BuildObjectIdMap", "the cross-reference walk failed", ex);
            }

            return map;
        }
    }

    // ------------------------------------------------------------------
    //  Result type
    // ------------------------------------------------------------------

    /// <summary>Result of a <see cref="PdfAccessibilityFixer.Fix(Stream,Stream,AccessibilityFixOptions)"/> operation.</summary>
    public sealed class AccessibilityFixResult
    {
        /// <summary>Log of fixes that were applied.</summary>
        public IReadOnlyList<string> FixesApplied { get; }
        /// <summary>Any errors or warnings that prevented some fixes.</summary>
        public IReadOnlyList<string> Warnings { get; }
        /// <summary>True if at least one fix was applied.</summary>
        public bool AnyFixApplied => FixesApplied.Count > 0;

        internal AccessibilityFixResult(IReadOnlyList<string> fixes, IReadOnlyList<string> warnings)
        {
            FixesApplied = fixes;
            Warnings     = warnings;
        }
    }
}


