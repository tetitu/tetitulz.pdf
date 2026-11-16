// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// The embedded-file repairs <see cref="PdfAConverter"/> makes for PDF/A-3 before it validates:
    /// what A67 asks of a kept attachment that can be supplied without guessing at its content. A file
    /// specification missing one of <c>/F</c> and <c>/UF</c> gets the other's value; one with no
    /// <c>/AFRelationship</c> gets <c>/Unspecified</c>; a stream whose <c>/Subtype</c> is not a MIME
    /// type gets <c>application/octet-stream</c> (ISO 19005-3 6.8's own fallback for an unknown type);
    /// a specification no <c>/AF</c> array names joins the catalog's.
    /// </summary>
    /// <remarks>
    /// What it leaves is the validator's to report: a specification with neither name, one held
    /// directly in the name tree (no object number to supersede), and a catalog that is not an
    /// indirect object.
    /// </remarks>
    internal static class PdfAEmbeddedFileRepair
    {
        /// <summary>What one run changed.</summary>
        internal sealed class Outcome
        {
            internal int SpecificationsRepaired;
            internal int SubtypesSet;
            internal int Associated;
            internal int Unrevisable;
        }

        /// <summary>Copies <paramref name="input"/> to <paramref name="output"/>, with the repairs appended as one revision.</summary>
        internal static Outcome Repair(byte[] input, Stream output)
        {
            var outcome = new Outcome();
            using var doc = PdfDocument.Open(new MemoryStream(input));
            var catalog = doc.Catalog?.Dictionary;
            var tree = doc.IsEncrypted ? null
                : doc.ResolveObject((doc.ResolveObject(catalog?[PdfName.Names]) as PdfDictionary)?[PdfName.EmbeddedFiles]) as PdfDictionary;
            var entries = new List<KeyValuePair<PdfObject, PdfObject>>();
            int budget = PdfLimits.MaxNameTreeEntries;
            if (tree != null) PdfNameTree.TryFlatten(tree, doc, entries, ref budget);

            var updates = new Dictionary<int, IndirectObject>();
            var unassociated = new List<PdfDictionary>();
            HashSet<PdfDictionary> associated = entries.Count > 0 ? PdfAValidator.AssociatedFilesOf(doc) : null;
            // A specification two keys name is repaired once: read twice, it was counted twice and
            // joined the catalog's /AF twice.
            var repaired = new HashSet<PdfDictionary>();
            foreach (var entry in entries)
            {
                if (!(doc.ResolveObject(entry.Value) is PdfDictionary spec) || !repaired.Add(spec)) continue;
                if (spec.ObjectNumber <= 0)
                {
                    outcome.Unrevisable++;
                    continue;
                }
                PdfDictionary revised = null;
                PdfDictionary Revised() => revised ??= PdfDictionary.CloneShallow(spec);
                if (spec[PdfName.F] == null && spec[PdfName.UF] != null) Revised()[PdfName.F] = spec[PdfName.UF];
                if (spec[PdfName.UF] == null && spec[PdfName.F] != null) Revised()[PdfName.UF] = spec[PdfName.F];
                if (spec[PdfName.AFRelationship] == null) Revised()[PdfName.AFRelationship] = new PdfName("Unspecified");
                if (revised != null)
                {
                    updates[spec.ObjectNumber] = new IndirectObject(spec.ObjectNumber, spec.GenerationNumber, revised);
                    outcome.SpecificationsRepaired++;
                }

                var ef = doc.ResolveObject(spec[PdfName.EF]) as PdfDictionary;
                foreach (var key in new[] { PdfName.F, PdfName.UF })
                    if (doc.ResolveObject(ef?[key]) is PdfStream file && file.ObjectNumber > 0 && !updates.ContainsKey(file.ObjectNumber)
                        && !PdfAValidator.IsMimeTypeName(doc.ResolveObject(file[PdfName.Subtype]) as PdfName))
                    {
                        var stream = PdfDictionary.CloneShallow(file);
                        stream[PdfName.Subtype] = new PdfName("application/octet-stream");
                        updates[file.ObjectNumber] = new IndirectObject(file.ObjectNumber, file.GenerationNumber, stream);
                        outcome.SubtypesSet++;
                    }

                if (!associated.Contains(spec)) unassociated.Add(spec);
            }

            if (unassociated.Count > 0)
            {
                // The catalog's /AF: an indirect array is superseded itself, a direct one (or none)
                // with the catalog — which needs an object number, as every catalog writer does.
                var existing = doc.ResolveObject(catalog[PdfName.AF]) as PdfArray;
                var af = new PdfArray();
                if (existing != null)
                    for (int i = 0; i < existing.Count; i++) af.Add(existing[i]);
                foreach (var spec in unassociated)
                    af.Add(new PdfReference(spec.ObjectNumber, spec.GenerationNumber));   // the object's own generation
                if (existing != null && existing.ObjectNumber > 0)
                {
                    updates[existing.ObjectNumber] = new IndirectObject(existing.ObjectNumber, existing.GenerationNumber, af);
                    outcome.Associated += unassociated.Count;
                }
                else if (doc.Trailer?[PdfName.Root] is PdfReference root && root.ReferenceObjectNumber > 0)
                {
                    var revisedCatalog = PdfDictionary.CloneShallow(catalog);
                    revisedCatalog[PdfName.AF] = af;
                    updates[root.ReferenceObjectNumber] = new IndirectObject(root.ReferenceObjectNumber, root.ReferenceGenerationNumber, revisedCatalog);
                    outcome.Associated += unassociated.Count;
                }
                else
                {
                    outcome.Unrevisable += unassociated.Count;
                }
            }

            RepairRevision.Write(input, output, doc, updates);
            return outcome;
        }
    }
}
