// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
// Content-stream tokenizing for UA50. Public in Core precisely so a plugin can do this —
// see the note on CheckNamedMcidsExist.
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// Validates a PDF against the PDF/UA-1 (ISO 14289-1) accessibility standard.
    ///
    /// <para>
    /// Checks performed:
    /// <list type="bullet">
    ///   <item>UA00 — A document catalog must be present.</item>
    ///   <item>UA01 — Document must be tagged (catalog /MarkInfo /Marked = true).</item>
    ///   <item>UA02 — Catalog must declare a natural language (/Lang entry).</item>
    ///   <item>UA03 — XMP metadata must declare <c>pdfuaid:part=1</c>.</item>
    ///   <item>UA04 — Document must have a document title set in /Info or XMP,
    ///                and /ViewerPreferences /DisplayDocTitle must be true. On an encrypted
    ///                document whose title could only be in XMP <c>dc:title</c> — an unreadable
    ///                packet, no <c>/Info /Title</c> — the missing-title half is reported as
    ///                UA99 rather than asserted, exactly as UA03 is.</item>
    ///   <item>UA05 — Catalog must have a /StructTreeRoot (tagged structure tree).</item>
    ///   <item>UA06 — If the document is encrypted, the security handler must permit content
    ///                extraction for accessibility (ISO 32000-1 Table 22, /P bit 10).
    ///                Encryption itself is <b>not</b> forbidden by PDF/UA-1.</item>
    ///   <item>UA07 — All figures must have alternate text (/Alt or /ActualText on Figure
    ///                structure elements) — checked structurally.</item>
    ///   <item>UA08 — All interactive form fields must have a /TU (tooltip / accessible name) entry.</item>
    ///   <item>UA09 — Pages with annotations must declare <c>/Tabs /S</c> (structure order).
    ///                <c>/Tabs</c> is the only key ISO 32000-1 Table 30 defines; the non-standard
    ///                <c>/TabOrder</c> spelling does not satisfy it and is reported, but the
    ///                message names it explicitly so
    ///                the owner of such a file knows which key to replace.</item>
    ///   <item>UA28 — The structure tree must have a <c>/ParentTree</c> once any element
    ///                references content.</item>
    ///   <item>UA29 — Structure elements must reference content (an MCID, an <c>/MCR</c>, an
    ///                <c>/OBJR</c> or a child element).</item>
    ///   <item>UA25 — Every font referenced by a page must be embedded
    ///                (/FontFile, /FontFile2 or /FontFile3 on the font descriptor).</item>
    ///   <item>UA26 — Every font referenced by a page must be mappable to Unicode
    ///                (/ToUnicode, or an encoding that yields Unicode).</item>
    ///   <item>UA27 — Annotations other than /Link, /Popup, /PrinterMark and /Widget must carry a
    ///                /Contents accessible description; /Link annotations need /Contents or an
    ///                enclosing &lt;Link&gt; structure element carrying /Alt. (/Widget annotations
    ///                are covered by UA08; /PrinterMark annotations are artifacts.)</item>
    ///   <item>UA51 — A TrueType font used for rendering against ISO 14289-1 7.21.6: a symbolic font
    ///                with /Encoding, a non-symbolic one not MacRoman or WinAnsi or with /Differences
    ///                names outside the Adobe Glyph List, a program cmap without the subtables asked
    ///                (<see cref="TrueTypeFontRules"/>).</item>
    ///   <item>UA52 — A TrapNet annotation a reader is presented with (ISO 14289-1 7.18.2).</item>
    ///   <item>UA53 — A reference XObject: a form with /Ref (7.20).</item>
    ///   <item>UA54 — An embedded CIDFontType2 whose /CIDToGIDMap is neither /Identity nor a stream (7.21.3.2).</item>
    ///   <item>UA55 — A /ToUnicode value of U+0000, U+FEFF or U+FFFE in a font used for rendering (7.21.7).</item>
    ///   <item>UA56 — A font subset whose /CharSet does not list exactly its program's glyphs, or whose
    ///     /CIDSet leaves out a CID its program holds (7.21.4.2).</item>
    ///   <item>UA57 — A font used for rendering paints a glyph its embedded program does not define (7.21.4.1).</item>
    ///   <item>UA99 — Reported when a check could not be completed: the document structure raised
    ///                an unexpected error, or the data the check needs is encrypted (the XMP
    ///                packet, which both UA03 and the title half of UA04 have to read).
    ///                Not a document defect as such, but it means the audit is incomplete
    ///                — inspect <see cref="LastValidationError"/> for the cause. A check that
    ///                neither ran nor said so is indistinguishable from a check that ran and found
    ///                nothing, which is why it is never silently omitted.</item>
    /// </list>
    /// </para>
    ///
    /// <b>Limitations:</b> Full PDF/UA compliance requires a complete tagged-content reader
    /// and role-map analysis. This checker performs header-level structural checks only.
    /// <para>
    /// This type does not write anything. To stamp the <c>pdfuaid</c> XMP identifier onto a
    /// document use <c>Tetitulz.Pdf.Accessibility.Metadata.PdfUaXmpWriter</c> in the
    /// <c>Tetitulz.Pdf.Accessibility</c> feature assembly — plugins must not reference feature
    /// assemblies, so it cannot be invoked from here.
    /// </para>
    /// </summary>
    public static class PdfUaChecker
    {
        // ----------------------------------------------------------------
        //  Diagnostics
        // ----------------------------------------------------------------

        // [ThreadStatic] so concurrent validations on a server host cannot read each
        // other's diagnostic (see CLAUDE.md "Diagnostics Pattern").
        [ThreadStatic] private static string _lastValidationError;

        /// <summary>
        /// Describes the most recent recoverable problem encountered by this thread while
        /// validating (a malformed sub-object, an unreadable encrypted stream, a traversal
        /// budget that was exhausted, …). <see langword="null"/> when the last validation ran
        /// cleanly. Failure-tolerant paths record here instead of swallowing the exception.
        /// </summary>
        public static string LastValidationError => _lastValidationError;

        private static void Report(string operation, string detail, Exception ex = null)
        {
            _lastValidationError = ex == null
                ? operation + ": " + detail
                : operation + ": " + detail + " — " + ex.GetType().Name + ": " + ex.Message;
        }

        // ----------------------------------------------------------------
        //  Traversal budgets (DoS guards)
        // ----------------------------------------------------------------

        // The structure-tree walk is StructureTreeWalk's (Base), with MaxStructNodes as both its
        // node ceiling and its /K-slot ceiling: a visited set stops re-expansion and does nothing
        // about the cost of one expansion, so N nodes that all name one shared N-entry array cost
        // N² while a per-node counter moves only N times. The slot ceiling is what fires on that
        // graph.
        private const int MaxStructDepth = 100;
        private const int MaxStructNodes = 200_000;
        // The AcroForm field walk's own ceilings live with that walk
        // (AcroFormFieldWalk.MaxFieldDepth / MaxFieldNodes), because it is shared with
        // PdfAccessibilityFixer and a second copy here would be a second thing to keep in step.
        // MaxPageTreeDepth / MaxPageTreeNodes bound exactly one walk: the hand-written
        // CollectPagesFrom below, which runs *only* for a document whose catalog the parser
        // refused to load — in practice an encrypted one. Every other document takes the
        // doc.Pages fast path, which is Core's own page walk; that one dedupes internally and
        // needs no ceiling from here. Do not read these as "the audit's page walk is bounded"
        // in general — one of the two page walks this file can take is not governed by them,
        // and does not need to be.
        private const int MaxPageTreeDepth = 256;
        private const int MaxPageTreeNodes = 200_000;
        private const int MaxResourceDepth = 32;
        private const int MaxResourceEntries = 200_000;
        // The /RoleMap hop cap moved to Core.Document.PdfRoleMap.MaxHops with the resolver itself.

        /// <summary>
        /// <c>/MCID</c> — the only <b>required</b> entry of a marked-content reference
        /// (ISO 32000-1 Table 324; its <c>/Type</c> is Optional).
        /// </summary>
        private static readonly PdfName McidKey = PdfName.MCID;

        /// <summary>
        /// <c>/Obj</c> — the only <b>required</b> entry of an object reference
        /// (ISO 32000-1 Table 325; its <c>/Type</c> is Optional).
        /// </summary>

        /// <summary>
        /// ISO 32000-1 Table 22, /P bit 10 (bit numbering in that table is 1-based, so
        /// bit 10 is <c>1 &lt;&lt; 9</c> = 512): "Extract text and graphics … for use by
        /// assistive technology". Deprecated in ISO 32000-2, where it is treated as always set.
        /// </summary>
        private const int PermissionExtractForAccessibility = 1 << 9;

        // ----------------------------------------------------------------
        //  Public API
        // ----------------------------------------------------------------

        /// <summary>
        /// Validates a PDF file against PDF/UA-1 and returns any violations found.
        /// </summary>
        public static PdfUaValidationResult Validate(string pdfPath)
        {
            if (pdfPath == null) throw new ArgumentNullException(nameof(pdfPath));
            using var fs = File.OpenRead(pdfPath);
            return Validate(fs);
        }

        /// <summary>
        /// Validates a PDF stream against PDF/UA-1 and returns any violations found.
        /// </summary>
        public static PdfUaValidationResult Validate(Stream input)
            => Validate(input, DefaultMaxMcidScanBytesPerPage, DefaultMaxMcidScanBytesPerDocument);

        /// <summary>
        /// Validates a PDF file against PDF/UA-1 with explicit content-scan ceilings.
        /// </summary>
        public static PdfUaValidationResult Validate(string pdfPath,
            int maxPageContentBytes, long maxDocumentContentBytes)
        {
            if (pdfPath == null) throw new ArgumentNullException(nameof(pdfPath));
            using var fs = File.OpenRead(pdfPath);
            return Validate(fs, maxPageContentBytes, maxDocumentContentBytes);
        }

        /// <summary>
        /// Validates a PDF stream against PDF/UA-1 with explicit content-scan ceilings.
        /// </summary>
        /// <param name="input">The PDF to check.</param>
        /// <param name="maxPageContentBytes">
        /// Decoded content bytes read per page before UA50's scan is skipped for that page.
        /// </param>
        /// <param name="maxDocumentContentBytes">
        /// Decoded content bytes read across the whole document before the scan stops.
        /// </param>
        /// <remarks>
        /// <b>A library's DoS ceiling has to be the caller's to set</b>, because only the caller
        /// knows whether the next file is a hostile upload or a 400-page press proof. These mirror
        /// <c>PdfXValidator</c>'s pair, which has taken them since the PDF/X round; the defaults are
        /// the same numbers and the same reasoning. A non-positive value is refused rather than
        /// silently meaning "unlimited" — <see cref="long.MaxValue"/> says that explicitly.
        /// </remarks>
        public static PdfUaValidationResult Validate(Stream input,
            int maxPageContentBytes, long maxDocumentContentBytes)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (maxPageContentBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxPageContentBytes),
                    "maxPageContentBytes must be positive; use long.MaxValue for no ceiling.");
            if (maxDocumentContentBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxDocumentContentBytes),
                    "maxDocumentContentBytes must be positive; use long.MaxValue for no ceiling.");

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            return ValidateDocument(doc, maxPageContentBytes, maxDocumentContentBytes);
        }

        // ----------------------------------------------------------------
        //  Internal validation
        // ----------------------------------------------------------------

        private static PdfUaValidationResult ValidateDocument(PdfDocument doc)
            => ValidateDocument(doc, DefaultMaxMcidScanBytesPerPage, DefaultMaxMcidScanBytesPerDocument);

        /// <summary>
        /// The checks, inside one <see cref="ScanReuse"/> scope: UA21's painted fonts and UA57's
        /// painted codes both walk every page through <c>PdfXValidator.ScanPage</c>, and each
        /// scanned and parsed every page again — most of a validation's time on a page of text.
        /// The PDF/A and PDF/X validators already ran their checks this way.
        /// </summary>
        private static PdfUaValidationResult ValidateDocument(PdfDocument doc,
            int maxPageContentBytes, long maxDocumentContentBytes)
        {
            using (ScanReuse.Begin())
                return ValidateDocumentInScope(doc, maxPageContentBytes, maxDocumentContentBytes);
        }

        private static PdfUaValidationResult ValidateDocumentInScope(PdfDocument doc,
            int maxPageContentBytes, long maxDocumentContentBytes)
        {
            _lastValidationError = null;
            UnreadableObjects.Begin();
            var violations = new List<PdfUaViolation>();

            // UA06 — encryption is permitted by PDF/UA-1 as long as the security handler
            // allows content extraction for accessibility.  Whatever the outcome we keep
            // auditing: names, numbers, booleans and dictionary structure are never
            // encrypted, so the structural checks below stay meaningful.  Only string and
            // stream *contents* are ciphertext, and this checker does not decrypt — so
            // `encrypted` is threaded into both checks that have to read a *stream* before
            // they can conclude anything: UA03 (the XMP pdfuaid:part declaration) and UA04
            // (a document title that lives in XMP dc:title rather than /Info).  Both then
            // downgrade an unreadable packet to UA99 "could not be verified" instead of
            // asserting a violation they cannot actually establish.  Checks that only need a
            // string's *presence* (UA02 /Lang, the /Info half of UA04) need no such handling:
            // ciphertext bytes are still bytes, and presence is all they claim to read.
            bool encrypted = CheckEncryption(doc, violations);

            var catalog = ResolveCatalog(doc);
            if (catalog == null)
            {
                violations.Add(new PdfUaViolation("UA00", "No document catalog found."));
                return Completed(violations);
            }

            // Structure-tree scan feeds UA07 (figure alt text) and UA27 (link annotations
            // nested in a <Link> element).  Run once, share the result.
            var structScan = RunStructTreeScan(doc, catalog, violations);
            var pages = RunCheckWithResult(violations, "page tree",
                () => CollectPages(doc, catalog, violations)) ?? new List<PageRef>();

            RunCheck(violations, "UA01", () => CheckTagging(doc, catalog, violations));
            RunCheck(violations, "UA02", () => CheckLanguage(doc, catalog, pages, structScan, encrypted, violations,
                maxPageContentBytes, maxDocumentContentBytes));
            RunCheck(violations, "UA05", () => CheckStructTreeRoot(doc, catalog, violations));
            RunCheck(violations, "UA03", () => CheckXmpMetadata(doc, catalog, violations, encrypted));
            RunCheck(violations, "UA04", () => CheckTitle(doc, catalog, violations, encrypted));
            RunCheck(violations, "UA07", () => CheckFigureAltText(structScan, violations));
            RunCheck(violations, "UA08", () => CheckFormFieldTooltips(doc, catalog, pages, structScan, violations));
            RunCheck(violations, "UA09", () => CheckTabOrder(doc, pages, violations));
            RunCheck(violations, "UA25/UA26", () => CheckFonts(doc, pages, violations,
                maxPageContentBytes, maxDocumentContentBytes));
            RunCheck(violations, "UA27", () => CheckAnnotations(doc, pages, structScan, violations));
            RunCheck(violations, "UA28/UA29", () => CheckHollowStructure(doc, catalog, structScan, violations));
            RunCheck(violations, "UA50", () => CheckNamedMcidsExist(doc, pages, encrypted, structScan,
                violations, maxPageContentBytes, maxDocumentContentBytes));

            return Completed(violations);
        }

        /// <summary>The result, with a UA99 notice when an object the checks read could not be read.</summary>
        private static PdfUaValidationResult Completed(List<PdfUaViolation> violations)
        {
            string unreadable = UnreadableObjects.Notice();
            if (unreadable != null) violations.Add(new PdfUaViolation("UA99", unreadable));
            string annotationsCut = ScanReuse.AnnotationsCutNotice("UA02, UA08, UA27 and the content scans");
            if (annotationsCut != null) violations.Add(new PdfUaViolation("UA99", annotationsCut));
            return new PdfUaValidationResult(violations);
        }

        /// <summary>
        /// Runs one check. A check that throws must not abort the whole audit, but it must not
        /// vanish either: the cause is recorded in <see cref="LastValidationError"/> and a UA99
        /// entry marks the report as incomplete.
        /// </summary>
        private static void RunCheck(List<PdfUaViolation> v, string name, Action check)
        {
            try
            {
                check();
            }
            catch (Exception ex)
            {
                Report("PdfUaChecker." + name, "check aborted", ex);
                v.Add(new PdfUaViolation("UA99",
                    $"The {name} check could not be completed ({ex.GetType().Name}: {ex.Message}). " +
                    "The conformance report is incomplete."));
            }
        }

        /// <summary>
        /// <see cref="RunCheck(List{PdfUaViolation},string,Action)"/> for a check that also
        /// produces a value. Named distinctly so a value-returning lambda can never bind to the
        /// <see cref="Action"/> overload by accident.
        /// </summary>
        private static T RunCheckWithResult<T>(List<PdfUaViolation> v, string name, Func<T> check)
            where T : class
        {
            try
            {
                return check();
            }
            catch (Exception ex)
            {
                Report("PdfUaChecker." + name, "check aborted", ex);
                v.Add(new PdfUaViolation("UA99",
                    $"The {name} check could not be completed ({ex.GetType().Name}: {ex.Message}). " +
                    "The conformance report is incomplete."));
                return null;
            }
        }

        // ----------------------------------------------------------------
        //  Indirect-reference-safe accessors
        // ----------------------------------------------------------------
        //  Every value in a PDF dictionary may be an indirect reference. A bare
        //  `x as PdfDictionary` therefore produces a *false* violation for any document
        //  that stores the value indirectly, which is entirely legal.

        private static PdfObject Resolve(PdfDocument doc, PdfObject obj)
            => UnreadableObjects.Resolve(doc, obj,
                ex => Report("PdfUaChecker.Resolve", "could not resolve indirect reference", ex));

        private static PdfDictionary ResolveDict(PdfDocument doc, PdfObject obj)
            => Resolve(doc, obj) as PdfDictionary;

        private static PdfArray ResolveArray(PdfDocument doc, PdfObject obj)
            => Resolve(doc, obj) as PdfArray;

        private static PdfName ResolveName(PdfDocument doc, PdfObject obj)
            => Resolve(doc, obj) as PdfName;

        private static PdfString ResolveString(PdfDocument doc, PdfObject obj)
            => Resolve(doc, obj) as PdfString;

        private static bool ResolveBool(PdfDocument doc, PdfObject obj)
            => Resolve(doc, obj) is PdfBoolean b && b.Value;

        private static int? ResolveInt(PdfDocument doc, PdfObject obj)
            => Resolve(doc, obj) is PdfNumber n ? n.IntValue : (int?)null;

        /// <summary>
        /// True when the value resolves to a string that carries actual characters.
        /// An empty or whitespace-only /Alt, /TU or /Contents is the same as having none.
        /// </summary>
        private static bool HasNonEmptyString(PdfDocument doc, PdfObject obj)
        {
            var s = ResolveString(doc, obj);
            if (s == null || s.RawBytes == null || s.RawBytes.Length == 0) return false;
            // On an encrypted document the bytes are ciphertext; GetText() then yields
            // mojibake rather than null, which is still the honest answer to "is a value
            // present?" — presence is all this predicate claims to establish.
            return !string.IsNullOrWhiteSpace(s.GetText());
        }

        private static PdfDictionary ResolveCatalog(PdfDocument doc)
        {
            var catalog = doc.Catalog?.Dictionary;
            if (catalog != null) return catalog;

            // PdfDocument skips catalog loading for encrypted documents (objects inside
            // object streams cannot be decoded without the key). The catalog itself is
            // almost always a top-level object, so resolve it directly and keep auditing.
            try
            {
                return doc.ResolveObject(doc.Trailer?[PdfName.Root]) as PdfDictionary;
            }
            catch (Exception ex)
            {
                Report("PdfUaChecker.ResolveCatalog", "trailer /Root is not resolvable", ex);
                return null;
            }
        }

        // ----------------------------------------------------------------
        //  UA06 — encryption must permit accessibility extraction
        // ----------------------------------------------------------------

        /// <summary>
        /// PDF/UA-1 does not forbid encryption (ISO 14289-1 §7.1 only requires that assistive
        /// technology be able to extract the content). What matters is the standard security
        /// handler's /P permission bit 10. Returns whether the document is encrypted.
        /// </summary>
        private static bool CheckEncryption(PdfDocument doc, List<PdfUaViolation> v)
        {
            if (!doc.IsEncrypted) return false;

            var enc = doc.EncryptionDictionary;
            if (enc == null)
            {
                v.Add(new PdfUaViolation("UA06",
                    "The document is encrypted but its encryption dictionary could not be read, " +
                    "so it cannot be established that the security handler permits content " +
                    "extraction for accessibility (ISO 32000-1 Table 22, /P bit 10)."));
                return true;
            }

            var filter = ResolveName(doc, enc[PdfName.Filter]);
            if (filter != null && filter.Value != "Standard")
            {
                v.Add(new PdfUaViolation("UA06",
                    $"The document uses the non-standard security handler /{filter.Value}. " +
                    "PDF/UA-1 requires that content extraction for accessibility be permitted, " +
                    "which cannot be verified outside the standard handler's /P bits."));
                return true;
            }

            int revision = ResolveInt(doc, enc[PdfName.R]) ?? 0;

            // Revision 2: permission bits 7-12 are reserved and shall be 1, so accessibility
            // extraction is always permitted.
            // Revision 6 (ISO 32000-2 / AES-256): bit 10 is deprecated and shall be treated as
            // if it were set.
            if (revision <= 2 || revision >= 6) return true;

            // PdfPermissionValue, not ResolveInt: /P is a 32-bit bitmask that producers write
            // unsigned, and an int cast saturates it to int.MaxValue — every bit set, so bit 10
            // read as "accessibility extraction permitted" for every such document.
            int? p = PdfPermissionValue.FromNumber(Resolve(doc, enc[PdfName.P]));
            if (p == null)
            {
                v.Add(new PdfUaViolation("UA06",
                    "The encryption dictionary has no /P permissions entry, so it cannot be " +
                    "established that content extraction for accessibility is permitted " +
                    "(ISO 32000-1 Table 22, /P bit 10)."));
                return true;
            }

            if ((p.Value & PermissionExtractForAccessibility) == 0)
            {
                v.Add(new PdfUaViolation("UA06",
                    "The document's security handler denies content extraction for accessibility " +
                    $"(/P bit 10 is clear; /P = {p.Value}, /R = {revision}). PDF/UA-1 requires " +
                    "assistive technology to be able to extract the document's content."));
            }

            return true;
        }

        // ----------------------------------------------------------------
        //  UA01 — /MarkInfo /Marked = true
        // ----------------------------------------------------------------

        private static void CheckTagging(PdfDocument doc, PdfDictionary catalog, List<PdfUaViolation> v)
        {
            var markInfoObj = catalog[PdfName.MarkInfo];
            if (markInfoObj == null)
            {
                v.Add(new PdfUaViolation("UA01",
                    "PDF/UA-1 requires a /MarkInfo dictionary with /Marked true in the document catalog."));
                return;
            }

            // /MarkInfo is very commonly stored indirectly (`/MarkInfo 9 0 R`). Resolving is
            // mandatory — the previous bare cast reported every such file as "not a dictionary".
            var markInfo = ResolveDict(doc, markInfoObj);
            if (markInfo == null)
            {
                v.Add(new PdfUaViolation("UA01",
                    "The /MarkInfo entry in the catalog is not a dictionary."));
                return;
            }

            if (!ResolveBool(doc, markInfo[PdfName.Marked]))
                v.Add(new PdfUaViolation("UA01",
                    "PDF/UA-1 requires /MarkInfo /Marked = true. The document is not tagged."));
        }

        // ----------------------------------------------------------------
        //  UA02 — /Lang
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-1 §7.2: "Natural language shall be declared as discussed in ISO 32000-1:2008,
        /// 14.9.2 and/or as described in ISO 32000-1:2008, 7.9.2" — the catalog's <c>/Lang</c> is one
        /// way, a structure element's, a marked-content sequence's or a string's language escape
        /// another.
        /// </summary>
        /// <remarks>
        /// <para>With a catalog <c>/Lang</c> every language is determined, so it must not be empty.
        /// Without one, each thing that carries text must determine its own, as the rules
        /// 7.2-21 to -34 ask: a structure element's <c>/Alt</c>, <c>/ActualText</c> or <c>/E</c>
        /// (its <c>/Lang</c> or an ancestor's), an annotation's <c>/Contents</c> and a field's
        /// <c>/TU</c> (a language escape, §7.9.2.2), page text inside tagged content (an enclosing
        /// marked-content <c>/Lang</c>, or its structure element's; artifacts need none), and XMP text in
        /// <c>x-default</c>, which only the catalog can give. The conformance corpus passes documents
        /// that declare every language that way (7.2-t21 to -t34), so a missing catalog entry alone
        /// is no failure.</para>
        /// <para>The page-text half reads the content through <see cref="DefinedMcids"/>'s walk. It
        /// runs on every unencrypted document, since rule 7.2-29 counts the marked-content tags, and
        /// a single content stream's parse is kept for UA50 and the font walks
        /// (<see cref="ScanReuse.ParseAndKeep"/>).</para>
        /// </remarks>
        private static void CheckLanguage(PdfDocument doc, PdfDictionary catalog, List<PageRef> pages,
            StructTreeScan scan, bool encrypted, List<PdfUaViolation> v,
            int maxPageContentBytes, long maxDocumentContentBytes)
        {
            var langObj = catalog[PdfName.Lang];
            if (langObj != null && !HasNonEmptyString(doc, langObj))
            {
                v.Add(new PdfUaViolation("UA02",
                    "The catalog /Lang entry is present but empty. PDF/UA-1 requires a non-empty " +
                    "BCP 47 language tag (e.g. \"en-US\")."));
                return;
            }
            bool catalogDeclares = langObj != null;

            // Strings and content are ciphertext to this checker, so neither a language's syntax nor
            // whether each text declares its own can be read.
            if (encrypted)
            {
                // A structure element's /Lang is a string, its presence readable through the
                // ciphertext: with none anywhere, nothing but the catalog could declare a language.
                if (!catalogDeclares && scan.ElementsWithLanguage == 0)
                    v.Add(new PdfUaViolation("UA02",
                        "PDF/UA-1 requires the natural language to be declared (ISO 14289-1 §7.2): the document " +
                        "has no catalog /Lang and no structure element with one."));
                else if (!catalogDeclares)
                    v.Add(new PdfUaViolation("UA99",
                        "The document has no catalog /Lang and is encrypted, so whether every text declares " +
                        "its own natural language (UA02) could not be verified."));
                return;
            }

            // rule 7.2-29: every /Lang — the catalog's, a structure element's, a marked-content
            // sequence's — is a language identifier (§14.9.2.1). The page walk below counts the last.
            int invalidTags = scan.InvalidLanguageTags
                + (catalogDeclares && !IsLanguageIdentifier(ResolveString(doc, langObj).GetText()) ? 1 : 0);

            long documentBytes = 0;
            int text = 0;
            foreach (var page in pages)
            {
                int objNum = page.Dict?.ObjectNumber ?? 0;
                var languages = new MarkedContentLanguages(objNum, scan.McidsWithLanguage);
                if (DefinedMcids(doc, page, objNum, v, ref documentBytes, maxPageContentBytes,
                        maxDocumentContentBytes, languages, "UA02 (the language of page text)") != null)
                {
                    text += languages.TextWithoutLanguage;
                    invalidTags += languages.InvalidLanguageTags;
                }
            }
            if (invalidTags > 0)
                v.Add(new PdfUaViolation("UA02",
                    $"{invalidTags} /Lang value(s) are not language identifiers (ISO 32000-1 §14.9.2.1: a " +
                    "Language-Tag such as \"en-US\", or the empty string for an unknown language; " +
                    "rule 7.2-29)."));

            if (catalogDeclares) return;

            var undetermined = new List<string>();
            if (scan.AlternatesWithoutLanguage > 0)
                undetermined.Add($"{scan.AlternatesWithoutLanguage} structure element(s) with /Alt, /ActualText or /E and no /Lang of their own or an ancestor's");

            // An annotation's /Contents and a field's /TU (rules 7.2-24, -25): a language
            // escape, or the /Lang of the structure element whose /OBJR names the annotation (a field
            // through its widgets) — the conformance corpus 7.2-t24-pass-b, -t25-pass-b.
            int contents = 0, tooltips = 0;
            foreach (var page in pages)
            {
                var annots = ResolveArray(doc, page.Dict?[PdfName.Annots]);
                if (!ScanReuse.AdmitAnnotations(annots)) continue;
                for (int i = 0; i < annots.Count; i++)
                {
                    var annot = ResolveDict(doc, annots[i]);
                    int objNum = (annots[i] as PdfReference)?.ReferenceObjectNumber ?? annot?.ObjectNumber ?? 0;
                    if (annot != null && LacksLanguageEscape(doc, annot[PdfName.Contents])
                        && !scan.AnnotationsWithLanguage.Contains(objNum))
                        contents++;
                }
            }
            var fields = AcroFormFieldWalk.Walk(catalog, obj => Resolve(doc, obj));
            foreach (var field in fields.Fields)
                if (LacksLanguageEscape(doc, field.Dictionary[PdfName.TU])
                    && !WidgetsOf(doc, field.Dictionary).Any(scan.AnnotationsWithLanguage.Contains))
                    tooltips++;
            if (contents > 0) undetermined.Add($"{contents} annotation /Contents without a language escape or a /Lang on its structure element");
            if (tooltips > 0) undetermined.Add($"{tooltips} field /TU without a language escape or a /Lang on its widget's structure element");

            // Outline titles are text strings too; only an escape or the catalog gives their language
            // (the conformance corpus 7.2-t02-fail-a).
            int titles = 0;
            OutlineItems.Walk(doc, catalog, item =>
            {
                if (LacksLanguageEscape(doc, item[PdfName.Title])) titles++;
                return true;
            });
            if (titles > 0) undetermined.Add($"{titles} outline title(s) without a language escape");

            if (XmpHasDefaultLanguageText(doc, catalog))
                undetermined.Add("XMP metadata text in x-default");

            if (text > 0) undetermined.Add($"{text} text-showing operator(s) outside artifacts with no /Lang on their marked content or structure element");

            if (undetermined.Count > 0)
                v.Add(new PdfUaViolation("UA02",
                    "The document catalog has no /Lang, and the natural language of some text is not " +
                    "declared any other way (ISO 14289-1 §7.2; rules 7.2-21 to -34): " +
                    string.Join("; ", undetermined) + "."));
        }

        /// <summary>
        /// Whether a text string is present and does not begin with a language escape (§7.9.2.2:
        /// U+001B, a language code, U+001B, in a UTF-16BE string).
        /// </summary>
        private static bool LacksLanguageEscape(PdfDocument doc, PdfObject obj)
        {
            if (!HasNonEmptyString(doc, obj)) return false;
            string text = ResolveString(doc, obj).GetText();
            return text.Length == 0 || text[0] != (char)0x1B;
        }

        /// <summary>
        /// Whether a <c>/Lang</c> value is a language identifier (ISO 32000-1 §14.9.2.1): the empty
        /// string (language unknown), or letters 1 to 8, then any number of <c>-</c> and 1 to 8 letters
        /// or digits — rule 7.2-29's <c>/^[a-zA-Z]{1,8}(-[a-zA-Z0-9]{1,8})*$/</c>, read by hand
        /// rather than by a regular expression over a file's field. The profile refuses the empty string;
        /// the clause allows it.
        /// </summary>
        internal static bool IsLanguageIdentifier(string tag)
        {
            if (tag == null) return false;
            if (tag.Length == 0) return true;
            int run = 0;
            bool first = true;
            foreach (char c in tag)
            {
                if (c == '-')
                {
                    if (run == 0) return false;
                    first = false;
                    run = 0;
                    continue;
                }
                bool letter = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
                bool digit = c >= '0' && c <= '9';
                if (!(letter || (!first && digit)) || ++run > 8) return false;
            }
            return run > 0;
        }

        /// <summary>The object numbers of a terminal field's widgets: itself when merged, else its widget kids.</summary>
        private static IEnumerable<int> WidgetsOf(PdfDocument doc, PdfDictionary field)
        {
            if (field.ObjectNumber > 0 && field[PdfName.Rect] != null) yield return field.ObjectNumber;
            var kids = ResolveArray(doc, field[PdfName.Kids]);
            if (kids == null) yield break;
            for (int i = 0; i < kids.Count; i++)
            {
                int objNum = (kids[i] as PdfReference)?.ReferenceObjectNumber ?? ResolveDict(doc, kids[i])?.ObjectNumber ?? 0;
                if (objNum > 0) yield return objNum;
            }
        }

        /// <summary>Whether the catalog's XMP has text in <c>x-default</c>; false when it cannot be read.</summary>
        private static bool XmpHasDefaultLanguageText(PdfDocument doc, PdfDictionary catalog)
        {
            var stream = Resolve(doc, catalog[PdfName.Metadata]) as PdfStream;
            if (stream == null) return false;
            try
            {
                byte[] xmp = XmpStreamBytes.Read(stream);
                if (xmp == null || xmp.Length == 0) return false;
                PdfUaXmpIdentifier.Read(xmp, out _, out _, out bool hasDefault);
                return hasDefault;
            }
            catch (Exception ex)
            {
                // UA03 reports an unreadable packet; this half has nothing of its own to add.
                Report("PdfUaChecker.CheckLanguage", "the XMP packet could not be read for its x-default text", ex);
                return false;
            }
        }

        /// <summary>
        /// What <see cref="DefinedMcids"/> also counts for UA02: text shown outside an artifact whose
        /// language neither an enclosing marked-content sequence's <c>/Lang</c> nor its structure
        /// element (or an ancestor) declares — untagged text included, which has no other way.
        /// </summary>
        private sealed class MarkedContentLanguages
        {
            private readonly int _page;
            private readonly HashSet<long> _mcidsWithLanguage;

            internal MarkedContentLanguages(int page, HashSet<long> mcidsWithLanguage)
            {
                _page = page;
                _mcidsWithLanguage = mcidsWithLanguage;
            }

            internal int TextWithoutLanguage;

            /// <summary>Marked-content <c>/Lang</c> values that are not language identifiers.</summary>
            internal int InvalidLanguageTags;

            internal bool StructureDeclares(int mcid) => _mcidsWithLanguage.Contains(McidKeyOf(_page, mcid));
        }

        /// <summary>A (page object number, MCID) pair as one key.</summary>
        private static long McidKeyOf(int page, int mcid) => ((long)page << 32) | (uint)mcid;

        // ----------------------------------------------------------------
        //  UA05 — /StructTreeRoot
        // ----------------------------------------------------------------

        private static void CheckStructTreeRoot(PdfDocument doc, PdfDictionary catalog, List<PdfUaViolation> v)
        {
            var strObj = catalog[PdfName.StructTreeRoot];
            if (strObj == null)
            {
                v.Add(new PdfUaViolation("UA05",
                    "PDF/UA-1 requires a /StructTreeRoot in the document catalog " +
                    "(the document must have a tagged structure tree)."));
                return;
            }

            if (ResolveDict(doc, strObj) == null)
                v.Add(new PdfUaViolation("UA05",
                    "The catalog /StructTreeRoot entry does not resolve to a dictionary, so the " +
                    "document has no usable structure tree."));
        }

        // ----------------------------------------------------------------
        //  UA03 — XMP pdfuaid:part=1
        // ----------------------------------------------------------------

        private static void CheckXmpMetadata(PdfDocument doc, PdfDictionary catalog,
            List<PdfUaViolation> v, bool encrypted)
        {
            var metaRef = catalog[PdfName.Metadata];
            if (metaRef == null)
            {
                v.Add(new PdfUaViolation("UA03",
                    "PDF/UA-1 requires an XMP metadata stream (/Metadata in catalog) " +
                    "declaring pdfuaid:part=1."));
                return;
            }

            var metaStream = Resolve(doc, metaRef) as PdfStream;
            if (metaStream == null)
            {
                v.Add(new PdfUaViolation("UA03",
                    "The /Metadata entry does not point to a valid stream."));
                return;
            }

            byte[] xmpBytes;
            try
            {
                xmpBytes = XmpStreamBytes.Read(metaStream);
            }
            catch (Exception ex)
            {
                Report("PdfUaChecker.CheckXmpMetadata", "XMP stream could not be decoded", ex);
                if (encrypted) v.Add(Ua03Unverifiable("the stream could not be decoded"));
                else
                    v.Add(new PdfUaViolation("UA03",
                        $"The XMP metadata stream could not be decoded: {ex.Message}"));
                return;
            }

            if (xmpBytes == null || xmpBytes.Length == 0)
            {
                v.Add(new PdfUaViolation("UA03", "The XMP metadata stream is empty."));
                return;
            }

            string partValue = null;
            try
            {
                // One reader for the identification schema: UA03, PdfUa2Checker's UA2-5 and the
                // accessibility audit's target detection all ask which part the packet declares.
                PdfUaXmpIdentifier.Read(xmpBytes, out partValue, out _);
            }
            catch (XmlException ex)
            {
                Report("PdfUaChecker.CheckXmpMetadata", "XMP is not well-formed XML", ex);
                // On an encrypted document the stream bytes are ciphertext unless
                // /EncryptMetadata is false, so "not well-formed" says nothing about the
                // document's conformance — but neither does silence.
                if (encrypted) v.Add(Ua03Unverifiable("the packet is not well-formed XML"));
                else
                    v.Add(new PdfUaViolation("UA03",
                        $"XMP metadata is not well-formed XML: {ex.Message}"));
                return;
            }

            if (partValue != "1")
                v.Add(new PdfUaViolation("UA03",
                    $"XMP metadata must declare pdfuaid:part=\"1\" " +
                    $"(found: \"{partValue ?? "(missing)"}\" )."));
        }

        /// <summary>
        /// The XMP packet of an encrypted document is ciphertext (unless <c>/EncryptMetadata</c> is
        /// false), and this checker does not decrypt. UA03 therefore cannot be decided.
        ///
        /// <para>
        /// <b>It must still be reported.</b> Suppressing it left the pdfuaid:part declaration —
        /// the document's own PDF/UA conformance assertion — neither confirmed nor refuted, and an
        /// omitted check is indistinguishable from a check that ran and found nothing: an encrypted
        /// document with a garbage /Metadata stream came back with <b>zero</b> violations and
        /// <see cref="PdfUaValidationResult.IsConforming"/> true, and the combined audit printed
        /// "NO BLOCKING ISSUES DETECTED" over a file that cannot possibly be PDF/UA-1. Reported as
        /// UA99 (the "audit is incomplete" code) rather than UA03, because asserting the
        /// declaration is *wrong* would be a claim this checker equally cannot support.
        /// </para>
        /// </summary>
        private static PdfUaViolation Ua03Unverifiable(string cause)
            => new PdfUaViolation("UA99",
                "UA03 could not be verified: the document is encrypted, so its XMP metadata stream " +
                $"is ciphertext to this checker ({cause}). The pdfuaid:part=\"1\" declaration that " +
                "PDF/UA-1 requires was therefore neither confirmed nor refuted — decrypt the " +
                "document and re-run the audit, or inspect the packet with a tool that holds the " +
                "key. The conformance report is incomplete.");

        // ----------------------------------------------------------------
        //  UA04 — document title
        // ----------------------------------------------------------------

        private static void CheckTitle(PdfDocument doc, PdfDictionary catalog,
            List<PdfUaViolation> v, bool encrypted)
        {
            // Check /ViewerPreferences /DisplayDocTitle = true. This half is always decidable:
            // it is a boolean in a dictionary, and encryption never touches those.
            //
            // Through Core.Document.PdfViewerPreferences, which takes a resolver rather than a
            // PdfDocument precisely so an encrypted document — which loads no catalog object — can
            // still be read. Three checkers open-coded this two-level read; sharing it follows the
            // precedent CLAUDE.md records for /P, where the one step that could make the two
            // accessibility halves disagree is shared rather than mirrored.
            bool displayTitle =
                PdfViewerPreferences.Read(catalog, o => Resolve(doc, o)).DisplayDocTitle == true;

            if (!displayTitle)
                v.Add(new PdfUaViolation("UA04",
                    "PDF/UA-1 requires /ViewerPreferences /DisplayDocTitle = true " +
                    "so that the document title is shown in the title bar."));

            // Check that a title is actually set (in /Info or XMP). /Info /Title is a *string*:
            // on an encrypted document its bytes are ciphertext, but HasNonEmptyString only
            // claims presence, which is exactly what UA04 needs, so this half needs no
            // encryption handling.
            bool hasTitle = false;
            var infoDict = ResolveDict(doc, doc.Trailer?[PdfName.Info]);
            if (infoDict != null)
                hasTitle = HasNonEmptyString(doc, infoDict[PdfName.Title]);

            if (hasTitle) return;

            // A title may instead live in XMP dc:title (valid, common in PDF 2.0 / PDF/UA
            // workflows), and that is a *stream*: on an encrypted document it is ciphertext to
            // this checker. "I could not read the packet" is not evidence that the document has
            // no title, so it must not be reported as one — see Ua04Unverifiable.
            switch (FindXmpDcTitle(doc, catalog))
            {
                case XmpTitleState.Present:
                    return;

                case XmpTitleState.Unreadable when encrypted:
                    // Deliberately the same treatment UA03 gives the same condition
                    // (see Ua03Unverifiable): downgrade to UA99, never assert the violation.
                    v.Add(Ua04Unverifiable());
                    return;

                default:
                    // Either the packet was read and carries no dc:title, or it is unreadable on
                    // a document that is not encrypted — in which case "unreadable" is itself a
                    // document defect and UA03 reports it. Both mean: no title from XMP.
                    v.Add(new PdfUaViolation("UA04",
                        "PDF/UA-1 requires a non-empty document title in the /Info dictionary " +
                        "or XMP dc:title."));
                    return;
            }
        }

        /// <summary>
        /// The outcome of looking for a document title in the XMP packet's <c>dc:title</c>.
        /// The third state is the whole point: "no title found" and "could not look" are
        /// different answers, and collapsing them produced a false UA04 on every encrypted
        /// document whose title lives in XMP.
        /// </summary>
        internal enum XmpTitleState
        {
            /// <summary>The packet was read and carries no non-empty <c>dc:title</c>.</summary>
            Absent,
            /// <summary>The packet was read and carries a non-empty <c>dc:title</c>.</summary>
            Present,
            /// <summary>
            /// The packet exists but could not be decoded or is not well-formed XML, so nothing
            /// can be concluded about the title it may contain.
            /// </summary>
            Unreadable,
        }

        /// <summary>
        /// Looks for a non-empty <c>dc:title</c> in the catalog's XMP packet.
        ///
        /// <para>
        /// A packet that parses as well-formed XML is plaintext — encrypted bytes do not parse —
        /// so <see cref="XmpTitleState.Absent"/> is a trustworthy answer even on an encrypted
        /// document (which is legal: <c>/EncryptMetadata false</c> leaves the packet in the
        /// clear). Only a decode failure or an <see cref="XmlException"/> yields
        /// <see cref="XmpTitleState.Unreadable"/>, which is the same line UA03 draws in
        /// <see cref="CheckXmpMetadata"/>.
        /// </para>
        /// </summary>
        /// <remarks>
        /// <c>internal</c> (not <c>private</c>) and named in this assembly's
        /// <c>InternalsVisibleTo</c> for <c>Tetitulz.Pdf.Accessibility</c>: PDF/UA-2 §8.11.1 needs
        /// the same non-empty-<c>dc:title</c> lookup, without the <c>/Info /Title</c> fallback
        /// UA04 also accepts, so <c>PdfUa2StructureChecker</c> reuses this rather than
        /// re-deriving the BOM handling, DTD-processing settings and encryption-aware "absent vs.
        /// unreadable" distinction this method already carries.
        /// </remarks>
        internal static XmpTitleState FindXmpDcTitle(PdfDocument doc, PdfDictionary catalog)
        {
            var metaStream = Resolve(doc, catalog[PdfName.Metadata]) as PdfStream;

            // No /Metadata at all is not an "unreadable" packet — the *absence* is visible even
            // under encryption, because dictionary structure is never ciphertext. UA03 reports
            // the absence; for UA04 it simply means the title is not there.
            if (metaStream == null) return XmpTitleState.Absent;

            byte[] bytes;
            try
            {
                bytes = XmpStreamBytes.Read(metaStream);
            }
            catch (Exception ex)
            {
                Report("PdfUaChecker.FindXmpDcTitle", "XMP stream could not be decoded", ex);
                return XmpTitleState.Unreadable;
            }

            // An empty packet is a definite fact about the document, not a failure to read it —
            // matching CheckXmpMetadata, which reports empty as a plain UA03 whether or not the
            // document is encrypted.
            if (bytes == null || bytes.Length == 0) return XmpTitleState.Absent;

            try
            {
                // BOM-aware: XMP permits UTF-16, and Encoding.UTF8.GetString does not throw on it —
                // it substitutes, so a UTF-16 packet parsed as mojibake and dc:title was never
                // found. One decoder, in Core.Metadata.XmpPacket.
                string xmp = Tetitulz.Pdf.Core.Metadata.XmpPacket.DecodeText(bytes);
                var settings = new XmlReaderSettings
                {
                    // Ignore, not Prohibit -- MATCHING CheckXmpMetadata above, which reads THIS SAME
                    // packet. A packet with an internal DTD subset parsed there and threw here, so
                    // FindXmpDcTitle returned Unreadable and, for an unencrypted document, the
                    // consumer's default arm reported a FALSE UA04 on a document that plainly has a
                    // title. That arm's comment said "'unreadable' is itself a document defect and
                    // UA03 reports it" -- but the sibling method had just read the packet fine, so
                    // UA03 was never raised and nothing reported the truth.
                    //
                    // Neither setting is the safer one: XmlResolver is null in both, so no entity is
                    // ever expanded and neither can be made to fetch anything. Ignore simply
                    // tolerates a subset that Prohibit rejects.
                    //
                    // NOT COVERED BY A TEST, AND THAT IS STATED RATHER THAN IMPLIED. The
                    // divergence is verified by reading the two methods, and this change is
                    // strictly more permissive, so it cannot reject anything that parsed before.
                    // A fixture reaching it through Validate could not be built: dc:title was not
                    // found in the CONTROL packet either, so the harness never exercised the DTD
                    // question at all, and chasing that is a separate matter from this alignment.
                    DtdProcessing = DtdProcessing.Ignore,
                    XmlResolver = null,
                };
                using var sr = new StringReader(xmp);
                using var reader = XmlReader.Create(sr, settings);
                while (reader.Read())
                {
                    // Find dc:title and check its subtree (dc:title/rdf:Alt/rdf:li)
                    // for any non-empty text — scoped to title so dc:creator etc.
                    // can't satisfy it.
                    if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "title")
                    {
                        using var sub = reader.ReadSubtree();
                        while (sub.Read())
                            if (sub.NodeType == XmlNodeType.Text
                                && !string.IsNullOrWhiteSpace(sub.Value))
                                return XmpTitleState.Present;
                    }
                }
            }
            catch (XmlException ex)
            {
                // Not well-formed: either a broken packet, or ciphertext. Never silent — the
                // cause is retrievable from LastValidationError.
                Report("PdfUaChecker.FindXmpDcTitle", "XMP is not well-formed XML", ex);
                return XmpTitleState.Unreadable;
            }
            catch (Exception ex)
            {
                Report("PdfUaChecker.FindXmpDcTitle", "XMP could not be read for dc:title", ex);
                return XmpTitleState.Unreadable;
            }

            return XmpTitleState.Absent;
        }

        /// <summary>
        /// The encrypted-document counterpart of <see cref="Ua03Unverifiable"/>, for the half of
        /// UA04 that has to read the XMP packet.
        ///
        /// <para>
        /// A title may legitimately live only in XMP <c>dc:title</c>. On an encrypted document
        /// that packet is ciphertext to this checker, so a plain "no document title" finding
        /// would be a <b>false</b> UA04 on a perfectly valid file — asserting the absence of
        /// something the checker was never able to look at. Reported as UA99 for the same reason
        /// UA03 is: the audit is incomplete, which is a different claim from "the document is
        /// wrong", and the two must not be confused. The <c>/ViewerPreferences
        /// /DisplayDocTitle</c> half of UA04 is unaffected and is still reported normally — it
        /// is a boolean, and encryption never touches those.
        /// </para>
        /// </summary>
        private static PdfUaViolation Ua04Unverifiable()
            => new PdfUaViolation("UA99",
                "UA04 could not be fully verified: the document has no title in its /Info " +
                "dictionary, and the XMP metadata stream that could carry one in dc:title is " +
                "ciphertext to this checker because the document is encrypted. Whether the " +
                "document title PDF/UA-1 requires is present was therefore neither confirmed nor " +
                "refuted — decrypt the document and re-run the audit. The conformance report is " +
                "incomplete.");

        // ----------------------------------------------------------------
        //  Structure-tree scan (feeds UA07 and UA27)
        // ----------------------------------------------------------------

        /// <summary>Aggregated facts gathered in a single pass over the structure tree.</summary>
        private sealed class StructTreeScan
        {
            internal int FiguresMissingAlt;

            /// <summary>
            /// Object numbers of annotations referenced (via an /OBJR kid) from a
            /// &lt;Link&gt; structure element that carries a non-empty /Alt.
            /// </summary>
            internal readonly HashSet<int> LinkAnnotationsWithStructAlt = new HashSet<int>();

            /// <summary>
            /// Object numbers of annotations referenced (via an /OBJR kid) from any structure element
            /// with a non-empty /Alt — the "alternative description" ISO 14289-1 §7.18.1 accepts
            /// for an annotation without /Contents, and for a widget whose field has no /TU.
            /// </summary>
            internal readonly HashSet<int> AnnotationsWithStructAlt = new HashSet<int>();

            /// <summary>Elements with /Alt, /ActualText or /E and no /Lang of their own or an ancestor's.</summary>
            internal int AlternatesWithoutLanguage;

            /// <summary>Elements whose /Lang is not a language identifier (rule 7.2-29).</summary>
            internal int InvalidLanguageTags;

            /// <summary>Elements with a non-empty /Lang of their own.</summary>
            internal int ElementsWithLanguage;

            /// <summary>Annotations an /OBJR names from an element with a /Lang of its own or an ancestor's.</summary>
            internal readonly HashSet<int> AnnotationsWithLanguage = new HashSet<int>();

            /// <summary>(page, MCID) keys (<see cref="McidKeyOf"/>) whose element or an ancestor has /Lang.</summary>
            internal readonly HashSet<long> McidsWithLanguage = new HashSet<long>();

            internal bool BudgetExhausted;

            /// <summary>The ceiling that stopped the walk, phrased for a report; null when none did.</summary>
            internal string Ceiling;

            /// <summary>Structure elements visited that carry an /S (i.e. real elements).</summary>
            internal int Elements;

            /// <summary>
            /// Elements whose <c>/K</c> reaches no content at all: no MCID, no <c>/MCR</c>, no
            /// <c>/OBJR</c> and no child element. Such an element describes nothing.
            /// </summary>
            internal int ElementsWithoutContent;

            /// <summary>
            /// Elements that do reach content, i.e. the tree is connected to the page somewhere.
            /// </summary>
            internal int ElementsWithContent;

            /// <summary>
            /// Marked-content identifiers the tree <b>names</b>, as (page object number, MCID).
            /// </summary>
            /// <remarks>
            /// <para>Only references that resolve to a page and carry <b>no <c>/Stm</c></b> are
            /// collected. An <c>/MCR</c> with <c>/Stm</c> points into a form XObject's stream
            /// rather than the page's own content, and this checker scans page content only —
            /// so including those would compare an MCID against the wrong stream and invent
            /// violations. They are skipped and counted in <see cref="McidsSkippedForStm"/>.</para>
            /// </remarks>
            internal readonly List<KeyValuePair<int, int>> NamedMcids = new List<KeyValuePair<int, int>>();

            /// <summary>References skipped because they name a stream other than the page's.</summary>
            internal int McidsSkippedForStm;
        }

        private static StructTreeScan RunStructTreeScan(PdfDocument doc, PdfDictionary catalog,
            List<PdfUaViolation> v)
        {
            var scan = new StructTreeScan();
            try
            {
                var strTree = ResolveDict(doc, catalog[PdfName.StructTreeRoot]);
                if (strTree == null) return scan; // UA05 reports the absence.

                var roleMap = BuildRoleMap(doc, strTree);
                WalkStructTree(strTree, doc, roleMap, scan);

                if (scan.BudgetExhausted)
                {
                    Report("PdfUaChecker.RunStructTreeScan",
                        "structure-tree traversal stopped at " + scan.Ceiling + "; " +
                        "the structure-derived checks (UA07, UA27) are partial");
                    // …AND a UA99. Reporting only into the [ThreadStatic] slot left the returned
                    // result saying nothing: a tree truncated after a clean prefix produced an
                    // empty violation list and IsConforming == true for a document full of
                    // un-alt-texted figures, and the caller had to know to read a static property
                    // afterwards — which PdfUaValidationPlugin, the way a host reaches this, never
                    // does. The class doc for UA99 already stated the opposite rule.
                    v.Add(new PdfUaViolation("UA99",
                        "The structure-tree walk stopped at " + scan.Ceiling + ", so the " +
                        "structure-derived checks (UA07 figure alt text, UA27 link nesting, " +
                        "UA28/UA29 hollow structure) examined only part of the tree."));
                }
            }
            catch (Exception ex)
            {
                Report("PdfUaChecker.RunStructTreeScan", "structure-tree scan aborted", ex);
                v.Add(new PdfUaViolation("UA99",
                    $"The structure-tree scan could not be completed ({ex.GetType().Name}: " +
                    $"{ex.Message}). The conformance report is incomplete."));
            }
            return scan;
        }

        // ----------------------------------------------------------------
        //  UA50 — an MCID the tree names must exist in the page's content
        // ----------------------------------------------------------------

        /// <summary>
        /// Maximum decoded page-content bytes this check will read, per page. A hollow-tagging
        /// check is not worth an unbounded inflate; past the ceiling the page is reported through
        /// <c>UA99</c> instead of being silently treated as "no MCIDs found".
        /// </summary>
        public const int DefaultMaxMcidScanBytesPerPage = 8 * 1024 * 1024;

        /// <summary>
        /// Default decoded page-content bytes this check will read across the <b>whole document</b>
        /// (128 MiB), matching <c>PdfXValidator.DefaultMaxDocumentContentBytes</c>. Override it
        /// through <see cref="Validate(Stream, int, long)"/>.
        /// </summary>
        /// <remarks>
        /// <b>A per-page cap alone does not bound a 50 000-page file</b> — the sentence is
        /// <c>PdfXValidator</c>'s, and both content walks in this assembly carry both halves. Only
        /// reading the walks against each other shows a ceiling present in one and absent in the
        /// other: each is individually reasonable, and no test of either can see what the other
        /// bounds.
        /// </remarks>
        public const long DefaultMaxMcidScanBytesPerDocument = 128L * 1024 * 1024;

        /// <summary><c>/Stm</c> — an MCR entry naming a content stream other than the page's.</summary>
        private static readonly PdfName StmKey = PdfName.Stm;

        /// <summary>
        /// UA50 — a structure element names a marked-content identifier that its page's content
        /// stream never defines, so the element describes nothing.
        /// </summary>
        /// <remarks>
        /// <para><b>A content-level check in a plugin.</b> Decoding a content stream needs
        /// <c>PdfStream.GetDecodedData()</c> and <c>Core.Text.ContentStreamParser</c>, both in
        /// Core, so a plugin that cannot reference a feature can still make one;
        /// <c>PdfXValidator</c> in this assembly parses content streams too.</para>
        /// <para><b>Only the safe direction is checked.</b> "The tree names MCID <i>n</i> and no
        /// <c>BDC</c> in that page defines it" is unambiguous. The converse — text that no MCID
        /// covers — is not: marked content nests through a <c>Do</c>, so a form XObject's text can
        /// legitimately be covered by a <c>BDC</c> in the invoking page stream. That belongs to
        /// the deep checker, which follows forms.</para>
        /// <para>References carrying <c>/Stm</c> are skipped for the same reason and counted, so
        /// "found nothing" cannot be mistaken for "there is nothing".</para>
        /// </remarks>
        private static void CheckNamedMcidsExist(PdfDocument doc, List<PageRef> pages,
            bool encrypted, StructTreeScan scan, List<PdfUaViolation> v,
            int maxPageContentBytes, long maxDocumentContentBytes)
        {
            // Reported even when nothing else here runs: a reference carrying /Stm points into a
            // content stream other than the page's, which this check does not decode. The counter
            // existed for exactly this and was read by nothing, so a document whose structure
            // elements ALL use /Stm — the ordinary shape when a producer tags placed artwork —
            // produced a report byte-identical to one where UA50 ran and found every MCID present.
            if (scan.McidsSkippedForStm > 0)
                v.Add(new PdfUaViolation("UA99",
                    $"{scan.McidsSkippedForStm} marked-content reference(s) carry /Stm, so they " +
                    "name content in a stream other than the page's own; UA50 did not check those."));

            if (scan.NamedMcids.Count == 0) return;

            // An encrypted document's content streams are ciphertext, and this check reads them.
            // Decoding them anyway would find no BDC and report every named MCID as missing — a
            // false UA50 on a file ISO 14289-1 §7.1 expressly permits. Said out loud instead.
            if (encrypted)
            {
                v.Add(new PdfUaViolation("UA99",
                    "The document is encrypted, so its page content streams could not be decoded " +
                    "and UA50 (every named marked-content identifier exists) did not run."));
                return;
            }

            // The pages the audit COLLECTED, not doc.Pages. PdfDocument loads no page tree for an
            // encrypted document, so doc.Pages is empty there while CollectPages' hand-written
            // fallback has the pages — this check then looped over nothing and returned, a result
            // indistinguishable from "every named MCID exists".
            if (pages == null || pages.Count == 0)
            {
                v.Add(new PdfUaViolation("UA99",
                    "No page could be read for UA50, so the check that every marked-content " +
                    "identifier named by the structure tree exists did not run."));
                return;
            }

            // Group the named MCIDs by page so each page's content is decoded at most once.
            var byPage = new Dictionary<int, HashSet<int>>();
            foreach (var named in scan.NamedMcids)
            {
                if (!byPage.TryGetValue(named.Key, out var set))
                    byPage[named.Key] = set = new HashSet<int>();
                set.Add(named.Value);
            }

            var missing = new List<string>();
            // One running total across every page, so the per-page ceiling cannot be multiplied by
            // the page count. Its absence here was the divergence from PdfXValidator's walk.
            long documentBytes = 0;

            foreach (var page in pages)
            {
                int objNum = page.Dict?.ObjectNumber ?? 0;
                if (objNum <= 0 || !byPage.TryGetValue(objNum, out var named)) continue;

                HashSet<int> defined = DefinedMcids(doc, page, objNum, v, ref documentBytes,
                    maxPageContentBytes, maxDocumentContentBytes);
                if (defined == null) continue;   // unreadable or over the ceiling; UA99 raised

                foreach (int mcid in named)
                    if (!defined.Contains(mcid))
                        missing.Add($"page object {objNum} MCID {mcid}");
            }

            if (missing.Count == 0) return;

            // Cap the enumeration: a systematically broken tree would otherwise produce a
            // message thousands of entries long.
            const int MaxListed = 10;
            string list = string.Join(", ", missing.GetRange(0, Math.Min(MaxListed, missing.Count)));
            if (missing.Count > MaxListed) list += $", … ({missing.Count} in total)";

            v.Add(new PdfUaViolation("UA50",
                "The structure tree names marked-content identifiers that the page's content " +
                $"stream does not define, so those elements describe nothing: {list}. " +
                "Re-tag the page so every structure element points at marked content that exists."));
        }

        /// <summary>
        /// The MCIDs a page's own content stream defines, or <c>null</c> when it could not be
        /// read — in which case a <c>UA99</c> is added, because an empty set would otherwise read
        /// as "this page defines no MCIDs" and turn an unreadable page into violations.
        /// </summary>
        private static HashSet<int> DefinedMcids(PdfDocument doc, PageRef page, int objNum,
            List<PdfUaViolation> v, ref long documentBytes,
            int maxPageContentBytes, long maxDocumentContentBytes,
            MarkedContentLanguages languages = null, string purpose = "UA50 (marked-content identifiers exist)")
        {
            long remaining = Math.Min(maxPageContentBytes,
                                      maxDocumentContentBytes - documentBytes);
            if (remaining <= 0)
            {
                v.Add(new PdfUaViolation("UA99",
                    $"The {maxDocumentContentBytes}-byte document content ceiling was reached " +
                    $"before page object {objNum}, so {purpose} did not run for it or for any later page."));
                return null;
            }

            byte[] content;
            string refusal;
            try
            {
                content = PageContentBytesWithinCeiling(doc, page.Dict, remaining, out refusal);
            }
            catch (Exception ex)
            {
                Report("PdfUaChecker.DefinedMcids", $"page object {objNum} content could not be decoded", ex);
                v.Add(new PdfUaViolation("UA99",
                    $"The content stream of page object {objNum} could not be decoded, so {purpose} " +
                    "did not run for it."));
                return null;
            }

            if (refusal != null)
            {
                v.Add(new PdfUaViolation("UA99",
                    $"The content stream of page object {objNum} was not decoded within the " +
                    $"{remaining} bytes the scan ceilings leave, or could not be decoded at all " +
                    $"({refusal}), so {purpose} did not run for it."));
                return null;
            }

            if (content == null) return new HashSet<int>();

            documentBytes += content.Length;

            var defined = new HashSet<int>();
            // UA02's frames: one per open marked-content sequence — whether a language is declared
            // for what it holds (an enclosing /Lang, or its MCID's structure element's) and whether
            // it is an artifact, whose text needs none. Text outside every sequence has neither.
            var frames = languages == null ? null : new Stack<(bool Language, bool Artifact)>();
            // One content stream decodes to the stream's own cached array, the one UA02's run, UA50's
            // and the font walk's scan all hold, so its parse is kept for them; an array-valued
            // /Contents is concatenated afresh on every run and would only fill the cache.
            bool shared = Resolve(doc, page.Dict?[PdfName.Contents]) is PdfStream;
            try
            {
                foreach (var op in shared ? ScanReuse.ParseAndKeep(content) : ContentStreamParser.Parse(content))
                {
                    if (frames != null)
                    {
                        switch (op.Operator)
                        {
                            case PdfOperators.BeginMarkedContent:
                            case PdfOperators.BeginMarkedContentProps:
                            {
                                var outer = frames.Count > 0 ? frames.Peek() : (Language: false, Artifact: false);
                                bool artifact = outer.Artifact
                                    || (op.Operands.Length > 0 && (op.Operands[0] as PdfName)?.Value == "Artifact");
                                var props = op.Operator == PdfOperators.BeginMarkedContent || op.Operands.Length < 2 ? null
                                    : op.Operands[1] as PdfDictionary
                                      ?? (op.Operands[1] is PdfName named
                                          ? ResolveDict(doc, ResolveDict(doc, page.Resources?[PdfName.Properties])?[named])
                                          : null);
                                var mcidNumber = Resolve(doc, props?[McidKey]) as PdfNumber;
                                var tag = ResolveString(doc, props?[PdfName.Lang]);
                                if (tag != null && !IsLanguageIdentifier(tag.GetText()))
                                    languages.InvalidLanguageTags++;
                                bool language = outer.Language
                                    || HasNonEmptyString(doc, props?[PdfName.Lang])
                                    || (mcidNumber != null && McidOf(mcidNumber) is int languageMcid && languages.StructureDeclares(languageMcid));
                                frames.Push((language, artifact));
                                break;
                            }
                            case PdfOperators.EndMarkedContent:
                                if (frames.Count > 0) frames.Pop();
                                break;
                            case PdfOperators.ShowText:
                            case PdfOperators.ShowTextArray:
                            case PdfOperators.NextLineShowText:
                            case PdfOperators.NextLineSetSpacingShowText:
                                var current = frames.Count > 0 ? frames.Peek() : (Language: false, Artifact: false);
                                if (!current.Language && !current.Artifact)
                                    languages.TextWithoutLanguage++;
                                break;
                        }
                    }

                    // BDC takes /Tag and a property list: either an inline dictionary or a name
                    // referring to /Properties. Only the inline form carries the MCID directly;
                    // the named form is resolved through the page resources below.
                    if (op.Operator != PdfOperators.BeginMarkedContentProps) continue;
                    if (op.Operands.Length < 2) continue;

                    if (op.Operands[1] is PdfDictionary inline &&
                        inline[McidKey] is PdfNumber n)
                    {
                        if (McidOf(n) is int inlineMcid) defined.Add(inlineMcid);
                        continue;
                    }

                    if (op.Operands[1] is PdfName propName)
                    {
                        // PageRef.Resources, which the page walk already resolved through
                        // §7.7.3.4's inheritance — a page whose /Resources lives on an ancestor
                        // /Pages node still finds its /Properties.
                        var props = ResolveDict(doc, page.Resources?[PdfName.Properties]);
                        var entry = ResolveDict(doc, props?[propName]);

                        // Resolved, like the two lines above it. `entry` is a /Properties entry in
                        // the PAGE RESOURCES — a real document object, which may legally hold
                        // indirect values — not an inline content-stream dictionary, so the
                        // exemption that covers the inline `BDC` operand does not apply here.
                        //
                        // The direction is the damaging one: the MCID is simply not added to
                        // `defined`, and CheckNamedMcidsExist then reports a structure element as
                        // pointing at marked content the page never defines. A UA50 violation
                        // invented for a conforming file.
                        if (Resolve(doc, entry?[McidKey]) is PdfNumber viaProps && McidOf(viaProps) is int namedMcid)
                            defined.Add(namedMcid);
                    }
                }
            }
            catch (Exception ex)
            {
                Report("PdfUaChecker.DefinedMcids", $"page object {objNum} content could not be parsed", ex);
                v.Add(new PdfUaViolation("UA99",
                    $"The content stream of page object {objNum} could not be parsed, so {purpose} " +
                    "did not run for it."));
                return null;
            }

            return defined;
        }

        /// <summary>
        /// Detects a <b>hollow</b> structure tree: one whose elements are not connected to any
        /// page content, so it describes a document that is not really tagged.
        ///
        /// <para>
        /// <b>Why this check exists here as well as in the Accessibility feature.</b> This plugin
        /// ships as its own package and is validated standalone. Without these two rules a file
        /// with a <c>/StructTreeRoot</c>, a handful of empty <c>/StructElem</c>s, no
        /// <c>/ParentTree</c> and no marked content anywhere reported <b>zero</b> violations —
        /// the single worst outcome an accessibility checker can produce, because the file looks
        /// certified and a screen reader gets nothing from it.
        /// </para>
        ///
        /// <para>
        /// Both rules are dictionary-level: the plugin does not decode content streams, so it
        /// cannot see whether the MCIDs an element names really exist. That remains the deeper
        /// checker's job (UA16–UA18 in <c>Tetitulz.Pdf.Accessibility</c>) and is why
        /// <see cref="PdfUaValidationResult.IsConforming"/> is not a conformance certificate.
        /// </para>
        /// </summary>
        private static void CheckHollowStructure(PdfDocument doc, PdfDictionary catalog,
            StructTreeScan scan, List<PdfUaViolation> v)
        {
            var structTree = ResolveDict(doc, catalog[PdfName.StructTreeRoot]);
            if (structTree == null) return;   // UA05 already reports the absence.

            // UA28 — no /ParentTree. ISO 32000-1 Table 322 makes it "required if any structure
            // element contains content items": it is the only route from a page's marked content
            // back to the element that owns it. Conditioned on the tree actually reaching content,
            // so a tree that reaches none is reported once, by UA29, which says the stronger thing.
            if (scan.ElementsWithContent > 0
                && Resolve(doc, structTree[PdfName.ParentTree]) == null)
            {
                v.Add(new PdfUaViolation("UA28",
                    "The structure tree has no /ParentTree. Marked content on a page can only be " +
                    "resolved to a structure element through that number tree, so no content in this " +
                    "document is reachable from its structure tree — the tagging is not connected to " +
                    "anything. Add a /ParentTree whose /Nums maps each page's /StructParents key to " +
                    "its structure elements."));
            }

            // UA29 — elements that reach no content at all.
            if (scan.ElementsWithoutContent > 0)
            {
                bool everything = scan.ElementsWithContent == 0;
                v.Add(new PdfUaViolation("UA29",
                    $"{scan.ElementsWithoutContent} of {scan.Elements} structure element(s) reference no " +
                    "content: no marked-content identifier, no /MCR, no /OBJR and no child element. " +
                    (everything
                        ? "No element in this tree is connected to any page content, so the document is " +
                          "not tagged in any meaningful sense despite declaring a structure tree. "
                        : "Those elements describe content that does not exist. ") +
                    "Point each element's /K at the marked content it describes, or remove it."));
            }
        }

        /// <summary>
        /// Reads /RoleMap so that a document-specific role such as <c>/MyImage</c> mapped to
        /// <c>/Figure</c> is checked as a figure, exactly as a conforming reader would.
        /// </summary>
        private static Dictionary<string, string> BuildRoleMap(PdfDocument doc, PdfDictionary structTreeRoot)
            => PdfRoleMap.Build(structTreeRoot, o => Resolve(doc, o));

        /// <summary>
        /// One implementation, in <see cref="PdfRoleMap.Resolve"/>.
        /// </summary>
        /// <remarks>
        /// A copy with a hop cap and <b>no visited set</b> would stop on a three-node cycle wherever
        /// 16 mod 3 lands and return a role from the middle of the chain, while the accessibility
        /// feature stops at the first repeat and returns the role the chain re-enters: one file,
        /// two answers.
        /// </remarks>
        private static string CanonicalRole(string role, Dictionary<string, string> roleMap)
            => PdfRoleMap.Resolve(role, roleMap);

        /// <summary>
        /// One pass over the structure tree through <see cref="StructureTreeWalk"/> — the walk shared
        /// with the accessibility feature, the tagged-text extractor and <c>PdfStructureTreeReader</c>.
        /// </summary>
        /// <remarks>
        /// <para>Per node: UA07's figure test on the raw <i>or</i> mapped role, the element count, and
        /// — once the walk leaves it — whether its <c>/K</c> reached content. Per <c>/K</c> slot: the
        /// MCID it names and, under a <c>&lt;Link&gt;</c> carrying an <c>/Alt</c>, the annotation an
        /// <c>/OBJR</c> points at. ISO 32000-1 Table 323 makes <c>/Pg</c> inheritable, so the page an
        /// MCID resolves against is the node's own or the nearest ancestor's — carried as the page's
        /// object number, which is how the content scan keys its marked content.</para>
        /// <para>The ceilings are this checker's own: <see cref="MaxStructDepth"/>, and
        /// <see cref="MaxStructNodes"/> for nodes entered and for <c>/K</c> slots examined alike — the
        /// latter because a visited set stops re-expansion and does nothing about the cost of one
        /// expansion, so N elements naming one shared N-entry array cost N² while a per-node count
        /// moves N times. Any ceiling makes the scan partial, which <see cref="RunStructTreeScan"/>
        /// reports and records as a UA99.</para>
        /// </remarks>
        private static void WalkStructTree(PdfDictionary root, PdfDocument doc,
            Dictionary<string, string> roleMap, StructTreeScan scan)
        {
            var walk = new StructureTreeWalk(doc)
            {
                MaxDepth = MaxStructDepth,
                MaxNodes = MaxStructNodes,
                MaxEdges = MaxStructNodes,
            };

            walk.Report = (walkEvent, at, objectNumber, ex) =>
            {
                switch (walkEvent)
                {
                    case StructureTreeWalkEvent.DepthCapReached:
                        scan.BudgetExhausted = true;
                        scan.Ceiling ??= $"the {MaxStructDepth}-level depth cap";
                        break;
                    case StructureTreeWalkEvent.NodeCapReached:
                        scan.BudgetExhausted = true;
                        scan.Ceiling ??= $"the {MaxStructNodes}-node ceiling";
                        break;
                    case StructureTreeWalkEvent.EdgeCapReached:
                        scan.BudgetExhausted = true;
                        scan.Ceiling ??= $"the {MaxStructNodes}-slot /K ceiling";
                        break;
                    case StructureTreeWalkEvent.UnresolvableKid:
                    case StructureTreeWalkEvent.UnresolvableKids:
                        Report("PdfUaChecker.Resolve", "could not resolve indirect reference", ex);
                        break;
                    // A revisit is a shared subtree (legal: §14.7.2 permits a DAG) or a cycle, entered
                    // once; it is neither reported nor a reason to call the scan partial.
                }
            };

            walk.Enter = node =>
            {
                string rawRole = node.Role;
                string role = CanonicalRole(rawRole, roleMap);

                // Raw OR mapped, not the canonical name alone. §14.7.4 forbids remapping a standard
                // type, but nothing stops a file containing /RoleMap << /Figure /Div >> — and testing
                // only the canonical name then reports every <Figure> in the document as a <Div>, so
                // EVERY un-alt-texted figure becomes invisible and UA07 says the document is clean.
                // UA07 is owned exclusively by this checker (the deep one defers to it), so there is no
                // second opinion to catch it. Asking "does this element MEAN Figure" cannot be defeated
                // that way and needs no standard-type vocabulary to decide.
                if ((rawRole == "Figure" || role == "Figure") && !HasTextAlternative(doc, node.Dictionary))
                    scan.FiguresMissingAlt++;

                var state = new StructNodeState
                {
                    Role = role,
                    // Only nodes with /S are structure elements; the /StructTreeRoot itself is not one.
                    IsElement = rawRole != null,
                    // ISO 14289-1 §7.18.1: a Link annotation's alternate description may come from the
                    // enclosing <Link> structure element's /Alt instead of the annotation's /Contents.
                    LinkProvidesAlt = role == "Link" && HasNonEmptyString(doc, node.Dictionary[PdfName.Alt]),
                    ProvidesAlt = HasNonEmptyString(doc, node.Dictionary[PdfName.Alt]),
                    DeclaresLanguage = HasNonEmptyString(doc, node.Dictionary[PdfName.Lang])
                        || ((node.Parent?.Tag as StructNodeState)?.DeclaresLanguage ?? false),
                    // /Pg is inheritable (Table 323): this node's own entry wins, else the ancestor's.
                    Page = PageObjectNumber(doc, node.Dictionary) is int own && own > 0
                        ? own
                        : (node.Parent?.Tag as StructNodeState)?.Page ?? 0,
                };
                node.Tag = state;

                if (state.IsElement) scan.Elements++;

                // rules 7.2-21 to -23: an alternate text whose language nothing declares.
                // Counted always, judged by UA02 only when the catalog has no /Lang.
                if (state.IsElement && HasNonEmptyString(doc, node.Dictionary[PdfName.Lang]))
                    scan.ElementsWithLanguage++;

                if (state.IsElement && ResolveString(doc, node.Dictionary[PdfName.Lang]) is PdfString lang
                    && !IsLanguageIdentifier(lang.GetText()))
                    scan.InvalidLanguageTags++;

                if (state.IsElement && !state.DeclaresLanguage
                    && (node.Dictionary[PdfName.Alt] != null || node.Dictionary[PdfName.ActualText] != null
                        || node.Dictionary[PdfName.E] != null))
                    scan.AlternatesWithoutLanguage++;
            };

            walk.Kid = kid =>
            {
                var state = (StructNodeState)kid.Parent.Tag;
                if (ReachesContent(kid)) state.ReachesContent = true;
                int? mcid = CollectNamedMcid(kid.Resolved, doc, scan, state.Page);
                if (mcid.HasValue && state.DeclaresLanguage)
                    scan.McidsWithLanguage.Add(McidKeyOf(state.Page, mcid.Value));

                if (kid.Kind == StructKidKind.ObjectReference && (state.LinkProvidesAlt || state.ProvidesAlt || state.DeclaresLanguage))
                {
                    var objRef = ((PdfDictionary)kid.Resolved)[PdfName.Obj];
                    int annotObjNum = (objRef as PdfReference)?.ReferenceObjectNumber
                                      ?? (Resolve(doc, objRef)?.ObjectNumber ?? 0);
                    if (annotObjNum > 0)
                    {
                        if (state.LinkProvidesAlt) scan.LinkAnnotationsWithStructAlt.Add(annotObjNum);
                        if (state.ProvidesAlt) scan.AnnotationsWithStructAlt.Add(annotObjNum);
                        if (state.DeclaresLanguage) scan.AnnotationsWithLanguage.Add(annotObjNum);
                    }
                }
            };

            walk.Leave = node =>
            {
                var state = (StructNodeState)node.Tag;
                if (!state.IsElement) return;

                // An element whose /K reaches nothing describes nothing. An empty <TD>/<TH> keeps a
                // table grid rectangular and an empty <Lbl> is legal, so those are not counted.
                if (state.ReachesContent) scan.ElementsWithContent++;
                else if (!RoleMayBeEmpty(state.Role)) scan.ElementsWithoutContent++;
            };

            walk.Run(root);
        }

        /// <summary>What the scan knows about one node while the walk is inside it.</summary>
        private sealed class StructNodeState
        {
            internal string Role;
            internal bool IsElement;
            internal bool LinkProvidesAlt;
            /// <summary>A non-empty /Alt, which describes an annotation this element references.</summary>
            internal bool ProvidesAlt;
            /// <summary>This element or an ancestor has a non-empty /Lang.</summary>
            internal bool DeclaresLanguage;
            internal bool ReachesContent;
            /// <summary>Object number of the page this node belongs to, or 0 when unknown.</summary>
            internal int Page;
        }

        /// <summary>
        /// Whether a <c>/K</c> slot reaches real content — a marked-content identifier, an <c>/MCR</c>,
        /// an <c>/OBJR</c> (either of the latter two with or without its Optional <c>/Type</c>), or a
        /// nested structure element. A nested element counts, visited already or not: the subtree is
        /// judged on its own terms, and treating a parent as empty because its children hold the
        /// MCIDs would flag every interior node.
        /// </summary>
        private static bool ReachesContent(StructureTreeKid kid)
            => kid.IsMarkedContentIdentifier
            || (kid.Resolved is PdfDictionary && kid.Kind != StructKidKind.Unknown);

        /// <summary>
        /// Roles for which having no content is legitimate: an empty table cell keeps a grid
        /// rectangular (and is required by the regularity rules), and an empty label is legal.
        /// </summary>
        private static bool RoleMayBeEmpty(string role)
            => role == "TD" || role == "TH" || role == "Lbl" || role == "Private" || role == "Artifact";

        /// <summary>
        /// Classification of one <c>/K</c> child dictionary.
        ///
        /// <para>
        /// <b>/Type is Optional on both content-reference forms.</b> ISO 32000-1 Table 324 makes
        /// <c>/MCID</c> the only required entry of a marked-content reference and Table 325 makes
        /// <c>/Obj</c> the only required entry of an object reference; <c>/Type</c> is Optional in
        /// both. Insisting on <c>/Type /MCR</c> / <c>/Type /OBJR</c> made every element whose /K is
        /// a typeless (and entirely legal) reference look empty, which produced a false UA29 with
        /// the strongest wording the checker owns, and silently disabled the ISO 14289-1 §7.18.1
        /// escape hatch that lets a &lt;Link&gt; element's /Alt describe its annotation.
        /// </para>
        ///
        /// <para><b>This ladder is <c>Base.Operations.StructKidClassifier</c>'s</b>, with the two
        /// Optional <c>/Type</c>s handled; the shared enum has the four values this checker
        /// reads.</para>
        /// </summary>
        private static StructKidKind ClassifyStructKid(PdfDocument doc, PdfDictionary dict)
            => StructKidClassifier.Classify(dict, doc);

        /// <summary>
        /// Records the MCID a <c>/K</c> child names, against the page it belongs to.
        /// </summary>
        /// <remarks>
        /// Two shapes carry an MCID (§14.7.4.2): a bare integer, and a dictionary whose
        /// <c>/MCID</c> entry is one — the latter with or without <c>/Type /MCR</c>, since
        /// Table 324 makes <c>/MCID</c> the only required entry. A dictionary that also carries
        /// <c>/Stm</c> points into some other content stream (a form XObject's, typically) and is
        /// deliberately skipped: this checker reads page content only, so measuring such an MCID
        /// against the page's stream would invent a violation.
        /// </remarks>
        /// <returns>The MCID recorded, or <see langword="null"/> when the kid names none on a page.</returns>
        private static int? CollectNamedMcid(PdfObject kid, PdfDocument doc, StructTreeScan scan, int page)
        {
            if (page <= 0) return null; // No page to resolve the MCID against.

            var resolved = Resolve(doc, kid);

            if (resolved is PdfNumber direct)
            {
                int directMcid = McidOf(direct) ?? NoMcid;
                scan.NamedMcids.Add(new KeyValuePair<int, int>(page, directMcid));
                return directMcid;
            }

            if (!(resolved is PdfDictionary dict)) return null;
            if (!(Resolve(doc, dict[McidKey]) is PdfNumber mcid)) return null;

            if (dict[StmKey] != null) { scan.McidsSkippedForStm++; return null; }

            int namedMcid = McidOf(mcid) ?? NoMcid;
            scan.NamedMcids.Add(new KeyValuePair<int, int>(page, namedMcid));
            return namedMcid;
        }

        /// <summary>
        /// What a structure kid naming an invalid MCID is recorded as: no page defines it, so the
        /// kid is reported as pointing at marked content the page lacks — never dropped.
        /// </summary>
        private const int NoMcid = -1;

        /// <summary>
        /// An MCID (a non-negative integer) as an <c>int</c>, or null for anything else.
        /// Not <c>(int)LongValue</c>, which keeps the low 32 bits: MCID 4 294 967 297 read as 1 and
        /// matched marked content 1, so a structure element pointing nowhere passed UA50.
        /// </summary>
        private static int? McidOf(PdfNumber number)
            => number.IsInteger && number.LongValue >= 0 && number.LongValue <= int.MaxValue
                ? (int)number.LongValue
                : (int?)null;

        /// <summary>Object number of the page a structure node's <c>/Pg</c> names, else 0.</summary>
        private static int PageObjectNumber(PdfDocument doc, PdfDictionary node)
        {
            var pg = node[PdfName.Pg];
            if (pg == null) return 0;
            if (pg is PdfReference r) return r.ReferenceObjectNumber;
            return Resolve(doc, pg)?.ObjectNumber ?? 0;
        }

        /// <summary>
        /// ISO 14289-1 §7.3: a Figure needs an alternate description (/Alt) or replacement
        /// text (/ActualText). Either satisfies assistive technology.
        /// </summary>
        /// <remarks>
        /// An empty /Alt describes nothing, but an /ActualText is replacement text even when empty
        /// (§14.9.4: the element's content reads as that text — here, as nothing). the rule
        /// 7.3-1 is <c>(Alt != null &amp;&amp; Alt != '') || ActualText != null</c>, and its corpus
        /// passes a Figure whose ActualText is empty (7.3-t01-pass-c).
        /// </remarks>
        private static bool HasTextAlternative(PdfDocument doc, PdfDictionary node)
            => HasNonEmptyString(doc, node[PdfName.Alt])
            || ResolveString(doc, node[PdfName.ActualText]) != null;

        // ----------------------------------------------------------------
        //  UA07 — figures must have alternate text
        // ----------------------------------------------------------------

        private static void CheckFigureAltText(StructTreeScan scan, List<PdfUaViolation> v)
        {
            if (scan.FiguresMissingAlt > 0)
                v.Add(new PdfUaViolation("UA07",
                    $"{scan.FiguresMissingAlt} Figure element(s) in the structure tree are missing " +
                    "an /Alt (alternate text) or /ActualText entry. PDF/UA-1 requires a text " +
                    "alternative for all figures."));
        }

        // ----------------------------------------------------------------
        //  UA08 — form fields must have /TU
        // ----------------------------------------------------------------

        /// <summary>
        /// Counts <b>terminal</b> form fields with no usable /TU.
        /// <para>
        /// The traversal itself lives in <see cref="AcroFormFieldWalk"/> and is shared with
        /// <see cref="PdfAccessibilityFixer"/>. That is the point: this check decides a document is
        /// broken and the fixer decides what to repair, so the two must classify every dictionary
        /// identically. Nothing here may re-derive
        /// what a field is, which of them are terminal, or how /FT is inherited.
        /// </para>
        /// </summary>
        private static void CheckFormFieldTooltips(PdfDocument doc, PdfDictionary catalog,
            List<PageRef> pages, StructTreeScan scan, List<PdfUaViolation> v)
        {
            var walk = AcroFormFieldWalk.Walk(catalog, obj => Resolve(doc, obj));

            if (walk.BudgetExhausted)
                Report("PdfUaChecker.CollectMissingTooltips",
                    $"field traversal stopped after {AcroFormFieldWalk.MaxFieldNodes} nodes; UA08 is partial");

            // The other way the walk can come back short: the /Kids descent stops at
            // MaxFieldDepth, and a truncated tree and a fully-walked one would otherwise give the
            // same empty answer. Reported for the same reason the budget is — "no field needs a
            // tooltip" must never quietly mean "none of the ones I reached".
            if (walk.DepthExceeded)
                Report("PdfUaChecker.CollectMissingTooltips",
                    $"field traversal stopped at {AcroFormFieldWalk.MaxFieldDepth} levels of /Kids; UA08 is partial");

            // ISO 14289-1 §7.18.1 does not apply to a widget that is hidden or outside the CropBox,
            // and its fourth paragraph accepts the widget's alternative description — the enclosing
            // element's /Alt — for a field with no /TU: the rule 7.18.1-3 and its corpus
            // (7.18.1-t03-pass-b to -d). The walk still answers which fields lack /TU (the fixer
            // adds one to each); this side decides which of them §7.18.1 leaves described.
            var widgetsInScope = WidgetsPresentedToReader(doc, pages);
            int missing = 0;
            for (int i = 0; i < walk.Fields.Count; i++)
            {
                if (walk.Fields[i].NeedsAccessibleName
                    && HasUndescribedWidget(doc, walk.Fields[i].Dictionary, widgetsInScope, scan))
                    missing++;
            }

            if (missing > 0)
                v.Add(new PdfUaViolation("UA08",
                    $"{missing} interactive form field(s) are missing a /TU (tooltip / accessible name) entry. " +
                    "PDF/UA-1 requires all form fields to have an accessible name."));
        }

        /// <summary>
        /// Object numbers of the widgets on the pages, each mapped to whether §7.18.1 applies to it
        /// (not hidden, not outside its page's CropBox).
        /// </summary>
        private static Dictionary<int, bool> WidgetsPresentedToReader(PdfDocument doc, List<PageRef> pages)
        {
            var widgets = new Dictionary<int, bool>();
            foreach (var page in pages)
            {
                var annots = ResolveArray(doc, page.Dict?[PdfName.Annots]);
                if (!ScanReuse.AdmitAnnotations(annots)) continue;
                for (int i = 0; i < annots.Count; i++)
                {
                    var annot = ResolveDict(doc, annots[i]);
                    if (annot == null) continue;
                    if (ResolveName(doc, annot[PdfName.Subtype])?.Value != PdfAnnotationSubtypes.Widget) continue;
                    int objNum = (annots[i] as PdfReference)?.ReferenceObjectNumber ?? annot.ObjectNumber;
                    if (objNum <= 0) continue;
                    int? flags = PdfAnnotationFlags.FromNumber(Resolve(doc, annot[PdfName.F]));
                    widgets[objNum] = PdfUaAnnotationScope.IsPresentedToReader(PdfAnnotationSubtypes.Widget, flags,
                        ResolveArray(doc, annot[PdfName.Rect]), CropBoxOf(doc, page), doc.ResolveObject);
                }
            }
            return widgets;
        }

        /// <summary>
        /// Whether a terminal field without /TU has a widget §7.18.1 applies to and no structure
        /// element describes. The widgets are the field itself when it is merged with its one widget,
        /// else its /Kids that are not fields; a field none of whose widgets is on a page keeps the
        /// verdict the walk gave it.
        /// </summary>
        private static bool HasUndescribedWidget(PdfDocument doc, PdfDictionary field,
            Dictionary<int, bool> widgetsInScope, StructTreeScan scan)
        {
            var widgets = new List<int>();
            if (field.ObjectNumber > 0 && widgetsInScope.ContainsKey(field.ObjectNumber))
                widgets.Add(field.ObjectNumber);
            var kids = ResolveArray(doc, field[PdfName.Kids]);
            if (kids != null)
                for (int i = 0; i < kids.Count; i++)
                {
                    int objNum = (kids[i] as PdfReference)?.ReferenceObjectNumber ?? ResolveDict(doc, kids[i])?.ObjectNumber ?? 0;
                    if (objNum > 0 && widgetsInScope.ContainsKey(objNum)) widgets.Add(objNum);
                }
            if (widgets.Count == 0) return true;

            foreach (int widget in widgets)
                if (widgetsInScope[widget] && !scan.AnnotationsWithStructAlt.Contains(widget))
                    return true;
            return false;
        }

        /// <summary>A page's CropBox, inherited and defaulted to its MediaBox; null when the page is
        /// not one <see cref="PdfDocument.Pages"/> loaded (an encrypted document's hand-walked pages).</summary>
        private static PdfArray CropBoxOf(PdfDocument doc, PageRef page)
            => page.Index >= 0 && page.Index < doc.Pages.Count && ReferenceEquals(doc.Pages[page.Index].Dictionary, page.Dict)
                ? doc.Pages[page.Index].CropBox
                : null;

        // ----------------------------------------------------------------
        //  Page enumeration
        // ----------------------------------------------------------------

        /// <summary>A page dictionary paired with its (possibly inherited) resource dictionary.</summary>
        private readonly struct PageRef
        {
            internal PageRef(PdfDictionary dict, PdfDictionary resources, int index)
            {
                Dict = dict;
                Resources = resources;
                Index = index;
            }

            internal PdfDictionary Dict { get; }
            internal PdfDictionary Resources { get; }
            internal int Index { get; }
        }

        private static List<PageRef> CollectPages(PdfDocument doc, PdfDictionary catalog,
            List<PdfUaViolation> v)
        {
            var pages = new List<PageRef>();

            // Fast path: the parser already resolved the page tree (with attribute
            // inheritance) for every document it could load a catalog for — which is every
            // document except an encrypted one. This branch is therefore the one nearly all
            // real input takes, and it carries no budget of its own on purpose: Core's page
            // walk dedupes as it goes, so a /Kids graph that revisits nodes costs it time
            // linear in the number of distinct nodes, not exponential in the depth. Measured
            // over 500/1000/2000/4000-page trees it is linear. Nothing to guard here.
            if (doc.Catalog != null)
            {
                var loaded = doc.Pages;
                for (int i = 0; i < loaded.Count; i++)
                    pages.Add(new PageRef(loaded[i].Dictionary, loaded[i].Resources, i));
                return pages;
            }

            // Encrypted document: PdfDocument deliberately skips catalog/page loading, so walk
            // the page tree here. Dictionary structure is never encrypted, only string and
            // stream contents, so the structural checks below still hold. This is the *only*
            // caller of CollectPagesFrom, and hence the only place MaxPageTreeNodes /
            // MaxPageTreeDepth ever apply — the hand-written walk has no dedupe of Core's, so
            // it brings its own visited set and budget.
            var root = ResolveDict(doc, catalog[PdfName.Pages]);
            if (root == null) return pages;

            int budget = MaxPageTreeNodes;
            bool truncated = false;
            CollectPagesFrom(root, doc, pages, null, new HashSet<PdfDictionary>(), ref budget, 0,
                ref truncated);
            // A page the walk never reached is a page every page-based check silently skipped, so
            // the refusal travels with the result rather than only into the diagnostic slot.
            if (truncated)
                v.Add(new PdfUaViolation("UA99",
                    $"The page-tree walk stopped after {MaxPageTreeNodes} /Kids slots, so the page-based " +
                    "checks (UA09 tab order, UA25/UA26 fonts, UA27 annotations, UA50) did not run " +
                    "for every page."));
            return pages;
        }

        private static void CollectPagesFrom(PdfDictionary node, PdfDocument doc,
            List<PageRef> pages, PdfDictionary inheritedResources,
            HashSet<PdfDictionary> visited, ref int budget, int depth, ref bool truncated)
        {
            if (node == null) return;

            if (depth > MaxPageTreeDepth)
            {
                // Reported, not silently dropped. PdfDeepAccessibilityChecker reports its own
                // depth refusal, and this one returned quietly — so on a page tree deeper than the
                // cap the two audits of the same bytes disagreed about how many pages exist, and
                // only one of them said why.
                Report("PdfUaChecker.CollectPagesFrom",
                    $"the page tree is deeper than {MaxPageTreeDepth}; the deeper pages were not collected");
                truncated = true;
                return;
            }

            if (budget <= 0)
            {
                Report("PdfUaChecker.CollectPagesFrom",
                    $"page-tree traversal stopped after {MaxPageTreeNodes} /Kids slots");
                truncated = true;
                return;
            }
            if (!visited.Add(node)) return; // cycle guard

            // NO charge here — the budget is spent per /Kids SLOT in the loop below, and nowhere
            // else. Charging per node as well cost 2 per page on a flat tree, so this walk
            // exhausted at ~100 000 pages while PdfDeepAccessibilityChecker, which charges only
            // per slot against the same 200 000 ceiling, audited 200 000. On a 150 000-page
            // encrypted document the two checkers audited different pages of the same file.
            //
            // The slot is the right unit and the node is redundant: the visited set already stops
            // a shared /Kids array being expanded twice, and what it cannot stop is the n resolves
            // each expansion costs. That is the reasoning the deep checker's loop states, and this
            // walker now matches it exactly.

            var resources = ResolveDict(doc, node[PdfName.Resources]) ?? inheritedResources;

            var kids = ResolveArray(doc, node[PdfName.Kids]);
            var type = ResolveName(doc, node[PdfName.Type])?.Value;

            // `kids == null` tolerates a leaf that forgot /Type /Page — but a node that positively
            // declares /Type /Pages is not a leaf, it is an EMPTIED BRANCH, which is what a
            // page-removal tool leaves behind. Counting it as a page would shift every later page
            // index and run UA25/UA26/UA27/UA50 against a non-page. One rule, in
            // Core.Document.PdfPageTreeShape, with Core's second clause: an untyped, childless node
            // must also be page-SHAPED. Without it a node with neither /Contents nor its own /MediaBox
            // would count as a page, and UA25/UA26/UA27/UA50 would run against a non-page: exactly
            // what the comment below this one warns about, one case further out.
            if (Core.Document.PdfPageTreeShape.IsLeafPage(
                    ResolveName(doc, node[PdfName.Type]), kids,
                    hasContents: node[PdfName.Contents] != null,
                    hasMediaBox: ResolveArray(doc, node[PdfName.MediaBox]) != null))
            {
                pages.Add(new PageRef(node, resources, pages.Count));
                return;
            }

            // An emptied branch reaches here with no /Kids to walk. The leaf test does not call it
            // a page, so the loop below needs its own guard.
            if (kids == null) return;

            for (int i = 0; i < kids.Count; i++)
            {
                // Per /Kids *slot*, not per fresh node — same reasoning as WalkStructTree: N
                // intermediate /Pages nodes sharing one indirect N-entry /Kids array resolve N²
                // times while a per-node counter moves only N times. This walker only runs for a
                // document whose catalog could not be loaded (i.e. an encrypted one), and PDF/UA-1
                // expressly permits encryption, so it is a supported audit target for input the
                // attacker chose.
                if (budget <= 0)
                {
                    Report("PdfUaChecker.CollectPagesFrom",
                        $"page-tree traversal stopped after {MaxPageTreeNodes} /Kids slots");
                    truncated = true;
                    return;
                }
                budget--;

                var kid = ResolveDict(doc, kids[i]);
                if (kid != null)
                    CollectPagesFrom(kid, doc, pages, resources, visited, ref budget, depth + 1,
                        ref truncated);
            }
        }

        /// <summary>
        /// The decoded bytes of a page's <c>/Contents</c> within <paramref name="remaining"/> bytes,
        /// whether it is one stream or an array of them (§7.7.3.3: the array's parts are
        /// concatenated <i>with white space between</i>, so a token cannot be split across the
        /// join) — or <see langword="null"/> and a reason when a part will not fit.
        /// </summary>
        /// <remarks>
        /// <para>Reads the page DICTIONARY rather than a <c>PdfPage</c>, because the audit's page
        /// list is built from dictionaries — the hand-written walk that serves an encrypted document
        /// has no <c>PdfPage</c> to offer.</para>
        ///
        /// <para><b>The ceiling reaches the decoder rather than being applied after it.</b> Checking
        /// the length of what came back means a Flate bomb is fully inflated first — to Core's
        /// <c>MaxDecompressedBytes</c>, 512 MB — and only then rejected for exceeding 8 MiB.
        /// <c>PdfXValidator.DecodeWithinCeilings</c>, the other content walk in this assembly, has
        /// always passed its ceiling to <c>TryGetDecodedData</c> for exactly this reason; the two
        /// were read against each other and only one of them was bounded where it matters.</para>
        ///
        /// <para><b>All or nothing across an array-valued <c>/Contents</c>.</b> A part left out is a
        /// part whose <c>BDC</c> operators are not seen, and UA50 would then report every identifier
        /// they defined as missing — inventing violations for a conforming file, which is the one
        /// outcome this check must never produce.</para>
        /// </remarks>
        private static byte[] PageContentBytesWithinCeiling(
            PdfDocument doc, PdfDictionary page, long remaining, out string refusal)
        {
            string reason = null;
            long consumed = 0;

            byte[] bytes = PdfPageContent.Concatenate(page, o => Resolve(doc, o), s =>
            {
                if (reason != null) return null;
                if (!s.TryGetDecodedData(Math.Max(0, remaining - consumed),
                                         out byte[] data, out string why))
                {
                    reason = why ?? "the content stream could not be decoded";
                    return null;
                }
                consumed += data?.Length ?? 0;
                return data;
            });

            // `refusal` is the single authority on whether this succeeded, and the caller returns on
            // it before reading the bytes. Returning null here as well was a second channel for one
            // fact: a mutation removing it killed nothing, because nothing could reach the value.
            refusal = reason;
            return bytes;
        }

        // ----------------------------------------------------------------
        //  UA09 — /Tabs /S on annotated pages
        // ----------------------------------------------------------------

        private static void CheckTabOrder(PdfDocument doc, List<PageRef> pages, List<PdfUaViolation> v)
        {
            int badPages = 0;
            int legacyKeyPages = 0;
            foreach (var page in pages)
            {
                var pageDict = page.Dict;
                if (pageDict == null) continue;

                // Only check pages that have annotations.
                var annots = ResolveArray(doc, pageDict[PdfName.Annots]);
                if (annots == null || annots.Count == 0) continue;

                // ISO 32000-1 Table 30 names this key /Tabs, and only /Tabs. There is no
                // /TabOrder key in the specification and no reader honours it, so a page carrying
                // only /TabOrder /S genuinely fails ISO 14289-1 §7.18.3 — independent checkers fail it,
                // and this library's own SetStructureTabOrder / PdfAccessibilityFixer.FixTabs both
                // treat the page as still needing the real key. Accepting it here would make this
                // checker the one component that calls a broken page conformant, which is the
                // single thing an accessibility checker must never do.
                //
                // So: /Tabs /S is the *only* thing that satisfies UA09. The non-standard spelling
                // never does — it is only *named* in the message, so the owner of a file carrying it
                // is told which key to replace rather than merely that a key is missing.
                var tabs = ResolveName(doc, pageDict[PdfName.Tabs]);

                if (tabs != null && tabs.Value == "S") continue;

                badPages++;

                // Named whenever the page is already non-conformant and carries the legacy key,
                // including the /Tabs /R + /TabOrder /S combination: the advice ("delete
                // /TabOrder, write /Tabs /S") is what the user needs in either case.
                if (ResolveName(doc, pageDict[PdfName.TabOrder])?.Value == "S")
                    legacyKeyPages++;
            }

            if (badPages > 0)
                v.Add(new PdfUaViolation("UA09",
                    $"{badPages} page(s) with annotations do not have /Tabs /S (structure order). " +
                    "PDF/UA-1 requires tab order to follow the structure tree on all annotated pages."
                    + (legacyKeyPages > 0
                        ? $" {legacyKeyPages} of them declare the legacy /TabOrder /S key that earlier " +
                          "versions of this library wrote; /TabOrder is not a PDF key (ISO 32000-1 " +
                          "Table 30), no reader honours it, and it does not satisfy this requirement " +
                          "— remove it and write /Tabs /S instead."
                        : string.Empty)));
        }

        // ----------------------------------------------------------------
        //  UA25 / UA26 — font embedding and Unicode mapping
        // ----------------------------------------------------------------

        private sealed class FontScan
        {
            /// <summary>Each font found without an embedded program, with the label it is reported by.</summary>
            internal readonly List<(PdfDictionary Font, string Label)> NotEmbedded = new List<(PdfDictionary, string)>();
            internal readonly SortedSet<string> NoUnicodeMapping = new SortedSet<string>(StringComparer.Ordinal);
            /// <summary>Each font found, for UA51's TrueType rules, which judge the painted ones.</summary>
            internal readonly List<(PdfDictionary Font, string Label)> Fonts = new List<(PdfDictionary, string)>();
            /// <summary>Each form XObject with a <c>/Ref</c> — a reference XObject (UA53).</summary>
            internal readonly SortedSet<string> ReferenceXObjects = new SortedSet<string>(StringComparer.Ordinal);
        }

        /// <remarks>
        /// <para><b>UA25 asks ISO 14289-1 7.21.4.1's question, which is about fonts that are
        /// used.</b> "A font is considered to be used if at least one of its glyphs is referenced
        /// from a content stream", and NOTE 2 exempts one referenced only in render mode 3, which
        /// paints nothing. Reporting every font in a page's resources, used or not, would disagree
        /// with the deep checker's UA21, which tracks the text state, and an audit running both
        /// would fail this library's own searchable PDFs, whose OCR layer is written in a font that
        /// is deliberately not embedded.</para>
        ///
        /// <para>Which fonts are painted is <see cref="PdfUaFontUse"/>'s answer, the one the deep
        /// checker's UA21 uses too. Where it cannot be established for the whole document, every
        /// font without a program is reported: a font this checker cannot see used is
        /// not thereby shown to be unused.</para>
        /// </remarks>
        private static void CheckFonts(PdfDocument doc, List<PageRef> pages,
            List<PdfUaViolation> v, int maxPageContentBytes, long maxDocumentContentBytes)
        {
            var scan = new FontScan();
            var visitedResources = new HashSet<PdfDictionary>();
            int budget = MaxResourceEntries;

            foreach (var page in pages)
                CollectFonts(page.Resources, doc, scan, visitedResources, ref budget, 0, v);

            // The fonts an annotation's appearance paints: ISO 14289-1 7.21.4.1 covers every font used
            // for rendering, and an appearance stream renders, under its own /Resources — reached from
            // /Annots, never from a page's /XObject entries, so the walk above cannot see them.
            foreach (var page in pages)
            {
                var annots = ResolveArray(doc, page.Dict?[PdfName.Annots]);
                if (!ScanReuse.AdmitAnnotations(annots)) continue;
                for (int i = 0; i < annots.Count; i++)
                {
                    if (budget <= 0) { ReportResourceBudget(v); break; }
                    budget--;
                    var normal = ResolveDict(doc, ResolveDict(doc, annots[i])?[PdfName.AP])?[PdfName.N];
                    if (normal == null) continue;
                    if (Resolve(doc, normal) is PdfStream appearance)
                    {
                        CollectFonts(ResolveDict(doc, appearance[PdfName.Resources]), doc, scan, visitedResources, ref budget, 0, v);
                        continue;
                    }
                    if (ResolveDict(doc, normal) is PdfDictionary states)
                        foreach (var kvp in states)
                            if (Resolve(doc, kvp.Value) is PdfStream state)
                                CollectFonts(ResolveDict(doc, state[PdfName.Resources]), doc, scan, visitedResources, ref budget, 0, v);
                }
            }

            HashSet<PdfDictionary> painted = null;
            bool paintedRead = false;
            HashSet<PdfDictionary> Painted()
            {
                if (paintedRead) return painted;
                paintedRead = true;
                painted = PdfUaFontUse.PaintedFonts(doc, maxPageContentBytes, maxDocumentContentBytes);
                if (PdfUaFontUse.LastError != null)
                    Report("PdfUaChecker.CheckFonts", PdfUaFontUse.LastError);
                return painted;
            }

            if (scan.NotEmbedded.Count > 0)
            {
                Painted();
                var reported = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var (font, label) in scan.NotEmbedded)
                    if (painted == null || painted.Contains(font)) reported.Add(label);

                if (reported.Count > 0)
                    v.Add(new PdfUaViolation("UA25",
                        "PDF/UA-1 requires every font used for rendering to be embedded (ISO 14289-1 " +
                        $"§7.21.4.1). Not embedded: {string.Join(", ", reported)}."));
            }

            // UA51 — ISO 14289-1 7.21.6, through TrueTypeFontRules (PDF/A's A80 and PDF/UA-2's
            // §8.4.5.7 read the same functions): the painted TrueType fonts.
            var trueType = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var (font, label) in scan.Fonts)
            {
                string problem = TrueTypeFontRules.WhatIsWrong(doc, font, TrueTypeCmapRule.Iso32000Part1);
                if (problem != null && (Painted() == null || Painted().Contains(font)))
                    trueType.Add($"{label}: {problem}");
            }
            if (trueType.Count > 0)
                v.Add(new PdfUaViolation("UA51",
                    $"A TrueType font used for rendering breaks ISO 14289-1 7.21.6: {string.Join("; ", trueType)}."));

            // UA53 — ISO 14289-1 7.20: no reference XObjects.
            if (scan.ReferenceXObjects.Count > 0)
                v.Add(new PdfUaViolation("UA53",
                    $"A form XObject is a reference XObject (/Ref), which ISO 14289-1 7.20 forbids: {string.Join(", ", scan.ReferenceXObjects)}."));

            // UA54 — 7.21.3.2, an embedded CIDFontType2's /CIDToGIDMap; UA55 — 7.21.7, a /ToUnicode
            // value of 0, U+FEFF or U+FFFE; UA56 — 7.21.4.2, a subset's /CharSet or /CIDSet against its
            // program. All of the fonts used for rendering, through the readings PDF/A's A81 to A83 share.
            var cidToGid = new SortedSet<string>(StringComparer.Ordinal);
            var toUnicode = new SortedSet<string>(StringComparer.Ordinal);
            var subsetSets = new SortedSet<string>(StringComparer.Ordinal);
            var subsetUnjudged = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var (font, label) in scan.Fonts)
            {
                bool cidProblem = !TrueTypeFontRules.CidToGidMapConforms(doc, font);
                string valueProblem = FontUnicodeMapping.ForbiddenToUnicodeValue(doc, font);
                string subsetProblem = FontSubsetSets.WhatIsWrong(doc, font, SubsetSetRule.Iso32000Part1, out string unjudged);
                if (!cidProblem && valueProblem == null && subsetProblem == null && unjudged == null) continue;
                if (Painted() != null && !Painted().Contains(font)) continue;
                if (cidProblem) cidToGid.Add(label);
                if (valueProblem != null) toUnicode.Add($"{label}: {valueProblem}");
                if (subsetProblem != null) subsetSets.Add($"{label}: {subsetProblem}");
                if (unjudged != null) subsetUnjudged.Add($"{label}: {unjudged}");
            }
            if (cidToGid.Count > 0)
                v.Add(new PdfUaViolation("UA54",
                    "An embedded CIDFontType2's /CIDToGIDMap is neither /Identity nor a stream (ISO 14289-1 " +
                    $"7.21.3.2): {string.Join(", ", cidToGid)}."));
            if (toUnicode.Count > 0)
                v.Add(new PdfUaViolation("UA55",
                    $"A /ToUnicode CMap maps a code to U+0000, U+FEFF or U+FFFE (ISO 14289-1 7.21.7): {string.Join("; ", toUnicode)}."));
            if (subsetSets.Count > 0)
                v.Add(new PdfUaViolation("UA56",
                    $"A font subset's /CharSet or /CIDSet does not match its program (ISO 14289-1 7.21.4.2): {string.Join("; ", subsetSets)}."));
            if (subsetUnjudged.Count > 0)
                v.Add(new PdfUaViolation("UA99",
                    $"UA56 (ISO 14289-1 7.21.4.2) was not judged for a font subset: {string.Join("; ", subsetUnjudged)}."));

            // UA57 — 7.21.4.1: every glyph referenced for rendering is in the embedded program, through the
            // reading PDF/A's A84 shares, over the codes each font paints.
            if (scan.Fonts.Count > 0)
            {
                var codes = PdfUaFontUse.PaintedCodes(doc, maxPageContentBytes, maxDocumentContentBytes);
                if (codes == null)
                    v.Add(new PdfUaViolation("UA99",
                        "UA57 (ISO 14289-1 7.21.4.1) was not judged: the glyphs used for rendering could not be established" +
                        (PdfUaFontUse.LastError != null ? " (" + PdfUaFontUse.LastError + ")" : "") + "."));
                else
                {
                    var glyphless = new SortedSet<string>(StringComparer.Ordinal);
                    var glyphsUnjudged = new SortedSet<string>(StringComparer.Ordinal);
                    foreach (var (font, label) in scan.Fonts)
                    {
                        if (!codes.TryGetValue(font, out HashSet<int> fontCodes)) continue;
                        string missing = FontGlyphPresence.WhatIsMissing(doc, font, fontCodes, out string notJudged);
                        if (missing != null) glyphless.Add($"{label}: {missing}");
                        if (notJudged != null) glyphsUnjudged.Add($"{label}: {notJudged}");
                    }
                    if (glyphless.Count > 0)
                        v.Add(new PdfUaViolation("UA57",
                            $"A font used for rendering paints a glyph its embedded program does not define (ISO 14289-1 7.21.4.1): {string.Join("; ", glyphless)}."));
                    if (glyphsUnjudged.Count > 0)
                        v.Add(new PdfUaViolation("UA99",
                            $"UA57 (ISO 14289-1 7.21.4.1) was not judged for a font: {string.Join("; ", glyphsUnjudged)}."));
                }
            }

            if (scan.NoUnicodeMapping.Count > 0)
                v.Add(new PdfUaViolation("UA26",
                    "PDF/UA-1 requires every font to map its glyphs to Unicode via /ToUnicode or " +
                    "a Unicode-capable encoding (ISO 14289-1 §7.21.7), otherwise the text cannot " +
                    $"be read out or extracted. No Unicode mapping: {string.Join(", ", scan.NoUnicodeMapping)}."));
        }

        /// <summary>
        /// Walks a resource dictionary and its Form XObjects.
        ///
        /// <para>
        /// <b>Why this needs a budget and not only a depth cap.</b> The visited set bounds how many
        /// distinct resource dictionaries are <i>expanded</i>; it says nothing about the cost of one
        /// expansion. N page resource dictionaries that all name the same <c>/XObject</c> dictionary
        /// of N form entries cost N² resolve-and-probe operations behind a depth cap of 32 — minutes
        /// of pure dictionary probing from a file of a few hundred kilobytes. The budget is spent per
        /// <i>entry examined</i>, exactly as in the struct-tree, field-tree and page-tree walkers.
        /// </para>
        /// </summary>
        private static void CollectFonts(PdfDictionary resources, PdfDocument doc,
            FontScan scan, HashSet<PdfDictionary> visited, ref int budget, int depth,
            List<PdfUaViolation> v)
        {
            if (resources == null || depth > MaxResourceDepth) return;
            // A Form XObject whose /Resources reference itself would otherwise recurse forever.
            if (!visited.Add(resources)) return;

            var fontDict = ResolveDict(doc, resources[PdfName.Font]);
            if (fontDict != null)
            {
                foreach (var kvp in fontDict)
                {
                    if (budget <= 0) { ReportResourceBudget(v); return; }
                    budget--;

                    var font = ResolveDict(doc, kvp.Value);
                    if (font == null) continue;
                    CheckFontEmbedded(font, doc, scan);
                    CheckFontUnicode(font, doc, scan);
                    scan.Fonts.Add((font, FontLabel(doc, font)));
                }
            }

            var xobjDict = ResolveDict(doc, resources[PdfName.XObject]);
            if (xobjDict != null)
            {
                foreach (var kvp in xobjDict)
                {
                    if (budget <= 0) { ReportResourceBudget(v); return; }
                    budget--;

                    var xobj = Resolve(doc, kvp.Value) as PdfStream;
                    if (xobj == null) continue;
                    if (ResolveName(doc, xobj[PdfName.Subtype])?.Value != "Form") continue;
                    if (xobj[PdfName.Ref] != null) scan.ReferenceXObjects.Add("/" + kvp.Key.Value);
                    CollectFonts(ResolveDict(doc, xobj[PdfName.Resources]), doc, scan, visited,
                        ref budget, depth + 1, v);
                }
            }

            // Tiling patterns and Type 3 glyph procedures carry their own /Resources, and a font
            // reachable only through one of them would be invisible to UA25/UA26 if the walk stopped
            // at Form XObjects. The deep checker (PdfDeepAccessibilityChecker.CollectFontsFrom)
            // descends into both too, so a non-embedded font inside a tiling pattern is reported by
            // both checkers on the same file. Same budget, same depth accounting, so the traversal
            // stays bounded.
            var patternDict = ResolveDict(doc, resources[PdfName.Pattern]);
            if (patternDict != null)
            {
                foreach (var kvp in patternDict)
                {
                    if (budget <= 0) { ReportResourceBudget(v); return; }
                    budget--;

                    var pattern = Resolve(doc, kvp.Value) as PdfDictionary;
                    if (pattern == null) continue;
                    // Only /PatternType 1 (tiling) has content and therefore resources; a shading
                    // pattern paints no text.
                    var ptype = Resolve(doc, pattern[PdfName.PatternType]) as PdfNumber;
                    if (ptype == null || ptype.IntValue != 1) continue;
                    CollectFonts(ResolveDict(doc, pattern[PdfName.Resources]), doc, scan, visited,
                        ref budget, depth + 1, v);
                }
            }

            var fontDictForType3 = ResolveDict(doc, resources[PdfName.Font]);
            if (fontDictForType3 != null)
            {
                foreach (var kvp in fontDictForType3)
                {
                    if (budget <= 0) { ReportResourceBudget(v); return; }
                    budget--;

                    var f = Resolve(doc, kvp.Value) as PdfDictionary;
                    if (f == null) continue;
                    if (ResolveName(doc, f[PdfName.Subtype])?.Value != "Type3") continue;
                    CollectFonts(ResolveDict(doc, f[PdfName.Resources]), doc, scan, visited,
                        ref budget, depth + 1, v);
                }
            }
        }

        /// <summary>
        /// Records the resource-budget refusal on both channels: the diagnostic slot, and — once
        /// per audit — a <c>UA99</c>, so a truncated font walk cannot report an empty
        /// <c>Violations</c> list that reads as "every font is embedded".
        /// </summary>
        private static void ReportResourceBudget(List<PdfUaViolation> v)
        {
            Report("PdfUaChecker.CollectFonts",
                $"resource traversal stopped after {MaxResourceEntries} entries; " +
                "the font checks (UA25, UA26) are partial");

            foreach (var existing in v)
                if (existing.Code == "UA99" && existing.Message.Contains("resource traversal"))
                    return;

            v.Add(new PdfUaViolation("UA99",
                $"The resource traversal stopped after {MaxResourceEntries} entries, so the font " +
                "checks (UA25 embedding, UA26 Unicode mapping) examined only part of the document."));
        }

        private static string FontLabel(PdfDocument doc, PdfDictionary font)
            => ResolveName(doc, font[PdfName.BaseFont])?.Value
            ?? ResolveName(doc, font[PdfName.Name])?.Value
            ?? "(unnamed font)";

        /// <summary>
        /// UA25's half of the shared embedding walk: the Type 3 exemption, the
        /// unresolvable-descendants report and the missing-<c>/Subtype</c> fall-through, as the
        /// shared walk has them.
        /// </summary>
        private static void CheckFontEmbedded(PdfDictionary font, PdfDocument doc, FontScan scan)
            => FontEmbeddingProbe.ReportUnembedded(
                font, doc,
                label => scan.NotEmbedded.Add((font, label)),
                fallbackLabel: null,
                reportDiagnostic: detail => Report("PdfUaChecker.CheckFontEmbedded", detail));

        // One implementation, in FontUnicodeMapping — shared with PDF/A's level A and U rule (A77),
        // which asks ISO 14289-1 7.21.7's question in the same words.
        private static void CheckFontUnicode(PdfDictionary font, PdfDocument doc, FontScan scan)
        {
            if (!FontUnicodeMapping.Maps(doc, font)) scan.NoUnicodeMapping.Add(FontLabel(doc, font));
        }

        // ----------------------------------------------------------------
        //  UA27 — annotations need an accessible description
        // ----------------------------------------------------------------

        private static void CheckAnnotations(PdfDocument doc, List<PageRef> pages,
            StructTreeScan scan, List<PdfUaViolation> v)
        {
            int missingContents = 0;
            int linksWithoutDescription = 0;
            int trapNets = 0;

            foreach (var page in pages)
            {
                if (page.Dict == null) continue;
                var annots = ResolveArray(doc, page.Dict[PdfName.Annots]);
                if (!ScanReuse.AdmitAnnotations(annots)) continue;

                for (int i = 0; i < annots.Count; i++)
                {
                    int objNum = (annots[i] as PdfReference)?.ReferenceObjectNumber ?? 0;
                    var annot = ResolveDict(doc, annots[i]);
                    if (annot == null) continue;
                    if (objNum == 0) objNum = annot.ObjectNumber;

                    string subtype = ResolveName(doc, annot[PdfName.Subtype])?.Value;
                    int? flags = PdfAnnotationFlags.FromNumber(Resolve(doc, annot[PdfName.F]));

                    // The scope the deep checker and the tagger share: not a pop-up, not a
                    // printer's mark, not hidden, not outside the CropBox. When the copies of it
                    // diverged, a press-ready file with registration marks was reported
                    // non-conforming here while the deep checker passed it.
                    if (!PdfUaAnnotationScope.IsPresentedToReader(subtype, flags,
                            ResolveArray(doc, annot[PdfName.Rect]), CropBoxOf(doc, page), doc.ResolveObject)) continue;

                    // UA52 — ISO 14289-1 7.18.2: no TrapNet annotation a reader is presented with.
                    if (subtype == PdfAnnotationSubtypes.TrapNet) trapNets++;

                    // Narrowed here, and only here: a /Widget's description is its field's /TU,
                    // which UA08 judges (Matterhorn 28-004 names Widget, Link and PrinterMark
                    // together).
                    if (subtype == PdfAnnotationSubtypes.Widget) continue;

                    if (subtype == PdfAnnotationSubtypes.Link)
                    {
                        bool described = HasNonEmptyString(doc, annot[PdfName.Contents])
                            || (objNum > 0 && scan.LinkAnnotationsWithStructAlt.Contains(objNum));
                        if (!described) linksWithoutDescription++;
                        continue;
                    }

                    // §7.18.1: "Any annotation that does not have a Contents key in its dictionary
                    // shall have an alternative description" — the enclosing element's /Alt
                    // (the conformance corpus 7.18.1-t02-pass-a, -e).
                    if (!HasNonEmptyString(doc, annot[PdfName.Contents])
                        && !(objNum > 0 && scan.AnnotationsWithStructAlt.Contains(objNum)))
                        missingContents++;
                }
            }

            if (trapNets > 0)
                v.Add(new PdfUaViolation("UA52",
                    $"{trapNets} TrapNet annotation(s) a reader is presented with; ISO 14289-1 7.18.2 forbids them."));

            if (missingContents > 0)
                v.Add(new PdfUaViolation("UA27",
                    $"{missingContents} annotation(s) have neither a /Contents entry nor an /Alt on " +
                    "their enclosing structure element. PDF/UA-1 (ISO 14289-1 §7.18.1) requires every " +
                    "visible annotation other than /Link, /Popup, /PrinterMark and /Widget to carry an " +
                    "accessible description."));

            if (linksWithoutDescription > 0)
                v.Add(new PdfUaViolation("UA27",
                    $"{linksWithoutDescription} /Link annotation(s) have neither a /Contents " +
                    "entry nor an enclosing <Link> structure element with /Alt. PDF/UA-1 " +
                    "(ISO 14289-1 §7.18.5) requires an alternate description for every link."));
        }
    }

    // ----------------------------------------------------------------
    //  Result types
    // ----------------------------------------------------------------

    /// <summary>Result of a PDF/UA-1 validation check.</summary>
    public sealed class PdfUaValidationResult
    {
        /// <summary>List of violations found. Empty list means the document passed all checks.</summary>
        public IReadOnlyList<PdfUaViolation> Violations { get; }

        /// <summary>
        /// True when this checker found no violations.
        ///
        /// <para>
        /// <b>This is not a PDF/UA-1 conformance certificate and must never be surfaced to a user
        /// as "this file is accessible."</b> What it means is narrower: the problems
        /// <i>this checker looks for</i> were not found. In particular it does <b>not</b> prove:
        /// </para>
        /// <list type="bullet">
        ///   <item><description><b>that the tagging describes the content.</b> UA50 decodes each
        ///     page far enough to confirm that an MCID the tree names really exists, but nothing
        ///     here can see whether a page's text is marked at all, or whether the reading order
        ///     matches the visual one — both need form XObjects followed, which is the deep
        ///     checker's job. UA28/UA29 catch only the crudest hollow trees, from the
        ///     dictionaries alone.</description></item>
        ///   <item><description><b>that alternate text is meaningful.</b> A <c>/Alt</c> of
        ///     <c>"image"</c> satisfies every check here and helps nobody.</description></item>
        ///   <item><description><b>that the document meets WCAG.</b> Colour contrast, link purpose,
        ///     meaningful sequence and language of parts are outside what any structural check can
        ///     see.</description></item>
        /// </list>
        ///
        /// <para>
        /// For the deeper content-level checks — <b>UA10–UA24 and UA30</b>: heading hierarchy, list
        /// and table structure, role mapping, <c>/ParentTree</c> coverage and the mapping it
        /// encodes, alt-text quality, leaf elements and whole subtrees referencing no content,
        /// unmarked page content, pages missing <c>/StructParents</c>, <c>/MarkInfo /Suspects</c>,
        /// font embedding and Unicode mapping, link descriptions, annotations unreachable from the
        /// structure tree, and image-only pages — use
        /// <c>Tetitulz.Pdf.Accessibility.PdfAccessibility.Audit</c>. Before making any conformance
        /// claim, validate with an independent PDF/UA checker <i>and</i> have a human review the file.
        /// </para>
        /// </summary>
        public bool IsConforming => Violations.Count == 0;

        internal PdfUaValidationResult(List<PdfUaViolation> violations)
            => Violations = violations;
    }

    /// <summary>A single PDF/UA-1 conformance violation.</summary>
    public sealed class PdfUaViolation
    {
        /// <summary>Check code (e.g. "UA01").</summary>
        public string Code { get; }
        /// <summary>Human-readable description of the violation.</summary>
        public string Message { get; }

        internal PdfUaViolation(string code, string message)
        {
            Code = code;
            Message = message;
        }

        public override string ToString() => $"[{Code}] {Message}";
    }
}
