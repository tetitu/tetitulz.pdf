// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Plugins.PageEdits
{
    /// <summary>
    /// Merges multiple PDF documents into a single PDF.
    /// </summary>
    public static class PdfMerger
    {
        [ThreadStatic] private static string _lastNameTreeWarning;
        [ThreadStatic] private static string _lastFormWarning;

        /// <summary>
        /// Maximum suffixes tried when a merged name-tree key collides, per key.
        /// </summary>
        /// <remarks>
        /// A legitimate merge needs one attempt per colliding source, so even a hundred-document
        /// merge stays far under this. It exists because the search is linear in the number of
        /// names already taken while the number of collisions is chosen by the FILE: a source that
        /// repeats one key N times costs O(N²) here, and N is bounded only by
        /// <see cref="PdfLimits.MaxNameTreeEntries"/>.
        /// </remarks>
        private const int MaxRenameAttempts = 1000;

        /// <summary>
        /// Why the merged catalog <c>/Names</c> tree is not a faithful union of the sources', or
        /// <c>null</c> when it is. Read it after <see cref="Merge(Stream, Stream[])"/> on the
        /// calling thread; it is reset at the start of every merge, on every path, so a value here
        /// always belongs to the merge that just ran.
        /// </summary>
        /// <remarks>
        /// Two things get reported: a flatten cut short by
        /// <see cref="PdfLimits.MaxNameTreeEntries"/>, and keys that collided between sources and
        /// were suffixed. Both leave a valid document, and both are invisible in the output — which
        /// is exactly why they are reported rather than left for the caller to discover.
        /// </remarks>
        public static string LastNameTreeWarning => _lastNameTreeWarning;

        /// <summary>
        /// Why the merged <c>/AcroForm</c> is not a faithful union of the sources' forms, or
        /// <c>null</c> when it is. Read it after a merge on the calling thread; it is reset at the
        /// start of every merge, on every path.
        /// </summary>
        /// <remarks>
        /// Reports terminal fields whose fully-qualified name was already taken by an earlier
        /// source and were therefore suffixed. §12.7.3.2 makes two fields with the same
        /// fully-qualified name <i>one</i> field that "shall have the same value", so keeping both
        /// silently would hand a viewer a contradiction it resolves by picking one value and
        /// propagating it to the other page — the confidently-wrong answer the <c>/Names</c> merge
        /// already refuses for name-tree keys.
        /// </remarks>
        public static string LastFormWarning => _lastFormWarning;

        [ThreadStatic] private static string _lastXfaWarning;

        /// <summary>
        /// Non-null when a source carried an <c>/AcroForm /XFA</c> packet that the merged form does
        /// not; null when no source had one. Read it after a merge on the calling thread; it is
        /// reset at the start of every merge, on every path.
        /// </summary>
        /// <remarks>
        /// <para><b>A page superset is not a form XFA can describe</b> — the splitter's reasoning
        /// (<c>PdfSplitter.LastXfaWarning</c>) in the other direction. XFA (§12.7.8) holds the whole
        /// form as XML: its layout, its field definitions and, in a static form, its values. The
        /// merged document's fields are the union of every source's, and no source's packet
        /// describes that union.</para>
        ///
        /// <para><b>So the first source's packet is dropped rather than carried.</b> The merged form
        /// is a clone of the first source's dictionary; carried with it, <c>/XFA</c> would hand a
        /// viewer that honours XFA a form covering only the first input's pages, laid over a
        /// document containing all of them. The AcroForm fields
        /// themselves are merged exactly, so a viewer reading those is unaffected; what is lost is
        /// the XFA view, and this is where a caller learns it.</para>
        /// </remarks>
        public static string LastXfaWarning => _lastXfaWarning;

        [ThreadStatic] private static string _lastStructureWarning;

        /// <summary>
        /// Why the merged logical structure is not a faithful union of the sources' tag trees, or
        /// <c>null</c> when it is.
        /// </summary>
        /// <remarks>
        /// The cases it reports are ones a caller cannot see from the output: an input that was not
        /// tagged, so <c>/MarkInfo /Marked</c> was withheld; a <c>/RoleMap</c> that maps a tag
        /// differently from an earlier input; a <c>/ParentTree</c> too large to read in full. A
        /// merged document whose structure is silently partial looks exactly like one whose sources
        /// were.
        /// </remarks>
        public static string LastStructureWarning => _lastStructureWarning;

        [ThreadStatic] private static string _lastOptionalContentWarning;

        /// <summary>
        /// Why the merged optional content is not a faithful union of the sources' layers, or
        /// <c>null</c> when it is.
        /// </summary>
        /// <remarks>
        /// Both cases it reports are decisions the merge had to make because a source's
        /// configuration describes its own document: alternate configurations that were dropped,
        /// and a <c>/BaseState /OFF</c> that was rewritten as an explicit list. Neither is visible
        /// in the output, and both change what a viewer offers.
        /// </remarks>
        public static string LastOptionalContentWarning => _lastOptionalContentWarning;

        [ThreadStatic] private static string _lastPageLabelWarning;

        /// <summary>
        /// Why the merged page labels are not a faithful union of the sources', or <c>null</c> when
        /// they are.
        /// </summary>
        /// <remarks>
        /// Chiefly that two sources both numbered their pages from 1, so the merged document now
        /// carries the same label twice. Nothing in the output shows that, and it is the difference
        /// between "go to page 3" resolving and being ambiguous.
        /// </remarks>
        public static string LastPageLabelWarning => _lastPageLabelWarning;

        [ThreadStatic] private static string _lastOutputIntentWarning;

        /// <summary>
        /// Which output intents were dropped as contradictory, or <c>null</c> when none were.
        /// </summary>
        /// <remarks>
        /// A document has one printing condition per subtype. Two sources prepared for different
        /// ones cannot both describe the merged document, and a prepress workflow given both may
        /// colour-manage with either.
        /// </remarks>
        public static string LastOutputIntentWarning => _lastOutputIntentWarning;

        [ThreadStatic] private static string _lastObjectWarning;

        /// <summary>
        /// How many source objects could not be read, and which sources contributed no pages, or
        /// <c>null</c> when every object was read and every source gave pages.
        /// </summary>
        /// <remarks>
        /// An unreadable object is left out, and a reference to it is written as null. A source whose
        /// page tree cannot be read opens with no pages and is named here, so the merge is never a
        /// silent partial result. Its leaf pages still travel, unreachable.
        /// </remarks>
        public static string LastObjectWarning => _lastObjectWarning;


        /// <summary>
        /// Clears every warning slot. Each <c>Merge</c> calls it before anything that can throw: a
        /// stale warning read after a failed merge would describe the previous merge on this thread.
        /// </summary>
        private static void ResetWarnings()
        {
            _lastNameTreeWarning = null;
            _lastFormWarning = null;
            _lastStructureWarning = null;
            _lastOptionalContentWarning = null;
            _lastPageLabelWarning = null;
            _lastOutputIntentWarning = null;
            _lastXfaWarning = null;
            _lastObjectWarning = null;
        }

        /// <summary>
        /// Merges multiple PDF files into a single output file.
        /// </summary>
        public static void Merge(string outputPath, params string[] inputPaths)
        {
            ResetWarnings();
            if (outputPath == null) throw new ArgumentNullException(nameof(outputPath));
            if (inputPaths == null) throw new ArgumentNullException(nameof(inputPaths));
            if (inputPaths.Length == 0) throw new ArgumentException("At least one input file is required.", nameof(inputPaths));

            // INPUTS FIRST, OUTPUT LAST. FileMode.Create truncates: run before the inputs are open,
            // a missing file, a sharing violation or a permission error would leave a ZERO-BYTE file
            // where the caller's previous output was, from a call that then throws.
            var streams = new List<FileStream>();
            try
            {
                foreach (var path in inputPaths)
                {
                    if (path == null) throw new ArgumentException("Input path cannot be null.", nameof(inputPaths));

                    // …and an input that IS the output is refused rather than truncated: the merge
                    // reads every source object, so emptying one of them first guarantees failure.
                    if (SamePath(path, outputPath))
                        throw new ArgumentException(
                            $"Input '{path}' is also the output path. Merging reads every source " +
                            "object, and opening the output truncates it, so the merge would read " +
                            "an empty file. Write to a different path.", nameof(inputPaths));

                    streams.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
                }

                using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    Merge(output, streams.ToArray<Stream>());
            }
            finally
            {
                foreach (var s in streams)
                    s.Dispose();
            }
        }

        /// <summary>
        /// Whether two paths name the same file, as far as can be told without opening them.
        /// </summary>
        /// <remarks>
        /// <see cref="Path.GetFullPath(string)"/> normalises separators, <c>.</c> and <c>..</c>;
        /// case-insensitivity matches Windows and is the safe direction elsewhere, because a false
        /// match is a refusal the caller can work around and a false miss truncates their input.
        /// A symlink or a hard link to the same file still slips through — hence "as far as can be
        /// told".
        /// </remarks>
        private static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                // An unusable path is the FileStream constructor's problem to report, with a better
                // message than anything this comparison could produce.
                return false;
            }
        }

        /// <summary>
        /// Merges multiple PDF streams into a single output stream.
        /// </summary>
        public static void Merge(Stream output, params Stream[] inputs)
        {
            ResetWarnings();
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (inputs == null) throw new ArgumentNullException(nameof(inputs));
            if (inputs.Length == 0) throw new ArgumentException("At least one input stream is required.", nameof(inputs));

            var documents = new List<PdfDocument>();
            try
            {
                for (int i = 0; i < inputs.Length; i++)
                {
                    var input = inputs[i];
                    if (input == null) throw new ArgumentException("Input stream cannot be null.", nameof(inputs));

                    var doc = PdfDocument.Open(input);
                    documents.Add(doc);

                    // Fail closed on encrypted input, as every other whole-file rewrite in this
                    // repository does (PdfSanitizer, PdfLinearizer, PdfRedactionEngine).
                    //
                    // PdfDocument does not load the catalog or the page tree of an encrypted file,
                    // so such an input reports ZERO pages rather than throwing — and a merge would
                    // quietly leave out everything the encrypted source should contribute.
                    if (doc.IsEncrypted)
                        throw new PdfEncryptedDocumentException(
                            $"Input {i} is encrypted, and merging rewrites every object with new " +
                            "object numbers, so its content cannot be carried across. Decrypt it first " +
                            "(Tetitulz.Pdf.Encryption.PdfDecryptor), merge, then re-encrypt if required.");
                }

                MergeDocuments(output, documents);
            }
            finally
            {
                foreach (var doc in documents)
                    doc.Dispose();
            }
        }

        private static void MergeDocuments(Stream output, List<PdfDocument> documents)
        {
            int maxMajor = 1;
            int maxMinor = 4;
            foreach (var doc in documents)
            {
                if (doc.Version.Major > maxMajor ||
                    (doc.Version.Major == maxMajor && doc.Version.Minor > maxMinor))
                {
                    maxMajor = doc.Version.Major;
                    maxMinor = doc.Version.Minor;
                }
            }

            var objectMaps = new List<Dictionary<int, int>>();
            var allObjects = new List<(int newObjNum, PdfObject obj, int docIndex)>();
            var pageObjectNumbers = new List<int>();
            // Maps (docIndex, original page object number) → the resolved PdfPage,
            // whose getters supply inherited /MediaBox /Resources /Rotate /CropBox.
            // The intermediate /Pages nodes that carry those inheritable attributes
            // are skipped during flattening, so we must materialise them onto each
            // leaf page or the merged pages lose their size/resources/rotation.
            var pageLookup = new Dictionary<(int, int), PdfPage>();

            // A page held directly in /Kids (Core's page list reads one): new object number → page.
            var directPages = new Dictionary<int, PdfPage>();
            int unreadableObjects = 0;
            string firstUnreadable = null;
            var pagelessSources = new List<int>();

            int nextObjectNumber = 1;

            for (int docIndex = 0; docIndex < documents.Count; docIndex++)
            {
                var doc = documents[docIndex];
                var objectMap = new Dictionary<int, int>();
                int source = docIndex;

                // An object the parser cannot read is left out — a font, an image, a page tree —
                // a reference to it is written as null, and the merge reports it.
                foreach (var kvp in doc.Parser.EnumerateAllObjects((n, ex) =>
                {
                    unreadableObjects++;
                    firstUnreadable ??= $"input {source}'s object {n}: {ex.Message}";
                }))
                {
                    int originalObjNum = kvp.Key;
                    var obj = kvp.Value;

                    if (PdfObjectHelper.IsCatalogObject(obj, doc) || PdfObjectHelper.IsPagesTreeNode(obj, doc))
                        continue;

                    int newObjNum = nextObjectNumber++;
                    objectMap[originalObjNum] = newObjNum;
                    allObjects.Add((newObjNum, obj, docIndex));
                }

                foreach (var page in doc.Pages)
                {
                    int origPageObjNum = page.Dictionary.ObjectNumber;
                    if (origPageObjNum > 0 && objectMap.ContainsKey(origPageObjNum))
                    {
                        pageObjectNumbers.Add(objectMap[origPageObjNum]);
                        pageLookup[(docIndex, origPageObjNum)] = page;
                    }
                    else if (origPageObjNum <= 0)
                    {
                        // §7.7.3.2 asks for indirect references, but a direct page has no number to
                        // carry: it is written as a new object.
                        int newObjNum = nextObjectNumber++;
                        allObjects.Add((newObjNum, page.Dictionary, docIndex));
                        pageObjectNumbers.Add(newObjNum);
                        directPages[newObjNum] = page;
                    }
                }
                if (doc.Pages.Count == 0) pagelessSources.Add(docIndex);

                objectMaps.Add(objectMap);
            }

            if (unreadableObjects > 0 || pagelessSources.Count > 0)
                _lastObjectWarning =
                    (unreadableObjects > 0
                        ? $"{unreadableObjects} object(s) could not be read and were left out, and a " +
                          $"reference to one is written as null; the first was {firstUnreadable}. "
                        : string.Empty) +
                    (pagelessSources.Count > 0
                        ? $"Input(s) {string.Join(", ", pagelessSources)} contributed no pages" +
                          (unreadableObjects > 0 ? ", which an unreadable page tree explains" : string.Empty) + "."
                        : string.Empty);

            int catalogObjNum = nextObjectNumber++;
            int pagesTreeObjNum = nextObjectNumber++;

            var remappedObjects = new List<(int objNum, PdfObject obj)>();
            // new object number → the source document it came from. Needed by the /Names merge,
            // which must rewrite a renamed destination only in the document whose key it renamed.
            var docOfNewObject = new Dictionary<int, int>();

            // A direct value several pages inherit is written once, named by each of them.
            var inherited = new PdfObjectHelper.InheritedCopies(() => nextObjectNumber++, remappedObjects);
            foreach (var entry in pageLookup) inherited.Count(entry.Value, documents[entry.Key.Item1]);
            foreach (var (newObjNum, _, docIndex) in allObjects)
                if (directPages.TryGetValue(newObjNum, out var direct)) inherited.Count(direct, documents[docIndex]);

            for (int i = 0; i < allObjects.Count; i++)
            {
                var (newObjNum, originalObj, docIndex) = allObjects[i];
                var objectMap = objectMaps[docIndex];
                docOfNewObject[newObjNum] = docIndex;

                var cloned = PdfObjectHelper.DeepClone(originalObj, objectMap, pagesTreeObjNum, doc: documents[docIndex]);
                // If this is a leaf page, fold in any inherited attributes that the
                // skipped /Pages ancestors supply in the source.
                if (cloned is PdfDictionary clonedPage
                    && (directPages.TryGetValue(newObjNum, out var srcPage)
                        || pageLookup.TryGetValue((docIndex, originalObj.ObjectNumber), out srcPage)))
                {
                    PdfObjectHelper.MaterializeInheritedPageAttributes(clonedPage, srcPage, objectMap, pagesTreeObjNum,
                        documents[docIndex], inherited);
                }
                PdfObjectHelper.Stamp(cloned, originalObj, newObjNum);
                remappedObjects.Add((newObjNum, cloned));
            }

            var kidsArray = new PdfArray();
            foreach (int pageObjNum in pageObjectNumbers)
            {
                kidsArray.Add(new PdfReference(pageObjNum, 0));
            }

            // One implementation, in PdfObjectHelper.BuildPageTreeAndCatalog. /Count comes from
            // the kids array by construction now, rather than from two callers agreeing.
            var (pagesDict, catalogDict) =
                PdfObjectHelper.BuildPageTreeAndCatalog(kidsArray, pagesTreeObjNum, catalogObjNum);

            // Carry over interactive form fields: concatenate every source's /AcroForm /Fields and
            // /CO (remapped through its object map) into one form cloned from the first source's;
            // /DR is the union, /NeedAppearances the OR, and a later source's own /DA and /Q are
            // written onto its fields.
            var combinedFields = new PdfArray();
            PdfDictionary templateForm = null;
            int templateDocIndex = 0;
            // The cloned dictionaries by new object number: a colliding field name is repaired on
            // the CLONE, never on the source document being read.
            var clonedByNum = new Dictionary<int, PdfDictionary>();
            foreach (var (n, o) in remappedObjects)
                if (o is PdfDictionary cd) clonedByNum[n] = cd;
            // Fully-qualified name of every top-level field carried so far → the source that took
            // it. §12.7.3.2: two fields with the same fully-qualified name are ONE field and "shall
            // have the same value", so concatenating two filled copies of one template would hand
            // a viewer two /V values for one field — it picks one and shows it on both pages.
            var takenFieldNames = new Dictionary<string, int>(StringComparer.Ordinal);
            int renamedFields = 0;
            int unrenameableFields = 0;
            // The union of every source's /DR, and the OR of their /NeedAppearances. Every later
            // source's fields name fonts by resource name in their /DA, so a form keeping only the
            // first source's /DR would carry a field written `/DA (/Arial 10 Tf 0 g)` into a form
            // that defines no /Arial, and any appearance regeneration would draw it in a fallback
            // face or not at all.
            var mergedDr = new PdfDictionary();
            var drCollisions = new List<string>();
            // Kept distinct by a set: a search of the list made n colliding names n² / 2 comparisons.
            var drCollisionSet = new HashSet<string>(StringComparer.Ordinal);
            bool needAppearances = false;
            // Sources carrying an /XFA packet. Counted rather than flagged, because the caller's
            // question is how much of the form description was lost, not merely whether any was.
            int xfaSources = 0;
            var listedFields = new HashSet<int>();
            // Every source's /CO, in source order (Table 218: the calculated fields, in the order
            // their calculations run). The template's alone left a later source's totals out.
            var combinedCalculationOrder = new PdfArray();
            var listedCalculations = new HashSet<int>();
            for (int d = 0; d < documents.Count; d++)
            {
                var srcCatalog = documents[d].Catalog?.Dictionary;
                if (srcCatalog == null) continue;
                if (!(documents[d].ResolveObject(srcCatalog[PdfName.AcroForm]) is PdfDictionary af))
                    continue;
                if (templateForm == null) { templateForm = af; templateDocIndex = d; }

                if (documents[d].ResolveObject(af[PdfName.CO]) is PdfArray co)
                    foreach (var c in co)
                        if (c is PdfReference cr && objectMaps[d].TryGetValue(cr.ReferenceObjectNumber, out int cn)
                            && listedCalculations.Add(cn))
                            combinedCalculationOrder.Add(new PdfReference(cn, 0));

                if (XfaPacket.IsPresent(af)) xfaSources++;

                if (documents[d].ResolveObject(af[PdfName.NeedAppearances]) is PdfBoolean na && na.Value)
                    needAppearances = true;
                MergeDefaultResources(documents[d], af, objectMaps[d], pagesTreeObjNum,
                    mergedDr, drCollisions, drCollisionSet);

                // The merged form is the template's, so its /DA and /Q are the defaults every field
                // without its own inherits (§12.7.3.3). A later source whose own differ has them
                // written onto its top-level fields that lack them — both keys are inheritable, so
                // the fields' kids keep the default their form gave them.
                PdfObject laterDa = null, laterQ = null;
                if (af != templateForm)
                {
                    PdfObject da = documents[d].ResolveObject(af[PdfName.DA]);
                    if (da is PdfString daText
                        && !(documents[templateDocIndex].ResolveObject(templateForm[PdfName.DA]) is PdfString t
                             && t.GetText() == daText.GetText()))
                        laterDa = new PdfString(daText.RawBytes, daText.IsHex);
                    int q = QuaddingOf(documents[d], af), templateQ = QuaddingOf(documents[templateDocIndex], templateForm);
                    if (q != templateQ) laterQ = new PdfNumber(q);
                }

                var untitledSeen = new HashSet<PdfDictionary>(PdfReferenceEquality.Instance);
                if (documents[d].ResolveObject(af[PdfName.Fields]) is PdfArray fields)
                    foreach (var f in fields)
                        // A field the array lists twice is one field (§12.7.3.2), listed and renamed
                        // once: renaming each repeat against the names already minted would cost a
                        // thousand attempts a copy, with no cap on the copies.
                        if (f is PdfReference fr && objectMaps[d].TryGetValue(fr.ReferenceObjectNumber, out int nn)
                            && listedFields.Add(nn))
                        {
                            combinedFields.Add(new PdfReference(nn, 0));
                            clonedByNum.TryGetValue(nn, out var clonedField);
                            if (clonedField != null)
                            {
                                if (laterDa != null && clonedField[PdfName.DA] == null) clonedField.Set(PdfName.DA, laterDa);
                                if (laterQ != null && clonedField[PdfName.Q] == null) clonedField.Set(PdfName.Q, laterQ);
                            }
                            var map = objectMaps[d];
                            DeduplicateFieldNames(documents[d], f, clonedField, d, takenFieldNames,
                                kid => kid is PdfReference kr && map.TryGetValue(kr.ReferenceObjectNumber, out int kn)
                                       && clonedByNum.TryGetValue(kn, out var kc) ? kc : null,
                                untitledSeen, ref renamedFields, ref unrenameableFields);
                        }
            }
            if (renamedFields > 0 || unrenameableFields > 0 || drCollisions.Count > 0)
                _lastFormWarning =
                    (renamedFields > 0
                        ? $"{renamedFields} form field name(s) were already taken by an earlier " +
                          "source and were suffixed rather than merged into one field. "
                        : string.Empty) +
                    (unrenameableFields > 0
                        ? $"{unrenameableFields} field name(s) collided more than {MaxRenameAttempts} " +
                          "times and were left duplicated; the merged form has two fields sharing a " +
                          "fully-qualified name, which §12.7.3.2 makes one field. "
                        : string.Empty) +
                    (drCollisions.Count > 0
                        ? "Default-resource name(s) " + JoinedUnderCap(drCollisions) +
                          " are defined by more than one source; the first source's definition was " +
                          "kept, so a later source's /DA naming one of them may resolve to a " +
                          "different resource."
                        : string.Empty);
            if (combinedFields.Count > 0 && templateForm != null)
            {
                var newForm = PdfObjectHelper.DeepClone(templateForm, objectMaps[templateDocIndex], pagesTreeObjNum)
                              as PdfDictionary ?? new PdfDictionary();
                newForm.Set(PdfName.Fields, combinedFields);
                if (combinedCalculationOrder.Count > 0) newForm.Set(PdfName.CO, combinedCalculationOrder);
                else newForm.Remove(PdfName.CO);
                if (mergedDr.Count > 0) newForm.Set(PdfName.DR, mergedDr);
                // §12.7.2: /NeedAppearances asks the viewer to regenerate appearances. It is ORed
                // rather than taken from the template, because a later source that needed
                // regeneration would otherwise stop asking for it and render its fields blank.
                if (needAppearances) newForm.Set(PdfName.NeedAppearances, PdfBoolean.True);
                // Not a union and not subsettable: see LastXfaWarning. The clone inherited the
                // template source's packet, which describes that source's form alone.
                newForm.Remove(PdfName.XFA);
                int acroFormObjNum = nextObjectNumber++;
                newForm.ObjectNumber = acroFormObjNum;
                newForm.GenerationNumber = 0;
                remappedObjects.Add((acroFormObjNum, newForm));
                catalogDict.Set(PdfName.AcroForm, new PdfReference(acroFormObjNum, 0));
            }

            if (xfaSources > 0)
                _lastXfaWarning =
                    $"{xfaSources} source(s) carried an /AcroForm /XFA packet, which the merged " +
                    "form does not: XFA describes a whole form, and no source's packet describes " +
                    "the union of every source's fields. The merged /AcroForm /Fields are complete; " +
                    "the XFA view of them is not present in the output.";

            // Carry over the catalog /Names trees (named destinations, embedded files, document-level
            // JavaScript, …). This runs BEFORE the outline merge because a bookmark can reach its
            // page through a named destination, and a collision resolved here renames that
            // destination in the very items the outline merge is about to re-chain.
            int namesObjNum = MergeNames(documents, objectMaps, remappedObjects, docOfNewObject,
                pagesTreeObjNum, ref nextObjectNumber);
            if (namesObjNum > 0)
                catalogDict.Set(PdfName.Names, new PdfReference(namesObjNum, 0));

            // Carry over the document outline (bookmarks). Every source's /Outlines objects were
            // already cloned + reference-remapped above (so each item's destination points at the
            // merged page); here we combine the per-source trees under one fresh root and re-chain
            // the top-level items.
            int outlinesObjNum = MergeOutlines(documents, objectMaps, remappedObjects, ref nextObjectNumber);
            if (outlinesObjNum > 0)
                catalogDict.Set(PdfName.Outlines, new PdfReference(outlinesObjNum, 0));

            // Carry over the logical structure (the tag tree). Last, because it MUTATES objects the
            // steps above cloned - every /StructParents in a later source shifts - and because its
            // /ParentTree values are the very structure elements those steps have finished remapping.
            int structRootObjNum = MergeStructureTrees(documents, objectMaps, remappedObjects,
                docOfNewObject, pagesTreeObjNum, catalogDict, ref nextObjectNumber);
            if (structRootObjNum > 0)
                catalogDict.Set(PdfName.StructTreeRoot, new PdfReference(structRootObjNum, 0));

            // Carry over optional content (layers). Without it the group dictionaries survive and
            // the configuration that HIDES any of them does not, so every layer a source had
            // switched off comes out visible.
            int ocPropertiesObjNum = MergeOptionalContent(documents, objectMaps, remappedObjects,
                pagesTreeObjNum, ref nextObjectNumber);
            if (ocPropertiesObjNum > 0)
                catalogDict.Set(PdfName.OCProperties, new PdfReference(ocPropertiesObjNum, 0));

            // Page labels: a label key is a PAGE INDEX, so it shifts by the pages contributed
            // before it rather than by the previous source key span.
            int pageLabelsObjNum = MergePageLabels(documents, objectMaps, remappedObjects,
                pagesTreeObjNum, ref nextObjectNumber);
            if (pageLabelsObjNum > 0)
                catalogDict.Set(PdfName.PageLabels, new PdfReference(pageLabelsObjNum, 0));

            // Output intents, one per subtype - see MergeOutputIntents for why two of a subtype is
            // a contradiction rather than a union.
            PdfArray outputIntents = MergeOutputIntents(documents, objectMaps, pagesTreeObjNum);
            if (outputIntents != null)
                catalogDict.Set(PdfName.OutputIntents, outputIntents);

            // NO clone-depth warning here, deliberately. PdfObjectHelper.DeepClone maps an
            // indirect reference rather than following it, so its depth grows only through DIRECT
            // nesting — and PdfParser.MaxParseDepth is the SAME PdfLimits.MaxObjectGraphDepth,
            // enforced by throwing. No document this merge can load has ever reached the clone cap,
            // so a property reporting it would be surface that can never be non-null.

            var writer = new PdfWriter(output);
            writer.WriteHeader(new PdfVersion(maxMajor, maxMinor));

            long xrefOffset = PdfObjectHelper.WriteObjectsPagesCatalogAndXref(
                writer, remappedObjects, pagesTreeObjNum, pagesDict, catalogObjNum, catalogDict);

            var idArray = PdfTrailerBuilder.NewFileId();

            var trailer = new PdfDictionary();
            // /Size must cover every object actually written — the AcroForm and /Outlines
            // carry-over allocate object numbers after the initial count, so use the final
            // counter, not the early `totalObjects` snapshot.
            trailer.Set(PdfName.Size, new PdfNumber(nextObjectNumber));
            trailer.Set(PdfName.Root, new PdfReference(catalogObjNum, 0));
            trailer.Set(PdfName.ID, idArray);

            writer.WriteTrailer(trailer, xrefOffset);
        }

        /// <summary>How many colliding names <see cref="LastFormWarning"/> lists before it counts the rest.</summary>
        internal const int MaxListedCollisions = 20;

        /// <summary>
        /// A form's <c>/Q</c>: 0 (left), 1 (centred) or 2 (right), and 0 when absent or anything
        /// else — it is written onto the merged fields, so a value no reader can use must not travel.
        /// <c>(int)LongValue</c> read 4 294 967 297 as 1 and centred them.
        /// </summary>
        private static int QuaddingOf(PdfDocument doc, PdfDictionary form)
            => doc.ResolveObject(form[PdfName.Q]) is PdfNumber n && n.IsInteger && n.LongValue >= 0 && n.LongValue <= 2
                ? (int)n.LongValue
                : 0;

        /// <summary>The first <see cref="MaxListedCollisions"/> names, and how many more: joined whole, a file chose the warning's length.</summary>
        private static string JoinedUnderCap(List<string> names)
            => names.Count <= MaxListedCollisions
                ? string.Join(", ", names)
                : string.Join(", ", names.GetRange(0, MaxListedCollisions)) + " and " +
                  (names.Count - MaxListedCollisions).ToString(System.Globalization.CultureInfo.InvariantCulture) + " more";

        /// <summary>
        /// Unions one source's <c>/DR</c> (§12.7.2's default resource dictionary) into
        /// <paramref name="into"/>, one sub-dictionary — <c>/Font</c>, <c>/XObject</c>,
        /// <c>/ExtGState</c>, … — at a time.
        /// </summary>
        /// <remarks>
        /// A colliding resource NAME keeps the earlier source's definition and is reported, rather
        /// than being suffixed the way a field name is: a <c>/DR</c> key is named from inside every
        /// <c>/DA</c> string that uses it, and rewriting those strings is a content-level edit this
        /// merge does not do. Reporting a name that now means something else is the honest half of
        /// that; silently keeping only the first source's whole <c>/DR</c> was neither.
        /// </remarks>
        private static void MergeDefaultResources(
            PdfDocument source, PdfDictionary form, Dictionary<int, int> objectMap,
            int pagesTreeObjNum, PdfDictionary into, List<string> collisions, HashSet<string> collisionSet)
        {
            if (!(source.ResolveObject(form[PdfName.DR]) is PdfDictionary dr)) return;

            foreach (var category in dr)
            {
                if (!(source.ResolveObject(category.Value) is PdfDictionary group))
                {
                    if (!into.ContainsKey(category.Key))
                        into.Set(category.Key, PdfObjectHelper.DeepClone(
                            category.Value, objectMap, pagesTreeObjNum));
                    continue;
                }

                if (!(into[category.Key] is PdfDictionary target))
                    into.Set(category.Key, target = new PdfDictionary());

                foreach (var entry in group)
                {
                    if (target.ContainsKey(entry.Key))
                    {
                        string name = "/" + category.Key.Value + " /" + entry.Key.Value;
                        if (collisionSet.Add(name)) collisions.Add(name);
                        continue;
                    }
                    target.Set(entry.Key,
                        PdfObjectHelper.DeepClone(entry.Value, objectMap, pagesTreeObjNum));
                }
            }
        }

        private enum FieldNameOutcome
        {
            /// <summary>The name was free, or the collision is one source's own business.</summary>
            Unchanged,
            /// <summary>The clone's <c>/T</c> was suffixed so the merged form keeps unique names.</summary>
            Renamed,
            /// <summary>Every suffix was taken; the duplicate survives and must be reported.</summary>
            Exhausted,
        }

        /// <summary>
        /// Keeps the merged <c>/AcroForm</c>'s fully-qualified field names unique (§12.7.3.2) by
        /// suffixing the later source's terminal <c>/T</c>, exactly as <see cref="MergeNames"/> does
        /// for name-tree keys.
        /// </summary>
        /// <remarks>
        /// <para>Only the entries of <c>/Fields</c> are inspected, because the fully-qualified name
        /// of a top-level field <i>is</i> its <c>/T</c> — renaming it renames its whole subtree
        /// consistently, so a widget or a child field needs no separate repair.</para>
        /// <para>A collision <b>within one source</b> is left alone: those two dictionaries were
        /// already one field before the merge, and renaming one would change the behaviour of a
        /// document the caller did not ask to alter.</para>
        /// <para>A field with no <c>/T</c> adds no segment to its kids' names (§12.7.3.2), so its
        /// titled kids are top-level names of the merged form, and are deduplicated in its place —
        /// down to <see cref="AcroFormPageFilter.MaxFieldDepth"/>. Two sources each holding an
        /// untitled parent of <c>Total</c> merged into two fields of one name.</para>
        /// </remarks>
        private static void DeduplicateFieldNames(
            PdfDocument source, PdfObject sourceField, PdfDictionary clonedField,
            int docIndex, Dictionary<string, int> taken, Func<PdfObject, PdfDictionary> cloneOf,
            HashSet<PdfDictionary> untitledSeen, ref int renamed, ref int exhausted, int depth = 0)
        {
            FieldNameVisits++;
            var srcField = source.ResolveObject(sourceField) as PdfDictionary;
            if (srcField == null) return;
            if (source.ResolveObject(srcField[PdfName.T]) is PdfString)
            {
                switch (DeduplicateFieldName(source, srcField, clonedField, docIndex, taken))
                {
                    case FieldNameOutcome.Renamed: renamed++; break;
                    case FieldNameOutcome.Exhausted: exhausted++; break;
                }
                return;
            }
            // Each untitled node once a source: one shared by two parents at every level was walked
            // once a path, as the FDF reader's was.
            if (depth >= AcroFormPageFilter.MaxFieldDepth || !untitledSeen.Add(srcField)
                || !(source.ResolveObject(srcField[PdfName.Kids]) is PdfArray kids)) return;
            foreach (var kid in kids)
                DeduplicateFieldNames(source, kid, cloneOf(kid), docIndex, taken, cloneOf,
                    untitledSeen, ref renamed, ref exhausted, depth + 1);
        }

        /// <summary>Fields <see cref="DeduplicateFieldNames"/> has visited on the calling thread: the evidence that an untitled node is read once.</summary>
        [ThreadStatic] internal static long FieldNameVisits;

        private static FieldNameOutcome DeduplicateFieldName(
            PdfDocument source, PdfObject sourceField, PdfDictionary clonedField,
            int docIndex, Dictionary<string, int> taken)
        {
            var srcField = source.ResolveObject(sourceField) as PdfDictionary;
            if (!(source.ResolveObject(srcField?[PdfName.T]) is PdfString t)) return FieldNameOutcome.Unchanged;

            string name = PdfObjectHelper.NameKeyLookup(t.RawBytes);
            if (!taken.TryGetValue(name, out int owner))
            {
                taken[name] = docIndex;
                return FieldNameOutcome.Unchanged;
            }
            if (owner == docIndex || clonedField == null) return FieldNameOutcome.Unchanged;

            // Bounded for the same reason the name-tree rename is: how many times one name repeats
            // is chosen by the FILE, and each attempt walks the names already minted.
            for (int attempt = 1; attempt <= MaxRenameAttempts; attempt++)
            {
                byte[] renamed = PdfObjectHelper.AppendToNameKey(t.RawBytes,
                    attempt == 1 ? "." + docIndex : "." + docIndex + "." + attempt);
                string lookup = PdfObjectHelper.NameKeyLookup(renamed);
                if (taken.ContainsKey(lookup)) continue;
                taken[lookup] = docIndex;
                clonedField.Set(PdfName.T, new PdfString(renamed, t.IsHex));
                return FieldNameOutcome.Renamed;
            }
            return FieldNameOutcome.Exhausted;
        }

        // Combines every source document's catalog /Names trees into one name dictionary.
        //
        // Each category (/Dests, /EmbeddedFiles, /JavaScript, …) is flattened out of every source,
        // concatenated in document order, then re-emitted as ONE flat leaf: `<< /Names [k v k v …] >>`
        // with no /Kids and no /Limits. Re-using a source's own leaf objects is the tempting
        // shortcut and it breaks — an intermediate node's /Limits would then describe a key range
        // that no longer matches the merged set, and a reader's binary search over it silently
        // misses entries.
        //
        // Rather than hard-coding the category list from Table 31, every key present on any
        // source's name dictionary is carried: the categories are all name trees, and a fixed list
        // would silently drop a category added by a later PDF version.
        //
        // Returns the new name dictionary's object number, or 0 when no source has one.
        private static int MergeNames(
            List<PdfDocument> documents,
            List<Dictionary<int, int>> objectMaps,
            List<(int objNum, PdfObject obj)> remappedObjects,
            Dictionary<int, int> docOfNewObject,
            int pagesTreeObjNum,
            ref int nextObjectNumber)
        {
            int remainingEntries = PdfLimits.MaxNameTreeEntries;
            bool truncated = false;
            int collisions = 0;
            int repeated = 0;
            // Per category, the next number a key whose bounded suffixes are all taken tries.
            var fallbackSuffix = new Dictionary<PdfName, long>();

            var categoryOrder = new List<PdfName>();
            // IsHex travels with the key: a UTF-16BE key is half 0x00 bytes, and while those are
            // legal inside a literal string (§7.3.4.2) the source wrote it as <FEFF…> for a reason.
            var merged = new Dictionary<PdfName, List<(byte[] Key, bool IsHex, PdfObject Value)>>();
            var takenKeys = new Dictionary<PdfName, HashSet<string>>();
            // docIndex → (original /Dests key → the key it was renamed to). Only populated when a
            // collision actually happens, so the reference rewrite below normally does nothing.
            var destRenames = new Dictionary<int, Dictionary<string, byte[]>>();

            for (int docIndex = 0; docIndex < documents.Count; docIndex++)
            {
                var catalog = documents[docIndex].Catalog?.Dictionary;
                if (catalog == null) continue;
                if (!(documents[docIndex].ResolveObject(catalog[PdfName.Names]) is PdfDictionary srcNames))
                    continue;

                foreach (var category in srcNames)
                {
                    var flat = new List<KeyValuePair<PdfString, PdfObject>>();
                    if (!PdfObjectHelper.TryFlattenNameTree(category.Value, documents[docIndex], flat, ref remainingEntries))
                        truncated = true;
                    if (flat.Count == 0) continue;

                    if (!merged.TryGetValue(category.Key, out var entries))
                    {
                        entries = new List<(byte[], bool, PdfObject)>();
                        merged[category.Key] = entries;
                        takenKeys[category.Key] = new HashSet<string>(StringComparer.Ordinal);
                        categoryOrder.Add(category.Key);
                    }
                    var taken = takenKeys[category.Key];
                    // §7.9.6: a tree's keys are unique, so a key this source repeats is one entry —
                    // the first, as a lookup finds it. A repeat is not renamed against the suffixes
                    // this source minted, which would cost a thousand attempts a copy.
                    var ownKeys = new HashSet<string>(StringComparer.Ordinal);

                    foreach (var entry in flat)
                    {
                        byte[] key = entry.Key.RawBytes;
                        string lookup = PdfObjectHelper.NameKeyLookup(key);
                        if (!ownKeys.Add(lookup)) { repeated++; continue; }
                        if (!taken.Add(lookup))
                        {
                            // §7.9.6 requires the keys in one tree to be unique, so two sources
                            // naming the same destination cannot both keep the name. SUFFIX the
                            // later one — overwriting would silently re-point the later document's
                            // bookmark at the earlier document's page, which is a confidently wrong
                            // answer rather than an obviously changed name.
                            // BOUNDED, and the bound is not decoration. §7.9.6 requires unique keys
                            // but nothing stops a FILE from repeating one, and TryFlattenNameTree
                            // does not de-duplicate within a source — so N copies of one key make
                            // the k-th copy walk the whole chain it already minted, which is
                            // O(N²) iterations each allocating two byte[] and a string. At the
                            // MaxNameTreeEntries ceiling that is ~1.25e11 iterations: a hang, not a
                            // slow merge. The breadth budget added alongside this loop bounds how
                            // many entries arrive; it does not bound the work spent per entry.
                            byte[] renamed = key;
                            bool renamedOk = false;
                            for (int attempt = 1; attempt <= MaxRenameAttempts; attempt++)
                            {
                                renamed = PdfObjectHelper.AppendToNameKey(key,
                                    attempt == 1 ? "." + docIndex : "." + docIndex + "." + attempt);
                                lookup = PdfObjectHelper.NameKeyLookup(renamed);
                                if (taken.Add(lookup)) { renamedOk = true; break; }
                            }
                            // Every bounded suffix taken: the search goes on with a number no earlier
                            // probe in this category used, so each failing probe meets a distinct
                            // taken key and the whole merge spends at most one probe a key. Dropping
                            // the key would send its own document's links, still naming it, to the
                            // earlier document's entry — the re-pointing the suffixing exists to avoid.
                            while (!renamedOk)
                            {
                                if (!fallbackSuffix.TryGetValue(category.Key, out long next)) next = MaxRenameAttempts + 1;
                                fallbackSuffix[category.Key] = next + 1;
                                renamed = PdfObjectHelper.AppendToNameKey(key,
                                    "." + docIndex + "." + next.ToString(System.Globalization.CultureInfo.InvariantCulture));
                                renamedOk = taken.Add(PdfObjectHelper.NameKeyLookup(renamed));
                            }
                            collisions++;
                            if (category.Key == PdfName.Dests)
                            {
                                if (!destRenames.TryGetValue(docIndex, out var map))
                                    destRenames[docIndex] = map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                                map[PdfObjectHelper.NameKeyLookup(key)] = renamed;
                            }
                            key = renamed;
                        }

                        entries.Add((
                            key,
                            entry.Key.IsHex,
                            PdfObjectHelper.DeepClone(entry.Value, objectMaps[docIndex], pagesTreeObjNum)));
                    }
                }
            }

            if (collisions > 0)
                RewriteRenamedDestinations(remappedObjects, docOfNewObject, destRenames);

            var said = new List<string>();
            if (truncated)
                said.Add("Name tree walk was cut short by the depth cap or the " +
                         $"{PdfLimits.MaxNameTreeEntries}-entry budget; the merged /Names is incomplete.");
            if (collisions > 0)
                said.Add($"{collisions} name-tree key(s) collided between sources and were suffixed rather than overwritten.");
            if (repeated > 0)
                said.Add($"{repeated} name-tree key(s) a source repeats were kept once, its first entry " +
                         "(§7.9.6 keys are unique).");
            _lastNameTreeWarning = said.Count > 0 ? string.Join(" ", said) : null;

            if (categoryOrder.Count == 0) return 0;

            var nameDict = new PdfDictionary();
            foreach (var category in categoryOrder)
            {
                var entries = merged[category];
                entries.Sort((x, y) => PdfNameTree.CompareKeys(x.Key, y.Key));

                var leafNames = new PdfArray();
                foreach (var entry in entries)
                {
                    leafNames.Add(new PdfString(entry.Key, entry.IsHex));
                    leafNames.Add(entry.Value);
                }

                var leaf = new PdfDictionary();
                leaf.Set(PdfName.Names, leafNames);
                int leafObjNum = nextObjectNumber++;
                leaf.ObjectNumber = leafObjNum;
                leaf.GenerationNumber = 0;
                remappedObjects.Add((leafObjNum, leaf));
                nameDict.Set(category, new PdfReference(leafObjNum, 0));
            }

            int nameDictObjNum = nextObjectNumber++;
            nameDict.ObjectNumber = nameDictObjNum;
            nameDict.GenerationNumber = 0;
            remappedObjects.Add((nameDictObjNum, nameDict));
            return nameDictObjNum;
        }

        // Points every reference to a renamed named destination at its new name, within the source
        // document whose key was renamed and nowhere else. Without this a suffixed key would leave
        // the later document's bookmarks and link annotations resolving to nothing — the entry
        // would be present but unreachable, which is the same invisible failure as overwriting it.
        private static void RewriteRenamedDestinations(
            List<(int objNum, PdfObject obj)> remappedObjects,
            Dictionary<int, int> docOfNewObject,
            Dictionary<int, Dictionary<string, byte[]>> destRenames)
        {
            // Built once: the walk below has to follow an indirect /S, and these are the CLONED
            // objects, so the source document cannot answer for them — DeepClone has already
            // rewritten every reference into this numbering.
            var byNum = new Dictionary<int, PdfObject>(remappedObjects.Count);
            foreach (var (objNum, obj) in remappedObjects) byNum[objNum] = obj;

            foreach (var (objNum, obj) in remappedObjects)
            {
                if (!docOfNewObject.TryGetValue(objNum, out int docIndex)) continue;
                if (!destRenames.TryGetValue(docIndex, out var renames)) continue;
                RewriteRenamedDestinations(obj, renames, byNum, 0);
            }
        }


        private static void RewriteRenamedDestinations(PdfObject obj, Dictionary<string, byte[]> renames,
            Dictionary<int, PdfObject> byNum, int depth)
        {
            if (obj == null || depth > PdfLimits.MaxObjectGraphDepth) return;

            if (obj is PdfArray array)
            {
                for (int i = 0; i < array.Count; i++)
                    RewriteRenamedDestinations(array[i], renames, byNum, depth + 1);
                return;
            }

            if (obj is PdfDictionary dict)
            {
                // §12.3.2.3: an outline item or link annotation names its target either directly in
                // /Dest or through /A << /S /GoTo /D (name) >>. /D is checked only under /S /GoTo —
                // it means an appearance state on a widget and a dash phase elsewhere, and a
                // /GoToR action's /D names a destination in a *different* file.
                //
                // Both names are followed through the CLONED graph, like /S below. A name written as
                // its own object — /Dest 9 0 R — is followed too: left as it is, the object still
                // says (Home), a key the merge kept for the FIRST document, and the link would go to
                // the wrong document's page. The new name is written here as a direct string rather
                // than into the object, which something other than a destination may also reference.
                if (PdfObjectHelper.DerefCloned(dict[PdfName.Dest], byNum) is PdfString dest &&
                    renames.TryGetValue(PdfObjectHelper.NameKeyLookup(dest.RawBytes), out var newDest))
                    dict.Set(PdfName.Dest, new PdfString(newDest, dest.IsHex));

                // Resolved against the CLONED graph: read raw, an indirect /S would read as null,
                // the GoTo test would fail, and the /D rename below would be skipped — leaving the
                // action naming a key the merge suffixed away.
                if ((PdfObjectHelper.DerefCloned(dict[PdfName.S], byNum) as PdfName)?.Value == "GoTo" &&
                    PdfObjectHelper.DerefCloned(dict[PdfName.D], byNum) is PdfString actionDest &&
                    renames.TryGetValue(PdfObjectHelper.NameKeyLookup(actionDest.RawBytes), out var newActionDest))
                    dict.Set(PdfName.D, new PdfString(newActionDest, actionDest.IsHex));

                foreach (var kvp in dict)
                    RewriteRenamedDestinations(kvp.Value, renames, byNum, depth + 1);
                return;
            }
        }

        /// <summary>
        /// Carries every source's page labels across, each source's ranges shifted by the pages
        /// contributed before it.
        /// </summary>
        /// <returns>The merged <c>/PageLabels</c> object number, or 0 when no source had labels.</returns>
        /// <remarks>
        /// <para><b>A page-label key is a page index, so it shifts exactly like a
        /// <c>/ParentTree</c> key</b> — by the number of pages already contributed rather than by
        /// the previous document's key span, because the index is into the page tree and not into a
        /// namespace of its own.</para>
        ///
        /// <para><b>A source with no labels still needs an entry, and that is why the default one is
        /// synthesised.</b> §12.4.2 requires the tree to have an entry for page index 0, so merging
        /// an unlabelled document ahead of a labelled one would otherwise produce a tree whose first
        /// key is not 0 — malformed, and read by viewers as anything from "ignore the labels" to
        /// "apply the first range from page 0". The synthesised entry is plain decimal numbering,
        /// which is what a reader shows for an unlabelled page anyway, so it states the existing
        /// behaviour rather than inventing one.</para>
        ///
        /// <para><b>Labels can repeat after a merge, and that is reported rather than repaired.</b>
        /// Two documents both numbering their pages from 1 give the merged document two page 1s.
        /// Renumbering them would discard what each source actually said — which is the thing a
        /// merged bundle's labels exist to preserve — so the ranges are carried faithfully and
        /// <see cref="LastPageLabelWarning"/> says that navigation by label is now ambiguous.</para>
        /// </remarks>
        private static int MergePageLabels(
            List<PdfDocument> documents,
            List<Dictionary<int, int>> objectMaps,
            List<(int objNum, PdfObject obj)> remappedObjects,
            int pagesTreeObjNum,
            ref int nextObjectNumber)
        {
            var nums = new PdfArray();
            int pagesSoFar = 0;
            int sourcesWithLabels = 0;
            int remainingEntries = PdfLimits.MaxNameTreeEntries;
            var warnings = new List<string>();

            for (int docIndex = 0; docIndex < documents.Count; docIndex++)
            {
                var doc = documents[docIndex];
                int pageCount = doc.Pages.Count;
                var catalog = doc.Catalog?.Dictionary;

                var entries = new List<KeyValuePair<PdfObject, PdfObject>>();
                if (catalog != null &&
                    !PdfNumberTree.TryFlatten(catalog[PdfName.PageLabels], doc, entries,
                                              ref remainingEntries))
                    warnings.Add($"input {docIndex}'s /PageLabels was too large to read in full, so " +
                                 "some of its pages are labelled by the range before them");

                if (entries.Count > 0) sourcesWithLabels++;

                // A key is a page index of its own source. One past the source's last page names no
                // page, and shifted it landed inside the next source's range, leaving /Nums out of
                // order; it is dropped, and the keys kept are put in order once each.
                var kept = new List<(long Key, PdfObject Value)>();
                int outOfRange = 0;
                foreach (var entry in entries)
                {
                    if (!PdfNumberTree.TryKey(entry.Key, out long key))
                    {
                        warnings.Add($"input {docIndex}'s /PageLabels has a non-integer key, which " +
                                     "names no page and was dropped");
                        continue;
                    }
                    if (key < 0 || key >= pageCount) { outOfRange++; continue; }
                    kept.Add((key, entry.Value));
                }
                if (outOfRange > 0)
                    warnings.Add($"input {docIndex}'s /PageLabels has {outOfRange} key(s) outside its " +
                                 $"{pageCount} page(s), which name no page and were dropped");
                kept = kept.OrderBy(k => k.Key).ToList();

                // The default entry for a source that has none - see the remarks - and for one whose
                // first range starts after its first page, which the previous source's last range
                // would otherwise run on over. Emitted only when this source contributes pages.
                if (pageCount > 0 && (kept.Count == 0 || kept[0].Key > 0))
                {
                    var decimalStyle = new PdfDictionary();
                    decimalStyle.Set(PdfName.S, PdfName.D);
                    nums.Add(new PdfNumber(pagesSoFar));
                    nums.Add(decimalStyle);
                }

                long previous = -1;
                foreach (var (key, value) in kept)
                {
                    if (key == previous) continue;   // a key the source repeats: its first range
                    previous = key;
                    nums.Add(new PdfNumber(key + pagesSoFar));
                    nums.Add(PdfObjectHelper.DeepClone(value, objectMaps[docIndex], pagesTreeObjNum));
                }

                pagesSoFar += pageCount;
            }

            // Nothing but synthesised defaults says exactly what no tree at all says, and
            // writing one would make every merge of ordinary documents look like a
            // relabelling. The defaults exist to keep a REAL range from a later source off
            // the pages ahead of it, so they are only worth writing when there is one.
            if (sourcesWithLabels == 0) return 0;

            if (sourcesWithLabels > 1)
                warnings.Add("more than one input supplied page labels, so the merged document may " +
                             "carry the same label twice - each source's numbering was kept as it " +
                             "was written, which makes navigation by label ambiguous");

            var tree = new PdfDictionary();
            tree.Set(PdfName.Nums, nums);

            int objNum = nextObjectNumber++;
            tree.ObjectNumber = objNum;
            tree.GenerationNumber = 0;
            remappedObjects.Add((objNum, tree));

            if (warnings.Count > 0) _lastPageLabelWarning = string.Join("; ", warnings);
            return objNum;
        }

        /// <summary>
        /// Carries every source's output intents across, keeping one intent per subtype.
        /// </summary>
        /// <returns>The merged array, or <c>null</c> when no source declared an intent.</returns>
        /// <remarks>
        /// <b>Two intents of the same subtype is not a merge, it is a contradiction.</b> §14.11.5
        /// permits several output intents but a PDF/X file has exactly one <c>GTS_PDFX</c>, and it
        /// names the printing condition the whole document was prepared for. Two sources prepared
        /// for different conditions cannot both be right about the merged document, and emitting
        /// both produces a file that no PDF/X validator accepts and that a prepress workflow may
        /// colour-manage with either. The first is kept and
        /// <see cref="LastOutputIntentWarning"/> names what was dropped, so the caller can decide -
        /// which is the only place that decision can be made.
        /// </remarks>
        private static PdfArray MergeOutputIntents(
            List<PdfDocument> documents,
            List<Dictionary<int, int>> objectMaps,
            int pagesTreeObjNum)
        {
            var intents = new PdfArray();
            var subtypes = new HashSet<string>(StringComparer.Ordinal);
            var warnings = new List<string>();

            for (int docIndex = 0; docIndex < documents.Count; docIndex++)
            {
                var doc = documents[docIndex];
                var catalog = doc.Catalog?.Dictionary;
                if (catalog == null) continue;
                if (!(doc.ResolveObject(catalog[PdfName.OutputIntents]) is PdfArray sourceIntents))
                    continue;

                for (int i = 0; i < sourceIntents.Count; i++)
                {
                    string subtype =
                        (doc.ResolveObject(doc.ResolveObject(sourceIntents[i]) is PdfDictionary intent
                            ? intent[PdfName.S] : null) as PdfName)?.Value;

                    if (subtype != null && !subtypes.Add(subtype))
                    {
                        warnings.Add($"input {docIndex} declares a second /{subtype} output intent; " +
                                     "an earlier input's was kept, because one document has one " +
                                     "printing condition and two of them cannot both describe it");
                        continue;
                    }

                    intents.Add(PdfObjectHelper.DeepClone(
                        sourceIntents[i], objectMaps[docIndex], pagesTreeObjNum));
                }
            }

            if (warnings.Count > 0) _lastOutputIntentWarning = string.Join("; ", warnings);
            return intents.Count > 0 ? intents : null;
        }

        /// <summary>
        /// Carries every source's optional content (layers) across: the union of their groups, and
        /// one default configuration in which a group that was hidden stays hidden.
        /// </summary>
        /// <returns>The merged <c>/OCProperties</c> object number, or 0 when no source had layers.</returns>
        /// <remarks>
        /// <para><b>The configuration travels, or hidden content shows.</b> The optional-content
        /// group dictionaries are copied and remapped like everything else, but a group is hidden
        /// only because the catalog's configuration says so: without <c>/OCProperties</c> every layer
        /// a source switched off would come out visible — the same class of defect as a redaction
        /// that draws a box over intact text.</para>
        ///
        /// <para><b><c>/BaseState /OFF</c> is normalised away rather than carried.</b> It means
        /// "every group in this document is off except those in <c>/ON</c>" — a statement about
        /// <i>this</i> document's groups, which stops being true the moment another document's
        /// groups join the list, and would switch every one of them off. So a source using it has
        /// its effectively-hidden set computed here (its own groups, less its <c>/ON</c>) and those
        /// groups are named in the merged <c>/OFF</c>. The result is identical for the source's own
        /// layers and leaves the other sources alone. <c>/ON</c> is then not carried, because under
        /// the default base state everything not in <c>/OFF</c> is already on: writing it would be
        /// surface that means nothing.</para>
        ///
        /// <para><b>Alternate configurations (<c>/Configs</c>) are not carried, and that is
        /// reported.</b> A configuration names a complete on/off state for a document; two
        /// documents' alternates are not a set of alternates for the merged one, and silently
        /// concatenating them would offer a viewer states that switch off half the pages.</para>
        /// </remarks>
        private static int MergeOptionalContent(
            List<PdfDocument> documents,
            List<Dictionary<int, int>> objectMaps,
            List<(int objNum, PdfObject obj)> remappedObjects,
            int pagesTreeObjNum,
            ref int nextObjectNumber)
        {
            var groups = new PdfArray();
            var seenGroups = new HashSet<int>();
            var off = new PdfArray();
            var offSeen = new HashSet<int>();
            var order = new PdfArray();
            var rbGroups = new PdfArray();
            var warnings = new List<string>();

            for (int docIndex = 0; docIndex < documents.Count; docIndex++)
            {
                var doc = documents[docIndex];
                var catalog = doc.Catalog?.Dictionary;
                if (catalog == null) continue;
                if (!(doc.ResolveObject(catalog[PdfName.OCProperties]) is PdfDictionary properties))
                    continue;

                var map = objectMaps[docIndex];
                var ownGroups = new List<PdfReference>();

                if (doc.ResolveObject(properties[PdfName.OCGs]) is PdfArray sourceGroups)
                {
                    for (int i = 0; i < sourceGroups.Count; i++)
                    {
                        if (!(PdfObjectHelper.DeepClone(sourceGroups[i], map, pagesTreeObjNum)
                              is PdfReference cloned)) continue;
                        ownGroups.Add(cloned);
                        if (seenGroups.Add(cloned.ReferenceObjectNumber)) groups.Add(cloned);
                    }
                }

                if (properties.ContainsKey(PdfName.Configs))
                    warnings.Add($"input {docIndex}'s alternate optional-content configurations " +
                                 "(/Configs) were not carried across; only the default " +
                                 "configuration was merged");

                if (!(doc.ResolveObject(properties[PdfName.D]) is PdfDictionary config)) continue;

                bool baseOff = (doc.ResolveObject(config[PdfName.BaseState]) as PdfName)?.Value == "OFF";
                if (baseOff)
                {
                    var on = new HashSet<int>();
                    foreach (PdfReference reference in ClonedReferences(doc, config[PdfName.ON], map, pagesTreeObjNum))
                        on.Add(reference.ReferenceObjectNumber);

                    foreach (PdfReference group in ownGroups)
                        if (!on.Contains(group.ReferenceObjectNumber) &&
                            offSeen.Add(group.ReferenceObjectNumber))
                            off.Add(group);

                    warnings.Add($"input {docIndex} used /BaseState /OFF, which names its own groups " +
                                 "only; its hidden groups were listed individually in the merged " +
                                 "/OFF so the other inputs' layers are unaffected");
                }
                else
                {
                    foreach (PdfReference hidden in ClonedReferences(doc, config[PdfName.OFF], map, pagesTreeObjNum))
                        if (offSeen.Add(hidden.ReferenceObjectNumber)) off.Add(hidden);
                }

                // /Order and /RBGroups are nested arrays whose leaves are group references, so they
                // travel through the same clone that remapped everything else - and they are
                // concatenated rather than unioned, because each source's grouping is a statement
                // about its own layers.
                if (doc.ResolveObject(config[PdfName.Order]) is PdfArray sourceOrder)
                    for (int i = 0; i < sourceOrder.Count; i++)
                        order.Add(PdfObjectHelper.DeepClone(sourceOrder[i], map, pagesTreeObjNum));

                if (doc.ResolveObject(config[PdfName.RBGroups]) is PdfArray sourceRb)
                    for (int i = 0; i < sourceRb.Count; i++)
                        rbGroups.Add(PdfObjectHelper.DeepClone(sourceRb[i], map, pagesTreeObjNum));
            }

            if (groups.Count == 0) return 0;

            var defaultConfig = new PdfDictionary();
            if (off.Count > 0) defaultConfig.Set(PdfName.OFF, off);
            if (order.Count > 0) defaultConfig.Set(PdfName.Order, order);
            if (rbGroups.Count > 0) defaultConfig.Set(PdfName.RBGroups, rbGroups);

            var merged = new PdfDictionary();
            merged.Set(PdfName.OCGs, groups);
            merged.Set(PdfName.D, defaultConfig);

            int objNum = nextObjectNumber++;
            merged.ObjectNumber = objNum;
            merged.GenerationNumber = 0;
            remappedObjects.Add((objNum, merged));

            if (warnings.Count > 0)
                _lastOptionalContentWarning = string.Join("; ", warnings);
            return objNum;
        }

        /// <summary>
        /// The references in an optional-content array, remapped into the merged document.
        /// </summary>
        private static IEnumerable<PdfReference> ClonedReferences(
            PdfDocument doc, PdfObject arrayObject, Dictionary<int, int> map, int pagesTreeObjNum)
        {
            if (!(doc.ResolveObject(arrayObject) is PdfArray array)) yield break;

            for (int i = 0; i < array.Count; i++)
                if (PdfObjectHelper.DeepClone(array[i], map, pagesTreeObjNum) is PdfReference cloned)
                    yield return cloned;
        }

        /// <summary>
        /// Carries every source's logical structure across: one merged <c>/StructTreeRoot</c>, one
        /// <c>/ParentTree</c> with each source's keys shifted clear of the others, and every
        /// <c>/StructParents</c> / <c>/StructParent</c> shifted to match.
        /// </summary>
        /// <returns>The merged root's object number, or 0 when no source had a structure tree.</returns>
        /// <remarks>
        /// <para><b>The structure objects are copied like every other object; this makes them
        /// reachable.</b> Without a merged <c>/StructTreeRoot</c> they would be unreachable, and two
        /// documents remediated to PDF/UA would merge into one that fails UA01.</para>
        ///
        /// <para><b>The key shift is the whole difficulty.</b> A <c>/ParentTree</c> key is an index
        /// into a per-document namespace: every source numbers its pages' <c>/StructParents</c> from
        /// zero, so concatenating two trees collides every key and a page ends up owning another
        /// document's structure elements. Each source is therefore shifted by the sum of its
        /// predecessors' <c>/ParentTreeNextKey</c>, and the same shift is applied to every
        /// <c>/StructParents</c> and <c>/StructParent</c> in that source's cloned objects — the two
        /// must move together or they name each other's entries.</para>
        ///
        /// <para><b>The span comes from the tree, not only from the declared next key.</b>
        /// <c>/ParentTreeNextKey</c> is what §14.7.4.4 says a writer should maintain, and a file
        /// whose declared value is smaller than its largest key would otherwise have its own entries
        /// overwritten by the next document's. The largest of the two and of the <c>/StructParents</c>
        /// the source's objects name is used — each within PDF's integer range (ISO 32000-1 Annex C):
        /// a declared <c>long.MaxValue</c> would wrap every later source's keys negative.</para>
        ///
        /// <para><b><c>/MarkInfo /Marked</c> is written only when EVERY source was marked.</b>
        /// Merging a tagged document with an untagged one produces a partially tagged one, and
        /// <c>/Marked true</c> claims the whole document's content is marked. The structure that
        /// does exist still travels — which is strictly better than dropping it — and
        /// <see cref="LastStructureWarning"/> says why the claim was withheld.</para>
        /// </remarks>
        private static int MergeStructureTrees(
            List<PdfDocument> documents,
            List<Dictionary<int, int>> objectMaps,
            List<(int objNum, PdfObject obj)> remappedObjects,
            Dictionary<int, int> docOfNewObject,
            int pagesTreeObjNum,
            PdfDictionary catalogDict,
            ref int nextObjectNumber)
        {
            var byNum = new Dictionary<int, PdfDictionary>();
            foreach (var (n, o) in remappedObjects)
                if (o is PdfDictionary d) byNum[n] = d;

            var warnings = new List<string>();
            var mergedKids = new PdfArray();
            var mergedNums = new PdfArray();
            var roleMap = new PdfDictionary();
            var classMap = new PdfDictionary();
            // What each kept key's value says, resolved in its own source: the conflict test reads
            // content, never two documents' object numbers.
            var roleMapSays = new Dictionary<PdfName, string>();
            var classMapSays = new Dictionary<PdfName, string>();
            var topLevelKids = new List<int>();   // new object numbers whose /P must be re-parented
            // Every source's /IDTree entries (Table 322: required once any element carries an /ID), the
            // identifiers kept distinct by their bytes, and every source's PDF 2.0 /Namespaces entry.
            var idEntries = new List<(byte[] Key, PdfObject KeyObject, PdfObject Value)>();
            var idsSeen = new HashSet<string>(StringComparer.Ordinal);
            var namespaces = new PdfArray();

            long offset = 0;
            bool everySourceMarked = true;
            bool anySourceTagged = false;
            int remainingEntries = PdfLimits.MaxNameTreeEntries;

            // Each source's cloned dictionaries, gathered once: the shift below walked every object
            // of the merge for each tagged source, sources × objects for a bundle of tagged files.
            var dictionariesOf = new List<PdfDictionary>[documents.Count];
            foreach (var (n, o) in remappedObjects)
                if (o is PdfDictionary od && docOfNewObject.TryGetValue(n, out int owner)
                    && owner >= 0 && owner < documents.Count)
                    (dictionariesOf[owner] ??= new List<PdfDictionary>()).Add(od);

            for (int docIndex = 0; docIndex < documents.Count; docIndex++)
            {
                var doc = documents[docIndex];
                var catalog = doc.Catalog?.Dictionary;
                PdfDictionary srcRoot = catalog == null
                    ? null
                    : doc.ResolveObject(catalog[PdfName.StructTreeRoot]) as PdfDictionary;

                if (srcRoot == null)
                {
                    everySourceMarked = false;
                    continue;
                }

                anySourceTagged = true;
                if (!IsMarked(doc, catalog)) everySourceMarked = false;

                // ---- the source's parent tree, flattened and shifted ----
                var entries = new List<KeyValuePair<PdfObject, PdfObject>>();
                if (!PdfNumberTree.TryFlatten(srcRoot[PdfName.ParentTree], doc, entries,
                                              ref remainingEntries))
                    warnings.Add($"input {docIndex}'s /ParentTree was too large to read in full, so " +
                                 "some of its structure is not reachable from the merged tree");

                long maxKey = -1;
                foreach (var entry in entries)
                {
                    if (!PdfNumberTree.TryKey(entry.Key, out long key))
                    {
                        warnings.Add($"input {docIndex}'s /ParentTree has a non-integer key, which " +
                                     "names no structure element and was dropped");
                        continue;
                    }
                    if (key < 0 || key > int.MaxValue)
                    {
                        warnings.Add($"input {docIndex}'s /ParentTree has a key outside 0 to {int.MaxValue}, " +
                                     "which no /StructParents can name, and it was dropped");
                        continue;
                    }

                    if (key > maxKey) maxKey = key;
                    mergedNums.Add(new PdfNumber(key + offset));
                    mergedNums.Add(PdfObjectHelper.DeepClone(
                        entry.Value, objectMaps[docIndex], pagesTreeObjNum));
                }

                // ---- the source's top-level structure elements ----
                foreach (PdfObject kid in KidsOf(doc, srcRoot))
                {
                    PdfObject cloned = PdfObjectHelper.DeepClone(
                        kid, objectMaps[docIndex], pagesTreeObjNum);
                    mergedKids.Add(cloned);
                    if (cloned is PdfReference reference)
                        topLevelKids.Add(reference.ReferenceObjectNumber);
                }

                MergeMapping(doc, srcRoot, PdfName.RoleMap, roleMap, roleMapSays, objectMaps[docIndex], pagesTreeObjNum, docIndex, warnings);
                MergeMapping(doc, srcRoot, PdfName.ClassMap, classMap, classMapSays, objectMaps[docIndex], pagesTreeObjNum, docIndex, warnings);
                MergeIdTree(doc, srcRoot, idEntries, idsSeen, objectMaps[docIndex], pagesTreeObjNum, docIndex, warnings,
                    ref remainingEntries);
                MergeNamespaces(doc, srcRoot, namespaces, objectMaps[docIndex], pagesTreeObjNum, docIndex, warnings,
                    ref remainingEntries);

                // ---- shift this source's own /StructParents and /StructParent ----
                // The span covers the keys the tree holds and the ones the source's objects name, and
                // the declared next key within PDF's integer range (ISO 32000-1 Annex C). A declared
                // value past it — long.MaxValue saturated — made the offset wrap every later key negative.
                IReadOnlyList<PdfDictionary> own = dictionariesOf[docIndex] ?? (IReadOnlyList<PdfDictionary>)Array.Empty<PdfDictionary>();
                long maxUsed = -1;
                foreach (var dict in own)
                    foreach (var name in new[] { PdfName.StructParents, PdfName.StructParent })
                        if (dict[name] is PdfNumber used && used.IsInteger && used.LongValue >= 0
                            && used.LongValue <= int.MaxValue && used.LongValue > maxUsed)
                            maxUsed = used.LongValue;
                long declaredNext = doc.ResolveObject(srcRoot[PdfName.ParentTreeNextKey]) is PdfNumber dn
                                    && dn.IsInteger && dn.LongValue >= 0 && dn.LongValue <= int.MaxValue
                    ? dn.LongValue : 0;
                long span = Math.Max(declaredNext, Math.Max(maxKey, maxUsed) + 1);

                if (offset > 0)
                    ShiftStructParents(own, offset);

                offset += span;
            }

            if (offset - 1 > int.MaxValue)
                warnings.Add($"the merged /ParentTree's keys reach {offset - 1}, past {int.MaxValue}, the " +
                             "integer limit of ISO 32000-1 Annex C, so a reader may not follow the later " +
                             "sources' structure");

            if (!anySourceTagged) return 0;

            int parentTreeNum = nextObjectNumber++;
            var parentTree = new PdfDictionary();
            parentTree.Set(PdfName.Nums, mergedNums);
            parentTree.ObjectNumber = parentTreeNum;
            parentTree.GenerationNumber = 0;
            remappedObjects.Add((parentTreeNum, parentTree));

            int rootNum = nextObjectNumber++;
            var root = new PdfDictionary();
            root.Set(PdfName.Type, PdfName.StructTreeRoot);
            root.Set(PdfName.K, mergedKids);
            root.Set(PdfName.ParentTree, new PdfReference(parentTreeNum, 0));
            root.Set(PdfName.ParentTreeNextKey, new PdfNumber(offset));
            if (roleMap.Count > 0) root.Set(PdfName.RoleMap, roleMap);
            if (classMap.Count > 0) root.Set(PdfName.ClassMap, classMap);
            if (idEntries.Count > 0)
            {
                // One leaf, its keys in §7.9.6's byte order — the order a reader's binary search assumes.
                idEntries.Sort((a, b) => PdfNameTree.CompareKeys(a.Key, b.Key));
                var names = new PdfArray();
                foreach (var (_, keyObject, value) in idEntries)
                {
                    names.Add(keyObject);
                    names.Add(value);
                }
                var idTree = new PdfDictionary();
                idTree.Set(PdfName.Names, names);
                root.Set(PdfName.IDTree, idTree);
            }
            if (namespaces.Count > 0) root.Set(PdfName.Namespaces, namespaces);
            root.ObjectNumber = rootNum;
            root.GenerationNumber = 0;
            remappedObjects.Add((rootNum, root));

            // Each source's top-level element still points at ITS root, which the merged catalog no
            // longer names. §14.7.2 Table 323 makes /P required, and a checker that walks upward
            // from an element would otherwise leave the merged tree through a dictionary nothing
            // references.
            foreach (int kid in topLevelKids)
                if (byNum.TryGetValue(kid, out var element))
                    element.Set(PdfName.P, new PdfReference(rootNum, 0));

            var markInfo = new PdfDictionary();
            markInfo.Set(PdfName.Marked, everySourceMarked ? PdfBoolean.True : PdfBoolean.False);
            catalogDict.Set(PdfName.MarkInfo, markInfo);
            if (!everySourceMarked)
                warnings.Add("/MarkInfo /Marked is false because not every input was a marked, " +
                             "tagged document; the structure that exists was still carried across");

            if (warnings.Count > 0) _lastStructureWarning = string.Join("; ", warnings);
            return rootNum;
        }

        /// <summary>The structure root's <c>/K</c> as a list, whether it is one element or an array.</summary>
        /// <remarks>
        /// §14.7.2 lets <c>/K</c> be a single element or an array of them, and a root with exactly
        /// one top-level element — a lone <c>/Document</c>, which is what most producers emit — takes
        /// the single form. Treating only the array case would carry nothing across for precisely
        /// the most common shape.
        /// </remarks>
        private static IEnumerable<PdfObject> KidsOf(PdfDocument doc, PdfDictionary root)
        {
            PdfObject k = root[PdfName.K];
            if (k == null) yield break;

            if (doc.ResolveObject(k) is PdfArray array)
            {
                for (int i = 0; i < array.Count; i++) yield return array[i];
                yield break;
            }

            yield return k;
        }

        /// <summary>
        /// Unions a source's <c>/RoleMap</c> or <c>/ClassMap</c> into the merged one, first
        /// definition winning.
        /// </summary>
        /// <remarks>
        /// A colliding key is reported rather than overwritten, for the reason
        /// <see cref="MergeDefaultResources"/> gives about <c>/DR</c>: the losing document's elements
        /// still carry the tag name, so silently taking the later mapping would change what those
        /// elements mean. Two documents mapping the same custom tag to different standard ones is a
        /// genuine conflict a caller has to know about.
        /// <para>A value is cloned through its source's object map, as every other carried object is:
        /// a <c>/ClassMap</c> attribute object held indirectly (<c>/Bordered 12 0 R</c>) written with
        /// the source's number would name some other object in the renumbered file, and every element
        /// of that class would take the wrong attributes. Whether two inputs disagree is judged on
        /// what their values say, resolved in their own documents, not on the text of a reference
        /// each document numbers its own way.</para>
        /// </remarks>
        private static void MergeMapping(
            PdfDocument doc, PdfDictionary root, PdfName which,
            PdfDictionary into, Dictionary<PdfName, string> says, Dictionary<int, int> objectMap,
            int pagesTreeObjNum, int docIndex, List<string> warnings)
        {
            if (!(doc.ResolveObject(root[which]) is PdfDictionary map)) return;

            foreach (var entry in map)
            {
                string meaning = Said(doc, entry.Value, 0);
                if (!into.ContainsKey(entry.Key))
                {
                    into.Set(entry.Key, PdfObjectHelper.DeepClone(entry.Value, objectMap, pagesTreeObjNum));
                    says[entry.Key] = meaning;
                    continue;
                }

                if (!string.Equals(says[entry.Key], meaning, StringComparison.Ordinal))
                    warnings.Add($"input {docIndex} maps /{entry.Key.Value} in {which} differently " +
                                 "from an earlier input; the earlier mapping was kept");
            }
        }

        /// <summary>
        /// Flattens a source's <c>/IDTree</c> (Table 322) into the merged entries, each value cloned
        /// through the source's object map, under the one entry budget every tree of the merge shares.
        /// </summary>
        /// <remarks>
        /// The elements travel with their <c>/ID</c>, and a <c>/Headers</c> attribute or a reader's
        /// lookup resolves an identifier only through this tree, so dropping it leaves identifiers that
        /// name nothing. An identifier two sources both declare is kept for the first — the earlier
        /// element keeps its name, as a colliding <c>/RoleMap</c> key keeps its earlier mapping — and
        /// said; a key that is not a string names no element (§7.9.6) and is dropped and said. Each
        /// is counted once a source, since a file chooses how many it repeats.
        /// </remarks>
        private static void MergeIdTree(
            PdfDocument doc, PdfDictionary root,
            List<(byte[] Key, PdfObject KeyObject, PdfObject Value)> into, HashSet<string> seen,
            Dictionary<int, int> objectMap, int pagesTreeObjNum, int docIndex, List<string> warnings,
            ref int remainingEntries)
        {
            if (root[PdfName.IDTree] == null) return;

            var entries = new List<KeyValuePair<PdfObject, PdfObject>>();
            if (!PdfNameTree.TryFlatten(root[PdfName.IDTree], doc, entries, ref remainingEntries))
                warnings.Add($"input {docIndex}'s /IDTree was too large to read in full, so some of its " +
                             "element identifiers are not in the merged tree");

            int repeated = 0, notStrings = 0;
            foreach (var entry in entries)
            {
                if (!(doc.ResolveObject(entry.Key) is PdfString key))
                {
                    notStrings++;
                    continue;
                }
                if (!seen.Add(Convert.ToBase64String(key.RawBytes)))
                {
                    repeated++;
                    continue;
                }
                into.Add((key.RawBytes,
                    PdfObjectHelper.DeepClone(key, objectMap, pagesTreeObjNum),
                    PdfObjectHelper.DeepClone(entry.Value, objectMap, pagesTreeObjNum)));
            }

            if (repeated > 0)
                warnings.Add($"input {docIndex}'s /IDTree declares {repeated} element identifier(s) an earlier " +
                             "input declared too; the earlier elements kept them");
            if (notStrings > 0)
                warnings.Add($"input {docIndex}'s /IDTree has {notStrings} key(s) that are not strings (§7.9.6), " +
                             "which name no element and were dropped");
        }

        /// <summary>
        /// Appends a source's <c>/Namespaces</c> entries (Table 354: every namespace dictionary the
        /// tree uses), cloned, to the merged array — each source's elements still name their own
        /// namespace dictionaries by reference, so every one stays listed.
        /// </summary>
        private static void MergeNamespaces(
            PdfDocument doc, PdfDictionary root, PdfArray into,
            Dictionary<int, int> objectMap, int pagesTreeObjNum, int docIndex, List<string> warnings,
            ref int remainingEntries)
        {
            if (!(doc.ResolveObject(root[PdfName.Namespaces]) is PdfArray declared)) return;

            for (int i = 0; i < declared.Count; i++)
            {
                if (remainingEntries-- <= 0)
                {
                    warnings.Add($"input {docIndex}'s /Namespaces was too long to carry in full");
                    return;
                }
                into.Add(PdfObjectHelper.DeepClone(declared[i], objectMap, pagesTreeObjNum));
            }
        }

        // A mapping value as text with its references followed, a few levels deep: an attribute
        // object, or an array of them, is a handful of names and numbers.
        private static string Said(PdfDocument doc, PdfObject value, int depth)
        {
            if (depth > 8) return "…";
            value = value is PdfReference ? doc.ResolveObject(value) : value;
            switch (value)
            {
                case PdfArray array:
                    var items = new List<string>(array.Count);
                    for (int i = 0; i < array.Count; i++) items.Add(Said(doc, array[i], depth + 1));
                    return "[" + string.Join(" ", items) + "]";
                case PdfDictionary dict:
                    var pairs = new List<string>();
                    foreach (var kvp in dict) pairs.Add("/" + kvp.Key.Value + " " + Said(doc, kvp.Value, depth + 1));
                    pairs.Sort(StringComparer.Ordinal);
                    return "<<" + string.Join(" ", pairs) + ">>";
                default:
                    return value?.ToString() ?? "null";
            }
        }

        /// <summary>Dictionaries <see cref="ShiftStructParents"/> has visited on the calling thread: the evidence that each is visited once.</summary>
        [ThreadStatic] internal static long ShiftStructParentsSteps;

        /// <summary>
        /// Adds <paramref name="offset"/> to every <c>/StructParents</c> and <c>/StructParent</c> in
        /// <paramref name="sourceDictionaries"/>, the dictionaries cloned from one source.
        /// </summary>
        /// <remarks>
        /// <para>Both keys are reserved for this purpose — <c>/StructParents</c> on a page or form
        /// XObject (Table 30, Table 95) and <c>/StructParent</c> on an annotation or a marked
        /// content reference (Table 168) — so matching them by name cannot collide with anything
        /// else.</para>
        ///
        /// <para>An <b>indirect</b> value is left alone, unreported: rewriting it would mean
        /// rewriting the referenced number object, which may be shared with another key this shift
        /// does not apply to.</para>
        /// </remarks>
        private static void ShiftStructParents(IReadOnlyList<PdfDictionary> sourceDictionaries, long offset)
        {
            foreach (PdfDictionary dict in sourceDictionaries)
            {
                ShiftStructParentsSteps++;
                Shift(dict, PdfName.StructParents);
                Shift(dict, PdfName.StructParent);
            }

            void Shift(PdfDictionary dict, PdfName key)
            {
                if (dict[key] is PdfNumber number)
                    dict.Set(key, new PdfNumber(number.LongValue + offset));
            }
        }

        /// <summary>Whether a source declares itself a marked, tagged document (§14.7.1).</summary>
        private static bool IsMarked(PdfDocument doc, PdfDictionary catalog)
            => doc.ResolveObject(catalog[PdfName.MarkInfo]) is PdfDictionary markInfo
               && doc.ResolveObject(markInfo[PdfName.Marked]) is PdfBoolean marked
               && marked.Value;

        // Combines every source document's /Outlines (bookmark) tree into one. The outline
        // dictionaries are already cloned + reference-remapped in `remappedObjects`; this walks each
        // source root's top-level (/First → /Next) chain, then re-parents and re-chains all
        // top-level items in document order under a fresh root. Returns the new root's object
        // number, or 0 when no source has an outline. Named destinations are merged by MergeNames,
        // which runs first: an item naming a destination a collision renamed is given the new name.
        private static int MergeOutlines(
            List<PdfDocument> documents,
            List<Dictionary<int, int>> objectMaps,
            List<(int objNum, PdfObject obj)> remappedObjects,
            ref int nextObjectNumber)
        {
            // Index the cloned dictionaries by new object number so the items can be mutated.
            var byNum = new Dictionary<int, PdfDictionary>();
            foreach (var (n, o) in remappedObjects)
                if (o is PdfDictionary d) byNum[n] = d;

            var topItems = new List<int>(); // new object numbers of top-level items, in order
            long totalCount = 0;

            for (int docIndex = 0; docIndex < documents.Count; docIndex++)
            {
                var catalog = documents[docIndex].Catalog?.Dictionary;
                if (catalog == null) continue;
                if (!(documents[docIndex].ResolveObject(catalog[PdfName.Outlines]) is PdfDictionary srcRoot))
                    continue;
                int origRoot = srcRoot.ObjectNumber;
                if (origRoot <= 0 || !objectMaps[docIndex].TryGetValue(origRoot, out int newRoot)) continue;
                if (!byNum.TryGetValue(newRoot, out var clonedRoot)) continue;

                int? cur = (clonedRoot[PdfName.First] as PdfReference)?.ReferenceObjectNumber;
                // A VISITED SET, not a counter — the same guard, and for the same reason, as
                // PdfSplitter.OutlineWalk.ReadLevel. A /Next ring is one hop deep, so a counter does
                // not prevent it: it appended the same items a million times, and since the
                // re-chain loop below lets later writes win, the FIRST item came out with a /Prev
                // pointing FORWARD at a later item. That is a /Prev cycle in the OUTPUT — §12.3.3
                // says the first item has none — from a merge that reported success.
                var seen = new HashSet<int>();
                int guard = 0;
                while (cur.HasValue && byNum.ContainsKey(cur.Value) &&
                       seen.Add(cur.Value) && guard++ < PdfLimits.MaxNameTreeEntries)
                {
                    topItems.Add(cur.Value);
                    cur = (byNum[cur.Value][PdfName.Next] as PdfReference)?.ReferenceObjectNumber;
                }
                // CLAMPED, and the clamp is not decoration: /Count is a value the FILE chooses, and
                // a double→long cast of an out-of-range or NaN value is unspecified — on x64 it
                // yields long.MinValue, whose Math.Abs throws OverflowException. One crafted
                // `/Count -9223372036854775808` therefore killed the whole merge with an exception
                // naming nothing about the input. §12.3.3 makes /Count a count of visible items, so
                // anything past int.MaxValue is nonsense either way.
                if (clonedRoot[PdfName.Count] is PdfNumber cn)
                {
                    double declared = cn.DoubleValue;
                    // Not double.IsFinite: that is .NET Core 2.1+ and this assembly also ships a
                    // netstandard2.0 leg. Core states the same predicate this way (PdfRealFormat).
                    if (!double.IsNaN(declared) && !double.IsInfinity(declared))
                        totalCount += SaturatingCast.ToLong(Math.Min(Math.Abs(declared), int.MaxValue));
                }
            }

            if (topItems.Count == 0) return 0;

            int rootNum = nextObjectNumber++;
            var root = new PdfDictionary();
            root.Set(PdfName.Type, PdfName.Outlines);
            root.Set(PdfName.First, new PdfReference(topItems[0], 0));
            root.Set(PdfName.Last, new PdfReference(topItems[topItems.Count - 1], 0));
            // Summed from up to `documents.Count` clamped values, so cap once more before writing.
            root.Set(PdfName.Count,
                new PdfNumber(totalCount > 0 ? Math.Min(totalCount, int.MaxValue) : topItems.Count));
            root.ObjectNumber = rootNum;
            root.GenerationNumber = 0;

            // Re-parent each top-level item to the new root and re-chain Prev/Next in document
            // order. Items interior to a source keep their existing (already-remapped) siblings;
            // only the per-source boundary items get new cross-source links.
            for (int i = 0; i < topItems.Count; i++)
            {
                var item = byNum[topItems[i]];
                item.Set(PdfName.Parent, new PdfReference(rootNum, 0));
                if (i > 0) item.Set(PdfName.Prev, new PdfReference(topItems[i - 1], 0));
                else item.Remove(PdfName.Prev);
                if (i < topItems.Count - 1) item.Set(PdfName.Next, new PdfReference(topItems[i + 1], 0));
                else item.Remove(PdfName.Next);
            }

            remappedObjects.Add((rootNum, root));
            return rootNum;
        }

    }
}
