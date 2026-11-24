// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Metadata;

namespace Tetitulz.Pdf.Base.Operations.Structure
{
    /// <summary>
    /// Reads and modifies Optional Content Groups (OCG layers) in a PDF.
    ///
    /// <para>
    /// PDF OCGs are stored in the document catalog under /OCProperties:
    /// <code>
    /// /OCProperties &lt;&lt;
    ///   /OCGs [array of indirect refs to OCG dicts]
    ///   /D   &lt;&lt; /Order [...] /ON [...] /OFF [...] &gt;&gt;   % default view config
    /// &gt;&gt;
    /// </code>
    /// Each OCG dict: &lt;&lt; /Type /OCG /Name (layer name) &gt;&gt;
    /// </para>
    ///
    /// Supported operations:
    ///   <list type="bullet">
    ///     <item>List all OCGs by name.</item>
    ///     <item>Set a layer's default visibility (on/off).</item>
    ///     <item>Rename a layer.</item>
    ///   </list>
    /// </summary>
    public static class PdfOcgManager
    {
        // ----------------------------------------------------------------
        //  Public API
        // ----------------------------------------------------------------

        /// <summary>
        /// Returns information about all Optional Content Groups in the document.
        /// Returns an empty list if the document has no OCGs.
        /// </summary>
        public static IReadOnlyList<OcgInfo> ListLayers(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            EncryptedRead.Refuse(doc, "the layers", "PdfOperations.ListLayers(Stream, string)");
            return CollectOcgs(doc);
        }

        /// <summary>
        /// Creates a new Optional Content Group and returns its object number, or <c>0</c> when the
        /// document has no readable catalog.
        /// </summary>
        /// <param name="input">Source PDF stream. Not modified.</param>
        /// <param name="output">Receives the original bytes plus one appended revision.</param>
        /// <param name="layerName">The layer's <c>/Name</c>, as a viewer's layer panel shows it.</param>
        /// <param name="visible">Whether the layer starts on in the default configuration.</param>
        /// <returns>The new OCG's object number, or <c>0</c> if nothing was written.</returns>
        /// <remarks>
        /// <para><b>An OCG that is not in <c>/OCProperties /OCGs</c> does not exist.</b> §8.11.4.2
        /// makes that array the document's inventory of groups, and a viewer builds its layer panel
        /// from it — a group reachable only from an <c>/OC</c> entry is invisible to the UI and, per
        /// the X13 reading in §12b, is a PDF/X violation. So the group, the inventory and the
        /// default configuration are written in one revision or not at all.</para>
        ///
        /// <para><b>Membership of <c>/ON</c> is not the same as being visible, and neither list is
        /// a default.</b> §8.11.4.3: a group named in neither <c>/ON</c> nor <c>/OFF</c> takes
        /// <c>/BaseState</c>, which is <c>/ON</c> unless the document says otherwise. Writing the
        /// group into the matching list states the intent explicitly rather than inheriting it,
        /// which is what keeps <see cref="SetLayerVisibility"/>'s later answer meaningful.</para>
        ///
        /// <para><b>Creating the layer is not assigning content to it.</b> A new group starts empty;
        /// <see cref="AssignObjectToLayer"/> puts an existing XObject or annotation under it. Marked
        /// content (<c>/OC … BDC</c> inside a content stream) is a separate problem, because an OCG
        /// must be an indirect reference and the content-stream builder inlines its property lists.
        /// </para>
        /// </remarks>
        public static int CreateLayer(Stream input, Stream output, string layerName,
                                      bool visible = true)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (layerName == null) throw new ArgumentNullException(nameof(layerName));

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            RefuseEncrypted(doc);

            // A /Root written directly is no object a revision can supersede: the document is copied
            // through, and 0 — which no new layer's number can be — says nothing was written.
            if (doc.Catalog?.Dictionary == null
                || !(doc.Trailer[PdfName.Root] is PdfReference root) || root.ReferenceObjectNumber <= 0)
            {
                ms.Position = 0;
                ms.CopyTo(output);
                return 0;
            }
            int catalogObjNum = root.ReferenceObjectNumber;

