// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Tetitulz.Pdf.DocxToPdf.Rendering
{
    /// <summary>
    /// Which header and footer part each page of a section shows — the one decision for the legacy
    /// renderer and the Builder mapper, so a document's bands cannot differ between the two paths.
    ///
    /// <para><b>A reference is inherited by type</b> (ECMA-376 Part 1 §17.10.5): a section with no
    /// reference of a type (default, first, even) takes the previous section's, which is how Word
    /// writes "same as previous"; a section's own reference of that type replaces it. The sections
    /// are entered in document order through <see cref="EnterSection"/>.</para>
    ///
    /// <para><b>A type stands only for itself.</b> The first page of a <c>w:titlePg</c> section
    /// that references no <c>first</c> band shows none, which is what titlePg is for, and an even
    /// page under <c>w:evenAndOddHeaders</c> with no <c>even</c> band likewise; the default band is
    /// the default page's alone, and a <c>first</c> reference is never taken for it.</para>
    ///
    /// <para><b>A reference naming no part is an ordinary outcome</b> — a producer may write a stale
    /// id — so the band is not drawn and the id is reported once, rather than the whole conversion
    /// failing on the lookup.</para>
    /// </summary>
    internal sealed class HeaderFooterReferences
    {
        private readonly MainDocumentPart _main;
        private readonly Dictionary<HeaderFooterValues, string> _headers = new Dictionary<HeaderFooterValues, string>();
        private readonly Dictionary<HeaderFooterValues, string> _footers = new Dictionary<HeaderFooterValues, string>();
        private readonly HashSet<string> _missingReported = new HashSet<string>();

        internal HeaderFooterReferences(MainDocumentPart main)
        {
            _main = main;
        }

        /// <summary>Takes a section's own references, each replacing the inherited one of its type.</summary>
        internal void EnterSection(SectionProperties sectPr)
        {
            if (sectPr == null) return;
            foreach (var r in sectPr.Elements<HeaderReference>())
                if (r.Id?.Value != null) _headers[r.Type?.Value ?? HeaderFooterValues.Default] = r.Id.Value;
            foreach (var r in sectPr.Elements<FooterReference>())
                if (r.Id?.Value != null) _footers[r.Type?.Value ?? HeaderFooterValues.Default] = r.Id.Value;
        }

        /// <summary>The header part a page of the current section shows for <paramref name="want"/>, or null for none.</summary>
        internal OpenXmlElement Header(HeaderFooterValues want)
            => _headers.TryGetValue(want, out string id) && Part(id, "header") is HeaderPart hp ? hp.Header : null;

        /// <summary>The footer part a page of the current section shows for <paramref name="want"/>, or null for none.</summary>
        internal OpenXmlElement Footer(HeaderFooterValues want)
            => _footers.TryGetValue(want, out string id) && Part(id, "footer") is FooterPart fp ? fp.Footer : null;

        private OpenXmlPart Part(string id, string kind)
        {
            var part = OpenXmlPartLookup.TryGetPart(_main, id);
            if (part == null && _missingReported.Add(id))
                DocxDiagnostics.Report("HeaderFooterReferences",
                    $"the {kind} reference '{id}' names no part of the document; that {kind} was not drawn. " +
                    "Reported once per reference.");
            return part;
        }
    }
}
