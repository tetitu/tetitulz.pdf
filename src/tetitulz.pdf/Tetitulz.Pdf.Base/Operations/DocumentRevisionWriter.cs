// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base.Operations
{
    /// <summary>
    /// Writes a document edit as a single appended PDF revision (an incremental update).
    ///
    /// <para>
    /// <b>What this type is now.</b> A thin adapter over
    /// <see cref="IncrementalUpdateWriter.WriteUpdate(byte[], Stream, IReadOnlyList{IndirectObject}, IncrementalUpdateOptions)"/>.
    /// It owns only the three things Core's writer deliberately does not:
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     the caller-facing shape — two <c>objectNumber → object</c> maps (revised objects and
    ///     newly allocated ones) rather than a flat list;
    ///   </description></item>
    ///   <item><description>
    ///     object-number and generation lookup through <see cref="PdfObjectIndex"/>, including
    ///     the allocation of the cross-reference stream's own object number;
    ///   </description></item>
    ///   <item><description>
    ///     the guarantee that an edit never corrupts the caller's file: every
    ///     failure is reported through <see cref="DocumentEditDiagnostics"/> and the output is
    ///     left holding exactly the original document.
    ///   </description></item>
    /// </list>
    ///
    /// <para>
    /// <b>Why the capability is split this way.</b> Everything below the object maps — the choice
    /// between a classic <c>xref</c> table and a cross-reference stream, the whitelisted trailer,
    /// the preserved/deterministically-synthesised <c>/ID</c>, the verified <c>/Prev</c> — once
    /// lived in <c>Tetitulz.Pdf.Accessibility</c> alongside this adapter, so it was reachable only
    /// from that feature assembly. <c>Tetitulz.Pdf.Plugins.Conformance</c> needs exactly the same
    /// behaviour and must never reference a feature (plugin → feature is a dependency cycle),
    /// which left it emitting a classic table onto xref-stream documents and warning about it. So
    /// the format logic moved into Core, and this adapter followed it down into Base (plan 04 D2)
    /// once the Conformance plugin needed the font remediator built on it. Both callers can now
    /// reach both halves.
    /// </para>
    ///
    /// <para>
    /// Serialisation itself is <see cref="PdfWriter"/>'s: it handles streams (including
    /// <c>/Length</c> and the <c>stream</c>/<c>endstream</c> envelope), writes strings from
    /// <see cref="PdfString.RawBytes"/> with no encoding round-trip — Hebrew and CJK <c>/Alt</c>
    /// and <c>/TU</c> text survive byte-for-byte — and threads real generation numbers into the
    /// cross-reference entries.
    /// </para>
    /// </summary>
    public static class DocumentRevisionWriter
    {
        /// <summary>
        /// Writes <paramref name="originalBytes"/> verbatim followed by exactly one appended
        /// revision containing every modified and new object.
        ///
        /// <para>
        /// When both object maps are empty (or null) only the original bytes are written, so
        /// callers can hand over unconditionally and get a byte-identical passthrough for a
        /// no-op edit.
        /// </para>
        ///
        /// <para>
        /// The revision is assembled in memory and copied to <paramref name="output"/> only
        /// once it is complete. A failure part-way therefore leaves <paramref name="output"/>
        /// holding exactly the original document rather than a truncated revision, and is
        /// reported through <see cref="DocumentEditDiagnostics"/> rather than thrown.
        /// </para>
        /// </summary>
        /// <param name="output">
        /// Destination for the whole document. Positioned at the start of the PDF; the
        /// original bytes have <b>not</b> been written yet — this method writes them.
        /// </param>
        /// <param name="originalBytes">The complete original PDF.</param>
        /// <param name="doc">The parsed original, used for the trailer and the previous xref offset.</param>
        /// <param name="index">
        /// Index over <paramref name="doc"/>, used for generation numbers and for allocating
        /// the cross-reference stream's own object number. May be null.
        /// </param>
        /// <param name="modifiedObjects">Existing objects being revised, keyed by object number.</param>
        /// <param name="newObjects">
        /// Objects being added, keyed by the object number the caller allocated (normally
        /// from <see cref="PdfObjectIndex.AllocateObjectNumber"/>).
        /// </param>
        /// <param name="newTrailerInfoObjectNumber">
        /// Object number of a newly written <c>/Info</c> dictionary, used only when the
        /// original trailer had no <c>/Info</c>. Pass -1 when not applicable.
        /// </param>
        public static void Write(
            Stream output,
            byte[] originalBytes,
            PdfDocument doc,
            PdfObjectIndex index,
            IReadOnlyDictionary<int, PdfObject> modifiedObjects,
            IReadOnlyDictionary<int, PdfObject> newObjects,
            int newTrailerInfoObjectNumber = -1)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (originalBytes == null) throw new ArgumentNullException(nameof(originalBytes));

            List<IndirectObject> objects = MergeObjects(modifiedObjects, newObjects);

            try
            {
                IncrementalUpdateWriter.WriteUpdate(
                    originalBytes, output, objects, BuildOptions(doc, index, newTrailerInfoObjectNumber));
            }
            catch (Exception ex)
            {
                // The revision is buffered inside the Core writer, so nothing partial reached
                // `output`: it still holds exactly the original document. Report and return
                // rather than throw — an edit must never corrupt the caller's file.
                DocumentEditDiagnostics.Report("DocumentRevisionWriter.Write",
                    "the incremental revision could not be built; the original document was written unchanged", ex);
            }
        }

        // ------------------------------------------------------------------
        //  Core writer configuration
        // ------------------------------------------------------------------

        private static IncrementalUpdateOptions BuildOptions(
            PdfDocument doc, PdfObjectIndex index, int newTrailerInfoObjectNumber)
        {
            var options = new IncrementalUpdateOptions
            {
                Document = doc,
                NewInfoObjectNumber = newTrailerInfoObjectNumber,
                DiagnosticSink = DocumentEditDiagnostics.Report,
            };

            if (index != null)
            {
                // The index is the authoritative view of the document's object numbering — it
                // was built from the parsed original and is what every caller allocated from.
                options.Size = index.MaxObjectNumber + 1;
                options.ObjectNumberResolver = index.GetObjectNumber;
                options.GenerationResolver = index.GetGeneration;

                // Only consulted when a cross-reference stream is actually emitted. Reserve
                // first so the allocator can never hand out a number this revision has already
                // written — a caller may number a new object itself rather than through the index
                // — then take the next free one. (A Math.Max against highestObjectNumber + 1 stood
                // here too; after the reserve it could never decide anything, and was removed.)
                options.XrefStreamObjectNumberAllocator = highestObjectNumber =>
                {
                    index.ReserveObjectNumber(highestObjectNumber);
                    return index.AllocateObjectNumber();
                };
            }

            return options;
        }

        // ------------------------------------------------------------------
        //  Object maps → the Core writer's flat, generation-carrying list
        // ------------------------------------------------------------------

        private static List<IndirectObject> MergeObjects(
            IReadOnlyDictionary<int, PdfObject> modifiedObjects,
            IReadOnlyDictionary<int, PdfObject> newObjects)
        {
            var merged = new SortedList<int, PdfObject>();

            AddAll(merged, newObjects, "newObjects");
            // Modified objects win a collision: a revision of an existing object is never
            // something a caller meant to shadow with a freshly allocated one.
            AddAll(merged, modifiedObjects, "modifiedObjects");

            // Generation 0 throughout: the Core writer keeps a superseded object's own
            // (IncrementalUpdateWriter.KeptGeneration), from the document's table, so a lookup here
            // from the index built from that same table would add nothing a test could tell.
            var list = new List<IndirectObject>(merged.Count);
            foreach (KeyValuePair<int, PdfObject> kvp in merged)
                list.Add(new IndirectObject(kvp.Key, 0, kvp.Value));

            return list;
        }

        private static void AddAll(SortedList<int, PdfObject> target,
            IReadOnlyDictionary<int, PdfObject> source, string sourceName)
        {
            if (source == null) return;

            foreach (var kvp in source)
            {
                if (kvp.Key <= 0)
                {
                    DocumentEditDiagnostics.Report("DocumentRevisionWriter.Write",
                        "object number " + kvp.Key.ToString(CultureInfo.InvariantCulture) + " in " + sourceName +
                        " is not a valid indirect object number; it was skipped");
                    continue;
                }

                if (kvp.Value == null)
                {
                    DocumentEditDiagnostics.Report("DocumentRevisionWriter.Write",
                        "object " + kvp.Key.ToString(CultureInfo.InvariantCulture) + " in " + sourceName +
                        " is null; it was skipped");
                    continue;
                }

                if (target.ContainsKey(kvp.Key))
                {
                    DocumentEditDiagnostics.Report("DocumentRevisionWriter.Write",
                        "object " + kvp.Key.ToString(CultureInfo.InvariantCulture) +
                        " appears in both object maps; the entry from " + sourceName + " was used");
                }

                target[kvp.Key] = kvp.Value;
            }
        }
    }
}