            int nextObjNum = doc.CrossReferenceTable.GetMaxObjectNumber() + 1;
            int ocgNum = nextObjNum++;

            var ocg = new PdfDictionary();
            ocg.Set(PdfName.Type, PdfName.OCG);
            ocg.Set(PdfName.Name, new PdfString(layerName));
            ocg.ObjectNumber = ocgNum;
            ocg.GenerationNumber = 0;

            var objects = new List<IndirectObject> { new IndirectObject(ocgNum, 0, ocg) };

            // Every entry rebuilt below is read through EntryRead and refused when the parser could
            // not read it. Read as absent, an unreadable /OCProperties, /D, /OCGs, /ON or /OFF was
            // replaced by one holding only the new layer, which dropped the others from the
            // inventory and the visibility lists.
            PdfDictionary ocProps = ReadForRewrite(doc, doc.Catalog.Dictionary[PdfName.OCProperties], "/OCProperties") as PdfDictionary;
            PdfDictionary defaultConfig = ocProps == null
                ? null
                : ReadForRewrite(doc, ocProps[PdfName.D], "the default configuration /D") as PdfDictionary;
            ReadForRewrite(doc, ocProps?[PdfName.OCGs], "/OCGs");
            ReadForRewrite(doc, defaultConfig?[PdfName.ON], "/ON");
            ReadForRewrite(doc, defaultConfig?[PdfName.OFF], "/OFF");

            // The inventory, with the new group appended rather than replacing what is there.
            var allOcgs = BuildOcgList(ocProps?[PdfName.OCGs], doc);
            allOcgs.Add(ocgNum);

            var onList = BuildOcgList(defaultConfig?[PdfName.ON], doc);
            var offList = BuildOcgList(defaultConfig?[PdfName.OFF], doc);
            if (visible) onList.Add(ocgNum); else offList.Add(ocgNum);

            PdfDictionary updatedConfig = defaultConfig != null
                ? PdfPageContentAppender.CloneDictionary(defaultConfig)
                : new PdfDictionary();
            updatedConfig.Set(PdfName.ON, OcgRefArray(doc, onList));
            updatedConfig.Set(PdfName.OFF, OcgRefArray(doc, offList));

            int configObjNum = ocProps != null && defaultConfig != null
                ? GetIndirectObjNum(ocProps, PdfName.D)
                : 0;

            PdfDictionary updatedOcProps = ocProps != null
                ? PdfPageContentAppender.CloneDictionary(ocProps)
                : new PdfDictionary();
            updatedOcProps.Set(PdfName.OCGs, OcgRefArray(doc, allOcgs));

            if (configObjNum > 0)
            {
                updatedConfig.ObjectNumber = configObjNum;
                updatedConfig.GenerationNumber = 0;
                objects.Add(new IndirectObject(configObjNum, 0, updatedConfig));
            }
            else
            {
                int newConfigNum = nextObjNum++;
                updatedConfig.ObjectNumber = newConfigNum;
                updatedConfig.GenerationNumber = 0;
                objects.Add(new IndirectObject(newConfigNum, 0, updatedConfig));
                updatedOcProps.Set(PdfName.D, new PdfReference(newConfigNum, 0));
            }

            // The catalog is rewritten whenever /OCProperties itself changes, which it always does
            // here: the inventory gained an entry.
            var updatedCatalog = PdfPageContentAppender.CloneDictionary(doc.Catalog.Dictionary);
            updatedCatalog.Set(PdfName.OCProperties, updatedOcProps);
            updatedCatalog.ObjectNumber = catalogObjNum;
            updatedCatalog.GenerationNumber = 0;
            objects.Add(new IndirectObject(catalogObjNum, 0, updatedCatalog));

            PdfPageContentAppender.AppendRevision(ms, output, doc, objects, nextObjNum);
            return ocgNum;
        }

