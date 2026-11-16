// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Plugins.PageEdits
{
    /// <summary>
    /// Splits a PDF document into individual pages or page ranges.
    /// </summary>
    public static class PdfSplitter
    {
        [ThreadStatic] private static string _lastNameTreeWarning;

        /// <summary>
        /// Why the extracted catalog <c>/Names</c> tree is not a faithful filtered copy of the
        /// source's, or <c>null</c> when it is. Read it on the calling thread after an extraction;
        /// it is reset at the start of every one, on every path.
        /// </summary>
        /// <remarks>
        /// Currently reports only a flatten cut short by <see cref="PdfLimits.MaxTreeDepth"/> or
        /// <see cref="PdfLimits.MaxNameTreeEntries"/>. Named destinations dropped because their
        /// page is outside the range are the <i>point</i> of the operation, not an anomaly, so they
        /// are not reported here.
        /// </remarks>
        public static string LastNameTreeWarning => _lastNameTreeWarning;

        [ThreadStatic] private static string _lastResourceWarning;

        /// <summary>
        /// Why resources the kept pages do not draw may still be in the extract, or <c>null</c> when
        /// it carries only what they draw. Read it on the calling thread after an extraction; it is
        /// reset at the start of every one, on every path.
        /// </summary>
        /// <remarks>
        /// <para>An extract carries, of each resource dictionary its pages draw through, only the
        /// entries their drawing names — their content, their annotations' appearances, and the
        /// forms, Type 3 glyphs, tiling patterns and soft-mask groups drawn from them — so a
        /// <c>/Resources</c> every page shares does not bring the other pages' pictures and forms
        /// along.</para>
        /// <para>A resource dictionary is carried whole, as this reports, when what is drawn through
        /// it could not all be read (a stream that will not decode, more content than
        /// <see cref="PdfLimits.MaxDecompressedBytes"/> or more streams than
        /// <see cref="PdfLimits.MaxFormXObjectInvocationsPerPage"/>), and when something the extract
        /// carries names it without its drawing having been read — a form's <c>/DR</c>, a widget's
        /// icon: what those draw is not known, so nothing of theirs is left out.</para>
        /// </remarks>
        public static string LastResourceWarning => _lastResourceWarning;

        [ThreadStatic] private static string _lastCatalogWarning;

        /// <summary>
        /// Which document-catalog entries the extraction could not carry, or <c>null</c> when it
        /// carried everything it found. Read it on the calling thread after an extraction; it is
        /// reset at the start of every one, on every path.
        /// </summary>
        /// <remarks>
        /// The extracted catalog is rebuilt rather than copied, so an entry that is not carried is
        /// gone with no other trace. <see cref="CarriedCatalogKeys"/> lists what travels verbatim;
        /// everything else is page-subset-<i>dependent</i> and cannot — <c>/PageLabels</c> is a
        /// number tree keyed by page index, which extraction renumbers, and <c>/StructTreeRoot</c>
        /// describes pages the output no longer contains. Those are named here rather than dropped
        /// in silence, because "this extract is untagged" is a fact about the operation, not about
        /// the source.
        /// </remarks>
        public static string LastCatalogWarning => _lastCatalogWarning;

        [ThreadStatic] private static string _lastCloneWarning;

        /// <summary>
        /// Non-null when an object graph was too deeply nested to copy in full, naming how many
        /// subtrees were dropped. Reset at the start of every extraction, on every path.
        /// </summary>
        /// <remarks>
        /// The depth caps substitute <c>null</c>, which is a legal PDF object — so "the source had
        /// nothing there" and "we gave up at <see cref="PdfLimits.MaxObjectGraphDepth"/> levels"
        /// produce the same output. The caps are on an object's own nesting, which the parser refuses
        /// past the same limit first, so no file reaches them today; they and this report are the
        /// defence should the two limits ever part.
        /// </remarks>
        public static string LastCloneWarning => _lastCloneWarning;

        [ThreadStatic] private static string _lastObjectWarning;

        /// <summary>
        /// Non-null when an object the kept pages reference could not be parsed and is therefore
        /// absent from the output, naming how many and the first. Reset at the start of every
        /// extraction, on every path.
        /// </summary>
        /// <remarks>
        /// The parser answers an unreadable object with the null object, which is also what a file
        /// that defines nothing there yields (§7.3.9) — so without this the output of a damaged
        /// input and the output of a sparse one would look the same, though only the damaged one
        /// has references that dangle. <c>PdfParser.LastObjectParseError</c> tells the two apart.
        /// </remarks>
        public static string LastObjectWarning => _lastObjectWarning;

        [ThreadStatic] private static string _lastFormWarning;

        /// <summary>
        /// Why the extracted <c>/AcroForm</c> is not the exact subset of the source's fields that
        /// belong to the extracted pages, or <c>null</c> when it is. Reset at the start of every
        /// extraction, on every path.
        /// </summary>
        /// <remarks>
        /// Reports a <b>source</b> anomaly only — a <c>/Kids</c> cycle, or a field walk stopped by
        /// <see cref="AcroFormPageFilter.MaxFieldDepth"/> or its edge budget — because those make the
        /// filter's answer conservative rather than exact. A field dropped because its widgets are
        /// all on pages the caller did not ask for is the <i>point</i> of the operation, so it is
        /// silent here, exactly as <see cref="LastNameTreeWarning"/> is silent about a named
        /// destination whose page is out of range.
        /// </remarks>
        public static string LastFormWarning => _lastFormWarning;

        [ThreadStatic] private static string _lastXfaWarning;

        /// <summary>
        /// Non-null when the source carried an <c>/AcroForm /XFA</c> packet that the extract
        /// dropped, or <c>null</c> when it had none. Reset at the start of every extraction, on
        /// every path.
        /// </summary>
        /// <remarks>
        /// <para><b>Why it is dropped rather than filtered.</b> XFA (§12.7.8) holds the form as XML,
        /// and its <c>datasets</c> packet holds the <i>values</i> — so a hybrid form's XML can name
        /// a field whose widgets all live on pages the caller did not ask for. Carrying the packet
        /// verbatim is the same leak as carrying a dropped field's <c>/V</c>, which the field filter
        /// beside this exists to prevent, arriving through an XML channel instead of the object
        /// graph.</para>
        /// <para>Filtering it would mean parsing the datasets tree and pruning it against the
        /// surviving fields — and a packet pruned <i>almost</i> right still leaks while looking
        /// filtered, which is worse than not carrying it. The XML also describes the whole source
        /// form: a page subset is not a form XFA can describe. So the extract drops the packet and
        /// says so here. A <b>hybrid</b> form is unaffected in practice — its AcroForm fields carry
        /// the same content and are filtered exactly; a <b>dynamic</b> XFA form does not survive
        /// page extraction in any case, since its pages are generated from the XML rather than
        /// stored.</para>
        /// </remarks>
        public static string LastXfaWarning => _lastXfaWarning;

        /// <summary>
        /// Catalog entries carried into the extracted document unchanged, because none of them
        /// depends on which pages were kept.
        /// </summary>
        /// <remarks>
        /// <c>/OCProperties</c> is the one that is not cosmetic. Without it a reader has no
        /// configuration that can turn an optional-content group off, so content the author hid
        /// becomes <b>visible</b> in the extract — the opposite of what splitting a document to
        /// share fewer pages is for.
        /// </remarks>
        private static readonly PdfName[] CarriedCatalogKeys =
        {
            PdfName.OCProperties,
            PdfName.Metadata,
            PdfName.Lang,
            PdfName.ViewerPreferences,
            PdfName.PageMode,
            PdfName.PageLayout,
        };

        /// <summary>Catalog entries the rebuilt catalog produces itself.</summary>
        private static readonly PdfName[] RebuiltCatalogKeys =
        {
            PdfName.Type, PdfName.Pages, PdfName.AcroForm, PdfName.Names, PdfName.Outlines,
        };

        /// <summary>
        /// Splits a PDF file into individual single-page PDF files.
        /// </summary>
        /// <remarks>
        /// Each <c>Last*</c> slot afterwards says what any page's extract reported — the first page
        /// that did, and how many more — not the last page's alone.
        /// </remarks>
        public static void Split(string inputPath, string outputDirectory)
        {
            ResetDiagnostics();
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            if (outputDirectory == null) throw new ArgumentNullException(nameof(outputDirectory));

            if (!Directory.Exists(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            using (var doc = PdfDocument.Open(inputPath))
            {
                RefuseEncrypted(doc);

                int pageCount = doc.Pages.Count;
                if (pageCount == 0)
                    throw new PdfException(
                        "The document exposes no pages, so there is nothing to split. Creating the " +
                        "output directory and writing nothing looks exactly like success, which is " +
                        "why this is an exception: check the input for a damaged or absent page tree.");

                int digitCount = pageCount.ToString().Length;
                if (digitCount < 3) digitCount = 3;

                ForEachPage(doc, new SplitWalks(doc), (page, extract) =>
                {
                    string fileName = $"page_{page.ToString().PadLeft(digitCount, '0')}.pdf";
                    string outputPath = Path.Combine(outputDirectory, fileName);

                    using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        extract(output);
                    }
                });
            }
        }

        /// <summary>
        /// Extracts every page of <paramref name="doc"/> alone, through one set of walks, each into
        /// the stream <paramref name="write"/> hands its extraction; the <c>Last*</c> slots afterwards
        /// are the whole split's (<see cref="PageWarnings"/>).
        /// </summary>
        private static void ForEachPage(PdfDocument doc, SplitWalks walks, Action<int, Action<Stream>> write)
        {
            var reported = new PageWarnings();
            int pageCount = doc.Pages.Count;
            for (int i = 1; i <= pageCount; i++)
            {
                int page = i;
                write(page, output => ExtractPages(doc, output, page, page, walks));
                reported.Note(page);
            }
            reported.Publish();
        }

        /// <summary>
        /// Each page of <paramref name="doc"/> as a document of its own, in page order, every one
        /// extracted from the one open document.
        /// </summary>
        /// <remarks>
        /// Opening the input again for each page would parse the whole file once a page — 800 pages
        /// measured 8.8 s against 0.7 s for 200, pages² — and so would reading the catalog's
        /// <c>/Names</c>, outline and field tree for each page, which <see cref="SplitWalks"/> reads
        /// once.
        /// </remarks>
        internal static List<byte[]> EachPage(PdfDocument doc) => EachPage(doc, new SplitWalks(doc));

        /// <summary>
        /// <see cref="EachPage(PdfDocument)"/> over walks the caller holds, so a test can read how
        /// often the document was walked.
        /// </summary>
        internal static List<byte[]> EachPage(PdfDocument doc, SplitWalks walks)
        {
            ResetDiagnostics();
            RefuseEncrypted(doc);

            var pages = new List<byte[]>(doc.Pages.Count);
            ForEachPage(doc, walks, (_, extract) =>
            {
                using var ms = new MemoryStream();
                extract(ms);
                pages.Add(ms.ToArray());
            });
            return pages;
        }

        /// <summary>
        /// The warnings of a split into a document a page. Each page's extraction resets every slot,
        /// so on their own they would describe the last page alone; each slot holds the first page's
        /// warning and how many more pages had one.
        /// </summary>
        private sealed class PageWarnings
        {
            private readonly string[] _first = new string[7];
            private readonly int[] _firstPage = new int[7];
            private readonly int[] _pages = new int[7];

            internal void Note(int page)
            {
                Note(0, _lastNameTreeWarning, page);
                Note(1, _lastCatalogWarning, page);
                Note(2, _lastCloneWarning, page);
                Note(3, _lastObjectWarning, page);
                Note(4, _lastFormWarning, page);
                Note(5, _lastXfaWarning, page);
                Note(6, _lastResourceWarning, page);
            }

            private void Note(int slot, string warning, int page)
            {
                if (warning == null) return;
                if (_pages[slot]++ == 0)
                {
                    _first[slot] = warning;
                    _firstPage[slot] = page;
                }
            }

            internal void Publish()
            {
                _lastNameTreeWarning = Combined(0);
                _lastCatalogWarning = Combined(1);
                _lastCloneWarning = Combined(2);
                _lastObjectWarning = Combined(3);
                _lastFormWarning = Combined(4);
                _lastXfaWarning = Combined(5);
                _lastResourceWarning = Combined(6);
            }

            private string Combined(int slot)
            {
                if (_pages[slot] == 0) return null;
                string page = "Page " + _firstPage[slot].ToString(System.Globalization.CultureInfo.InvariantCulture);
                return _pages[slot] == 1
                    ? page + ": " + _first[slot]
                    : page + " and " + (_pages[slot] - 1).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                      " more: " + _first[slot];
            }
        }

        /// <summary>
        /// Extracts a range of pages from a PDF file and writes them to a new PDF file.
        /// Page numbers are 1-based and inclusive.
        /// </summary>
        public static void SplitRange(string inputPath, string outputPath, int startPage, int endPage)
        {
            ResetDiagnostics();
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            if (outputPath == null) throw new ArgumentNullException(nameof(outputPath));

            using (var doc = PdfDocument.Open(inputPath))
            using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                ExtractPages(doc, output, startPage, endPage);
            }
        }

        /// <summary>
        /// Extracts a range of pages from a PDF stream and writes them to an output stream.
        /// Page numbers are 1-based and inclusive.
        /// </summary>
        public static void SplitRange(Stream input, Stream output, int startPage, int endPage)
        {
            ResetDiagnostics();
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));

            using (var doc = PdfDocument.Open(input))
            {
                ExtractPages(doc, output, startPage, endPage);
            }
        }

        /// <summary>
        /// Extracts an arbitrary set of pages into a single new PDF. Page numbers are 1-based, and
        /// the output keeps them <b>in the order given</b> — so this reorders as readily as it
        /// subsets.
        /// </summary>
        /// <remarks>
        /// <para><see cref="SplitRange(Stream, Stream, int, int)"/> can only take a contiguous run;
        /// extracting fragments and merging them would prune <c>/Outlines</c>, <c>/Names</c> and
        /// <c>/AcroForm</c> once per fragment. This prunes once, against the real kept-page set,
        /// which is the only way those three come out right.</para>
        /// <para><b>A repeated page number is refused.</b> Referencing one page object twice under
        /// <c>/Kids</c> would make the page tree a graph rather than a tree (§7.7.3) and a page
        /// carries a single <c>/Parent</c>; readers disagree about what that means.</para>
        /// <para>Check <see cref="LastCatalogWarning"/>, <see cref="LastNameTreeWarning"/>,
        /// <see cref="LastCloneWarning"/>, <see cref="LastFormWarning"/> and
        /// <see cref="LastXfaWarning"/> afterwards: catalog entries that describe pages which are no
        /// longer present (<c>/PageLabels</c>, <c>/StructTreeRoot</c>) are dropped by design, and
        /// silence about that would be the misleading part.</para>
        /// </remarks>
        /// <exception cref="ArgumentException">
        /// <paramref name="pageNumbers"/> is empty, or names a page more than once.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// A page number is below 1 or past the document's page count.
        /// </exception>
        public static void ExtractPages(Stream input, Stream output, IReadOnlyList<int> pageNumbers)
        {
            ResetDiagnostics();
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (pageNumbers == null) throw new ArgumentNullException(nameof(pageNumbers));

            using (var doc = PdfDocument.Open(input))
            {
                ExtractPages(doc, output, pageNumbers);
            }
        }

        /// <summary>
        /// Extracts an arbitrary set of pages from one file into another. Page numbers are 1-based
        /// and the output keeps them in the order given; see
        /// <see cref="ExtractPages(Stream, Stream, IReadOnlyList{int})"/> for the full contract.
        /// </summary>
        public static void ExtractPages(string inputPath, string outputPath, IReadOnlyList<int> pageNumbers)
        {
            ResetDiagnostics();
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            if (outputPath == null) throw new ArgumentNullException(nameof(outputPath));
            if (pageNumbers == null) throw new ArgumentNullException(nameof(pageNumbers));

            using (var doc = PdfDocument.Open(inputPath))
            using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                ExtractPages(doc, output, pageNumbers);
            }
        }

        /// <summary>
        /// Clears every diagnostic this extraction can set, on this thread.
        /// </summary>
        /// <remarks>
        /// Called before the argument checks, not after them: a warning read after a <b>rejected</b>
        /// call would otherwise describe the previous extraction on this thread, which is worse than
        /// describing nothing.
        /// </remarks>
        private static void ResetDiagnostics()
        {
            _lastNameTreeWarning = null;
            _lastCatalogWarning = null;
            _lastCloneWarning = null;
            _lastObjectWarning = null;
            _lastFormWarning = null;
            _lastXfaWarning = null;
            _lastResourceWarning = null;
            PdfObjectHelper.ResetDepthTruncations();
        }

        private static void ExtractPages(
            PdfDocument doc, Stream output, int startPage, int endPage, SplitWalks walks = null)
        {
            ResetDiagnostics();

            RefuseEncrypted(doc);

            int pageCount = doc.Pages.Count;

            if (startPage < 1)
                throw new ArgumentOutOfRangeException(nameof(startPage), "Start page must be >= 1.");
            if (endPage < startPage)
                throw new ArgumentOutOfRangeException(nameof(endPage), "End page must be >= start page.");
            if (endPage > pageCount)
                throw new ArgumentOutOfRangeException(nameof(endPage), $"End page {endPage} exceeds document page count {pageCount}.");

            var range = new List<int>(endPage - startPage + 1);
            for (int n = startPage; n <= endPage; n++) range.Add(n);

            ExtractPagesCore(doc, output, range, walks ?? new SplitWalks(doc));
        }

        /// <summary>Validates an arbitrary 1-based page list, then extracts it.</summary>
        private static void ExtractPages(PdfDocument doc, Stream output, IReadOnlyList<int> pageNumbers)
        {
            ResetDiagnostics();

            RefuseEncrypted(doc);

            int pageCount = doc.Pages.Count;

            if (pageNumbers.Count == 0)
                throw new ArgumentException(
                    "No pages were requested. A page-less PDF looks exactly like success to a " +
                    "caller that only checks for an exception, so say which pages to keep.",
                    nameof(pageNumbers));

            var requested = new HashSet<int>();
            foreach (int n in pageNumbers)
            {
                if (n < 1 || n > pageCount)
                    throw new ArgumentOutOfRangeException(nameof(pageNumbers),
                        $"Page {n} is out of range; the document has {pageCount} page(s).");

                if (!requested.Add(n))
                    throw new ArgumentException(
                        $"Page {n} was requested more than once. One page object cannot appear " +
                        "twice under /Kids without making the page tree a graph rather than a tree " +
                        "(§7.7.3), and a page carries a single /Parent — extract once and merge if " +
                        "a duplicate is what you want.", nameof(pageNumbers));
            }

            ExtractPagesCore(doc, output, pageNumbers, new SplitWalks(doc));
        }

        /// <summary>
        /// The extraction itself, over an already-validated 1-based page list. Output page order is
        /// the list's order, so this reorders as readily as it subsets.
        /// </summary>
        /// <remarks>
        /// <c>walks</c> is what every extraction from the document reads of the whole of it; one
        /// instance serves every extraction a split makes.
        /// </remarks>
        private static void ExtractPagesCore(
            PdfDocument doc, Stream output, IReadOnlyList<int> pageNumbers, SplitWalks walks)
        {
            var neededObjectNumbers = new HashSet<int>();
            var pageDictionaries = new List<PdfDictionary>();
            // original page object number → resolved PdfPage, for folding in
            // inherited /MediaBox /Resources /Rotate /CropBox (the /Pages ancestors
            // that carry them are not copied into the extracted document).
            var pageLookup = new Dictionary<int, PdfPage>();

            var keptPages = new List<PdfPage>();
            // A page held directly in /Kids — §7.7.3.2 asks for indirect references, and Core's page
            // list reads one anyway — has no object number to carry, so it is written as a new
            // object; left out, a one-page extract of it would be a document of no pages.
            var directPages = new List<PdfPage>();

            foreach (int n in pageNumbers)
            {
                var page = doc.Pages[n - 1];
                var pageDict = page.Dictionary;
                pageDictionaries.Add(pageDict);
                keptPages.Add(page);

                if (pageDict.ObjectNumber > 0)
                {
                    neededObjectNumbers.Add(pageDict.ObjectNumber);
                    pageLookup[pageDict.ObjectNumber] = page;
                }
                else
                {
                    directPages.Add(page);
                }
            }

            // The pages this extraction keeps. Everything below asks the same question of a
            // destination — "does it still have somewhere to point?" — against this one set, and
            // the object walk itself refuses to follow a reference to any page not in it.
            //
            // The set has to exist BEFORE the walk, which is why the loop above does not collect:
            // a link annotation's /Dest [pageRef /Fit] and a widget's /P are ordinary references,
            // and following them would drag pages OUTSIDE the range into the output as orphan
            // objects — content streams included, recoverable with any text extractor.
            var keptPageObjNums = new HashSet<int>(pageLookup.Keys);

            // Which fields may travel, decided before anything is collected: the walk must refuse a
            // field the plan dropped as it refuses a page the range dropped. Collected wholesale, a
            // field whose widgets all land on DROPPED pages would come along with its /V intact:
            // split page 1 of a filled form to share it and the recipient would receive the answers
            // written on pages 2 onward — invisible in a viewer, one object dump away.
            //
            // RESOLVED, not cast to a reference. §12.7.2 requires indirection for the field
            // dictionaries, not for the form itself, so `/AcroForm << /Fields [6 0 R] … >>` is
            // legal; cast, it would yield null here and skip the whole carry-over, leaving widgets
            // on the page in a document with no form. PdfMerger reads both shapes the same way.
            var srcCatalog = doc.Catalog?.Dictionary;
            var srcAcroForm = doc.ResolveObject(srcCatalog?[PdfName.AcroForm]) as PdfDictionary;
            AcroFormPageFilterResult formPlan = null;
            if (srcAcroForm != null)
            {
                formPlan = AcroFormPageFilter.Filter(srcAcroForm, doc, keptPageObjNums, keptPages,
                    index: walks.FieldIndex(srcAcroForm));
                walks.FieldEdgesExamined += formPlan.EdgesExamined;
                _lastFormWarning = formPlan.Warning;
            }
            var guard = srcAcroForm == null
                ? new CollectionGuard(doc, keptPageObjNums)
                : new CollectionGuard(doc, keptPageObjNums, keptPages, walks.FieldNodes(srcAcroForm), formPlan);

            // What the kept pages draw of their resources, read before anything is collected: the
            // walk leaves out an entry of a shared /Resources that only the dropped pages draw.
            var resourceUse = SplitResourceUse.Read(doc, keptPages);
            guard.Resources = resourceUse;

            // A value many pages inherit is one instance, walked once: walking a direct /Resources
            // on the /Pages node for every kept page would cost pages × entries.
            var walkedInherited = new HashSet<object>(PdfReferenceEquality.Instance);
            foreach (var page in keptPages)
            {
                CollectReferencedObjects(page.Dictionary, doc, neededObjectNumbers, guard);

                // §7.7.3.4's inheritable attributes live on an ancestor /Pages node, which the walk
                // above never reaches (it skips /Parent, and the ancestors are not written anyway).
                // MaterializeInheritedPageAttributes folds the resolved values onto the cloned page,
                // and the objects they NAME — every font and XObject of an inherited /Resources — are
                // collected here; uncollected, DeepClone would map each to PdfNull and the extracted
                // page would carry `/Font << /F1 null >>`: a page that renders blank.
                foreach (PdfObject value in new PdfObject[] { page.Resources, page.MediaBox, page.CropBox })
                    if (value != null && walkedInherited.Add(value))
                        CollectReferencedObjects(value, doc, neededObjectNumbers, guard);
                // An inherited value held as an indirect object is carried as that object, so every
                // page names it rather than holding a copy (MaterializeInheritedPageAttributes).
                foreach (PdfObject inherited in new PdfObject[] { page.Resources, page.MediaBox, page.CropBox })
                {
                    int number = PdfObjectHelper.InheritedObjectNumber(inherited, doc);
                    if (number > 0) neededObjectNumbers.Add(number);
                }
            }

            // The catalog entries carried verbatim below name objects the page walk never reaches
            // (an optional-content configuration, an XMP packet), so they are collected here or
            // DeepClone maps every one of them to null.
            foreach (var key in CarriedCatalogKeys)
                CollectReferencedObjects(doc.Catalog?.Dictionary?[key], doc, neededObjectNumbers, guard);

            // Carry over the AcroForm (if any) so interactive form fields survive
            // the split — its catalog-level dict isn't reached from the pages, so
            // collect it (and its /Fields tree + /DR) explicitly.
            int srcAcroFormObjNum = -1;
            PdfDictionary directAcroForm = null;
            // Source object number → the keys whose arrays name fields — the form's /Fields and /CO,
            // a kept field's /Kids — which the clone holds a place for and WriteFieldArray writes
            // from the plan's kept entries.
            var rebuiltArrays = new Dictionary<int, PdfName[]>();
            if (srcAcroForm != null)
            {
                var srcForm = srcAcroForm;

                // Nothing survived: no /AcroForm is written at all, so the extract does not carry a
                // /DR and /DA describing fields it no longer has.
                if (formPlan.AnyFieldSurvives)
                {
                    if (srcCatalog[PdfName.AcroForm] is PdfReference afRef)
                    {
                        srcAcroFormObjNum = afRef.ReferenceObjectNumber;
                        neededObjectNumbers.Add(srcAcroFormObjNum);
                        rebuiltArrays[srcAcroFormObjNum] = FormArrayKeys;
                    }
                    else
                    {
                        // Held directly on the catalog: there is no object number to remap, so the
                        // form is re-emitted as a fresh indirect object below (a catalog written
                        // from scratch cannot carry it inline and still have its /Fields remapped).
                        directAcroForm = srcForm;
                    }

                    // The form's own page-independent entries — /DR, /DA, /NeedAppearances, /Q,
                    // /SigFlags — travel verbatim and stay coherent with whatever fields remain:
                    // each is a default the surviving fields still inherit. /Fields and /CO are
                    // skipped here BECAUSE they name the field tree; only its surviving part may be
                    // collected, and that is what the loop below does.
                    foreach (var kvp in srcForm)
                    {
                        if (kvp.Key == PdfName.Fields || kvp.Key == PdfName.CO) continue;
                        // /XFA is skipped for the same reason as /Fields, one channel over: the key
                        // is removed from the clone below, so collecting the packet's streams here
                        // would leave them in the file as orphan objects.
                        if (kvp.Key == PdfName.XFA) continue;
                        CollectReferencedObjects(kvp.Value, doc, neededObjectNumbers, guard);
                    }

                    // Each surviving field, collected node by node with /Kids skipped: a dropped kid
                    // is then reachable from nothing, so it is never written. Collecting the field
                    // graph with one CollectReferencedObjects call instead would walk /Kids and put
                    // every dropped field's /V back in the file as an orphan object — which is the
                    // whole defect, one level down.
                    foreach (var node in formPlan.KeptNodes)
                    {
                        if (node.ObjectNumber > 0)
                        {
                            neededObjectNumbers.Add(node.ObjectNumber);
                            rebuiltArrays[node.ObjectNumber] = KidsArrayKey;
                        }
                        foreach (var kvp in node)
                        {
                            if (kvp.Key == PdfName.Parent || kvp.Key == PdfName.Kids) continue;
                            CollectReferencedObjects(kvp.Value, doc, neededObjectNumbers, guard);
                        }
                    }
                }
            }

            // --- catalog /Names -------------------------------------------------------------
            //
            // The whole /Names graph deliberately does NOT go through CollectReferencedObjects:
            // that would follow every named destination into its page and so drag pages OUTSIDE
            // the range into the output as orphan objects. Instead each category is flattened,
            // /Dests is filtered against the kept pages, and only the surviving values' graphs are
            // collected. The flattening is the document's, so SplitWalks does it once.
            var names = walks.Names;
            if (names.Truncated)
                _lastNameTreeWarning =
                    "Name tree walk was cut short by the depth cap or the " +
                    $"{PdfLimits.MaxNameTreeEntries}-entry budget; the extracted /Names is incomplete.";

            var nameCategories = names.KeptFor(keptPageObjNums);
            foreach (var (_, entries) in nameCategories)
                foreach (var entry in entries)
                    CollectReferencedObjects(entry.Value, doc, neededObjectNumbers, guard);
            nameCategories.RemoveAll(c => c.Entries.Count == 0);

            // --- /Outlines ------------------------------------------------------------------
            var keptOutline = walks.Outline.KeptFor(keptPageObjNums);
            CollectOutlineObjects(keptOutline, doc, neededObjectNumbers, guard);

            // --- resources others name ------------------------------------------------------
            //
            // A category dictionary that something carried names without its drawing having been
            // read — a form's /DR, a widget's icon — is carried whole after all, and walked again,
            // since the walk left its entries out. What that brings may name another, so until none.
            var carriedDirectly = new List<PdfObject>();
            if (directAcroForm != null) carriedDirectly.Add(directAcroForm);
            foreach (var key in CarriedCatalogKeys) carriedDirectly.Add(doc.Catalog?.Dictionary?[key]);
            foreach (var (_, entries) in nameCategories)
                foreach (var entry in entries) carriedDirectly.Add(entry.Value);
            for (List<PdfDictionary> released;
                 (released = resourceUse.ReleaseWhatOthersName(neededObjectNumbers, carriedDirectly)).Count > 0;)
                foreach (PdfDictionary category in released)
                    CollectReferencedObjects(category, doc, neededObjectNumbers, guard);

            if (resourceUse.UndecidedCount > 0 || resourceUse.ReleasedCount > 0)
                _lastResourceWarning = ResourceWarning(resourceUse);

            var objectMap = new Dictionary<int, int>();
            var objectsToWrite = new List<(int newObjNum, PdfObject obj, int origObjNum)>();
            int nextObjNum = 1;

            int unreadable = 0;
            string firstUnreadable = null;
            foreach (int origObjNum in neededObjectNumbers)
            {
                var obj = doc.GetObject(origObjNum);
                if (obj == null || obj is PdfNull)
                {
                    // A null object with no parse error is what the file holds (§7.3.9); one with a
                    // parse error is an object the kept pages reference and the output will not
                    // have, which the caller cannot otherwise tell from an object the file never had.
                    if (PdfParser.LastObjectParseError is string reason && unreadable++ == 0)
                        firstUnreadable = $"object {origObjNum}: {reason}";
                    continue;
                }

                if (PdfObjectHelper.IsCatalogObject(obj, doc) || PdfObjectHelper.IsPagesTreeNode(obj, doc))
                    continue;

                int newObjNum = nextObjNum++;
                objectMap[origObjNum] = newObjNum;
                objectsToWrite.Add((newObjNum, obj, origObjNum));
            }

            if (unreadable > 0)
                _lastObjectWarning = $"{unreadable} object(s) the kept pages reference could not be read and are " +
                                     $"absent from the output (the first: {firstUnreadable}).";

            int pagesTreeObjNum = nextObjNum++;
            int catalogObjNum = nextObjNum++;

            var remappedObjects = new List<(int objNum, PdfObject obj)>();

            // A direct value several kept pages inherit is written once, named by each of them.
            var sharedInherited = new PdfObjectHelper.InheritedCopies(() => nextObjNum++, remappedObjects);
            foreach (var page in keptPages) sharedInherited.Count(page, doc);

            foreach (var (newObjNum, originalObj, origObjNum) in objectsToWrite)
            {
                rebuiltArrays.TryGetValue(origObjNum, out var rebuilt);
                var cloned = PdfObjectHelper.DeepClone(originalObj, objectMap, pagesTreeObjNum, doc: doc,
                    rebuiltKeys: rebuilt);
                if (cloned is PdfDictionary clonedPage
                    && pageLookup.TryGetValue(origObjNum, out var srcPage))
                {
                    PdfObjectHelper.MaterializeInheritedPageAttributes(clonedPage, srcPage, objectMap, pagesTreeObjNum, doc,
                        sharedInherited);
                }
                PdfObjectHelper.Stamp(cloned, originalObj, newObjNum);
                remappedObjects.Add((newObjNum, cloned));
            }

            var directPageNumbers = new Dictionary<PdfDictionary, int>();
            foreach (var page in directPages)
            {
                if (!(PdfObjectHelper.DeepClone(page.Dictionary, objectMap, pagesTreeObjNum, doc: doc) is PdfDictionary clonedPage))
                    continue;
                clonedPage.Set(PdfName.Parent, new PdfReference(pagesTreeObjNum, 0));
                PdfObjectHelper.MaterializeInheritedPageAttributes(clonedPage, page, objectMap, pagesTreeObjNum, doc,
                    sharedInherited);
                int pageObjNum = nextObjNum++;
                PdfObjectHelper.Stamp(clonedPage, page.Dictionary, pageObjNum);
                remappedObjects.Add((pageObjNum, clonedPage));
                directPageNumbers[page.Dictionary] = pageObjNum;
            }

            // One index over the cloned objects, shared by the two post-clone passes below: both ask
            // "what did this reference become?".
            var clonedByNum = new Dictionary<int, PdfObject>();
            foreach (var (objNum, obj) in remappedObjects) clonedByNum[objNum] = obj;

            PruneDanglingPageReferences(remappedObjects, clonedByNum);
            PruneDroppedFieldTargets(remappedObjects, clonedByNum);

            var kidsArray = new PdfArray();
            foreach (var pageDict in pageDictionaries)
            {
                int origObjNum = pageDict.ObjectNumber;
                if (origObjNum > 0 && objectMap.TryGetValue(origObjNum, out int newPageObjNum))
                {
                    kidsArray.Add(new PdfReference(newPageObjNum, 0));
                }
                else if (directPageNumbers.TryGetValue(pageDict, out int directPageObjNum))
                {
                    kidsArray.Add(new PdfReference(directPageObjNum, 0));
                }
            }

            int extractedPageCount = kidsArray.Count;

            // One implementation, in PdfObjectHelper.BuildPageTreeAndCatalog. /Count comes from
            // the kids array by construction now, rather than from two callers agreeing.
            var (pagesDict, catalogDict) =
                PdfObjectHelper.BuildPageTreeAndCatalog(kidsArray, pagesTreeObjNum, catalogObjNum);
            PdfDictionary clonedAcroForm = null;
            if (srcAcroFormObjNum > 0 && objectMap.TryGetValue(srcAcroFormObjNum, out int newAfNum))
            {
                clonedByNum.TryGetValue(newAfNum, out var clonedAfObj);
                clonedAcroForm = clonedAfObj as PdfDictionary;
                if (clonedAcroForm != null)
                    catalogDict.Set(PdfName.AcroForm, new PdfReference(newAfNum, 0));
            }
            else if (directAcroForm != null)
            {
                clonedAcroForm = PdfObjectHelper.DeepClone(directAcroForm, objectMap, pagesTreeObjNum,
                                     rebuiltKeys: FormArrayKeys) as PdfDictionary;
                if (clonedAcroForm != null)
                {
                    int formObjNum = nextObjNum++;
                    clonedAcroForm.ObjectNumber = formObjNum;
                    clonedAcroForm.GenerationNumber = 0;
                    remappedObjects.Add((formObjNum, clonedAcroForm));
                    clonedByNum[formObjNum] = clonedAcroForm;
                    catalogDict.Set(PdfName.AcroForm, new PdfReference(formObjNum, 0));
                }
            }

            // The dropped fields are already absent from the file — nothing collected them — so what
            // is left is the arrays that named them, which the clone holds a place for.
            if (clonedAcroForm != null)
            {
                ApplyFormFieldPlan(srcAcroForm, clonedAcroForm, doc, formPlan, clonedByNum,
                    objectMap, pagesTreeObjNum, walks);

                // /XFA: dropped, never subset. See LastXfaWarning for why filtering it is the wrong
                // trade. Removing the key is what makes the drop real — the packet's streams are left
                // uncollected above, so a surviving key would dangle.
                if (srcAcroForm != null && srcAcroForm[PdfName.XFA] != null)
                {
                    clonedAcroForm.Remove(PdfName.XFA);
                    _lastXfaWarning =
                        "The source /AcroForm carried an /XFA packet, which was not copied into the " +
                        "extract: XFA holds the form and its values as XML describing the WHOLE " +
                        "source form, so it cannot be subset to the extracted pages, and carrying " +
                        "it verbatim would take values for dropped fields with it. The extracted " +
                        "AcroForm fields are unaffected.";
                }
            }

            // Catalog entries that do not depend on WHICH pages were kept, carried verbatim.
            //
            // The extracted catalog is built from scratch, so an entry not named here is not carried.
            // /OCProperties is the one that is not cosmetic: with no
            // configuration a reader has nothing that can turn a group off, so a layer the author
            // set /OFF — an internal-markup or pre-release layer — becomes VISIBLE in the extract,
            // including in this repository's own renderers, whose IsOcEntryHidden can only consult
            // /OCProperties.
            //
            // Page-subset-DEPENDENT entries are deliberately not carried and are reported instead:
            // /PageLabels is a number tree keyed by page index, which extraction renumbers, and
            // /StructTreeRoot describes the structure of pages that are no longer here.
            CarryCatalogEntries(srcCatalog, doc, catalogDict, objectMap, pagesTreeObjNum);

            // Re-emit the surviving name-tree entries as one flat leaf per category, exactly as
            // PdfMerger does: reusing the source's leaf objects would leave the intermediate nodes'
            // /Limits describing keys the filtered tree no longer holds.
            if (nameCategories.Count > 0)
            {
                var nameDict = new PdfDictionary();
                foreach (var (category, entries) in nameCategories)
                {
                    var leafNames = new PdfArray();
                    // The source tree was already sorted by byte value (§7.9.6) and filtering
                    // preserves order, but sort anyway — a malformed source is not required to be.
                    entries.Sort((x, y) => PdfNameTree.CompareKeys(x.Key.RawBytes, y.Key.RawBytes));
                    foreach (var entry in entries)
                    {
                        // A fresh key rather than the source instance: the parsed one may carry the
                        // object number of an indirect string, and nothing good comes of an output
                        // graph aliasing objects still owned by the document being read.
                        leafNames.Add(new PdfString(entry.Key.RawBytes, entry.Key.IsHex));
                        leafNames.Add(PdfObjectHelper.DeepClone(entry.Value, objectMap, pagesTreeObjNum));
                    }

                    var leaf = new PdfDictionary();
                    leaf.Set(PdfName.Names, leafNames);
                    int leafObjNum = nextObjNum++;
                    leaf.ObjectNumber = leafObjNum;
                    leaf.GenerationNumber = 0;
                    remappedObjects.Add((leafObjNum, leaf));
                    nameDict.Set(category, new PdfReference(leafObjNum, 0));
                }

                int nameDictObjNum = nextObjNum++;
                nameDict.ObjectNumber = nameDictObjNum;
                nameDict.GenerationNumber = 0;
                remappedObjects.Add((nameDictObjNum, nameDict));
                catalogDict.Set(PdfName.Names, new PdfReference(nameDictObjNum, 0));
            }

            // Re-chain the pruned outline under a fresh root. The items themselves are already
            // cloned and reference-remapped in `remappedObjects`; only their sibling/parent/child
            // links and their /Count need rebuilding around the dropped ones.
            if (keptOutline.Count > 0)
            {
                var itemsByNewNum = new Dictionary<int, PdfDictionary>();
                foreach (var (objNum, obj) in remappedObjects)
                    if (obj is PdfDictionary itemDict) itemsByNewNum[objNum] = itemDict;

                int outlinesObjNum = nextObjNum++;
                var (first, last, count) = RechainOutline(keptOutline, outlinesObjNum, itemsByNewNum, objectMap);
                var outlineRoot = new PdfDictionary();
                outlineRoot.Set(PdfName.Type, PdfName.Outlines);
                if (first > 0)
                {
                    outlineRoot.Set(PdfName.First, new PdfReference(first, 0));
                    outlineRoot.Set(PdfName.Last, new PdfReference(last, 0));
                }
                outlineRoot.Set(PdfName.Count, new PdfNumber(count));
                outlineRoot.ObjectNumber = outlinesObjNum;
                outlineRoot.GenerationNumber = 0;
                remappedObjects.Add((outlinesObjNum, outlineRoot));
                if (first > 0)
                    catalogDict.Set(PdfName.Outlines, new PdfReference(outlinesObjNum, 0));
            }

            if (PdfObjectHelper.DepthTruncations > 0)
                _lastCloneWarning =
                    PdfObjectHelper.DepthTruncations + " object subtree(s) nested deeper than " +
                    PdfLimits.MaxObjectGraphDepth + " levels were replaced by null in the extracted " +
                    "document; the depth cap is what stops untrusted input stack-overflowing, and a " +
                    "substituted null is a legal object, so this cannot be seen in the output.";

            var writer = new PdfWriter(output);
            writer.WriteHeader(doc.Version);

            long xrefOffset = PdfObjectHelper.WriteObjectsPagesCatalogAndXref(
                writer, remappedObjects, pagesTreeObjNum, pagesDict, catalogObjNum, catalogDict);

            var trailer = new PdfDictionary();
            // /Size must cover every object actually written, so it is read from the final counter
            // rather than from a snapshot: the /Names leaves and the outline root are allocated
            // after the catalog is numbered.
            trailer.Set(PdfName.Size, new PdfNumber(nextObjNum));
            trailer.Set(PdfName.Root, new PdfReference(catalogObjNum, 0));

            // §14.4: strongly recommended for an unencrypted file and required by PDF/A, as every
            // full-rewrite path writes it. A fresh identifier rather than the source's: this IS a
            // different document, and §14.4's purpose is telling one file from another.
            trailer.Set(PdfName.ID, PdfTrailerBuilder.NewFileId());

            writer.WriteTrailer(trailer, xrefOffset);
        }

        /// <summary>
        /// Drops the annotation keys left pointing at a page the extract does not contain.
        /// </summary>
        /// <remarks>
        /// A page outside the range is no longer collected, so <c>DeepClone</c> maps every reference
        /// to it to <see cref="PdfNull"/>. <c>/Dest [null /Fit]</c> and <c>/P null</c> are worse than
        /// absent — the same judgement <c>RechainOutline</c> already makes for an outline item whose
        /// destination was dropped — so the keys go rather than the annotation: the annotation is
        /// still on the page, it simply no longer claims to lead anywhere.
        /// </remarks>
        private static void PruneDanglingPageReferences(
            List<(int objNum, PdfObject obj)> remappedObjects, Dictionary<int, PdfObject> byNum)
        {
            foreach (var (_, obj) in remappedObjects)
            {
                if (!(obj is PdfDictionary dict)) continue;

                // §12.5.6.19: a widget's /P names the page it appears on.
                if (dict[PdfName.P] is PdfNull) dict.Remove(PdfName.P);

                if (IsDanglingDestination(dict[PdfName.Dest], byNum)) dict.Remove(PdfName.Dest);

                // §12.6.4.2: only /GoTo names a page in THIS document. A /URI or /GoToR action is
                // untouched by the split and must survive it.
                var action = PdfObjectHelper.DerefCloned(dict[PdfName.A], byNum) as PdfDictionary;
                if (action != null &&
                    // Deref'd like /A above it and /D below it: read raw, an indirect /S would read
                    // as null, the GoTo test would fail, and the action would be KEPT — a link whose
                    // target page did not survive the split. `byNum` maps the CLONED objects, which is the right graph here:
                    // DeepClone has already rewritten this reference to the new numbering.
                    (PdfObjectHelper.DerefCloned(action[PdfName.S], byNum) as PdfName)?.Value == "GoTo" &&
                    IsDanglingDestination(action[PdfName.D], byNum))
                    dict.Remove(PdfName.A);
            }
        }

        /// <summary>
        /// Drops the entries a form action is left holding for a field the extract does not carry.
        /// </summary>
        /// <remarks>
        /// <see cref="CollectionGuard"/> refuses a reference into a dropped field, so the copy holds
        /// <see cref="PdfNull"/> where a SubmitForm or ResetForm action's <c>/Fields</c> (§12.7.6.2,
        /// §12.7.6.3) or a Hide action's <c>/T</c> (§12.6.4.10) named one. The entries go: a null is
        /// not a field, and an action naming the fields that remain is what the extract can still do.
        /// Every copied object is searched, actions included wherever they sit — an <c>/A</c>, an
        /// <c>/AA</c> trigger, a <c>/Next</c> chain — since a direct action has no object of its own.
        /// </remarks>
        private static void PruneDroppedFieldTargets(
            List<(int objNum, PdfObject obj)> remappedObjects, Dictionary<int, PdfObject> byNum)
        {
            foreach (var (_, obj) in remappedObjects) PruneActionTargets(obj, byNum, 0);
        }

        private static void PruneActionTargets(PdfObject obj, Dictionary<int, PdfObject> byNum, int depth)
        {
            if (depth > MaxCollectDepth) return;

            if (obj is PdfDictionary dict)
            {
                string action = (PdfObjectHelper.DerefCloned(dict[PdfName.S], byNum) as PdfName)?.Value;
                if (action == "SubmitForm" || action == "ResetForm")
                {
                    RemoveNulls(PdfObjectHelper.DerefCloned(dict[PdfName.Fields], byNum) as PdfArray);
                }
                else if (action == "Hide")
                {
                    var target = PdfObjectHelper.DerefCloned(dict[PdfName.T], byNum);
                    if (target is PdfArray targets) RemoveNulls(targets);
                    else if (target is PdfNull) dict.Set(PdfName.T, new PdfArray());
                }

                foreach (var kvp in dict)
                    if (!(kvp.Value is PdfReference)) PruneActionTargets(kvp.Value, byNum, depth + 1);
            }
            else if (obj is PdfArray array)
            {
                for (int i = 0; i < array.Count; i++)
                    if (!(array[i] is PdfReference)) PruneActionTargets(array[i], byNum, depth + 1);
            }
        }

        // Rebuilt in one pass: removing one entry at a time shifts the rest each time, which a
        // /Fields of many dropped fields made quadratic.
        private static void RemoveNulls(PdfArray array)
        {
            if (array == null) return;
            var kept = new List<PdfObject>(array.Count);
            for (int i = 0; i < array.Count; i++)
                if (!(array[i] is PdfNull)) kept.Add(array[i]);
            if (kept.Count == array.Count) return;
            array.Clear();
            foreach (var item in kept) array.Add(item);
        }

        // A destination whose page reference did not survive the clone: [null /Fit], or the /D of a
        // << /D [null /Fit] >> wrapper. A named (string) destination is not one — the /Names filter
        // above decides those, and a name that survived resolves to a kept page.
        private static bool IsDanglingDestination(PdfObject dest, Dictionary<int, PdfObject> byNum)
        {
            var resolved = PdfObjectHelper.DerefCloned(dest, byNum);
            if (resolved is PdfDictionary wrapper) resolved = PdfObjectHelper.DerefCloned(wrapper[PdfName.D], byNum);
            return resolved is PdfArray array && array.Count > 0 && array[0] is PdfNull;
        }


        // ------------------------------------------------------------------
        //  AcroForm — the extract carries only the fields it can still show
        // ------------------------------------------------------------------

        // The keys of a field-naming array: the form's /Fields and /CO, a field's /Kids.
        private static readonly PdfName[] FormArrayKeys = { PdfName.Fields, PdfName.CO };
        private static readonly PdfName[] KidsArrayKey = { PdfName.Kids };

        /// <summary>
        /// Writes the cloned form's <c>/Fields</c>, each surviving field's <c>/Kids</c> and the
        /// <c>/CO</c> calculation order from what <paramref name="plan"/> kept.
        /// </summary>
        /// <remarks>
        /// <para>The dropped fields are already <b>absent from the file</b> — the collection pass never
        /// reached them, so their <c>/V</c> and their appearance streams were never written. What is
        /// left is the arrays that named them, which the clone holds a place for: each is written
        /// with the kept entries alone, since a <c>/Fields [null 4 0 R]</c> is the same judgement
        /// <c>RechainOutline</c> and <see cref="PruneDanglingPageReferences"/> make elsewhere in this
        /// file.</para>
        /// <para>The arrays are written from the plan, not copied whole and narrowed: a copy would
        /// cost what each holds in every extract — pages × fields for a split of a form with a field
        /// on every page — and an array held as an indirect object of its own, which no walk
        /// collects, would come out of the copy as <c>null</c>, taking every field with it.</para>
        /// </remarks>
        private static void ApplyFormFieldPlan(
            PdfDictionary srcAcroForm, PdfDictionary clonedAcroForm, PdfDocument doc,
            AcroFormPageFilterResult plan, Dictionary<int, PdfObject> clonedByNum,
            Dictionary<int, int> objectMap, int pagesTreeObjNum, SplitWalks walks)
        {
            if (srcAcroForm == null || clonedAcroForm == null || plan == null) return;

            WriteFieldArray(srcAcroForm, clonedAcroForm, PdfName.Fields, doc, plan, clonedByNum,
                objectMap, pagesTreeObjNum, new HashSet<PdfDictionary>(), 0);

            // §12.7.2 Table 218's /CO names terminal fields in calculation order. An entry naming a
            // dropped field would name nothing, and asking a viewer to run a calculation on nothing
            // is worse than not listing an order at all.
            if (srcAcroForm[PdfName.CO] == null) return;

            var order = walks.CalculationOrder(srcAcroForm);
            var positions = new List<int>();
            if (order != null)
                foreach (var node in plan.KeptNodes)
                    if (order.PositionsOf.TryGetValue(node, out var at)) positions.AddRange(at);
            positions.Sort();

            var keptCo = new PdfArray();
            foreach (int i in positions)
                keptCo.Add(PdfObjectHelper.DeepClone(order.Entries[i], objectMap, pagesTreeObjNum));

            if (keptCo.Count == 0) clonedAcroForm.Remove(PdfName.CO);
            else clonedAcroForm.Set(PdfName.CO, keptCo);
        }

        /// <summary>
        /// Writes one <c>/Fields</c> or <c>/Kids</c> array of the clone with the entries the plan
        /// kept, then each surviving field's own <c>/Kids</c>.
        /// </summary>
        /// <remarks>
        /// Written from the <b>source</b> array by <b>index</b>: the plan is expressed in positions,
        /// so one rule covers a field written as the indirect reference §12.7.2 Table 218 asks for and
        /// one written as a direct dictionary, which has a position but no object number, and an
        /// array held directly or as an indirect object of its own.
        /// </remarks>
        private static void WriteFieldArray(
            PdfDictionary srcOwner, PdfDictionary clonedOwner, PdfName arrayKey, PdfDocument doc,
            AcroFormPageFilterResult plan, Dictionary<int, PdfObject> clonedByNum,
            Dictionary<int, int> objectMap, int pagesTreeObjNum, HashSet<PdfDictionary> done, int depth)
        {
            if (depth > AcroFormPageFilter.MaxFieldDepth ||
                !plan.KeptChildIndices.TryGetValue(srcOwner, out var keptIndices) ||
                !(doc.ResolveObject(srcOwner[arrayKey]) is PdfArray srcArray))
            {
                // No plan for this array — past the walk's depth, or no array to plan — so it is
                // written as a copy of the source would hold it.
                if (srcOwner[arrayKey] != null)
                    clonedOwner.Set(arrayKey, PdfObjectHelper.DeepClone(srcOwner[arrayKey], objectMap, pagesTreeObjNum));
                else
                    clonedOwner.Remove(arrayKey);
                return;
            }

            var kept = new PdfArray();
            foreach (int i in keptIndices)
            {
                if (i < 0 || i >= srcArray.Count) continue;
                var entry = srcArray[i];

                // `done` is reference identity over the SOURCE nodes: a /Kids diamond reaches one
                // field twice, and its one copy has its /Kids written once.
                var srcKid = doc.ResolveObject(entry) as PdfDictionary;
                bool writeKids = srcKid != null && done.Add(srcKid);

                PdfDictionary clonedKid;
                if (entry is PdfDictionary direct)
                {
                    // A field written in place has no object of its own, so its copy is made here.
                    clonedKid = PdfObjectHelper.DeepClone(direct, objectMap, pagesTreeObjNum, depth: 2,
                        rebuiltKeys: writeKids ? KidsArrayKey : null) as PdfDictionary;
                    kept.Add(clonedKid);
                }
                else
                {
                    var clonedEntry = PdfObjectHelper.DeepClone(entry, objectMap, pagesTreeObjNum);
                    clonedKid = PdfObjectHelper.DerefCloned(clonedEntry, clonedByNum) as PdfDictionary;
                    kept.Add(clonedEntry);
                }

                if (writeKids && clonedKid != null)
                    WriteFieldArray(srcKid, clonedKid, PdfName.Kids, doc, plan, clonedByNum,
                        objectMap, pagesTreeObjNum, done, depth + 1);
            }

            if (kept.Count == 0 && arrayKey == PdfName.Kids) clonedOwner.Remove(PdfName.Kids);
            else clonedOwner.Set(arrayKey, kept);
        }

        /// <summary>
        /// Refuses an encrypted document, which this operation cannot read and must not appear to
        /// have processed.
        /// </summary>
        /// <remarks>
        /// <see cref="PdfDocument"/> loads no page tree for an encrypted file, so such a document
        /// reports <b>zero</b> pages rather than throwing: <c>Split</c> would create the output
        /// directory, write nothing and return, and <c>SplitRange</c> would throw
        /// <see cref="ArgumentOutOfRangeException"/> blaming the caller's range for the document's
        /// encryption. Same refusal as <c>PdfMerger.Merge</c>, and for the same reason.
        /// </remarks>
        internal static void RefuseEncrypted(PdfDocument doc)
        {
            if (!doc.IsEncrypted) return;
            throw new PdfEncryptedDocumentException(
                "The document is encrypted, so its page tree cannot be read and no page can be " +
                "extracted. Decrypt it first (Tetitulz.Pdf.Encryption.PdfDecryptor), or run the " +
                "extraction inside PdfEncryptedRoundTrip.Run.");
        }

        /// <summary>
        /// Copies the page-subset-independent catalog entries onto the rebuilt catalog and records
        /// the ones that could not travel on <see cref="LastCatalogWarning"/>.
        /// </summary>
        private static void CarryCatalogEntries(
            PdfDictionary srcCatalog, PdfDocument doc, PdfDictionary catalogDict,
            Dictionary<int, int> objectMap, int pagesTreeObjNum)
        {
            if (srcCatalog == null) return;

            foreach (var key in CarriedCatalogKeys)
            {
                var value = srcCatalog[key];
                if (value == null || value is PdfNull) continue;
                catalogDict.Set(key, PdfObjectHelper.DeepClone(value, objectMap, pagesTreeObjNum));
            }

            var dropped = new List<string>();
            foreach (var entry in srcCatalog)
            {
                if (Array.IndexOf(RebuiltCatalogKeys, entry.Key) >= 0) continue;
                if (Array.IndexOf(CarriedCatalogKeys, entry.Key) >= 0) continue;
                dropped.Add("/" + entry.Key.Value);
            }

            if (dropped.Count > 0)
                _lastCatalogWarning =
                    "The extracted catalog does not carry " + string.Join(", ", dropped) +
                    ": each depends on which pages the document contains (a /PageLabels number tree " +
                    "is keyed by page index, a /StructTreeRoot describes pages that are not here), " +
                    "so carrying it unchanged would describe the wrong document.";
        }

        // ------------------------------------------------------------------
        //  Outline (bookmark) pruning
        // ------------------------------------------------------------------

        // One surviving outline item. `KeepOwnDestination` is false for an item kept only because a
        // descendant survived — its own destination points outside the range, so the clone loses
        // /Dest and /A and it becomes a pure grouping item rather than a link to a page the
        // extracted file does not contain.
        internal sealed class OutlineNode
        {
            public int SourceObjNum;
            public bool KeepOwnDestination;
            /// <summary>
            /// The item carries an action that does not name a page in THIS document — /URI,
            /// /GoToR, /JavaScript, /Named. Such an action stays valid in an extract, so the item
            /// is worth keeping and its /A must not be stripped.
            /// </summary>
            public bool KeepPageIndependentAction;
            public readonly List<OutlineNode> Children = new List<OutlineNode>();
        }

        // ------------------------------------------------------------------
        //  What every extraction reads of the whole document, read once
        // ------------------------------------------------------------------

        /// <summary>
        /// The flattened <c>/Names</c>, the outline's shape and the field tree of one document, each
        /// read the first time an extraction asks and kept for every later one.
        /// </summary>
        /// <remarks>
        /// None of the three depends on which pages are kept — only what an extraction selects from
        /// them does — so a split into single pages reading them for each page would read the
        /// document pages times: 800 pages with a named destination, an outline item and a field each
        /// measured 1.0 s that way, of which 45 ms was the pages. An extraction's selection costs
        /// what it keeps.
        /// </remarks>
        internal sealed class SplitWalks
        {
            private readonly PdfDocument _doc;
            private NameWalk _names;
            private OutlineWalk _outline;
            private bool _fieldsRead;
            private AcroFormFieldIndex _fields;

            internal SplitWalks(PdfDocument doc) { _doc = doc; }

            /// <summary>How many of the three walks have run: each runs at most once.</summary>
            internal int Walks { get; private set; }

            /// <summary>The edge budget every extraction's field filter spent, summed: the evidence
            /// that each one examined only the fields that reach its pages.</summary>
            internal long FieldEdgesExamined { get; set; }

            internal NameWalk Names
            {
                get
                {
                    if (_names == null) { Walks++; _names = NameWalk.Read(_doc); }
                    return _names;
                }
            }

            internal OutlineWalk Outline
            {
                get
                {
                    if (_outline == null) { Walks++; _outline = OutlineWalk.Read(_doc, Names.NamedDestinations); }
                    return _outline;
                }
            }

            /// <summary>The field index of <paramref name="form"/>, or <c>null</c> when its tree has none.</summary>
            internal AcroFormFieldIndex FieldIndex(PdfDictionary form)
            {
                ReadFields(form);
                return _fields != null && ReferenceEquals(_fields.Form, form) ? _fields : null;
            }

            private HashSet<int> _fieldNodes;
            private bool _fieldNodesComplete;

            /// <summary>
            /// The object numbers of every field node of <paramref name="form"/>, and whether that is
            /// all of them: what <see cref="CollectionGuard"/> refuses unless the plan kept it.
            /// </summary>
            internal (HashSet<int> Numbers, bool Complete) FieldNodes(PdfDictionary form)
            {
                ReadFields(form);
                return (_fieldNodes, _fieldNodesComplete);
            }

            // One walk of the field tree for both views: the index lists every node when it can be
            // built, and a breadth-first listing under the edge budget stands in when it cannot.
            private void ReadFields(PdfDictionary form)
            {
                if (_fieldsRead) return;
                _fieldsRead = true;
                Walks++;
                _fields = AcroFormFieldIndex.TryBuild(form, _doc);
                if (_fields != null)
                {
                    _fieldNodes = new HashSet<int>(_fields.NodeNumbers);
                    _fieldNodesComplete = true;
                }
                else
                {
                    _fieldNodes = AcroFormPageFilter.FieldNodeNumbers(form, _doc, out _fieldNodesComplete);
                }
            }

            private bool _orderRead;
            private CalculationOrderIndex _order;

            /// <summary>
            /// The positions in <paramref name="form"/>'s <c>/CO</c> at which each field is named, or
            /// <c>null</c> when it holds no array there.
            /// </summary>
            internal CalculationOrderIndex CalculationOrder(PdfDictionary form)
            {
                if (!_orderRead)
                {
                    _orderRead = true;
                    _order = CalculationOrderIndex.Read(form, _doc);
                }
                return _order != null && ReferenceEquals(_order.Form, form) ? _order : null;
            }
        }

        /// <summary>A form's <c>/CO</c> array, and the positions at which it names each field.</summary>
        internal sealed class CalculationOrderIndex
        {
            private CalculationOrderIndex(PdfDictionary form, PdfArray entries) { Form = form; Entries = entries; }

            internal PdfDictionary Form { get; }

            internal PdfArray Entries { get; }

            internal Dictionary<PdfDictionary, List<int>> PositionsOf { get; } = new Dictionary<PdfDictionary, List<int>>();

            internal static CalculationOrderIndex Read(PdfDictionary form, PdfDocument doc)
            {
                if (!(doc.ResolveObject(form[PdfName.CO]) is PdfArray entries)) return null;

                var index = new CalculationOrderIndex(form, entries);
                for (int i = 0; i < entries.Count; i++)
                {
                    if (!(doc.ResolveObject(entries[i]) is PdfDictionary field)) continue;
                    if (!index.PositionsOf.TryGetValue(field, out var at))
                        index.PositionsOf[field] = at = new List<int>();
                    at.Add(i);
                }
                return index;
            }
        }

        /// <summary>The catalog's <c>/Names</c>, every category flattened.</summary>
        internal sealed class NameWalk
        {
            private readonly List<(PdfName Category, List<KeyValuePair<PdfString, PdfObject>> Entries)> _categories =
                new List<(PdfName Category, List<KeyValuePair<PdfString, PdfObject>> Entries)>();

            // The /Dests entries' positions by the page each lands on, ascending.
            private readonly Dictionary<int, List<int>> _destsByPage = new Dictionary<int, List<int>>();

            /// <summary>Whether the depth cap or the entry budget cut a flatten short.</summary>
            internal bool Truncated { get; private set; }

            /// <summary>
            /// The flattened <c>/Dests</c>, by key, used to resolve a bookmark or a destination that
            /// names its target instead of pointing at the page.
            /// </summary>
            internal Dictionary<string, PdfObject> NamedDestinations { get; } =
                new Dictionary<string, PdfObject>(StringComparer.Ordinal);

            internal static NameWalk Read(PdfDocument doc)
            {
                var walk = new NameWalk();
                var srcCatalog = doc.Catalog?.Dictionary;
                if (srcCatalog == null || !(doc.ResolveObject(srcCatalog[PdfName.Names]) is PdfDictionary srcNames))
                    return walk;

                int budget = PdfLimits.MaxNameTreeEntries;
                foreach (var category in srcNames)
                {
                    var entries = new List<KeyValuePair<PdfString, PdfObject>>();
                    if (!PdfObjectHelper.TryFlattenNameTree(category.Value, doc, entries, ref budget))
                        walk.Truncated = true;
                    if (entries.Count == 0) continue;

                    if (category.Key == PdfName.Dests)
                        foreach (var entry in entries)
                            walk.NamedDestinations[PdfObjectHelper.NameKeyLookup(entry.Key.RawBytes)] = entry.Value;

                    walk._categories.Add((category.Key, entries));
                }

                // After every category, so a destination naming another resolves against all of them.
                foreach (var (category, entries) in walk._categories)
                {
                    if (category != PdfName.Dests) continue;
                    for (int i = 0; i < entries.Count; i++)
                    {
                        int page = PdfObjectHelper.DestinationPageObjectNumber(entries[i].Value, doc, walk.NamedDestinations);
                        if (page <= 0) continue;
                        if (!walk._destsByPage.TryGetValue(page, out var onPage))
                            walk._destsByPage[page] = onPage = new List<int>();
                        onPage.Add(i);
                    }
                }
                return walk;
            }

            /// <summary>
            /// Each category's entries for one extraction, in the order the source tree holds them:
            /// <c>/Dests</c> narrowed to the destinations on a kept page, every other category whole.
            /// </summary>
            /// <remarks>
            /// Every list is the extraction's own copy: the extraction sorts it in place, and an
            /// unstable sort of a list another extraction already sorted can order equal keys
            /// differently than it would the source order.
            /// </remarks>
            internal List<(PdfName Category, List<KeyValuePair<PdfString, PdfObject>> Entries)> KeptFor(
                HashSet<int> keptPageObjNums)
            {
                var kept = new List<(PdfName Category, List<KeyValuePair<PdfString, PdfObject>> Entries)>(_categories.Count);
                foreach (var (category, entries) in _categories)
                {
                    if (category != PdfName.Dests)
                    {
                        kept.Add((category, new List<KeyValuePair<PdfString, PdfObject>>(entries)));
                        continue;
                    }

                    var positions = new List<int>();
                    foreach (int page in keptPageObjNums)
                        if (_destsByPage.TryGetValue(page, out var onPage)) positions.AddRange(onPage);
                    positions.Sort();

                    var dests = new List<KeyValuePair<PdfString, PdfObject>>(positions.Count);
                    foreach (int i in positions) dests.Add(entries[i]);
                    kept.Add((category, dests));
                }
                return kept;
            }
        }

        /// <summary>
        /// The source outline's shape: every item the walk reaches, in preorder, with its parent, the
        /// page its own destination lands on and whether its action names no page here.
        /// </summary>
        internal sealed class OutlineWalk
        {
            private readonly List<int> _sourceObjNum = new List<int>();
            private readonly List<int> _parent = new List<int>();
            private readonly List<int> _destPage = new List<int>();
            private readonly List<bool> _pageIndependent = new List<bool>();

            // The items whose own destination lands on each page, ascending.
            private readonly Dictionary<int, List<int>> _byDestPage = new Dictionary<int, List<int>>();

            // The items every extraction keeps: those with a page-independent action, and their
            // ancestors, ascending.
            private readonly List<int> _alwaysKept = new List<int>();

            internal static OutlineWalk Read(PdfDocument doc, Dictionary<string, PdfObject> namedDestinations)
            {
                var walk = new OutlineWalk();
                if (doc.ResolveObject(doc.Catalog?.Dictionary?[PdfName.Outlines]) is PdfDictionary root)
                    walk.ReadLevel(root[PdfName.First], doc, namedDestinations, new HashSet<int>(), 0, -1);

                var always = new HashSet<int>();
                for (int i = 0; i < walk._sourceObjNum.Count; i++)
                {
                    if (walk._pageIndependent[i])
                        for (int j = i; j >= 0 && always.Add(j); j = walk._parent[j]) { }

                    int page = walk._destPage[i];
                    if (page <= 0) continue;
                    if (!walk._byDestPage.TryGetValue(page, out var onPage))
                        walk._byDestPage[page] = onPage = new List<int>();
                    onPage.Add(i);
                }
                walk._alwaysKept.AddRange(always);
                walk._alwaysKept.Sort();
                return walk;
            }

            private void ReadLevel(
                PdfObject firstRef, PdfDocument doc, Dictionary<string, PdfObject> namedDestinations,
                HashSet<int> visited, int depth, int parent)
            {
                if (depth > PdfLimits.MaxTreeDepth) return;

                var cur = firstRef as PdfReference;
                while (cur != null)
                {
                    int objNum = cur.ReferenceObjectNumber;
                    // The visited set bounds both a /Next ring and a /First that points back at an
                    // ancestor; the depth cap alone bounds neither, since both are shallow.
                    if (!visited.Add(objNum)) return;
                    if (!(doc.GetObject(objNum) is PdfDictionary item)) return;

                    int index = _sourceObjNum.Count;
                    _sourceObjNum.Add(objNum);
                    _parent.Add(parent);
                    _destPage.Add(PdfObjectHelper.DestinationPageObjectNumber(
                        OutlineDestination(item, doc), doc, namedDestinations));
                    _pageIndependent.Add(HasPageIndependentAction(item, doc));

                    ReadLevel(item[PdfName.First], doc, namedDestinations, visited, depth + 1, index);
                    cur = item[PdfName.Next] as PdfReference;
                }
            }

            /// <summary>
            /// The outline pruned to the items that still have somewhere to point, so a survivor
            /// whose parent was dropped is re-parented rather than orphaned: an item is kept when its
            /// own destination is on a kept page, its action names no page here, or a descendant is
            /// kept. The surviving shape is re-chained from scratch by the caller.
            /// </summary>
            internal List<OutlineNode> KeptFor(HashSet<int> keptPageObjNums)
            {
                // The kept items are the seeds and their ancestors; in preorder a parent comes before
                // its children and siblings come in order, so ascending order builds the tree.
                var keep = new HashSet<int>(_alwaysKept);
                foreach (int page in keptPageObjNums)
                    if (_byDestPage.TryGetValue(page, out var onPage))
                        foreach (int i in onPage)
                            for (int j = i; j >= 0 && keep.Add(j); j = _parent[j]) { }

                var order = new List<int>(keep);
                order.Sort();

                var top = new List<OutlineNode>();
                var built = new Dictionary<int, OutlineNode>(order.Count);
                foreach (int i in order)
                {
                    int destPage = _destPage[i];
                    var node = new OutlineNode
                    {
                        SourceObjNum = _sourceObjNum[i],
                        KeepOwnDestination = destPage > 0 && keptPageObjNums.Contains(destPage),
                        KeepPageIndependentAction = _pageIndependent[i],
                    };
                    built[i] = node;
                    (_parent[i] < 0 ? top : built[_parent[i]].Children).Add(node);
                }
                return top;
            }
        }

        // §12.3.3: an outline item names its target either directly in /Dest or through an action.
        // Only /S /GoTo lands in this document — /GoToR and friends target another file, so an item
        // carrying one has no page here and is treated as having no destination.
        private static PdfObject OutlineDestination(PdfDictionary item, PdfDocument doc)
        {
            var dest = item[PdfName.Dest];
            if (dest != null && !(dest is PdfNull)) return dest;

            if (doc.ResolveObject(item[PdfName.A]) is PdfDictionary action &&
                (doc.ResolveObject(action[PdfName.S]) as PdfName)?.Value == "GoTo")
                return action[PdfName.D];

            return null;
        }

        /// <summary>
        /// True when the item's <c>/A</c> action targets something other than a page in this
        /// document, and therefore still means what it meant before the split.
        /// </summary>
        /// <remarks>
        /// <c>OutlineDestination</c> resolves <c>/GoTo</c> only, so every other action kind answers
        /// "no destination" there. Reading that as "nothing worth keeping" would drop /URI bookmarks
        /// from every extracted file.
        /// </remarks>
        private static bool HasPageIndependentAction(PdfDictionary item, PdfDocument doc)
        {
            if (!(doc.ResolveObject(item[PdfName.A]) is PdfDictionary action)) return false;
            string s = (doc.ResolveObject(action[PdfName.S]) as PdfName)?.Value;
            // An /S this reader does not recognise is kept: it names no page here either, and
            // dropping an action because it is unfamiliar is the destructive direction.
            return s != null && s != "GoTo";
        }

        // Adds each survivor to the extraction set, then collects the graph hanging off its
        // NON-STRUCTURAL entries only. Following /First //Last //Next //Prev would pull the dropped
        // items back in as orphan objects — and /Dest //A are skipped for an item kept only for its
        // children, so a destination outside the range cannot drag its page in either.
        private static void CollectOutlineObjects(
            List<OutlineNode> level, PdfDocument doc, HashSet<int> collected, CollectionGuard keptPages)
        {
            foreach (var node in level)
            {
                collected.Add(node.SourceObjNum);
                if (doc.GetObject(node.SourceObjNum) is PdfDictionary item)
                {
                    foreach (var kvp in item)
                    {
                        if (kvp.Key == PdfName.First || kvp.Key == PdfName.Last ||
                            kvp.Key == PdfName.Next || kvp.Key == PdfName.Prev ||
                            kvp.Key == PdfName.Parent)
                            continue;
                        if (!node.KeepOwnDestination && kvp.Key == PdfName.Dest) continue;
                        if (!node.KeepOwnDestination && !node.KeepPageIndependentAction &&
                            kvp.Key == PdfName.A) continue;
                        CollectReferencedObjects(kvp.Value, doc, collected, keptPages);
                    }
                }
                CollectOutlineObjects(node.Children, doc, collected, keptPages);
            }
        }

        // Rebuilds /First //Last //Next //Prev //Parent //Count over the survivors of one level and
        // returns (first, last, descendant count). Returns (0, 0, 0) when nothing at this level
        // survived cloning.
        private static (int First, int Last, int Count) RechainOutline(
            List<OutlineNode> level, int parentObjNum,
            Dictionary<int, PdfDictionary> itemsByNewNum, Dictionary<int, int> objectMap)
        {
            var live = new List<(OutlineNode Node, int ObjNum)>();
            foreach (var node in level)
                if (objectMap.TryGetValue(node.SourceObjNum, out int newNum) &&
                    itemsByNewNum.ContainsKey(newNum))
                    live.Add((node, newNum));

            if (live.Count == 0) return (0, 0, 0);

            int total = 0;
            for (int i = 0; i < live.Count; i++)
            {
                var item = itemsByNewNum[live[i].ObjNum];
                item.Set(PdfName.Parent, new PdfReference(parentObjNum, 0));

                if (i > 0) item.Set(PdfName.Prev, new PdfReference(live[i - 1].ObjNum, 0));
                else item.Remove(PdfName.Prev);
                if (i < live.Count - 1) item.Set(PdfName.Next, new PdfReference(live[i + 1].ObjNum, 0));
                else item.Remove(PdfName.Next);

                if (!live[i].Node.KeepOwnDestination)
                {
                    // The out-of-range page reference is already null after DeepClone, and a /Dest
                    // of [null /Fit] is worse than none at all.
                    item.Remove(PdfName.Dest);

                    // But an action that names no page in this document is not invalidated by the
                    // split, so it must survive — a "Specification" bookmark whose /A is a /URI keeps
                    // its hyperlink. Stripping is for /GoTo, the only action kind OutlineDestination
                    // resolves and therefore the only one that can dangle.
                    if (!live[i].Node.KeepPageIndependentAction)
                        item.Remove(PdfName.A);
                }

                var (childFirst, childLast, childCount) =
                    RechainOutline(live[i].Node.Children, live[i].ObjNum, itemsByNewNum, objectMap);
                if (childFirst > 0)
                {
                    item.Set(PdfName.First, new PdfReference(childFirst, 0));
                    item.Set(PdfName.Last, new PdfReference(childLast, 0));
                    item.Set(PdfName.Count, new PdfNumber(childCount));
                }
                else
                {
                    item.Remove(PdfName.First);
                    item.Remove(PdfName.Last);
                    item.Remove(PdfName.Count);
                }

                total += 1 + childCount;
            }

            return (live[0].ObjNum, live[live.Count - 1].ObjNum, total);
        }

        /// <summary>What <see cref="LastResourceWarning"/> says of a read that left resources whole.</summary>
        private static string ResourceWarning(SplitResourceUse use)
        {
            var said = new List<string>(2);
            if (use.UndecidedCount > 0)
                said.Add(use.UndecidedCount.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                         " resource dictionar" + (use.UndecidedCount == 1 ? "y" : "ies") +
                         " of the kept pages carried whole, because what is drawn through it could not all be read (the first: " +
                         use.FirstUndecided + ")");
            if (use.ReleasedCount > 0)
                said.Add(use.ReleasedCount.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                         " resource categor" + (use.ReleasedCount == 1 ? "y" : "ies") +
                         " carried whole, because something the extract carries names it without its drawing having been read");
            return string.Join("; ", said) + ": what other pages draw through them may be in the extract.";
        }

        /// <summary>
        /// What the collection walk refuses to follow a reference into: a page the extraction does
        /// not keep, and a field the form plan dropped.
        /// </summary>
        /// <remarks>
        /// <para><b>A page</b>, because a link annotation's <c>/Dest [pageRef /Fit]</c> and a widget's
        /// <c>/P</c> are ordinary references: following them would write another page's whole content
        /// stream into a "one page" extract — invisible in a viewer and readable with any text
        /// extractor. A
        /// page is the document's page list's (<see cref="PdfObjectHelper.IsPageObject"/>), not only
        /// a <c>/Type /Page</c>.</para>
        /// <para><b>A field</b>, for the same reason one channel over: a kept button's SubmitForm or
        /// ResetForm <c>/Fields</c>, a Hide action's <c>/T</c> and a kept field's <c>/AA</c> name other
        /// fields by reference, so following them would carry a field the plan dropped — its
        /// <c>/V</c>, and the appearance stream that draws it — into the extract of another page. A
        /// field a kept page lists in its <c>/Annots</c> is on that page and is never refused. When
        /// the field tree was too large to list, a dictionary that reads as a field is refused
        /// unless kept, the safe direction for a guard whose job is to withhold.</para>
        /// </remarks>
        internal sealed class CollectionGuard
        {
            private readonly PdfDocument _doc;
            private readonly HashSet<int> _keptPages;
            private readonly HashSet<int> _fieldNodes;
            private readonly bool _fieldNodesComplete;
            private readonly HashSet<int> _keptFields;

            /// <summary>A guard over pages alone, for a document with no form.</summary>
            internal CollectionGuard(PdfDocument doc, HashSet<int> keptPages)
            {
                _doc = doc;
                _keptPages = keptPages;
            }

            internal CollectionGuard(
                PdfDocument doc, HashSet<int> keptPages, IReadOnlyList<PdfPage> pages,
                (HashSet<int> Numbers, bool Complete) fieldNodes, AcroFormPageFilterResult plan)
                : this(doc, keptPages)
            {
                _fieldNodes = fieldNodes.Numbers;
                _fieldNodesComplete = fieldNodes.Complete;
                _keptFields = new HashSet<int>();
                if (plan != null)
                    foreach (var node in plan.KeptNodes)
                        if (node.ObjectNumber > 0) _keptFields.Add(node.ObjectNumber);
                foreach (var page in pages)
                    if (page?.GetAnnotations() is PdfArray annots)
                        foreach (var annot in annots)
                            if (annot is PdfReference listed) _keptFields.Add(listed.ReferenceObjectNumber);
            }

            /// <summary>What the kept pages draw of their resources; null leaves every resource entry to the walk.</summary>
            internal SplitResourceUse Resources { get; set; }

            /// <summary>Whether the walk leaves out <paramref name="key"/> of <paramref name="container"/>: a resource entry no kept page draws.</summary>
            internal bool Skips(PdfDictionary container, PdfName key)
                => Resources != null && Resources.Skips(container, key);

            internal bool Refuses(int number, PdfObject resolved)
            {
                if (!_keptPages.Contains(number) && PdfObjectHelper.IsPageObject(resolved, _doc)) return true;
                if (_fieldNodes == null || _keptFields.Contains(number)) return false;
                if (_fieldNodes.Contains(number)) return true;
                return !_fieldNodesComplete && resolved is PdfDictionary dict &&
                       AcroFormPageFilter.LooksLikeField(dict, _doc);
            }
        }

        // Indirect-reference cycles are bounded by `collected`; this caps the direct (within-object)
        // nesting depth, which the parser already refuses past the same limit.
        private static int MaxCollectDepth => PdfLimits.MaxObjectGraphDepth;

        /// <summary>
        /// How many values the collection walk has visited on the calling thread: the evidence of
        /// what an extraction walked, which its output cannot show.
        /// </summary>
        [ThreadStatic] internal static long CollectionSteps;

        /// <summary>
        /// Adds every object reachable from <paramref name="obj"/> to <paramref name="collected"/>,
        /// <b>except a page outside the extracted range and a field the form plan dropped</b>
        /// (<see cref="CollectionGuard"/>).
        /// </summary>
        /// <remarks>
        /// <para>A refused reference is neither collected nor followed, so the clone writes it as
        /// <see cref="PdfNull"/>. The kept pages are already in <paramref name="collected"/> when this
        /// runs, so their own references still resolve.</para>
        /// <para>A reference is a step on a work list, not a level of depth: counted against
        /// <see cref="MaxCollectDepth"/>, a chain of objects — a bead thread's <c>/N</c>, an action's
        /// <c>/Next</c> — would be cut about 128 links in and its tail written as
        /// <see cref="PdfNull"/>. The depth is each object's own nesting.</para>
        /// </remarks>
        private static void CollectReferencedObjects(PdfObject obj, PdfDocument doc,
            HashSet<int> collected, CollectionGuard keptPages)
        {
            var pending = new Stack<(PdfObject Value, int Depth)>();
            pending.Push((obj, 0));
            while (pending.Count > 0)
            {
                var (value, depth) = pending.Pop();
                CollectionSteps++;
                if (value == null || value is PdfNull)
                    continue;
                if (depth > MaxCollectDepth)
                {
                    // Counted, not silent: the objects below this point are not collected, so the
                    // clone writes PdfNull where a subtree was — a legal object, and therefore invisible.
                    PdfObjectHelper.DepthTruncations++;
                    continue;
                }

                if (value is PdfReference reference)
                {
                    int refObjNum = reference.ReferenceObjectNumber;
                    if (refObjNum <= 0 || collected.Contains(refObjNum))
                        continue;

                    var resolved = doc.GetObject(refObjNum);
                    if (keptPages != null && keptPages.Refuses(refObjNum, resolved))
                        continue;

                    collected.Add(refObjNum);

                    if (resolved != null && !(resolved is PdfNull))
                        pending.Push((resolved, 0));
                    continue;
                }

                if (value is PdfDictionary dict)    // a stream's dictionary too
                {
                    foreach (var kvp in dict)
                        if (kvp.Key != PdfName.Parent && !(keptPages != null && keptPages.Skips(dict, kvp.Key)))
                            pending.Push((kvp.Value, depth + 1));
                }
                else if (value is PdfArray array)
                {
                    for (int i = 0; i < array.Count; i++)
                        pending.Push((array[i], depth + 1));
                }
            }
        }
    }
}
