// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Metadata;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Accessibility.Metadata
{
    /// <summary>
    /// A document title's <c>dc:title</c>, staged into a revision a title writer is building: every
    /// writer that sets a title (<c>SetDocumentTitle</c>, its encrypted plan, the remediation
    /// pipeline's metadata step, the auto-tagger) writes it beside the <c>/Info /Title</c> it
    /// writes, because ISO 14289-1 7.1 and ISO 14289-2 §8.11.1 read the title from the catalog's
    /// Metadata stream alone — a conforming reader ignores the document information dictionary.
    /// </summary>
    /// <remarks>
    /// The packet is the document's own with the title merged in (<see cref="PdfUaXmpWriter.BuildTitlePacket"/>):
    /// no PDF/UA identifier is added, a declaration this write does not judge. A packet that already
    /// carries the title is left alone. A new <c>/Metadata</c> stream is referenced from the catalog
    /// the caller's revision writes, so the reference lands beside the caller's own catalog edits.
    /// </remarks>
    internal static class DocumentTitleXmp
    {
        /// <summary>
        /// Stages <paramref name="title"/> as the packet's <c>dc:title</c>.
        /// </summary>
        /// <param name="doc">The document the revision is for.</param>
        /// <param name="index">Its object index, which allocates a new stream's number.</param>
        /// <param name="catalog">The document's catalog as read.</param>
        /// <param name="catalogInRevision">
        /// The catalog dictionary the caller's revision writes, created and registered by the caller
        /// on first call; asked only when a new <c>/Metadata</c> stream needs referencing.
        /// </param>
        /// <param name="title">The title. Blank stages nothing.</param>
        /// <param name="modified">The revision's superseded objects.</param>
        /// <param name="created">The revision's new objects.</param>
        /// <param name="operation">The operation named in a diagnostic.</param>
        /// <returns>Whether anything was staged.</returns>
        internal static bool Stage(
            PdfDocument doc, PdfObjectIndex index, PdfDictionary catalog,
            Func<PdfDictionary> catalogInRevision, string title,
            IDictionary<int, PdfObject> modified, IDictionary<int, PdfObject> created, string operation)
        {
            if (doc == null || index == null || catalog == null || string.IsNullOrWhiteSpace(title)) return false;

            void Report(string detail, Exception ex) => AccessibilityDiagnostics.Report(operation, detail, ex);

            string existing = XmpPacketDom.ReadCatalogPacket(doc, catalog, Report);
            string packet = PdfUaXmpWriter.BuildTitlePacket(existing, title);
            if (existing != null && string.Equals(packet, existing, StringComparison.Ordinal)) return false;

            Dictionary<int, PdfObject> revisedObjects, newObjects;
            try
            {
                XmpMetadataRevision.Build(doc, index, catalog, packet, out revisedObjects, out newObjects, Report);
            }
            catch (PdfException ex)
            {
                Report("the title could not be written to the XMP dc:title, where PDF/UA reads it", ex);
                return false;
            }

            foreach (KeyValuePair<int, PdfObject> entry in newObjects) created[entry.Key] = entry.Value;
            foreach (KeyValuePair<int, PdfObject> entry in revisedObjects)
            {
                if (entry.Value is PdfStream)
                {
                    modified[entry.Key] = entry.Value;
                    continue;
                }

                // The catalog Build revised to reference a new stream: the reference goes onto the
                // catalog the caller writes, which carries the caller's own edits.
                PdfDictionary target = catalogInRevision?.Invoke();
                if (target == null)
                {
                    Report("the catalog could not be revised to reference the new XMP Metadata stream; " +
                           "the title was not written to dc:title", null);
                    foreach (int number in newObjects.Keys) created.Remove(number);
                    return false;
                }
                target.Set(PdfName.Metadata, ((PdfDictionary)entry.Value)[PdfName.Metadata]);
            }
            return true;
        }
    }
}
