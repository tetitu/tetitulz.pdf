// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// PDF/X-6 (ISO 15930-9:2020), checked in part. The standard was not available; three public sources
    /// were, and each check names the one it rests on:
    /// <list type="bullet">
    ///   <item>the standard's preview (§6.1–6.2, normative): "PDF/X-6 is a restricted subset of PDF 2.0
    ///     … These features shall be used as prescribed in ISO 32000-2", and deprecated features "should
    ///     not appear";</item>
    ///   <item>the ISO TC 130 task force's <i>Application Notes for PDF/X-6</i> (APTech, 2022), which
    ///     lists what changed from PDF/X-4: page-level output intents, annotations, forms and
    ///     signatures, actions, black-point compensation, DPart;</item>
    ///   <item>ISO 32000-2, whose requirements §6.1 makes PDF/X-6's.</item>
    /// </list>
    /// <para><b>Violations</b> are only what those state: PDF 2.0 (X12), a GTS_PDFX output intent for the
    /// document or for every page (X02), no XFA (X28), ISO 32000-2's own optional content entries (X13).
    /// <b>Informational</b>, never affecting <see cref="PdfXValidationResult.IsConforming"/>: X29, which
    /// says what was not read; X-4's other rules, run and reported under their own codes as carried from
    /// X-4 — the Notes list what changed, not every rule that stayed; X26 and X27; and the deprecated
    /// features. X-4's rules the Notes say changed (actions X07, annotations X24, page intents X25), and
    /// those whose X-4 reading cannot be X-6's (X03's version string, X10's <c>/Info</c>, which PDF 2.0
    /// deprecates, X11 and X08 against one document-level intent), are not run.</para>
    /// </summary>
    public static partial class PdfXValidator
    {
        /// <summary>The prefix of an X-4 rule's finding carried to X-6 as informational.</summary>
        internal const string CarriedFromX4 =
            "Carried from PDF/X-4, not confirmed for PDF/X-6 (ISO 15930-9 was not available): ";

        /// <summary>What X-6 checks and what it does not — the first X29 entry of every X-6 result.</summary>
        internal const string X6Coverage =
            "PDF/X-6 is checked in part. Judged: PDF 2.0 and the use of its features as ISO 32000-2 prescribes " +
            "(ISO 15930-9 §6.1, from its public preview); a GTS_PDFX output intent for the document or for every " +
            "page (the PDF/X-6 Application Notes §3.2); no XFA (§3.4); ISO 32000-2's own optional content entries. " +
            "Not read: ISO 15930-9 §6.5 to §6.19 and Annex B — file structure, the characterised printing " +
            "condition and the print element colour spaces, graphics, fonts, bounding boxes, trapping, metadata " +
            "and identification, annotations, interactive forms beyond XFA, actions, optional content, viewer " +
            "preferences, alternate presentations, document requirements, spectral data, PDF/X-6n. PDF/X-4's " +
            "rules the Application Notes do not list as changed are run and reported here, not as violations.";

        private static PdfXValidationResult EncryptedX6(PdfXConformanceLevel level)
            => new PdfXValidationResult(new List<PdfXViolation>(), level, new List<PdfXViolation>
            {
                new PdfXViolation("X29", "The document is encrypted, so nothing in it was examined. " + X6Coverage),
                new PdfXViolation("X01", CarriedFromX4 + "PDF/X requires an unencrypted document."),
            });

        private static PdfXValidationResult ValidateX6(PdfDocument doc, PdfDictionary catalog,
            int maxPageContentBytes, long maxDocumentContentBytes)
        {
            var v = new List<PdfXViolation>();
            var info = new List<PdfXViolation> { new PdfXViolation("X29", X6Coverage) };

            // X12 — ISO 15930-9 §6.1: "PDF/X-6 is a restricted subset of PDF 2.0".
            var minimum = new PdfVersion(2, 0);
            if (doc.Version < minimum)
                v.Add(new PdfXViolation("X12",
                    $"PDF/X-6 is a subset of PDF 2.0 (ISO 15930-9 §6.1); this file declares {doc.Version}."));

            CheckX6OutputIntents(catalog, doc, v);

            // X28 — the Application Notes §3.4: "XML-based XFA forms are not permitted."
            if (Resolve<PdfDictionary>(doc, catalog[PdfName.AcroForm])?[PdfName.XFA] != null)
                v.Add(new PdfXViolation("X28",
                    "The interactive form carries /XFA; PDF/X-6 does not permit XFA forms (the PDF/X-6 " +
                    "Application Notes, §3.4)."));

            // X13 — ISO 32000-2's own optional content requirements, which §6.1 makes PDF/X-6's.
            CheckOptionalContentDeclared(catalog, doc, v, info, maxPageContentBytes, maxDocumentContentBytes);

            var carried = new List<PdfXViolation>();
            CheckFontEmbedding(doc, carried, info, maxPageContentBytes, maxDocumentContentBytes);
            CheckPageBoxes(doc, carried);
            CheckOpi(doc, carried, info, maxPageContentBytes, maxDocumentContentBytes);
            CheckTransferFunctions(doc, carried, info, maxPageContentBytes, maxDocumentContentBytes);
            CheckLzwAndEmbeddedFiles(catalog, doc, carried, info, maxPageContentBytes, maxDocumentContentBytes);
            CheckBleedBox(doc, carried);
            CheckSpotColourAlternates(doc, carried, info, maxPageContentBytes, maxDocumentContentBytes);
            CheckNonPrintableAnnotations(doc, carried);
            CheckJpxImages(doc, carried, info, maxPageContentBytes, maxDocumentContentBytes);
            foreach (var c in carried)
                info.Add(new PdfXViolation(c.Code, CarriedFromX4 + c.Message));

            CheckHalftoneTransferFunctions(doc, info, maxPageContentBytes, maxDocumentContentBytes);
            CheckOptionalContentConfigurations(doc, PdfXConformanceLevel.X6, info);
            ReportDeprecatedFeatures(doc, info);

            return new PdfXValidationResult(v, PdfXConformanceLevel.X6, info);
        }

        /// <summary>
        /// X02 at X-6 — the PDF/X-6 Application Notes §3.2: "If a document does not have a document-level
        /// output intent defined, there must be page-level output intent defined". A GTS_PDFX intent in the
        /// catalog's <c>/OutputIntents</c>, or else in every page's (ISO 32000-2 Table 31; a page's entry is
        /// not inherited). What the intent must hold is X11's at the earlier levels and ISO 15930-9 §6.6.2's
        /// here, which was not available, so it is not judged.
        /// </summary>
        private static void CheckX6OutputIntents(PdfDictionary catalog, PdfDocument doc, List<PdfXViolation> v)
        {
            var intents = new List<PdfDictionary>();
            CollectGtsPdfxIntents(catalog[PdfName.OutputIntents], doc, intents);
            if (intents.Count > 0) return;

            var without = new List<int>();
            for (int i = 0; i < doc.Pages.Count; i++)
            {
                intents.Clear();
                CollectGtsPdfxIntents(doc.Pages[i].Dictionary[PdfName.OutputIntents], doc, intents);
                if (intents.Count == 0) without.Add(i + 1);
            }
            if (without.Count == 0) return;

            string pages = string.Join(", ", without.Take(MaxNamedFindings).Select(p => p.ToString(CultureInfo.InvariantCulture))) +
                           (without.Count > MaxNamedFindings ? $" and {(without.Count - MaxNamedFindings).ToString(CultureInfo.InvariantCulture)} more" : "");
            v.Add(new PdfXViolation("X02",
                "The catalog has no /OutputIntent with /S /GTS_PDFX, and page(s) " + pages + " have none of their own; " +
                "PDF/X-6 requires one for the document or for every page (the PDF/X-6 Application Notes, §3.2)."));
        }

        /// <summary>
        /// X29 — ISO 15930-9 §6.1: "Any feature in ISO 32000-2 that is marked as deprecated should not
        /// appear in a PDF/X-6 file". A recommendation, so informational; one entry per construct, as
        /// <see cref="Pdf20DeprecationChecker"/> finds them (its own scan ceilings, and an advisory walk
        /// that reports no partial run).
        /// </summary>
        private static void ReportDeprecatedFeatures(PdfDocument doc, List<PdfXViolation> info)
        {
            foreach (var group in Pdf20DeprecationChecker.Check(doc).GroupBy(f => f.Construct))
            {
                var first = group.First();
                int count = group.Count();
                info.Add(new PdfXViolation("X29",
                    $"ISO 15930-9 §6.1: a feature ISO 32000-2 deprecates should not appear in a PDF/X-6 file — " +
                    $"{group.Key}, {count.ToString(CultureInfo.InvariantCulture)} occurrence(s); the first: {first.Detail}."));
            }
        }
    }
}
