// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;

namespace Tetitulz.Pdf.Core.Writing
{
    /// <summary>
    /// The generation an existing object carries, for a revision that supersedes it or refers to it
    /// (ISO 32000-1 §7.5.6: an updated object keeps its object number and generation).
    /// </summary>
    /// <remarks>
    /// <para><b>The cross-reference table is the authority, not a reference that points at the
    /// object</b> — §7.5.4 makes the entry's generation the object's own, and a damaged file can
    /// carry an <c>N G R</c> that disagrees with it. A reference is the fallback for an object the
    /// table cannot answer for.</para>
    ///
    /// <para><b>Never read it from a clone.</b> <c>PdfDictionary.CloneShallow</c> builds a bare
    /// container that carries neither <c>ObjectNumber</c> nor <c>GenerationNumber</c>, so code that
    /// asks the staged copy for its generation would compile, read correctly, and still emit
    /// <c>0</c> at every site.</para>
    ///
    /// <para><b>Moved from Signing's <c>SigningHelpers</c></b>, where it served the three writers
    /// there that build their own cross-reference sections and so never reach
    /// <see cref="IncrementalUpdateWriter"/>. All seven existing objects they supersede were once
    /// written at generation 0: the timestamper's catalog and <c>/AcroForm</c>, <c>PdfSigner</c>'s
    /// adopted field and catalog, <c>VisualSignatureBuilder</c>'s page and catalog, and
    /// <c>PdfLtvRefresher</c>'s catalog. The incremental writer (<c>KeptGeneration</c>) and Base's
    /// reference sites now ask the same question.</para>
    /// </remarks>
    internal static class ExistingObjectGeneration
    {
        /// <summary>
        /// The generation of in-use object <paramref name="objectNumber"/> in
        /// <paramref name="table"/>; else <paramref name="reference"/>'s when it is a reference; else 0.
        /// </summary>
        internal static int Of(CrossReferenceTable table, int objectNumber, PdfObject reference = null)
        {
            if (objectNumber <= 0) return 0;

            CrossReferenceEntry entry = table?.GetEntry(objectNumber);
            if (entry != null && entry.Type == CrossReferenceEntry.EntryType.Normal)
                return entry.GenerationNumber;

            return (reference as PdfReference)?.ReferenceGenerationNumber ?? 0;
        }

        /// <summary>A reference to existing object <paramref name="objectNumber"/> at its own generation.</summary>
        internal static PdfReference ReferenceTo(CrossReferenceTable table, int objectNumber)
            => new PdfReference(objectNumber, Of(table, objectNumber));
    }
}
