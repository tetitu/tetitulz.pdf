// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base.Operations.Metadata
{
    /// <summary>What <see cref="PdfVersionSetter.SetVersion"/> did.</summary>
    public sealed class PdfVersionChange
    {
        internal PdfVersionChange(PdfVersion previous, PdfVersion version, bool revisionAppended)
        {
            Previous = previous;
            Version = version;
            RevisionAppended = revisionAppended;
        }

        /// <summary>The version the document declared: its header, or its catalog's <c>/Version</c> when that is later.</summary>
        public PdfVersion Previous { get; }

        /// <summary>The version the output declares.</summary>
        public PdfVersion Version { get; }

        /// <summary>
        /// Whether a revision was appended; false when the document already declared the version
        /// asked for, and the output is then the input's bytes unchanged.
        /// </summary>
        public bool RevisionAppended { get; }
    }

    /// <summary>
    /// Raises an existing document's PDF version through the catalog's <c>/Version</c> (ISO 32000-2
    /// §7.7.2 Table 29), in one appended revision.
    /// </summary>
    /// <remarks>
    /// <para><b>Raised, never lowered.</b> A higher version is a promise a reader of it can keep for
    /// any file of a lower one, so raising needs nothing but the entry. A lower one would claim the
    /// document uses nothing past it, which only a scan of every feature the file uses could say,
    /// and the scan here reads objects being written, not a file's; lowering is refused.</para>
    /// <para><b>The header is not touched</b>: the original bytes come through unchanged, so a signature
    /// over them keeps its byte range, and a reader takes the later of the header and
    /// <c>/Version</c>. The entry is itself PDF 1.4, so a target below 1.4 — which no reader of the
    /// entry would read — is refused. A certification that permits no changes (<c>/P 1</c>) reports
    /// the appended revision as a change, as it would any.</para>
    /// <para>A clear-text revision writer: an encrypted document is refused
    /// (<see cref="EncryptedRevision.Refuse"/>).</para>
    /// </remarks>
    public static class PdfVersionSetter
    {
        /// <summary>
        /// Writes <paramref name="input"/> to <paramref name="output"/> declaring <paramref name="version"/>.
        /// </summary>
        /// <param name="input">Source PDF. Not disposed.</param>
        /// <param name="output">Receives the original bytes plus one appended revision, or the original bytes
        /// alone when the document already declares <paramref name="version"/>.</param>
        /// <param name="version">The version to declare: 1.4 to 1.7, or 2.0, and not below the document's.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="version"/> is not a published version.</exception>
        /// <exception cref="Core.PdfException">The version is below the document's or below 1.4; the
        /// document is encrypted; or its catalog cannot be superseded.</exception>
        public static PdfVersionChange SetVersion(Stream input, Stream output, PdfVersion version)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (!version.IsPublished)
                throw new ArgumentOutOfRangeException(nameof(version), version.ToString(), "A PDF version is 1.0 to 1.7, or 2.0.");

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            EncryptedRevision.Refuse(doc, "set the version of", "the catalog's /Version");

            PdfVersion previous = doc.Version;
            if (version < previous)
                throw new Core.PdfException(
                    $"The document declares PDF {previous}; it is not lowered to {version}. A lower version claims the " +
                    "document uses nothing past it, and only a rewrite that removes those features can make that true.");

            if (version == previous)
            {
                ms.Position = 0;
                ms.CopyTo(output);
                return new PdfVersionChange(previous, version, revisionAppended: false);
            }

            if (version < PdfVersion.Pdf14)
                throw new Core.PdfException(
                    $"PDF {version} cannot be declared over PDF {previous}: the header is part of the original bytes, " +
                    "and the catalog's /Version that raises it is itself PDF 1.4, so a raise is to 1.4 or above.");

            int catalogObjNum = CatalogObject.NumberOrRefuse(doc.Trailer, "the catalog's /Version");
            if (doc.Catalog == null)
                throw new Core.PdfException("The document's catalog cannot be read.");

            var updated = PdfPageContentAppender.CloneDictionary(doc.Catalog.Dictionary);
            updated.Set(PdfName.Version, new PdfName(version.ToString()));
            updated.ObjectNumber = catalogObjNum;
            updated.GenerationNumber = 0;

            PdfPageContentAppender.AppendRevision(
                ms, output, doc, new List<IndirectObject> { new IndirectObject(catalogObjNum, 0, updated) },
                doc.CrossReferenceTable.GetMaxObjectNumber() + 1);

            return new PdfVersionChange(previous, version, revisionAppended: true);
        }
    }
}