        /// <summary>
        /// Puts an existing object under a layer by setting its <c>/OC</c> entry, and reports
        /// whether it was written.
        /// </summary>
        /// <param name="input">Source PDF stream. Not modified.</param>
        /// <param name="output">Receives the original bytes plus one appended revision.</param>
        /// <param name="objectNumber">
        /// The XObject or annotation to place under the layer. It must be an indirect object whose
        /// value is a dictionary or a stream.
        /// </param>
        /// <param name="ocgObjectNumber">The group, as returned by <see cref="CreateLayer"/>.</param>
        /// <returns><c>true</c> when a revision was appended.</returns>
        /// <remarks>
        /// <para><b>Only whole objects, deliberately.</b> §8.11.3.2 gives two ways to make content
        /// optional: an <c>/OC</c> entry on an image or form XObject or an annotation, which is a
        /// dictionary edit and appends cleanly as a revision; and a <c>/OC … BDC</c> marked-content
        /// section inside a content stream, which is content surgery and needs a page
        /// <c>/Properties</c> entry because an OCG has to be an indirect reference. This does the
        /// first. Doing the second through this API would look identical to a caller and behave
        /// nothing like it.</para>
        ///
        /// <para><b>The group is not validated against the inventory here, and that is not an
        /// oversight</b> — a caller may legitimately assign to a group created in an earlier
        /// revision. What it does refuse is an object that is not a dictionary or stream, because
        /// an <c>/OC</c> on anything else is not a thing the specification defines and a viewer
        /// would ignore it silently.</para>
        /// </remarks>
        public static bool AssignObjectToLayer(Stream input, Stream output,
                                               int objectNumber, int ocgObjectNumber)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            RefuseEncrypted(doc);

            PdfObject target = objectNumber > 0 ? doc.GetObject(objectNumber) : null;

            // One clone path for both: PdfStream derives from PdfDictionary and
            // PdfDictionary.CloneShallow returns a stream for a stream, carrying RawData. Writing a
            // separate stream branch here would be a second answer to a question that already has
            // one - and the copy this library shipped before that guard existed dropped an image
            // XObject's data with no exception and no diagnostic.
            if (!(target is PdfDictionary source))
            {
                ms.Position = 0;
                ms.CopyTo(output);
                return false;
            }

            PdfDictionary updated = PdfPageContentAppender.CloneDictionary(source);

            updated.Set(PdfName.OC, ExistingObjectGeneration.ReferenceTo(doc.CrossReferenceTable, ocgObjectNumber));
            updated.ObjectNumber = objectNumber;
            updated.GenerationNumber = 0;

