// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>PDF/A conformance part (ISO 19005-1/2/3/4).</summary>
    public enum PdfAConformancePart
    {
        /// <summary>PDF/A-1 (ISO 19005-1). Supports conformance levels A and B.</summary>
        Part1 = 1,
        /// <summary>PDF/A-2 (ISO 19005-2). Supports levels A, B, and U.</summary>
        Part2 = 2,
        /// <summary>PDF/A-3 (ISO 19005-3). Like PDF/A-2 but allows any embedded files.</summary>
        Part3 = 3,
        /// <summary>PDF/A-4 (ISO 19005-4). Based on PDF 2.0. Supports sub-conformances f, e, a.</summary>
        Part4 = 4
    }

    /// <summary>
    /// The conformance level a PDF/A validation judges a file at: the one its XMP
    /// <c>pdfaid:conformance</c> declares, or one the caller requires.
    /// </summary>
    /// <remarks>
    /// A required level decides every level-specific rule, and a file whose declared letter it does
    /// not admit is an A07 finding. The levels nest, so a higher one is admitted: a file declaring A
    /// validates at level B. Each part admits its own levels: PDF/A-1
    /// <see cref="A"/> and <see cref="B"/>; PDF/A-2 and -3 those and <see cref="U"/>; PDF/A-4
    /// <see cref="None"/> (PDF/A-4 itself, which declares no letter), <see cref="E"/> and <see cref="F"/>.
    /// </remarks>
    public enum PdfAConformanceLevel
    {
        /// <summary>The level the file's <c>pdfaid:conformance</c> declares.</summary>
        Declared = 0,
        /// <summary>Level A, accessible (PDF/A-1, -2, -3).</summary>
        A = 1,
        /// <summary>Level B, basic (PDF/A-1, -2, -3).</summary>
        B = 2,
        /// <summary>Level U, Unicode (PDF/A-2, -3).</summary>
        U = 3,
        /// <summary>PDF/A-4 itself: no <c>pdfaid:conformance</c> (ISO 19005-4 6.7.3; rule 6.7.3-3).</summary>
        None = 4,
        /// <summary>PDF/A-4e, engineering (ISO 19005-4 Annex B).</summary>
        E = 5,
        /// <summary>PDF/A-4f, with embedded files of any kind (ISO 19005-4 Annex A).</summary>
        F = 6,
    }

    /// <summary>
    /// Validates a PDF against a PDF/A conformance level (ISO 19005-1/2/3/4).
    ///
    /// Common checks (all parts):
    ///   A01 — No encryption
    ///   A02 — No action of a type the part does not permit, wherever it is (Launch, JavaScript
    ///         before PDF/A-4, …; ISO 19005-1 6.6.1, 19005-2/3 6.5.1, 19005-4 6.6.1)
    ///   A03 — No embedded files (PDF/A-1; PDF/A-2 to -4 admit them under A67-A68)
    ///   A04 — No /AA where the part forbids it: PDF/A-1 the catalog, widgets and fields; PDF/A-2/3
    ///         those and pages; PDF/A-4 only the E, X, D, U, Fo and Bl triggers on a catalog, a page
    ///         or a non-widget annotation
    ///   A05 — Retired: a JavaScript /OpenAction is an A02
    ///   A06 — All fonts must be fully embedded
    ///   A07 — XMP metadata stream must declare the correct PDF/A part and conformance
    ///   A22 — No named action other than NextPage, PrevPage, FirstPage and LastPage
    ///   A23 — No /A action on a widget annotation
    ///   A24–A28 — A JPXDecode image's colour channels, colour specifications, METH, EnumCS and
    ///         bit depth (ISO 19005-2/3 §6.2.8.3, rules 6.2.8.3-1 to -5; ISO 19005-4
    ///         §6.2.7.3, rules 6.2.7.3-1 to -5; PDF/A-2, -3 and -4)
    ///   A29 — A JPXDecode image uses HT code-blocks (HTJ2K), outside the JPX baseline §6.2.8.3
    ///         admits through ISO 32000-1 §7.4.9 and T.801 M.9.2.3 (PDF/A-2 and -3), and that
    ///         ISO 32000-2 §7.4.9 itself limits PDF 2.0 to (PDF/A-4)
    ///   A30 — No LZW compression, in a stream or an inline image (every part; rules
    ///         6.1.10-1/-2, 6.1.7.2-1 and 6.1.10-1, 6.1.6.2-1 and 6.1.9-1)
    ///   A31 — A filter outside ISO 32000's standard set, or a Crypt filter not naming Identity in
    ///         a stream, or any Crypt in an inline image (PDF/A-2, -3 and -4; the same rules)
    ///   A32 — The file header begins at byte 0 and reads %PDF-d.d (PDF/A-1), or %PDF-1.n with n
    ///         from 0 to 7 alone on its line (PDF/A-2, -3; rule 6.1.2-1)
    ///   A33 — The header is followed by one EOL marker and a comment of % and four bytes above 127
    ///         (every part; rule 6.1.2-2)
    ///   A34 — An XMP property in no predefined schema and no extension schema the packet describes
    ///         (PDF/A-1 against the XMP Specification of January 2004, ISO 19005-1 6.7.9; PDF/A-2 and -3
    ///         against June 2005's, 19005-2/3 6.6.2.3.1)
    ///   A35 — An XMP property whose value is not of the type its schema declares (the same clauses)
    ///   A36 — An extension schema description that is not well formed: a field missing, not text, not
    ///         its value type's, or not under the required prefix; a value type no one defines
    ///         (ISO 19005-1 6.7.8; 19005-2/3 6.6.2.3.2 and 6.6.2.3.3)
    ///   A37 — An annotation type the part does not permit (ISO 19005-1 6.5.2; 19005-2/3/4 6.3.1)
    ///   A38 — An annotation's /F missing, not printing, or hiding it (19005-1 6.5.3; 19005-2/3/4 6.3.2)
    ///   A39 — An annotation appearance the part does not admit: none where one is required, an /AP
    ///         beyond /N, an /N of the wrong kind (19005-1 6.5.3; 19005-2/3/4 6.3.3)
    ///   A40 — A blend mode ISO 32000 does not define (19005-2/3 6.2.10; 19005-4 6.2.9, annotations too)
    ///   A41 — A page with transparency, no PDF/A output intent, and no page group /CS (the same clauses)
    ///   A42 — The trailer (the last, or a linearized file's first-page one) lacks a non-empty /ID
    ///   A43 — More than one EOL marker follows the last %%EOF
    ///   A44 — The xref keyword not followed by one EOL; in PDF/A-1 a subsection header not one space
    ///   A45 — An indirect object's header or endobj not spaced by single white-space and EOL markers
    ///   A46 — PDF/A-4: a trailer /Info without a catalog /PieceInfo, or one holding more than /ModDate
    ///   A47 — An output intent's profile not a prtr or mntr profile of RGB, CMYK or GRAY below
    ///         version 3 (PDF/A-1, 6.2.2) or 5 (PDF/A-2 to -4, 6.2.3); A15 every part's too
    ///   A48 — /DestOutputProfileRef in a PDF/X output intent (PDF/A-2, -3) or any (PDF/A-4)
    ///   A49 — PDF/A-4: more than one PDF/A output intent in an /OutputIntents array
    ///   A50 — A content stream operator ISO 32000 does not define (every part)
    ///   A51 — A rendering intent other than the four (every part)
    ///   A52 — PDF/A-2 onwards: a stream naming a resource without a resource dictionary of its own
    ///   A53 — PDF/A-4: a stream naming a resource its resources do not define
    ///   A54 — PDF/A-2 onwards: a DeviceN spot colorant without a /Colorants entry
    ///   A55 — PDF/A-2 onwards: Separations of one name with different tint transforms or alternates
    ///   A56 — A transfer function: /TR, or /TR2 other than /Default (every part)
    ///   A57 — PDF/A-2 onwards: /HTP (/HTO), a halftone not of type 1 or 5, a /HalftoneName, a
    ///         /TransferFunction where ISO 32000-1 has none or none where it requires one (PDF/A-2, -3)
    ///   A58 — An image with /Alternates, /OPI, /Interpolate true (an inline /I), or a depth the part
    ///         does not admit (1, 2, 4, 8; 16 from PDF/A-2; an image mask's or a /Mask's 1)
    ///   A59 — A form with /OPI or /Ref; before PDF/A-4 a /PS, a /Subtype2 /PS, a PostScript XObject
    ///   A60 — /AcroForm /NeedAppearances true (every part)
    ///   A61 — PDF/A-2 onwards: /Perms beyond /UR3 and /DocMDP (/DocMDP alone in PDF/A-4); in PDF/A-2
    ///         and -3 a DocMDP signature reference with /DigestLocation, /DigestMethod or /DigestValue
    ///   A62 — PDF/A-4: a catalog /Version that does not read 2.d
    ///   A63 — PDF/A-2 onwards: /Names /AlternatePresentations, or a page's /PresSteps
    ///   A64 — The XMP packet header's bytes or encoding attribute (every part)
    ///   A65 — PDF/A-1 to -3: an object or a drawn operand beyond the part's implementation limits —
    ///         integers, reals, strings, names, PDF/A-1's arrays and dictionaries, the object count,
    ///         q nesting, DeviceN colorants
    ///   A66 — PDF/A-2 and -3: a page boundary smaller than 3 or larger than 14 400 units
    ///   A67 — PDF/A-2 onwards: an embedded file's specification without /F and /UF; from PDF/A-3 a
    ///         stream /Subtype that is not a MIME type; in PDF/A-3 no /AFRelationship or no /AF
    ///         array naming it; PDF/A-4f's name dictionary without /EmbeddedFiles
    ///   A68 — PDF/A-2 and PDF/A-4 (not -4e, -4f): an embedded file that is not a conforming PDF/A
    ///         file of a part the part admits
    ///   A69 — PDF/A-1: the catalog's /OCProperties
    ///   A73 — Level A of PDF/A-1 to -3: no /MarkInfo, or one whose /Marked is not true
    ///   A74 — Level A of PDF/A-1 to -3: no /StructTreeRoot
    ///   A75 — Level A of PDF/A-1 to -3: a non-standard structure type not mapped to a standard one,
    ///         a circular mapping, or (PDF/A-2, -3) a standard type remapped
    ///   A77 — Level A of PDF/A-1, levels A and U of PDF/A-2 and -3: a font used for rendering that
    ///         does not map its codes to Unicode
    ///   A78 — PDF/A-4: an /ActualText holding a Private Use code point
    ///   A79 — PDF/A-2 onwards: an XMP metadata stream not encoded as UTF-8
    ///   A80 — A TrueType font used for rendering against the part's TrueType clause: encoding,
    ///         /Differences, the embedded program's cmap
    ///   A81 — An embedded CIDFontType2 whose /CIDToGIDMap is neither /Identity nor a stream
    ///   A82 — Levels A and U of PDF/A-2 and -3, all of PDF/A-4: a /ToUnicode value of U+0000, U+FEFF
    ///         or U+FFFE
    ///   A87 — PDF/A-2 and -3: a font's, a colourant's or a structure type's name whose bytes are not
    ///         UTF-8 (6.1.8, rule 6.1.8-1)
    ///   A76 — A stream whose /Length is not its data's, with /F, /FFilter or /FDecodeParms, or
    ///         (before PDF/A-4) whose stream keyword is not followed by CRLF or LF or endstream not
    ///         preceded by an EOL
    ///   A70 — PDF/A-2 onwards: an optional content configuration without a /Name (an empty one
    ///         before PDF/A-4), or two sharing one
    ///   A71 — PDF/A-2 onwards: a configuration's /Order that does not name every group /OCGs lists
    ///   A72 — PDF/A-2 and -3: a configuration with an /AS entry
    ///
    /// The action rules are the published validation profiles' (see CheckActions), each message citing
    /// the ISO clause and the rule id.
    ///
    /// PDF/A-4 specific (ISO 19005-4, based on PDF 2.0):
    ///   A4-01 — The file header begins at byte 0 and reads %PDF-2.n, n from 0 to 9, alone on its
    ///         line (rule 6.1.2-1)
    ///   A4-02 — /NeedsRendering true in the document catalog (PDF/A-2 and -3 too)
    ///   A4-03 — a /Requirements key in the document catalog (PDF/A-2 and -3 too)
    ///   A4-04 — XMP metadata must include pdfaid:rev="2020"
    ///   A4-05 — Embedded files must have an /AFRelationship key
    ///   A4-06 — A name whose bytes are not UTF-8, in an object or a drawn operand (6.1.7, Name
    ///         objects; ISO 32000-2 §7.3.5)
    ///
    /// A99 — a check that did not run in full. Never a violation: it lands on
    /// <see cref="PdfAValidationResult.Informational"/> and leaves IsConformant alone, as X99 does.
    /// </summary>
    public static partial class PdfAValidator
    {
        // [ThreadStatic] so concurrent validations on a server host cannot read each other's
        // diagnostic (CLAUDE.md, "Diagnostics Pattern").
        [ThreadStatic] private static string _lastValidationError;

        /// <summary>
        /// Describes a recoverable problem this thread hit while validating — a traversal guard that
        /// stopped a walk early, a stream that would not decode. <see langword="null"/> when the last
        /// validation on this thread ran to completion.
        /// </summary>
        /// <remarks>
        /// It holds <b>one</b> message. <see cref="PdfAValidationResult.Informational"/> is the
        /// complete record — one A99 entry per check that did not run in full — and is what to read
        /// before treating <see cref="PdfAValidationResult.IsConformant"/> as a verdict. It is reset at
        /// the start of every validation, on every path.
        /// </remarks>
        public static string LastValidationError => _lastValidationError;

        private static void Report(string operation, string detail)
            => _lastValidationError = operation + ": " + detail;

        /// <summary>The code of an informational entry saying a check did not run in full.</summary>
        internal const string PartialCheckCode = "A99";

        // The A99 entries of the validation running on this thread. Set up and taken down by
        // ValidateDocument; [ThreadStatic] for the reason the slot above is.
        [ThreadStatic] private static List<PdfAViolation> _partialChecks;

        /// <summary>
        /// Records that a check did not run in full: an A99 entry on the result, naming the checks
        /// in <paramref name="concerns"/>.
        /// </summary>
        /// <remarks>
        /// <b>A partial check reports no violation</b>, so without this entry a document whose walk
        /// was cut short validated exactly like one that was clean — and <see cref="PdfAConverter"/>
        /// stamped <c>pdfaid:part</c> on it. One entry per message: a budget spent on one page is
        /// seen again at the first entry of every page after it, and that is one fact, not many.
        /// </remarks>
        private static void ReportPartial(string message, params string[] concerns)
        {
            var list = _partialChecks;
            if (list == null) return;
            foreach (var entry in list)
                if (entry.Message == message) return;
            list.Add(new PdfAViolation(PartialCheckCode, message, concerns));
        }

        public static PdfAValidationResult Validate(string pdfPath,
            PdfAConformancePart part = PdfAConformancePart.Part1)
        {
            using (var doc = PdfDocument.Open(pdfPath))
                return ValidateDocument(doc, part);
        }

        public static PdfAValidationResult Validate(Stream input,
            PdfAConformancePart part = PdfAConformancePart.Part1)
        {
            using (var doc = PdfDocument.Open(input))
                return ValidateDocument(doc, part);
        }

        /// <summary>
        /// Validates the file at <paramref name="pdfPath"/> against <paramref name="part"/> at
        /// <paramref name="level"/> — the declared level, or one the caller requires.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is not a level of <paramref name="part"/>.</exception>
        public static PdfAValidationResult Validate(string pdfPath, PdfAConformancePart part, PdfAConformanceLevel level)
        {
            RequireLevelOf(part, level);
            using (var doc = PdfDocument.Open(pdfPath))
                return ValidateDocument(doc, part, level);
        }

        /// <summary>
        /// Validates <paramref name="input"/> against <paramref name="part"/> at
        /// <paramref name="level"/> — the declared level, or one the caller requires.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is not a level of <paramref name="part"/>.</exception>
        public static PdfAValidationResult Validate(Stream input, PdfAConformancePart part, PdfAConformanceLevel level)
        {
            RequireLevelOf(part, level);
            using (var doc = PdfDocument.Open(input))
                return ValidateDocument(doc, part, level);
        }

        /// <summary>The <c>pdfaid:conformance</c> letter <paramref name="level"/> stands for; null for <see cref="PdfAConformanceLevel.None"/>.</summary>
        internal static string LetterOf(PdfAConformanceLevel level)
            => level == PdfAConformanceLevel.None || level == PdfAConformanceLevel.Declared ? null : level.ToString();

        /// <summary>
        /// The <c>pdfaid:conformance</c> values a file judged at <paramref name="level"/> may declare —
        /// <see langword="null"/> standing for none — the higher levels of the part included.
        /// </summary>
        private static string[] AdmittedLetters(PdfAConformancePart part, PdfAConformanceLevel level)
        {
            bool unicode = part == PdfAConformancePart.Part2 || part == PdfAConformancePart.Part3;
            switch (level)
            {
                case PdfAConformanceLevel.B: return unicode ? new[] { "B", "U", "A" } : new[] { "B", "A" };
                case PdfAConformanceLevel.U: return new[] { "U", "A" };
                case PdfAConformanceLevel.None: return new string[] { null };
                default: return new[] { LetterOf(level) };
            }
        }

        internal static void RequireLevelOf(PdfAConformancePart part, PdfAConformanceLevel level)
        {
            bool admitted;
            switch (level)
            {
                case PdfAConformanceLevel.Declared: admitted = true; break;
                case PdfAConformanceLevel.A:
                case PdfAConformanceLevel.B: admitted = part != PdfAConformancePart.Part4; break;
                case PdfAConformanceLevel.U: admitted = part == PdfAConformancePart.Part2 || part == PdfAConformancePart.Part3; break;
                case PdfAConformanceLevel.None:
                case PdfAConformanceLevel.E:
                case PdfAConformanceLevel.F: admitted = part == PdfAConformancePart.Part4; break;
                default: admitted = false; break;
            }
            if (!admitted)
                throw new ArgumentOutOfRangeException(nameof(level), level,
                    "PDF/A-" + ((int)part).ToString(System.Globalization.CultureInfo.InvariantCulture) + " has no conformance level " + level + ".");
        }

        // ------------------------------------------------------------------

        private static PdfAValidationResult ValidateDocument(PdfDocument doc,
            PdfAConformancePart part = PdfAConformancePart.Part1,
            PdfAConformanceLevel level = PdfAConformanceLevel.Declared)
        {
            // Reset first, on every path: a guard reported by the PREVIOUS document on this thread
            // would otherwise be read as this one's, which is the per-document-scope trap CLAUDE.md
            // records for PdfDecryptor.LastPermsValidation.
            _lastValidationError = null;
            var partial = new List<PdfAViolation>();
            _partialChecks = partial;
            try
            {
                using (ScanReuse.Begin())
                    return ValidateChecks(doc, part, level, partial);
            }
            finally
            {
                _partialChecks = null;
            }
        }

        private static PdfAValidationResult ValidateChecks(
            PdfDocument doc, PdfAConformancePart part, PdfAConformanceLevel level, List<PdfAViolation> partial)
        {
            var violations = new List<PdfAViolation>();

            CheckEncryption(doc, violations);

            if (doc.IsEncrypted)
            {
                // Cannot inspect the document further without decrypting it. A01 is already a
                // violation, so nothing here passes as clean and no A99 is needed.
                return new PdfAValidationResult(violations, partial);
            }

            CheckFileHeader(doc, part, violations);
            CheckFileStructure(doc, part, violations);
            CheckStreams(doc, part, violations);

            var identification = ReadIdentification(doc);
            // The letter every level-specific rule reads: the declared one, or the one required.
            string conformance = level == PdfAConformanceLevel.Declared ? identification?.Conformance : LetterOf(level);
            CheckActions(doc, part, conformance, violations);
            CheckLzw(doc, part, violations);
            // PDF/A-1 forbids embedded files (A03). PDF/A-2 admits conforming PDF/A-1 and -2 files,
            // PDF/A-3 any file, PDF/A-4 PDF/A-1, -2 and -4 files (-4e and -4f any): A67-A68, and
            // PDF/A-4's /AFRelationship, A4-05.
            if (part == PdfAConformancePart.Part1)
                CheckEmbeddedFiles(doc, violations);
            CheckEmbeddedFileSpecifications(doc, part, conformance, violations);
            CheckFontEmbedding(doc, violations);
            // A09-A14 — real transparency. Part 1 only: Parts 2/3/4 are based on PDF 1.7/2.0 and
            // permit the transparent imaging model outright (ISO 19005-2/3's own transparency rule,
            // 6.2.10-2, is conditional on a MISSING OutputIntent, which this validator's own A08
            // already treats as a separate, unconditional requirement).
            if (part == PdfAConformancePart.Part1)
            {
                CheckTransparency(doc, violations);
                CheckIccBasedComponentCounts(doc, violations);
            }
            // A19: every part (ISO 19005-1 Cor. 1 6.3.6, 19005-2/3 6.2.11.5, 19005-4 6.2.10.5).
            CheckGlyphWidthConsistency(doc, part, violations);
            CheckColourConsistency(doc, violations, part);
            CheckXmpMetadata(identification, violations, part, level);
            CheckXmpProperties(identification, violations, part);
            CheckAnnotations(doc, part, conformance, violations);
            CheckBlending(doc, part, violations);
            CheckContentStreams(doc, part, violations);
            CheckSpotColours(doc, part, violations);
            CheckGraphicsStates(doc, part, violations);
            CheckXObjects(doc, part, violations);
            CheckDocumentLevel(doc, part, identification, conformance, violations);
            CheckLevelAStructure(doc, part, conformance, violations);
            CheckFontUnicodeMapping(doc, part, conformance, violations);
            // A86 before A80-A84: they share LastValidationError's one slot, and the narrower statement
            // of a font none of them could read — A83's — is the one left in it.
            CheckNotdefReferences(doc, part, violations);
            CheckFontDictionaries(doc, part, conformance, violations);
            CheckActualText(doc, part, violations);
            CheckImplementationLimits(doc, part, violations);
            CheckOutputIntentProfile(doc, violations);
            CheckOutputIntents(doc, part, violations);
            CheckOptionalContent(doc, part, violations);
            // PDF/A-1 is PDF 1.4, which has neither JPXDecode nor these two catalog keys.
            if (part != PdfAConformancePart.Part1)
            {
                // A24-A29: ISO 19005-2/3 §6.2.8.3, ISO 19005-4 §6.2.7.3.
                CheckJpxImages(doc, part, violations);
                CheckNeedsRendering(doc, part, violations);
                CheckRequirementsArray(doc, part, violations);
            }

            // PDF/A-4 specific checks
            if (part == PdfAConformancePart.Part4)
                CheckEmbeddedFilesAfRelationship(doc, violations);

            string annotationsCut = ScanReuse.AnnotationsCutNotice("the annotation, action, embedded-file and content checks");
            if (annotationsCut != null) ReportPartial(annotationsCut, "A37", "A38", "A39", "A40", "A67");

            return new PdfAValidationResult(violations, partial);
        }

        /// <summary>
        /// A08 — a <c>GTS_PDFA1</c> output intent must embed its ICC profile.
        /// </summary>
        /// <remarks>
        /// <para>ISO 19005-1 §6.2.2: the output intent's <c>/DestOutputProfile</c> is required.
        /// The validator did not check it, and <see cref="PdfAConverter"/>'s own documentation
        /// said so out loud — which meant the converter could emit an intent with no profile and
        /// the validator would call the result conformant. A false pass, from two components in
        /// the same assembly disagreeing about the same clause.</para>
        /// <para>Scope is deliberately narrow: this reports an output intent that is *present*
        /// and *incomplete*. Whether a document is required to carry one at all depends on what
        /// colour spaces it actually uses, which needs the content-level colour analysis this
        /// validator does not do — so a document with no <c>/OutputIntents</c> is not reported
        /// here rather than being guessed at.</para>
        /// </remarks>
        private static void CheckOutputIntentProfile(PdfDocument doc, List<PdfAViolation> v)
        {
            var intents = doc.ResolveObject(doc.Catalog?.Dictionary?[PdfName.OutputIntents]) as PdfArray;
            if (intents == null) return;

            for (int i = 0; i < intents.Count; i++)
            {
                if (!(doc.ResolveObject(intents[i]) is PdfDictionary intent)) continue;

                // Only the PDF/A intent is governed by §6.2.2; a file may legitimately carry
                // others (a GTS_PDFX intent, for one) alongside it.
                string subtype = (doc.ResolveObject(intent[PdfName.S]) as PdfName)?.Value;
                if (subtype != "GTS_PDFA1") continue;

                if (doc.ResolveObject(intent[PdfName.DestOutputProfile]) is PdfStream) continue;

                v.Add(new PdfAViolation("A08",
                    "The GTS_PDFA1 output intent has no embedded /DestOutputProfile ICC profile " +
                    "(ISO 19005-1 §6.2.2)."));
                return; // one report is enough; a second intent is a different defect
            }
        }

        // A01 — encryption
        private static void CheckEncryption(PdfDocument doc, List<PdfAViolation> v)
        {
            if (doc.IsEncrypted)
                v.Add(new PdfAViolation("A01",
                    "PDF/A-1b forbids encryption. The document contains an /Encrypt dictionary."));
        }

        // A03 — embedded files
        private static void CheckEmbeddedFiles(PdfDocument doc, List<PdfAViolation> v)
        {
            var catalog = doc.Catalog?.Dictionary;
            if (catalog == null) return;

            var namesObj = catalog[PdfName.Names];
            if (namesObj != null)
            {
                var namesDict = doc.ResolveObject(namesObj) as PdfDictionary;
                if (namesDict != null && namesDict[PdfName.EmbeddedFiles] != null)
                    v.Add(new PdfAViolation("A03",
                        "PDF/A-1b forbids embedded files. Found /Names /EmbeddedFiles in the document catalog."));
            }
        }

        // A06 — font embedding, of the fonts used for rendering (ISO 19005-1 6.3.4, 19005-2/3
        // 6.2.11.4.1, 19005-4 6.2.10.4.1): a font named but never painted, or painted only in text
        // rendering mode 3, needs no program — PdfUaFontUse.PaintedFonts, the one answer UA25 reads
        // too; null from it (content that could not be read) leaves every named font in scope.
        private static void CheckFontEmbedding(PdfDocument doc, List<PdfAViolation> v)
        {
            var unembedded = new List<(PdfDictionary Font, string Label)>();
            // The fonts every content stream a page draws can name: the page's resources and, through
            // PdfXValidator.ScanPage — the one walk X04 reads — those of its form XObjects, tiling
            // patterns, Type 3 glyph procedures, soft-mask groups and annotation appearance streams.
            // A walk that followed /XObject entries alone never saw a font painted only by a
            // FreeText's appearance, and PdfAConverter stamped the file. A resource dictionary the
            // pages share is read once (ScanReuse.FirstVisit); the budget bounds the font entries read.
            var seenResources = new HashSet<PdfDictionary>();
            var seenFonts = new HashSet<PdfDictionary>();
            int budget = MaxResourceEntries;
            long documentBytes = 0;

            for (int i = 0; i < doc.Pages.Count; i++)
            {
                var scan = PdfXValidator.ScanPage(doc, doc.Pages[i],
                    PdfXValidator.DefaultMaxPageContentBytes, ref documentBytes,
                    PdfXValidator.DefaultMaxDocumentContentBytes, collectContent: false);
                if (scan.Incomplete != null)
                    ReportPartial($"Page {i + 1}: the A06 font-embedding check did not run in full — {scan.Incomplete}.", "A06");

                foreach (var res in scan.Resources)
                {
                    if (!ScanReuse.FirstVisit(seenResources, res)) continue;
                    if (!(doc.ResolveObject(res[PdfName.Font]) is PdfDictionary fontDict)) continue;
                    foreach (var kvp in fontDict)
                    {
                        if (budget <= 0)
                        {
                            ReportFontWalkGuard($"resource traversal stopped after {MaxResourceEntries} entries");
                            goto Judge;
                        }
                        budget--;
                        if (doc.ResolveObject(kvp.Value) is PdfDictionary font && seenFonts.Add(font))
                            CheckFontEmbedded(font, doc, unembedded);
                    }
                }
            }

            Judge:
            if (unembedded.Count == 0) return;
            HashSet<PdfDictionary> painted = PdfUaFontUse.PaintedFonts(doc,
                PdfXValidator.DefaultMaxPageContentBytes, PdfXValidator.DefaultMaxDocumentContentBytes);
            if (PdfUaFontUse.LastError != null)
                Report("PdfAValidator.CheckFontEmbedding", PdfUaFontUse.LastError);
            var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (font, label) in unembedded)
                if ((painted == null || painted.Contains(font)) && reported.Add(label))
                    v.Add(new PdfAViolation("A06",
                        $"PDF/A-1b requires all fonts to be embedded. Font not embedded: {label}"));
        }

        /// <summary>
        /// Resource-dictionary entries examined before the A06 and A09-A14 walks give up. Mirrors
        /// <c>PdfUaChecker</c>'s ceiling of the same name, and is spent per <i>entry</i> rather than
        /// per node for the reason stated there.
        /// </summary>
        private const int MaxResourceEntries = 200_000;

        private static void ReportFontWalkGuard(string detail)
            => ReportResourceGuard("PdfAValidator.CollectUnembeddedFonts", "A06 font-embedding",
                detail, "A06");

        private static void ReportTransparencyWalkGuard(string detail)
            => ReportResourceGuard("PdfAValidator.CheckResourcesForTransparency", "A09-A14 transparency",
                detail, "A09", "A10", "A11", "A12", "A13", "A14");

        /// <summary>
        /// A resource walk stopped by its depth cap or budget: the slot, and an A99 naming
        /// <paramref name="check"/>.
        /// </summary>
        /// <remarks>
        /// The check is a parameter because two walks share this. Written into the message, the
        /// transparency walk running out would report <i>"the A06 font-embedding check is
        /// partial"</i> — naming the wrong check, and one that had in fact run.
        /// </remarks>
        private static void ReportResourceGuard(string operation, string check, string detail,
            params string[] concerns)
        {
            Report(operation, $"{detail}; the {check} check is partial");
            ReportPartial($"The {check} check did not run in full — {detail}.", concerns);
        }

        /// <summary>
        /// A06's half of the shared embedding walk. Standard-14 fonts get no exemption here:
        /// PDF/A-1b requires even them to be embedded, unlike ISO 32000-1 §9.6.2.2.
        /// </summary>
        /// <remarks>
        /// One traversal, in <see cref="FontEmbeddingProbe"/>, shared with PDF/UA and PDF/X; its
        /// remarks give the edges (a Type 3 font, an unresolved <c>/DescendantFonts</c>, no
        /// <c>/Subtype</c>) where a private copy would answer wrongly.
        /// </remarks>
        private static void CheckFontEmbedded(
            PdfDictionary fontDict, PdfDocument doc, List<(PdfDictionary Font, string Label)> unembedded)
            => FontEmbeddingProbe.ReportUnembedded(fontDict, doc, name => unembedded.Add((fontDict, name)));

        // A09-A14 — real transparency (ISO 19005-1:2005 §6.4; rules 6.4-1..6.4-6).
        //
        // "Transparency flattening" is sometimes framed as a rendering-infrastructure problem
        // (composite the whole page down to opaque content, the way a distiller would). PDF/A-1's
        // actual normative text asks a much narrower question: does the document contain any of
        // six specific transparency constructs at all. That is a dictionary/resource walk, not a
        // rendering problem — a walk over every resource dictionary a page's content reaches:
        // PdfXValidator.ScanPage's, the one X09 reads, so an /ExtGState inside a highlight's
        // appearance stream, a tiling pattern, a Type 3 glyph procedure or a soft-mask group is
        // examined as the page's own are (a walk that followed /XObject entries alone saw none of
        // them), and each dictionary the pages share is read once.
        private static void CheckTransparency(PdfDocument doc, List<PdfAViolation> v)
        {
            var seenResources = new HashSet<PdfDictionary>();
            var seenXObjects = new HashSet<PdfStream>();
            int budget = MaxResourceEntries;
            long documentBytes = 0;

            for (int i = 0; i < doc.Pages.Count; i++)
            {
                var page = doc.Pages[i];
                // §11.4.7: a page dictionary may itself declare a transparency group, not only a
                // Form XObject.
                CheckGroupForTransparency(page.Dictionary, doc, v);

                var scan = PdfXValidator.ScanPage(doc, page,
                    PdfXValidator.DefaultMaxPageContentBytes, ref documentBytes,
                    PdfXValidator.DefaultMaxDocumentContentBytes, collectContent: false);
                if (scan.Incomplete != null)
                    ReportPartial($"Page {i + 1}: the A09-A14 transparency checks did not run in full — {scan.Incomplete}.",
                        "A09", "A10", "A11", "A12", "A13", "A14");

                // An annotation appearance is a form XObject reached from /Annots, not from any
                // /XObject entry: its own /SMask and /Group are read here, its resources below.
                foreach (var ap in scan.AppearanceStreams)
                    if (seenXObjects.Add(ap)) CheckXObjectForTransparency(ap, doc, v);

                foreach (var res in scan.Resources)
                {
                    if (!ScanReuse.FirstVisit(seenResources, res)) continue;

                    if (doc.ResolveObject(res[PdfName.ExtGState]) is PdfDictionary extGStateDict)
                    {
                        foreach (var kvp in extGStateDict)
                        {
                            if (budget <= 0)
                            {
                                ReportTransparencyWalkGuard($"resource traversal stopped after {MaxResourceEntries} entries");
                                return;
                            }
                            budget--;
                            if (doc.ResolveObject(kvp.Value) is PdfDictionary gs) CheckExtGStateForTransparency(gs, doc, v);
                        }
                    }

                    if (doc.ResolveObject(res[PdfName.XObject]) is PdfDictionary xobjDict)
                    {
                        foreach (var kvp in xobjDict)
                        {
                            if (budget <= 0)
                            {
                                ReportTransparencyWalkGuard($"resource traversal stopped after {MaxResourceEntries} entries");
                                return;
                            }
                            budget--;
                            if (doc.ResolveObject(kvp.Value) is PdfStream xobj && seenXObjects.Add(xobj))
                                CheckXObjectForTransparency(xobj, doc, v);
                        }
                    }
                }
            }
        }

        /// <summary>One XObject's own transparency: its <c>/SMask</c> (A10) and, for a form, its <c>/Group</c> (A11).</summary>
        private static void CheckXObjectForTransparency(PdfStream xobj, PdfDocument doc, List<PdfAViolation> v)
        {
            // Table 89: an image XObject's own /SMask, unlike an ExtGState's — the two are
            // different transparency mechanisms (a soft-mask image bound to one particular
            // drawing of the image, versus a group result bound into the graphics state),
            // and ISO 19005-1 §6.4 / rule 6.4-2 forbids this one outright.
            if (xobj[PdfName.SMask] != null)
                v.Add(new PdfAViolation("A10",
                    "PDF/A-1b forbids an /SMask entry on an XObject (ISO 19005-1 §6.4, " +
                    "rule 6.4-2)."));

            if ((doc.ResolveObject(xobj[PdfName.Subtype]) as PdfName)?.Value == "Form")
                CheckGroupForTransparency(xobj, doc, v);
        }

        private static void CheckGroupForTransparency(PdfDictionary owner, PdfDocument doc, List<PdfAViolation> v)
        {
            if (owner == null) return;
            var group = doc.ResolveObject(owner[PdfName.Group]) as PdfDictionary;
            if (group == null) return;

            var s = doc.ResolveObject(group[PdfName.S]) as PdfName;
            if (s?.Value == "Transparency")
                v.Add(new PdfAViolation("A11",
                    "PDF/A-1b forbids a transparency group (/Group /S /Transparency, ISO 19005-1 " +
                    "§6.4, rule 6.4-3)."));
        }

        private static void CheckExtGStateForTransparency(PdfDictionary gs, PdfDocument doc, List<PdfAViolation> v)
        {
            var smask = doc.ResolveObject(gs[PdfName.SMask]);
            if (smask != null && !(smask is PdfName smaskName && smaskName.Value == "None"))
                v.Add(new PdfAViolation("A09",
                    "PDF/A-1b requires /SMask to be /None or absent in an /ExtGState dictionary " +
                    "(ISO 19005-1 §6.4, rule 6.4-1)."));

            var bm = doc.ResolveObject(gs[PdfName.BM]);
            if (bm != null && !IsAllowedBlendMode(bm))
                v.Add(new PdfAViolation("A12",
                    "PDF/A-1b requires /BM to be /Normal or /Compatible when present (ISO 19005-1 " +
                    "§6.4, rule 6.4-4)."));

            // Absent means the ISO 32000-1 §8.4.5 default of 1.0 (fully opaque), which is
            // conformant — only an explicit value away from 1.0 is a violation.
            if (doc.ResolveObject(gs[PdfName.CA]) is PdfNumber ca && !IsOpaqueAlpha(ca.DoubleValue))
                v.Add(new PdfAViolation("A13",
                    "PDF/A-1b requires /CA (stroking alpha) to be 1.0 when present (ISO 19005-1 " +
                    "§6.4, rule 6.4-5)."));

            if (doc.ResolveObject(gs[PdfName.ca]) is PdfNumber caLower && !IsOpaqueAlpha(caLower.DoubleValue))
                v.Add(new PdfAViolation("A14",
                    "PDF/A-1b requires /ca (non-stroking alpha) to be 1.0 when present (ISO 19005-1 " +
                    "§6.4, rule 6.4-6)."));
        }

        /// <summary>
        /// Whether an alpha value is 1.0 as the rules 6.4-5 and 6.4-6 read it: within 10<sup>-6</sup>,
        /// exclusive (<c>CA - 1.0 &lt; 0.000001 &amp;&amp; CA - 1.0 &gt; -0.000001</c>). A writer's
        /// <c>1.0000001</c> or <c>0.9999999</c> is opaque — the conformance corpus passes both
        /// (6-4-t03-pass-b).
        /// </summary>
        private static bool IsOpaqueAlpha(double alpha)
            => alpha - 1.0 < AlphaTolerance && alpha - 1.0 > -AlphaTolerance;

        private const double AlphaTolerance = 0.000001;

        /// <summary>A single name is the common case; ISO 32000-1 Table 136 also permits an array
        /// naming several blend modes in preference order, and every one of them must be allowed
        /// for the whole value to be — a reader picks the first it supports, and any entry could be
        /// that one.</summary>
        private static bool IsAllowedBlendMode(PdfObject bm)
        {
            if (bm is PdfName n) return n.Value == "Normal" || n.Value == "Compatible";

            if (bm is PdfArray arr)
            {
                if (arr.Count == 0) return false;
                for (int i = 0; i < arr.Count; i++)
                {
                    if (!(arr[i] is PdfName an) || (an.Value != "Normal" && an.Value != "Compatible"))
                        return false;
                }
                return true;
            }

            return false;
        }

        // A15-A18 — output-intent colour consistency (ISO 19005-1:2005 §6.2.2/§6.2.3.3;
        // rules 6.2.2-2, 6.2.3.3-1/2/3). A15 is Part 1's alone; A16-A18 every part's (below).
        //
        // "Colour conversion" sounds like it needs a CMM; these four rules do not. They ask
        // whether device-dependent colour usage is *consistent* with the OutputIntent's declared
        // class, which — like A09-A14 — is a scan, not a transform. The scan itself is
        // PdfXValidator.ScanPage (widened from private to internal for exactly this reuse) rather
        // than a second copy of its 380-line resource/content walk; what is new is the reading of
        // its streams (CollectPaintedDeviceFamilies): only what the page draws, each colour remapped
        // through the Default colour spaces of the stream that paints it, because PDF/A's question
        // ("which device family is painted, if any") differs from PDF/X-1a's ("is this specific
        // space present").
        //
        // PDF/A-2, -3 and -4 ask the same of the colours a page paints (ISO 19005-2/3/4 6.2.4.3;
        // rules 6.2.4.3-2/-3/-4), and count a transparency group's blending space as used too; PDF/A-4
        // judges each page against its own PDF/A output intent where it has one, and lets a page whose
        // blending space is an RGB (or CMYK) ICC space, or CalRGB, use DeviceRGB (or DeviceCMYK).
        private static void CheckColourConsistency(PdfDocument doc, List<PdfAViolation> v,
            PdfAConformancePart part = PdfAConformancePart.Part1)
        {
            bool part1 = part == PdfAConformancePart.Part1;
            bool part4 = part == PdfAConformancePart.Part4;
            CheckMultipleOutputIntentsShareProfile(doc, v, part);

            int? documentIntent = GetPdfA1OutputIntentComponents(doc, doc.Catalog?.Dictionary);
            var pageIntentsAnswered = new Dictionary<object, int?>(PdfReferenceEquality.Instance);
            bool rgbWithout = false, cmykWithout = false, grayWithout = false;

            long documentBytes = 0;
            for (int i = 0; i < doc.Pages.Count; i++)
            {
                var painted = new DeviceFamilies();
                var scan = PdfXValidator.ScanPage(doc, doc.Pages[i],
                    PdfXValidator.DefaultMaxPageContentBytes, ref documentBytes,
                    PdfXValidator.DefaultMaxDocumentContentBytes);

                if (scan.Incomplete != null)
                {
                    // An A99 per page, as X99 is: "found nothing" on a partial scan is not "there
                    // is nothing". PdfAConverter does not judge A16-A18 before writing its output
                    // intent, so it sets this entry aside the way it sets those codes aside.
                    ReportPartial($"Page {i + 1}: the A16-A18 colour-consistency check did not run " +
                                  $"in full — {scan.Incomplete}.", "A16", "A17", "A18");

                    // The slot holds one message, and the resource walks that run first give a more
                    // specific one for the same oversized dictionary — so this fills it only when
                    // it is still empty. Informational has every entry either way.
                    if (_lastValidationError == null)
                        Report("PdfAValidator.CheckColourConsistency",
                            $"page {i + 1}'s colour-space scan is partial ({scan.Incomplete}); " +
                            "A16-A18 may under-report for this document");
                }

                var pageDict = doc.Pages[i].Dictionary;
                var pageGroupSpace = Resolve<PdfDictionary>(doc, pageDict?[PdfName.Group])?[PdfName.CS];
                if (!part1)
                    ClassifyPainted(doc, pageGroupSpace,
                        Resolve<PdfDictionary>(doc, doc.Pages[i].Resources?[PdfName.ColorSpace]), 0, painted);
                CollectPaintedDeviceFamilies(doc, scan, painted, groupSpaces: !part1);

                int? intent = documentIntent;
                int? blending = null;
                if (part4)
                {
                    intent = GetPdfA1OutputIntentComponents(doc, pageDict, pageIntentsAnswered) ?? documentIntent;
                    blending = DeviceIndependentComponents(doc, pageGroupSpace);
                }

                rgbWithout |= painted.Rgb && intent != PdfColorSpaceNames.RgbComponents && blending != PdfColorSpaceNames.RgbComponents;
                cmykWithout |= painted.Cmyk && intent != PdfColorSpaceNames.CmykComponents && blending != PdfColorSpaceNames.CmykComponents;
                grayWithout |= painted.Gray && intent == null;

                // Once all three are already found, further pages cannot add anything this check reports.
                if (rgbWithout && cmykWithout && grayWithout) break;
            }

            string clause = part1 ? "ISO 19005-1 §6.2.3.3, rule 6.2.3.3" : $"ISO 19005-{(int)part} §6.2.4.3, rule 6.2.4.3";
            string level = part1 ? "PDF/A-1b" : $"PDF/A-{(int)part}";
            string orBlending = part4 ? ", or an RGB blending space," : "";
            if (rgbWithout)
                v.Add(new PdfAViolation("A16",
                    $"{level} requires an RGB-class OutputIntent{orBlending} when DeviceRGB is used " +
                    $"({clause}-{(part1 ? 1 : 2)})."));

            if (cmykWithout)
                v.Add(new PdfAViolation("A17",
                    $"{level} requires a CMYK-class OutputIntent{orBlending.Replace("RGB", "CMYK")} when DeviceCMYK is used " +
                    $"({clause}-{(part1 ? 2 : 3)})."));

            if (grayWithout)
                v.Add(new PdfAViolation("A18",
                    $"{level} requires an OutputIntent when an uncalibrated colour space " +
                    $"(DeviceGray) is used ({clause}-{(part1 ? 3 : 4)})."));
        }

        /// <summary>
        /// The component count of a device-independent blending space — an ICCBased space's
        /// <c>/N</c>, or 3 for CalRGB — or <see langword="null"/> for a device space or none.
        /// </summary>
        private static int? DeviceIndependentComponents(PdfDocument doc, PdfObject space)
        {
            if (!(doc.ResolveObject(space) is PdfArray array) || array.Count < 1) return null;
            string family = (doc.ResolveObject(array[0]) as PdfName)?.Value;
            if (family == PdfColorSpaceNames.CalRGB) return PdfColorSpaceNames.RgbComponents;
            if (family == PdfColorSpaceNames.ICCBased && array.Count > 1)
                return (doc.ResolveObject((doc.ResolveObject(array[1]) as PdfStream)?[PdfName.N]) as PdfNumber)?.IntValue;
            return null;
        }

        /// <summary>
        /// The <c>GTS_PDFA1</c> output intent's declared component count (1/3/4), or <c>null</c>
        /// when there is no such intent or its profile does not resolve — the same fail-safe
        /// direction <c>PdfXValidator.OutputIntentIsRgb</c> takes: an unreadable profile answers
        /// "not this class" rather than being treated as satisfying any rule.
        /// </summary>
        /// <remarks>
        /// <paramref name="answered"/>, when given, keeps each array's answer by its identity, so pages
        /// that share one array read it once, not once a page.
        /// </remarks>
        private static int? GetPdfA1OutputIntentComponents(PdfDocument doc, PdfDictionary owner,
            Dictionary<object, int?> answered = null)
        {
            var intents = doc.ResolveObject(owner?[PdfName.OutputIntents]) as PdfArray;
            if (intents == null) return null;
            if (answered != null && answered.TryGetValue(intents, out int? known)) return known;
            int? components = Pdfa1IntentComponents(doc, intents);
            if (answered != null) answered[intents] = components;
            return components;
        }

        /// <summary>
        /// How many times this thread has walked an <c>/OutputIntents</c> array for the colour and
        /// blending rules: the memo's evidence, since a page sharing an array answers the same either way.
        /// </summary>
        [ThreadStatic] internal static long OutputIntentArraysWalked;

        private static int? Pdfa1IntentComponents(PdfDocument doc, PdfArray intents)
        {
            OutputIntentArraysWalked++;
            for (int i = 0; i < intents.Count; i++)
            {
                if (!(doc.ResolveObject(intents[i]) is PdfDictionary intent)) continue;
                if ((doc.ResolveObject(intent[PdfName.S]) as PdfName)?.Value != "GTS_PDFA1") continue;

                var profile = doc.ResolveObject(intent[PdfName.DestOutputProfile]) as PdfStream;
                if (profile == null) return null;

                return (doc.ResolveObject(profile[PdfName.N]) as PdfNumber)?.IntValue;
            }

            return null;
        }

        /// <summary>A15 — rule 6.2.2-2: every /OutputIntents entry that carries a
        /// /DestOutputProfile must name the SAME indirect object, so a reader cannot land on two
        /// different destination profiles depending which entry it happened to read.</summary>
        private static void CheckMultipleOutputIntentsShareProfile(PdfDocument doc, List<PdfAViolation> v,
            PdfAConformancePart part = PdfAConformancePart.Part1)
        {
            var intents = doc.ResolveObject(doc.Catalog?.Dictionary?[PdfName.OutputIntents]) as PdfArray;
            if (intents == null || intents.Count < 2) return;

            PdfReference first = null;
            for (int i = 0; i < intents.Count; i++)
            {
                if (!(doc.ResolveObject(intents[i]) is PdfDictionary intent)) continue;
                if (!(intent[PdfName.DestOutputProfile] is PdfReference profileRef)) continue;

                if (first == null) { first = profileRef; continue; }
                if (!first.Equals(profileRef))
                {
                    v.Add(new PdfAViolation("A15", part == PdfAConformancePart.Part1
                        ? "PDF/A-1b requires every /OutputIntents entry that carries a " +
                          "/DestOutputProfile to name the same indirect object (ISO 19005-1 §6.2.2, " +
                          "rule 6.2.2-2)."
                        : $"PDF/A-{(int)part} requires every /OutputIntents entry that carries a " +
                          $"/DestOutputProfile to name the same indirect object (ISO 19005-{(int)part} §6.2.3, " +
                          "rule 6.2.3-2)."));
                    return;
                }
            }
        }

        /// <summary>
        /// A20 — ISO 19005-1:2005 rule 6.2.3.2-2 (the published profile's wording, the ISO text itself being
        /// unavailable — same accept-the-risk basis A15-A18 already document): "The number of
        /// colour components in the colour space described by the ICC profile data must match the
        /// number of components actually in the ICC profile" — N=1 for GRAY, N=3 for RGB or Lab,
        /// N=4 for CMYK. Every <c>[/ICCBased …]</c> array reachable from a page's <c>/ColorSpace</c>
        /// resources or an image XObject's own <c>/ColorSpace</c> is checked; each profile stream
        /// is checked once, however many places name it.
        /// </summary>
        /// <remarks>
        /// <para><b>Not rule 6.2.3.2-1</b>, which is a different, larger check (device class
        /// restricted to prtr/mntr/scnr/spac, connection space restricted to RGB/CMYK/GRAY/Lab, ICC
        /// version below 3.0) — GAPS.md names only the component-count half as the gap this closes.
        /// </para>
        /// <para><b>Fail-safe, the same direction as A15-A18's `GetPdfA1OutputIntentComponents`.</b>
        /// A profile this cannot parse at all (too short, wrong file signature, over
        /// <c>PdfLimits.MaxIccProfileBytes</c>) or whose data colour space is not one of ICC.1
        /// Table 19's sixteen signatures answers "cannot determine" from
        /// <c>IccProfileComponents.TryGetDataColorSpaceComponentCount</c> — and is skipped, not
        /// reported, because a component count this cannot itself verify is not evidence of a
        /// declared/actual mismatch.</para>
        /// <para><b>Reuses <c>PdfXValidator.ScanPage</c></b> and reads two resource sites from it
        /// (every reachable <c>/ColorSpace</c> resource dictionary, and an image XObject's own
        /// <c>/ColorSpace</c>) — not a third copy of the walk, and not the content-stream half, since
        /// <c>cs</c>/<c>CS</c> only ever names a resource (§8.6.8) and an inline <c>[/ICCBased …]</c>
        /// array is not legal there. A profile is checked whether or not anything is painted with it:
        /// a mismatched profile in the file is the finding, unlike A16–A18's device families, which
        /// count only what is drawn.</para>
        /// </remarks>
        private static void CheckIccBasedComponentCounts(PdfDocument doc, List<PdfAViolation> v)
        {
            // A performance guard, not a correctness one — the stop-at-the-first-finding return in
            // CheckOneIccBasedSpace already makes a mismatched profile reported once however many
            // places name it, dedup or not. What this avoids is re-decoding and re-parsing the SAME
            // conformant profile stream once per page on a large document that never mismatches, so
            // no early return ever cuts the walk short. Not independently testable through the
            // violation list for that reason — see the test named for exactly this distinction.
            var checkedProfiles = new HashSet<PdfStream>();
            var seenResources = new HashSet<PdfDictionary>();
            long documentBytes = 0;

            for (int i = 0; i < doc.Pages.Count; i++)
            {
                var scan = PdfXValidator.ScanPage(doc, doc.Pages[i],
                    PdfXValidator.DefaultMaxPageContentBytes, ref documentBytes,
                    PdfXValidator.DefaultMaxDocumentContentBytes, collectContent: false);

                // The scan's partial flag is read: a profile beyond the point it stopped is not
                // examined, and the result says so.
                if (scan.Incomplete != null)
                    ReportPartial($"Page {i + 1}: the A20-A21 ICC profile check did not run in full " +
                                  $"— {scan.Incomplete}.", "A20", "A21");

                foreach (var res in scan.Resources)
                {
                    if (!ScanReuse.FirstVisit(seenResources, res)) continue;
                    var csDict = doc.ResolveObject(res[PdfName.ColorSpace]) as PdfDictionary;
                    if (csDict != null)
                        foreach (var kvp in csDict)
                            if (CheckOneIccBasedSpace(doc, kvp.Value, checkedProfiles, v, i + 1))
                                return;

                    // A shading's own /ColorSpace (Table 78) is a colour space of the file as a resource's is.
                    if (doc.ResolveObject(res[PdfName.Shading]) is PdfDictionary shDict)
                        foreach (var kvp in shDict)
                            if (doc.ResolveObject(kvp.Value) is PdfDictionary shading
                                && CheckOneIccBasedSpace(doc, shading[PdfName.ColorSpace], checkedProfiles, v, i + 1))
                                return;

                    var xoDict = doc.ResolveObject(res[PdfName.XObject]) as PdfDictionary;
                    if (xoDict == null) continue;

                    foreach (var kvp in xoDict)
                    {
                        var xo = doc.ResolveObject(kvp.Value) as PdfStream;
                        if (xo == null) continue;
                        if ((doc.ResolveObject(xo[PdfName.Subtype]) as PdfName)?.Value != "Image") continue;
                        if (CheckOneIccBasedSpace(doc, xo[PdfName.ColorSpace], checkedProfiles, v, i + 1))
                            return;
                    }
                }
            }
        }

        /// <summary>
        /// Checks one colour-space value, if it is or holds an <c>[/ICCBased …]</c> array not already
        /// checked — a profile nested as an <c>/Indexed</c> or <c>[/Pattern base]</c> base or as a
        /// <c>/Separation</c> or <c>/DeviceN</c> alternate is a profile in the file (a classifier
        /// descends every base); returns <see langword="true"/> once a violation is recorded (the
        /// caller stops at the first finding, matching every other single-verdict A-code in this file).
        /// </summary>
        private static bool CheckOneIccBasedSpace(PdfDocument doc, PdfObject csObj,
            HashSet<PdfStream> checkedProfiles, List<PdfAViolation> v, int pageNumber, int depth = 0)
        {
            if (depth > PdfLimits.MaxResourceDepth) return false;
            if (!(doc.ResolveObject(csObj) is PdfArray arr) || arr.Count < 2) return false;
            switch ((doc.ResolveObject(arr[0]) as PdfName)?.Value)
            {
                case PdfColorSpaceNames.Indexed:
                case PdfColorSpaceNames.Pattern:
                    return CheckOneIccBasedSpace(doc, arr[1], checkedProfiles, v, pageNumber, depth + 1);
                case PdfColorSpaceNames.Separation:
                case PdfColorSpaceNames.DeviceN:
                    return arr.Count > 2 && CheckOneIccBasedSpace(doc, arr[2], checkedProfiles, v, pageNumber, depth + 1);
                case PdfColorSpaceNames.ICCBased:
                    break;
                default:
                    return false;
            }
            if (!(doc.ResolveObject(arr[1]) is PdfStream profile)) return false;
            if (!checkedProfiles.Add(profile)) return false;

            int? declaredN = (doc.ResolveObject(profile[PdfName.N]) as PdfNumber)?.IntValue;

            byte[] profileBytes;
            try
            {
                profileBytes = profile.GetDecodedData();
            }
            catch (Exception ex)
            {
                Report("PdfAValidator.CheckIccBasedComponentCounts",
                    $"page {pageNumber}'s ICCBased profile stream could not be decoded: {ex.Message}");
                ReportPartial($"Page {pageNumber}: the A20-A21 ICC profile check did not run in full " +
                              $"— an ICCBased profile stream could not be decoded: {ex.Message}.",
                              "A20", "A21");
                return false;
            }

            // A20 first, then A21, one verdict per profile: a profile whose /N disagrees with its
            // data space is reported for that and its header is not judged — the same
            // stop-at-the-first-finding shape every single-verdict A-code in this file uses.
            if (IccProfileComponents.TryGetDataColorSpaceComponentCount(profileBytes, out int actual)
                && declaredN != actual)
            {
                v.Add(new PdfAViolation("A20",
                    declaredN == null
                        ? $"Page {pageNumber}: an ICCBased colour space's profile stream has no /N " +
                          $"entry, but its data colour space carries {actual} component(s) " +
                          "(ISO 19005-1 §6.2.3.2, rule 6.2.3.2-2)."
                        : $"Page {pageNumber}: an ICCBased colour space declares /N {declaredN}, but " +
                          $"its profile's data colour space actually carries {actual} component(s) " +
                          "(ISO 19005-1 §6.2.3.2, rule 6.2.3.2-2)."));
                return true;
            }

            return CheckIccProfileHeader(profileBytes, v, pageNumber);
        }

        /// <summary>
        /// A21 — ISO 19005-1:2005 rule 6.2.3.2-1 (the published profile's wording, the ISO text itself being
        /// unavailable — the same accept-the-risk basis as A15-A20): "All ICCBased colour spaces
        /// shall be embedded as ICC profile streams as described in PDF Reference 4.5", tested by
        /// the profile as its device class being <c>prtr</c>, <c>mntr</c>, <c>scnr</c> or
        /// <c>spac</c>, its data colour space being <c>RGB</c>, <c>CMYK</c>, <c>GRAY</c> or
        /// <c>Lab</c>, and its version being below 3.0 — an ICC v2 profile, since PDF 1.4 predates
        /// v4.
        /// </summary>
        /// <remarks>
        /// <para>Read from the header alone, through <c>IccProfileComponents.TryGetHeaderFields</c>,
        /// which applies the same "is this an ICC header at all" gate as the evaluator; a profile
        /// that fails that gate is skipped, not reported, exactly as A20 skips it — "cannot verify"
        /// is not "verified wrong". Every clause the profile fails is named in the one violation.</para>
        /// <para>The rule judges the <b>data colour space</b>, not the profile connection space:
        /// GAPS.md once recorded this residual as "device class, PCS, version", and the PCS half was a
        /// misreading of the rule.</para>
        /// </remarks>
        private static bool CheckIccProfileHeader(byte[] profileBytes, List<PdfAViolation> v, int pageNumber)
        {
            if (!IccProfileComponents.TryGetHeaderFields(
                    profileBytes, out string deviceClass, out string dataColorSpace, out int majorVersion))
                return false;   // not an ICC header at all — see remarks

            var problems = new List<string>(3);
            if (!(deviceClass == "prtr" || deviceClass == "mntr" || deviceClass == "scnr" || deviceClass == "spac"))
                problems.Add($"device class '{deviceClass}' (PDF/A-1 accepts prtr, mntr, scnr or spac)");
            if (!(dataColorSpace == "RGB " || dataColorSpace == "CMYK" || dataColorSpace == "GRAY" || dataColorSpace == "Lab "))
                problems.Add($"data colour space '{dataColorSpace}' (PDF/A-1 accepts RGB, CMYK, GRAY or Lab)");
            if (majorVersion >= 3)
                problems.Add($"ICC version {majorVersion}.x (PDF/A-1 accepts a version below 3.0)");
            if (problems.Count == 0) return false;

            v.Add(new PdfAViolation("A21",
                $"Page {pageNumber}: an ICCBased colour space's profile is not one PDF/A-1 accepts: " +
                string.Join("; ", problems) + " (ISO 19005-1 §6.2.3.2, rule 6.2.3.2-1)."));
            return true;
        }

        /// <summary>
        /// A19 — ISO 19005-1:2005 Technical Corrigendum 1, §6.3.6: "For every font embedded in a
        /// conforming file and used for rendering, the glyph width information stored in the
        /// Widths entry of the font dictionary and in the embedded font program shall be
        /// consistent." Read from the corrigendum text directly (<c>docs/iso/ISO_19005_1;2005_Cor_1
        /// ;2007_en.pdf</c>), not a secondary source.
        /// </summary>
        /// <remarks>
        /// <para><b>Deliberately narrower than "every embedded font."</b> Simple TrueType, Type 1 and
        /// MMType1 fonts are covered through <c>Core.Fonts.SimpleFontWidthProbe</c> (the narrow public
        /// façade this uses, since Core's font classes are <c>internal</c> and no plugin is in its
        /// <c>InternalsVisibleTo</c>), which answers only where the glyph is selected without
        /// ambiguity: for TrueType, a symbolic font with no <c>/Encoding</c> addressing its embedded
        /// program's built-in cmap directly, or a <c>/Differences</c> name resolved through the font's
        /// own <c>post</c> table or a <c>gNN</c>-style subset-name convention; for Type 1, a
        /// <c>/Differences</c> name looked up in a <c>/FontFile3</c> CFF charset or a <c>/FontFile</c>
        /// Type 1 program. See that class's remarks for why a code left to a base encoding is declined
        /// rather than guessed at (composing <c>/ToUnicode</c>-priority decoding with a Unicode cmap
        /// lookup risks silently comparing the wrong glyph's width).</para>
        /// <para>Every font named in resources is checked, not only ones a content-stream scan
        /// confirms are drawn — over-inclusive versus the spec's "used for rendering," matching A06's
        /// own established precedent (it checks every declared font too) rather than adding new
        /// content-stream usage scanning for no real accuracy gain.</para>
        /// </remarks>
        private static void CheckGlyphWidthConsistency(PdfDocument doc, PdfAConformancePart part, List<PdfAViolation> v)
        {
            string clause = part == PdfAConformancePart.Part1 ? "ISO 19005-1:2005 Technical Corrigendum 1 §6.3.6"
                : part == PdfAConformancePart.Part4 ? "ISO 19005-4 6.2.10.5" : $"ISO 19005-{(int)part} 6.2.11.5";
            var seenFonts = new HashSet<PdfDictionary>();
            var seenResources = new HashSet<PdfDictionary>();
            long documentBytes = 0;

            for (int i = 0; i < doc.Pages.Count; i++)
            {
                var scan = PdfXValidator.ScanPage(doc, doc.Pages[i],
                    PdfXValidator.DefaultMaxPageContentBytes, ref documentBytes,
                    PdfXValidator.DefaultMaxDocumentContentBytes, collectContent: false);

                // A partial scan is reported: a font beyond the point the scan stopped is not
                // compared, and the result says so.
                if (scan.Incomplete != null)
                    ReportPartial($"Page {i + 1}: the A19 glyph-width check did not run in full — " +
                                  $"{scan.Incomplete}.", "A19");

                foreach (var res in scan.Resources)
                {
                    if (!ScanReuse.FirstVisit(seenResources, res)) continue;
                    var fontDict = doc.ResolveObject(res[PdfName.Font]) as PdfDictionary;
                    if (fontDict == null) continue;

                    foreach (var kvp in fontDict)
                    {
                        var fontObj = doc.ResolveObject(kvp.Value) as PdfDictionary;
                        if (fontObj == null) continue;
                        if (!seenFonts.Add(fontObj)) continue;

                        CheckFontGlyphWidths(doc, fontObj, kvp.Key.Value, clause, v);
                    }
                }
            }
        }

        /// <summary>Simple-font codes are single bytes (§9.6.6.1) — the same bound
        /// <c>PdfTrueTypeFont</c>'s own <c>/Differences</c> tables use.</summary>
        private const int MaxSimpleFontCode = 255;

        private static void CheckFontGlyphWidths(
            PdfDocument doc, PdfDictionary fontDict, string resourceKey, string clause, List<PdfAViolation> v)
        {
            var probe = new Core.Fonts.SimpleFontWidthProbe(fontDict, doc);
            if (!probe.IsTrueType && !probe.IsType1 && !probe.IsType3) return;
            string program = probe.IsTrueType ? "the embedded TrueType program's own glyph advance"
                : probe.IsType3 ? "the Type 3 glyph procedure's d0/d1 width"
                : "the embedded Type 1 program's own glyph advance";

            for (int code = 0; code <= MaxSimpleFontCode; code++)
            {
                if (!probe.HasExplicitWidth(code)) continue;
                if (!probe.TryGetEmbeddedWidth(code, out double embedded)) continue;

                double declared = probe.GetWidth(code);
                if (Math.Abs(declared - embedded) > GlyphWidthTolerance)
                {
                    v.Add(new PdfAViolation("A19",
                        $"/{resourceKey} code {code}: /Widths declares {declared}, {program} is " +
                        $"{embedded:0.##} ({clause})."));
                    return; // one report per font is enough; a caller wanting every code can re-scan
                }
            }
        }

        /// <summary>
        /// A <c>/Widths</c> entry is conventionally a whole number and
        /// <c>TrueTypeFontProgram.GetAdvance</c>'s unitsPerEm scaling rounds to the nearest integer
        /// — a tolerance of 1 absorbs that rounding without absorbing a real mismatch, which is never
        /// off by exactly one glyph-space unit in practice. A CFF or Type 1 advance is compared
        /// unrounded.
        /// </summary>
        private const double GlyphWidthTolerance = 1.0;

        /// <summary>
        /// A4-02 — the document catalog's <c>/NeedsRendering</c>, true (ISO 19005-2/3/4 6.4.2; rule 6.4.2-2,
        /// "A document's Catalog shall not contain the NeedsRendering key", tested as <c>NeedsRendering == false</c>).
        /// PDF/A-2 and -3 too, under the code's PDF/A-4 rule. The key is the catalog's (ISO 32000-1 Table 28), never
        /// the AcroForm dictionary's.
        /// </summary>
        private static void CheckNeedsRendering(PdfDocument doc, PdfAConformancePart part, List<PdfAViolation> v)
        {
            var catalog = doc.Catalog?.Dictionary;
            if (catalog == null) return;
            if (doc.ResolveObject(catalog[PdfName.NeedsRendering]) is PdfBoolean { Value: true })
                v.Add(new PdfAViolation("A4-02",
                    $"The document catalog has /NeedsRendering true (ISO 19005-{(int)part} 6.4.2; rule 6.4.2-2)."));
        }

        /// <summary>
        /// A4-03 — a <c>/Requirements</c> key in the document catalog (ISO 19005-2/3 6.11, ISO 19005-4 6.12; rules
        /// 6.11-1 and 6.12-1). PDF/A-2 and -3 too, under the code's PDF/A-4 rule.
        /// </summary>
        private static void CheckRequirementsArray(PdfDocument doc, PdfAConformancePart part, List<PdfAViolation> v)
        {
            var catalog = doc.Catalog?.Dictionary;
            if (catalog == null) return;
            string clause = part == PdfAConformancePart.Part4 ? "ISO 19005-4 6.12; rule 6.12-1" : $"ISO 19005-{(int)part} 6.11; rule 6.11-1";
            if (catalog[PdfName.Requirements] != null)
                v.Add(new PdfAViolation("A4-03", $"The document catalog has a /Requirements key ({clause})."));
        }

        // A4-05 — embedded files must carry an /AFRelationship key
        private static void CheckEmbeddedFilesAfRelationship(PdfDocument doc, List<PdfAViolation> v)
        {
            var catalog = doc.Catalog?.Dictionary;
            if (catalog == null) return;

            var namesObj = catalog[PdfName.Names];
            if (namesObj == null) return;

            var namesDict = doc.ResolveObject(namesObj) as PdfDictionary;
            if (namesDict == null) return;

            var efObj = namesDict[PdfName.EmbeddedFiles];
            if (efObj == null) return;

            // Walk the EmbeddedFiles name tree to find file specification dictionaries
            var efDict = doc.ResolveObject(efObj) as PdfDictionary;
            if (efDict == null) return;

            CheckAfRelationshipInNameTree(efDict, doc, v);
        }

        // Traversal is Core.Document.PdfNameTree (GAPS §15c): one implementation across the three
        // rings that walk name trees, and it adds the /Kids cycle guard and entry budget this copy
        // lacked. Only the value matters here — the key names the attachment, the violation names
        // the file — so the key is deliberately not read.
        private static void CheckAfRelationshipInNameTree(
            PdfDictionary node, PdfDocument doc, List<PdfAViolation> v)
        {
            if (node == null) return;

            var flat = new List<KeyValuePair<PdfObject, PdfObject>>();
            int budget = PdfLimits.MaxNameTreeEntries;
            PdfNameTree.TryFlatten(node, doc, flat, ref budget);

            foreach (var entry in flat)
            {
                var fileSpec = doc.ResolveObject(entry.Value) as PdfDictionary;
                if (fileSpec != null && fileSpec[PdfName.AFRelationship] == null)
                {
                    var fileName = (fileSpec.GetName(PdfName.F)?.Value
                                 ?? fileSpec[PdfName.F]?.ToString()
                                 ?? "(unknown)");
                    v.Add(new PdfAViolation("A4-05",
                        $"PDF/A-4 requires /AFRelationship on embedded file '{fileName}'."));
                }
            }
        }

        /// <summary>
        /// What the XMP packet's PDF/A identification schema declares (<c>pdfaid:part</c>,
        /// <c>pdfaid:conformance</c>, <c>pdfaid:rev</c>), read once per validation: A07 and A4-04
        /// judge it, and PDF/A-4's action rule depends on its conformance letter.
        /// </summary>
        private sealed class PdfAIdentification
        {
            internal string Part;
            internal string Conformance;
            internal string Rev;

            /// <summary>The parsed packet, for the property rules (A34-A36); null when unreadable.</summary>
            internal XmlDocument Packet;

            /// <summary>
            /// The prefix each identification property is written under, by local name (part,
            /// conformance, amd, corr, rev), for the clause's required prefix <c>pdfaid</c>.
            /// </summary>
            internal Dictionary<string, string> Prefixes = new Dictionary<string, string>();

            /// <summary>The A07 message when the packet is missing or unreadable; the values are then null.</summary>
            internal string Problem;
        }

        /// <summary>
        /// Reads the catalog's <c>/Metadata</c> packet's PDF/A identification, or <see langword="null"/>
        /// when the document has no catalog. Each value is read as element content first, then as an
        /// attribute (some writers put the properties on the <c>rdf:Description</c>).
        /// </summary>
        private static PdfAIdentification ReadIdentification(PdfDocument doc)
        {
            var catalog = doc.Catalog?.Dictionary;
            if (catalog == null) return null;

            var metaRef = catalog[PdfName.Metadata];
            if (metaRef == null)
                return new PdfAIdentification
                {
                    Problem = "PDF/A-1b requires an XMP metadata stream in the document catalog (/Metadata entry missing).",
                };

            var metaStream = doc.ResolveObject(metaRef) as PdfStream;
            if (metaStream == null)
                return new PdfAIdentification
                {
                    Problem = "PDF/A-1b requires an XMP metadata stream; the /Metadata entry does not point to a stream.",
                };

            // GUARDED. This was a bare call, and a /Metadata stream with a corrupt FlateDecode
            // payload therefore threw InvalidDataException out of the public Validate — through
            // PdfAValidationPlugin and into whatever batch job called it — instead of being the A07
            // violation it plainly is. PdfUaChecker and PdfXValidator both wrap the identical call.
            byte[] xmpBytes;
            try
            {
                xmpBytes = XmpStreamBytes.Read(metaStream);
            }
            catch (Exception ex)
            {
                Report("PdfAValidator.CheckXmpMetadata", "the XMP metadata stream could not be decoded");
                return new PdfAIdentification
                {
                    Problem = $"The XMP metadata stream could not be decoded ({ex.GetType().Name}: {ex.Message}), " +
                              "so the PDF/A identifier it should carry could not be read.",
                };
            }

            if (xmpBytes == null || xmpBytes.Length == 0)
                return new PdfAIdentification { Problem = "The XMP metadata stream is empty." };

            // Proper XML parsing avoids false positives from string search matching content that
            // happens to contain the searched patterns.
            try
            {
                var xmlDoc = new XmlDocument { XmlResolver = null };
                using (var reader = XmlReader.Create(
                    new MemoryStream(xmpBytes),
                    new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null }))
                {
                    xmlDoc.Load(reader);
                }

                var nsMgr = new XmlNamespaceManager(xmlDoc.NameTable);
                nsMgr.AddNamespace("pdfaid", Core.Metadata.XmpPacket.PdfAIdNamespace);

                var prefixes = new Dictionary<string, string>();
                string Property(string name)
                {
                    XmlNode node = xmlDoc.SelectSingleNode("//pdfaid:" + name, nsMgr);
                    string value = node?.InnerText?.Trim();
                    if (value == null)
                    {
                        node = xmlDoc.SelectSingleNode("//*/@pdfaid:" + name, nsMgr);
                        value = node?.Value?.Trim();
                    }
                    if (node != null) prefixes[name] = node.Prefix;
                    return value;
                }

                var identification = new PdfAIdentification
                {
                    Part = Property("part"),
                    Conformance = Property("conformance"),
                    Rev = Property("rev"),
                    Packet = xmlDoc,
                    Prefixes = prefixes,
                };
                Property("amd");
                Property("corr");
                return identification;
            }
            catch (XmlException xmlEx)
            {
                return new PdfAIdentification { Problem = $"XMP metadata is not well-formed XML: {xmlEx.Message}" };
            }
            catch (Exception ex)
            {
                // Anything else the reader can raise on a hostile packet — a decoder fallback, an
                // out-of-memory on a declared-huge entity — is still "the metadata could not be
                // read", which is an A07, not an exception out of a validator.
                Report("PdfAValidator.CheckXmpMetadata", "the XMP packet could not be parsed");
                return new PdfAIdentification
                {
                    Problem = $"XMP metadata could not be parsed ({ex.GetType().Name}: {ex.Message}).",
                };
            }
        }

        // A07 — XMP metadata conformance; A4-04 — PDF/A-4's pdfaid:rev
        private static void CheckXmpMetadata(PdfAIdentification id, List<PdfAViolation> v,
            PdfAConformancePart part = PdfAConformancePart.Part1,
            PdfAConformanceLevel level = PdfAConformanceLevel.Declared)
        {
            if (id == null) return;
            if (id.Problem != null)
            {
                v.Add(new PdfAViolation("A07", id.Problem));
                return;
            }

            string expectedPart = ((int)part).ToString();
            if (id.Part != expectedPart)
                v.Add(new PdfAViolation("A07",
                    $"XMP metadata does not declare pdfaid:part=\"{expectedPart}\"" +
                    $" (found: \"{id.Part ?? "(missing)"}\")" +
                    $" — required for PDF/A-{expectedPart}."));

            // pdfaid:conformance — validate the letter against the set permitted for this part
            // rather than accepting any non-empty value (an invalid letter like "Z" must NOT pass).
            // Part 1: A or B; Parts 2/3: A, B, or U. Part 4 has no level of its own, so the letter
            // is absent — or E for PDF/A-4e, F for PDF/A-4f (rule 6.7.3-3 in the PDF/A-4,
            // -4e and -4f profiles: "A file that does not conform to either PDF/A-4e or PDF/A-4f
            // shall not provide any pdfaid:conformance").
            // A level the caller requires decides which letters the file may declare, and that is the
            // letter's whole check. The levels nest — a level A file is a level U and a level B one —
            // so the published profiles' rule 6.7.11-3 / 6.6.4-3 admits B, U or A for level B, U or A
            // for level U, A alone for level A; PDF/A-4's rule 6.7.3-3 none for PDF/A-4, E for -4e, F
            // for -4f.
            if (level != PdfAConformanceLevel.Declared)
            {
                // Absent is not empty: an empty pdfaid:conformance is a property present (rule 6.7.3-3).
                string[] admitted = AdmittedLetters(part, level);
                if (Array.IndexOf(admitted, id.Conformance) < 0)
                    v.Add(new PdfAViolation("A07",
                        "XMP metadata declares " +
                        (id.Conformance == null ? "no pdfaid:conformance" : $"pdfaid:conformance=\"{id.Conformance}\"") +
                        $"; validated as PDF/A-{expectedPart}{LetterOf(level)?.ToLowerInvariant()}, which admits " +
                        (admitted[0] == null ? "none (rule 6.7.3-3)." : string.Join(", ", admitted) + ".")));
            }
            else if (part == PdfAConformancePart.Part4)
            {
                if (id.Conformance != null && id.Conformance != "E" && id.Conformance != "F")
                    v.Add(new PdfAViolation("A07",
                        $"pdfaid:conformance=\"{id.Conformance}\" is not valid for PDF/A-4: the property is " +
                        "absent, or E (PDF/A-4e) or F (PDF/A-4f) (ISO 19005-4 6.7.3; rule 6.7.3-3)."));
            }
            else if (string.IsNullOrEmpty(id.Conformance))
            {
                v.Add(new PdfAViolation("A07",
                    $"XMP metadata is missing pdfaid:conformance — required for PDF/A-{expectedPart}."));
            }
            else
            {
                var allowed = part == PdfAConformancePart.Part1
                    ? new[] { "A", "B" }
                    : new[] { "A", "B", "U" };
                if (System.Array.IndexOf(allowed, id.Conformance) < 0)
                    v.Add(new PdfAViolation("A07",
                        $"pdfaid:conformance=\"{id.Conformance}\" is not valid for PDF/A-{expectedPart}" +
                        $" (allowed: {string.Join("/", allowed)})."));
            }

            // The identification schema's required prefix (ISO 19005-1 6.7.11, 19005-2/3 6.6.4,
            // 19005-4 6.7.3; rules 6.7.11-4 to -6, 6.6.4-4 to -7, 6.7.3-4 and -6): the
            // properties each part's rules name.
            var named = part == PdfAConformancePart.Part1 ? new[] { "part", "conformance", "amd" }
                : part == PdfAConformancePart.Part4 ? new[] { "part", "rev" }
                : new[] { "part", "conformance", "amd", "corr" };
            foreach (var name in named)
                if (id.Prefixes.TryGetValue(name, out string prefix) && prefix != "pdfaid")
                    v.Add(new PdfAViolation("A07",
                        $"The PDF/A identification property {prefix}:{name} is not written under the prefix pdfaid, " +
                        "which the identification schema requires."));

            // PDF/A-4 also requires pdfaid:rev="2020" (A4-04)
            if (part == PdfAConformancePart.Part4 && id.Rev != "2020")
                v.Add(new PdfAViolation("A4-04",
                    $"PDF/A-4 requires pdfaid:rev=\"2020\" in XMP metadata" +
                    $" (found: \"{id.Rev ?? "(missing)"}\")" + "."));
        }
    }
}
