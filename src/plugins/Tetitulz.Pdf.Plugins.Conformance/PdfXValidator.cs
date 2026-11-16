// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>PDF/X conformance level to validate against.</summary>
    public enum PdfXConformanceLevel
    {
        /// <summary>PDF/X-1a (ISO 15930-1 / -4): CMYK + spot colours only, all fonts embedded, no transparency.</summary>
        X1a = 1,
        /// <summary>PDF/X-3 (ISO 15930-3 / -6): Allows device-independent colour (RGB, Lab); fonts embedded; no transparency.</summary>
        X3 = 3,
        /// <summary>PDF/X-4 (ISO 15930-7): Like PDF/X-3 but transparency and layers allowed.</summary>
        X4 = 4,
        /// <summary>
        /// PDF/X-6 (ISO 15930-9), based on PDF 2.0 — <b>checked in part</b>. ISO 15930-9 was not
        /// available; what is checked is what its public preview (§6.1–6.2) and the PDF/X-6 Application
        /// Notes (2022) state, plus ISO 32000-2's own requirements, which §6.1 makes PDF/X-6's. The rest is
        /// reported on <see cref="PdfXValidationResult.Informational"/>, so a result at this level is
        /// always <see cref="PdfXValidationResult.IsPartial"/> (PdfXValidator.X6.cs).
        /// </summary>
        X6 = 6
    }

    /// <summary>
    /// Validates a PDF against the PDF/X (ISO 15930) print-exchange standard.
    ///
    /// <para>
    /// Checks performed:
    /// <list type="bullet">
    ///   <item>X01 — No encryption.</item>
    ///   <item>X02 — /OutputIntents array must contain an entry with S = "GTS_PDFX".</item>
    ///   <item>X03 — XMP metadata must declare the correct GTS_PDFXVersion.</item>
    ///   <item>X04 — All fonts must be fully embedded (/FontFile, /FontFile2, or /FontFile3).
    ///     There is no standard-14 exemption: ISO 15930 embeds every font, unlike ISO 32000-1.</item>
    ///   <item>X05 — Every page must have a /TrimBox or an /ArtBox, and not both (§6.2.4).</item>
    ///   <item>X10 — /Trapped must be /True or /False in the Info dictionary (§6.2.2). Absent, or
    ///         the /Unknown default, is a violation.</item>
    ///   <item>X11 — the GTS_PDFX output intent's own contents (§6.2.3): exactly one such intent,
    ///         an /OutputConditionIdentifier, and an embedded /DestOutputProfile (X-1a admits no
    ///         substitute; later parts accept a registered characterisation name).</item>
    ///   <item>X12 (X-4 only) — the file must declare PDF 1.6 or later.</item>
    ///   <item>X06 — No embedded OPI comments or /OPI dictionaries.</item>
    ///   <item>X07 — No /AA (additional-actions) on the catalog, a page, an annotation or a form
    ///         field; no document-level JavaScript; and no JavaScript, Launch, Sound, Movie,
    ///         ResetForm, ImportData or Rendition action anywhere, /Next chains included.</item>
    ///   <item>X08 — Colour. X-1a: no RGB or Lab (only CMYK, grey and spot). X-3 and X-4: a device
    ///     colour space only where the output intent's profile is in it (<see cref="ColourPolicy"/>).
    ///     Checked in named <c>/ColorSpace</c> resources, image XObjects, inline images, shadings,
    ///     spot alternates, transparency blending spaces, and the colour operators of every content
    ///     stream, following form XObjects.</item>
    ///   <item>X09 (X-1a / X-3) — No transparency: groups and soft masks on XObjects, and
    ///     /SMask, /ca, /CA or a non-/Normal /BM in an /ExtGState.</item>
    ///   <item>X14–X19 — the core rules every level shares (PdfXValidator.CoreRules.cs): no transfer
    ///     curves, no LZW, no embedded files, a BleedBox enclosing the TrimBox, one alternate per
    ///     spot colour, no audio, video or 3-D annotations.</item>
    ///   <item>X25 (X-4) — no page-level output intent: "PDF/X-4 does not allow for page-level
    ///     output Intents, and only uses a document level output intent" (the PDF/X-6 Application
    ///     Notes, §3.2); page /OutputIntents are PDF 2.0's (ISO 32000-2 Table 31).</item>
    ///   <item>X24 — no annotation in the print area: outside the BleedBox, or the TrimBox/ArtBox
    ///     where there is none; a PrinterMark outside the TrimBox/ArtBox alone; a widget named by
    ///     its field; TrapNet and Popup not judged (the standards committee's Application Notes
    ///     §2.28 — PdfXValidator.CoreRules.cs).</item>
    ///   <item>X20–X23 (X-4 only) — a JPXDecode image's header (PdfXValidator.Jpx.cs): its colour
    ///     channels, its colour specifications, CIEJab, its bit depth — the conditions a published    ///     PDF/X preflight check names, read as PDF/A's A24, A25, A27 and A28 read them.</item>
    /// </list>
    /// </para>
    ///
    /// <b>X99 — a check that could not run.</b> Content-level checking is bounded
    /// (<see cref="DefaultMaxPageContentBytes"/>, <see cref="DefaultMaxDocumentContentBytes"/>) and
    /// a stream may fail to decode. When that happens the check is *skipped*, which reports no
    /// violation and is therefore indistinguishable from a clean result — so an X99 entry lands on
    /// <see cref="PdfXValidationResult.Informational"/> naming the page and the check. X99 never
    /// affects <see cref="PdfXValidationResult.IsConforming"/>: it says a check did not run, not
    /// that the document is wrong.
    /// </summary>
    public static partial class PdfXValidator
    {
        // ----------------------------------------------------------------
        //  Diagnostics
        // ----------------------------------------------------------------

        /// <summary>
        /// Reason the most recent validation had to tolerate a failure, or <c>null</c> when it
        /// ran cleanly. <c>[ThreadStatic]</c> per the repository diagnostics convention, so
        /// concurrent validations on a server host cannot read each other's messages.
        /// </summary>
        [ThreadStatic]
        private static string _lastValidationError;

        /// <summary>
        /// Why a check could not be completed on this thread, or <c>null</c>.
        ///
        /// <para>
        /// Read this after a clean-looking result: a check that could not run reports nothing,
        /// which is indistinguishable from a check that ran and found nothing. An undecodable XMP
        /// packet, for example, silently skips the whole GTS_PDFXVersion rule.
        /// </para>
        /// </summary>
        public static string LastValidationError => _lastValidationError;

        private static void Report(string operation, string detail, Exception ex = null)
        {
            _lastValidationError = ex == null
                ? "[" + operation + "] " + detail
                : "[" + operation + "] " + detail + " — " + ex.GetType().Name + ": " + ex.Message;
        }

        // ----------------------------------------------------------------
        //  Ceilings for content-level checks
        // ----------------------------------------------------------------

        /// <summary>
        /// Decoded content-stream bytes examined per page before the content-level checks are
        /// skipped for that page (8 MiB). Raise or lower it through the four-argument
        /// <see cref="Validate(Stream, PdfXConformanceLevel, int, long)"/> overload.
        ///
        /// <para>The ceiling reaches the decoder rather than being checked after it: each content
        /// stream decodes through <c>PdfStream.TryGetDecodedData</c> with whatever this ceiling and
        /// the document's leave, so an unfiltered stream over it is refused without being touched and
        /// a filtered one stops inflating at the limit instead of at Core's 512 MB.</para>
        /// </summary>
        public const int DefaultMaxPageContentBytes = 8 * 1024 * 1024;

        /// <summary>
        /// Decoded content-stream bytes examined across the whole document before content-level
        /// checking stops (128 MiB). A per-page cap alone does not bound a 50 000-page file.
        /// </summary>
        public const long DefaultMaxDocumentContentBytes = 128L * 1024 * 1024;

        // ----------------------------------------------------------------
        //  Public API
        // ----------------------------------------------------------------

        /// <summary>Validates a PDF file against the specified PDF/X level.</summary>
        public static PdfXValidationResult Validate(string pdfPath,
            PdfXConformanceLevel level = PdfXConformanceLevel.X1a)
            => Validate(pdfPath, level, DefaultMaxPageContentBytes, DefaultMaxDocumentContentBytes);

        /// <summary>
        /// Validates a PDF file against the specified PDF/X level, with explicit content-scan
        /// ceilings.
        /// </summary>
        public static PdfXValidationResult Validate(string pdfPath, PdfXConformanceLevel level,
            int maxPageContentBytes, long maxDocumentContentBytes)
        {
            if (pdfPath == null) throw new ArgumentNullException(nameof(pdfPath));
            using var fs = File.OpenRead(pdfPath);
            return Validate(fs, level, maxPageContentBytes, maxDocumentContentBytes);
        }

        /// <summary>Validates a PDF stream against the specified PDF/X level.</summary>
        public static PdfXValidationResult Validate(Stream input,
            PdfXConformanceLevel level = PdfXConformanceLevel.X1a)
            => Validate(input, level, DefaultMaxPageContentBytes, DefaultMaxDocumentContentBytes);

        /// <summary>
        /// Validates a PDF stream against the specified PDF/X level, with explicit content-scan
        /// ceilings.
        /// </summary>
        public static PdfXValidationResult Validate(Stream input, PdfXConformanceLevel level,
            int maxPageContentBytes, long maxDocumentContentBytes)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            _lastValidationError = null;
            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            using (ScanReuse.Begin())
                return ValidateDocument(doc, level, maxPageContentBytes, maxDocumentContentBytes);
        }

        // ----------------------------------------------------------------
        //  Core validation
        // ----------------------------------------------------------------

        private static PdfXValidationResult ValidateDocument(PdfDocument doc,
            PdfXConformanceLevel level, int maxPageContentBytes, long maxDocumentContentBytes)
        {
            var v = new List<PdfXViolation>();
            var info = new List<PdfXViolation>();

            // X01 — no encryption.
            if (doc.IsEncrypted)
            {
                if (level == PdfXConformanceLevel.X6)
                    return EncryptedX6(level);
                v.Add(new PdfXViolation("X01", "PDF/X requires an unencrypted document."));
                return new PdfXValidationResult(v, level);
            }

            var catalog = doc.Catalog?.Dictionary;
            if (catalog == null)
            {
                v.Add(new PdfXViolation("X00", "No document catalog found."));
                return new PdfXValidationResult(v, level);
            }

            if (level == PdfXConformanceLevel.X6)
                return ValidateX6(doc, catalog, maxPageContentBytes, maxDocumentContentBytes);

            CheckOutputIntents(catalog, doc, level, v);
            CheckXmpMetadata(catalog, doc, level, v);
            CheckFontEmbedding(doc, v, info, maxPageContentBytes, maxDocumentContentBytes);
            CheckPageBoxes(doc, v);
            CheckTrapped(doc, v);
            CheckAdditionalActions(catalog, doc, v, info);
            CheckOpi(doc, v, info, maxPageContentBytes, maxDocumentContentBytes);
            // X08 runs at every level; only the forbidden set differs. X-3 and X-4 constrain the
            // device spaces against the output intent; they do not permit anything.
            CheckColorSpaces(doc, v, info,
                level == PdfXConformanceLevel.X1a
                    ? ColourPolicy.X1a()
                    : ColourPolicy.AgainstOutputIntent(
                          OutputIntentComponents(catalog, doc),
                          level == PdfXConformanceLevel.X3 ? "PDF/X-3" : "PDF/X-4"),
                maxPageContentBytes, maxDocumentContentBytes);
            // The core rules the PDF Association's "PDF/X in a Nutshell" lists for "all parts and
            // conformance levels" (GAPS.md §12b).
            CheckTransferFunctions(doc, v, info, maxPageContentBytes, maxDocumentContentBytes);
            CheckLzwAndEmbeddedFiles(catalog, doc, v, info, maxPageContentBytes, maxDocumentContentBytes);
            CheckBleedBox(doc, v);
            CheckSpotColourAlternates(doc, v, info, maxPageContentBytes, maxDocumentContentBytes);
            CheckNonPrintableAnnotations(doc, v);
            CheckAnnotationsInThePrintArea(doc, v);
            CheckHalftoneTransferFunctions(doc, info, maxPageContentBytes, maxDocumentContentBytes);
            if (level == PdfXConformanceLevel.X1a || level == PdfXConformanceLevel.X3)
                CheckTransparency(doc, v, info, maxPageContentBytes, maxDocumentContentBytes);
            if (level == PdfXConformanceLevel.X4)
            {
                CheckX4Version(doc, v);
                CheckOptionalContentDeclared(catalog, doc, v, info,
                    maxPageContentBytes, maxDocumentContentBytes);
                CheckOptionalContentConfigurations(doc, level, info);
                CheckJpxImages(doc, v, info, maxPageContentBytes, maxDocumentContentBytes);
            }

            string annotationsCut = ScanReuse.AnnotationsCutNotice("X13, X19, X24, the action checks and the content scans");
            if (annotationsCut != null) info.Add(new PdfXViolation("X99", annotationsCut));

            return new PdfXValidationResult(v, level, info);
        }

        // X02 — the catalog's /OutputIntents holds a GTS_PDFX intent. Document level only: a page's
        // /OutputIntents is PDF 2.0's (ISO 32000-2 Table 31), which PDF/X-1a, -3 and -4 predate, and
        // X-4 forbids one outright (X25, the PDF/X-6 Application Notes §3.2). A page's intent does
        // not stand in for the document's: an X-4 file carrying its printing condition only on a
        // page fails X02.
        // X11 — the contents of the intent that was found (ISO 15930-1 §6.2.3).
        private static void CheckOutputIntents(PdfDictionary catalog, PdfDocument doc,
            PdfXConformanceLevel level, List<PdfXViolation> v)
        {
            if (level == PdfXConformanceLevel.X4)
            {
                int pagesWithIntents = 0;
                for (int i = 0; i < doc.Pages.Count; i++)
                    if (doc.Pages[i].Dictionary[PdfName.OutputIntents] != null) pagesWithIntents++;
                if (pagesWithIntents > 0)
                    v.Add(new PdfXViolation("X25",
                        $"{pagesWithIntents} page(s) carry /OutputIntents. PDF/X-4 permits a document-level " +
                        "output intent only (the PDF/X-6 Application Notes, §3.2: \"PDF/X-4 does not allow for " +
                        "page-level output Intents\")."));
            }

            var intents = new List<PdfDictionary>();
            CollectGtsPdfxIntents(catalog[PdfName.OutputIntents], doc, intents);

            if (intents.Count == 0)
            {
                v.Add(new PdfXViolation("X02",
                    "No /OutputIntent entry with /S = /GTS_PDFX found in the document catalog."));
                return;
            }

            // More than one PDF/X output intent leaves the printing condition ambiguous, which is
            // the one thing the intent exists to pin down. Distinct dictionaries only: the same
            // object listed twice is one intent, not two.
            var distinct = new HashSet<PdfDictionary>(intents);
            if (distinct.Count > 1)
                v.Add(new PdfXViolation("X11",
                    $"PDF/X requires exactly one /GTS_PDFX output intent; found {distinct.Count}. " +
                    "The printing condition is ambiguous."));

            var intent = intents[0];

            // /OutputConditionIdentifier is a required entry of any output intent
            // (ISO 32000-1 §14.11.5, Table 365) — not merely a PDF/X rule.
            string conditionId = GetTextValue(doc, intent[PdfName.OutputConditionIdentifier]);
            if (string.IsNullOrWhiteSpace(conditionId))
                v.Add(new PdfXViolation("X11",
                    "The /GTS_PDFX output intent has no /OutputConditionIdentifier, which is a " +
                    "required entry — nothing names the printing condition."));

            // The embedded profile. X-1a admits no substitute. The later parts allow a registered
            // characterisation name to stand in, so there the profile is required only when the
            // identifier does not name one ("Custom" is the spec's placeholder for "it does not").
            bool hasProfile = intent[PdfName.DestOutputProfile] != null;
            bool identifiesRegisteredCondition =
                !string.IsNullOrWhiteSpace(conditionId) &&
                !string.Equals(conditionId, "Custom", StringComparison.Ordinal);

            // X-1a and X-4 require the embedded profile; X-3 accepts a registered characterisation
            // name in its place. X-4's half is the PDF Association's "PDF/X in a Nutshell": "PDF/X-4
            // inherits the rules of PDF/X-3 … with the requirement of always embedding the output
            // intent ICC profile" (GAPS.md §12b).
            bool profileMandatory = level == PdfXConformanceLevel.X1a
                                 || level == PdfXConformanceLevel.X4;

            if (!hasProfile && (profileMandatory || !identifiesRegisteredCondition))
            {
                v.Add(new PdfXViolation("X11",
                    profileMandatory
                        ? (level == PdfXConformanceLevel.X1a ? "PDF/X-1a" : "PDF/X-4") +
                          " requires the output intent to embed its ICC profile in " +
                          "/DestOutputProfile; a characterisation name is not a substitute."
                        : "The output intent has neither a /DestOutputProfile nor an " +
                          "/OutputConditionIdentifier naming a registered printing condition."));
            }
        }

        private static void CollectGtsPdfxIntents(PdfObject oiObj, PdfDocument doc,
            List<PdfDictionary> into)
        {
            var oiArray = oiObj as PdfArray ?? (doc.ResolveObject(oiObj) as PdfArray);
            if (oiArray == null) return;
            for (int i = 0; i < oiArray.Count; i++)
            {
                var entry = doc.ResolveObject(oiArray[i]) as PdfDictionary
                         ?? oiArray[i] as PdfDictionary;
                if (entry != null && ResolveName(doc, entry, PdfName.S) == "GTS_PDFX")
                    into.Add(entry);
            }
        }

        /// <summary>A text-string entry's value, following an indirect reference.</summary>
        private static string GetTextValue(PdfDocument doc, PdfObject obj)
            => (doc.ResolveObject(obj) as PdfString ?? obj as PdfString)?.GetText();

        // X03 — XMP declares GTS_PDFXVersion
        private static void CheckXmpMetadata(PdfDictionary catalog, PdfDocument doc,
            PdfXConformanceLevel level, List<PdfXViolation> v)
        {
            var metaRef = catalog[PdfName.Metadata];
            if (metaRef == null)
            {
                v.Add(new PdfXViolation("X03",
                    "PDF/X requires an XMP metadata stream (/Metadata in catalog) " +
                    "declaring the GTS_PDFXVersion."));
                return;
            }

            var metaStream = doc.ResolveObject(metaRef) as PdfStream;
            if (metaStream == null)
            {
                // DELIBERATELY a diagnostic and NOT an X03, unlike PdfAValidator (A07) and
                // PdfUaChecker (UA03), which report a violation for this same input.
                //
                // This side distinguishes "the check could not run" from "the document is
                // non-conforming", and MetadataNotAStream_SetsLastValidationError pins exactly that
                // reasoning. A malformed packet IS an X03 (MalformedXmp_ReportsX03AndSets...):
                // unreadable CONTENT is a defect, an unreadable CHECK is not. Raised as a
                // three-way drift by the duplication audit; it is a deliberate difference.
                Report("PdfXValidator.CheckXmpMetadata",
                    "the catalog's /Metadata does not resolve to a stream; the GTS_PDFXVersion check was skipped");
                return;
            }

            byte[] xmpBytes;
            try
            {
                xmpBytes = XmpStreamBytes.Read(metaStream);
            }
            catch (Exception ex)
            {
                Report("PdfXValidator.CheckXmpMetadata",
                    "the XMP metadata stream could not be decoded; the GTS_PDFXVersion check was skipped", ex);
                return;
            }

            if (xmpBytes == null || xmpBytes.Length == 0)
            {
                v.Add(new PdfXViolation("X03", "The XMP metadata stream is empty."));
                return;
            }

            string xmp;
            try
            {
                // BOM-aware: XMP permits UTF-16, and Encoding.UTF8.GetString does not throw on it —
                // it substitutes, so this catch never fired and the version search silently failed
                // on a conforming file. One decoder, in Core.Metadata.XmpPacket.
                xmp = Tetitulz.Pdf.Core.Metadata.XmpPacket.DecodeText(xmpBytes);
            }
            catch (Exception ex)
            {
                // Reported, never a bare `catch { return; }`: a decode failure silently skipping
                // the whole GTS_PDFXVersion rule would pass the file with no trace.
                Report("PdfXValidator.CheckXmpMetadata",
                    "the XMP metadata stream is not valid UTF-8; the GTS_PDFXVersion check was skipped", ex);
                return;
            }

            // Look for the appropriate GTS_PDFXVersion value.
            string expectedVersion = level switch
            {
                PdfXConformanceLevel.X1a => "PDF/X-1a:",
                PdfXConformanceLevel.X3  => "PDF/X-3:",
                PdfXConformanceLevel.X4  => "PDF/X-4",
                _                        => "PDF/X-"
            };

            // Read the actual GTS_PDFXVersion value rather than substring-matching
            // the whole XMP (which passes if the token appears anywhere — e.g. in a
            // title or comment). The expected prefix tolerates the year/profile
            // suffix (e.g. "PDF/X-4p", "PDF/X-1a:2001").
            string declared = ExtractPdfxVersion(xmp);
            if (declared == null)
            {
                v.Add(new PdfXViolation("X03",
                    "XMP metadata does not declare GTS_PDFXVersion."));
            }
            else if (!declared.StartsWith(expectedVersion, StringComparison.Ordinal))
            {
                v.Add(new PdfXViolation("X03",
                    $"GTS_PDFXVersion=\"{declared}\" does not match the expected '{expectedVersion}'."));
            }
        }

        // Returns the GTS_PDFXVersion value from the XMP packet (element text or
        // attribute value, regardless of prefix), or null if absent. Parsed with a
        // hardened reader (no DTD/external entities).
        private static string ExtractPdfxVersion(string xmp)
        {
            try
            {
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                };
                using var sr = new StringReader(xmp);
                using var reader = XmlReader.Create(sr, settings);
                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        // Attribute form: <rdf:Description pdfxid:GTS_PDFXVersion="PDF/X-4">
                        if (reader.HasAttributes)
                        {
                            for (int i = 0; i < reader.AttributeCount; i++)
                            {
                                reader.MoveToAttribute(i);
                                if (reader.LocalName == "GTS_PDFXVersion")
                                    return reader.Value;
                            }
                            reader.MoveToElement();
                        }
                        // Element form: <pdfxid:GTS_PDFXVersion>PDF/X-4</...>
                        if (reader.LocalName == "GTS_PDFXVersion")
                            return reader.ReadElementContentAsString();
                    }
                }
            }
            catch (Exception ex)
            {
                // Malformed XMP is treated as "no version declared", which produces an X03
                // violation — but the caller must be able to tell that apart from a packet that
                // genuinely omits the key, because the remediation differs.
                Report("PdfXValidator.ExtractPdfxVersion",
                    "the XMP packet could not be parsed; GTS_PDFXVersion is treated as absent", ex);
            }
            return null;
        }

        // X04 — all fonts embedded
        /// <remarks>
        /// Walks <b>every resource dictionary reachable from the page</b> via <see cref="ScanPage"/>,
        /// not just <c>page.Resources</c>, so a font used only inside a form XObject, a tiling
        /// pattern, a Type 3 glyph procedure or an annotation appearance stream — a logo, a stamp,
        /// a filled form field — is examined. <c>collectContent: false</c> because this
        /// check needs the dictionaries, never the operators.
        /// </remarks>
        private static void CheckFontEmbedding(PdfDocument doc, List<PdfXViolation> v,
            List<PdfXViolation> info, int maxPageContentBytes, long maxDocumentContentBytes)
        {
            var notEmbedded = new List<string>();
            var seenFonts = new HashSet<PdfDictionary>();
            var seenResources = new HashSet<PdfDictionary>();
            long documentBytes = 0;

            for (int p = 0; p < doc.Pages.Count; p++)
            {
                var scan = ScanPage(doc, doc.Pages[p], maxPageContentBytes,
                    ref documentBytes, maxDocumentContentBytes, collectContent: false, printElementsOnly: true);

                if (scan.Incomplete != null)
                    info.Add(new PdfXViolation("X99",
                        $"Page {p + 1}: the X04 font check did not run in full — {scan.Incomplete}."));

                foreach (var res in scan.Resources)
                {
                // A dictionary pages share names the same fonts on each: read once.
                if (!seenResources.Add(res)) continue;
                var fontDict = Resolve<PdfDictionary>(doc, res[PdfName.Font]);
                if (fontDict == null) continue;

                foreach (var kvp in fontDict)
                {
                    var fontObj = doc.ResolveObject(kvp.Value) as PdfDictionary
                               ?? kvp.Value as PdfDictionary;
                    if (fontObj == null) continue;

                    // One font object is normally referenced from several resource dictionaries;
                    // without this it would be reported once per reference.
                    if (!seenFonts.Add(fontObj)) continue;

                    // One traversal, in FontEmbeddingProbe. This site walked only descArr[0] —
                    // §9.7.1 gives a Type 0 font exactly one descendant, so that was latent rather
                    // than live, but it was not what the other two validators did. It also had no
                    // cycle guard, being iterative rather than recursive; the shared walk is
                    // recursive and carries both that and the depth cap.
                    //
                    // ISO 15930 requires *every* font to be embedded and grants the standard 14 no
                    // exemption — unlike ISO 32000-1 §9.6.2.2, which is where the temptation to
                    // skip Type1 comes from. Exempting it accepted `/BaseFont /Helvetica` with no
                    // descriptor, the most common way a real file fails PDF/X. The shared walk
                    // exempts only Type 3, whose glyphs are content streams.
                    //
                    // The resource key is the fallback label: where a font names itself neither by
                    // /BaseFont nor /Name, "/F1" tells a reader more than a constant does.
                    FontEmbeddingProbe.ReportUnembedded(
                        fontObj, doc, name => notEmbedded.Add(name), fallbackLabel: kvp.Key.Value);
                }
                }
            }

            if (notEmbedded.Count > 0)
                v.Add(new PdfXViolation("X04",
                    $"PDF/X requires all fonts to be embedded. Not embedded: " +
                    string.Join(", ", notEmbedded) + "."));
        }

        // X13 (X-4 only) — optional content must be declared in the catalog
        /// <summary>
        /// Every optional-content group an <c>/OC</c> entry names must be reachable from the
        /// catalog's <c>/OCProperties</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>⚠ Unverified reading of ISO 15930-7</b>, implemented on an accept-the-risk
        /// decision because the standard text was not available, and not settled by the
        /// public sources the colour rules rest on (GAPS.md §12b).</para>
        /// <para>The reading: PDF/X-4 is the part that <i>permits</i> optional content, and what a
        /// print exchange needs from it is that the visibility of every group is determined by the
        /// file rather than by the consumer's defaults. ISO 32000-1 §8.11.4.2 already requires an
        /// OCG in use to appear in <c>/OCProperties /OCGs</c>; this reports the case where it does
        /// not, which is the state in which a RIP and a viewer can disagree about what prints.</para>
        /// <para><b>Checks <c>/OC</c> on XObjects, on annotations, and on the <c>BDC /OC</c>
        /// marked-content form</b> (§14.6.2's operator, §8.11.4.2's note that its property operand
        /// is the OCG/OCMD dictionary itself), and that <c>/OCProperties</c> has the <c>/OCGs</c> and
        /// <c>/D</c> ISO 32000 itself requires. It does <b>not</b> judge what <c>/D</c> or a
        /// <c>/Configs</c> entry holds: that is ISO 15930-7 6.24's, which was not available, and X27
        /// reports it without judging (PdfXValidator.Unconfirmed.cs).</para>
        /// <para><b>The BDC check's fail-safe direction is the opposite of the other two's.</b> An
        /// <c>/OC</c> entry on an XObject or annotation is a fully-parsed dictionary value — if it
        /// fails to resolve to a declared group, that is a definite fact worth reporting. A BDC
        /// property operand is a name resolved through content-stream bytes this check parses for
        /// the first time; failing to resolve it (no <c>/Properties</c> entry, no
        /// <c>/Properties</c> dictionary, a name the stream never defines) is the kind of
        /// imperfect-parse uncertainty the stopping rule below warns against turning into a
        /// violation, so an unresolvable property is skipped rather than reported. Only a property
        /// that <i>does</i> resolve to a dictionary — and is then not reachable from
        /// <c>/OCProperties /OCGs</c> — is a finding.</para>
        /// <para><b>X-4 permits transparency</b>; the one constraint on it checked is its blending
        /// colour space, under X08's device-space rule (<see cref="ColourSpaceSites"/>).</para>
        /// </remarks>
        private static void CheckOptionalContentDeclared(PdfDictionary catalog, PdfDocument doc,
            List<PdfXViolation> v, List<PdfXViolation> info,
            int maxPageContentBytes, long maxDocumentContentBytes)
        {
            var declared = new HashSet<PdfDictionary>();
            var ocProperties = Resolve<PdfDictionary>(doc, catalog[PdfName.OCProperties]);
            var ocgs = Resolve<PdfArray>(doc, ocProperties?[PdfName.OCGs]);
            // ISO 32000-1 Table 100 (ISO 32000-2 Table 98): both entries are required. Without /D no
            // configuration says which groups are on, which is the state a RIP and a viewer disagree in.
            if (ocProperties != null && (ocgs == null || Resolve<PdfDictionary>(doc, ocProperties[PdfName.D]) == null))
                v.Add(new PdfXViolation("X13",
                    "The catalog's /OCProperties lacks " +
                    (ocgs == null ? "the /OCGs array" : "the /D default configuration") +
                    " ISO 32000 requires (ISO 32000-1 Table 100, ISO 32000-2 Table 98)."));
            if (ocgs != null)
                for (int i = 0; i < ocgs.Count; i++)
                {
                    var g = Resolve<PdfDictionary>(doc, ocgs[i]);
                    if (g != null) declared.Add(g);
                }

            long documentBytes = 0;
            var seenOc = new HashSet<PdfDictionary>();
            var seenContent = new HashSet<byte[]>();

            for (int p = 0; p < doc.Pages.Count; p++)
            {
                // collectContent:true — the BDC /OC walk below needs the decoded content streams,
                // which the XObject/annotation checks alone never did.
                var scan = ScanPage(doc, doc.Pages[p], maxPageContentBytes,
                    ref documentBytes, maxDocumentContentBytes, collectContent: true);

                if (scan.Incomplete != null)
                    info.Add(new PdfXViolation("X99",
                        $"Page {p + 1}: the X13 optional-content check did not run in full — {scan.Incomplete}."));

                foreach (var res in scan.Resources)
                {
                    if (!seenOc.Add(res)) continue;   // a dictionary pages share is judged once
                    var xoDict = Resolve<PdfDictionary>(doc, res[PdfName.XObject]);
                    if (xoDict == null) continue;

                    foreach (var kvp in xoDict)
                    {
                        var xo = Resolve<PdfDictionary>(doc, kvp.Value);
                        if (xo?[PdfName.OC] == null) continue;

                        if (!IsDeclaredOc(doc, xo[PdfName.OC], declared))
                        {
                            v.Add(new PdfXViolation("X13",
                                $"XObject /{kvp.Key.Value} on page {p + 1} names optional content that is " +
                                "not reachable from the catalog's /OCProperties /OCGs, so its visibility " +
                                "is not determined by the file."));
                            return;
                        }
                    }
                }

                var annots = Resolve<PdfArray>(doc, doc.Pages[p].Dictionary[PdfName.Annots]);
                if (ScanReuse.AdmitAnnotations(annots))
                {
                    for (int a = 0; a < annots.Count; a++)
                    {
                        var annot = Resolve<PdfDictionary>(doc, annots[a]);
                        if (annot?[PdfName.OC] == null) continue;

                        if (!IsDeclaredOc(doc, annot[PdfName.OC], declared))
                        {
                            v.Add(new PdfXViolation("X13",
                                $"An annotation on page {p + 1} names optional content that is not " +
                                "reachable from the catalog's /OCProperties /OCGs."));
                            return;
                        }
                    }
                }

                // BDC /OC uses no `continue`-out-early guard like the annotation loop above — a
                // plain `for`, since that guard's shape would skip every check after it on a page
                // with none of ITS subject, and this block would never run on a page with no
                // annotations to skip past.
                for (int s = 0; s < scan.ContentStreams.Count; s++)
                {
                    if (scan.ContentStreams[s] != null && !seenContent.Add(scan.ContentStreams[s])) continue;
                    if (FindUndeclaredMarkedContentOc(doc, scan.ContentStreams[s],
                            scan.ContentStreamResources[s], declared))
                    {
                        v.Add(new PdfXViolation("X13",
                            $"Page {p + 1}: a BDC /OC marked-content block names optional content " +
                            "that is not reachable from the catalog's /OCProperties /OCGs."));
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// True when some <c>BDC /OC</c> block in <paramref name="content"/> names optional content
        /// that is not reachable from the catalog's <c>/OCProperties /OCGs</c>. See the fail-safe
        /// remark on <see cref="CheckOptionalContentDeclared"/>: a property operand this cannot
        /// resolve is skipped, not reported.
        /// </summary>
        private static bool FindUndeclaredMarkedContentOc(PdfDocument doc, byte[] content,
            PdfDictionary resources, HashSet<PdfDictionary> declared)
        {
            foreach (var op in ScanReuse.Parse(content))
            {
                if (op.Operator != PdfOperators.BeginMarkedContentProps || op.Operands.Length < 2)
                    continue;

                // The tag is the operator's first operand (e.g. /OC, /Span, /Artifact); only /OC
                // carries the §8.11.4.2 meaning that its property operand is an OCG/OCMD itself.
                if (!(op.Operands[0] is PdfName tag) || tag.Value != PdfName.OC.Value) continue;

                var ocDict = MarkedContentInfo.ResolvePropertyDictionary(op.Operands[1], resources, doc);
                if (ocDict == null) continue;   // unresolvable — fail-safe means skip, not report

                if (!IsDeclaredOc(doc, ocDict, declared)) return true;
            }
            return false;
        }

        /// <summary>
        /// True when an <c>/OC</c> value resolves to a declared group — directly, or through an
        /// <c>/OCMD</c>'s <c>/OCGs</c> (which may be one group or an array).
        /// </summary>
        /// <remarks>Fail-safe: an <c>/OC</c> that resolves to nothing answers <c>false</c>, so it
        /// is reported rather than assumed harmless.</remarks>
        private static bool IsDeclaredOc(PdfDocument doc, PdfObject oc, HashSet<PdfDictionary> declared)
        {
            var dict = Resolve<PdfDictionary>(doc, oc);
            if (dict == null) return false;

            if (declared.Contains(dict)) return true;

            // An /OCMD groups one or more OCGs; every one it names must be declared.
            PdfObject inner = dict[PdfName.OCGs];
            if (inner == null) return false;

            var single = Resolve<PdfDictionary>(doc, inner);
            if (single != null) return declared.Contains(single);

            var arr = Resolve<PdfArray>(doc, inner);
            if (arr == null || arr.Count == 0) return false;

            for (int i = 0; i < arr.Count; i++)
            {
                var g = Resolve<PdfDictionary>(doc, arr[i]);
                if (g == null || !declared.Contains(g)) return false;
            }
            return true;
        }

        // X12 — PDF/X-4 requires PDF 1.6 or later (ISO 15930-7)
        /// <remarks>
        /// Checkable against the file header and needing no interpretation. X-4's embedded profile
        /// (X11) and its blending space (X08) come from the public sources GAPS.md §12b names; its
        /// `/OCProperties` constraints are not implemented, because neither source states them.
        /// </remarks>
        private static void CheckX4Version(PdfDocument doc, List<PdfXViolation> v)
        {
            var minimum = new PdfVersion(1, 6);
            if (doc.Version < minimum)
                v.Add(new PdfXViolation("X12",
                    $"PDF/X-4 requires PDF 1.6 or later; this file declares {doc.Version}."));
        }

        // X10 — /Trapped, ISO 15930-1 §6.2.2
        /// <remarks>
        /// The standard requires the Info dictionary to state the trapping condition explicitly:
        /// <c>/True</c> or <c>/False</c>. <c>/Unknown</c> is the PDF default and is precisely what
        /// PDF/X refuses — "nobody recorded whether this file is trapped" is not an answer a print
        /// exchange can accept — and an absent key means the same thing.
        /// </remarks>
        private static void CheckTrapped(PdfDocument doc, List<PdfXViolation> v)
        {
            var info = Resolve<PdfDictionary>(doc, doc.Trailer?[PdfName.Info]);
            var trapped = ResolveName(doc, info, PdfName.Trapped);

            if (trapped == null)
            {
                v.Add(new PdfXViolation("X10",
                    "PDF/X requires /Trapped in the document information dictionary, with the " +
                    "value /True or /False. It is absent."));
                return;
            }

            if (trapped != "True" && trapped != "False")
                v.Add(new PdfXViolation("X10",
                    $"PDF/X requires /Trapped to be /True or /False; found /{trapped}. " +
                    "/Unknown is the PDF default and is not an acceptable declaration."));
        }

        // X05 — exactly one of /TrimBox or /ArtBox on every page (ISO 15930-1 §6.2.4)
        private static void CheckPageBoxes(PdfDocument doc, List<PdfXViolation> v)
        {
            var missing = new List<int>();
            var both    = new List<int>();
            for (int i = 0; i < doc.Pages.Count; i++)
            {
                var pd = doc.Pages[i].Dictionary;
                bool hasTrim = pd[PdfName.TrimBox] != null;
                bool hasArt  = pd[PdfName.ArtBox]  != null;
                if (!hasTrim && !hasArt) missing.Add(i + 1);
                else if (hasTrim && hasArt) both.Add(i + 1);
            }
            if (missing.Count > 0)
                v.Add(new PdfXViolation("X05",
                    $"PDF/X requires a /TrimBox or /ArtBox on every page. " +
                    $"Missing on page(s): {string.Join(", ", missing)}."));

            // The two boxes carry different intent — trimmed page vs. meaningful content — and a
            // RIP cannot know which one the producer meant when both are present, so §6.2.4 makes
            // it "one or the other", not "at least one". The two cases stay separate violations
            // because the remediation differs: synthesise a box, versus delete one.
            if (both.Count > 0)
                v.Add(new PdfXViolation("X05",
                    $"PDF/X allows a /TrimBox or an /ArtBox, but not both. " +
                    $"Both present on page(s): {string.Join(", ", both)}."));
        }

        /// <summary>
        /// Action types PDF/X excludes: they either run code, fetch external data or depend on a
        /// viewer the printing workflow does not have. Rendering a page must not execute anything.
        /// </summary>
        private static readonly HashSet<string> ForbiddenActionTypes = new HashSet<string>(
            StringComparer.Ordinal)
        {
            "JavaScript", "Launch", "Sound", "Movie", "ResetForm", "ImportData", "Rendition",
        };

        // X07 — no /AA (additional actions), no forbidden action types, no document-level JavaScript
        /// <remarks>
        /// <para>Covers every place an action lives: an <c>/AA</c> on the catalog, a page, an
        /// <b>annotation</b> or a form field; a <c>/Launch</c> or <c>/JavaScript</c> action on a
        /// link; and a catalog <c>/Names /JavaScript</c> name tree, the ordinary place
        /// document-level script lives. The AcroForm field tree is walked itself: the
        /// <c>/Annots</c> loop reaches a field only when the field dictionary <i>is</i> its widget,
        /// and a field whose widgets are separate <c>/Kids</c>, or that has none, is on no page —
        /// while <c>/K</c> keystroke and <c>/F</c> format scripts live on the field.</para>
        /// <para><b>The two halves read shared sources.</b> The <c>/AA</c> owners are
        /// <see cref="ActionOwnerWalk"/>'s, which <see cref="PdfAValidator"/>'s A04 and A23 read
        /// too; every node of the field tree counts, terminal or not, because <c>/AA</c> is
        /// forbidden outright. The action types are <see cref="PdfActionDispatcher"/>'s list, so a
        /// <c>/Launch</c> second on a <c>/Next</c> chain, which the same trigger runs, and an
        /// outline item's script are looked at.</para>
        /// <para>A script inside a forbidden <c>/AA</c> is two entries, one for the <c>/AA</c> and one
        /// for its type: two rules, as in <see cref="PdfAValidator"/>. A merged field and widget is
        /// one dictionary reached twice and its <c>/AA</c> one entry. Entries with the same message
        /// are counted in one (<see cref="ActionFindings"/>).</para>
        /// </remarks>
        private static void CheckAdditionalActions(PdfDictionary catalog, PdfDocument doc,
            List<PdfXViolation> v, List<PdfXViolation> info)
        {
            var findings = new ActionFindings();

            var walk = ActionOwnerWalk.Walk(doc, obj => Resolve<PdfObject>(doc, obj));

            // A walk that stopped examined some fields and not others, which is exactly the "check
            // did not run in full" state X99 exists for: reporting nothing would be
            // indistinguishable from a form with no actions in it. Same rule as the content
            // ceilings, and the same reason — a false pass reaches a RIP.
            if (walk.FieldWalk.BudgetExhausted || walk.FieldWalk.DepthExceeded)
                info.Add(new PdfXViolation("X99",
                    "The X07 action check did not run in full — the AcroForm field walk stopped " +
                    (walk.FieldWalk.BudgetExhausted
                        ? $"after {AcroFormFieldWalk.MaxFieldNodes} nodes"
                        : $"at its depth cap of {AcroFormFieldWalk.MaxFieldDepth}") +
                    ", so fields beyond that point were not examined for /AA."));

            var reportedAa = new HashSet<PdfDictionary>(PdfReferenceEquality.Instance);
            foreach (var owner in walk.Owners)
            {
                if (owner.Dictionary[PdfName.AA] == null) continue;
                switch (owner.Kind)
                {
                    case ActionOwnerKind.Catalog:
                        findings.Add("X07", "PDF/X does not allow /AA (additional-actions) on the document catalog",
                            ActionFindings.Where(owner));
                        break;
                    case ActionOwnerKind.Page:
                        findings.Add("X07", "PDF/X does not allow /AA (additional-actions) on pages",
                            ActionFindings.Where(owner));
                        break;
                    default:
                        if (reportedAa.Add(owner.Dictionary))
                            findings.Add("X07", "PDF/X does not allow /AA (additional-actions) on annotations or form fields",
                                ActionFindings.Where(owner));
                        break;
                }
            }

            // Document-level JavaScript lives in the /Names name tree, not in /AA. The key is the
            // rule, so an empty tree is reported too; its scripts are reported by type below.
            var names = Resolve<PdfDictionary>(doc, catalog[PdfName.Names]);
            if (names != null && names[PdfName.JavaScript] != null)
                findings.Add("X07", "PDF/X does not allow document-level JavaScript (/Names /JavaScript)",
                    "the document catalog");

            var actions = PdfActionDispatcher.ExtractActions(doc);
            string cut = PdfActionDispatcher.LastChainDiagnostic;
            if (cut != null)
                info.Add(new PdfXViolation("X99", "The X07 action-type check did not run in full — " + cut));

            foreach (var action in actions)
            {
                string type = ResolveName(doc, action.Dictionary, PdfName.S);
                if (type != null && ForbiddenActionTypes.Contains(type))
                    findings.Add("X07", $"PDF/X does not allow a /{type} action", ActionFindings.Where(action));
            }

            findings.Emit((code, message) => v.Add(new PdfXViolation(code, message)));
        }

        // X06 — no /OPI
        /// <remarks>
        /// Walks every resource dictionary reachable from the page, like X04, so an <c>/OPI</c> on
        /// an XObject inside a form, a pattern or an annotation appearance is reported — an
        /// OPI-bearing placed image is far more likely to sit inside a form than directly on the
        /// page.
        /// </remarks>
        private static void CheckOpi(PdfDocument doc, List<PdfXViolation> v,
            List<PdfXViolation> info, int maxPageContentBytes, long maxDocumentContentBytes)
        {
            long documentBytes = 0;
            var seen = new HashSet<PdfDictionary>();   // a dictionary pages share is judged once

            for (int p = 0; p < doc.Pages.Count; p++)
            {
                var scan = ScanPage(doc, doc.Pages[p], maxPageContentBytes,
                    ref documentBytes, maxDocumentContentBytes, collectContent: false, printElementsOnly: true);

                if (scan.Incomplete != null)
                    info.Add(new PdfXViolation("X99",
                        $"Page {p + 1}: the X06 OPI check did not run in full — {scan.Incomplete}."));

                foreach (var res in scan.Resources)
                {
                    if (!seen.Add(res)) continue;
                    var xoDict = Resolve<PdfDictionary>(doc, res[PdfName.XObject]);
                    if (xoDict == null) continue;

                    foreach (var kvp in xoDict)
                    {
                        var xo = Resolve<PdfDictionary>(doc, kvp.Value);
                        if (xo != null && xo[PdfName.OPI] != null)
                        {
                            v.Add(new PdfXViolation("X06",
                                "PDF/X does not allow /OPI (Open Prepress Interface) dictionaries."));
                            return;
                        }
                    }
                }
            }
        }

        // ----------------------------------------------------------------
        //  X08 (X-1a only) — no RGB or Lab colour anywhere
        // ----------------------------------------------------------------

        // The colour rule is the one PDF/X check a dictionary walk cannot answer. A page whose
        // /Resources name no colour space at all can still fill in RGB with `1 0 0 rg`, and an RGB
        // JPEG carries its space on the image XObject rather than in /ColorSpace. Both were
        // invisible to the resource-dictionary-only check this replaces, and both are ordinary
        // output from ordinary producers — so a genuinely non-conforming file validated clean.
        private static void CheckColorSpaces(PdfDocument doc, List<PdfXViolation> v,
            List<PdfXViolation> info, ColourPolicy policy,
            int maxPageContentBytes, long maxDocumentContentBytes)
        {
            long documentBytes = 0;
            var seen = new SeenAcrossPages();

            for (int i = 0; i < doc.Pages.Count; i++)
            {
                int pageNumber = i + 1;
                var scan = ScanPage(doc, doc.Pages[i], maxPageContentBytes,
                    ref documentBytes, maxDocumentContentBytes, printElementsOnly: true);

                if (scan.Incomplete != null)
                    info.Add(new PdfXViolation("X99",
                        $"Page {pageNumber}: the X08 colour check did not run in full — {scan.Incomplete}."));

                string found = FindForbiddenColour(doc, doc.Pages[i], scan, policy, seen);
                if (found != null)
                {
                    v.Add(new PdfXViolation("X08",
                        $"{policy.Because}; found {found} on page {pageNumber}."));
                    return;
                }
            }
        }

        /// <summary>
        /// What a check walking every page's scan has judged already: a resource dictionary or a
        /// content stream pages share answers the same on each, and judged on every page it was pages ×
        /// entries — a thousand pages over one forty-thousand-entry dictionary took PDF/X 25 s.
        /// </summary>
        private sealed class SeenAcrossPages
        {
            internal readonly HashSet<PdfDictionary> Resources = new HashSet<PdfDictionary>();
            internal readonly HashSet<byte[]> Contents = new HashSet<byte[]>();
        }

        // Returns a description of the first forbidden colour usage found, or null.
        private static string FindForbiddenColour(PdfDocument doc, PdfPage page, PageScan scan,
                                                  ColourPolicy policy, SeenAcrossPages seen)
        {
            foreach (var site in ColourSpaceSites(doc, page, scan, seen.Resources))
            {
                string bad = ClassifyColorSpace(doc, site.Space, 0, policy);
                if (bad != null) return site.Describe(bad);
            }

            foreach (byte[] content in scan.ContentStreams)
            {
                if (content != null && !seen.Contents.Add(content)) continue;
                string bad = FindForbiddenColourInContent(doc, content, policy);
                if (bad != null) return bad;
            }

            return null;
        }

        /// <summary>
        /// One place a colour space is written in a dictionary, and how a finding there is worded.
        /// </summary>
        private readonly struct ColourSpaceSite
        {
            internal readonly PdfObject Space;
            private readonly string _before;
            private readonly string _after;

            internal ColourSpaceSite(PdfObject space, string before, string after)
            {
                Space = space;
                _before = before;
                _after = after;
            }

            /// <summary>The site named around what was found there. Concatenated, not formatted:
            /// a resource name may hold a brace.</summary>
            internal string Describe(string found) => _before + found + _after;
        }

        /// <summary>
        /// Every colour space a page's scan reaches in a dictionary, rather than in a content
        /// stream: named resources, images, shadings, and transparency groups' blending spaces.
        /// X08 classifies each against the policy; X18 collects the spot colours among them.
        /// </summary>
        /// <remarks>
        /// <para><b>A shading</b> carries its own <c>/ColorSpace</c> (§8.7.4.3 Table 78) and is
        /// reached either as a <c>/Shading</c> resource or through a <c>/PatternType 2</c> pattern.
        /// Neither was looked at once, and a DeviceRGB gradient validated PDF/X-1a clean. A tiling
        /// pattern is not a site: it paints with ordinary operators from its own
        /// <c>/Resources</c>, which <see cref="ScanPage"/> already collects.</para>
        /// <para><b>A blending space</b> (a <c>/Group</c>'s <c>/CS</c>) is a site because published preflight profiles
        /// check it under the same device-space rule ("… used for transparency blend color space
        /// but OutputIntent not …"). The groups are the page's, a form XObject's, an annotation
        /// appearance's and a soft mask's <c>/G</c>. Those last three sites are read after the
        /// resources, so a file that offends in both places reports what it reported before.</para>
        /// </remarks>
        private static IEnumerable<ColourSpaceSite> ColourSpaceSites(PdfDocument doc, PdfPage page,
                                                                     PageScan scan, HashSet<PdfDictionary> seen)
        {
            // Only the dictionaries no earlier page's scan held: the sites in one are the same wherever it is reached.
            var fresh = new List<PdfDictionary>();
            foreach (var res in scan.Resources)
                if (seen.Add(res)) fresh.Add(res);

            foreach (var res in fresh)
            {
                var csDict = Resolve<PdfDictionary>(doc, res[PdfName.ColorSpace]);
                if (csDict != null)
                    foreach (var kvp in csDict)
                        yield return new ColourSpaceSite(kvp.Value, "the ",
                            $" colour space (resource /{kvp.Key.Value})");

                // Image XObjects carry their space on the image, not in /ColorSpace.
                var xoDict = Resolve<PdfDictionary>(doc, res[PdfName.XObject]);
                if (xoDict != null)
                {
                    foreach (var kvp in xoDict)
                    {
                        // PdfStream derives from PdfDictionary, so the stream *is* its dictionary.
                        var xo = Resolve<PdfStream>(doc, kvp.Value);
                        if (xo == null) continue;
                        string subtype = ResolveName(doc, xo, PdfName.Subtype);
                        if (subtype == "Image")
                            yield return new ColourSpaceSite(xo[PdfName.ColorSpace], "a ",
                                $" image (XObject /{kvp.Key.Value})");
                        else if (subtype == "Form" && GroupSpace(doc, xo) is PdfObject cs)
                            yield return new ColourSpaceSite(cs, "a ",
                                $" transparency blending space (/Group /CS of form XObject /{kvp.Key.Value})");
                    }
                }

                var shDict = Resolve<PdfDictionary>(doc, res[PdfName.Shading]);
                if (shDict != null)
                    foreach (var kvp in shDict)
                        yield return new ColourSpaceSite(
                            Resolve<PdfDictionary>(doc, kvp.Value)?[PdfName.ColorSpace], "a ",
                            $" shading (resource /{kvp.Key.Value})");

                var patDict = Resolve<PdfDictionary>(doc, res[PdfName.Pattern]);
                if (patDict != null)
                {
                    foreach (var kvp in patDict)
                    {
                        var pat = Resolve<PdfDictionary>(doc, kvp.Value);
                        if (pat == null) continue;
                        if ((Resolve<PdfNumber>(doc, pat[PdfName.PatternType]))?.IntValue != 2) continue;
                        yield return new ColourSpaceSite(
                            Resolve<PdfDictionary>(doc, pat[PdfName.Shading])?[PdfName.ColorSpace], "a ",
                            $" shading pattern (resource /{kvp.Key.Value})");
                    }
                }
            }

            foreach (var res in fresh)
            {
                var egsDict = Resolve<PdfDictionary>(doc, res[PdfName.ExtGState]);
                if (egsDict == null) continue;
                foreach (var kvp in egsDict)
                {
                    var smask = Resolve<PdfDictionary>(doc, Resolve<PdfDictionary>(doc, kvp.Value)?[PdfName.SMask]);
                    var group = Resolve<PdfStream>(doc, smask?[PdfName.G]);
                    if (GroupSpace(doc, group) is PdfObject cs)
                        yield return new ColourSpaceSite(cs, "a ",
                            $" transparency blending space (the soft mask's group in /ExtGState /{kvp.Key.Value})");
                }
            }

            if (GroupSpace(doc, page.Dictionary) is PdfObject pageSpace)
                yield return new ColourSpaceSite(pageSpace, "a ",
                    " transparency blending space (the page's /Group /CS)");

            foreach (var ap in scan.AppearanceStreams)
                if (GroupSpace(doc, ap) is PdfObject apSpace)
                    yield return new ColourSpaceSite(apSpace, "a ",
                        " transparency blending space (/Group /CS of an annotation appearance)");
        }

        /// <summary>The <c>/CS</c> of <paramref name="owner"/>'s <c>/Group</c>, or null.</summary>
        private static PdfObject GroupSpace(PdfDocument doc, PdfDictionary owner)
            => Resolve<PdfDictionary>(doc, owner?[PdfName.Group])?[PdfName.CS];

        // Takes no PageScan: resolving a `cs` operand that names a /ColorSpace entry would be a
        // second path to a verdict the caller already reaches, so nothing here reads resources.
        private static string FindForbiddenColourInContent(PdfDocument doc, byte[] content, ColourPolicy policy)
        {
            foreach (var op in ScanReuse.Parse(content))
            {
                switch (op.Operator)
                {
                    // The three operators that set a device colour directly. Each was once answered
                    // here without the policy: `rg` was reported even against an RGB output intent,
                    // where X-3 and X-4 allow it.
                    case PdfOperators.SetRgbFill:
                    case PdfOperators.SetRgbStroke:
                        if (policy.IsForbidden(PdfColorSpaceNames.DeviceRGB))
                            return "DeviceRGB set by the " + op.Operator + " operator";
                        break;
                    case PdfOperators.SetCmykFill:
                    case PdfOperators.SetCmykStroke:
                        if (policy.IsForbidden(PdfColorSpaceNames.DeviceCMYK))
                            return "DeviceCMYK set by the " + op.Operator + " operator";
                        break;
                    case PdfOperators.SetGrayFill:
                    case PdfOperators.SetGrayStroke:
                        if (policy.IsForbidden(PdfColorSpaceNames.DeviceGray))
                            return "DeviceGray set by the " + op.Operator + " operator";
                        break;

                    // `cs` / `CS` name either a device space directly or a /ColorSpace resource.
                    case PdfOperators.SetColorSpaceFill:
                    case PdfOperators.SetColorSpaceStroke:
                    {
                        // Only the *device* form needs handling here. When `cs` names a resource
                        // instead, that resource is by definition an entry in a reachable
                        // /ColorSpace dictionary, which the caller has already classified — X08
                        // is about a forbidden space being present, not about it being selected.
                        // Resolving the name again would be a second path to the same verdict,
                        // and a test written against it passes with this branch deleted.
                        var name = op.Operands.Length > 0 ? op.Operands[0] as PdfName : null;
                        if (name != null && policy.IsForbidden(name.Value))
                            return $"the {name.Value} colour space selected by {op.Operator}";
                        break;
                    }

                    // Inline image: the parser hands back a PdfStream whose dictionary holds the
                    // abbreviated keys, so /CS and /ColorSpace both have to be tried.
                    case PdfOperators.BeginInlineImage:
                    {
                        var img = op.Operands.Length > 0 ? op.Operands[0] as PdfStream : null;
                        var csObj = img?[PdfName.CS] ?? img?[PdfName.ColorSpace];
                        string bad = ClassifyColorSpace(doc, csObj, 0, policy);
                        if (bad != null) return $"a {bad} inline image";
                        break;
                    }
                }
            }
            return null;
        }

        // Names a forbidden space, or null when the object is allowed / unrecognised.
        // Depth-guarded: /Indexed bases and /Separation alternates nest arbitrarily.
        private static string ClassifyColorSpace(PdfDocument doc, PdfObject csObj, int depth, ColourPolicy policy)
        {
            if (csObj == null || depth > PdfLimits.MaxResourceDepth) return null;

            var resolved = doc.ResolveObject(csObj) ?? csObj;

            if (resolved is PdfName name)
            {
                // Inline-image abbreviations (Table 93) share this path: /RGB is DeviceRGB, /G
                // DeviceGray, /CMYK DeviceCMYK. §8.9.7 reserves them, so none names a resource.
                string expanded = PdfColorSpaceNames.Expand(name.Value);
                return policy.IsForbidden(expanded) ? expanded : null;
            }

            var arr = resolved as PdfArray;
            if (arr == null || arr.Count == 0) return null;

            string family = (doc.ResolveObject(arr[0]) as PdfName ?? arr[0] as PdfName)?.Value;
            if (family == null) return null;

            if (policy.IsForbidden(family)) return family;

            switch (family)
            {
                case PdfColorSpaceNames.ICCBased:
                {
                    // The profile's component count decides: /N 3 is RGB whatever the profile says
                    // it is, and PDF/X-1a admits only 1- and 4-component output.
                    var profile = arr.Count > 1 ? Resolve<PdfStream>(doc, arr[1]) : null;
                    // Resolved, not `GetInteger`: that reads the raw entry, so a producer writing
                    // `/N 6 0 R` — legal under §7.3.10 — yielded null, the 3-component test never
                    // fired and an RGB ICC profile passed X08. The stream itself is already
                    // resolved one line up; the number inside it can be indirect too.
                    int? n = (Resolve<PdfNumber>(doc, profile?[PdfName.N]))?.IntValue;
                    // Gated on the POLICY, not on /N alone. X-3 and X-4 admit device-independent
                    // colour — ICCBased is the canonical example, and an sRGB/AdobeRGB working
                    // space is ordinary Illustrator and InDesign output — so reporting X08 for one
                    // would contradict the very message it is reported under ("PDF/X-4 allows
                    // device-independent colour (ICCBased, …) … found an ICCBased 3-component
                    // (RGB)"), fail a conforming file, and make PdfXConverter withhold the
                    // GTS_PDFXVersion identifier from a file it should stamp.
                    if (n == PdfColorSpaceNames.RgbComponents && policy.ForbidIccBasedRgb)
                        return "an ICCBased 3-component (RGB)";
                    return null;
                }

                case PdfColorSpaceNames.Indexed:
                    // [/Indexed base hival lookup] — the palette entries are in the base space.
                    return arr.Count > 1 ? ClassifyColorSpace(doc, arr[1], depth + 1, policy) : null;

                case PdfColorSpaceNames.Separation:
                    // [/Separation name alternate tintTransform]
                    return arr.Count > 2 ? ClassifyColorSpace(doc, arr[2], depth + 1, policy) : null;

                case PdfColorSpaceNames.DeviceN:
                    // [/DeviceN names alternate tintTransform attributes]
                    return arr.Count > 2 ? ClassifyColorSpace(doc, arr[2], depth + 1, policy) : null;

                case PdfColorSpaceNames.Pattern:
                    // [/Pattern base] — an uncoloured tiling pattern takes its colour from the `scn`
                    // operands in the base space (§8.7.3.3), so the base is a space the page paints
                    // in: `[/Pattern /DeviceRGB]` on a PDF/X-1a page fails X08. PdfAValidator's
                    // A16-A18 classifier descends the same way.
                    return arr.Count > 1 ? ClassifyColorSpace(doc, arr[1], depth + 1, policy) : null;

                default:
                    return null;
            }
        }

        /// <summary>
        /// Which colour spaces a conformance level forbids. X-1a and the later parts differ, so
        /// this cannot be a single static predicate.
        /// </summary>
        /// <remarks>
        /// <para>X-1a admits only DeviceGray, DeviceCMYK and spot colour; RGB in any form, and Lab,
        /// are out.</para>
        /// <para><b>X-3 and X-4 are read from two public sources that agree</b> (GAPS.md §12b's
        /// stopping rule), since ISO 15930-3 and -7 themselves are not available:</para>
        /// <list type="bullet">
        ///   <item>The PDF Association's <i>PDF/X in a Nutshell</i> (CC BY 4.0, in
        ///   <c>docs/iso</c>): PDF/X-3 "requires, however, that device color spaces may be used only
        ///   if the same color space is used for the ICC profile in the Output Intent, so
        ///   DeviceRGB requires the Output Intent to use an RGB ICC profile"; and "PDF/X-4 inherits
        ///   the rules of PDF/X-3".</item>
        ///   <item>A commercial preflight tool's PDF/X-4 checks, as a reseller
        ///   publishes them (duon.zendesk.com, "[EN] … (PDF/X-4)"). "CMYK used but PDF/X
        ///   OutputIntent not CMYK", "RGB used but PDF/X OutputIntent not RGB", and the same pair
        ///   "for alt. color" (a spot colour's alternate) and "for transparency blend color space".
        ///   DeviceGray is the one refinement: "DeviceGray used … but OutputIntent not Gray or
        ///   CMYK".</item>
        /// </list>
        /// <para>Device-independent colour (ICCBased, CalRGB, CalGray, Lab) is admitted at those
        /// levels whatever the intent. <see cref="ClassifyColorSpace"/> is level-agnostic: it
        /// reaches an alternate and an <c>/Indexed</c> base with the same policy, and
        /// <see cref="ColourSpaceSites"/> hands it each blending space.</para>
        /// <para><b>An intent whose profile is not Gray, RGB or CMYK</b> (none embedded, which X-3
        /// allows for a registered condition, or an unreadable <c>/N</c>) refuses DeviceRGB and
        /// admits DeviceCMYK and DeviceGray. That is the conventional press condition. Refusing
        /// CMYK there would fail every X-3 file that names FOGRA39 instead of embedding it.</para>
        /// </remarks>
        private sealed class ColourPolicy
        {
            internal bool ForbidDeviceRgb;
            internal bool ForbidDeviceCmyk;
            internal bool ForbidDeviceGray;
            internal bool ForbidCalRgb;
            internal bool ForbidLab;

            /// <summary>
            /// Whether an <c>[/ICCBased …]</c> space whose profile declares <c>/N 3</c> is
            /// forbidden. True only for X-1a, which admits 1- and 4-component output alone; the
            /// later parts exist precisely to admit ICC-tagged RGB.
            /// </summary>
            internal bool ForbidIccBasedRgb;

            /// <summary>Named in the violation message so a reader can see which rule fired.</summary>
            internal string Because;

            /// <summary>
            /// Whether the family <paramref name="value"/> names is forbidden. Takes the name as
            /// written: a caller holding an inline-image abbreviation expands it first, and a
            /// <c>cs</c> operand is not expanded, because there <c>/G</c> names a resource.
            /// </summary>
            internal bool IsForbidden(string value)
                => (ForbidDeviceRgb  && value == PdfColorSpaceNames.DeviceRGB)
                || (ForbidDeviceCmyk && value == PdfColorSpaceNames.DeviceCMYK)
                || (ForbidDeviceGray && value == PdfColorSpaceNames.DeviceGray)
                || (ForbidCalRgb     && value == PdfColorSpaceNames.CalRGB)
                || (ForbidLab        && value == PdfColorSpaceNames.Lab);

            /// <summary>X-1a: no RGB of any kind, no Lab.</summary>
            internal static ColourPolicy X1a() => new ColourPolicy
            {
                ForbidDeviceRgb  = true,
                ForbidCalRgb     = true,
                ForbidLab        = true,
                ForbidIccBasedRgb = true,
                Because          = "PDF/X-1a allows only DeviceCMYK, DeviceGray and spot colours",
            };

            /// <summary>
            /// X-3 / X-4: device-independent colour is admitted, and a device colour space only
            /// where the output intent's profile is in that space.
            /// </summary>
            /// <param name="intentComponents">The output intent profile's <c>/N</c>, or
            /// <see langword="null"/> when there is none to read (<see cref="OutputIntentComponents"/>).</param>
            /// <param name="level">"PDF/X-3" or "PDF/X-4", for the message.</param>
            internal static ColourPolicy AgainstOutputIntent(int? intentComponents, string level)
            {
                bool gray = intentComponents == PdfColorSpaceNames.GrayComponents;
                bool rgb  = intentComponents == PdfColorSpaceNames.RgbComponents;
                bool cmyk = intentComponents == PdfColorSpaceNames.CmykComponents;
                bool known = gray || rgb || cmyk;

                string intent = gray ? "a Gray" : rgb ? "an RGB" : cmyk ? "a CMYK" : null;
                return new ColourPolicy
                {
                    ForbidDeviceRgb  = !rgb,
                    ForbidDeviceCmyk = known && !cmyk,
                    ForbidDeviceGray = rgb,
                    ForbidCalRgb     = false,
                    ForbidLab        = false,
                    ForbidIccBasedRgb = false,
                    Because          = level + " allows a device colour space only where the output " +
                                       "intent's profile is in that space — DeviceRGB needs an RGB " +
                                       "intent, DeviceCMYK a CMYK one, DeviceGray a Gray or CMYK one — " +
                                       (intent != null
                                           ? "and this output intent's profile is " + intent + " profile"
                                           : "and this output intent has no Gray, RGB or CMYK profile " +
                                             "to read, so only DeviceRGB is refused"),
                };
            }
        }

        /// <summary>
        /// The <c>/N</c> of the <c>GTS_PDFX</c> output intent's embedded profile when it is 1, 3 or
        /// 4, else <see langword="null"/>: no intent, no embedded profile, or a count that is not an
        /// integer or names no Gray, RGB or CMYK space. <see cref="ColourPolicy.AgainstOutputIntent"/>
        /// says what an unknown intent admits.
        /// </summary>
        private static int? OutputIntentComponents(PdfDictionary catalog, PdfDocument doc)
        {
            var intents = new List<PdfDictionary>();
            CollectGtsPdfxIntents(catalog[PdfName.OutputIntents], doc, intents);
            for (int i = 0; i < doc.Pages.Count; i++)
                CollectGtsPdfxIntents(doc.Pages[i].Dictionary[PdfName.OutputIntents], doc, intents);

            if (intents.Count == 0) return null;

            var profile = Resolve<PdfStream>(doc, intents[0][PdfName.DestOutputProfile]);
            if (!(Resolve<PdfObject>(doc, profile?[PdfName.N]) is PdfNumber n) || !n.IsInteger)
                return null;

            long count = n.LongValue;
            return count == PdfColorSpaceNames.GrayComponents
                || count == PdfColorSpaceNames.RgbComponents
                || count == PdfColorSpaceNames.CmykComponents
                ? (int)count
                : (int?)null;
        }

        // ----------------------------------------------------------------
        //  Page scan — resources and content streams reachable from one page
        // ----------------------------------------------------------------

        /// <summary>Everything a content-level check needs from one page, plus why it may be partial.</summary>
        /// <remarks>
        /// <c>internal</c> rather than <c>private</c> — <see cref="PdfAValidator"/>'s colour-
        /// consistency check (A17-A19) reuses <see cref="ScanPage"/> for the identical
        /// resource/content-stream collection problem ("what colour spaces are reachable from this
        /// page"), rather than duplicating the 380-line walk (forms, tiling patterns, Type 3 glyph
        /// procedures, annotation appearances, three nested budgets) for a different verdict rule.
        /// Nothing about the walk itself changed; only its visibility did.
        /// </remarks>
        internal sealed class PageScan
        {
            public readonly List<PdfDictionary> Resources = new List<PdfDictionary>();
            public readonly List<byte[]> ContentStreams = new List<byte[]>();

            /// <summary>
            /// The resource dictionary in scope for the content stream at the same index in
            /// <see cref="ContentStreams"/> (<see langword="null"/> when the stream has none).
            /// </summary>
            /// <remarks>
            /// X08's <c>cs</c>/<c>CS</c> check does not need this — a forbidden colour space named
            /// by a resource is already classified once across every reachable <c>/ColorSpace</c>
            /// dictionary, so which stream selected it does not change the verdict. A <c>BDC</c>
            /// property operand is different: it is a name resolved through <b>this specific
            /// stream's</b> <c>/Properties</c> entry, and two streams can map the same name to two
            /// different dictionaries — so X13's marked-content check needs the pairing X08 never
            /// did.
            /// </remarks>
            public readonly List<PdfDictionary> ContentStreamResources = new List<PdfDictionary>();

            /// <summary>
            /// The object the content stream at the same index in <see cref="ContentStreams"/> was
            /// decoded from — a form XObject, a tiling pattern, a Type 3 glyph procedure, a soft-mask
            /// group or an annotation appearance — or <see langword="null"/> for the page's own content.
            /// </summary>
            /// <remarks>
            /// The walk collects every stream a resource dictionary <b>names</b>, drawn or not, which
            /// is what X04 ("a font in the file") wants. PDF/A-1's A16–A18 want what is <b>drawn</b>:
            /// ISO 19005-1 Corrigendum 2 says a named resource that the associated content stream does
            /// not reference is not used. This lets that check follow <c>Do</c>, <c>scn</c>,
            /// <c>Tf</c> and <c>gs</c> from the page's content to the streams they draw, over the
            /// streams this walk already decoded, rather than walking the resources a second time.
            /// </remarks>
            public readonly List<PdfStream> ContentStreamSources = new List<PdfStream>();

            /// <summary>
            /// The index in <see cref="ContentStreams"/> of the page's own content, or -1 when it was
            /// not collected (no content, not decodable, or over a byte budget).
            /// </summary>
            /// <remarks>
            /// A marked-content <c>/MCID</c> is only meaningful against the parent-tree entry of the
            /// stream it sits in, and a page's own content is the one stream whose entry is the page's
            /// <c>/StructParents</c> — a form XObject or an appearance carries its own. The page's
            /// content is added first when it is added at all, but "first" is not the same as "the
            /// page's", so the index is recorded rather than assumed.
            /// </remarks>
            public int PageContentIndex = -1;

            /// <summary>
            /// Annotation <c>/AP</c> appearance streams, in the order they were seeded.
            /// <para>
            /// An appearance is a form XObject in its own right, but no <c>/XObject</c> resource
            /// dictionary names it — it hangs off the annotation. The scan therefore contributes
            /// its <c>/Resources</c> (which is all X04 and X06 need) while the stream's <b>own</b>
            /// dictionary was reachable from nowhere, so X09 could not see a <c>/Group</c> or
            /// <c>/SMask</c> written directly on it.
            /// </para>
            /// </summary>
            public readonly List<PdfStream> AppearanceStreams = new List<PdfStream>();

            /// <summary>
            /// Non-null when a ceiling or guard stopped the walk. A check that reads a partial scan
            /// must report X99 rather than let "found nothing" pass for "there is nothing".
            /// </summary>
            public string Incomplete;

            /// <summary>
            /// Resource-dictionary <b>entries</b> this page's scan may still examine.
            /// </summary>
            /// <remarks>
            /// <para><see cref="AddResources"/> caps how many <i>distinct</i> resource dictionaries
            /// are collected and <see cref="AddContent"/> caps decoded bytes; neither bounds the
            /// edges examined, which is the cost that actually runs away.
            /// <see cref="WalkNestedResources"/> re-iterates every dictionary collected so far on
            /// each of up to <see cref="PdfLimits.MaxFormXObjectDepth"/> rounds and enumerates each
            /// one's whole <c>/XObject</c>, <c>/Pattern</c> and <c>/Font</c> dictionary — so 4 096
            /// forms all naming one 50 000-entry <c>/XObject</c> dictionary is 2×10<sup>8</sup>
            /// resolve-and-probe operations per round, from a file small enough to arrive as an
            /// email attachment, and five checks each re-run the scan.</para>
            /// <para>Same shape as <c>PdfUaChecker</c>'s: spent per entry examined, never per node
            /// reached, because a visited set bounds re-expansion and says nothing about the cost of
            /// one expansion.</para>
            /// </remarks>
            public int Budget = MaxScanResourceEntries;

            /// <summary>
            /// Whether a byte ceiling refused a stream — which, unlike the entry budget and the depth
            /// cap, depends on what the page's own content already spent, so a scan so refused is not
            /// one another page can share.
            /// </summary>
            internal bool BytesRefused;

            /// <summary>The bytes the nested streams decoded into this scan — for a shared resource walk, what each page charges.</summary>
            internal long NestedBytes;
        }

        /// <summary>Resource entries one page's scan may examine before it is abandoned.</summary>
        /// <remarks>
        /// Exhausting it sets <c>Incomplete</c>, so the existing X99 machinery reports the partial
        /// scan rather than letting "found nothing" pass for "there is nothing". The value sits far
        /// above what the sibling caps already admit — 4 096 distinct dictionaries of ten entries
        /// each, re-walked at every nesting level, is around 10<sup>5</sup> — because exhausting it
        /// degrades the scan, and a ceiling that fired on real artwork would make X99 the noise a
        /// caller learns to ignore.
        /// </remarks>
        private const int MaxScanResourceEntries = 200_000;

        /// <summary>
        /// Spends one unit of <see cref="PageScan.Budget"/>. Returns <c>false</c> — once, with the
        /// refusal recorded for X99 — when the page's scan has run out.
        /// </summary>
        private static bool SpendScanEntry(PageScan scan)
        {
            if (scan.Budget <= 0)
            {
                // The wording names the unit twice on purpose: what is counted is
                // resource-dictionary entries — edges walked — not distinct dictionaries reached,
                // and a reader who sees only "resource entries" tends to assume the latter, which
                // is the cap this one exists to sit beside rather than duplicate.
                scan.Incomplete ??= $"more than {MaxScanResourceEntries} resource-dictionary " +
                                    "entries are reachable from this page; the scan stops after " +
                                    $"that many resource entries and is therefore partial";
                return false;
            }
            scan.Budget--;
            return true;
        }

        /// <param name="collectContent">
        /// <c>false</c> for checks that only need the reachable <b>resource dictionaries</b> (X04
        /// fonts, X06 OPI): the walk then never decodes a content stream, which is the expensive
        /// part. X08 needs the operators themselves and passes <c>true</c>.
        /// </param>
        /// <param name="doc">The document, for resolving indirect references.</param>
        /// <param name="page">The page to scan.</param>
        /// <param name="maxPageContentBytes">
        /// Ceiling on this page's decoded content. Exceeding it refuses the page rather than
        /// returning a partial scan a caller could mistake for a complete one.
        /// </param>
        /// <param name="documentBytes">
        /// Running total of decoded content across the document, carried between pages.
        /// </param>
        /// <param name="maxDocumentContentBytes">Ceiling on that running total.</param>
        /// <param name="printElementsOnly">
        /// When <see langword="true"/>, an annotation whose appearance is <b>not a print element</b>
        /// under ISO 15930-9:2020, 6.2.2 is skipped. <b>This is a PDF/X scoping rule and nothing
        /// else's.</b> The walk is shared with <c>PdfAValidator</c>, <c>Pdf20DeprecationChecker</c>
        /// and <c>Accessibility</c>'s PDF/UA-2 structure checker, none of which is scoped to what
        /// prints — a screen reader is shown the annotation a press never sees — so it defaults to
        /// <see langword="false"/> and only the X04/X06/X08/X09 cluster passes it.
        /// </param>
        internal static PageScan ScanPage(PdfDocument doc, PdfPage page, int maxPageContentBytes,
            ref long documentBytes, long maxDocumentContentBytes, bool collectContent = true,
            bool printElementsOnly = false)
        {
            // One validation's checks share a page's scan and its parse (ScanReuse).
            var reuse = ScanReuse.Current;
            if (reuse != null && reuse.TryGet(page?.Dictionary, maxPageContentBytes, ref documentBytes,
                    maxDocumentContentBytes, collectContent, printElementsOnly, out var reused))
                return reused;
            long documentBytesBefore = documentBytes;

            var scan = new PageScan();
            // PdfDictionary and PdfStream do not override Equals, so these sets are reference
            // identity — which is what a cycle guard wants: a form XObject that draws itself.
            var seenResources = new HashSet<PdfDictionary>();
            var seenForms = new HashSet<PdfStream>();
            long pageBytes = 0;

            if (documentBytes >= maxDocumentContentBytes)
            {
                scan.Incomplete = $"the document content ceiling of {maxDocumentContentBytes} bytes " +
                                  "was reached before this page";
                return scan;
            }

            byte[] content = null;
            if (collectContent)
            {
                // One budget across the parts of an array-valued /Contents, and all or nothing: a
                // part left out can hold the colour-space or graphics state the next part's
                // operators depend on, so scanning the rest would read state that is not there.
                long consumed = 0, documentSoFar = documentBytes;
                bool refused = false;
                content = PdfPageContent.Concatenate(page.Dictionary, doc.ResolveObject, part =>
                {
                    if (refused) return null;
                    byte[] decoded = DecodeWithinCeilings(part, "the page content stream", scan,
                        maxPageContentBytes, pageBytes + consumed, documentSoFar + consumed,
                        maxDocumentContentBytes);
                    if (decoded == null) refused = true;
                    else consumed += decoded.Length + 1;   // the separator Concatenate inserts
                    return decoded;
                });
                if (refused) content = null;
            }

            // A page naming resources another page has walked, and showing no annotation, takes the walk
            // below its own content from that page's (ScanReuse.SharedWalk): the walk reads the
            // resources alone, so it is the same for every page naming them. Taken only where the full
            // walk would have reached the same streams — no byte ceiling refused one in it, and its
            // bytes fit what this page's content leaves — and charged as the full walk charges.
            if (reuse != null && page.Resources != null && !HasAnnotations(doc, page))
            {
                PdfDictionary pageResources = page.Resources;
                long own = content?.Length ?? 0;
                // Made within exactly what this page's own content leaves, so it decodes what this page's
                // full walk would decode and nothing more: a stream decodes once, and its decode is kept
                // on it, so a walk under a wider ceiling would change what a later bounded decode says.
                int pageLeft = (int)Math.Max(0, maxPageContentBytes - own);
                long documentLeft = Math.Max(0, maxDocumentContentBytes - documentBytes - own);
                PageScan shared = reuse.SharedWalk(pageResources, maxPageContentBytes, maxDocumentContentBytes, collectContent,
                    () => WalkResourcesAlone(doc, pageResources, pageLeft, documentLeft, collectContent));
                if (!shared.BytesRefused && own + shared.NestedBytes <= maxPageContentBytes
                    && documentBytes + own + shared.NestedBytes <= maxDocumentContentBytes)
                {
                    scan.Resources.AddRange(shared.Resources);
                    AddContent(scan, content, maxPageContentBytes, ref pageBytes,
                        ref documentBytes, maxDocumentContentBytes, pageResources, source: null);
                    if (scan.ContentStreams.Count > 0) scan.PageContentIndex = 0;
                    scan.ContentStreams.AddRange(shared.ContentStreams);
                    scan.ContentStreamResources.AddRange(shared.ContentStreamResources);
                    scan.ContentStreamSources.AddRange(shared.ContentStreamSources);
                    scan.Budget = shared.Budget;
                    scan.Incomplete ??= shared.Incomplete;
                    pageBytes += shared.NestedBytes;
                    documentBytes += shared.NestedBytes;
                    reuse.Keep(page?.Dictionary, maxPageContentBytes, maxDocumentContentBytes, collectContent,
                        printElementsOnly, scan, documentBytes - documentBytesBefore);
                    return scan;
                }
            }

            AddResources(doc, page.Resources, scan, seenResources);
            int streamsBefore = scan.ContentStreams.Count;
            AddContent(scan, content, maxPageContentBytes, ref pageBytes,
                ref documentBytes, maxDocumentContentBytes, page.Resources, source: null);
            if (scan.ContentStreams.Count > streamsBefore) scan.PageContentIndex = streamsBefore;

            // Annotation appearance streams are a resource root of their own: a stamp or a form
            // field draws from /AP, whose /Resources are reachable from nowhere else on the page.
            // A font used only there is still a font in the file, which is what X04 asks about.
            SeedAnnotationAppearances(doc, page, scan, seenResources, seenForms, collectContent,
                maxPageContentBytes, ref pageBytes, ref documentBytes, maxDocumentContentBytes,
                printElementsOnly);

            // Form XObjects, tiling patterns and Type 3 glyph procedures all contribute their own
            // resources and content, and may nest. Both matter: the colour operator and the space
            // it names can sit at different levels.
            WalkNestedResources(doc, scan, 0, seenResources, seenForms, collectContent,
                maxPageContentBytes, ref pageBytes, ref documentBytes, maxDocumentContentBytes);

            reuse?.Keep(page?.Dictionary, maxPageContentBytes, maxDocumentContentBytes, collectContent,
                printElementsOnly, scan, documentBytes - documentBytesBefore);
            return scan;
        }

        /// <summary>Whether the page lists any annotation — whose appearances the shared resource walk does not hold.</summary>
        private static bool HasAnnotations(PdfDocument doc, PdfPage page)
            => Resolve<PdfArray>(doc, page.Dictionary?[PdfName.Annots]) is PdfArray annots && annots.Count > 0;

        /// <summary>
        /// The scan of <paramref name="resources"/> with no page content and no annotation: the
        /// dictionary, and every form, pattern, Type 3 procedure and soft-mask group it reaches, in the
        /// order the full walk reaches them — its bytes counted from nothing, in
        /// <see cref="PageScan.NestedBytes"/>.
        /// </summary>
        private static PageScan WalkResourcesAlone(PdfDocument doc, PdfDictionary resources, int maxPageContentBytes,
            long maxDocumentContentBytes, bool collectContent)
        {
            var walk = new PageScan();
            var seenResources = new HashSet<PdfDictionary>();
            var seenForms = new HashSet<PdfStream>();
            long pageBytes = 0, documentBytes = 0;
            AddResources(doc, resources, walk, seenResources);
            WalkNestedResources(doc, walk, 0, seenResources, seenForms, collectContent,
                maxPageContentBytes, ref pageBytes, ref documentBytes, maxDocumentContentBytes);
            walk.NestedBytes = documentBytes;
            return walk;
        }

        /// <summary>
        /// Whether <paramref name="annot"/>'s appearance stream is a <b>print element</b> under
        /// ISO 15930-9:2020, 6.2.2 — its printable flag set, and its invisible and hidden flags
        /// clear.
        /// </summary>
        /// <remarks>
        /// The three conditions are taken literally from the clause rather than folded into
        /// <c>PdfAnnotationFlags.IsHiddenInPrint</c>, which answers a *rendering* question and so
        /// tests only Hidden and Print. <b>Invisible is the third</b>, and ISO 32000-2 12.5.3 gives
        /// it a narrower meaning — do not render an annotation of an *unknown* type — so a renderer
        /// is right to ignore it where this clause is not.
        /// </remarks>
        private static bool IsPrintElement(PdfDocument doc, PdfDictionary annot)
        {
            // /F is optional and defaults to 0, which leaves the printable flag clear.
            int flags = PdfAnnotationFlags.FromNumber(Resolve<PdfNumber>(doc, annot[PdfName.F])) ?? 0;

            return (flags & PdfAnnotationFlags.Print) != 0
                && (flags & PdfAnnotationFlags.Hidden) == 0
                && (flags & PdfAnnotationFlags.Invisible) == 0;
        }

        /// <summary>
        /// Adds the normal appearance stream of every annotation that is a <b>print element</b> —
        /// and, for a state-dependent <c>/AP /N</c> sub-dictionary, each state's stream.
        /// </summary>
        /// <remarks>
        /// <para><b>A non-printing annotation's appearance is out of scope, and scanning it anyway
        /// is a false fail.</b> ISO 15930-9:2020, 6.2.2 defines a print element as content drawn or
        /// referenced from a page's <c>Contents</c> stream, or from "the appearance stream of any
        /// annotation, including those with a <c>Subtype</c> value of <c>Widget</c> (form fields),
        /// which has its printable flag set and its invisible and hidden flags clear"; everything
        /// else in the file is a non-print element (6.2.3). Clause 6 is written against print
        /// elements — 6.6.3 is titled *Print element colour spaces* — so a stamp that never reaches
        /// the press must not have its colour, its fonts or its transparency judged.</para>
        ///
        /// <para><b>Quoted for PDF/X-6, carried to the earlier levels.</b> ISO 15930-7's
        /// counterpart is its own 6.2, which the preview in <c>docs/iso</c> does not reach, so the
        /// wording at X-1a/X-3/X-4 is unverified — but every part of the family is built on the same
        /// print/non-print split, and the whole point of that split is to scope the restrictions to
        /// what prints. GAPS.md §12b records which half is quotation.</para>
        ///
        /// <para><b>`/F` defaults to 0, so an annotation with no `/F` is NOT a print element</b> —
        /// the printable flag is clear. That is the same reading `AnnotationVisibility` applies for
        /// a print render, and it is why the fixtures in this plugin's tests carry an
        /// explicit `/F`.</para>
        /// </remarks>
        private static void SeedAnnotationAppearances(PdfDocument doc, PdfPage page, PageScan scan,
            HashSet<PdfDictionary> seenResources, HashSet<PdfStream> seenForms, bool collectContent,
            int maxPageContentBytes, ref long pageBytes,
            ref long documentBytes, long maxDocumentContentBytes, bool printElementsOnly)
        {
            var annots = Resolve<PdfArray>(doc, page.Dictionary?[PdfName.Annots]);
            if (annots == null) return;
            if (!ScanReuse.AdmitAnnotations(annots))
            {
                scan.Incomplete ??= "the validation's annotation ceiling was reached, so this page's annotations " +
                                    "were not scanned";
                return;
            }

            for (int i = 0; i < annots.Count; i++)
            {
                if (!SpendScanEntry(scan)) return;
                var annot = Resolve<PdfDictionary>(doc, annots[i]);
                if (annot == null) continue;
                if (printElementsOnly && !IsPrintElement(doc, annot)) continue;

                var ap = Resolve<PdfDictionary>(doc, annot[PdfName.AP]);
                var normal = ap?[PdfName.N];
                if (normal == null) continue;

                // /N is either the appearance stream itself or a dictionary of named states.
                var direct = Resolve<PdfStream>(doc, normal);
                if (direct != null)
                {
                    AddAppearance(doc, direct, scan, seenResources, seenForms, collectContent,
                        maxPageContentBytes, ref pageBytes, ref documentBytes, maxDocumentContentBytes);
                    continue;
                }

                var states = Resolve<PdfDictionary>(doc, normal);
                if (states == null) continue;
                foreach (var kvp in states)
                {
                    if (!SpendScanEntry(scan)) return;
                    var stateStream = Resolve<PdfStream>(doc, kvp.Value);
                    if (stateStream != null)
                        AddAppearance(doc, stateStream, scan, seenResources, seenForms, collectContent,
                            maxPageContentBytes, ref pageBytes, ref documentBytes, maxDocumentContentBytes);
                }
            }
        }

        private static void AddAppearance(PdfDocument doc, PdfStream ap, PageScan scan,
            HashSet<PdfDictionary> seenResources, HashSet<PdfStream> seenForms, bool collectContent,
            int maxPageContentBytes, ref long pageBytes,
            ref long documentBytes, long maxDocumentContentBytes)
        {
            if (!seenForms.Add(ap)) return;

            // Recorded whether or not content is being collected: X09 scans with
            // collectContent:false and still has to inspect this stream's own dictionary.
            scan.AppearanceStreams.Add(ap);

            var apResources = Resolve<PdfDictionary>(doc, ap[PdfName.Resources]);

            if (collectContent)
            {
                byte[] data = DecodeWithinCeilings(ap, "an annotation appearance stream", scan,
                    maxPageContentBytes, pageBytes, documentBytes, maxDocumentContentBytes);
                AddContent(scan, data, maxPageContentBytes, ref pageBytes,
                    ref documentBytes, maxDocumentContentBytes, apResources, ap);
            }

            AddResources(doc, apResources, scan, seenResources);
        }

        /// <summary>
        /// Descends into every construct that carries its own <c>/Resources</c>: form XObjects,
        /// tiling patterns, Type 3 fonts (whose <c>/CharProcs</c> are content streams drawn with
        /// the font's own resource dictionary), and the transparency groups of <c>/ExtGState</c>
        /// soft masks.
        /// </summary>
        /// <remarks>
        /// A font or colour space reachable *only* through a pattern, a glyph procedure or a soft
        /// mask's group would otherwise be invisible to every content-level check — X08 would read
        /// the page as clean and X04 never see the font. A soft mask's content is drawn, and
        /// nothing but the <c>/SMask</c> entry reaches it.
        /// </remarks>
        private static void WalkNestedResources(PdfDocument doc, PageScan scan, int depth,
            HashSet<PdfDictionary> seenResources, HashSet<PdfStream> seenForms, bool collectContent,
            int maxPageContentBytes, ref long pageBytes,
            ref long documentBytes, long maxDocumentContentBytes)
        {
            if (depth > PdfLimits.MaxFormXObjectDepth)
            {
                scan.Incomplete ??= $"form XObject nesting exceeded {PdfLimits.MaxFormXObjectDepth} levels";
                return;
            }

            // Snapshot: AddResources appends while we iterate.
            var current = scan.Resources.ToArray();
            bool addedAny = false;

            foreach (var res in current)
            {
                var xoDict = Resolve<PdfDictionary>(doc, res[PdfName.XObject]);
                if (xoDict == null) continue;

                foreach (var kvp in xoDict)
                {
                    if (!SpendScanEntry(scan)) return;
                    var form = Resolve<PdfStream>(doc, kvp.Value);
                    if (form == null) continue;
                    if (ResolveName(doc, form, PdfName.Subtype) != "Form") continue;
                    if (AddNested(doc, form, $"form XObject /{kvp.Key.Value}", scan,
                            seenResources, seenForms, collectContent, maxPageContentBytes,
                            ref pageBytes, ref documentBytes, maxDocumentContentBytes))
                        addedAny = true;
                }
            }

            // Tiling patterns are streams with their own /Resources. A shading pattern is a plain
            // dictionary with none and no content, but its /ExtGState (Table 76) is a graphics state
            // the paint applies: it is wrapped as a resource dictionary of its own, so every rule
            // reading the /ExtGState entries of scan.Resources — A09-A14, A40, A56/A57, X09, X14 —
            // sees it as it sees the page's.
            foreach (var res in current)
            {
                var patDict = Resolve<PdfDictionary>(doc, res[PdfName.Pattern]);
                if (patDict == null) continue;

                foreach (var kvp in patDict)
                {
                    if (!SpendScanEntry(scan)) return;
                    var pattern = Resolve<PdfStream>(doc, kvp.Value);
                    if (pattern == null)
                    {
                        var shadingPattern = Resolve<PdfDictionary>(doc, kvp.Value);
                        var patternState = Resolve<PdfDictionary>(doc, shadingPattern?[PdfName.ExtGState]);
                        if (patternState != null && AddResources(doc, ShadingPatternResources(kvp.Key, patternState), scan, seenResources))
                            addedAny = true;
                        continue;
                    }
                    if (AddNested(doc, pattern, $"pattern /{kvp.Key.Value}", scan,
                            seenResources, seenForms, collectContent, maxPageContentBytes,
                            ref pageBytes, ref documentBytes, maxDocumentContentBytes))
                        addedAny = true;
                }
            }

            // Type 3 fonts: the font dictionary carries /Resources for its glyph procedures, and
            // each /CharProcs entry is a content stream painted with them.
            foreach (var res in current)
            {
                var fontDict = Resolve<PdfDictionary>(doc, res[PdfName.Font]);
                if (fontDict == null) continue;

                foreach (var kvp in fontDict)
                {
                    if (!SpendScanEntry(scan)) return;
                    var font = Resolve<PdfDictionary>(doc, kvp.Value);
                    if (ResolveName(doc, font, PdfName.Subtype) != "Type3") continue;

                    var glyphResources = Resolve<PdfDictionary>(doc, font[PdfName.Resources]);
                    if (AddResources(doc, glyphResources, scan, seenResources))
                        addedAny = true;

                    if (!collectContent) continue;
                    var procs = Resolve<PdfDictionary>(doc, font[PdfName.CharProcs]);
                    if (procs == null) continue;
                    foreach (var proc in procs)
                    {
                        if (!SpendScanEntry(scan)) return;
                        var glyph = Resolve<PdfStream>(doc, proc.Value);
                        if (glyph != null)
                            AddNested(doc, glyph, $"Type 3 glyph /{proc.Key.Value}", scan,
                                seenResources, seenForms, collectContent, maxPageContentBytes,
                                ref pageBytes, ref documentBytes, maxDocumentContentBytes,
                                drawnWith: glyphResources);
                    }
                }
            }

            // Soft masks: an /ExtGState's /SMask names a transparency group, a form XObject
            // reached from nowhere else — not from any /XObject entry — whose content is drawn to
            // make the mask. It carries its own /Resources and its own colour operators, so an RGB
            // fill inside a soft mask on a PDF/X-1a page is an X08 like any other.
            foreach (var res in current)
            {
                var egsDict = Resolve<PdfDictionary>(doc, res[PdfName.ExtGState]);
                if (egsDict == null) continue;

                foreach (var kvp in egsDict)
                {
                    if (!SpendScanEntry(scan)) return;
                    var smask = Resolve<PdfDictionary>(doc, Resolve<PdfDictionary>(doc, kvp.Value)?[PdfName.SMask]);
                    var group = Resolve<PdfStream>(doc, smask?[PdfName.G]);
                    if (group == null) continue;
                    if (AddNested(doc, group, $"soft mask group of /ExtGState /{kvp.Key.Value}", scan,
                            seenResources, seenForms, collectContent, maxPageContentBytes,
                            ref pageBytes, ref documentBytes, maxDocumentContentBytes))
                        addedAny = true;
                }
            }

            if (addedAny)
                WalkNestedResources(doc, scan, depth + 1, seenResources, seenForms, collectContent,
                    maxPageContentBytes, ref pageBytes, ref documentBytes, maxDocumentContentBytes);
        }

        /// <summary>Adds one nested stream's content and resources; returns true if it contributed
        /// a new resource dictionary, which is what drives another walk round.</summary>
        /// <remarks>
        /// <c>drawnWith</c> is the resources the stream is drawn with when it has none of its own —
        /// a Type 3 glyph procedure's are its font's (§9.6.5). Without them a glyph procedure was
        /// paired with no resources at all, so a name it used (a font, an image) resolved to
        /// nothing, and a non-embedded font drawn only inside a Type 3 glyph was never seen to be
        /// used.
        /// </remarks>
        private static bool AddNested(PdfDocument doc, PdfStream stream, string label, PageScan scan,
            HashSet<PdfDictionary> seenResources, HashSet<PdfStream> seenForms, bool collectContent,
            int maxPageContentBytes, ref long pageBytes,
            ref long documentBytes, long maxDocumentContentBytes, PdfDictionary drawnWith = null)
        {
            if (!seenForms.Add(stream)) return false;

            var streamResources = Resolve<PdfDictionary>(doc, stream[PdfName.Resources]) ?? drawnWith;

            if (collectContent)
            {
                byte[] data = DecodeWithinCeilings(stream, label, scan,
                    maxPageContentBytes, pageBytes, documentBytes, maxDocumentContentBytes);
                AddContent(scan, data, maxPageContentBytes, ref pageBytes,
                    ref documentBytes, maxDocumentContentBytes, streamResources, stream);
            }

            return AddResources(doc, streamResources, scan, seenResources);
        }

        /// <summary>
        /// A shading pattern's <c>/ExtGState</c> as the one entry of a resource dictionary's
        /// <c>/ExtGState</c>, under the pattern's own name. One per shading pattern dictionary, so a
        /// pattern several pages share is one dictionary to every rule (<see cref="ScanReuse.FirstVisit"/>).
        /// </summary>
        private static PdfDictionary ShadingPatternResources(PdfName patternName, PdfDictionary patternState)
        {
            var entries = _shadingPatternResources.GetValue(patternState, state =>
            {
                var gsEntries = new PdfDictionary();
                gsEntries.Set(patternName, state);
                var resources = new PdfDictionary();
                resources.Set(PdfName.ExtGState, gsEntries);
                return resources;
            });
            return entries;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PdfDictionary, PdfDictionary> _shadingPatternResources
            = new System.Runtime.CompilerServices.ConditionalWeakTable<PdfDictionary, PdfDictionary>();

        private static bool AddResources(PdfDocument doc, PdfDictionary res, PageScan scan,
            HashSet<PdfDictionary> seen)
        {
            if (res == null || !seen.Add(res)) return false;
            if (scan.Resources.Count >= PdfLimits.MaxPluginResourceEntries)
            {
                scan.Incomplete ??= $"more than {PdfLimits.MaxPluginResourceEntries} resource " +
                                    "dictionaries are reachable from this page";
                return false;
            }
            scan.Resources.Add(res);
            return true;
        }

        /// <summary>
        /// Decodes one content stream within what the page's and the document's ceilings leave, or
        /// returns <see langword="null"/> having marked the scan incomplete.
        /// </summary>
        /// <remarks>
        /// <b>The refusal carries the decoder's own reason</b>, because Core raises no distinct type
        /// for a limit: "exceeds the N-byte decode limit" and "failed to decompress" are both
        /// <c>TryGetDecodedData</c>'s <see langword="false"/>, and only the text says which.
        /// </remarks>
        private static byte[] DecodeWithinCeilings(PdfStream stream, string label, PageScan scan,
            int maxPageContentBytes, long pageBytes, long documentBytes, long maxDocumentContentBytes)
        {
            long remaining = Math.Max(0, Math.Min(maxPageContentBytes - pageBytes,
                                                  maxDocumentContentBytes - documentBytes));
            if (stream.TryGetDecodedData(remaining, out byte[] data, out string reason))
                return data;

            Report("PdfXValidator.DecodeWithinCeilings", $"{label} was not decoded: {reason}");
            scan.BytesRefused = true;
            scan.Incomplete ??= $"{label} was not decoded within the {remaining} bytes the content " +
                                $"ceilings leave, or could not be decoded at all: {reason}";
            return null;
        }

        private static void AddContent(PageScan scan, byte[] content, int maxPageContentBytes,
            ref long pageBytes, ref long documentBytes, long maxDocumentContentBytes,
            PdfDictionary resources, PdfStream source)
        {
            if (content == null || content.Length == 0) return;

            if (pageBytes + content.Length > maxPageContentBytes)
            {
                scan.BytesRefused = true;
                scan.Incomplete ??= $"the per-page content ceiling of {maxPageContentBytes} bytes " +
                                    "would be exceeded";
                return;
            }
            if (documentBytes + content.Length > maxDocumentContentBytes)
            {
                scan.BytesRefused = true;
                scan.Incomplete ??= $"the document content ceiling of {maxDocumentContentBytes} " +
                                    "bytes would be exceeded";
                return;
            }

            pageBytes += content.Length;
            documentBytes += content.Length;
            scan.ContentStreams.Add(content);
            scan.ContentStreamResources.Add(resources);
            scan.ContentStreamSources.Add(source);
        }

        private static T Resolve<T>(PdfDocument doc, PdfObject obj) where T : PdfObject
            => obj == null ? null : (doc.ResolveObject(obj) as T ?? obj as T);

        /// <summary>Reads a name-valued entry, <b>following an indirect reference</b>.</summary>
        /// <remarks>
        /// <see cref="PdfDictionary.GetName"/> is a plain <c>as PdfName</c> over the raw entry, so a
        /// producer that writes <c>/Subtype 6 0 R</c> makes it answer <c>null</c> — indistinguishable
        /// from the key being absent. Every name read in this file goes through here instead,
        /// because a miss fails in the <b>false-pass</b> direction: a form XObject or a Type 3 font
        /// whose <c>/Subtype</c> is indirect would be skipped by the resource walk, hiding
        /// everything inside it from X04, X06, X08, X09 and X13 at once. The same rule as the
        /// <c>PdfResolve</c> rule CLAUDE.md states for the two renderers.
        /// </remarks>
        private static string ResolveName(PdfDocument doc, PdfDictionary dict, PdfName key)
            => dict == null ? null : Resolve<PdfName>(doc, dict[key])?.Value;

        // X09 (X-1a / X-3) — no transparency
        /// <remarks>
        /// Walks every resource dictionary reachable from the page, like X04 and X06, so an
        /// <c>/ExtGState</c> soft mask or an <c>/SMask</c>-bearing image inside a form XObject, a
        /// tiling pattern or an annotation appearance is seen — a form XObject is the ordinary
        /// place for placed artwork, which is exactly the artwork most likely to carry
        /// transparency.
        /// </remarks>
        private static void CheckTransparency(PdfDocument doc, List<PdfXViolation> v,
            List<PdfXViolation> info, int maxPageContentBytes, long maxDocumentContentBytes)
        {
            long documentBytes = 0;
            var seenTransparency = new HashSet<PdfDictionary>();

            for (int p = 0; p < doc.Pages.Count; p++)
            {
                var page = doc.Pages[p];

                // Check page-level /Group (transparency group)
                if (page.Dictionary[PdfName.Group] != null)
                {
                    v.Add(new PdfXViolation("X09",
                        $"PDF/X-1a/X-3 does not allow transparency groups (/Group on page)."));
                    return;
                }

                var scan = ScanPage(doc, page, maxPageContentBytes,
                    ref documentBytes, maxDocumentContentBytes, collectContent: false, printElementsOnly: true);

                if (scan.Incomplete != null)
                    info.Add(new PdfXViolation("X99",
                        $"Page {p + 1}: the X09 transparency check did not run in full — {scan.Incomplete}."));

                foreach (var res in scan.Resources)
                {
                // A dictionary pages share is judged once.
                if (!seenTransparency.Add(res)) continue;

                // /ExtGState is checked first because it is how a producer ordinarily introduces
                // transparency: `/GS0 gs` with an /SMask, a constant alpha or a blend mode. None of
                // it touches the /XObject resources, so a check that reads only those reported an
                // X-1a file full of transparency as conforming.
                var egsDict = Resolve<PdfDictionary>(doc, res[PdfName.ExtGState]);
                if (egsDict != null)
                {
                    foreach (var kvp in egsDict)
                    {
                        var gs = doc.ResolveObject(kvp.Value) as PdfDictionary
                              ?? kvp.Value as PdfDictionary;
                        if (gs == null) continue;

                        var smask = doc.ResolveObject(gs[PdfName.SMask]) ?? gs[PdfName.SMask];
                        if (smask != null && !(smask is PdfName sm && sm.Value == "None"))
                        {
                            v.Add(new PdfXViolation("X09",
                                "PDF/X-1a/X-3 does not allow a soft mask (/SMask) in an /ExtGState."));
                            return;
                        }

                        // Resolved, for the same reason as /N above: `GetDouble` reads the raw
                        // entry, so `/ca 9 0 R` answered null and `null < 1.0` is false — a page
                        // of 50%-alpha artwork validated PDF/X-1a clean.
                        double? fillAlpha   = (Resolve<PdfNumber>(doc, gs[PdfName.ca]))?.DoubleValue;
                        double? strokeAlpha = (Resolve<PdfNumber>(doc, gs[PdfName.CA]))?.DoubleValue;
                        if (fillAlpha < 1.0 || strokeAlpha < 1.0)
                        {
                            v.Add(new PdfXViolation("X09",
                                "PDF/X-1a/X-3 does not allow constant alpha below 1.0 (/ca or /CA in an /ExtGState)."));
                            return;
                        }

                        var bmObj = doc.ResolveObject(gs[PdfName.BM]) ?? gs[PdfName.BM];
                        // /BM may be an array of names — the first recognised one wins (§11.6.3).
                        var bmArray = bmObj as PdfArray;
                        var bm = bmObj as PdfName
                              ?? (bmArray != null && bmArray.Count > 0 ? bmArray[0] as PdfName : null);
                        if (bm != null
                            && bm.Value != PdfBlendModes.Normal
                            && bm.Value != PdfBlendModes.Compatible)
                        {
                            v.Add(new PdfXViolation("X09",
                                $"PDF/X-1a/X-3 does not allow the blend mode /{bm.Value} (only /Normal and /Compatible)."));
                            return;
                        }
                    }
                }

                var xoDict = Resolve<PdfDictionary>(doc, res[PdfName.XObject]);
                if (xoDict == null) continue;
                foreach (var kvp in xoDict)
                {
                    var xo = doc.ResolveObject(kvp.Value) as PdfDictionary
                          ?? kvp.Value as PdfDictionary;
                    if (xo == null) continue;
                    if (xo[PdfName.SMask] != null || xo[PdfName.Group] != null)
                    {
                        v.Add(new PdfXViolation("X09",
                            "PDF/X-1a/X-3 does not allow soft masks (/SMask) or transparency groups (/Group) on XObjects."));
                        return;
                    }
                }
                }

                // Annotation appearance streams, whose own dictionaries no /XObject resource names.
                // The scan adds each one so its /Resources are walked; the stream itself is read
                // here, since `/AP /N` carrying `/Group << /S /Transparency >>` is how a stamp or a
                // filled form field ordinarily introduces a group. After the resource loop:
                // CheckTransparency reports the first violation it finds and stops, so a file that
                // offends in both places reports the resource's message.
                for (int a = 0; a < scan.AppearanceStreams.Count; a++)
                {
                    var ap = scan.AppearanceStreams[a];
                    if (ap[PdfName.SMask] != null || ap[PdfName.Group] != null)
                    {
                        v.Add(new PdfXViolation("X09",
                            "PDF/X-1a/X-3 does not allow soft masks (/SMask) or transparency groups " +
                            "(/Group) on an annotation appearance stream (/AP)."));
                        return;
                    }
                }
            }
        }
    }

    // ----------------------------------------------------------------
    //  Result types
    // ----------------------------------------------------------------

    /// <summary>Result of a PDF/X conformance validation.</summary>
    public sealed class PdfXValidationResult
    {
        /// <summary>The conformance level that was checked.</summary>
        public PdfXConformanceLevel Level { get; }

        /// <summary>
        /// List of violations found. Empty means the document passed **every check that ran** —
        /// consult <see cref="Informational"/> to find out whether any were skipped.
        /// </summary>
        public IReadOnlyList<PdfXViolation> Violations { get; }

        /// <summary>
        /// Checks that could not be completed, as X99 entries (ceiling reached, stream would not
        /// decode). Never empty-implies-clean: a skipped check reports no violation, so this list is
        /// the only way to tell "conforming" from "not examined". It deliberately does **not**
        /// affect <see cref="IsConforming"/>.
        /// </summary>
        public IReadOnlyList<PdfXViolation> Informational { get; }

        /// <summary>True if no violations were found by the checks that ran.</summary>
        public bool IsConforming => Violations.Count == 0;

        /// <summary>True when at least one check was skipped, so a clean result is not a full pass.</summary>
        public bool IsPartial => Informational.Count > 0;

        internal PdfXValidationResult(List<PdfXViolation> violations, PdfXConformanceLevel level,
            List<PdfXViolation> informational = null)
        {
            Violations = violations;
            Informational = informational ?? new List<PdfXViolation>();
            Level = level;
        }
    }

    /// <summary>A single PDF/X conformance violation.</summary>
    public sealed class PdfXViolation
    {
        /// <summary>Check code (e.g. "X02").</summary>
        public string Code { get; }
        /// <summary>Human-readable description of the violation.</summary>
        public string Message { get; }

        internal PdfXViolation(string code, string message) { Code = code; Message = message; }

        public override string ToString() => $"[{Code}] {Message}";
    }
}