            int newSize = Math.Max(doc.CrossReferenceTable.GetMaxObjectNumber() + 1,
                                   objectNumber + 1);
            PdfPageContentAppender.AppendRevision(
                ms, output, doc,
                new List<IndirectObject> { new IndirectObject(objectNumber, 0, updated) },
                newSize);
            return true;
        }

        /// <summary>
        /// Puts a page's existing content on a layer, by wrapping it in an <c>/OC … BDC</c>
        /// … <c>EMC</c> marked-content section (§8.11.3.2).
        /// </summary>
        /// <param name="input">Source PDF stream.</param>
        /// <param name="output">Receives the document with one revision appended.</param>
        /// <param name="pageIndex">Zero-based page whose content joins the layer.</param>
        /// <param name="ocgObjectNumber">
        /// The optional-content group, from <see cref="CreateLayer"/> or <see cref="ListLayers"/>.
        /// </param>
        /// <returns>True when the page was wrapped; false when the index names no page.</returns>
        /// <remarks>
        /// <para><b>This is the route <see cref="AssignObjectToLayer"/> cannot take.</b> That one
        /// writes an <c>/OC</c> key on an object — an annotation, an image or form XObject
        /// — which covers everything a document draws <i>through a reference</i>. Ink written
        /// directly into a content stream has no object to hang a key on, so §8.11.3.2's other
        /// route is the only one: a marked-content section naming the group.</para>
        ///
        /// <para><b>The property list has to be an indirect reference, and that is what needed a
        /// <c>/Properties</c> entry.</b> §14.6.2 lets a <c>BDC</c> carry its property list
        /// inline only when the list contains no indirect reference, and an OCG is always one —
        /// so the group is named through the page's <c>/Properties</c> resource dictionary, which
        /// this adds. <c>Core/Writing/PdfContentStreamBuilder</c> inlines its own BDC property
        /// lists for exactly the opposite reason: its lists hold an MCID and nothing indirect.</para>
        ///
        /// <para><b>The existing streams are not rewritten.</b> §7.8.2 concatenates a
        /// <c>/Contents</c> array as if it were one stream, so the <c>BDC</c> goes in a new stream
        /// before them and the <c>EMC</c> in a new stream after — the original bytes are
        /// untouched and any signature over them still verifies, which re-serialising the content
        /// would have cost. It also means this composes: wrapping twice nests two sections rather
        /// than corrupting one.</para>
        ///
        /// <para><b>The whole page, deliberately.</b> Naming a region would mean deciding which
        /// operators fall inside it, and a content stream has no addressable sub-parts — the
        /// caller who needs finer control draws through a form XObject and uses
        /// <see cref="AssignObjectToLayer"/>, which is what that method is for.</para>
        /// </remarks>
        public static bool AssignPageContentToLayer(Stream input, Stream output,
                                                    int pageIndex, int ocgObjectNumber)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            RefuseEncrypted(doc);

            if (pageIndex < 0 || doc.Pages == null || pageIndex >= doc.Pages.Count)
            {
                ms.Position = 0;
                ms.CopyTo(output);
                return false;
            }

            PdfPage page = doc.Pages[pageIndex];
            int pageObjNum = page.Dictionary.ObjectNumber;
            if (pageObjNum <= 0)
            {
                ms.Position = 0;
                ms.CopyTo(output);
                return false;
            }

            int nextObjNum = doc.CrossReferenceTable.GetMaxObjectNumber() + 1;
            var objects = new List<IndirectObject>();

            // The page is superseded with its /Resources and their /Properties rebuilt from what was
            // read, so an unreadable one is refused: read as absent, the page kept only the layer's
            // entry, and its fonts and images went.
            string where = "page " + (pageIndex + 1).ToString(CultureInfo.InvariantCulture) + "'s ";
            ReadForRewrite(doc, page.Dictionary[PdfName.Resources], where + "/Resources", AssignConsequence);
            ReadForRewrite(doc, page.Resources?[PdfName.Properties], where + "/Properties", AssignConsequence);

            // A name unique within this page's /Properties, so wrapping a page twice — for two
            // layers — does not have the second entry overwrite the first and silently move
            // the first section onto the second group.
            PdfDictionary existingProperties = ResolveProperties(doc, page);
            string propertyName = UniquePropertyName(existingProperties, ocgObjectNumber);

            int beginNum = nextObjNum++;
            objects.Add(new IndirectObject(beginNum, 0,
                ContentStream("/OC /" + propertyName + " BDC\n")));

            int endNum = nextObjNum++;
            objects.Add(new IndirectObject(endNum, 0, ContentStream("EMC\n")));

            PdfDictionary updatedPage = PdfPageContentAppender.CloneDictionary(page.Dictionary);

            var contents = new PdfArray();
            contents.Add(new PdfReference(beginNum, 0));
            foreach (PdfObject existing in
                     PdfPageContentAppender.CopyExistingContents(doc, page.Dictionary))
                contents.Add(existing);
            contents.Add(new PdfReference(endNum, 0));
            updatedPage.Set(PdfName.Contents, contents);

            // The /Properties entry the BDC's name resolves through. Merged into whatever the page
            // already had rather than replacing it: another marked-content section on this page is
            // naming its own group through the same dictionary.
            var properties = existingProperties == null
                ? new PdfDictionary()
                : PdfPageContentAppender.CloneDictionary(existingProperties);
            properties.Set(new PdfName(propertyName), ExistingObjectGeneration.ReferenceTo(doc.CrossReferenceTable, ocgObjectNumber));

            PdfDictionary resources = page.Resources == null
                ? new PdfDictionary()
                : PdfPageContentAppender.CloneDictionary(page.Resources);
            resources.Set(PdfName.Properties, properties);
            updatedPage.Set(PdfName.Resources, resources);

            updatedPage.ObjectNumber = pageObjNum;
            updatedPage.GenerationNumber = 0;
            objects.Add(new IndirectObject(pageObjNum, 0, updatedPage));

            PdfPageContentAppender.AppendRevision(ms, output, doc, objects, nextObjNum);
            return true;
        }

        /// <summary>A content stream holding <paramref name="operators"/>, uncompressed.</summary>
        /// <remarks>
        /// Uncompressed on purpose: these are a dozen bytes each, and Flate would make them larger
        /// as well as unreadable to anyone opening the file to see what happened.
        /// </remarks>
        private static PdfStream ContentStream(string operators)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(operators);

            var stream = new PdfStream();
            stream.Set(PdfName.Length, new PdfNumber(bytes.Length));
            stream.RawData = bytes;
            return stream;
        }

        /// <summary>The page's own <c>/Properties</c> dictionary, resolved, or null.</summary>
        private static PdfDictionary ResolveProperties(PdfDocument doc, PdfPage page)
        {
            PdfObject raw = page.Resources?[PdfName.Properties];
            return raw == null ? null : doc.ResolveObject(raw) as PdfDictionary;
        }

        /// <summary>
        /// A <c>/Properties</c> key that is free on this page, preferring one named after the group.
        /// </summary>
        /// <remarks>
        /// Reusing a key that already names a different object would move whatever content
        /// referenced it onto this group — a silent change to a part of the page nobody asked
        /// about.
        /// </remarks>
        private static string UniquePropertyName(PdfDictionary properties, int ocgObjectNumber)
        {
            string preferred = "oc" + ocgObjectNumber.ToString(CultureInfo.InvariantCulture);
            if (properties == null || properties[new PdfName(preferred)] == null) return preferred;

            // No cap. Each candidate is a different key and the dictionary holds finitely many, so
            // at most Count + 1 are tried. A cap giving up and returning `preferred` — which is
            // taken — would move the content already named by it onto this group: the one outcome
            // this method exists to prevent.
            for (int suffix = 2; ; suffix++)
            {
                string candidate = preferred + "_"
                                   + suffix.ToString(CultureInfo.InvariantCulture);
                if (properties[new PdfName(candidate)] == null) return candidate;
            }
        }

        /// <summary>
        /// Sets the default visibility of an OCG layer identified by name.
        /// If multiple layers share the name, all are updated.
        /// </summary>
        /// <param name="input">Source PDF stream.</param>
        /// <param name="output">Output PDF stream.</param>
        /// <param name="layerName">Name of the layer to update (case-sensitive).</param>
        /// <param name="visible">True to make the layer visible by default; false to hide it.</param>
        /// <returns>Number of layers updated.</returns>
        public static int SetLayerVisibility(Stream input, Stream output, string layerName, bool visible)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (layerName == null) throw new ArgumentNullException(nameof(layerName));

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            RefuseEncrypted(doc);

            // Each entry read for the rewrite, and refused when the parser could not read it, as
            // CreateLayer refuses them: an unreadable /ON or /OFF read as empty, and the lists written
            // back held only the targets — every layer the document hid shown under /BaseState /ON,
            // every other layer hidden under /BaseState /OFF. An unreadable /OCProperties, /D or
            // /OCGs answered "no such layer".
            var ocProps = ReadForRewrite(doc, doc.Catalog?.Dictionary?[PdfName.OCProperties], "/OCProperties",
                                         VisibilityConsequence) as PdfDictionary;
            if (ocProps == null) { ms.Position = 0; ms.CopyTo(output); return 0; }

            var defaultConfig = ReadForRewrite(doc, ocProps[PdfName.D], "the default configuration /D",
                                               VisibilityConsequence) as PdfDictionary;
            if (defaultConfig == null) { ms.Position = 0; ms.CopyTo(output); return 0; }

            // Resolved: an indirect /OCGs left <c>targets</c> empty, so this returned 0 and copied the
            // document through unchanged — the caller was told nothing happened, which was true,
            // and not that it had asked for something the file supported.
            var ocgsArr = ReadForRewrite(doc, ocProps[PdfName.OCGs], "/OCGs", VisibilityConsequence) as PdfArray;
            if (ocgsArr == null) { ms.Position = 0; ms.CopyTo(output); return 0; }
            ReadForRewrite(doc, defaultConfig[PdfName.ON], "/ON", VisibilityConsequence);
            ReadForRewrite(doc, defaultConfig[PdfName.OFF], "/OFF", VisibilityConsequence);

            // Collect target OCG object numbers.
            var targets = new HashSet<int>();
            for (int i = 0; i < ocgsArr.Count; i++)
            {
                var ocgRef = ocgsArr[i] as PdfReference;
                if (ocgRef == null) continue;
                var ocgDict = doc.ResolveObject(ocgRef) as PdfDictionary;
                if (ocgDict == null) continue;
                var name = OcgName(doc, ocgDict);
                if (name == layerName)
                    targets.Add(ocgRef.ReferenceObjectNumber);
            }

            if (targets.Count == 0) { ms.Position = 0; ms.CopyTo(output); return 0; }

            // Clone the default config and rebuild /ON and /OFF lists.
            var updatedConfig = PdfPageContentAppender.CloneDictionary(defaultConfig);

            var onList = BuildOcgList(defaultConfig[PdfName.ON], doc);
            var offList = BuildOcgList(defaultConfig[PdfName.OFF], doc);

            // Remove targets from both lists, then add to the desired one.
            onList.RemoveWhere(n => targets.Contains(n));
            offList.RemoveWhere(n => targets.Contains(n));
            if (visible)
                foreach (int n in targets) onList.Add(n);
            else
                foreach (int n in targets) offList.Add(n);

            updatedConfig.Set(PdfName.ON, OcgRefArray(doc, onList));
            updatedConfig.Set(PdfName.OFF, OcgRefArray(doc, offList));

            int maxObj = doc.CrossReferenceTable.GetMaxObjectNumber();
            int nextObjNum = maxObj + 1;
            var objects = new List<IndirectObject>();

            // If the default config was an indirect object, update it in-place.
            int configObjNum = GetIndirectObjNum(ocProps, PdfName.D);
            if (configObjNum > 0)
            {
                updatedConfig.ObjectNumber = configObjNum;
                updatedConfig.GenerationNumber = 0;
                objects.Add(new IndirectObject(configObjNum, 0, updatedConfig));
            }
            else
            {
                // Inline: re-embed the updated config inside a cloned /OCProperties, which the
                // catalog then names — so only here is a /Root written directly refused. It answered
                // "no such layer" for every file, needed or not.
                int catalogObjNum = CatalogObject.NumberOrRefuse(doc.Trailer, "the default configuration");
                int newConfigNum = nextObjNum++;
                updatedConfig.ObjectNumber = newConfigNum;
                updatedConfig.GenerationNumber = 0;
                objects.Add(new IndirectObject(newConfigNum, 0, updatedConfig));

                var updatedOcProps = PdfPageContentAppender.CloneDictionary(ocProps);
                updatedOcProps.Set(PdfName.D, new PdfReference(newConfigNum, 0));

                var updatedCatalog = PdfPageContentAppender.CloneDictionary(doc.Catalog.Dictionary);
                updatedCatalog.Set(PdfName.OCProperties, updatedOcProps);
                updatedCatalog.ObjectNumber = catalogObjNum;
                updatedCatalog.GenerationNumber = 0;
                objects.Add(new IndirectObject(catalogObjNum, 0, updatedCatalog));
            }

            int newSize = nextObjNum;
            PdfPageContentAppender.AppendRevision(ms, output, doc, objects, newSize);

            return targets.Count;
        }

        /// <summary>
        /// Renames an OCG layer. If multiple layers share the old name, all are renamed.
        /// </summary>
        /// <returns>Number of layers renamed.</returns>
        public static int RenameLayer(Stream input, Stream output, string oldName, string newName)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (oldName == null) throw new ArgumentNullException(nameof(oldName));
            if (newName == null) throw new ArgumentNullException(nameof(newName));

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            RefuseEncrypted(doc);

            var ocProps = GetOcProperties(doc);
            if (ocProps == null) { ms.Position = 0; ms.CopyTo(output); return 0; }

            // Resolved: an indirect /OCGs left <c>targets</c> empty, so this returned 0 and copied the
            // document through unchanged — the caller was told nothing happened, which was true,
            // and not that it had asked for something the file supported.
            var ocgsArr = doc.ResolveObject(ocProps[PdfName.OCGs]) as PdfArray;
            if (ocgsArr == null) { ms.Position = 0; ms.CopyTo(output); return 0; }

            int maxObj = doc.CrossReferenceTable.GetMaxObjectNumber();
            int nextObjNum = maxObj + 1;
            var objects = new List<IndirectObject>();
            int count = 0;

            for (int i = 0; i < ocgsArr.Count; i++)
            {
                var ocgRef = ocgsArr[i] as PdfReference;
                if (ocgRef == null) continue;
                var ocgDict = doc.ResolveObject(ocgRef) as PdfDictionary;
                if (ocgDict == null) continue;
                var name = OcgName(doc, ocgDict);
                if (name != oldName) continue;

                var updated = PdfPageContentAppender.CloneDictionary(ocgDict);
                updated.Set(PdfName.Name, new PdfString(newName));
                updated.ObjectNumber = ocgRef.ReferenceObjectNumber;
                updated.GenerationNumber = 0;
                objects.Add(new IndirectObject(ocgRef.ReferenceObjectNumber, 0, updated));
                count++;
            }

            if (count == 0) { ms.Position = 0; ms.CopyTo(output); return 0; }

            int newSize = nextObjNum;
            PdfPageContentAppender.AppendRevision(ms, output, doc, objects, newSize);

            return count;
        }

        // ----------------------------------------------------------------
        //  Helpers
        // ----------------------------------------------------------------

        /// <remarks>
        /// <para><b><c>OcgInfo.DefaultVisible</c> is the renderers' answer, not a second one.</b>
        /// Deciding visibility here — <c>isOn = !inOffList || inOnList</c> — would read <c>/OFF</c>
        /// and <c>/ON</c> and <b>never <c>/BaseState</c></b>, so a document whose default
        /// configuration says <c>/BaseState /OFF</c> would report every layer
        /// <c>DefaultVisible = true</c> while both renderers draw none of them. The rule lives once,
        /// in <c>PdfOptionalContentRefs.BuildHiddenSet</c>, which is also what
        /// <c>PdfPageRenderer.BuildHiddenOcgSet</c> calls, and it resolves an indirect <c>/OFF</c>,
        /// so an <c>/OFF 9 0 R</c> hides its layers.</para>
        /// </remarks>
        internal static IReadOnlyList<OcgInfo> CollectOcgs(PdfDocument doc)
        {
            var result = new List<OcgInfo>();
            var ocProps = GetOcProperties(doc);
            if (ocProps == null) return result;

            var ocgsArr = doc.ResolveObject(ocProps[PdfName.OCGs]) as PdfArray;
            if (ocgsArr == null) return result;

            // Null means "nothing is hidden", which is the common case and why the shared rule
            // reports it that way rather than allocating an empty set per document.
            var hidden = PdfOptionalContentRefs.BuildHiddenSet(doc, ocProps);

            for (int i = 0; i < ocgsArr.Count; i++)
            {
                var ocgRef = ocgsArr[i] as PdfReference;
                if (ocgRef == null) continue;
                var ocgDict = doc.ResolveObject(ocgRef) as PdfDictionary;
                if (ocgDict == null) continue;
                var name = OcgName(doc, ocgDict) ?? "(unnamed)";
                bool isOn = hidden == null || !hidden.Contains(ocgRef.ReferenceObjectNumber);
                result.Add(new OcgInfo(ocgRef.ReferenceObjectNumber, name, isOn));
            }
            return result;
        }

        /// <summary>
        /// An optional-content group's <c>/Name</c>, resolved: a text string by §8.11.2.1 (Table 96),
        /// accepted as a name too, since producers write both.
        /// </summary>
        /// <remarks>
        /// One read for listing, hiding and renaming, which each carried a copy. All three used
        /// <c>GetName</c> and then the raw indexer, neither of which follows a reference, so a layer
        /// whose name was written as its own object (<c>/Name 7 0 R</c>) listed as "(unnamed)" and
        /// could be neither hidden nor renamed by the name it showed in any viewer.
        /// </remarks>
        private static string OcgName(PdfDocument doc, PdfDictionary ocgDict)
            => Rendering.PdfResolve.Value(doc, ocgDict[PdfName.Name]) switch
            {
                PdfString text => text.GetText(),
                PdfName name => name.Value,
                _ => null,
            };

        /// <summary>
        /// <paramref name="value"/> resolved for a writer that rebuilds it, or null when absent;
        /// refused when the parser could not read it (<c>Core.Document.EntryRead</c>).
        /// </summary>
        private static PdfObject ReadForRewrite(PdfDocument doc, PdfObject value, string what,
            string consequence = "adding a layer would replace it with one holding only the new layer")
        {
            var resolved = EntryRead.Resolve(doc.ResolveObject, value, out string unreadable);
            if (unreadable != null)
                throw new Core.PdfException(
                    "The document's " + what + " could not be read (" + unreadable + "), so " + consequence +
                    ". No changes were written.");
            return resolved;
        }

        private const string VisibilityConsequence =
            "setting a layer's visibility would rewrite the default configuration without what it held";

        private const string AssignConsequence =
            "wrapping the page in a layer would replace it with one holding only the layer's entry";

        private static PdfDictionary GetOcProperties(PdfDocument doc)
        {
            var catalog = doc.Catalog?.Dictionary;
            if (catalog == null) return null;
            var obj = catalog[PdfName.OCProperties];
            if (obj == null) return null;
            return doc.ResolveObject(obj) as PdfDictionary;
        }

        // One implementation, in PdfOptionalContentRefs, which resolves an indirect /OFF. Read as
        // empty, it would let the caller's Set() below REPLACE the config's lists — making one
        // layer visible reset every other layer's state.
        private static HashSet<int> BuildOcgList(PdfObject listObj, PdfDocument doc)
            => PdfOptionalContentRefs.Collect(doc, listObj);

        /// <summary>
        /// References to the groups in <paramref name="objNums"/>, each at its own generation.
        /// </summary>
        /// <remarks>
        /// The lists are carried as object numbers, and this rebuilt every reference as
        /// <c>N 0 R</c>. A group at generation 1 then named no object (§7.3.10), so adding one layer
        /// dropped an existing one from the inventory and from both visibility lists.
        /// </remarks>
        private static PdfArray OcgRefArray(PdfDocument doc, HashSet<int> objNums)
        {
            var arr = new PdfArray();
            foreach (int n in objNums)
                arr.Add(ExistingObjectGeneration.ReferenceTo(doc.CrossReferenceTable, n));
            return arr;
        }

        /// <summary>
        /// Refuses to write into an encrypted document. It loads no catalog, so a new layer was
        /// "not written" and a page was "out of range", while an object put under a layer was
        /// rewritten with its strings still encrypted and appended in clear, where a reader
        /// decrypts them into garbage.
        /// </summary>
        private static void RefuseEncrypted(PdfDocument doc)
            => EncryptedRevision.Refuse(doc, "change the layers of", "the revision");

        private static int GetIndirectObjNum(PdfDictionary parent, PdfName key)
        {
            // The entry's own reference, or 0 for a dictionary written inline: the parser stamps an
            // identity only on a top-level object, so an inline /D has none.
            return parent[key] is PdfReference r ? r.ReferenceObjectNumber : 0;
        }
    }

    /// <summary>Information about a single Optional Content Group (layer).</summary>
    public sealed class OcgInfo
    {
        /// <summary>Internal PDF object number of the OCG dictionary.</summary>
        public int ObjectNumber { get; }
        /// <summary>Display name of the layer.</summary>
        public string Name { get; }
        /// <summary>Whether the layer is visible by default.</summary>
        public bool DefaultVisible { get; }

        internal OcgInfo(int objectNumber, string name, bool defaultVisible)
        {
            ObjectNumber = objectNumber;
            Name = name;
            DefaultVisible = defaultVisible;
        }
    }
}
