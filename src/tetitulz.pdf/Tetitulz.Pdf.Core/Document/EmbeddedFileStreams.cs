// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Document
{
    /// <summary>
    /// Which streams of a document are embedded files — the streams ISO 32000-2 §7.6.5's <c>/EFF</c>
    /// crypt filter applies to, rather than <c>/StmF</c>'s, and that the linearizer's embedded file
    /// stream hint table groups. The encryptor, the decryptor and the linearizer ask the same question,
    /// so they ask it here.
    /// </summary>
    /// <remarks>
    /// Two signs, both §7.11.4's: a stream's own <c>/Type /EmbeddedFile</c> (optional, but what every
    /// producer surveyed writes), and a file specification's <c>/EF</c> dictionary naming it — the
    /// definition, and the one that catches a stream without its <c>/Type</c>.
    /// </remarks>
    public static class EmbeddedFileStreams
    {
        /// <summary>The deepest a direct object is searched for a file specification's <c>/EF</c>.</summary>
        public const int MaxSearchDepth = 32;

        /// <summary>§7.11.3's related-files entry, <c>/RF</c>.</summary>
        private static readonly PdfName RelatedFiles = new PdfName("RF");

        /// <summary>True when <paramref name="obj"/> is a stream naming itself <c>/Type /EmbeddedFile</c>.</summary>
        public static bool NamesItself(PdfObject obj)
            => obj is PdfStream stream && stream.GetName(PdfName.Type) == PdfName.EmbeddedFile;

        /// <summary>
        /// The object numbers every file specification's <c>/EF</c> dictionary names among
        /// <paramref name="objects"/> and the direct objects inside them, to <see cref="MaxSearchDepth"/>.
        /// </summary>
        public static HashSet<int> ReferencedBy(IEnumerable<PdfObject> objects)
            => ReferencedBy(objects, null);

        /// <summary>
        /// <see cref="ReferencedBy(IEnumerable{PdfObject})"/>, with a file specification's <c>/EF</c> and
        /// <c>/RF</c> read through references into <paramref name="map"/>: an indirect <c>/EF</c> dictionary's
        /// streams (§7.11.3 allows any value to be indirect), and the related files an <c>/RF</c> dictionary's
        /// arrays name (§7.11.4.2: each array alternates a file name and its embedded file stream). A stream
        /// without its optional <c>/Type</c>, named only so, was taken for ordinary content.
        /// </summary>
        public static HashSet<int> ReferencedBy(IReadOnlyDictionary<int, PdfObject> map)
            => ReferencedBy(map?.Values ?? (IEnumerable<PdfObject>)new PdfObject[0], map);

        private static HashSet<int> ReferencedBy(IEnumerable<PdfObject> objects, IReadOnlyDictionary<int, PdfObject> map)
        {
            var found = new HashSet<int>();
            PdfObject Resolve(PdfObject o)
                => map != null && o is PdfReference r && map.TryGetValue(r.ReferenceObjectNumber, out PdfObject target) ? target : o;
            void AddStreams(PdfObject container)
            {
                if (Resolve(container) is PdfDictionary ef)
                    foreach (var kv in ef)
                        if (kv.Value is PdfReference reference) found.Add(reference.ReferenceObjectNumber);
            }
            void AddRelated(PdfObject container)
            {
                if (!(Resolve(container) is PdfDictionary rf)) return;
                foreach (var kv in rf)
                    if (Resolve(kv.Value) is PdfArray related)
                        foreach (var item in related)
                            if (item is PdfReference reference) found.Add(reference.ReferenceObjectNumber);
            }
            void Visit(PdfObject o, int depth)
            {
                if (depth > MaxSearchDepth) return;
                if (o is PdfDictionary d)
                {
                    AddStreams(d[PdfName.EF]);
                    if (map != null) AddRelated(d[RelatedFiles]);
                    foreach (var kv in d) if (!(kv.Value is PdfReference)) Visit(kv.Value, depth + 1);
                }
                else if (o is PdfArray a)
                {
                    foreach (var item in a) if (!(item is PdfReference)) Visit(item, depth + 1);
                }
            }
            foreach (PdfObject o in objects) Visit(o, 0);
            return found;
        }
    }
}
