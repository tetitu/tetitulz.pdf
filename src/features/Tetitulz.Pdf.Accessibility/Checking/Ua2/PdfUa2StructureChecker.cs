// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Fonts.Cff;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Plugins.Conformance;

namespace Tetitulz.Pdf.Accessibility.Checking.Ua2
{
    /// <summary>
    /// The PDF/UA-2 (ISO 14289-2:2024) structure-tree deep checker — clause 8.2.5.2 to 8.2.5.33's
    /// per-structure-type child/role rules, plus the namespace-membership requirement of
    /// ISO 32000-2:2020 §14.8.6.
    ///
    /// <para>
    /// <b>Deliberately named differently from <c>Plugins.Conformance.PdfUa2Checker</c></b>, which
    /// checks only Clause 5's XMP identifier (dictionary-level, no tree walk) — the same split
    /// PDF/UA-1 already has between <c>PdfUaChecker</c> (dictionary-level) and
    /// <c>PdfDeepAccessibilityChecker</c> (tree-walking), given a distinct name on each side
    /// there too. See GAPS.md §22d for why this is a new sibling type rather than a branch on
    /// either existing checker: the two rule sets are large and clause-driven enough that merging
    /// them costs more in legibility than it saves in duplication.
    /// </para>
    ///
    /// <para>
    /// <b>Clause by clause, grouped by shape.</b> §8.2.5.2 to §8.2.5.33 is the largest single item
    /// GAPS.md tracks (§22d), and each clause is a check of one of a few shapes: a whole-tree search
    /// by role (<c>H</c>, <c>Note</c>, <c>math</c>'s parent); a container's required children
    /// (<c>Ruby</c>, <c>Warichu</c>, <c>LI</c>); an element's own position or attributes
    /// (<c>Caption</c>, <c>Figure</c>'s <c>/Alt</c>, <c>TOCI</c>'s <c>/Ref</c> over its subtree, an
    /// ARIA <c>/role</c>'s owner); a small table-anchoring model (§8.2.5.26,
    /// <see cref="CheckTableRegularity"/>); annotations cross-referenced against every
    /// <c>/OBJR</c> in the tree, in either direction — must be absent or an artifact (§8.9.2.2,
    /// §8.9.2.4.13–.14 through <c>CheckAnnotationsMustBeArtifacts</c>), must be enclosed in a role
    /// (§8.9.2.1, §8.9.2.3, §8.2.5.20 through <see cref="CheckAnnotationsMustBeEnclosedInRole"/>),
    /// at most one widget per <c>Form</c>, <c>/Alt</c> matching <c>/Contents</c>; a flat
    /// page/annotation scan (prohibited subtypes, required <c>/Contents</c>, a FileAttachment's
    /// <c>/AFRelationship</c>, a page's <c>/Tabs</c>); catalog reads (XFA, <c>/Lang</c>,
    /// <c>/DisplayDocTitle</c>, optional content configurations, outline titles outside the Private
    /// Use Area, embedded-file <c>/Desc</c> through <c>Core.Document.PdfNameTree</c>); field reads
    /// (a text field's <c>/RV</c>, a multi-widget field's label,
    /// <see cref="CheckMultiWidgetFieldHasLabelOrContents"/>); and font reads over page resources
    /// through <c>WalkAllFontDescriptors</c> and <c>WalkAllTopLevelFonts</c>, which reuse
    /// <c>PdfXValidator.ScanPage</c> and <c>FontEmbeddingProbe.WalkDescriptors</c> rather than a
    /// third content walk — <c>/CIDToGIDMap</c>, a symbolic TrueType font's <c>/Encoding</c>
    /// (<c>Core.Fonts.FontFlags.IsSymbolic</c>), a Type 0 font's <c>CIDSystemInfo</c> against an
    /// embedded CMap's, <c>/ToUnicode</c> values and coverage
    /// (<see cref="CheckToUnicodeMapsReferencedGlyphs"/>, over <c>Core.Fonts.CMapParser</c>),
    /// <c>/WMode</c> (<see cref="CheckEmbeddedCMapWModeConsistent"/>), <c>/Widths</c> against the
    /// program (<see cref="CheckFontWidthConsistency"/>, over <see cref="SimpleFontWidthProbe"/>),
    /// <c>.notdef</c> (<see cref="CheckNoNotdefInFonts"/>), and embedding of the fonts used for
    /// rendering (<see cref="CheckFontsUsedForRenderingAreEmbedded"/>, over
    /// <see cref="FontEmbeddingProbe.ReportUnembedded"/>). XMP <c>dc:title</c> is
    /// <c>Plugins.Conformance.PdfUaChecker.FindXmpDcTitle</c>'s; §6.2's containment rules are
    /// <c>Base.Operations.Structure.PdfStructureNamespaceInclusion</c>'s
    /// (<see cref="CheckStructureContainmentRules"/>); §8.2.4 is a bounded transitive chase across
    /// namespace dictionaries under <see cref="Tetitulz.Pdf.Core.Document.PdfRoleMap.MaxHops"/> with a
    /// three-valued result (<see cref="CheckRoleMapNsNotSameNamespace"/>,
    /// <see cref="CheckElementBelongsToStandardNamespace"/>); §8.8 reads Link destinations
    /// (<see cref="CheckLinkDestinationsAreStructureDestinations"/>). §8.2.5.21 and §8.2.5.22 are
    /// closed through the §8.9 and §8.10 sub-clauses that name the same structure type, so every one
    /// of the thirty-two §8.2.5 subclauses is accounted for. Each check's own remarks say what it
    /// leaves out; see <see cref="CheckNoMarkedContentReferencesIntoAnnotationAppearances"/>'s own remarks.
    /// </para>
    /// </summary>
    public static partial class PdfUa2StructureChecker
    {
        /// <summary>ISO 32000-2:2020 §14.8.6.1 — the standard structure namespace for PDF 2.0.</summary>
        internal const string Pdf2StandardStructureNamespace = "http://iso.org/pdf2/ssn";

        /// <summary>ISO 32000-2:2020 §14.8.6.1 — the standard structure namespace for PDF 1.7,
        /// and the <i>default</i> standard structure namespace an absent <c>/NS</c> resolves to
        /// (see <see cref="CheckDocumentStructureType"/>'s own remarks on that default) — also
        /// the target namespace Table 356's own text names for a bare-<see cref="PdfName"/>
        /// <c>/RoleMapNS</c> value ("a single name identifying a structure element type in the
        /// default standard structure namespace").</summary>
        internal const string Pdf17StandardStructureNamespace = "http://iso.org/pdf/ssn";

        /// <summary>ISO 32000-2:2020 §14.8.6.3 — the one domain-specific namespace §8.2.4's own
        /// enumeration names alongside PDF 1.7/2.0. That subclause's own text: "Namespaces
        /// identified in this subclause do not require a RoleMapNS entry in their respective
        /// namespace dictionary" — a MathML-namespaced element is standard outright, with no
        /// role-map chase needed.</summary>
        internal const string MathMlNamespace = "http://www.w3.org/1998/Math/MathML";

        [ThreadStatic] private static string _lastValidationError;

        /// <summary>
        /// Describes the most recent recoverable problem encountered by this thread while
        /// validating. <see langword="null"/> when the last validation ran cleanly.
        /// </summary>
        public static string LastValidationError => _lastValidationError;

        private static void Report(string operation, string detail, Exception ex = null)
        {
            _lastValidationError = ex == null
                ? operation + ": " + detail
                : operation + ": " + detail + " — " + ex.GetType().Name + ": " + ex.Message;
        }

        /// <summary>Validates a PDF file's structure tree against this checker's PDF/UA-2 clauses.</summary>
        public static PdfUa2StructureResult Validate(string pdfPath)
        {
            if (pdfPath == null) throw new ArgumentNullException(nameof(pdfPath));
            using var fs = File.OpenRead(pdfPath);
            return Validate(fs);
        }

        /// <summary>Validates a PDF stream's structure tree against this checker's PDF/UA-2 clauses.</summary>
        public static PdfUa2StructureResult Validate(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            byte[] bytes = StreamBytes.ReadAll(input);
            using var ms  = new MemoryStream(bytes);
            using var doc = PdfDocument.Open(ms);
            return ValidateDocument(doc);
        }

        /// <summary>Validates an open document — shared by <see cref="Validate(Stream)"/> and
        /// <c>Wtpdf.PdfWtpdfChecker</c>, so the latter parses the file once.</summary>
        internal static PdfUa2StructureResult ValidateDocument(PdfDocument doc)
        {
            _annotationsOf = null;
            _annotations = null;
            _annotationEntriesRead = 0;
            _lastScanHits = 0;
            try
            {
                // One page scan for every check that asks: about fourteen font and appearance checks
                // each walk every page through ScanPage, and outside a scope each walked it afresh —
                // a page's shared /Annots array read once a check.
                using (ScanReuse.Begin())
                    return ValidateDocumentCore(doc);
            }
            finally
            {
                // The list holds the document's dictionaries; it must not keep them alive past the call.
                _annotationsOf = null;
                _annotations = null;
                _lastScanHits = ScanReuse.ClosedScanHits;
            }
        }

        [ThreadStatic] private static int _lastScanHits;

        /// <summary>The page scans the last validation on this thread reused, -1 when it joined a
        /// scope already open: the evidence that it opens one.</summary>
        internal static int LastScanHits => _lastScanHits;

        private static PdfUa2StructureResult ValidateDocumentCore(PdfDocument doc)
        {
            _lastValidationError = null;
            UnreadableObjects.Begin();
            var violations = new List<PdfUa2StructureViolation>();

            PdfDictionary catalog = doc.Catalog?.Dictionary;
            if (catalog == null)
            {
                try
                {
                    catalog = doc.ResolveObject(doc.Trailer?[PdfName.Root]) as PdfDictionary;
                }
                catch (Exception ex)
                {
                    Report("PdfUa2StructureChecker.ValidateDocument", "trailer /Root is not resolvable", ex);
                }
            }

            if (catalog == null)
            {
                violations.Add(new PdfUa2StructureViolation("UA2-8.2.5.2",
                    "No document catalog found; the structure tree could not be checked."));
                return Completed(violations);
            }

            // Dictionary structure — element roles, /K linkage, /NS references — is never
            // ciphertext, but the strings these checks judge are: a namespace's /NS name, /Alt and
            // /Contents, /Lang, a CIDSystemInfo's /Registry and /Ordering, an outline's /Title. Read
            // undecrypted, each object's strings are encrypted under its own key or a fresh IV, so
            // the standard namespace's name matched nothing, a figure's /Alt never equalled its
            // annotation's /Contents (UA2-8.9.4.2 reported for a conformant file), and an empty
            // string read as empty under RC4 and not under AES. This checker decrypts nothing, so an
            // encrypted document is not judged, and says so.
            if (doc.IsEncrypted)
            {
                violations.Add(new PdfUa2StructureViolation("UA2-99",
                    "The document is encrypted, and the strings the PDF/UA-2 structure checks judge " +
                    "(namespace names, /Alt, /Contents, /Lang, titles) are ciphertext without its key, " +
                    "so none of the checks was run. Decrypt the document first " +
                    "(Tetitulz.Pdf.Encryption.PdfDecryptor) and validate the decrypted file."));
                return Completed(violations);
            }

            RunCheck(doc, catalog, violations, "CheckDocumentStructureType", CheckDocumentStructureType);
            RunCheck(doc, catalog, violations, "CheckNamespacesAreDeclared", CheckNamespacesAreDeclared);
            RunCheck(doc, catalog, violations, "CheckProhibitedHType", CheckProhibitedHType);
            RunCheck(doc, catalog, violations, "CheckProhibitedNoteType", CheckProhibitedNoteType);
            RunCheck(doc, catalog, violations, "CheckFENoteRefsAreReciprocal", CheckFENoteRefsAreReciprocal);
            RunCheck(doc, catalog, violations, "CheckFENoteTypeValue", CheckFENoteTypeValue);
            RunCheck(doc, catalog, violations, "CheckRubyShape", CheckRubyShape);
            RunCheck(doc, catalog, violations, "CheckWarichuShape", CheckWarichuShape);
            RunCheck(doc, catalog, violations, "CheckCaptionPosition", CheckCaptionPosition);
            RunCheck(doc, catalog, violations, "CheckFigureHasAltOrActualText", CheckFigureHasAltOrActualText);
            RunCheck(doc, catalog, violations, "CheckTociHasRef", CheckTociHasRef);
            RunCheck(doc, catalog, violations, "CheckListItemContentIsWrapped", CheckListItemContentIsWrapped);
            RunCheck(doc, catalog, violations, "CheckListNumberingWhenLabelled", CheckListNumberingWhenLabelled);
            RunCheck(doc, catalog, violations, "CheckContinuedFromImpliesContinuedList", CheckContinuedFromImpliesContinuedList);
            RunCheck(doc, catalog, violations, "CheckTableRegularity", CheckTableRegularity);
            RunCheck(doc, catalog, violations, "CheckMathIsChildOfFormula", CheckMathIsChildOfFormula);
            RunCheck(doc, catalog, violations, "CheckHiddenAnnotationsAreArtifacts", CheckHiddenAnnotationsAreArtifacts);
            RunCheck(doc, catalog, violations, "CheckPageTabOrder", CheckPageTabOrder);
            RunCheck(doc, catalog, violations, "CheckNoXfaForms", CheckNoXfaForms);
            RunCheck(doc, catalog, violations, "CheckFormEnclosesAtMostOneWidget", CheckFormEnclosesAtMostOneWidget);
            RunCheck(doc, catalog, violations, "CheckAnnotationAltMatchesContents", CheckAnnotationAltMatchesContents);
            RunCheck(doc, catalog, violations, "CheckDeprecatedSoundMovieAbsent", CheckDeprecatedSoundMovieAbsent);
            RunCheck(doc, catalog, violations, "CheckDeprecatedTrapNetAbsent", CheckDeprecatedTrapNetAbsent);
            RunCheck(doc, catalog, violations, "CheckPopupNotInStructureTree", CheckPopupNotInStructureTree);
            RunCheck(doc, catalog, violations, "CheckPrinterMarkIsArtifact", CheckPrinterMarkIsArtifact);
            RunCheck(doc, catalog, violations, "CheckMarkupAnnotationsEnclosedInAnnot", CheckMarkupAnnotationsEnclosedInAnnot);
            RunCheck(doc, catalog, violations, "CheckLinkAnnotationEnclosedInLinkOrReference", CheckLinkAnnotationEnclosedInLinkOrReference);
            RunCheck(doc, catalog, violations, "CheckScreenHasContents", CheckScreenHasContents);
            RunCheck(doc, catalog, violations, "CheckThreeDAndRichMediaHaveContents", CheckThreeDAndRichMediaHaveContents);
            RunCheck(doc, catalog, violations, "CheckZeroSizeWidgetIsArtifact", CheckZeroSizeWidgetIsArtifact);
            RunCheck(doc, catalog, violations, "CheckFileAttachmentHasAFRelationship", CheckFileAttachmentHasAFRelationship);
            RunCheck(doc, catalog, violations, "CheckInkHasContents", CheckInkHasContents);
            RunCheck(doc, catalog, violations, "CheckTextFieldRVRequiresV", CheckTextFieldRVRequiresV);
            RunCheck(doc, catalog, violations, "CheckPuaContentHasActualTextOrAlt", CheckPuaContentHasActualTextOrAlt);
            RunCheck(doc, catalog, violations, "CheckCatalogLangSpecified", CheckCatalogLangSpecified);
            RunCheck(doc, catalog, violations, "CheckOcConfigNoAsKey", CheckOcConfigNoAsKey);
            RunCheck(doc, catalog, violations, "CheckOcConfigNamesWhenConfigsPresent", CheckOcConfigNamesWhenConfigsPresent);
            RunCheck(doc, catalog, violations, "CheckDisplayDocTitleIsTrue", CheckDisplayDocTitleIsTrue);
            RunCheck(doc, catalog, violations, "CheckEmbeddedFilesHaveDesc", CheckEmbeddedFilesHaveDesc);
            RunCheck(doc, catalog, violations, "CheckXmpDcTitlePresent", CheckXmpDcTitlePresent);
            RunCheck(doc, catalog, violations, "CheckCidToGidMapPresent", CheckCidToGidMapPresent);
            RunCheck(doc, catalog, violations, "CheckFontsUsedForRenderingAreEmbedded", CheckFontsUsedForRenderingAreEmbedded);
            RunCheck(doc, catalog, violations, "CheckSymbolicTrueTypeHasNoEncoding", CheckSymbolicTrueTypeHasNoEncoding);
            RunCheck(doc, catalog, violations, "CheckNonSymbolicTrueTypeEncoding", CheckNonSymbolicTrueTypeEncoding);
            RunCheck(doc, catalog, violations, "CheckTrueTypeCmapSubtables", CheckTrueTypeCmapSubtables);
            RunCheck(doc, catalog, violations, "CheckNonSymbolicTrueTypeDifferences", CheckNonSymbolicTrueTypeDifferences);
            RunCheck(doc, catalog, violations, "CheckCidSystemInfoRelationship", CheckCidSystemInfoRelationship);
            RunCheck(doc, catalog, violations, "CheckToUnicodeValuesValid", CheckToUnicodeValuesValid);
            RunCheck(doc, catalog, violations, "CheckToUnicodeMapsReferencedGlyphs", CheckToUnicodeMapsReferencedGlyphs);
            RunCheck(doc, catalog, violations, "CheckMultiWidgetFieldHasLabelOrContents", CheckMultiWidgetFieldHasLabelOrContents);
            RunCheck(doc, catalog, violations, "CheckEmbeddedCMapWModeConsistent", CheckEmbeddedCMapWModeConsistent);
            RunCheck(doc, catalog, violations, "CheckOutlineTitlesNoPua", CheckOutlineTitlesNoPua);
            RunCheck(doc, catalog, violations, "CheckAnnotationsIncludedInStructureTree", CheckAnnotationsIncludedInStructureTree);
            RunCheck(doc, catalog, violations, "CheckNoMarkedContentReferencesIntoAnnotationAppearances", CheckNoMarkedContentReferencesIntoAnnotationAppearances);
            RunCheck(doc, catalog, violations, "CheckFontWidthConsistency", CheckFontWidthConsistency);
            RunCheck(doc, catalog, violations, "CheckLinkDestinationsAreStructureDestinations", CheckLinkDestinationsAreStructureDestinations);
            RunCheck(doc, catalog, violations, "CheckOutlineDestinationsAreStructureDestinations", CheckOutlineDestinationsAreStructureDestinations);
            RunCheck(doc, catalog, violations, "CheckNamedAndOpenActionDestinationsAreStructureDestinations", CheckNamedAndOpenActionDestinationsAreStructureDestinations);
            RunCheck(doc, catalog, violations, "CheckNoNotdefInFonts", CheckNoNotdefInFonts);
            RunCheck(doc, catalog, violations, "CheckStructureContainmentRules", CheckStructureContainmentRules);
            RunCheck(doc, catalog, violations, "CheckRoleMapNsNotSameNamespace", CheckRoleMapNsNotSameNamespace);
            RunCheck(doc, catalog, violations, "CheckAriaRoleUsesAria11Owner", CheckAriaRoleUsesAria11Owner);
            RunCheck(doc, catalog, violations, "CheckElementBelongsToStandardNamespace", CheckElementBelongsToStandardNamespace);
            RunCheck(doc, catalog, violations, "CheckShownTextIsTaggedOrArtifact", CheckShownTextIsTaggedOrArtifact);
            RunCheck(doc, catalog, violations, "CheckEmbeddedFontsDefineShownGlyphs", CheckEmbeddedFontsDefineShownGlyphs);
            RunCheck(doc, catalog, violations, "CheckFormulaRepresentation", CheckFormulaRepresentation);
            RunCheck(doc, catalog, violations, "CheckWidgetActionsAreDescribed", CheckWidgetActionsAreDescribed);
            RunCheck(doc, catalog, violations, "CheckElementAndAnnotationTextNoPua", CheckElementAndAnnotationTextNoPua);

            return Completed(violations);
        }

        /// <summary>
        /// Runs one check, isolated: a failure in it becomes a UA2-99 finding rather than
        /// aborting the checks after it — the same isolation
        /// <c>PdfDeepAccessibilityChecker.RunCheck</c> gives UA10-UA30, for the same reason (one
        /// clause's bug must not silently hide every later clause's findings).
        /// </summary>
        private static void RunCheck(PdfDocument doc, PdfDictionary catalog,
            List<PdfUa2StructureViolation> v, string checkName, Action<PdfDocument, PdfDictionary, List<PdfUa2StructureViolation>> check)
        {
            try
            {
                check(doc, catalog, v);
            }
            catch (Exception ex)
            {
                Report($"PdfUa2StructureChecker.{checkName}", "check aborted", ex);
                v.Add(new PdfUa2StructureViolation("UA2-99",
                    $"The {checkName} check could not be completed " +
                    $"({ex.GetType().Name}: {ex.Message}). The conformance report is incomplete."));
            }
        }

        /// <summary>The result, with a UA2-99 notice when an object the checks read could not be read.</summary>
        private static PdfUa2StructureResult Completed(List<PdfUa2StructureViolation> violations)
        {
            string unreadable = UnreadableObjects.Notice();
            if (unreadable != null) violations.Add(new PdfUa2StructureViolation("UA2-99", unreadable));
            string annotationsCut = ScanReuse.AnnotationsCutNotice("the font and appearance checks");
            if (annotationsCut != null) violations.Add(new PdfUa2StructureViolation("UA2-99", annotationsCut));
            return new PdfUa2StructureResult(violations);
        }

        private static PdfObject Resolve(PdfDocument doc, PdfObject obj)
            => UnreadableObjects.Resolve(doc, obj,
                ex => Report("PdfUa2StructureChecker.Resolve", "could not resolve indirect reference", ex));

        // ----------------------------------------------------------------
        //  Clause 8.2.5.2 — Document and DocumentFragment
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.2.5.2: "The structure tree root … shall contain a single Document
        /// structure element as its only child … The namespace for that element shall be
        /// specified as the PDF 2.0 namespace, in accordance with ISO 32000-2:2020, 14.8.6."
        /// </summary>
        /// <remarks>
        /// <para><b>"Specified as" means an explicit <c>/NS</c>, not "whichever namespace this
        /// file's version implies.</b>" ISO 32000-2:2020 §14.8.6.1 is unconditional: "When a
        /// namespace is not explicitly specified for a given structure element … it shall be
        /// assumed to be within [the] default standard structure namespace" — and the default is
        /// always the <b>PDF 1.7</b> namespace (<c>http://iso.org/pdf/ssn</c>), regardless of the
        /// file's own declared PDF version. A <c>Document</c> element with no <c>/NS</c> entry at
        /// all is therefore, by this default, in the wrong namespace for this rule — it must
        /// explicitly name <see cref="Pdf2StandardStructureNamespace"/>, not merely omit
        /// contradicting it.</para>
        /// <para>Only the mandatory ("shall") half of §8.2.5.2 is checked. The clause's other two
        /// paragraphs — enclosing an included logical document/fragment in a
        /// <c>Document</c>/<c>DocumentFragment</c> element ("should"), and when
        /// <c>DocumentFragment</c> is appropriate — are guidance a checker cannot verify from
        /// structure alone (they turn on authorial intent about a document's origin), matching
        /// how §8.2.5.2's own text distinguishes "shall" from "should".</para>
        /// </remarks>
        private static void CheckDocumentStructureType(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var structTreeRootObj = Resolve(doc, catalog[PdfName.StructTreeRoot]);
            var structTreeRoot = structTreeRootObj as PdfDictionary;
            if (structTreeRoot == null)
            {
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.2",
                    "The document catalog has no /StructTreeRoot; PDF/UA-2 requires the structure " +
                    "tree root to contain a single Document structure element as its only child " +
                    "(ISO 14289-2 §8.2.5.2)."));
                return;
            }

            IReadOnlyList<PdfObject> rawKids = StructTreeWalker.GetRawKids(structTreeRoot, doc);
            if (rawKids.Count != 1)
            {
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.2",
                    $"The structure tree root must contain exactly one child (a Document structure " +
                    $"element); found {rawKids.Count} (ISO 14289-2 §8.2.5.2)."));
                return;
            }

            var child = Resolve(doc, rawKids[0]) as PdfDictionary;
            if (child == null)
            {
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.2",
                    "The structure tree root's single child does not resolve to a structure " +
                    "element dictionary (ISO 14289-2 §8.2.5.2)."));
                return;
            }

            string role = StructTreeWalker.GetRole(child, doc);
            if (role != "Document")
            {
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.2",
                    "The structure tree root's only child must be a Document structure element " +
                    $"(ISO 14289-2 §8.2.5.2); found /S {(role ?? "(absent)")}."));
                return;
            }

            if (!IsInPdf2Namespace(doc, child))
            {
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.2",
                    "The Document structure element must specify its namespace as the PDF 2.0 " +
                    $"standard structure namespace ({Pdf2StandardStructureNamespace}), per " +
                    "ISO 14289-2 §8.2.5.2 and ISO 32000-2:2020 §14.8.6 — an absent /NS defaults to " +
                    "the PDF 1.7 namespace, not PDF 2.0, so it must be present and explicit."));
            }
        }

        /// <summary>
        /// ISO 32000-2:2020 Table 354: the structure tree root's <c>/Namespaces</c> is "an array of
        /// namespace dictionaries used in the document" — so a namespace an element points at must
        /// be declared there.
        /// </summary>
        /// <remarks>
        /// <para><b>Found by authoring, not by reading the clause list.</b> Every other namespace
        /// rule here reads an element's own <c>/NS</c> and stops, so a document whose elements all
        /// reference a namespace dictionary the root never declares passed every check — which a
        /// mutation deleting the auto-tagger's <c>/Namespaces</c> write demonstrated, by staying
        /// green. That is a real conformance failure: a consumer resolving roles through
        /// <c>/Namespaces</c>, which Table 354 makes the document's list, finds nothing there.</para>
        ///
        /// <para><b>It reports the namespace, not each element.</b> A tree of ten thousand elements
        /// all pointing at one undeclared dictionary is one defect, and naming it once says what to
        /// fix; naming it per element would bury the finding under itself.</para>
        /// </remarks>
        private static void CheckNamespacesAreDeclared(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var structTreeRoot = ResolveStructTreeRoot(doc, catalog);
            if (structTreeRoot == null) return;

            var declared = new HashSet<string>(StringComparer.Ordinal);
            if (Resolve(doc, structTreeRoot[PdfName.Namespaces]) is PdfArray namespaces)
            {
                foreach (PdfObject entry in namespaces)
                {
                    if (Resolve(doc, entry) is PdfDictionary ns &&
                        (Resolve(doc, ns[PdfName.NS]) as PdfString)?.GetText() is string name)
                        declared.Add(name);
                }
            }

            var undeclared = new HashSet<string>(StringComparer.Ordinal);
            StructTreeWalker.Walk(structTreeRoot, doc, index: null, node =>
            {
                if (node.Dict == null) return;
                if (!(Resolve(doc, node.Dict[PdfName.NS]) is PdfDictionary ns)) return;

                string name = (Resolve(doc, ns[PdfName.NS]) as PdfString)?.GetText();
                if (name != null && !declared.Contains(name)) undeclared.Add(name);
            });

            foreach (string name in undeclared)
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.2",
                    $"A structure element names the namespace {name}, which the structure tree " +
                    "root's /Namespaces array does not declare; ISO 32000-2:2020 Table 354 makes " +
                    "that array the list of namespace dictionaries used in the document."));
        }

        /// <summary>
        /// True when <paramref name="element"/>'s <c>/NS</c> entry resolves to a namespace
        /// dictionary (ISO 32000-2:2020 Table 356) whose own <c>/NS</c> name equals
        /// <see cref="Pdf2StandardStructureNamespace"/>. An absent <c>/NS</c> answers false — see
        /// <see cref="CheckDocumentStructureType"/>'s remarks on why the default namespace does
        /// not satisfy this rule.
        /// </summary>
        private static bool IsInPdf2Namespace(PdfDocument doc, PdfDictionary element)
            => IsInNamespace(doc, element, Pdf2StandardStructureNamespace);

        /// <summary>
        /// Whether <paramref name="node"/> is the standard structure type <paramref name="type"/> of the
        /// PDF 1.7 namespace — or of the PDF 2.0 namespace too, when <paramref name="inPdf2Namespace"/> —
        /// followed from its own <c>/S</c>: in the default namespace (no <c>/NS</c>, ISO 32000-2
        /// §14.8.6.1: PDF 1.7) through the structure tree's <c>/RoleMap</c>
        /// (<see cref="PdfRoleMap.Means"/>); in another namespace through its <c>/RoleMapNS</c> (§14.8.6.2),
        /// a bare name a target in the default namespace, an array a type and its namespace, under
        /// <see cref="PdfRoleMap.MaxHops"/>. A chain that ends outside the standard namespaces, or cannot be
        /// followed, is not the type.
        /// </summary>
        private static bool IsStandardStructureType(PdfDocument doc, StructNode node,
            IReadOnlyDictionary<string, string> roleMap, string type, bool inPdf2Namespace)
        {
            if (node?.Dict == null || node.Role == null) return false;
            string role = node.Role;
            var nsDict = Resolve(doc, node.Dict[PdfName.NS]) as PdfDictionary;
            for (int hop = 0; hop <= PdfRoleMap.MaxHops; hop++)
            {
                if (nsDict == null) return PdfRoleMap.Means(role, roleMap, type);
                string ns = (Resolve(doc, nsDict[PdfName.NS]) as PdfString)?.GetText();
                if (ns == Pdf17StandardStructureNamespace) return role == type;
                if (ns == Pdf2StandardStructureNamespace) return inPdf2Namespace && role == type;
                if (ns == null || ns == MathMlNamespace) return false;

                PdfObject mapped = Resolve(doc, (Resolve(doc, nsDict[PdfName.RoleMapNS]) as PdfDictionary)?[new PdfName(role)]);
                if (mapped is PdfName bare)
                {
                    role = bare.Value;
                    nsDict = null;
                }
                else if (mapped is PdfArray target && target.Count >= 2
                         && Resolve(doc, target[0]) is PdfName targetRole
                         && Resolve(doc, target[1]) is PdfDictionary targetNs)
                {
                    role = targetRole.Value;
                    nsDict = targetNs;
                }
                else return false;
            }
            return false;
        }

        /// <summary>The catalog's <c>/StructTreeRoot</c>, or <see langword="null"/> when absent —
        /// shared by the whole-tree checks below, each of which has nothing to walk without it
        /// and simply finds nothing (not a violation of <i>their</i> clause; §8.2.5.2's own check
        /// is what reports a missing structure tree root).</summary>
        private static PdfDictionary ResolveStructTreeRoot(PdfDocument doc, PdfDictionary catalog)
            => Resolve(doc, catalog[PdfName.StructTreeRoot]) as PdfDictionary;

        /// <summary>Individual occurrences reported before collapsing into one summary — the same
        /// shape (if not the same number) as <c>PdfDeepAccessibilityChecker.IndividualReportLimit</c>,
        /// so a pathological tree cannot flood the result.</summary>
        private const int IndividualReportLimit = 25;

        // ----------------------------------------------------------------
        //  Clause 8.2.5.12 — Heading (Hn) and (H)
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.2.5.12: "Conforming files shall use the explicitly numbered heading
        /// structure types (H1-Hn) and shall not use the H structure type."
        /// </summary>
        /// <remarks>
        /// <para>Only this one, purely structural half of §8.2.5.12 is checked — a literal <c>/S
        /// H</c> anywhere in the tree is unambiguously prohibited. The clause's other requirement
        /// ("where a heading's level is evident, the heading level … shall match") needs judging
        /// real content against its nesting level, which this slice does not attempt.</para>
        /// <para><b>By the standard structure type an element is</b> (<see cref="IsStandardStructureType"/>),
        /// in either standard namespace, both of which define <c>H</c>: an <c>/S /H</c> in the default
        /// namespace, a custom type the <c>/RoleMap</c> or a namespace's <c>/RoleMapNS</c> maps to it —
        /// §8.2.4: a custom type "shall conform to the requirements for the standard structure types to
        /// which they are role mapped" — and not a custom namespace's own <c>H</c> mapped elsewhere.</para>
        /// </remarks>
        private static void CheckProhibitedHType(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var structTreeRoot = ResolveStructTreeRoot(doc, catalog);
            if (structTreeRoot == null) return;

            var roleMap = PdfRoleMap.Build(structTreeRoot, o => Resolve(doc, o));
            int found = 0;
            StructTreeWalker.Walk(structTreeRoot, doc, index: null, node =>
            {
                if (!IsStandardStructureType(doc, node, roleMap, "H", inPdf2Namespace: true)) return;
                found++;
                if (found > IndividualReportLimit) return;
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.12",
                    "The H structure type must not be used; conforming files use the explicitly " +
                    $"numbered heading types H1-Hn instead (ISO 14289-2 §8.2.5.12)." +
                    (node.ObjectNumber > 0 ? $" Object {node.ObjectNumber}." : "")));
            });

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.12",
                    $"…and {found - IndividualReportLimit} more H structure element(s) not listed individually."));
        }

        // ----------------------------------------------------------------
        //  Clause 8.2.5.14.1 — Footnotes and Endnotes (FENote): the Note prohibition
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.2.5.14.1: "FENote effectively replaces the Note structure type
        /// specified in ISO 32000-1:2008, 14.8.4.4.1. The Note standard structure type shall not
        /// be present in conforming files."
        /// </summary>
        /// <remarks>
        /// <para>Only the prohibition is checked here; the clause's <c>/Ref</c> linkage between a citation
        /// and its <c>FENote</c> is <see cref="CheckFENoteRefsAreReciprocal"/>.</para>
        /// <para><b>The standard type, not the name.</b> NOTE 1: "This does not prohibit using a Note
        /// structure type in any other namespace if it is role mapped to an allowed structure type" — so
        /// a custom namespace's <c>Note</c> mapped to <c>FENote</c> is not reported, and a custom type
        /// mapped to the PDF 1.7 <c>Note</c> is (<see cref="IsStandardStructureType"/>; the PDF 2.0
        /// namespace defines no <c>Note</c>).</para>
        /// </remarks>
        private static void CheckProhibitedNoteType(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var structTreeRoot = ResolveStructTreeRoot(doc, catalog);
            if (structTreeRoot == null) return;

            var roleMap = PdfRoleMap.Build(structTreeRoot, o => Resolve(doc, o));
            int found = 0;
            StructTreeWalker.Walk(structTreeRoot, doc, index: null, node =>
            {
                if (!IsStandardStructureType(doc, node, roleMap, "Note", inPdf2Namespace: false)) return;
                found++;
                if (found > IndividualReportLimit) return;
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.14.1",
                    "The Note structure type must not be present; conforming files use FENote " +
                    $"instead (ISO 14289-2 §8.2.5.14.1)." +
                    (node.ObjectNumber > 0 ? $" Object {node.ObjectNumber}." : "")));
            });

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.14.1",
                    $"…and {found - IndividualReportLimit} more Note structure element(s) not listed individually."));
        }

        /// <summary>
        /// ISO 14289-2:2024 §8.2.5.14.1: "Real content that refers to footnotes or endnotes … shall
        /// use the Ref entry … on the referring structure element to reference the corresponding
        /// FENote. The FENote shall also use the Ref entry to identify all citations that reference
        /// it." Shares <c>UA2-8.2.5.14.1</c> with <see cref="CheckProhibitedNoteType"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>Checked in one direction only, the one the file itself decides.</b> An element
        /// whose <c>/Ref</c> (ISO 32000-2:2020 Table 355: indirect references to the structure
        /// elements its content refers to) names an <c>FENote</c> is, by that entry, a citation of
        /// it — so that <c>FENote</c>'s own <c>/Ref</c> must name the element back. The converse
        /// is not checked: whether content that <i>does not</i> carry a <c>/Ref</c> refers to a
        /// note is a judgment about the content, and an <c>FENote</c>'s <c>/Ref</c> may name
        /// something that is not a citation, which a converse check would misreport.</para>
        /// <para>Matched by object number, so an element stored as a direct dictionary (which no
        /// <c>/Ref</c> could name) and a <c>/Ref</c> entry that is not an indirect reference are
        /// skipped as unprovable. An <c>FENote</c> whose <c>/Ref</c> names another <c>FENote</c> is
        /// judged like any other element: a one-sided edge between two notes is a violation whether
        /// it is read as the first citing the second or as the second being listed among the first's
        /// citations without referencing it. Each <c>FENote</c>'s <c>/Ref</c> is read into a set
        /// once, so the work is linear in the total length of the arrays, whatever a hostile file
        /// makes them.</para>
        /// </remarks>
        private static void CheckFENoteRefsAreReciprocal(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var structTreeRoot = ResolveStructTreeRoot(doc, catalog);
            if (structTreeRoot == null) return;

            var namedBy = new Dictionary<int, HashSet<int>>();   // FENote object number → its /Ref targets
            var reported = new HashSet<(int citation, int note)>();
            int found = 0;
            StructTreeWalker.Walk(structTreeRoot, doc, index: null, node =>
            {
                if (found > IndividualReportLimit) return;
                if (node.Dict == null || node.ObjectNumber <= 0) return;
                if (!(Resolve(doc, node.Dict[PdfName.Ref]) is PdfArray refs)) return;

                foreach (PdfObject raw in refs)
                {
                    // ReferenceObjectNumber, not the base ObjectNumber — which is the number of the
                    // indirect object a value was parsed from, and 0 on a reference inside an array.
                    if (!(raw is PdfReference noteRef) || noteRef.ReferenceObjectNumber <= 0) continue;
                    int noteNumber = noteRef.ReferenceObjectNumber;
                    var note = Resolve(doc, raw) as PdfDictionary;
                    if (StructTreeWalker.GetRole(note, doc) != "FENote") continue;

                    if (!namedBy.TryGetValue(noteNumber, out HashSet<int> backRefs))
                        namedBy[noteNumber] = backRefs = ReferencedObjectNumbers(doc, note);
                    if (backRefs.Contains(node.ObjectNumber)) continue;
                    if (!reported.Add((node.ObjectNumber, noteNumber))) continue;

                    found++;
                    if (found > IndividualReportLimit) return;
                    v.Add(new PdfUa2StructureViolation("UA2-8.2.5.14.1",
                        $"A {node.Role ?? "structure"} element (object {node.ObjectNumber}) cites the " +
                        $"FENote at object {noteNumber} through its /Ref, but that FENote's " +
                        "/Ref does not name it back; §8.2.5.14.1 requires an FENote to identify every " +
                        "citation that references it (ISO 14289-2 §8.2.5.14.1)."));
                }
            });

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.14.1",
                    $"…and at least one more citation(s) an FENote does not name back, " +
                    "not listed individually."));
        }

        /// <summary>The object numbers of every indirect reference in <paramref name="element"/>'s <c>/Ref</c> array.</summary>
        private static HashSet<int> ReferencedObjectNumbers(PdfDocument doc, PdfDictionary element)
        {
            var numbers = new HashSet<int>();
            if (Resolve(doc, element[PdfName.Ref]) is PdfArray refs)
                foreach (PdfObject raw in refs)
                    if (raw is PdfReference r && r.ReferenceObjectNumber > 0) numbers.Add(r.ReferenceObjectNumber);
            return numbers;
        }

        // ----------------------------------------------------------------
        //  Clause 8.2.5.14.2 — FENote attribute owner: NoteType's value set
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.2.5.14.2, Table 3: "The value of the NoteType shall be one of the
        /// following …: Footnote, Endnote, None."
        /// </summary>
        /// <remarks>
        /// <para>Read wherever an attribute object owned by <c>FENote</c> declares
        /// <c>NoteType</c> — the element's <c>/A</c> or its <c>/C</c> classes, through
        /// <see cref="PdfStructureAttributes.ReadAll(PdfDictionary, IReadOnlyDictionary{string, PdfObject}, Func{PdfObject, PdfObject})"/>. Only the value set is checked: whether a
        /// <c>NoteType</c> should appear at all is "if appropriate to the content … should".
        /// A <c>NoteType</c> under a different owner is not this table's attribute and is not
        /// judged.</para>
        /// </remarks>
        private static void CheckFENoteTypeValue(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var structTreeRoot = ResolveStructTreeRoot(doc, catalog);
            if (structTreeRoot == null) return;

            Func<PdfObject, PdfObject> resolve = o => Resolve(doc, o);
            var classMap = PdfStructureAttributes.BuildClassMap(structTreeRoot, resolve);

            int found = 0;
            StructTreeWalker.Walk(structTreeRoot, doc, index: null, node =>
            {
                if (found > IndividualReportLimit || node.Dict == null) return;

                var byOwner = AttributesOf(node.Dict, classMap, resolve);
                if (!byOwner.TryGetValue("FENote", out var entries)) return;
                if (!entries.TryGetValue(PdfName.NoteType.Value, out PdfObject value)) return;

                string noteType = (value as PdfName)?.Value;
                if (noteType == "Footnote" || noteType == "Endnote" || noteType == "None") return;

                found++;
                if (found > IndividualReportLimit) return;
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.14.2",
                    $"A {node.Role ?? "structure"} element's FENote-owned NoteType is " +
                    $"{(noteType != null ? "/" + noteType : "not a name")}; §8.2.5.14.2 allows only " +
                    "Footnote, Endnote or None (ISO 14289-2 §8.2.5.14.2)." +
                    (node.ObjectNumber > 0 ? $" Object {node.ObjectNumber}." : "")));
            });

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.14.2",
                    $"…and at least one more invalid NoteType value(s), not listed individually."));
        }

        /// <summary>
        /// The roles of <paramref name="element"/>'s direct <c>/K</c> children, in order —
        /// resolving each raw kid and reading its <c>/S</c>, skipping any kid that is not itself a
        /// structure element (an MCID leaf, an <c>/MCR</c>/<c>/OBJR</c> dictionary) since
        /// <see cref="StructTreeWalker.GetRole"/> answers <see langword="null"/> for those and a
        /// null role can never match a role name this file checks against.
        /// </summary>
        private static List<string> GetChildRoles(PdfDocument doc, PdfDictionary element)
        {
            var roles = new List<string>();
            foreach (PdfObject rawKid in StructTreeWalker.GetRawKids(element, doc))
            {
                var childDict = Resolve(doc, rawKid) as PdfDictionary;
                string role = StructTreeWalker.GetRole(childDict, doc);
                if (role != null) roles.Add(role);
            }
            return roles;
        }

        // ----------------------------------------------------------------
        //  Clause 8.2.5.23 — Ruby (Ruby, RB, RT, RP)
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.2.5.23: "The Ruby structure element shall contain a single RB
        /// structure element enclosing the annotated base text, and a single RT structure element
        /// enclosing the annotating ruby text."
        /// </summary>
        /// <remarks>
        /// This holds for <b>both</b> of the clause's shapes — the plain <c>RB, RT</c> pair and the
        /// four-element parenthetical form <c>RB, RP, RT, RP</c> — since both include exactly one
        /// <c>RB</c> and exactly one <c>RT</c>; only <c>RP</c>'s presence/count/order distinguishes
        /// them, and which shape applies is a semantic choice ("when used in this manner") this
        /// checker does not attempt to judge. So only the universal half is checked: every
        /// <c>Ruby</c> element must have exactly one <c>RB</c> child and exactly one <c>RT</c>
        /// child, in any order and with any number of interspersed <c>RP</c>s. The four-element
        /// form's specific <c>RB, RP, RT, RP</c> *order* requirement is deliberately not enforced,
        /// since a well-formed <c>RB, RT</c> pair (no <c>RP</c>s at all) would fail an
        /// order/count-of-four check that does not apply to it.
        /// </remarks>
        private static void CheckRubyShape(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var structTreeRoot = ResolveStructTreeRoot(doc, catalog);
            if (structTreeRoot == null) return;

            int found = 0;
            StructTreeWalker.Walk(structTreeRoot, doc, index: null, node =>
            {
                if (node.Role != "Ruby") return;

                var roles = GetChildRoles(doc, node.Dict);
                int rb = roles.FindAll(r => r == "RB").Count;
                int rt = roles.FindAll(r => r == "RT").Count;
                if (rb == 1 && rt == 1) return;

                found++;
                if (found > IndividualReportLimit) return;
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.23",
                    "A Ruby structure element must contain exactly one RB (base text) and exactly " +
                    $"one RT (ruby text) child (ISO 14289-2 §8.2.5.23); found {rb} RB and {rt} RT." +
                    (node.ObjectNumber > 0 ? $" Object {node.ObjectNumber}." : "")));
            });

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.23",
                    $"…and {found - IndividualReportLimit} more malformed Ruby structure element(s) not listed individually."));
        }

        // ----------------------------------------------------------------
        //  Clause 8.2.5.24 — Warichu (WT, WP)
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.2.5.24: "Content typeset as warichu shall be tagged in a
        /// three-element sequence consisting of the structure elements WP, WT and WP, grouped
        /// inside a Warichu structure element." The first <c>WP</c> encloses the opening
        /// delimiter, the <c>WT</c> the main body, the second <c>WP</c> the closing delimiter —
        /// unlike Ruby's shape, this one is unconditional whenever a <c>Warichu</c> element exists
        /// at all, so both count <b>and</b> order are checked.
        /// </summary>
        /// <remarks>
        /// The clause's other requirement — "The Warichu structure element shall not be used for
        /// content that is not typeset as warichu" — is not checkable from structure alone (it
        /// turns on what the content actually is), matching every other "shall not be used for
        /// purpose X" rule this checker declines across the clauses it covers.
        /// </remarks>
        private static void CheckWarichuShape(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var structTreeRoot = ResolveStructTreeRoot(doc, catalog);
            if (structTreeRoot == null) return;

            int found = 0;
            StructTreeWalker.Walk(structTreeRoot, doc, index: null, node =>
            {
                if (node.Role != "Warichu") return;

                var roles = GetChildRoles(doc, node.Dict);
                bool ok = roles.Count == 3 && roles[0] == "WP" && roles[1] == "WT" && roles[2] == "WP";
                if (ok) return;

                found++;
                if (found > IndividualReportLimit) return;
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.24",
                    "A Warichu structure element must contain exactly the three-element sequence " +
                    $"WP, WT, WP, in that order (ISO 14289-2 §8.2.5.24); found [{string.Join(", ", roles)}]." +
                    (node.ObjectNumber > 0 ? $" Object {node.ObjectNumber}." : "")));
            });

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.24",
                    $"…and {found - IndividualReportLimit} more malformed Warichu structure element(s) not listed individually."));
        }

        // ----------------------------------------------------------------
        //  Clause 8.2.5.27 — Caption
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.2.5.27: "When present, the Caption element shall be a child of the
        /// structure element enclosing the content to which the caption pertains. When the caption
        /// is intended to be consumed before the content …, it shall be the first child, but when
        /// it is intended to follow the content, it shall be the last child."
        /// </summary>
        /// <remarks>
        /// The clause names two cases (consumed before / after the content) that between them
        /// exhaust the legal positions: first child or last child, never anywhere in the middle.
        /// Which of the two applies is a semantic, authorial choice this checker does not attempt
        /// to judge — but a Caption that is <i>neither</i> is not conforming under either reading,
        /// so that half is checkable without knowing which case the author intended. The clause's
        /// first sentence ("a child of the structure element enclosing the content …") is not
        /// separately checked: a walked <c>Caption</c> node always has some parent by construction
        /// (the walk starts at the struct tree root), so there is nothing more concrete to verify
        /// from structure alone.
        /// </remarks>
        private static void CheckCaptionPosition(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var structTreeRoot = ResolveStructTreeRoot(doc, catalog);
            if (structTreeRoot == null) return;

            int found = 0;
            StructTreeWalker.Walk(structTreeRoot, doc, index: null, node =>
            {
                if (node.Role != "Caption" || node.Parent?.Dict == null) return;

                int index = FindChildIndex(doc, node.Parent.Dict, node.Dict, node.ObjectNumber,
                    out int structureElementSiblingCount);
                if (index < 0 || index == 0 || index == structureElementSiblingCount - 1) return;

                found++;
                if (found > IndividualReportLimit) return;
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.27",
                    "A Caption structure element must be the first or last child of the structure " +
                    "element it captions, never in between (ISO 14289-2 §8.2.5.27)." +
                    (node.ObjectNumber > 0 ? $" Object {node.ObjectNumber}." : "")));
            });

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.27",
                    $"…and {found - IndividualReportLimit} more misplaced Caption structure element(s) not listed individually."));
        }

        /// <summary>
        /// The index of <paramref name="target"/> (identified by object number when it has one,
        /// otherwise by dictionary reference identity for a direct/inline child) among
        /// <paramref name="parent"/>'s direct <c>/K</c> kids that resolve to a structure element,
        /// or -1 if not found — alongside <paramref name="structureElementSiblingCount"/>, the
        /// total number of such kids, counted over the <b>same</b> filtered list the index comes
        /// from. Returning both from one pass is deliberate: an earlier draft of this check counted
        /// the total separately with a plain <c>GetRawKids(...).Count</c> (every raw kid, MCID
        /// leaves included), which desyncs from an index counted over structure-element kids only
        /// — a Caption genuinely last among its structure-element siblings could then fail
        /// <c>index == count - 1</c> whenever the parent also carried any non-element kid,
        /// producing a false positive this one-pass version cannot reintroduce.
        /// </summary>
        /// <remarks>
        /// Matched the same way <c>StructTreeWalker</c>'s own cycle guard identifies a node — by
        /// object number first — rather than by role name, since two siblings can share a role.
        /// </remarks>
        private static int FindChildIndex(PdfDocument doc, PdfDictionary parent, PdfDictionary target,
            int targetObjectNumber, out int structureElementSiblingCount)
        {
            int index = 0;
            int foundAt = -1;
            foreach (PdfObject rawKid in StructTreeWalker.GetRawKids(parent, doc))
            {
                var childDict = Resolve(doc, rawKid) as PdfDictionary;
                if (StructTreeWalker.GetRole(childDict, doc) == null) continue; // not a structure element

                bool isTarget = targetObjectNumber > 0
                    ? rawKid is PdfReference r && r.ReferenceObjectNumber == targetObjectNumber
                    : ReferenceEquals(childDict, target);
                if (isTarget) foundAt = index;
                index++;
            }
            structureElementSiblingCount = index;
            return foundAt;
        }

        // ----------------------------------------------------------------
        //  Clause 8.2.5.28.2 — Figure properties
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.2.5.28.2: "A Figure structure element shall have at least one of the
        /// following properties: a) an alternate description (Alt property) …; b) a replacement
        /// text (ActualText property) …"
        /// </summary>
        /// <remarks>
        /// <para>Presence only, matching this clause's own wording ("shall have … a property") —
        /// not the <i>quality</i> of the alt text (empty string, a generic placeholder like
        /// "image"). That quality judgment already exists for PDF/UA-1's parallel requirement,
        /// <c>PdfDeepAccessibilityChecker.CheckFigureAltTextQuality</c> (UA15), and is out of
        /// scope for this slice — a presence check and a quality check are different questions,
        /// and duplicating the second here would be the same check drifting into two places to
        /// maintain rather than one reused (CLAUDE.md's DRY section). Both entries are direct
        /// structure-element dictionary keys (ISO 32000-2:2020 §14.9.3/§14.9.4, `/Alt` and
        /// `/ActualText` — read directly from <c>node.Dict</c>, not from a marked-content
        /// property list.</para>
        /// <para>An empty string for either counts as absent, the same reading UA15 already uses:
        /// a present-but-empty <c>/Alt</c> is not "having" the property in any useful sense.</para>
        /// </remarks>
        private static void CheckFigureHasAltOrActualText(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var structTreeRoot = ResolveStructTreeRoot(doc, catalog);
            if (structTreeRoot == null) return;

            int found = 0;
            StructTreeWalker.Walk(structTreeRoot, doc, index: null, node =>
            {
                if (node.Role != "Figure" || node.Dict == null) return;
                if (HasNonEmptyString(doc, node.Dict, PdfName.Alt)) return;
                if (HasNonEmptyString(doc, node.Dict, PdfName.ActualText)) return;

                found++;
                if (found > IndividualReportLimit) return;
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.28.2",
                    "A Figure structure element must have an Alt or an ActualText entry " +
                    "(ISO 14289-2 §8.2.5.28.2)." +
                    (node.ObjectNumber > 0 ? $" Object {node.ObjectNumber}." : "")));
            });

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.28.2",
                    $"…and {found - IndividualReportLimit} more Figure structure element(s) missing both, not listed individually."));
        }

        private static bool HasNonEmptyString(PdfDocument doc, PdfDictionary dict, PdfName key)
            => Resolve(doc, dict[key]) is PdfString s && !string.IsNullOrEmpty(s.GetText());

        // ----------------------------------------------------------------
        //  Clause 8.2.5.8 — Table of Contents (TOCI must identify its target via /Ref)
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.2.5.8: "Each TOCI in the table of contents shall identify the
        /// target of the reference using the Ref entry, either directly on the TOCI structure
        /// element itself or on a child structure element contained within, such as a Reference
        /// structure element."
        /// </summary>
        /// <remarks>
        /// <para>A variant of §8.2.5.28.2's shape (an element's own attribute, checked by role),
        /// widened from "the element itself" to "the element or anything in its own subtree" —
        /// "on a child structure element contained within" reads as anywhere nested, not only a
        /// direct child, so a <c>Reference</c> element two levels down still satisfies it. Reuses
        /// <see cref="StructTreeWalker.Walk"/>'s documented ability to start at any structure
        /// element, not only the tree root, rather than writing a second descent.</para>
        /// <para><b>Only the Ref-identification sentence is checked.</b> §8.2.5.8's other
        /// sentences — that tables of contents themselves must be identified using TOC/TOCI at
        /// all, and that leaders must be artifacts — both need recognizing a table of contents or
        /// a leader from real content with no structural marker distinguishing either from
        /// ordinary text, a semantic judgment this checker makes nowhere else.</para>
        /// <para>ISO 32000-2:2020 Table 355 defines <c>/Ref</c> as always an array ("containing
        /// zero, one or more indirect references to structure elements"), never a bare reference,
        /// so non-empty means a non-empty array, not merely a present key.</para>
        /// </remarks>
        private static void CheckTociHasRef(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var structTreeRoot = ResolveStructTreeRoot(doc, catalog);
            if (structTreeRoot == null) return;

            int found = 0;
            StructTreeWalker.Walk(structTreeRoot, doc, index: null, node =>
            {
                if (node.Role != "TOCI" || node.Dict == null || found > IndividualReportLimit) return;
                if (HasRefAnywhereInSubtree(doc, node.Dict)) return;

                found++;
                if (found > IndividualReportLimit) return;
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.8",
                    "A TOCI structure element does not identify the target of its reference via a " +
                    "non-empty Ref entry, on itself or on any structure element it contains " +
                    "(ISO 14289-2 §8.2.5.8)." +
                    (node.ObjectNumber > 0 ? $" Object {node.ObjectNumber}." : "")));
            });

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.8",
                    $"…and at least one more TOCI structure element(s) missing a Ref, not listed individually."));
        }

        private static bool HasRefAnywhereInSubtree(PdfDocument doc, PdfDictionary root)
        {
            bool found = false;
            StructTreeWalker.Walk(root, doc, index: null, node =>
            {
                if (found || node.Dict == null) return;
                if (Resolve(doc, node.Dict[PdfName.Ref]) is PdfArray refs && refs.Count > 0) found = true;
            });
            return found;
        }

        // ----------------------------------------------------------------
        //  Clause 8.2.5.25 — List (L, LI, LBody)
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.2.5.25: "Any real content within an LI structure element that is
        /// not enclosed in a Lbl structure element shall be enclosed in an LBody structure
        /// element."
        /// </summary>
        /// <remarks>
        /// <para>Read as a shape constraint on <c>LI</c>'s direct children: every child that
        /// resolves to a structure element must have role <c>Lbl</c> or <c>LBody</c> — nothing
        /// else, structure or otherwise, belongs directly under <c>LI</c> — and every direct
        /// child that is <i>not</i> a structure element (a bare MCID, an <c>/MCR</c> or
        /// <c>/OBJR</c> leaf) is exactly the "real content … not enclosed" case the clause
        /// prohibits, since content wrapped as the rule demands would be one level deeper, inside
        /// an <c>LBody</c>.</para>
        /// <para><b>Fail-safe on the ambiguous case.</b> <see cref="StructKidClassifier.Classify"/>
        /// can answer <see cref="StructKidKind.Unknown"/> for a direct <c>/K</c> child dictionary
        /// that names no recognisable kind at all (§14.7.2's own escape hatch for "not really a
        /// kid") — that case is skipped rather than reported as "real content", matching this
        /// codebase's established reading of <see cref="StructKidKind.Unknown"/> everywhere else
        /// it appears: "not a kid at all", not "content of an unknown kind".</para>
        /// <para>The clause's attribute sentences — <c>ListNumbering</c> when labels are present,
        /// and <c>ContinuedList</c> for a list that declares what it continues — are
        /// <see cref="CheckListNumberingWhenLabelled"/> and
        /// <see cref="CheckContinuedFromImpliesContinuedList"/>, sharing this code.</para>
        /// </remarks>
        private static void CheckListItemContentIsWrapped(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var structTreeRoot = ResolveStructTreeRoot(doc, catalog);
            if (structTreeRoot == null) return;

            int found = 0;
            StructTreeWalker.Walk(structTreeRoot, doc, index: null, node =>
            {
                if (node.Role != "LI" || node.Dict == null || found > IndividualReportLimit) return;

                foreach (PdfObject rawKid in StructTreeWalker.GetRawKids(node.Dict, doc))
                {
                    var resolved = Resolve(doc, rawKid);
                    var childDict = resolved as PdfDictionary;
                    string childRole = StructTreeWalker.GetRole(childDict, doc);

                    bool isUnwrappedContent;
                    if (childRole != null)
                    {
                        // A structure element: allowed only if it is Lbl or LBody itself.
                        isUnwrappedContent = childRole != "Lbl" && childRole != "LBody";
                    }
                    else if (resolved is PdfNumber)
                    {
                        isUnwrappedContent = true; // a bare MCID directly under LI
                    }
                    else if (childDict != null)
                    {
                        var kind = StructKidClassifier.Classify(childDict, doc);
                        isUnwrappedContent = kind == StructKidKind.MarkedContentReference
                                           || kind == StructKidKind.ObjectReference;
                        // StructKidKind.Unknown: not a kid at all — skip, per that enum's own docs.
                    }
                    else
                    {
                        continue; // unresolvable — cannot tell; skip rather than guess
                    }

                    if (!isUnwrappedContent) continue;

                    found++;
                    v.Add(new PdfUa2StructureViolation("UA2-8.2.5.25",
                        "An LI structure element's content must be enclosed in a Lbl or an LBody " +
                        "structure element, not present directly (ISO 14289-2 §8.2.5.25)." +
                        (node.ObjectNumber > 0 ? $" Object {node.ObjectNumber}." : "")));
                    break; // one finding per LI is enough; move to the next LI
                }
            });

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.25",
                    $"…and more LI structure element(s) with unwrapped content, not listed individually."));
        }

        /// <summary>
        /// ISO 14289-2:2024 §8.2.5.25: "If Lbl structure elements are present, the ListNumbering
        /// attribute (see Table B.5) shall be present on the respective L structure element and
        /// should have the value that most closely approximates the intended labelling scheme; in
        /// such cases the value None shall not be used."
        /// </summary>
        /// <remarks>
        /// <para><b>"The respective L"</b> is the <c>L</c> whose own <c>LI</c> children carry a
        /// <c>Lbl</c> child — a nested list's labels belong to the nested list, not the outer one,
        /// so only direct <c>L → LI → Lbl</c> is read.</para>
        /// <para><b>The value checked is the resolved one, not only an explicit entry.</b>
        /// ISO 32000-2:2020 Table 382 makes <c>ListNumbering</c> inheritable, and §14.8.5.3 is
        /// explicit that "there is no semantic distinction between attributes that are specified
        /// explicitly and ones that are inherited": an <c>L</c> takes its own value (its
        /// <c>/A</c>, then its <c>/C</c> classes), else its nearest ancestor's, else the default,
        /// <c>None</c>. So a nested list inheriting <c>/Decimal</c> from an enclosing element is
        /// not reported, and a list with no value anywhere above it is — the default is exactly
        /// the <c>None</c> the clause forbids. A value that is not a name is malformed, and is
        /// skipped rather than guessed at.</para>
        /// <para><b>Owners, per §14.8.5.5:</b> List attributes "may only be defined in attribute
        /// objects whose O (owner) entry has the value List or whose owner is any other owner
        /// excluding Layout, Table, PrintField and Artifact" — see
        /// <see cref="ReadOwnListAttribute"/>. Only presence and not-<c>None</c> are checked; which
        /// scheme "most closely approximates" the labels is a "should" about content.</para>
        /// </remarks>
        private static void CheckListNumberingWhenLabelled(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var structTreeRoot = ResolveStructTreeRoot(doc, catalog);
            if (structTreeRoot == null) return;

            Func<PdfObject, PdfObject> resolve = o => Resolve(doc, o);
            var classMap = PdfStructureAttributes.BuildClassMap(structTreeRoot, resolve);

            int found = 0;
            StructTreeWalker.Walk(structTreeRoot, doc, index: null, node =>
            {
                if (node.Role != "L" || node.Dict == null || found > IndividualReportLimit) return;
                if (!ListItemsCarryLbl(doc, node.Dict)) return;

                PdfObject resolved = null;
                for (StructNode n = node; n != null && resolved == null; n = n.Parent)
                    if (n.Dict != null)
                        resolved = ReadOwnListAttribute(n.Dict, PdfName.ListNumbering, classMap, resolve);

                if (resolved != null && !(resolved is PdfName)) return; // malformed — cannot determine
                string numbering = (resolved as PdfName)?.Value;
                if (numbering != null && numbering != "None") return;

                found++;
                if (found > IndividualReportLimit) return;
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.25",
                    (numbering == null
                        ? "An L structure element whose list items carry Lbl elements has no ListNumbering " +
                          "attribute, on itself or inherited, so it takes the default None"
                        : "An L structure element whose list items carry Lbl elements has ListNumbering /None") +
                    "; §8.2.5.25 requires ListNumbering to be present, and not None, when labels are " +
                    "present (ISO 14289-2 §8.2.5.25)." +
                    (node.ObjectNumber > 0 ? $" Object {node.ObjectNumber}." : "")));
            });

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.25",
                    $"…and at least one more labelled L structure element(s) without a " +
                    "ListNumbering value, not listed individually."));
        }

        /// <summary>
        /// ISO 14289-2:2024 §8.2.5.25: "If a list continues from a previous list, the ContinuedList
        /// attribute shall be present and have a value of true."
        /// </summary>
        /// <remarks>
        /// <para>Whether a list <i>continues</i> another is a fact about content, except where the
        /// file states it: ISO 32000-2:2020 Table 382 defines <c>ContinuedFrom</c> as "the ID of the
        /// list for which this list is a continuation". An <c>L</c> declaring <c>ContinuedFrom</c>
        /// has said it continues a previous list, so its own <c>ContinuedList</c> must be
        /// <c>true</c>; absent, it defaults to <c>false</c> and contradicts the declaration. Neither
        /// attribute is inheritable, so only the element's own <c>/A</c> and <c>/C</c> are read.</para>
        /// <para>Not checked: the clause's companion sentence that <c>ContinuedFrom</c> "shall be
        /// present" when the previous list is present in the document (that needs deciding which
        /// list is continued), and the <c>Ref</c> sentence (a "should").</para>
        /// </remarks>
        private static void CheckContinuedFromImpliesContinuedList(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var structTreeRoot = ResolveStructTreeRoot(doc, catalog);
            if (structTreeRoot == null) return;

            Func<PdfObject, PdfObject> resolve = o => Resolve(doc, o);
            var classMap = PdfStructureAttributes.BuildClassMap(structTreeRoot, resolve);

            int found = 0;
            StructTreeWalker.Walk(structTreeRoot, doc, index: null, node =>
            {
                if (node.Role != "L" || node.Dict == null || found > IndividualReportLimit) return;
                if (ReadOwnListAttribute(node.Dict, PdfName.ContinuedFrom, classMap, resolve) == null) return;
                if (ReadOwnListAttribute(node.Dict, PdfName.ContinuedList, classMap, resolve) is PdfBoolean b && b.Value)
                    return;

                found++;
                if (found > IndividualReportLimit) return;
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.25",
                    "An L structure element declares ContinuedFrom — that it continues a previous list — " +
                    "but its ContinuedList attribute is not true; §8.2.5.25 requires ContinuedList true " +
                    "for a list that continues a previous one (ISO 14289-2 §8.2.5.25)." +
                    (node.ObjectNumber > 0 ? $" Object {node.ObjectNumber}." : "")));
            });

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.25",
                    $"…and at least one more L structure element(s) declaring ContinuedFrom " +
                    "without ContinuedList true, not listed individually."));
        }

        /// <summary>True when any direct <c>LI</c> child of <paramref name="list"/> has a direct <c>Lbl</c> child.</summary>
        private static bool ListItemsCarryLbl(PdfDocument doc, PdfDictionary list)
        {
            foreach (PdfObject rawItem in StructTreeWalker.GetRawKids(list, doc))
            {
                var item = Resolve(doc, rawItem) as PdfDictionary;
                if (item == null || StructTreeWalker.GetRole(item, doc) != "LI") continue;
                if (GetChildRoles(doc, item).Contains("Lbl")) return true;
            }
            return false;
        }

        /// <summary>
        /// <see cref="PdfStructureAttributes.ReadAll(PdfDictionary, IReadOnlyDictionary{string, PdfObject}, Func{PdfObject, PdfObject})"/>,
        /// read once for every element naming the same attribute objects in one walk — the same resolved
        /// <c>/A</c> and <c>/C</c> — where a shared attribute object was read again for every element:
        /// elements × its entries, two counts the file chooses (twenty thousand elements naming one
        /// twenty-thousand-key object were 4×10^8 entries copied, per check). The memo belongs to the
        /// walk's <paramref name="resolve"/>, a closure over its document that each check makes afresh.
        /// The result is shared, and read only.
        /// </summary>
        private static Dictionary<string, Dictionary<string, PdfObject>> AttributesOf(
            PdfDictionary element, IReadOnlyDictionary<string, PdfObject> classMap, Func<PdfObject, PdfObject> resolve)
            => AttributeReadsOfWalk.GetValue(resolve, r => new AttributeReads(classMap, r)).Of(element);

        /// <summary>How many attribute readings the checks on this thread have made — one per distinct source a walk meets: the memo's liveness, for tests.</summary>
        [ThreadStatic] internal static long Readings;

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Func<PdfObject, PdfObject>, AttributeReads> AttributeReadsOfWalk =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Func<PdfObject, PdfObject>, AttributeReads>();

        /// <summary>
        /// One walk's attribute readings, by the element's resolved <c>/A</c> and <c>/C</c>: an
        /// object by identity (<see cref="PdfReferenceEquality"/>, the one reference comparer), a
        /// class named by a name by its value, since every name object spelling it is the same class.
        /// </summary>
        private sealed class AttributeReads
        {
            private static readonly object NoSource = new object();

            private readonly IReadOnlyDictionary<string, PdfObject> _classMap;
            private readonly Func<PdfObject, PdfObject> _resolve;
            private readonly Dictionary<object, Sources> _byAttributes = new Dictionary<object, Sources>(PdfReferenceEquality.Instance);

            /// <summary>The readings under one <c>/A</c>, by the <c>/C</c> beside it.</summary>
            private sealed class Sources
            {
                internal readonly Dictionary<string, Dictionary<string, Dictionary<string, PdfObject>>> ByClassName =
                    new Dictionary<string, Dictionary<string, Dictionary<string, PdfObject>>>(StringComparer.Ordinal);
                internal readonly Dictionary<object, Dictionary<string, Dictionary<string, PdfObject>>> ByClassObject =
                    new Dictionary<object, Dictionary<string, Dictionary<string, PdfObject>>>(PdfReferenceEquality.Instance);
            }

            internal AttributeReads(IReadOnlyDictionary<string, PdfObject> classMap, Func<PdfObject, PdfObject> resolve)
            {
                _classMap = classMap;
                _resolve = resolve;
            }

            internal Dictionary<string, Dictionary<string, PdfObject>> Of(PdfDictionary element)
            {
                object a = _resolve(element[PdfName.A]) ?? NoSource;
                PdfObject c = _resolve(element[PdfName.C]);
                if (!_byAttributes.TryGetValue(a, out Sources sources))
                    _byAttributes[a] = sources = new Sources();

                Dictionary<string, Dictionary<string, PdfObject>> byOwner;
                if (c is PdfName name)
                {
                    if (!sources.ByClassName.TryGetValue(name.Value, out byOwner))
                        sources.ByClassName[name.Value] = byOwner = Read(element);
                }
                else
                {
                    object key = c ?? NoSource;
                    if (!sources.ByClassObject.TryGetValue(key, out byOwner))
                        sources.ByClassObject[key] = byOwner = Read(element);
                }
                return byOwner;
            }

            private Dictionary<string, Dictionary<string, PdfObject>> Read(PdfDictionary element)
            {
                Readings++;
                return PdfStructureAttributes.ReadAll(element, _classMap, _resolve);
            }
        }

        /// <summary>
        /// One List attribute declared on <paramref name="element"/> itself — its <c>/A</c>, then
        /// its <c>/C</c> classes — under an owner ISO 32000-2:2020 §14.8.5.5 lets carry one:
        /// <c>List</c> first, then any other owner except <c>Layout</c>, <c>Table</c>,
        /// <c>PrintField</c> and <c>Artifact</c> (an attribute object with no <c>/O</c> included).
        /// Null when none declares <paramref name="key"/>.
        /// </summary>
        private static PdfObject ReadOwnListAttribute(
            PdfDictionary element, PdfName key,
            IReadOnlyDictionary<string, PdfObject> classMap, Func<PdfObject, PdfObject> resolve)
        {
            var byOwner = AttributesOf(element, classMap, resolve);
            if (byOwner.TryGetValue("List", out var list) && list.TryGetValue(key.Value, out PdfObject listValue))
                return listValue;

            foreach (var owner in byOwner)
            {
                if (owner.Key == "Layout" || owner.Key == "Table" || owner.Key == "PrintField" || owner.Key == "Artifact")
                    continue;
                if (owner.Value.TryGetValue(key.Value, out PdfObject value)) return value;
            }
            return null;
        }

        // ----------------------------------------------------------------
        //  Clause 8.2.5.26 — Table (Table, TR, TH, TD, THead, TBody, TFoot)
        // ----------------------------------------------------------------

        /// <summary>ISO 14289-2:2024 §8.2.5.26: "Tables shall be regular." NOTE 1: "Tables are
        /// regular when the number of logical cells is equal in each row after accounting for
        /// RowSpan and ColSpan attributes."</summary>
        /// <remarks>
        /// <para>A new, nineteenth shape: every earlier check reads structure roles and a handful
        /// of scalar attributes; this one runs a small table-anchoring model — the standard
        /// algorithm for turning a sequence of rows, each with <c>/RowSpan</c>/<c>/ColSpan</c>
        /// cells, into a logical column count per row (the same idea HTML's own table layout
        /// algorithm uses, simplified to a regularity check rather than a renderer: no attempt is
        /// made to detect or repair overlapping cells, only to sum each row's occupied width).
        /// A cell's <c>/RowSpan</c> contributes its <c>/ColSpan</c> width to that many
        /// <i>additional</i> rows below it; a table is regular exactly when every row's total
        /// width — its own cells' <c>/ColSpan</c> plus whatever earlier rows' spans still reach
        /// it — is the same number.</para>
        /// <para><b>Row groups are read together, not separately.</b> §8.2.5.26's second sentence
        /// ("Row groupings formed by THead, TBody and TFoot … shall be regular") independently
        /// requires each group to be internally regular and requires no cell to span a group
        /// boundary — checking the whole table's row sequence as one unit, as this does, covers
        /// every case that sentence covers plus more (two internally-regular groups of different
        /// widths would fail this check but pass a strict per-group reading) at the cost of never
        /// under-reporting an irregularity the standard actually flags. §8.2.5.26's further
        /// sentences — the header-cell semantic-sufficiency requirement, and the conditional
        /// <c>Scope</c>/<c>Headers</c> attribute rules — all turn on judging whether the header
        /// relationship is <i>adequately</i> conveyed, a semantic call this checker makes nowhere
        /// else, and are not attempted.</para>
        /// <para><see cref="CollectTableRows"/> stops at a nested <c>Table</c> role rather than
        /// attributing an inner table's rows to the outer one, and is depth-capped (not routed
        /// through <see cref="StructTreeWalker.Walk"/>'s own node-visit budget, since it is a
        /// small manual descent through only <c>THead</c>/<c>TBody</c>/<c>TFoot</c>) so a cyclic
        /// or pathologically deep row-group nesting terminates rather than recursing unbounded.
        /// </para>
        /// <para><b>Spans are attributes, read through <see cref="TableAttributes.ReadSpan"/></b> —
        /// from the cell's <c>/A</c> or its <c>/C</c> classes, owner <c>Table</c>, clamped exactly
        /// as PDF/UA-1's UA12 clamps them. This check first read <c>/RowSpan</c>/<c>/ColSpan</c> as
        /// entries on the structure element dictionary, where ISO 32000-2 Table 355 defines no
        /// such key, and so reported every conforming merged cell's table as irregular.</para>
        /// </remarks>
        private static void CheckTableRegularity(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var structTreeRoot = ResolveStructTreeRoot(doc, catalog);
            if (structTreeRoot == null) return;

            Func<PdfObject, PdfObject> resolve = o => Resolve(doc, o);
            var classMap = PdfStructureAttributes.BuildClassMap(structTreeRoot, resolve);

            int found = 0;
            StructTreeWalker.Walk(structTreeRoot, doc, index: null, node =>
            {
                if (node.Role != "Table" || node.Dict == null || found > IndividualReportLimit) return;

                var rows = new List<PdfDictionary>();
                CollectTableRows(doc, node.Dict, rows, depth: 0);
                if (rows.Count < 2) return; // nothing to compare

                int? expectedWidth = null;
                bool irregular = false;
                var activeSpans = new List<(int width, int rowsRemaining)>();

                foreach (PdfDictionary row in rows)
                {
                    int width = 0;
                    var stillActive = new List<(int width, int rowsRemaining)>();
                    foreach (var span in activeSpans)
                    {
                        width += span.width;
                        if (span.rowsRemaining - 1 > 0) stillActive.Add((span.width, span.rowsRemaining - 1));
                    }
                    activeSpans = stillActive;

                    foreach (PdfObject rawKid in StructTreeWalker.GetRawKids(row, doc))
                    {
                        var cell = Resolve(doc, rawKid) as PdfDictionary;
                        if (cell == null) continue;
                        string cellRole = StructTreeWalker.GetRole(cell, doc);
                        if (cellRole != "TH" && cellRole != "TD") continue;

                        int colSpan = TableAttributes.ReadSpan(cell, PdfName.ColSpan, classMap, resolve);
                        int rowSpan = TableAttributes.ReadSpan(cell, PdfName.RowSpan, classMap, resolve);
                        width += colSpan;
                        if (rowSpan > 1) activeSpans.Add((colSpan, rowSpan - 1));
                    }

                    if (expectedWidth == null) expectedWidth = width;
                    else if (width != expectedWidth) irregular = true;
                }

                if (!irregular) return;

                found++;
                if (found > IndividualReportLimit) return;
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.26",
                    "A Table structure element's rows do not have an equal number of logical " +
                    "cells after accounting for RowSpan and ColSpan; §8.2.5.26 requires tables to " +
                    "be regular (ISO 14289-2 §8.2.5.26)." +
                    (node.ObjectNumber > 0 ? $" Object {node.ObjectNumber}." : "")));
            });

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.26",
                    $"…and at least one more irregular Table structure element(s), not listed individually."));
        }

        /// <summary>Collects every <c>TR</c> row belonging to <paramref name="node"/> (a
        /// <c>Table</c>, or one of its <c>THead</c>/<c>TBody</c>/<c>TFoot</c> row groups), in
        /// document order. Does not descend into a nested <c>Table</c>. Depth-capped rather than
        /// routed through the shared node-visit budget, since a <c>Table → group → TR</c> nesting
        /// is at most two levels deep in a well-formed file.</summary>
        private static void CollectTableRows(
            PdfDocument doc, PdfDictionary node, List<PdfDictionary> rows, int depth)
        {
            if (depth > 4) return;

            foreach (PdfObject rawKid in StructTreeWalker.GetRawKids(node, doc))
            {
                var kid = Resolve(doc, rawKid) as PdfDictionary;
                if (kid == null) continue;
                string role = StructTreeWalker.GetRole(kid, doc);

                if (role == "TR") rows.Add(kid);
                else if (role == "THead" || role == "TBody" || role == "TFoot")
                    CollectTableRows(doc, kid, rows, depth + 1);
                // any other role (including a nested Table) is not descended into.
            }
        }

        // ----------------------------------------------------------------
        //  Clause 8.2.5.29.1 — Formula: math must be a child of Formula
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.2.5.29.1: "The math structure type, as defined in
        /// ISO 32000-2:2020, 14.8.6.3, shall occur only as a child of a Formula structure
        /// element."
        /// </summary>
        /// <remarks>
        /// A sixth check shape: rather than a container's own required children (<c>LI</c>,
        /// <c>Ruby</c>, <c>Warichu</c>) or a role's outright whole-tree prohibition (<c>H</c>,
        /// <c>Note</c>), this walks the whole tree for one role (<c>math</c>, the MathML
        /// namespace's root structure type) and checks its immediate *parent's* role rather than
        /// its own presence.
        /// <para>Only the parent-role requirement is checked. §8.2.5.29.1's alternative —
        /// representing the expression as presentation MathML via the Associated Files mechanism
        /// instead of structure elements, with a required <c>AFRelationship /Supplement</c> —
        /// and §8.2.5.29.2's <c>Alt</c>/<c>ActualText</c> requirement for non-mathematical
        /// scientific formulae are not attempted here: both need judging *which* Formula elements
        /// are mathematical versus non-mathematical, a semantic distinction this checker has no
        /// way to make (the same reasoning already given for Ruby's four-element form and for
        /// every "shall be used for content that is X" clause left unchecked elsewhere in this
        /// file).</para>
        /// </remarks>
        private static void CheckMathIsChildOfFormula(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var structTreeRoot = ResolveStructTreeRoot(doc, catalog);
            if (structTreeRoot == null) return;

            int found = 0;
            StructTreeWalker.Walk(structTreeRoot, doc, index: null, node =>
            {
                if (node.Role != "math") return;
                if (node.Parent != null && node.Parent.Role == "Formula") return;

                found++;
                if (found > IndividualReportLimit) return;
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.29.1",
                    "A math structure element must occur only as a child of a Formula structure " +
                    "element (ISO 14289-2 §8.2.5.29.1)." +
                    (node.ObjectNumber > 0 ? $" Object {node.ObjectNumber}." : "")));
            });

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.29.1",
                    $"…and {found - IndividualReportLimit} more math structure element(s) with the " +
                    "wrong parent, not listed individually."));
        }

        // ----------------------------------------------------------------
        //  Clause 8.2.5.21 (via §8.9.2.2) — Annot: hidden annotations must be artifacts
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.2.5.21 defers to §8.9 for Annot requirements. §8.9.2.2 "Annotations
        /// as artifacts": annotations meeting either of the following criteria "shall be
        /// artifacts": the Invisible flag is set; or the NoView flag is set and the ToggleNoView
        /// flag is not set. "Annotations that are artifacts shall either not be present in the
        /// structure tree or shall be enclosed within Artifact structure elements."
        /// </summary>
        /// <remarks>
        /// <para>A seventh check shape: unlike every earlier check, this one starts from the
        /// document's <i>annotations</i> (walking every page's <c>/Annots</c> and each entry's
        /// <c>/F</c> flags via <see cref="PdfAnnotationFlags"/>) rather than from the structure
        /// tree, then cross-references the object numbers that must be artifacts against every
        /// <c>/OBJR</c> found anywhere in the tree.</para>
        /// <para>An annotation stored as a direct (inline) dictionary in <c>/Annots</c> has no
        /// object number, so no <c>/OBJR</c> could name it either — skipped as unprovable, the same
        /// reasoning <c>PdfDeepAccessibilityChecker</c>'s UA23 already applies to the identical
        /// situation.</para>
        /// <para><b>"Not present in the structure tree" is the compliant half, not a gap in this
        /// check.</b> Only an annotation this check can actually find an <c>/OBJR</c> for is judged
        /// — an annotation absent from the tree entirely satisfies the clause by its first clause
        /// ("shall either not be present … or shall be enclosed"), so no violation exists to
        /// report for it.</para>
        /// <para>"Enclosed within" is read as anywhere in the ancestor chain, not only the
        /// immediate parent — checked by walking <see cref="StructNode.Parent"/> from the
        /// <c>/OBJR</c>'s own containing element up to the root, the same upward walk
        /// <c>CheckMathIsChildOfFormula</c> takes one step of.</para>
        /// </remarks>
        private static void CheckHiddenAnnotationsAreArtifacts(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var mustBeArtifact = CollectAnnotationObjectNumbers(doc, IsRequiredArtifactByFlags);
            CheckAnnotationsMustBeArtifacts(doc, catalog, v, "UA2-8.9.2.2",
                "An annotation flagged Invisible, or NoView without ToggleNoView, is present in the " +
                "structure tree but not enclosed within an Artifact structure element " +
                "(ISO 14289-2 §8.9.2.2).", mustBeArtifact);
        }

        /// <summary>
        /// ISO 14289-2:2024 §8.9.2.4.14: "A printer's mark annotation shall be an artifact."
        /// </summary>
        /// <remarks>
        /// The same "shall be an artifact" shape §8.9.2.2 already uses — either absent from the
        /// structure tree entirely, or enclosed within an <c>Artifact</c> structure element — but
        /// unconditional on the subtype rather than gated on a flag combination, so this simply
        /// supplies a different <see cref="CollectAnnotationObjectNumbers"/> predicate to the same
        /// <see cref="CheckAnnotationsMustBeArtifacts"/> helper §8.9.2.2 was refactored to share.
        /// </remarks>
        private static void CheckPrinterMarkIsArtifact(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var mustBeArtifact = CollectAnnotationObjectNumbers(doc, (d, dict) =>
                (Resolve(d, dict[PdfName.Subtype]) as PdfName)?.Value == PdfAnnotationSubtypes.PrinterMark);
            CheckAnnotationsMustBeArtifacts(doc, catalog, v, "UA2-8.9.2.4.14",
                "A printer's mark annotation is present in the structure tree but not enclosed " +
                "within an Artifact structure element; a printer's mark annotation shall be an " +
                "artifact (ISO 14289-2 §8.9.2.4.14).", mustBeArtifact);
        }

        /// <summary>
        /// Shared "shall be an artifact" cross-reference: reports every <paramref name="mustBeArtifact"/>
        /// annotation object number that is referenced by an <c>/OBJR</c> anywhere in the tree
        /// without that <c>/OBJR</c>'s containing element (or any ancestor) having role
        /// <c>Artifact</c>. An annotation absent from the tree entirely already satisfies "shall be
        /// an artifact" by its own first clause, so nothing is reported for it — not a gap in this
        /// check, the compliant half of the rule.
        /// </summary>
        private static void CheckAnnotationsMustBeArtifacts(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v,
            string code, string message, HashSet<int> mustBeArtifact)
        {
            var structTreeRoot = ResolveStructTreeRoot(doc, catalog);
            if (structTreeRoot == null) return;
            if (mustBeArtifact.Count == 0) return;

            int found = 0;
            StructTreeWalker.Walk(structTreeRoot, doc, index: null, node =>
            {
                if (node.Dict == null || found > IndividualReportLimit) return;

                foreach (PdfObject rawKid in StructTreeWalker.GetRawKids(node.Dict, doc))
                {
                    var objrDict = Resolve(doc, rawKid) as PdfDictionary;
                    if (objrDict == null) continue;
                    if (StructKidClassifier.Classify(objrDict, doc) != StructKidKind.ObjectReference) continue;
                    if (!(objrDict[PdfName.Obj] is PdfReference objRef)) continue;
                    if (!mustBeArtifact.Contains(objRef.ReferenceObjectNumber)) continue;
                    if (IsEnclosedInArtifact(node)) continue;

                    found++;
                    if (found > IndividualReportLimit) break;
                    v.Add(new PdfUa2StructureViolation(code,
                        message + (node.ObjectNumber > 0 ? $" Referencing object {node.ObjectNumber}." : "")));
                }
            });

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation(code,
                    $"…and at least one more, not listed individually."));
        }

        private static bool IsEnclosedInArtifact(StructNode node)
        {
            for (StructNode n = node; n != null; n = n.Parent)
                if (n.Role == "Artifact") return true;
            return false;
        }

        /// <summary>
        /// Object numbers of every page annotation, stored as an indirect reference (so an
        /// <c>/OBJR</c> could plausibly name it), for which <paramref name="predicate"/> answers
        /// true. Shared by every check that needs to correlate page annotations against structure
        /// tree <c>/OBJR</c> references, so the "direct dictionary → no object number → skip"
        /// judgment call is made once rather than re-derived per check.
        /// </summary>
        private static HashSet<int> CollectAnnotationObjectNumbers(
            PdfDocument doc, Func<PdfDocument, PdfDictionary, bool> predicate)
        {
            var result = new HashSet<int>();
            foreach (PageAnnotation annot in AnnotationsOf(doc))
            {
                if (annot.ObjectNumber <= 0) continue; // no object number
                if (predicate(doc, annot.Dict)) result.Add(annot.ObjectNumber);
            }
            return result;
        }

        /// <summary>An annotation the pages name: its object number (-1 when stored as a direct
        /// dictionary) and its dictionary.</summary>
        private readonly struct PageAnnotation
        {
            internal PageAnnotation(int objectNumber, PdfDictionary dict) { ObjectNumber = objectNumber; Dict = dict; }
            internal int ObjectNumber { get; }
            internal PdfDictionary Dict { get; }
        }

        [ThreadStatic] private static PdfDocument _annotationsOf;
        [ThreadStatic] private static List<PageAnnotation> _annotations;
        [ThreadStatic] private static long _annotationEntriesRead;

        /// <summary>The <c>/Annots</c> entries the last validation on this thread read — the
        /// evidence that an array several pages share is read once.</summary>
        internal static long LastAnnotationEntriesRead => _annotationEntriesRead;

        /// <summary>
        /// Every annotation the document's pages name, each once, read once per validation for
        /// every check that walks them — about fourteen do. Pages may name one <c>/Annots</c>
        /// array, so what they name between them is pages × entries: 800 pages over one array of
        /// 800 links took the validation 6.9 s against 0.5 s for 200. An array named by reference
        /// is read at its first page only, and an annotation named twice is listed once — the
        /// checks report about the document, not per page, so neither changes a judgment; it
        /// changes a count, which is now of annotations rather than of the places that name them.
        /// </summary>
        private static List<PageAnnotation> AnnotationsOf(PdfDocument doc)
        {
            if (_annotations != null && ReferenceEquals(_annotationsOf, doc)) return _annotations;

            var list = new List<PageAnnotation>();
            if (doc.Pages != null)
            {
                var arraysRead = new HashSet<int>();
                var annotationsListed = new HashSet<int>();
                foreach (PdfPage page in doc.Pages)
                {
                    if (page.Dictionary[PdfName.Annots] is PdfReference arrayRef
                        && !arraysRead.Add(arrayRef.ReferenceObjectNumber)) continue;
                    PdfArray annots = page.GetAnnotations();
                    if (annots == null) continue;

                    for (int i = 0; i < annots.Count; i++)
                    {
                        _annotationEntriesRead++;
                        int objNum = annots[i] is PdfReference r ? r.ReferenceObjectNumber : -1;
                        if (objNum > 0 && !annotationsListed.Add(objNum)) continue;
                        if (Resolve(doc, annots[i]) is PdfDictionary dict) list.Add(new PageAnnotation(objNum, dict));
                    }
                }
            }
            _annotationsOf = doc;
            _annotations = list;
            return list;
        }

        // ----------------------------------------------------------------
        //  Clause 8.9.2.3 — Markup annotations must be enclosed within Annot
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.9.2.3: "As annotation semantics are defined by annotation type,
        /// markup annotations shall be enclosed within Annot structure elements."
        /// </summary>
        /// <remarks>
        /// <para>Every §8.9.2.4.x subclause for a markup-annotation subtype (Table 171) reads
        /// "[subtype] annotations shall follow the requirements in 8.9.2.3" — so this one check,
        /// under one code, covers Text, FreeText, Line, Square, Circle, Polygon, PolyLine, the
        /// four text-markup subtypes (Highlight/Underline/Squiggly/StrikeOut), Caret, Stamp, Ink,
        /// FileAttachment, Redact and Projection. <b>Watermark is deliberately excluded</b>: its
        /// own subclause (§8.9.2.4.16) makes the 8.9.2.3 requirement conditional — "When used as
        /// real content, Watermark annotations shall follow the requirements of 8.9.2.3" — and
        /// whether a given watermark is real content or decoration is exactly the semantic
        /// judgment this checker leaves unattempted everywhere else. Only §8.9.2.3's enclosure
        /// sentence is checked; its RC/Contents textual-equivalence rule and its "Contents
        /// required when context is insufficient" rule both need comparing text for adequacy —
        /// left out for the same reason every other semantic half of every other clause in this
        /// file is.</para>
        /// <para><b>Gated on the same exemption §8.9.2.2 grants, not unconditional.</b> An
        /// annotation flagged Invisible, or NoView without ToggleNoView, is required by §8.9.2.2
        /// to be an artifact — the opposite of "enclosed within an Annot structure element" this
        /// clause asks for on the very same annotation. Checking both rules unconditionally would
        /// make one of them wrong on every such annotation, so this check reuses
        /// <see cref="CollectAnnotationObjectNumbers"/> with the same flag predicate
        /// <see cref="CheckHiddenAnnotationsAreArtifacts"/> already isolated, inverted.</para>
        /// <para><b>Unlike <see cref="CheckAnnotationsMustBeArtifacts"/>, absence from the tree is
        /// itself the violation here, not the compliant case</b> — §8.9.2.2's rule has a "shall
        /// either not be present … or shall be enclosed" escape hatch; §8.9.2.3 has none, so a
        /// markup annotation never referenced by any <c>/OBJR</c> at all is exactly as
        /// non-conforming as one enclosed in the wrong role. The walk therefore runs the opposite
        /// way from every prior <c>/OBJR</c> cross-reference in this file: it collects which
        /// target object numbers <i>were</i> found properly enclosed, then reports whatever is
        /// left over after the walk finishes, rather than reporting at the point of a match.</para>
        /// </remarks>
        private static void CheckMarkupAnnotationsEnclosedInAnnot(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var mustBeEnclosed = CollectAnnotationObjectNumbers(doc, (d, dict) =>
            {
                string subtype = (Resolve(d, dict[PdfName.Subtype]) as PdfName)?.Value;
                if (!IsMarkupAnnotationSubtypeRequiringAnnot(subtype)) return false;
                return !IsRequiredArtifactByFlags(d, dict); // exempt: §8.9.2.2 requires an artifact instead
            });

            CheckAnnotationsMustBeEnclosedInRole(doc, catalog, v, "UA2-8.9.2.3",
                objNum => $"Markup annotation object {objNum} is not enclosed within an Annot " +
                    "structure element anywhere in the structure tree; §8.9.2.3 requires it, " +
                    "unless the annotation is instead required to be an artifact by §8.9.2.2 " +
                    "(ISO 14289-2 §8.9.2.3).",
                mustBeEnclosed, role => role == "Annot");
        }

        private static bool IsMarkupAnnotationSubtypeRequiringAnnot(string subtype) => subtype switch
        {
            PdfAnnotationSubtypes.Text => true,
            PdfAnnotationSubtypes.FreeText => true,
            PdfAnnotationSubtypes.Line => true,
            PdfAnnotationSubtypes.Square => true,
            PdfAnnotationSubtypes.Circle => true,
            PdfAnnotationSubtypes.Polygon => true,
            PdfAnnotationSubtypes.PolyLine => true,
            PdfAnnotationSubtypes.Highlight => true,
            PdfAnnotationSubtypes.Underline => true,
            PdfAnnotationSubtypes.Squiggly => true,
            PdfAnnotationSubtypes.StrikeOut => true,
            PdfAnnotationSubtypes.Caret => true,
            PdfAnnotationSubtypes.Stamp => true,
            PdfAnnotationSubtypes.Ink => true,
            PdfAnnotationSubtypes.FileAttachment => true,
            PdfAnnotationSubtypes.Redact => true,
            PdfAnnotationSubtypes.Projection => true,
            _ => false,
        };

        /// <summary>True when §8.9.2.2 requires this annotation to be an artifact instead (the
        /// same Invisible / NoView-without-ToggleNoView test <see cref="CheckHiddenAnnotationsAreArtifacts"/>
        /// applies), shared by every "must instead be enclosed in some non-Artifact role" check
        /// so the two rules never disagree about the same annotation.</summary>
        internal static bool IsRequiredArtifactByFlags(PdfDocument doc, PdfDictionary annotation)
        {
            int flags = PdfAnnotationFlags.FromNumber(Resolve(doc, annotation[PdfName.F])) ?? 0;
            bool invisible = (flags & PdfAnnotationFlags.Invisible) != 0;
            bool noViewOnly = (flags & PdfAnnotationFlags.NoView) != 0
                            && (flags & PdfAnnotationFlags.ToggleNoView) == 0;
            return invisible || noViewOnly;
        }

        /// <summary>
        /// Shared "must be enclosed in one of these roles somewhere in the tree" cross-reference —
        /// the sibling of <see cref="CheckAnnotationsMustBeArtifacts"/> for clauses with no "or
        /// simply absent" escape hatch. Unlike that helper, absence from the tree is itself a
        /// violation here, so the walk collects which <paramref name="mustBeEnclosed"/> object
        /// numbers <i>were</i> found under a matching role, then reports whatever is left over
        /// once the walk finishes, rather than reporting at the point of a match.
        /// </summary>
        private static void CheckAnnotationsMustBeEnclosedInRole(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v,
            string code, Func<int, string> messageFor, HashSet<int> mustBeEnclosed,
            Func<string, bool> roleMatches)
        {
            var structTreeRoot = ResolveStructTreeRoot(doc, catalog);
            if (structTreeRoot == null) return;
            if (mustBeEnclosed.Count == 0) return;

            var enclosed = new HashSet<int>();
            StructTreeWalker.Walk(structTreeRoot, doc, index: null, node =>
            {
                if (node.Dict == null) return;

                foreach (PdfObject rawKid in StructTreeWalker.GetRawKids(node.Dict, doc))
                {
                    var objrDict = Resolve(doc, rawKid) as PdfDictionary;
                    if (objrDict == null) continue;
                    if (StructKidClassifier.Classify(objrDict, doc) != StructKidKind.ObjectReference) continue;
                    if (!(objrDict[PdfName.Obj] is PdfReference objRef)) continue;
                    if (!mustBeEnclosed.Contains(objRef.ReferenceObjectNumber)) continue;
                    if (IsEnclosedInMatchingRole(node, roleMatches)) enclosed.Add(objRef.ReferenceObjectNumber);
                }
            });

            int found = 0;
            foreach (int objNum in mustBeEnclosed)
            {
                if (enclosed.Contains(objNum)) continue;
                found++;
                if (found > IndividualReportLimit) continue;
                v.Add(new PdfUa2StructureViolation(code, messageFor(objNum)));
            }

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation(code,
                    $"…and {found - IndividualReportLimit} more, not listed individually."));
        }

        private static bool IsEnclosedInMatchingRole(StructNode node, Func<string, bool> roleMatches)
        {
            for (StructNode n = node; n != null; n = n.Parent)
                if (roleMatches(n.Role)) return true;
            return false;
        }

        // ----------------------------------------------------------------
        //  Clause 8.2.5.20 — Link annotations must be enclosed in Link or Reference
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.2.5.20: "A link annotation (see 8.9.2.4.2) and its associated
        /// content shall be enclosed in either a Link or Reference structure element."
        /// </summary>
        /// <remarks>
        /// The same shape as §8.9.2.3 (an unconditional "must be enclosed" rule, exempt when
        /// §8.9.2.2 requires an artifact instead), now sharing
        /// <see cref="CheckAnnotationsMustBeEnclosedInRole"/> rather than becoming its second
        /// independently-drifted copy — only the target subtype (<c>/Link</c> alone; a link
        /// <i>annotation</i> is unambiguous, unlike the sixteen markup subtypes §8.9.2.3 covers)
        /// and the accepted role set (<c>Link</c> or <c>Reference</c>, either satisfies the
        /// clause — which one is a "should"-level choice between external and intra-document
        /// targets, not checked) differ.
        /// <para>Only the enclosure sentence is checked. §8.2.5.20's further sentences — that
        /// links targeting different locations must be in separate structure elements, and that
        /// links targeting the <i>same</i> location and "semantically perceived to be a single
        /// link" must share one — both turn on judging what a link's target or a reader's
        /// perception is, left unattempted for the same reason every other semantic half of
        /// every other clause in this file is.</para>
        /// </remarks>
        private static void CheckLinkAnnotationEnclosedInLinkOrReference(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var mustBeEnclosed = CollectAnnotationObjectNumbers(doc, (d, dict) =>
            {
                if ((Resolve(d, dict[PdfName.Subtype]) as PdfName)?.Value != PdfAnnotationSubtypes.Link)
                    return false;
                return !IsRequiredArtifactByFlags(d, dict);
            });

            CheckAnnotationsMustBeEnclosedInRole(doc, catalog, v, "UA2-8.2.5.20",
                objNum => $"Link annotation object {objNum} is not enclosed within a Link or " +
                    "Reference structure element anywhere in the structure tree; §8.2.5.20 " +
                    "requires it, unless the annotation is instead required to be an artifact by " +
                    "§8.9.2.2 (ISO 14289-2 §8.2.5.20).",
                mustBeEnclosed, role => role == "Link" || role == "Reference");
        }

        // ----------------------------------------------------------------
        //  Clause 8.10.1 — Forms: no XFA
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.10.1: "XFA forms (see ISO 32000-2:2020, Annex K) shall not be
        /// present."
        /// </summary>
        /// <remarks>
        /// The simplest possible instance of the page/catalog-dictionary shape: presence of the
        /// catalog's <c>/AcroForm /XFA</c> entry alone is the violation, with nothing further to
        /// resolve or classify.
        /// </remarks>
        private static void CheckNoXfaForms(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var acroForm = Resolve(doc, catalog[PdfName.AcroForm]) as PdfDictionary;
            if (acroForm == null) return;
            if (acroForm[PdfName.XFA] == null) return;

            v.Add(new PdfUa2StructureViolation("UA2-8.10.1",
                "The document catalog's /AcroForm declares an /XFA entry; XFA forms shall not be " +
                "present (ISO 14289-2 §8.10.1)."));
        }

        // ----------------------------------------------------------------
        //  Clause 8.10.1 — Forms: a Form element encloses at most one widget
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.10.1: "A Form structure element shall enclose at most one widget
        /// annotation."
        /// </summary>
        /// <remarks>
        /// Collects every Widget-subtype annotation's object number
        /// (<see cref="CollectAnnotationObjectNumbers"/>), then walks the whole tree once; for
        /// every <c>/OBJR</c> found that names one of those object numbers, finds its nearest
        /// <c>Form</c>-role ancestor (<see cref="NearestFormAncestor"/> — the same upward walk
        /// <see cref="IsEnclosedInArtifact"/> takes, stopping at the first match instead of
        /// answering a bool) and tallies one count per distinct <c>Form</c> node. A widget with no
        /// <c>Form</c> ancestor at all is not this clause's concern — that is §8.10.1's separate
        /// "each widget shall be enclosed by a Form" sentence, not attempted here because judging
        /// its "unless … an artifact" exception needs the same semantic call §8.9.2.2 already
        /// makes for a *different* purpose, and reusing it here would silently narrow this clause
        /// to only the cases §8.9.2.2 already flags.
        /// </remarks>
        private static void CheckFormEnclosesAtMostOneWidget(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var structTreeRoot = ResolveStructTreeRoot(doc, catalog);
            if (structTreeRoot == null) return;

            var widgetAnnotations = CollectAnnotationObjectNumbers(doc, (d, dict) =>
                (Resolve(d, dict[PdfName.Subtype]) as PdfName)?.Value == PdfAnnotationSubtypes.Widget);
            if (widgetAnnotations.Count == 0) return;

            var countPerForm = new Dictionary<StructNode, int>();
            StructTreeWalker.Walk(structTreeRoot, doc, index: null, node =>
            {
                if (node.Dict == null) return;

                foreach (PdfObject rawKid in StructTreeWalker.GetRawKids(node.Dict, doc))
                {
                    var objrDict = Resolve(doc, rawKid) as PdfDictionary;
                    if (objrDict == null) continue;
                    if (StructKidClassifier.Classify(objrDict, doc) != StructKidKind.ObjectReference) continue;
                    if (!(objrDict[PdfName.Obj] is PdfReference objRef)) continue;
                    if (!widgetAnnotations.Contains(objRef.ReferenceObjectNumber)) continue;

                    StructNode form = NearestFormAncestor(node);
                    if (form == null) continue;

                    countPerForm.TryGetValue(form, out int count);
                    countPerForm[form] = count + 1;
                }
            });

            int found = 0;
            foreach (KeyValuePair<StructNode, int> kv in countPerForm)
            {
                if (kv.Value <= 1) continue;
                found++;
                if (found > IndividualReportLimit) continue;
                v.Add(new PdfUa2StructureViolation("UA2-8.10.1",
                    $"A Form structure element encloses {kv.Value} widget annotations; a Form " +
                    "structure element must enclose at most one (ISO 14289-2 §8.10.1)." +
                    (kv.Key.ObjectNumber > 0 ? $" Object {kv.Key.ObjectNumber}." : "")));
            }

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.10.1",
                    $"…and {found - IndividualReportLimit} more Form structure element(s) enclosing " +
                    "more than one widget annotation, not listed individually."));
        }

        private static StructNode NearestFormAncestor(StructNode node)
        {
            for (StructNode n = node; n != null; n = n.Parent)
                if (n.Role == "Form") return n;
            return null;
        }

        // ----------------------------------------------------------------
        //  Clause 8.9.3.3 — Tab order
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.9.3.3: "Every page that includes an annotation shall contain a
        /// Tabs entry in its page dictionary in accordance with ISO 32000-2:2020, Table 31, and
        /// its value shall be A, W or S."
        /// </summary>
        /// <remarks>
        /// An eighth check shape: a page-dictionary check with no structure-tree walk at all —
        /// unlike every earlier check in this file, which either starts from the tree or (for
        /// §8.9.2.2) cross-references it. "Includes an annotation" is read literally: any
        /// <c>/Annots</c> entry with at least one item, with no subtype exclusion — the clause
        /// itself names none, unlike §8.9.2.2's Invisible/NoView carve-out for a different rule.
        /// </remarks>
        private static void CheckPageTabOrder(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            if (doc.Pages == null) return;

            int found = 0;
            for (int i = 0; i < doc.Pages.Count; i++)
            {
                if (found > IndividualReportLimit) break;

                PdfPage page = doc.Pages[i];
                PdfArray annots = page.GetAnnotations();
                if (annots == null || annots.Count == 0) continue;

                var tabs = Resolve(doc, page.Dictionary[PdfName.Tabs]) as PdfName;
                if (tabs != null && (tabs.Value == "A" || tabs.Value == "W" || tabs.Value == "S"))
                    continue;

                found++;
                if (found > IndividualReportLimit) break;
                v.Add(new PdfUa2StructureViolation("UA2-8.9.3.3",
                    $"Page {i + 1} includes an annotation but has no /Tabs entry with value /A, " +
                    "/W, or /S (ISO 14289-2 §8.9.3.3)."));
            }

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.9.3.3",
                    $"…and at least one more page(s) with an annotation but no " +
                    "valid /Tabs entry, not listed individually."));
        }

        // ----------------------------------------------------------------
        //  Clause 8.9.4.2 — Alt properties: Alt must match Contents
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.9.4.2: "Where an annotation has a Contents entry providing an
        /// alternative description and the directly enclosing structure element has an Alt entry,
        /// the values of Alt and Contents shall be identical."
        /// </summary>
        /// <remarks>
        /// Reuses the <c>/OBJR</c> cross-reference shape (like §8.9.2.2 and §8.10.1's widget
        /// count), but the correlation runs the opposite direction from both: this check starts
        /// from the structure tree, testing every element that carries an <c>/Alt</c> against the
        /// annotation(s) its own direct <c>/K</c> names, rather than starting from annotations and
        /// searching the tree for them. "Directly enclosing" is read literally as the immediate
        /// parent named by the <c>/OBJR</c>'s own position — <c>node</c> itself, not an ancestor —
        /// unlike <see cref="IsEnclosedInArtifact"/> and <see cref="NearestFormAncestor"/>, which
        /// both walk upward because their clauses say "enclosed within", not "directly enclosing".
        /// <para>Only fires when <b>both</b> entries are present and hold actual string values;
        /// an element with <c>/Alt</c> but no annotation carrying <c>/Contents</c> underneath it,
        /// or a raw kid that does not resolve to an annotation at all, is not this clause's
        /// concern — nothing to compare, nothing to flag.</para>
        /// </remarks>
        private static void CheckAnnotationAltMatchesContents(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var structTreeRoot = ResolveStructTreeRoot(doc, catalog);
            if (structTreeRoot == null) return;

            int found = 0;
            StructTreeWalker.Walk(structTreeRoot, doc, index: null, node =>
            {
                if (node.Dict == null || found > IndividualReportLimit) return;

                var alt = Resolve(doc, node.Dict[PdfName.Alt]) as PdfString;
                if (alt == null) return;

                foreach (PdfObject rawKid in StructTreeWalker.GetRawKids(node.Dict, doc))
                {
                    var objrDict = Resolve(doc, rawKid) as PdfDictionary;
                    if (objrDict == null) continue;
                    if (StructKidClassifier.Classify(objrDict, doc) != StructKidKind.ObjectReference) continue;
                    if (!(objrDict[PdfName.Obj] is PdfReference objRef)) continue;

                    var annotDict = Resolve(doc, objRef) as PdfDictionary;
                    if (annotDict == null) continue;

                    var contents = Resolve(doc, annotDict[PdfName.Contents]) as PdfString;
                    if (contents == null) continue;
                    if (contents.GetText() == alt.GetText()) continue;

                    found++;
                    if (found > IndividualReportLimit) break;
                    v.Add(new PdfUa2StructureViolation("UA2-8.9.4.2",
                        "A structure element's /Alt does not match its enclosed annotation's " +
                        "/Contents; where both are present they must be identical " +
                        "(ISO 14289-2 §8.9.4.2)." +
                        (node.ObjectNumber > 0 ? $" Object {node.ObjectNumber}." : "")));
                }
            });

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.9.4.2",
                    $"…and at least one more mismatched Alt/Contents pair(s), " +
                    "not listed individually."));
        }

        // ----------------------------------------------------------------
        //  Clauses 8.9.2.4.9, 8.9.2.4.11, 8.9.2.4.15 — annotation subtypes that must not
        //  appear at all, or must not appear in the structure tree
        // ----------------------------------------------------------------

        /// <summary>
        /// Reports every annotation whose resolved <c>/Subtype</c> is one of <paramref name="subtypes"/>,
        /// regardless of whether it is referenced anywhere in the structure tree — the shape shared
        /// by §8.9.2.4.11 (Sound, Movie) and §8.9.2.4.15 (TrapNet), both of which read "deprecated
        /// … and shall not be present" (ISO 14289-2 §8.9.1's own general rule for every annotation
        /// type ISO 32000-2 deprecates), not merely "shall not be present <i>in the structure
        /// tree</i>" the way §8.9.2.4.9's Popup rule does.
        /// </summary>
        private static void CheckAnnotationSubtypesNotPresent(
            PdfDocument doc, string code, string message, List<PdfUa2StructureViolation> v,
            params string[] subtypes)
            => ReportAnnotations(doc, code, message, v, dict => HasSubtype(doc, dict, subtypes));

        /// <summary>
        /// The flat page/annotation scan the per-annotation checks share (§8.9.2.4.8, .10, .11, .12,
        /// .15, .19 and §8.10.3.3): every annotation <paramref name="violates"/> answers true for is
        /// reported under <paramref name="code"/> with its object number, the first
        /// <see cref="IndividualReportLimit"/> one by one and the rest as one count.
        /// </summary>
        /// <remarks>
        /// The scan does not stop at the limit: an annotation is cheap to test, so the summary's
        /// count is the count — stopping one past the limit, the summary would read "…and 1 more"
        /// for any number more.
        /// </remarks>
        private static void ReportAnnotations(PdfDocument doc, string code, string message,
            List<PdfUa2StructureViolation> v, Func<PdfDictionary, bool> violates)
        {
            int found = 0;
            foreach (PageAnnotation annot in AnnotationsOf(doc))
            {
                if (!violates(annot.Dict)) continue;

                found++;
                if (found > IndividualReportLimit) continue;
                v.Add(new PdfUa2StructureViolation(code,
                    message + (annot.ObjectNumber > 0 ? $" Object {annot.ObjectNumber}." : "")));
            }

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation(code,
                    $"…and {found - IndividualReportLimit} more, not listed individually."));
        }

        /// <summary>Whether <paramref name="annot"/>'s resolved <c>/Subtype</c> is one of <paramref name="subtypes"/>.</summary>
        private static bool HasSubtype(PdfDocument doc, PdfDictionary annot, params string[] subtypes)
        {
            string sub = (Resolve(doc, annot[PdfName.Subtype]) as PdfName)?.Value;
            for (int s = 0; s < subtypes.Length; s++)
                if (sub == subtypes[s]) return true;
            return false;
        }

        /// <summary>
        /// ISO 14289-2:2024 §8.9.2.4.11: "Sound and movie annotations are deprecated in PDF 2.0
        /// and therefore, in accordance with 8.9.1, shall not be present."
        /// </summary>
        private static void CheckDeprecatedSoundMovieAbsent(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
            => CheckAnnotationSubtypesNotPresent(doc, "UA2-8.9.2.4.11",
                "A Sound or Movie annotation is present; both are deprecated in PDF 2.0 and shall " +
                "not be present (ISO 14289-2 §8.9.2.4.11).", v,
                PdfAnnotationSubtypes.Sound, PdfAnnotationSubtypes.Movie);

        /// <summary>
        /// ISO 14289-2:2024 §8.9.2.4.15: "Trap network annotations are deprecated in PDF 2.0 and
        /// shall not be used in files conforming to this document, in accordance with 8.9.1."
        /// </summary>
        private static void CheckDeprecatedTrapNetAbsent(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
            => CheckAnnotationSubtypesNotPresent(doc, "UA2-8.9.2.4.15",
                "A TrapNet (trap network) annotation is present; trap network annotations are " +
                "deprecated in PDF 2.0 and shall not be used (ISO 14289-2 §8.9.2.4.15).", v,
                PdfAnnotationSubtypes.TrapNet);

        /// <summary>
        /// ISO 14289-2:2024 §8.9.2.4.9: "Popup annotations shall not be present in the structure
        /// tree."
        /// </summary>
        /// <remarks>
        /// Unlike <see cref="CheckAnnotationSubtypesNotPresent"/>'s two callers, a Popup annotation
        /// is legal in the file itself (it is how ISO 32000-2 attaches a comment pop-up to its
        /// parent markup annotation) — only its presence <i>in the structure tree</i> is
        /// prohibited, so this reuses the §8.9.2.2/§8.10.1 <c>/OBJR</c> cross-reference shape
        /// rather than <see cref="CheckAnnotationSubtypesNotPresent"/>'s flat page/annotation scan.
        /// </remarks>
        private static void CheckPopupNotInStructureTree(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var structTreeRoot = ResolveStructTreeRoot(doc, catalog);
            if (structTreeRoot == null) return;

            var popupAnnotations = CollectAnnotationObjectNumbers(doc, (d, dict) =>
                (Resolve(d, dict[PdfName.Subtype]) as PdfName)?.Value == PdfAnnotationSubtypes.Popup);
            if (popupAnnotations.Count == 0) return;

            int found = 0;
            StructTreeWalker.Walk(structTreeRoot, doc, index: null, node =>
            {
                if (node.Dict == null || found > IndividualReportLimit) return;

                foreach (PdfObject rawKid in StructTreeWalker.GetRawKids(node.Dict, doc))
                {
                    var objrDict = Resolve(doc, rawKid) as PdfDictionary;
                    if (objrDict == null) continue;
                    if (StructKidClassifier.Classify(objrDict, doc) != StructKidKind.ObjectReference) continue;
                    if (!(objrDict[PdfName.Obj] is PdfReference objRef)) continue;
                    if (!popupAnnotations.Contains(objRef.ReferenceObjectNumber)) continue;

                    found++;
                    if (found > IndividualReportLimit) break;
                    v.Add(new PdfUa2StructureViolation("UA2-8.9.2.4.9",
                        "A Popup annotation is referenced from the structure tree; Popup annotations " +
                        "shall not be present in the structure tree (ISO 14289-2 §8.9.2.4.9)." +
                        (node.ObjectNumber > 0 ? $" Object {node.ObjectNumber}." : "")));
                }
            });

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.9.2.4.9",
                    $"…and at least one more Popup annotation(s) present in the " +
                    "structure tree, not listed individually."));
        }

        // ----------------------------------------------------------------
        //  Clauses 8.9.2.4.12, 8.9.2.4.19 — annotation subtypes that require a Contents entry
        // ----------------------------------------------------------------

        /// <summary>
        /// Reports every annotation whose resolved <c>/Subtype</c> is one of <paramref name="subtypes"/>
        /// and whose <c>/Contents</c> is absent or an empty string — the shape shared by
        /// §8.9.2.4.12 (Screen) and §8.9.2.4.19 (3D, RichMedia), both flat page/annotation scans
        /// like <see cref="CheckAnnotationSubtypesNotPresent"/>, but testing an entry's value
        /// rather than merely its presence in the file.
        /// </summary>
        private static void CheckAnnotationSubtypesRequireContents(
            PdfDocument doc, string code, string message, List<PdfUa2StructureViolation> v,
            params string[] subtypes)
            => ReportAnnotations(doc, code, message, v, dict =>
                HasSubtype(doc, dict, subtypes) &&
                string.IsNullOrEmpty((Resolve(doc, dict[PdfName.Contents]) as PdfString)?.GetText()));

        /// <summary>ISO 14289-2:2024 §8.9.2.4.12: "Screen annotations shall include a Contents entry."</summary>
        private static void CheckScreenHasContents(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
            => CheckAnnotationSubtypesRequireContents(doc, "UA2-8.9.2.4.12",
                "A Screen annotation has no /Contents entry; §8.9.2.4.12 requires one " +
                "(ISO 14289-2 §8.9.2.4.12).", v, PdfAnnotationSubtypes.Screen);

        /// <summary>
        /// ISO 14289-2:2024 §8.9.2.4.19: "3D and RichMedia annotations shall include alternate
        /// descriptions in their respective Contents entries."
        /// </summary>
        private static void CheckThreeDAndRichMediaHaveContents(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
            => CheckAnnotationSubtypesRequireContents(doc, "UA2-8.9.2.4.19",
                "A 3D or RichMedia annotation has no alternate description in its /Contents entry; " +
                "§8.9.2.4.19 requires one (ISO 14289-2 §8.9.2.4.19).", v,
                PdfAnnotationSubtypes.ThreeD, PdfAnnotationSubtypes.RichMedia);

        // ----------------------------------------------------------------
        //  Clause 8.9.2.4.13 — a zero-size widget annotation shall be an artifact
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.9.2.4.13: "In addition to the requirements of 8.9.2.2, a widget
        /// annotation of zero height and width shall be an artifact in accordance with 8.3."
        /// </summary>
        /// <remarks>
        /// A third caller of <see cref="CheckAnnotationsMustBeArtifacts"/> (after §8.9.2.2 and
        /// §8.9.2.4.14), supplying yet another <see cref="CollectAnnotationObjectNumbers"/>
        /// predicate — a Widget whose <c>/Rect</c> resolves to four numbers with equal x
        /// coordinates and equal y coordinates. A malformed or unresolvable <c>/Rect</c> is read
        /// as "not provably zero-size" and skipped, the same fail-safe direction every other
        /// predicate in this file takes on unparseable input.
        /// </remarks>
        private static void CheckZeroSizeWidgetIsArtifact(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var mustBeArtifact = CollectAnnotationObjectNumbers(doc, (d, dict) =>
            {
                if ((Resolve(d, dict[PdfName.Subtype]) as PdfName)?.Value != PdfAnnotationSubtypes.Widget)
                    return false;

                var rect = Resolve(d, dict[PdfName.Rect]) as PdfArray;
                if (rect == null || rect.Count != 4) return false;

                double? x0 = (Resolve(d, rect[0]) as PdfNumber)?.DoubleValue;
                double? y0 = (Resolve(d, rect[1]) as PdfNumber)?.DoubleValue;
                double? x1 = (Resolve(d, rect[2]) as PdfNumber)?.DoubleValue;
                double? y1 = (Resolve(d, rect[3]) as PdfNumber)?.DoubleValue;
                if (x0 == null || y0 == null || x1 == null || y1 == null) return false;

                return x0 == x1 && y0 == y1;
            });
            CheckAnnotationsMustBeArtifacts(doc, catalog, v, "UA2-8.9.2.4.13",
                "A widget annotation of zero height and width is present in the structure tree but " +
                "not enclosed within an Artifact structure element; a zero-size widget shall be an " +
                "artifact (ISO 14289-2 §8.9.2.4.13).", mustBeArtifact);
        }

        // ----------------------------------------------------------------
        //  Clause 8.9.2.4.10 — File attachment annotations must have AFRelationship
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.9.2.4.10: "When a file attachment annotation … references a file
        /// specification dictionary …, the file specification dictionary shall include an
        /// AFRelationship entry."
        /// </summary>
        /// <remarks>
        /// A twelfth check shape: a flat page/annotation scan, like
        /// <see cref="CheckAnnotationSubtypesNotPresent"/> and
        /// <see cref="CheckAnnotationSubtypesRequireContents"/>, but reading one level deeper into
        /// a nested dictionary (<c>/FS</c>) rather than an entry on the annotation itself.
        /// <para>§7.11.1 lets a file specification be a bare string instead of a dictionary — "the
        /// string form is equivalent to the AFRelationship entry having the value of
        /// Unspecified" — so a string-form <c>/FS</c> already satisfies the clause's intent and is
        /// skipped rather than flagged as missing a dictionary it was never required to be.</para>
        /// </remarks>
        private static void CheckFileAttachmentHasAFRelationship(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
            => ReportAnnotations(doc, "UA2-8.9.2.4.10",
                "A file attachment annotation's file specification dictionary has no " +
                "/AFRelationship entry (ISO 14289-2 §8.9.2.4.10).", v, dict =>
                    HasSubtype(doc, dict, PdfAnnotationSubtypes.FileAttachment) &&
                    // Absent, or the legal string form, has nothing to check.
                    Resolve(doc, dict[PdfName.FS]) is PdfDictionary fs && fs[PdfName.AFRelationship] == null);

        /// <summary>
        /// ISO 14289-2:2024 §8.9.2.4.8: "A Contents entry describing the author's intent shall be
        /// provided [for Ink annotations]." Unlike §8.9.2.4.12/.19, this requirement is
        /// unconditional — freeform Ink content has no other mechanism to convey intent.
        /// </summary>
        private static void CheckInkHasContents(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
            => CheckAnnotationSubtypesRequireContents(doc, "UA2-8.9.2.4.8",
                "An Ink annotation has no /Contents entry describing the author's intent; " +
                "§8.9.2.4.8 requires one (ISO 14289-2 §8.9.2.4.8).", v, PdfAnnotationSubtypes.Ink);

        // ----------------------------------------------------------------
        //  Clause 8.10.3.3 — Text fields: RV requires V
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.10.3.3: "For text fields, when an RV entry … is present, a V
        /// entry … shall also be present, and they shall be textually equivalent."
        /// </summary>
        /// <remarks>
        /// <b>Only the presence half is checked.</b> <c>/RV</c> (§12.7.4.3 Table 228) is the
        /// field's <i>rich-text</i> value and may carry markup, while <c>/V</c> is plain text;
        /// judging whether the two are "textually equivalent" after stripping markup is a
        /// semantic comparison this checker does not attempt anywhere else, and a naive
        /// byte-for-byte comparison would false-flag every conforming field whose rich text
        /// merely formats the same words. "RV present without V" has no such ambiguity — it is a
        /// missing required entry, checkable outright.
        /// <para>Scoped to a Widget annotation whose own <c>/FT</c> resolves to <c>/Tx</c>
        /// directly; a text field whose <c>/FT</c> is inherited from an ancestor field dictionary
        /// (Table 220, not read by this check) is not reached. Narrower than the clause's full
        /// reach, in the same direction every earlier narrowing in this file has taken — silence
        /// on a case rather than a guess.</para>
        /// </remarks>
        private static void CheckTextFieldRVRequiresV(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
            => ReportAnnotations(doc, "UA2-8.10.3.3",
                "A text field has an /RV entry but no /V entry; §8.10.3.3 requires both " +
                "when either is present (ISO 14289-2 §8.10.3.3).", v, dict =>
                    HasSubtype(doc, dict, PdfAnnotationSubtypes.Widget) &&
                    (Resolve(doc, dict[PdfName.FT]) as PdfName)?.Value == "Tx" &&
                    dict[PdfName.RV] != null && dict[PdfName.V] == null);

        // ----------------------------------------------------------------
        //  Clause 8.4.3 — real content mapped to the Unicode PUA needs ActualText or Alt
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.4.3: "In all cases, where real content maps to Unicode PUA values, an
        /// ActualText or Alt entry shall be present."
        /// </summary>
        /// <remarks>
        /// <para><b>What "maps to PUA" means here.</b> A text-showing operator's strings are decoded
        /// through the font (<c>PdfFont.Decode</c>: <c>/ToUnicode</c> first, then the encoding) and
        /// checked with <see cref="ContainsUnicodePua"/>. Content inside an <c>/Artifact</c> sequence
        /// is not real content and is not judged.</para>
        /// <para><b>Where the entry may be.</b> The clause's own preceding sentence places an
        /// <c>/ActualText</c> "for this character or for the sequence of characters of which such
        /// character is a part", so any of these satisfies it: an enclosing marked-content sequence
        /// whose property list carries <c>/ActualText</c>; or, through the innermost <c>/MCID</c>, the
        /// structure element owning that marked content or any of its ancestors carrying
        /// <c>/ActualText</c> or <c>/Alt</c> (§14.9.3/§14.9.4 make both a replacement for everything
        /// the element encloses). An empty entry is present.</para>
        /// <para><b>Judged only where it can be decided.</b> MCIDs are resolved for the page's own
        /// content stream alone (<see cref="ShownText.IsPageContent"/>): a form XObject or an annotation
        /// appearance has its own <c>/StructParents</c> entry and is skipped unless marked-content
        /// <c>/ActualText</c> already settles it. An MCID no structure element claims is skipped too.
        /// PUA content on the page with no MCID at all is reported — untagged real content has no
        /// element to carry either entry. One finding per page and MCID.</para>
        /// </remarks>
        private static void CheckPuaContentHasActualTextOrAlt(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var fonts = new Dictionary<PdfDictionary, PdfFont>(PdfReferenceEquality.Instance);
            Dictionary<(int Page, int Mcid), StructNode> owners = null;
            var reported = new HashSet<(int Page, int Mcid)>();
            int found = 0;

            WalkShownText(doc, () => found > IndividualReportLimit, shown =>
            {
                if (shown.InArtifact || shown.HasMarkedContentActualText || !shown.IsPageContent) return;
                if (!ShowsPua(CachedFont(doc, shown.FontDict, fonts), shown.Segments)) return;

                if (shown.Mcid >= 0)
                {
                    owners ??= MapMarkedContentOwners(doc, catalog);
                    if (!owners.TryGetValue((shown.PageIndex, shown.Mcid), out StructNode owner)) return;
                    for (StructNode n = owner; n != null; n = n.Parent)
                        if (n.Dict != null && (Resolve(doc, n.Dict[PdfName.ActualText]) is PdfString
                                            || Resolve(doc, n.Dict[PdfName.Alt]) is PdfString))
                            return;
                }

                if (!reported.Add((shown.PageIndex, shown.Mcid))) return;
                found++;
                if (found > IndividualReportLimit) return;
                v.Add(new PdfUa2StructureViolation("UA2-8.4.3",
                    $"Page {shown.PageIndex + 1}: text mapped to the Unicode Private Use Area " +
                    (shown.Mcid >= 0
                        ? $"(marked content {shown.Mcid}) has no /ActualText or /Alt on its marked content, its structure element or any ancestor"
                        : "is neither marked as an artifact, tagged, nor covered by marked-content /ActualText") +
                    "; §8.4.3 requires one (ISO 14289-2 §8.4.3)."));
            });

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.4.3",
                    $"…and at least one more, not listed individually."));
        }

        /// <summary>True when any shown string decodes, through <paramref name="font"/>, to a PUA code point.</summary>
        private static bool ShowsPua(PdfFont font, List<object> segments)
        {
            if (font == null) return false;
            foreach (object segment in segments)
            {
                if (!(segment is byte[] bytes) || bytes.Length == 0) continue;
                string text;
                try { text = font.Decode(bytes); }
                catch (Exception ex)
                {
                    Report("PdfUa2StructureChecker.ShowsPua", "shown bytes could not be decoded", ex);
                    continue;
                }
                if (ContainsUnicodePua(text)) return true;
            }
            return false;
        }

        /// <summary>
        /// (page index, MCID) → the structure element that owns that marked content, for content on a
        /// page's own stream: a bare integer kid on the element's page, or an <c>/MCR</c> kid with no
        /// <c>/Stm</c> (one naming a form XObject stream is not page content). The page is the kid's or
        /// element's <c>/Pg</c>, inherited from the nearest ancestor that has one. The first owner found
        /// wins; a duplicate claim is a separate defect.
        /// </summary>
        private static Dictionary<(int Page, int Mcid), StructNode> MapMarkedContentOwners(
            PdfDocument doc, PdfDictionary catalog)
        {
            var owners = new Dictionary<(int Page, int Mcid), StructNode>();
            var structTreeRoot = ResolveStructTreeRoot(doc, catalog);
            if (structTreeRoot == null) return owners;

            // The walker's own /Pg resolution and inheritance (StructNode.PageIndex) — an /MCR kid's
            // /Pg overrides it through the same index.
            PdfObjectIndex index = PdfObjectIndex.Build(doc);
            StructTreeWalker.Walk(structTreeRoot, doc, index, node =>
            {
                foreach (PdfObject rawKid in node.RawKids)
                {
                    if (rawKid is PdfNumber mcid)
                    {
                        if (node.PageIndex >= 0 && !owners.ContainsKey((node.PageIndex, mcid.IntValue))) owners[(node.PageIndex, mcid.IntValue)] = node;
                        continue;
                    }
                    if (!(Resolve(doc, rawKid) is PdfDictionary kid) || kid[PdfName.Stm] != null) continue;
                    if (!(Resolve(doc, kid[PdfName.MCID]) is PdfNumber kidMcid)) continue;
                    int kidPage = kid[PdfName.Pg] is PdfReference pg && index.GetPageIndex(pg.ReferenceObjectNumber) is int p && p >= 0
                        ? p : node.PageIndex;
                    if (kidPage >= 0 && !owners.ContainsKey((kidPage, kidMcid.IntValue))) owners[(kidPage, kidMcid.IntValue)] = node;
                }
            });
            return owners;
        }

        // ----------------------------------------------------------------
        //  Clause 8.4.4 — the default natural language must be declared
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.4.4: "The default natural language for content and text strings
        /// shall be specified using the Lang entry, with a non-empty value, in the document
        /// catalog dictionary."
        /// </summary>
        /// <remarks>
        /// A catalog-only check, like §8.10.1's XFA prohibition, but testing a non-empty string
        /// value rather than bare presence — the same "presence, non-empty" reading
        /// <c>HasNonEmptyString</c> already uses for §8.2.5.28.2's Figure <c>/Alt</c>, applied
        /// here to the catalog directly instead of a structure element.
        /// <para>Only the catalog-level default is checked. The clause's further requirements —
        /// declaring a language <i>change</i> inside content or inside a text string, and that a
        /// declared <c>/Lang</c> "resolve to a specific language" (a BCP 47 well-formedness
        /// question) — are not attempted; both need reading beyond the catalog dictionary this
        /// check stays scoped to.</para>
        /// </remarks>
        private static void CheckCatalogLangSpecified(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var lang = Resolve(doc, catalog[PdfName.Lang]) as PdfString;
            if (lang != null && !string.IsNullOrEmpty(lang.GetText())) return;

            v.Add(new PdfUa2StructureViolation("UA2-8.4.4",
                "The document catalog has no /Lang entry with a non-empty value; §8.4.4 requires " +
                "the default natural language to be specified there (ISO 14289-2 §8.4.4)."));
        }

        // ----------------------------------------------------------------
        //  Clause 8.7 — Optional content configuration dictionaries
        // ----------------------------------------------------------------

        /// <summary>
        /// Every optional content configuration dictionary the catalog's <c>/OCProperties</c>
        /// declares — the default (<c>/D</c>) and every entry of <c>/Configs</c>, each once —
        /// through <c>Plugins.Conformance.OptionalContentConfigurations</c>, which PDF/UA-1's UA58
        /// and PDF/A's optional content rules read too.
        /// </summary>
        private static List<Plugins.Conformance.OptionalContentConfigurations.Configuration> OcConfigurations(
            PdfDocument doc, PdfDictionary catalog, out PdfDictionary ocProperties)
        {
            ocProperties = Resolve(doc, catalog[PdfName.OCProperties]) as PdfDictionary;
            return Plugins.Conformance.OptionalContentConfigurations.Read(doc, ocProperties);
        }

        /// <summary>
        /// ISO 14289-2:2024 §8.7: "The AS key shall not appear in any optional content
        /// configuration dictionary."
        /// </summary>
        private static void CheckOcConfigNoAsKey(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            int found = 0;
            foreach (var configuration in OcConfigurations(doc, catalog, out _))
            {
                if (found > IndividualReportLimit) break;
                if (configuration.Dictionary[PdfName.AS] == null) continue;

                found++;
                if (found > IndividualReportLimit) break;
                v.Add(new PdfUa2StructureViolation("UA2-8.7",
                    "An optional content configuration dictionary has an /AS entry; §8.7 " +
                    "prohibits it in any optional content configuration dictionary " +
                    "(ISO 14289-2 §8.7)."));
            }

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.7",
                    $"…and at least one more, not listed individually."));
        }

        /// <summary>
        /// ISO 14289-2:2024 §8.7: "All optional content configuration dictionaries in the file,
        /// including the default optional content configuration dictionary, shall contain a Name
        /// entry … whose value is a non-empty text string when: a) a file contains a Configs
        /// entry in the OCProperties entry of the document catalog dictionary …, and b) the
        /// Configs entry contains at least one optional content configuration dictionary."
        /// </summary>
        /// <remarks>
        /// The condition gates the whole check, not just which dictionaries it looks at: a file
        /// with only the default configuration and no configuration in <c>/Configs</c> is untouched
        /// by this rule at all. ISO 14289-1 7.10 says the same in the same words, so the rule is
        /// <c>OptionalContentConfigurations.UnnamedUnderPdfUa</c>, PDF/UA-1's UA58 reading it too.
        /// </remarks>
        private static void CheckOcConfigNamesWhenConfigsPresent(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var configurations = OcConfigurations(doc, catalog, out PdfDictionary ocProperties);
            int found = 0;
            foreach (var unnamed in Plugins.Conformance.OptionalContentConfigurations.UnnamedUnderPdfUa(
                         doc, ocProperties, configurations))
            {
                if (found > IndividualReportLimit) break;

                found++;
                if (found > IndividualReportLimit) break;
                v.Add(new PdfUa2StructureViolation("UA2-8.7",
                    "An optional content configuration dictionary has no non-empty /Name entry; " +
                    "§8.7 requires one on every configuration, including the default, once the " +
                    "file declares alternate configurations (ISO 14289-2 §8.7)."));
            }

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.7",
                    $"…and at least one more, not listed individually."));
        }

        // ----------------------------------------------------------------
        //  Clause 8.11.2 — ViewerPreferences /DisplayDocTitle must be true
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.11.2: "The ViewerPreferences dictionary of the document catalog
        /// dictionary shall be present and shall contain at least the DisplayDocTitle key with a
        /// value of true."
        /// </summary>
        private static void CheckDisplayDocTitleIsTrue(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            // Through Core.Document.PdfViewerPreferences, the one reader — which takes a resolver
            // rather than a PdfDocument so an encrypted document, loading no catalog object, can
            // still be read. The two findings below stay distinguishable because the wrapper
            // reports an absent dictionary as a null Dictionary rather than as a missing entry.
            var preferences = PdfViewerPreferences.Read(catalog, o => Resolve(doc, o));
            if (preferences.Dictionary == null)
            {
                v.Add(new PdfUa2StructureViolation("UA2-8.11.2",
                    "The document catalog has no /ViewerPreferences dictionary; §8.11.2 requires " +
                    "one with /DisplayDocTitle true (ISO 14289-2 §8.11.2)."));
                return;
            }

            if (preferences.DisplayDocTitle == true) return;

            v.Add(new PdfUa2StructureViolation("UA2-8.11.2",
                "The document catalog's /ViewerPreferences has no /DisplayDocTitle entry with " +
                "value true; §8.11.2 requires it (ISO 14289-2 §8.11.2)."));
        }

        // ----------------------------------------------------------------
        //  Clause 8.14.1 — Descriptions for embedded files
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.14.1: "The Desc entry … shall be present on all file specification
        /// dictionaries present in the EmbeddedFiles name tree of a conforming file, and shall
        /// provide sufficient context to make clear the intent of the embedded file."
        /// </summary>
        /// <remarks>
        /// Only presence is checked — "sufficient context" is a semantic judgment this checker
        /// makes nowhere else, the same reasoning that scopes §8.9.4.1's Contents-entry
        /// requirement out and §8.2.5.28.2's Figure <c>/Alt</c> check to presence alone.
        /// <para>Reuses <see cref="PdfNameTree"/> (the one name-tree traversal shared across every
        /// ring, per GAPS.md §15c) rather than open-coding a fourth copy of the
        /// <c>/Kids</c>-cycle-guarded walk — the same shape
        /// <c>PdfAValidator.CheckEmbeddedFilesAfRelationship</c> already uses for its own,
        /// different, per-file-spec entry requirement (A4-05).</para>
        /// </remarks>
        private static void CheckEmbeddedFilesHaveDesc(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var names = Resolve(doc, catalog[PdfName.Names]) as PdfDictionary;
            PdfObject efRoot = names?[PdfName.EmbeddedFiles];
            if (efRoot == null) return;

            var entries = new List<KeyValuePair<PdfObject, PdfObject>>();
            int budget = PdfLimits.MaxNameTreeEntries;
            PdfNameTree.TryFlatten(efRoot, doc, entries, ref budget);

            int found = 0;
            foreach (KeyValuePair<PdfObject, PdfObject> entry in entries)
            {
                if (found > IndividualReportLimit) break;

                var fileSpec = Resolve(doc, entry.Value) as PdfDictionary;
                if (fileSpec == null) continue;
                if (fileSpec[PdfName.Desc] != null) continue;

                found++;
                if (found > IndividualReportLimit) break;
                v.Add(new PdfUa2StructureViolation("UA2-8.14.1",
                    "A file specification dictionary in the EmbeddedFiles name tree has no /Desc " +
                    "entry; §8.14.1 requires one (ISO 14289-2 §8.14.1)."));
            }

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.14.1",
                    $"…and at least one more, not listed individually."));
        }

        // ----------------------------------------------------------------
        //  Clause 8.11.1 — XMP metadata must include dc:title
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.11.1: "The Metadata stream … in the document catalog dictionary
        /// shall include a dc:title entry reflecting the title of the document."
        /// </summary>
        /// <remarks>
        /// <para>Reuses <c>Plugins.Conformance.PdfUaChecker.FindXmpDcTitle</c> (promoted from
        /// <c>private</c> to <c>internal</c>, with a matching <c>InternalsVisibleTo</c> for this
        /// assembly) rather than re-deriving the same XMP lookup a second time — it already
        /// carries the BOM-aware decode, the permissive DTD-processing setting the codebase's own
        /// history says is load-bearing, and the encryption-aware "absent vs. unreadable"
        /// distinction. Unlike PDF/UA-1's UA04, which that method backs, §8.11.1 accepts only
        /// XMP <c>dc:title</c> — there is no <c>/Info /Title</c> fallback here, so only the
        /// method itself is reused, not UA04's larger surrounding check.</para>
        /// <para><c>Unreadable</c> is treated as "cannot be verified either way" and produces no
        /// finding, the same fail-safe direction this checker takes everywhere it cannot support
        /// a claim — silence rather than a guess, whether the packet is genuinely malformed or
        /// merely ciphertext on an encrypted document.</para>
        /// </remarks>
        private static void CheckXmpDcTitlePresent(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            switch (PdfUaChecker.FindXmpDcTitle(doc, catalog))
            {
                case PdfUaChecker.XmpTitleState.Present:
                case PdfUaChecker.XmpTitleState.Unreadable:
                    return;

                default:
                    v.Add(new PdfUa2StructureViolation("UA2-8.11.1",
                        "The document's XMP metadata has no non-empty dc:title entry; §8.11.1 " +
                        "requires one (ISO 14289-2 §8.11.1)."));
                    return;
            }
        }

        // ----------------------------------------------------------------
        //  Clause 8.4.5.3.2 — embedded CIDFontType2 fonts require CIDToGIDMap
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.4.5.3.2: "All embedded Type 2 CIDFonts in the CIDFont dictionary
        /// shall contain a CIDToGIDMap entry that shall be a stream mapping from CIDs to glyph
        /// indices or the name Identity."
        /// </summary>
        /// <remarks>
        /// <para>The first font-level check in this file, and the first to walk page resources
        /// rather than the structure tree, the catalog, or annotations. Reuses two existing,
        /// carefully-tuned traversals rather than adding a third content/resource walk (CLAUDE.md:
        /// "Conformance holds two content walks … and must not grow a third"):
        /// <c>PdfXValidator.ScanPage</c> to reach every resource dictionary a page can see
        /// (forms, patterns, annotation appearances, all cycle- and budget-guarded), and
        /// <c>FontEmbeddingProbe.WalkDescriptors</c> to follow a Type 0 font's
        /// <c>/DescendantFonts</c> down to its terminal CIDFont with a resolved
        /// <c>/FontDescriptor</c> — the same walk <c>PdfXValidator.CheckFontEmbedding</c> (X04)
        /// already reuses for a different verdict.</para>
        /// <para>Both are <c>internal</c> members of <c>Plugins.Conformance</c>, reachable here
        /// through the same <c>InternalsVisibleTo("Tetitulz.Pdf.Accessibility")</c> §8.11.1's
        /// reuse of <c>FindXmpDcTitle</c> added.</para>
        /// <para>Only a CIDFontType2 descendant whose descriptor carries <c>/FontFile2</c> (i.e.
        /// is actually embedded) is in scope — the clause's own word "embedded" — and a font
        /// whose embedding status cannot be confirmed (a <c>null</c> descriptor from an
        /// unresolved <c>/DescendantFonts</c> array or the depth cap) is skipped rather than
        /// guessed either way, per <c>WalkDescriptors</c>' own documented contract.</para>
        /// </remarks>
        private static void CheckCidToGidMapPresent(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            int found = 0;
            WalkAllFontDescriptors(doc, (leaf, subtype, fd) =>
            {
                if (found > IndividualReportLimit) return;
                if (subtype != "CIDFontType2") return;
                if (fd == null || fd[PdfName.FontFile2] == null) return;

                var cidToGidMap = Resolve(doc, leaf[PdfName.CIDToGIDMap]);
                bool valid = cidToGidMap is PdfStream
                          || (cidToGidMap is PdfName n && n.Value == "Identity");
                if (valid) return;

                found++;
                if (found > IndividualReportLimit) return;
                v.Add(new PdfUa2StructureViolation("UA2-8.4.5.3.2",
                    "An embedded CIDFontType2 font has no /CIDToGIDMap entry that is a stream " +
                    "or the name /Identity; §8.4.5.3.2 requires one (ISO 14289-2 §8.4.5.3.2)."));
            }, () => found > IndividualReportLimit);

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.4.5.3.2",
                    $"…and at least one more, not listed individually."));
        }

        /// <summary>
        /// Invokes <paramref name="onLeaf"/> once per terminal font descriptor reachable from any
        /// page's resources — the shared page/resource/font walk every font-level check in this
        /// file uses, so the <c>ScanPage</c> + document-scoped de-duplication +
        /// <c>WalkDescriptors</c> nesting is written once.
        /// </summary>
        /// <remarks>
        /// De-duplicates by font dictionary identity across the whole document — the same
        /// <c>seenFonts</c> pattern <c>PdfXValidator.CheckFontEmbedding</c> (X04) uses, since one
        /// font object is normally referenced from several resource dictionaries. Checked between
        /// pages and between resource dictionaries, <paramref name="shouldStop"/> lets a caller
        /// that has already hit its own report cap end the walk early without this helper needing
        /// to know what "cap" means to it.
        /// </remarks>
        private static void WalkAllFontDescriptors(
            PdfDocument doc, Action<PdfDictionary, string, PdfDictionary> onLeaf, Func<bool> shouldStop)
        {
            if (doc.Pages == null) return;

            var seenFonts = new HashSet<PdfDictionary>();
            long documentBytes = 0;

            for (int p = 0; p < doc.Pages.Count; p++)
            {
                if (shouldStop()) break;

                var scan = PdfXValidator.ScanPage(doc, doc.Pages[p],
                    PdfXValidator.DefaultMaxPageContentBytes, ref documentBytes,
                    PdfXValidator.DefaultMaxDocumentContentBytes, collectContent: false);

                foreach (PdfDictionary res in scan.Resources)
                {
                    if (shouldStop()) break;

                    var fontDict = Resolve(doc, res[PdfName.Font]) as PdfDictionary;
                    if (fontDict == null) continue;

                    foreach (KeyValuePair<PdfName, PdfObject> kvp in fontDict)
                    {
                        var fontObj = Resolve(doc, kvp.Value) as PdfDictionary;
                        if (fontObj == null) continue;
                        if (!seenFonts.Add(fontObj)) continue;

                        FontEmbeddingProbe.WalkDescriptors(fontObj, doc, onLeaf, diagnostic: null,
                            new HashSet<PdfDictionary>(), 0);
                    }
                }
            }
        }

        // ----------------------------------------------------------------
        //  Clause 8.4.5.5.1 — fonts used for rendering must be embedded
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.4.5.5.1: "The font programs for all fonts used for rendering
        /// within a conforming file, as determined by whether at least one of its glyphs is
        /// referenced from one or more content streams, shall be embedded within that file… A
        /// font referenced for use solely in [text rendering mode 3] is therefore not rendered
        /// and is thus exempt from the embedding requirement."
        /// </summary>
        /// <remarks>
        /// <para>The first check in this file to track <c>Tr</c> (text rendering mode) as part of
        /// its graphics-state shadow — unlike <see cref="CheckNoNotdefInFonts"/>'s own
        /// deliberately narrower shadow (font name only, since that clause applies "regardless of
        /// text rendering mode"), this clause's own exemption makes <c>Tr</c> load-bearing: a font
        /// shown only under mode 3 draws nothing (§9.3.6), so it is not "used for rendering" and
        /// carries no embedding requirement at all.</para>
        /// <para><b>Two passes, not one.</b> Pass one is <see cref="WalkShownText"/> — the content
        /// walk this clause shares with §8.4.5.6 and §8.4.5.9 — collecting the set of font
        /// dictionaries shown by a text-showing operator while <c>Tr != 3</c>. Pass two asks, only for fonts in that set, whether they carry an
        /// embedded program — reusing <see cref="FontEmbeddingProbe.ReportUnembedded"/> outright,
        /// the one shared answer to that question across A06/UA25/X04 (see its own remarks), so
        /// this becomes a fourth caller rather than a fourth copy of the <c>/DescendantFonts</c>
        /// walk, the Type 3 exemption, and the depth cap the other three already agree on.</para>
        /// <para>A font resource never reached by a text-showing operator at all (a dead
        /// <c>/Font</c> resource entry, or one only ever selected by a <c>Tf</c> nothing then
        /// shows) is never added to the set and so is never checked — the clause's own text scopes
        /// the requirement to fonts "referenced from one or more content streams" by an actual
        /// glyph, not merely listed in a resource dictionary. Fail-safe in the same direction as
        /// every other under-determined case in this file: this can only under-report, never
        /// over-report.</para>
        /// </remarks>
        private static void CheckFontsUsedForRenderingAreEmbedded(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var usedOutsideMode3 = new HashSet<PdfDictionary>();
            WalkShownText(doc, () => false, shown =>
            {
                if (shown.RenderMode != 3) usedOutsideMode3.Add(shown.FontDict);
            });

            if (usedOutsideMode3.Count == 0) return;

            int found = 0;
            foreach (PdfDictionary fontObj in usedOutsideMode3)
            {
                if (found > IndividualReportLimit) break;

                FontEmbeddingProbe.ReportUnembedded(fontObj, doc, label =>
                {
                    if (found > IndividualReportLimit) return;
                    found++;
                    if (found > IndividualReportLimit) return;
                    v.Add(new PdfUa2StructureViolation("UA2-8.4.5.5.1",
                        $"Font \"{label}\", shown by a text-showing operator outside text " +
                        "rendering mode 3, has no embedded font program; §8.4.5.5.1 requires " +
                        "one (ISO 14289-2 §8.4.5.5.1)."));
                });
            }

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.4.5.5.1",
                    $"…and at least one more, not listed individually."));
        }

        // ----------------------------------------------------------------
        //  Clause 8.4.5.7 — a symbolic TrueType font must not carry /Encoding
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.4.5.7: "Symbolic TrueType fonts shall not contain an Encoding
        /// entry in the font dictionary."
        /// </summary>
        /// <remarks>
        /// Reuses <c>Core.Fonts.FontFlags.IsSymbolic</c> — the one implementation of §9.6.6.4's
        /// symbolic test, shared across Core, a plugin and a feature — rather than re-testing the
        /// <c>/FontDescriptor /Flags</c> bit a fourth time. The clause's other rules are
        /// <see cref="CheckNonSymbolicTrueTypeEncoding"/>, <see cref="CheckTrueTypeCmapSubtables"/>
        /// and <see cref="CheckNonSymbolicTrueTypeDifferences"/>, sharing this code.
        /// </remarks>
        private static void CheckSymbolicTrueTypeHasNoEncoding(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            int found = 0;
            WalkAllFontDescriptors(doc, (leaf, subtype, fd) =>
            {
                if (found > IndividualReportLimit) return;
                if (subtype != "TrueType") return;
                if (!FontFlags.IsSymbolic(leaf, doc)) return;
                if (leaf[PdfName.Encoding] == null) return;

                found++;
                if (found > IndividualReportLimit) return;
                v.Add(new PdfUa2StructureViolation("UA2-8.4.5.7",
                    "A symbolic TrueType font has an /Encoding entry in its font dictionary; " +
                    "§8.4.5.7 prohibits it (ISO 14289-2 §8.4.5.7)."));
            }, () => found > IndividualReportLimit);

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.4.5.7",
                    $"…and at least one more, not listed individually."));
        }

        /// <summary>
        /// ISO 14289-2:2024 §8.4.5.7: "All non-symbolic TrueType fonts shall have either
        /// MacRomanEncoding or WinAnsiEncoding as the value for the Encoding key in the Font
        /// dictionary, or as the value for the BaseEncoding key in the dictionary that is the value
        /// of the Encoding key in the Font dictionary."
        /// </summary>
        /// <remarks>
        /// A dictionary read over every TrueType font a page's resources reach. <b>Only a font that
        /// declares its flags is judged</b> (<see cref="DeclaresFontFlags"/>): with no
        /// <c>/FontDescriptor /Flags</c>, <see cref="FontFlags.IsSymbolic"/> answers "not symbolic"
        /// by default, and a rule for non-symbolic fonts must not be applied on a default the file
        /// never stated.
        /// </remarks>
        private static void CheckNonSymbolicTrueTypeEncoding(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            int found = 0;
            WalkAllFontDescriptors(doc, (leaf, subtype, fd) =>
            {
                if (found > IndividualReportLimit) return;
                if (subtype != "TrueType" || !DeclaresFontFlags(doc, leaf) || FontFlags.IsSymbolic(leaf, doc)) return;

                string named = TrueTypeFontRules.DeclaredEncodingName(doc, leaf);
                if (TrueTypeFontRules.IsMacRomanOrWinAnsi(named)) return;

                found++;
                if (found > IndividualReportLimit) return;
                v.Add(new PdfUa2StructureViolation("UA2-8.4.5.7",
                    $"Non-symbolic TrueType font \"{FontLabel(doc, leaf)}\" has " +
                    (named == null ? "no /Encoding or /BaseEncoding name" : $"encoding /{named}") +
                    "; §8.4.5.7 requires MacRomanEncoding or WinAnsiEncoding (ISO 14289-2 §8.4.5.7)."));
            }, () => found > IndividualReportLimit);

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.4.5.7",
                    $"…and at least one more TrueType encoding finding(s), not listed individually."));
        }

        /// <summary>
        /// ISO 14289-2:2024 §8.4.5.7, the two <c>cmap</c> sentences: "For all non-symbolic TrueType
        /// fonts used for rendering, the embedded TrueType font program shall contain at least the
        /// Microsoft Unicode (3, 1) … or the Macintosh Roman (1, 0) 'cmap' subtable"; and for
        /// symbolic TrueType fonts, "The 'cmap' subtable in the embedded font program shall either
        /// contain the Microsoft Symbol (3, 0) or the Mac Roman (1, 0) encoding."
        /// </summary>
        /// <remarks>
        /// <para>Read through <see cref="TrueTypeFontProgram.HasCmapSubtable"/>, which records every
        /// subtable the <c>cmap</c> table declares whatever its format. The non-symbolic sentence
        /// is scoped as written — fonts <i>used for rendering</i>, i.e. shown outside mode 3
        /// (<see cref="CollectGlyphUsage"/>); the symbolic sentence carries no such condition and
        /// is applied to every embedded symbolic TrueType font the resources reach. Both judge only
        /// a font that declares its flags, and only an embedded program that parses.</para>
        /// <para>The symbolic sentence prints its Mac Roman pair as "(1, 0 – Platform ID = 1,
        /// Encoding ID = 1)"; the pair and the non-symbolic sentence's own "(1, 0 – … Encoding ID =
        /// 0)" both say encoding 0, which is Mac Roman, so (1, 0) is what is checked.</para>
        /// </remarks>
        private static void CheckTrueTypeCmapSubtables(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            int found = 0;

            var fonts = new Dictionary<PdfDictionary, PdfFont>(PdfReferenceEquality.Instance);
            foreach (KeyValuePair<PdfDictionary, GlyphUsage> entry in CollectGlyphUsage(doc, fonts))
            {
                if (found > IndividualReportLimit) break;
                PdfDictionary font = entry.Key;
                if (!entry.Value.IsReferencedForRendering) continue;
                if ((Resolve(doc, font[PdfName.Subtype]) as PdfName)?.Value != "TrueType") continue;
                if (!DeclaresFontFlags(doc, font) || FontFlags.IsSymbolic(font, doc)) continue;

                TrueTypeFontProgram program = EmbeddedTrueTypeProgram(doc, font, "CheckTrueTypeCmapSubtables");
                if (program == null || TrueTypeFontRules.CmapConforms(program, symbolic: false, TrueTypeCmapRule.Iso32000Part2)) continue;

                found++;
                if (found > IndividualReportLimit) break;
                v.Add(new PdfUa2StructureViolation("UA2-8.4.5.7",
                    $"Non-symbolic TrueType font \"{FontLabel(doc, font)}\" is used for rendering, but its " +
                    "embedded program has neither a (3,1) Microsoft Unicode nor a (1,0) Macintosh Roman " +
                    "'cmap' subtable; §8.4.5.7 requires one (ISO 14289-2 §8.4.5.7)."));
            }

            WalkAllFontDescriptors(doc, (leaf, subtype, fd) =>
            {
                if (found > IndividualReportLimit) return;
                if (subtype != "TrueType" || !DeclaresFontFlags(doc, leaf) || !FontFlags.IsSymbolic(leaf, doc)) return;

                TrueTypeFontProgram program = EmbeddedTrueTypeProgram(doc, leaf, "CheckTrueTypeCmapSubtables");
                if (program == null || TrueTypeFontRules.CmapConforms(program, symbolic: true, TrueTypeCmapRule.Iso32000Part2)) return;

                found++;
                if (found > IndividualReportLimit) return;
                v.Add(new PdfUa2StructureViolation("UA2-8.4.5.7",
                    $"Symbolic TrueType font \"{FontLabel(doc, leaf)}\"'s embedded program has neither a (3,0) " +
                    "Microsoft Symbol nor a (1,0) Macintosh Roman 'cmap' subtable; §8.4.5.7 requires one " +
                    "(ISO 14289-2 §8.4.5.7)."));
            }, () => found > IndividualReportLimit);

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.4.5.7",
                    $"…and at least one more 'cmap' subtable finding(s), not listed individually."));
        }

        /// <summary>
        /// ISO 14289-2:2024 §8.4.5.7: "all non-symbolic TrueType fonts shall not define a
        /// Differences array unless all the glyph names in the Differences array are listed in the
        /// Adobe Glyph List and the embedded font program contains at least the Microsoft Unicode
        /// (3, 1 …) encoding in the 'cmap' subtable."
        /// </summary>
        /// <remarks>
        /// <para>AGL membership is <see cref="PdfGlyphNames.TryGetUnicode"/> — the one glyph-name
        /// table, which holds the full Adobe Glyph List (compound names included) plus the
        /// ZapfDingbats names, a few producer aliases and the <c>uniXXXX</c>/<c>uXXXX</c>
        /// conventions. Being a superset, it can only under-report, never report a real AGL name.
        /// </para>
        /// <para>The program half is judged only when a TrueType program is embedded and parses; a
        /// font with none is §8.4.5.5.1's finding, not this one's.</para>
        /// </remarks>
        private static void CheckNonSymbolicTrueTypeDifferences(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            int found = 0;
            WalkAllFontDescriptors(doc, (leaf, subtype, fd) =>
            {
                if (found > IndividualReportLimit) return;
                if (subtype != "TrueType" || !DeclaresFontFlags(doc, leaf) || FontFlags.IsSymbolic(leaf, doc)) return;

                if (!TrueTypeFontRules.HasDifferences(doc, leaf)) return;
                string nonAglName = TrueTypeFontRules.FirstDifferencesNameOutsideGlyphList(doc, leaf);

                string problem;
                if (nonAglName != null)
                    problem = $"its /Differences array names /{nonAglName}, which is not in the Adobe Glyph List";
                else
                {
                    TrueTypeFontProgram program = EmbeddedTrueTypeProgram(doc, leaf, "CheckNonSymbolicTrueTypeDifferences");
                    if (program == null || program.HasCmapSubtable(3, 1)) return;
                    problem = "it defines a /Differences array but its embedded program has no (3,1) Microsoft Unicode 'cmap' subtable";
                }

                found++;
                if (found > IndividualReportLimit) return;
                v.Add(new PdfUa2StructureViolation("UA2-8.4.5.7",
                    $"Non-symbolic TrueType font \"{FontLabel(doc, leaf)}\": {problem}; §8.4.5.7 allows " +
                    "Differences only with Adobe Glyph List names and a (3,1) subtable (ISO 14289-2 §8.4.5.7)."));
            }, () => found > IndividualReportLimit);

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.4.5.7",
                    $"…and at least one more /Differences finding(s), not listed individually."));
        }

        /// <summary>
        /// True when <paramref name="font"/>'s <c>/FontDescriptor</c> carries a numeric <c>/Flags</c>
        /// — so <see cref="FontFlags.IsSymbolic"/>'s answer is the file's statement, not the default
        /// it gives a font that says nothing.
        /// </summary>
        private static bool DeclaresFontFlags(PdfDocument doc, PdfDictionary font)
            => TrueTypeFontRules.DeclaresFlags(doc, font);

        /// <summary>
        /// The <c>/FontFile2</c> program under <paramref name="fontOrCidFont"/>'s descriptor, parsed —
        /// or null when there is none, or it does not parse (reported to
        /// <see cref="LastValidationError"/> when reading it threw).
        /// </summary>
        private static TrueTypeFontProgram EmbeddedTrueTypeProgram(
            PdfDocument doc, PdfDictionary fontOrCidFont, string operation)
        {
            TrueTypeFontProgram program = TrueTypeFontRules.EmbeddedProgram(doc, fontOrCidFont, out Exception error);
            if (error != null)
                Report("PdfUa2StructureChecker." + operation,
                    "an embedded TrueType program could not be read; it was not checked", error);
            return program;
        }

        /// <summary>
        /// The glyph a <c>CIDFontType0</c>'s CFF program draws for <paramref name="cid"/>: through the
        /// charset for a CID-keyed program (a CID is not a glyph index), the CID itself otherwise
        /// (<c>CffFont.IsCidKeyed</c>); −1 when the charset maps it to nothing.
        /// </summary>
        private static int CffGlyphOf(CffFont cff, int cid)
            => !cff.IsCidKeyed ? cid : cff.CharCodeToGid.TryGetValue(cid, out int gid) ? gid : -1;

        /// <summary>The OpenType wrapper of a <c>/FontFile3</c> CFF program, through <see cref="EmbeddedFontProgram.ReadOpenTypeWrapper"/>; null when there is none, or reported when reading threw.</summary>
        private static TrueTypeFontProgram OpenTypeWrapper(PdfDocument doc, PdfDictionary cidFont)
        {
            try { return EmbeddedFontProgram.ReadOpenTypeWrapper(cidFont, doc); }
            catch (Exception ex)
            {
                Report("PdfUa2StructureChecker.OpenTypeWrapper",
                    "an embedded OpenType program could not be read; its vertical metrics were not checked", ex);
                return null;
            }
        }

        private static bool TryMapTrueTypeCid(PdfType0Font font, TrueTypeFontProgram program,
            IReadOnlyDictionary<int, int> cidToGid, int cid, out int gid)
        {
            gid = 0;
            if (font.IsIdentityCidToGid) gid = cid;
            else if (cidToGid == null || !cidToGid.TryGetValue(cid, out gid)) return false;
            return gid >= 0 && gid < program.GlyphCount && gid <= ushort.MaxValue;
        }

        /// <summary>
        /// §8.4.5.6's vertical sentence: for every CID shown outside rendering mode 3 through a
        /// composite font in vertical writing mode, the vertical displacement <c>w1y</c> — its
        /// <c>/W2</c> entry, or <c>/DW2</c>'s second number (−1000 when absent) — must match the
        /// embedded program's <c>vmtx</c> advance height, negated (ISO 32000-2 §9.7.4.3: a negative
        /// <c>w1y</c> places the next glyph below). The <c>vmtx</c> is the TrueType program's or a CFF
        /// program's OpenType wrapper's; the caller supplies the glyph each CID selects (−1 for none).
        /// </summary>
        /// <remarks>
        /// <para><b>Only the displacement is compared.</b> The position vector's counterpart in the
        /// program is not <c>vmtx</c> — that holds a top side bearing, not an origin — so comparing
        /// <c>v</c> would be a guess. The <c>/W2</c> lookup is <see cref="CidVerticalMetrics.TryFind"/>,
        /// the renderers' own, so the check reads the metrics a reader draws with.</para>
        /// <para><b>Judged over the CIDs shown</b>, not every <c>/W2</c> entry: a range entry may name
        /// tens of thousands of CIDs, and a glyph never shown cannot be inconsistent in any rendering.</para>
        /// </remarks>
        private static void CheckVerticalMetrics(PdfDocument doc, PdfDictionary cidFont, PdfType0Font font,
            TrueTypeFontProgram program, Func<int, int> glyphOf, GlyphUsage usage,
            List<PdfUa2StructureViolation> v, ref int found)
        {
            var w2 = Resolve(doc, cidFont[PdfName.W2]) as PdfArray;
            // Read once: the array was scanned for every CID shown.
            var w2Index = w2 == null ? null : CidVerticalMetrics.Index.Read(w2, o => Resolve(doc, o));
            double defaultW1y = CidVerticalMetrics.DefaultDisplacementY;
            // The elements resolved, as the renderers' FontCache resolves them — a `/DW2 [880 7 0 R]`
            // read as stored judged the font against the −1000 default instead of what it declares.
            if (Resolve(doc, cidFont[PdfName.DW2]) is PdfArray dw2 && dw2.Count >= 2 && Resolve(doc, dw2[1]) is PdfNumber dwNumber)
                defaultW1y = dwNumber.DoubleValue;

            var cids = new List<int>(usage.Rendered);
            cids.Sort();
            foreach (int cid in cids)
            {
                if (found > IndividualReportLimit) return;
                int gid = glyphOf(cid);
                if (gid < 0 || !program.TryGetVerticalAdvance(gid, out int advanceHeight)) continue;

                float w1y = 0;
                bool explicitEntry = w2Index != null && w2Index.TryFind(cid, out w1y, out _, out _);
                double declared = explicitEntry ? w1y : defaultW1y;
                if (Math.Abs(declared - (-advanceHeight)) <= 1) continue;

                found++;
                if (found > IndividualReportLimit) return;
                v.Add(new PdfUa2StructureViolation("UA2-8.4.5.6",
                    $"CID {cid}'s vertical displacement ({declared:0.##}, from {(explicitEntry ? "/W2" : "/DW2")}) " +
                    $"in composite font \"{font.BaseFont}\", used in vertical writing mode, does not match its " +
                    $"embedded font program's vertical advance ({-advanceHeight}) for glyph {gid}; §8.4.5.6 " +
                    "allows at most 1/1000 unit of difference (ISO 14289-2 §8.4.5.6)."));
            }
        }

        /// <summary>
        /// The <c>/FontFile3</c> CFF program under <paramref name="cidFont"/>'s descriptor, through
        /// <see cref="EmbeddedFontProgram.ReadCff"/> — or null when there is none or it does not parse
        /// (reported when reading it threw).
        /// </summary>
        private static CffFont EmbeddedCffProgram(PdfDocument doc, PdfDictionary cidFont, string operation)
        {
            try { return EmbeddedFontProgram.ReadCff(cidFont, doc); }
            catch (Exception ex)
            {
                Report("PdfUa2StructureChecker." + operation,
                    "an embedded CFF program could not be read; it was not checked", ex);
                return null;
            }
        }

        private static string FontLabel(PdfDocument doc, PdfDictionary font)
            => (Resolve(doc, font[PdfName.BaseFont]) as PdfName)?.Value ?? "(unnamed)";

        // ----------------------------------------------------------------
        //  Clause 8.4.5.3.1 — Type 0 font / descendant CIDSystemInfo relationship
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.4.5.3.1: for a Type 0 font, the <c>CIDSystemInfo</c> entry in its
        /// CIDFont dictionary and its <c>Encoding</c> dictionary shall have the following
        /// relationship: (a) if <c>/Encoding</c> is <c>Identity-H</c>/<c>Identity-V</c>, any
        /// values may be used; (b) otherwise, the corresponding <c>Registry</c>/<c>Ordering</c>
        /// values in both <c>CIDSystemInfo</c> dictionaries shall be identical, and the CIDFont's
        /// <c>Supplement</c> shall be greater than or equal to the CMap's.
        /// </summary>
        /// <remarks>
        /// <para><b>Only the embedded-CMap-stream form of clause (b) is checked.</b> When
        /// <c>/Encoding</c> is a <i>name</i> other than <c>Identity-H</c>/<c>Identity-V</c>, it
        /// names a predefined CMap (Table 116) whose own Registry/Ordering/Supplement are not
        /// written anywhere in the file — they are defined externally, by Adobe's CMap resource
        /// registry — and this checker has no such lookup table. When <c>/Encoding</c> is a
        /// <i>stream</i> instead, the embedded CMap carries its own <c>/CIDSystemInfo</c> entry
        /// directly (§9.7.5.3, Table 118), so the comparison is a pure dictionary read with
        /// nothing external to consult. A named predefined CMap is therefore skipped rather than
        /// guessed at — the same "cannot verify, so say nothing" direction every other
        /// unsupportable case in this file takes.</para>
        /// <para>Not reached through <see cref="WalkAllFontDescriptors"/>: that helper calls
        /// back only on the terminal CIDFont, never the Type 0 wrapper whose own
        /// <c>/Encoding</c> this clause needs alongside it, so this check walks page resources
        /// directly (still through the shared <c>PdfXValidator.ScanPage</c>) rather than
        /// stretching a helper built for a different shape.</para>
        /// </remarks>
        private static void CheckCidSystemInfoRelationship(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            int found = 0;
            WalkAllTopLevelFonts(doc, (fontObj, subtype) =>
            {
                if (found > IndividualReportLimit) return;
                if (subtype != "Type0") return;

                var encoding = Resolve(doc, fontObj[PdfName.Encoding]);
                if (encoding is PdfName encName
                    && (encName.Value == "Identity-H" || encName.Value == "Identity-V"))
                    return;
                if (!(encoding is PdfStream cmapStream)) return; // named predefined CMap

                var cmapInfo = Resolve(doc, cmapStream[PdfName.CIDSystemInfo]) as PdfDictionary;
                if (cmapInfo == null) return;

                var descendants = Resolve(doc, fontObj[PdfName.DescendantFonts]) as PdfArray;
                var cidFont = descendants != null && descendants.Count > 0
                    ? Resolve(doc, descendants[0]) as PdfDictionary
                    : null;
                var cidInfo = cidFont != null
                    ? Resolve(doc, cidFont[PdfName.CIDSystemInfo]) as PdfDictionary
                    : null;
                if (cidInfo == null) return;

                string cmapRegistry = (Resolve(doc, cmapInfo[PdfName.Registry]) as PdfString)?.GetText();
                string cmapOrdering = (Resolve(doc, cmapInfo[PdfName.Ordering]) as PdfString)?.GetText();
                int? cmapSupplement = (Resolve(doc, cmapInfo[PdfName.Supplement]) as PdfNumber)?.IntValue;
                string cidRegistry = (Resolve(doc, cidInfo[PdfName.Registry]) as PdfString)?.GetText();
                string cidOrdering = (Resolve(doc, cidInfo[PdfName.Ordering]) as PdfString)?.GetText();
                int? cidSupplement = (Resolve(doc, cidInfo[PdfName.Supplement]) as PdfNumber)?.IntValue;

                if (cmapRegistry == null || cmapOrdering == null || cmapSupplement == null
                    || cidRegistry == null || cidOrdering == null || cidSupplement == null)
                    return;

                bool ok = cmapRegistry == cidRegistry && cmapOrdering == cidOrdering
                       && cidSupplement.Value >= cmapSupplement.Value;
                if (ok) return;

                found++;
                if (found > IndividualReportLimit) return;
                v.Add(new PdfUa2StructureViolation("UA2-8.4.5.3.1",
                    "A Type 0 font's CIDSystemInfo does not have the required relationship " +
                    "with its embedded CMap's CIDSystemInfo (matching /Registry and " +
                    "/Ordering, and a /Supplement at least as high); §8.4.5.3.1 requires it " +
                    "(ISO 14289-2 §8.4.5.3.1)."));
            }, () => found > IndividualReportLimit);

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.4.5.3.1",
                    $"…and at least one more, not listed individually."));
        }

        /// <summary>
        /// Invokes <paramref name="onFont"/> once per <b>top-level</b> font dictionary reachable
        /// from any page's resources — unlike <see cref="WalkAllFontDescriptors"/>, this does not
        /// descend into a Type 0 font's <c>/DescendantFonts</c>, for checks that need an entry
        /// living on the wrapper itself (<c>/Encoding</c>, <c>/ToUnicode</c>) rather than on the
        /// terminal CIDFont, and that need every subtype including Type 3 (which
        /// <see cref="WalkAllFontDescriptors"/> skips outright, since Type 3 has no font program
        /// to have a descriptor for).
        /// </summary>
        /// <remarks>
        /// Same <c>ScanPage</c> + document-scoped de-duplication shape as
        /// <see cref="WalkAllFontDescriptors"/>, extracted the moment a second caller needed the
        /// top-level-only version rather than duplicating the page/resource nesting a third time.
        /// </remarks>
        private static void WalkAllTopLevelFonts(
            PdfDocument doc, Action<PdfDictionary, string> onFont, Func<bool> shouldStop)
        {
            if (doc.Pages == null) return;

            var seenFonts = new HashSet<PdfDictionary>();
            long documentBytes = 0;

            for (int p = 0; p < doc.Pages.Count; p++)
            {
                if (shouldStop()) break;

                var scan = PdfXValidator.ScanPage(doc, doc.Pages[p],
                    PdfXValidator.DefaultMaxPageContentBytes, ref documentBytes,
                    PdfXValidator.DefaultMaxDocumentContentBytes, collectContent: false);

                foreach (PdfDictionary res in scan.Resources)
                {
                    if (shouldStop()) break;

                    var fontDict = Resolve(doc, res[PdfName.Font]) as PdfDictionary;
                    if (fontDict == null) continue;

                    foreach (KeyValuePair<PdfName, PdfObject> kvp in fontDict)
                    {
                        if (shouldStop()) break;

                        var fontObj = Resolve(doc, kvp.Value) as PdfDictionary;
                        if (fontObj == null) continue;
                        if (!seenFonts.Add(fontObj)) continue;

                        string subtype = (Resolve(doc, fontObj[PdfName.Subtype]) as PdfName)?.Value;
                        onFont(fontObj, subtype);
                    }
                }
            }
        }

        // ----------------------------------------------------------------
        //  Clause 8.4.5.8 — ToUnicode values must not be zero, U+FEFF or U+FFFE
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.4.5.8: "If a ToUnicode CMap is present, the Unicode values it
        /// specifies shall all be greater than zero (0), but not equal to either U+FEFF or
        /// U+FFFE."
        /// </summary>
        /// <remarks>
        /// <para>This is the clause's unconditional sentence: <i>when</i> a <c>/ToUnicode</c> CMap
        /// exists, its destination values are checkable outright. The presence-and-coverage rule,
        /// with its four exceptions, is <see cref="CheckToUnicodeMapsReferencedGlyphs"/>.</para>
        /// <para>Reuses <c>Core.Fonts.CMapParser.Parse</c> — already reachable, since Core's
        /// <c>InternalsVisibleTo</c> already names this assembly — rather than writing a second
        /// bfchar/bfrange CMap-stream parser. <c>WalkAllTopLevelFonts</c> is used rather than
        /// <see cref="WalkAllFontDescriptors"/> because <c>/ToUnicode</c> lives on the font
        /// dictionary a reader actually indexes by character code, including Type 3, which the
        /// descriptor walk skips outright.</para>
        /// </remarks>
        private static void CheckToUnicodeValuesValid(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            int found = 0;
            WalkAllTopLevelFonts(doc, (fontObj, subtype) =>
            {
                if (found > IndividualReportLimit) return;

                if (!(Resolve(doc, fontObj[PdfName.ToUnicode]) is PdfStream cmapStream)) return;

                byte[] bytes;
                try { bytes = FontStreamBytes.CMap(cmapStream); }
                catch { return; } // cannot decode — cannot support a claim about its content

                Dictionary<int, string> map;
                try { map = CMapParser.Parse(bytes); }
                catch { return; }

                foreach (string unicode in map.Values)
                {
                    if (found > IndividualReportLimit) break;
                    if (string.IsNullOrEmpty(unicode)) continue;

                    bool invalid = false;
                    foreach (char c in unicode)
                        if (c == '\u0000' || c == '﻿' || c == '￾') { invalid = true; break; }
                    if (!invalid) continue;

                    found++;
                    if (found > IndividualReportLimit) break;
                    v.Add(new PdfUa2StructureViolation("UA2-8.4.5.8",
                        "A font's /ToUnicode CMap maps a character code to U+0000, U+FEFF or " +
                        "U+FFFE; §8.4.5.8 prohibits all three (ISO 14289-2 §8.4.5.8)."));
                }
            }, () => found > IndividualReportLimit);

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.4.5.8",
                    $"…and at least one more, not listed individually."));
        }

        /// <summary>
        /// ISO 14289-2:2024 §8.4.5.8: "The font dictionary of all fonts, regardless of their
        /// rendering mode usage, shall include a ToUnicode entry whose value is a CMap stream object
        /// that maps character codes for at least all referenced glyphs to Unicode values … unless
        /// the font meets at least one of the following four conditions: it uses the predefined
        /// encodings MacRomanEncoding, MacExpertEncoding or WinAnsiEncoding; it is a Type 1 or Type
        /// 3 font where the glyph names of the glyphs referenced are all contained in the Adobe Glyph
        /// List, or the set of named characters in the Symbol font; it is a Type 0 font whose
        /// descendant CIDFont uses the Adobe-GB1, Adobe-CNS1, Adobe-Japan1 or Adobe-KR-9 character
        /// collections; it is a non-symbolic TrueType font." Shares <c>UA2-8.4.5.8</c> with
        /// <see cref="CheckToUnicodeValuesValid"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>Judged per referenced font, over the codes it shows</b> —
        /// <see cref="CollectGlyphUsage"/>'s codes in any rendering mode, as the clause says. A font
        /// listed in resources but never shown references no glyph and is not judged. When no
        /// exception applies, a font with no <c>/ToUnicode</c> stream is reported; one with a
        /// <c>/ToUnicode</c> is reported for the first shown code its CMap does not map (a map the
        /// parser truncated at its budget is not read as missing codes).</para>
        /// <para><b>The exceptions, and where they cannot be decided.</b>
        /// <see cref="ToUnicodeExemptionOf"/> answers exempt, not exempt, or cannot-determine, and
        /// only "not exempt" is ever reported. A predefined encoding is the <c>/Encoding</c> name, or
        /// an encoding dictionary whose only content is such a <c>/BaseEncoding</c> — one with
        /// <c>/Differences</c> no longer uses the predefined encoding. A non-symbolic TrueType font
        /// must declare its <c>/Flags</c>. A Type 0 font's collection is its descendant's
        /// <c>/CIDSystemInfo</c> (Registry <c>Adobe</c>; Ordering <c>GB1</c>, <c>CNS1</c>,
        /// <c>Japan1</c> or <c>KR</c>). For Type 1 and Type 3, a shown code's glyph name is its
        /// <c>/Differences</c> name when it has one — judged through <see cref="PdfGlyphNames"/>,
        /// whose table is a superset of the glyph list — and otherwise comes from a standard base
        /// encoding (StandardEncoding, WinAnsi, MacRoman, MacExpert), all of whose names are glyph-list
        /// names; a code in no such table, a symbolic Type 1 font's built-in encoding, and
        /// ZapfDingbats are cannot-determine. The standard-14 Symbol font's names are the Symbol set
        /// the exception names, so it is exempt.</para>
        /// </remarks>
        private static void CheckToUnicodeMapsReferencedGlyphs(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var fonts = new Dictionary<PdfDictionary, PdfFont>(PdfReferenceEquality.Instance);
            int found = 0;

            foreach (KeyValuePair<PdfDictionary, GlyphUsage> entry in CollectGlyphUsage(doc, fonts))
            {
                if (found > IndividualReportLimit) break;
                PdfDictionary font = entry.Key;
                if (entry.Value.ReferencedCodes.Count == 0) continue;
                if (ToUnicodeExemptionOf(doc, font, entry.Value, fonts) != ToUnicodeExemption.NotExempt) continue;

                string subtype = (Resolve(doc, font[PdfName.Subtype]) as PdfName)?.Value ?? "(no subtype)";
                if (!(Resolve(doc, font[PdfName.ToUnicode]) is PdfStream cmapStream))
                {
                    found++;
                    if (found > IndividualReportLimit) break;
                    v.Add(new PdfUa2StructureViolation("UA2-8.4.5.8",
                        $"Font \"{FontLabel(doc, font)}\" (/{subtype}) is shown but has no /ToUnicode CMap, " +
                        "and meets none of §8.4.5.8's exceptions (a predefined encoding; Type 1/Type 3 glyph " +
                        "names all in the Adobe Glyph List; an Adobe CJK character collection; a non-symbolic " +
                        "TrueType font) (ISO 14289-2 §8.4.5.8)."));
                    continue;
                }

                Dictionary<int, string> map;
                try { map = CMapParser.Parse(FontStreamBytes.CMap(cmapStream)); }
                catch (Exception ex)
                {
                    Report("PdfUa2StructureChecker.CheckToUnicodeMapsReferencedGlyphs",
                        "a /ToUnicode CMap could not be read; its coverage was not checked", ex);
                    continue;
                }
                if (map == null || CMapParser.LastBudgetError != null) continue;

                var codes = new List<int>(entry.Value.ReferencedCodes);
                codes.Sort();
                foreach (int code in codes)
                {
                    if (map.TryGetValue(code, out string unicode) && !string.IsNullOrEmpty(unicode)) continue;

                    found++;
                    if (found > IndividualReportLimit) break;
                    v.Add(new PdfUa2StructureViolation("UA2-8.4.5.8",
                        $"Font \"{FontLabel(doc, font)}\" (/{subtype})'s /ToUnicode CMap does not map character " +
                        $"code {code}, which is shown; §8.4.5.8 requires it to map at least every referenced " +
                        "glyph (ISO 14289-2 §8.4.5.8)."));
                    break; // one coverage finding per font
                }
            }

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.4.5.8",
                    $"…and at least one more /ToUnicode finding(s), not listed individually."));
        }

        private enum ToUnicodeExemption { NotExempt, Exempt, CannotDetermine }

        /// <summary>Which of §8.4.5.8's four exceptions, if any, <paramref name="font"/> meets —
        /// see <see cref="CheckToUnicodeMapsReferencedGlyphs"/>'s remarks for each reading.</summary>
        private static ToUnicodeExemption ToUnicodeExemptionOf(
            PdfDocument doc, PdfDictionary font, GlyphUsage usage, Dictionary<PdfDictionary, PdfFont> fonts)
        {
            string subtype = (Resolve(doc, font[PdfName.Subtype]) as PdfName)?.Value;

            PdfObject encoding = Resolve(doc, font[PdfName.Encoding]);
            var encodingDict = encoding as PdfDictionary;
            string baseEncoding = (encoding as PdfName)?.Value
                ?? (Resolve(doc, encodingDict?[PdfName.BaseEncoding]) as PdfName)?.Value;
            if (subtype != "Type0" && (encodingDict == null || encodingDict[PdfName.Differences] == null)
                && (baseEncoding == "MacRomanEncoding" || baseEncoding == "MacExpertEncoding" || baseEncoding == "WinAnsiEncoding"))
                return ToUnicodeExemption.Exempt;

            switch (subtype)
            {
                case "TrueType":
                    if (!DeclaresFontFlags(doc, font)) return ToUnicodeExemption.CannotDetermine;
                    return FontFlags.IsSymbolic(font, doc) ? ToUnicodeExemption.NotExempt : ToUnicodeExemption.Exempt;

                case "Type0":
                {
                    var descendants = Resolve(doc, font[PdfName.DescendantFonts]) as PdfArray;
                    var cidFont = descendants != null && descendants.Count > 0
                        ? Resolve(doc, descendants[0]) as PdfDictionary : null;
                    var systemInfo = Resolve(doc, cidFont?[PdfName.CIDSystemInfo]) as PdfDictionary;
                    string registry = TextOf(doc, systemInfo?[PdfName.Registry]);
                    string ordering = TextOf(doc, systemInfo?[PdfName.Ordering]);
                    if (registry == null || ordering == null) return ToUnicodeExemption.CannotDetermine;
                    return registry == "Adobe" && (ordering == "GB1" || ordering == "CNS1" || ordering == "Japan1" || ordering == "KR")
                        ? ToUnicodeExemption.Exempt : ToUnicodeExemption.NotExempt;
                }

                case "Type1":
                case "MMType1":
                case "Type3":
                    return GlyphNamesExemption(doc, font, subtype, baseEncoding, usage, fonts);

                default:
                    return ToUnicodeExemption.CannotDetermine;
            }
        }

        private static ToUnicodeExemption GlyphNamesExemption(
            PdfDocument doc, PdfDictionary font, string subtype, string baseEncoding,
            GlyphUsage usage, Dictionary<PdfDictionary, PdfFont> fonts)
        {
            if (!(CachedFont(doc, font, fonts) is PdfSimpleFont simple)) return ToUnicodeExemption.CannotDetermine;

            // The standard-14 Symbol font needs no case of its own: Core never reads /Encoding for it
            // (§9.6.6.4 — its built-in encoding always wins), so it has no /Differences names, and every
            // code falls to a standard base table or to cannot-determine — never to "not exempt". Its
            // names are the Symbol set the exception names, so that is the right outcome; a line
            // answering Exempt for it would change no result.
            if (simple.BaseFont == StandardFonts.ZapfDingbats || simple.BaseFont == StandardFonts.ZapfDingbatsAbbrev)
                return ToUnicodeExemption.CannotDetermine;

            // The base encoding whose names a code outside /Differences takes — only a standard table,
            // never GetEncodingByName's WinAnsi fallback for an unknown name. With none named, a
            // non-symbolic Type 1 font's implicit base is StandardEncoding (§9.6.6.4); a symbolic one's
            // is its built-in encoding, and a Type 3 font has none.
            string baseName = baseEncoding is "StandardEncoding" or "WinAnsiEncoding" or "MacRomanEncoding" or "MacExpertEncoding"
                ? baseEncoding
                : baseEncoding == null && subtype != "Type3" && DeclaresFontFlags(doc, font) && !FontFlags.IsSymbolic(font, doc)
                    ? "StandardEncoding"
                    : null;
            Dictionary<int, char> baseTable = baseName != null ? StandardEncodings.GetEncodingByName(baseName) : null;

            bool undetermined = false;
            foreach (int code in usage.ReferencedCodes)
            {
                if (simple.DifferenceGlyphNames.TryGetValue(code, out string name))
                {
                    if (!PdfGlyphNames.TryGetUnicode(name, out _)) return ToUnicodeExemption.NotExempt;
                    continue;
                }
                if (baseTable != null && baseTable.ContainsKey(code)) continue;
                undetermined = true;
            }
            return undetermined ? ToUnicodeExemption.CannotDetermine : ToUnicodeExemption.Exempt;
        }

        private static string TextOf(PdfDocument doc, PdfObject value)
        {
            PdfObject resolved = Resolve(doc, value);
            return (resolved as PdfString)?.GetText() ?? (resolved as PdfName)?.Value;
        }

        // ----------------------------------------------------------------
        //  Clause 8.10.2.4 — TU entry: multi-widget fields need a label or Contents per widget
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.10.2.4: "When a form field dictionary has multiple widget
        /// annotations, a TU entry is often insufficient to provide full context for each
        /// annotation, thus, a label, a Contents entry, or both, shall be present for each
        /// annotation to provide additional context."
        /// </summary>
        /// <remarks>
        /// <para>A field dictionary "has multiple widget annotations" when its own <c>/Kids</c>
        /// array names two or more entries that each resolve to a Widget-subtype annotation
        /// dictionary directly (Table 220's merged field/widget shape), not further child
        /// fields — collected by <see cref="CollectMultiWidgetFieldWidgets"/>, a small recursive
        /// walk of <c>/AcroForm /Fields</c> depth-capped at
        /// <see cref="PdfLimits.MaxFieldInheritanceDepth"/> with a
        /// <see cref="PdfReferenceEquality"/>-keyed visited set, since a field tree can be
        /// pathologically deep or cyclic the same way every other tree this checker walks can be.
        /// </para>
        /// <para><b>"A label" is read as §8.10.2.2 defines it</b>: real content enclosed in one or
        /// more <c>Lbl</c> structure elements that are themselves a <i>direct</i> child of the
        /// same <c>Form</c> structure element that also encloses the widget's own <c>/OBJR</c> —
        /// not any ancestor <c>Lbl</c>, matching §8.10.2.2's own wording ("a direct descendent of
        /// a Form structure element that also includes the object reference"). Found via
        /// <see cref="NearestFormAncestor"/> (the same upward walk §8.10.1's widget-count check
        /// already uses) plus <see cref="GetChildRoles"/> on that Form node's own direct
        /// <c>/K</c>.</para>
        /// <para><b>"A Contents entry" is read directly off the widget annotation's own
        /// dictionary</b>, via the same <see cref="HasNonEmptyString"/> presence-not-quality
        /// reading every earlier clause in this file gives a text property. The two escapes are
        /// independent: a widget with no <c>/OBJR</c> anywhere in the tree (or no structure tree
        /// at all) still satisfies the clause through a <c>/Contents</c> entry alone.</para>
        /// <para>Only this one sentence — a label or a Contents entry present per widget — is
        /// checked. §8.10.2.4's surrounding text about when a <c>TU</c> entry alone already
        /// provides enough context turns on judging how much a sighted user already understands,
        /// a semantic call this checker makes nowhere else. A widget kid with no indirect
        /// reference of its own cannot be named by an <c>/OBJR</c>, so it is excluded from this
        /// check entirely — the same "no object number, unprovable" reasoning
        /// <see cref="CollectAnnotationObjectNumbers"/> already applies elsewhere in this file.
        /// </para>
        /// </remarks>
        private static void CheckMultiWidgetFieldHasLabelOrContents(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var widgetsByObjectNumber = new Dictionary<int, PdfDictionary>();
            HashSet<int> widgetObjectNumbers = CollectMultiWidgetFieldWidgets(doc, catalog, widgetsByObjectNumber);
            if (widgetObjectNumbers.Count == 0) return;

            var structTreeRoot = ResolveStructTreeRoot(doc, catalog);
            var hasLabel = new HashSet<int>();
            if (structTreeRoot != null)
            {
                StructTreeWalker.Walk(structTreeRoot, doc, index: null, node =>
                {
                    if (node.Dict == null) return;

                    foreach (PdfObject rawKid in StructTreeWalker.GetRawKids(node.Dict, doc))
                    {
                        var objrDict = Resolve(doc, rawKid) as PdfDictionary;
                        if (objrDict == null) continue;
                        if (StructKidClassifier.Classify(objrDict, doc) != StructKidKind.ObjectReference) continue;
                        if (!(objrDict[PdfName.Obj] is PdfReference objRef)) continue;
                        if (!widgetObjectNumbers.Contains(objRef.ReferenceObjectNumber)) continue;

                        StructNode form = NearestFormAncestor(node);
                        if (form == null) continue;
                        if (GetChildRoles(doc, form.Dict).Contains("Lbl"))
                            hasLabel.Add(objRef.ReferenceObjectNumber);
                    }
                });
            }

            int found = 0;
            foreach (int objNum in widgetObjectNumbers)
            {
                if (hasLabel.Contains(objNum)) continue;
                if (HasNonEmptyString(doc, widgetsByObjectNumber[objNum], PdfName.Contents)) continue;

                found++;
                if (found > IndividualReportLimit) continue;
                v.Add(new PdfUa2StructureViolation("UA2-8.10.2.4",
                    $"Widget annotation object {objNum} belongs to a field with multiple widget " +
                    "annotations but has neither a label (a Lbl structure element directly under " +
                    "its enclosing Form element) nor a /Contents entry; §8.10.2.4 requires one " +
                    "(ISO 14289-2 §8.10.2.4)."));
            }

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.10.2.4",
                    $"…and {found - IndividualReportLimit} more, not listed individually."));
        }

        /// <summary>
        /// Recursively collects the object numbers of every widget annotation that is a direct
        /// <c>/Kids</c> entry of a field dictionary naming two or more such widgets — the "field
        /// dictionary has multiple widget annotations" condition §8.10.2.4 turns on. Recurses into
        /// every kid's own <c>/Kids</c> regardless of whether the current level qualified, since a
        /// multi-widget field can nest arbitrarily deep below a single-widget or non-terminal one.
        /// </summary>
        private static HashSet<int> CollectMultiWidgetFieldWidgets(
            PdfDocument doc, PdfDictionary catalog, Dictionary<int, PdfDictionary> widgetsByObjectNumber)
        {
            var result = new HashSet<int>();
            var acroForm = Resolve(doc, catalog[PdfName.AcroForm]) as PdfDictionary;
            var fields = Resolve(doc, acroForm?[PdfName.Fields]) as PdfArray;
            if (fields == null) return result;

            var visited = new HashSet<PdfDictionary>(PdfReferenceEquality.Instance);

            void Walk(PdfArray kids, int depth)
            {
                if (kids == null || depth > PdfLimits.MaxFieldInheritanceDepth) return;

                var resolvedKids = new List<(PdfReference Ref, PdfDictionary Dict)>();
                bool allWidgets = kids.Count >= 2;
                foreach (PdfObject raw in kids)
                {
                    var dict = Resolve(doc, raw) as PdfDictionary;
                    if (dict == null) { allWidgets = false; continue; }
                    resolvedKids.Add((raw as PdfReference, dict));
                    if ((Resolve(doc, dict[PdfName.Subtype]) as PdfName)?.Value != PdfAnnotationSubtypes.Widget)
                        allWidgets = false;
                }

                if (allWidgets)
                {
                    foreach ((PdfReference r, PdfDictionary d) in resolvedKids)
                    {
                        if (r == null) continue;
                        result.Add(r.ReferenceObjectNumber);
                        widgetsByObjectNumber[r.ReferenceObjectNumber] = d;
                    }
                }

                foreach ((PdfReference _, PdfDictionary d) in resolvedKids)
                {
                    if (!visited.Add(d)) continue;
                    Walk(Resolve(doc, d[PdfName.Kids]) as PdfArray, depth + 1);
                }
            }

            Walk(fields, 0);
            return result;
        }

        // ----------------------------------------------------------------
        //  Clause 8.4.5.4 — CMaps: an embedded CMap's /WMode must match its own program
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.4.5.4: "For those CMaps that are embedded, the integer value of
        /// the WMode entry in the CMap dictionary shall be identical to the WMode value in the
        /// embedded CMap stream."
        /// </summary>
        /// <remarks>
        /// <para><b>Only this one sentence is checked</b> — deliberately not the clause's first
        /// sentence ("All CMaps used … except those listed in [Table 116] … shall be embedded"),
        /// which would need the same predefined-CMap-name lookup table
        /// <see cref="CheckCidSystemInfoRelationship"/>'s own remarks already say this checker
        /// carries none of, and for the same reason: Table 116's names are external to any file,
        /// so telling a legitimate predefined name from a made-up one needs a list this checker
        /// does not have. This sentence needs no such list: it applies only once <c>/Encoding</c>
        /// is already a stream (an embedded CMap), comparing two values both present in the file
        /// itself.</para>
        /// <para>Reuses <see cref="WalkAllTopLevelFonts"/> (the same top-level, not-descended-into
        /// walk §8.4.5.3.1's own check already uses to reach a Type 0 font's own
        /// <c>/Encoding</c>), filtered to <c>Type0</c> the same way. The dictionary-side value
        /// defaults to 0 when the <c>/WMode</c> entry itself is absent (ISO 32000-2:2020 Table
        /// 114/115's own default), matching every other "absent means the spec's own default, not
        /// unprovable" reading in this file. The program-side value is read by
        /// <see cref="ReadWModeFromCMapProgram"/>, a small text scan for the CMap program's own
        /// <c>/WMode … def</c> declaration (ISO 32000-2:2020, 9.7.5.3) — a new, narrowly-scoped
        /// reader rather than an addition to the shared <c>Core.Fonts.CMapParser</c>, since the
        /// only fact needed is one integer, not a second general-purpose parse of a file this
        /// checker already reads for a different clause's purposes elsewhere. A CMap program with
        /// no <c>/WMode</c> declaration of its own is read as "cannot verify" and skipped, not
        /// guessed at — the CMap's <i>program</i>, unlike its dictionary, has no documented
        /// default this checker can rely on.</para>
        /// </remarks>
        private static void CheckEmbeddedCMapWModeConsistent(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            int found = 0;
            WalkAllTopLevelFonts(doc, (fontObj, subtype) =>
            {
                if (found > IndividualReportLimit) return;
                if (subtype != "Type0") return;

                if (!(Resolve(doc, fontObj[PdfName.Encoding]) is PdfStream cmapStream)) return;

                int dictWMode = (Resolve(doc, cmapStream[PdfName.WMode]) as PdfNumber)?.IntValue ?? 0;

                byte[] data;
                try { data = FontStreamBytes.CMap(cmapStream); }
                catch { return; }

                int? programWMode = ReadWModeFromCMapProgram(data);
                if (!programWMode.HasValue || programWMode.Value == dictWMode) return;

                found++;
                if (found > IndividualReportLimit) return;
                v.Add(new PdfUa2StructureViolation("UA2-8.4.5.4",
                    $"An embedded CMap's /WMode dictionary entry ({dictWMode}) does not match " +
                    $"the WMode declared inside the CMap program ({programWMode.Value}); " +
                    "§8.4.5.4 requires them to be identical (ISO 14289-2 §8.4.5.4)."));
            }, () => found > IndividualReportLimit);

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.4.5.4",
                    $"…and at least one more, not listed individually."));
        }

        /// <summary>
        /// Reads the integer operand of a CMap program's own <c>/WMode … def</c> declaration
        /// (ISO 32000-2:2020, 9.7.5.3) directly from the decoded stream bytes — a CMap program is
        /// PostScript-like text, not a PDF dictionary, so there is no object model to resolve it
        /// through. Returns <see langword="null"/> when no such declaration is found (a malformed
        /// or unusual CMap program), never a guessed default.
        /// </summary>
        private static int? ReadWModeFromCMapProgram(byte[] data)
        {
            string text = Encoding.ASCII.GetString(data);
            int idx = text.IndexOf("/WMode", StringComparison.Ordinal);
            if (idx < 0) return null;
            idx += "/WMode".Length;

            while (idx < text.Length && char.IsWhiteSpace(text[idx])) idx++;
            int start = idx;
            if (idx < text.Length && text[idx] == '-') idx++;
            while (idx < text.Length && char.IsDigit(text[idx])) idx++;
            if (idx == start) return null;

            return int.TryParse(text.Substring(start, idx - start), out int wMode) ? wMode : (int?)null;
        }

        // ----------------------------------------------------------------
        //  Clause 8.6 — Text string objects must not use the Unicode PUA
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.6: "Text strings … intended to be human readable shall not use the
        /// Unicode PUA."
        /// </summary>
        /// <remarks>
        /// <para><b>Outline (bookmark) <c>/Title</c> entries</b> — the clause's own worked example
        /// ("Bookmarks (outlines) are an example of a user-facing text string"). The other text strings
        /// read under §8.6 are those whose definition makes them human-readable — a structure element's
        /// <c>/Alt</c>, <c>/ActualText</c>, <c>/E</c> and <c>/T</c>, an annotation's <c>/Contents</c> —
        /// in <see cref="CheckElementAndAnnotationTextNoPua"/>. A sweep of every text string would have
        /// to decide, for each one, whether it is human-facing at all (an annotation's internal
        /// <c>/NM</c> name, say, is not), which is the kind of intent judgment this file declines to
        /// make.</para>
        /// <para>Walks the catalog's `/Outlines` tree via `/First`/`/Next` — a small bespoke walk
        /// (mirroring <see cref="CollectMultiWidgetFieldWidgets"/>'s shape: a
        /// <see cref="PdfReferenceEquality"/>-keyed visited set for the cycle guard, and
        /// <see cref="PdfLimits.MaxTreeDepth"/> as the depth cap) rather than
        /// <see cref="StructTreeWalker"/>, which walks the structure tree, not the outline tree —
        /// two different §7.9-adjacent trees the spec itself does not share a traversal between.
        /// Depth increases only descending via `/First`; a `/Next` chain of siblings does not
        /// deepen the count, so a long flat outline (hundreds of bookmarks at one level) is not
        /// mistaken for a pathologically deep one and cut short.</para>
        /// <para>Checked via <see cref="ContainsUnicodePua"/> against the BMP Private Use Area
        /// (U+E000–U+F8FF) and both supplementary Private Use Areas (U+F0000–U+FFFFD,
        /// U+100000–U+10FFFD, decoded from UTF-16 surrogate pairs) — all three ranges ISO/IEC
        /// 10646 reserves for private use, per §8.4.2's own cross-reference.</para>
        /// </remarks>
        private static void CheckOutlineTitlesNoPua(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            int found = 0;
            WalkOutlineItems(doc, catalog, item =>
            {
                if (found > IndividualReportLimit) return false;

                var title = Resolve(doc, item[PdfName.Title]) as PdfString;
                if (title != null && ContainsUnicodePua(title.GetText()))
                {
                    found++;
                    if (found <= IndividualReportLimit)
                        v.Add(new PdfUa2StructureViolation("UA2-8.6",
                            "An outline (bookmark) /Title uses a Unicode Private Use Area " +
                            "codepoint; §8.6 prohibits PUA use in human-readable text " +
                            "strings (ISO 14289-2 §8.6)."));
                }
                return true;
            });

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.6",
                    $"…and at least one more, not listed individually."));
        }

        /// <summary>
        /// Visits every item of the catalog's document outline until <paramref name="visit"/> answers
        /// <see langword="false"/> — §8.6 (outline titles) and §8.8 (outline destinations) through
        /// the one bounded outline walk, <see cref="OutlineItems"/>' <c>Walk</c>.
        /// </summary>
        private static void WalkOutlineItems(PdfDocument doc, PdfDictionary catalog, Func<PdfDictionary, bool> visit)
            => OutlineItems.Walk(doc, catalog, visit);

        /// <summary>
        /// True when <paramref name="text"/> contains a codepoint in the BMP Private Use Area
        /// (U+E000–U+F8FF) or either supplementary Private Use Area (U+F0000–U+FFFFD,
        /// U+100000–U+10FFFD) — decoding UTF-16 surrogate pairs so a supplementary-plane PUA
        /// codepoint is not missed by testing each <see langword="char"/> in isolation.
        /// </summary>
        private static bool ContainsUnicodePua(string text)
        {
            if (text == null) return false;

            for (int i = 0; i < text.Length; i++)
            {
                int codePoint;
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    codePoint = char.ConvertToUtf32(text[i], text[i + 1]);
                    i++;
                }
                else
                {
                    codePoint = text[i];
                }

                bool isPua = (codePoint >= 0xE000 && codePoint <= 0xF8FF)
                          || (codePoint >= 0xF0000 && codePoint <= 0xFFFFD)
                          || (codePoint >= 0x100000 && codePoint <= 0x10FFFD);
                if (isPua) return true;
            }

            return false;
        }

        // ----------------------------------------------------------------
        //  Clause 8.9.2.1 — annotations must be included in the structure tree
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.9.2.1: "Annotations … shall be included in the structure tree
        /// unless explicitly excluded by the following subclauses."
        /// </summary>
        /// <remarks>
        /// <para><b>A general, role-agnostic version of the same shape §8.9.2.3 and §8.2.5.20
        /// already use</b> — those two require a <i>specific</i> enclosing role (<c>Annot</c>, or
        /// <c>Link</c>/<c>Reference</c>) for their own subtype sets; this clause only requires
        /// presence somewhere, under any role, so it is the exact same
        /// <see cref="CheckAnnotationsMustBeEnclosedInRole"/> helper called with
        /// <c>roleMatches: _ => true</c> rather than a new tree walk. It adds real coverage
        /// precisely where the specific checks do not reach: <c>Screen</c>, <c>3D</c> and
        /// <c>RichMedia</c> annotations have their own content requirements (§8.9.2.4.12,
        /// §8.9.2.4.19) but no existing check tests whether they are in the tree <i>at all</i>.
        /// </para>
        /// <para><b>The exclusions are every "explicitly excluded by the following subclauses"
        /// case this checker already knows about</b>, so an annotation is never flagged twice for
        /// contradictory reasons on the same object: <c>Popup</c> (§8.9.2.4.9 requires the
        /// opposite — absence), <c>Widget</c> (out of this clause's scope entirely — §8.9.2.1's
        /// own General text: "except for forms (see 8.10)"), <c>Sound</c>/<c>Movie</c>/
        /// <c>TrapNet</c> (deprecated and prohibited outright by their own clauses — a file that
        /// violates that prohibition is already flagged there, and re-flagging the same object for
        /// "not in the tree" here would be a second, misleading finding about content that should
        /// not exist at all), <c>PrinterMark</c> (§8.9.2.4.14 requires it to be an artifact
        /// unconditionally), <c>Watermark</c> (§8.9.2.4.16's requirement is conditional on
        /// "used as real content," the same semantic call this checker declines to make wherever
        /// it appears), and any annotation §8.9.2.2 already requires to be an artifact instead
        /// (<see cref="IsRequiredArtifactByFlags"/> — Invisible, or NoView without
        /// ToggleNoView).</para>
        /// </remarks>
        private static void CheckAnnotationsIncludedInStructureTree(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var mustBeIncluded = CollectAnnotationObjectNumbers(doc, (d, dict) =>
            {
                string subtype = (Resolve(d, dict[PdfName.Subtype]) as PdfName)?.Value;
                if (subtype == null) return false;
                if (subtype == PdfAnnotationSubtypes.Popup) return false;
                if (subtype == PdfAnnotationSubtypes.Widget) return false;
                if (subtype == PdfAnnotationSubtypes.Sound
                    || subtype == PdfAnnotationSubtypes.Movie
                    || subtype == PdfAnnotationSubtypes.TrapNet) return false;
                if (subtype == PdfAnnotationSubtypes.PrinterMark) return false;
                if (subtype == PdfAnnotationSubtypes.Watermark) return false;
                return !IsRequiredArtifactByFlags(d, dict);
            });

            CheckAnnotationsMustBeEnclosedInRole(doc, catalog, v, "UA2-8.9.2.1",
                objNum => $"Annotation object {objNum} is not referenced by any structure " +
                    "element anywhere in the structure tree; §8.9.2.1 requires every annotation " +
                    "to be included unless explicitly excluded by a more specific subclause " +
                    "(ISO 14289-2 §8.9.2.1).",
                mustBeIncluded, roleMatches: _ => true);
        }

        // ----------------------------------------------------------------
        //  Clause 8.9.2.1 — no marked-content reference into an annotation's
        //  appearance stream
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.9.2.1, a second, independent sentence sharing the same clause
        /// number as <see cref="CheckAnnotationsIncludedInStructureTree"/> (the same "one clause
        /// number, several sentences, several checks sharing the code" shape §8.7 and §8.10.1
        /// already use in this file): "ISO 32000-2 enables substructure within annotation
        /// appearance streams via marked content references. Files in conformity with this
        /// document shall not use marked content references to substructure annotation appearance
        /// streams (see ISO 32000-2:2020, Table 357)."
        /// </summary>
        /// <remarks>
        /// <para><b>What is forbidden is the reference, and the reference is in the structure
        /// tree.</b> A marked-content reference (ISO 32000-2 Table 357) is an <c>/MCR</c> kid of a
        /// structure element; its <c>/Stm</c> names the content stream its sequence lies in when
        /// that is not the page's own, and its <c>/StmOwn</c> the object referencing that stream —
        /// "a common use for this would be to identify the annotation dictionary owning the
        /// appearance stream". This reports every <c>/MCR</c> whose <c>/Stm</c> is an annotation's
        /// appearance stream, or whose <c>/StmOwn</c> is an annotation. Marked content inside an
        /// appearance stream that no reference names is not a use of one: a text field's
        /// appearance holds <c>/Tx BMC … EMC</c> by ISO 32000-2 §12.7.4.3, and a producer may leave
        /// an <c>/MCID</c> there that nothing in the tree claims. NOTE 4's effect, an annotation
        /// included whole in one element, is the <c>/OBJR</c> the tree-inclusion check reads.</para>
        /// <para>The appearance streams are <see cref="PdfXValidator.PageScan.AppearanceStreams"/>,
        /// every named <c>/AS</c> state included, scanned with <c>collectContent: false</c>: the
        /// question is which streams they are, so none is decoded. A form XObject an appearance
        /// draws is not among them; a reference into one is reported when its <c>/StmOwn</c> names
        /// the annotation. The annotations are those the pages' <c>/Annots</c> arrays list, each
        /// array read once however many pages share it. A page whose scan stopped at a ceiling is
        /// said (UA2-99), since a reference into one of its appearances would go unseen.</para>
        /// <para>The tree is <see cref="StructTreeWalker"/>'s, under its depth, node and edge
        /// ceilings.</para>
        /// </remarks>
        private static void CheckNoMarkedContentReferencesIntoAnnotationAppearances(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            if (doc.Pages == null) return;
            var structTreeRoot = ResolveStructTreeRoot(doc, catalog);
            if (structTreeRoot == null) return;

            var appearances = new HashSet<PdfStream>();
            var annotations = new HashSet<PdfDictionary>();
            var annotsRead = new HashSet<PdfArray>();
            long documentBytes = 0;
            int incompletePages = 0;

            for (int p = 0; p < doc.Pages.Count; p++)
            {
                var scan = PdfXValidator.ScanPage(doc, doc.Pages[p],
                    PdfXValidator.DefaultMaxPageContentBytes, ref documentBytes,
                    PdfXValidator.DefaultMaxDocumentContentBytes, collectContent: false);
                foreach (PdfStream ap in scan.AppearanceStreams) appearances.Add(ap);
                if (scan.Incomplete != null) incompletePages++;

                if (Resolve(doc, doc.Pages[p].Dictionary?[PdfName.Annots]) is PdfArray annots && annotsRead.Add(annots))
                {
                    for (int i = 0; i < annots.Count; i++)
                        if (Resolve(doc, annots[i]) is PdfDictionary annot) annotations.Add(annot);
                }
            }

            int found = 0;
            PdfObjectIndex index = PdfObjectIndex.Build(doc);
            StructTreeWalker.Walk(structTreeRoot, doc, index, node =>
            {
                if (found > IndividualReportLimit) return;
                foreach (PdfObject rawKid in node.RawKids)
                {
                    if (!(Resolve(doc, rawKid) is PdfDictionary kid)) continue;

                    bool intoAppearance = Resolve(doc, kid[PdfName.Stm]) is PdfStream stm && appearances.Contains(stm);
                    bool ownedByAnnotation = Resolve(doc, kid[PdfName.StmOwn]) is PdfDictionary owner && annotations.Contains(owner);
                    if (!intoAppearance && !ownedByAnnotation) continue;

                    found++;
                    if (found > IndividualReportLimit) return;
                    string element = (node.Role ?? "structure element") +
                        (node.ObjectNumber > 0 ? $" (object {node.ObjectNumber})" : "");
                    v.Add(new PdfUa2StructureViolation("UA2-8.9.2.1",
                        $"The {element} holds a marked-content reference whose " +
                        (intoAppearance ? "/Stm is an annotation's appearance stream" : "/StmOwn is an annotation") +
                        "; §8.9.2.1 prohibits using marked content references to substructure an " +
                        "annotation appearance stream (ISO 14289-2 §8.9.2.1)."));
                }
            });

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.9.2.1",
                    $"…and at least one more, not listed individually."));
            if (incompletePages > 0)
                v.Add(new PdfUa2StructureViolation("UA2-99",
                    $"{incompletePages} page(s) had their annotation appearances scanned only in part, " +
                    "so §8.9.2.1's marked-content references into them were not all checked."));
        }

        // ----------------------------------------------------------------
        //  Clause 8.4.5.6 — Font metrics: declared vs. embedded glyph widths
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.4.5.6: "For every font embedded in a conforming file and
        /// referenced for rendering, the glyph width information in the font dictionary and in
        /// the embedded font program shall be consistent for every glyph. ... consistent is
        /// defined to be a difference of no more than 1/1 000 unit in text space."
        /// </summary>
        /// <remarks>
        /// <para><b>Scope: fonts referenced for rendering, minus glyphs referenced only in mode
        /// 3.</b> <see cref="CollectGlyphUsage"/> runs the shared content walk once and records,
        /// per font, the glyphs shown outside text rendering mode 3 and the glyphs shown in it —
        /// character codes for a simple font, CIDs for a composite one, since <c>/Widths</c> is
        /// indexed by code and <c>/W</c> by CID. A font with no glyph shown outside mode 3 is not
        /// "referenced for rendering" (the same reading §8.4.5.5.1's own text gives: a font used
        /// solely in mode 3 "is therefore not rendered"), so it is not judged at all; in a font that
        /// is, every glyph with an explicit width is judged except one whose every reference is in
        /// mode 3. A glyph never shown at all is still judged — the clause says "every glyph" of a
        /// rendered font, and exempts only the mode-3 case.</para>
        /// <para><b>Simple TrueType, Type 1 and MMType1 fonts</b> reuse <see cref="SimpleFontWidthProbe"/>
        /// outright (<c>TryGetEmbeddedWidth</c>) and iterate only the physical <c>/Widths</c> array,
        /// never the declared <c>/LastChar</c>. A TrueType code is judged where
        /// <c>PdfTrueTypeFont.TryGetGlyphId</c>'s ladder resolves it without ambiguity (a symbolic font
        /// with no <c>/Encoding</c>, or a <c>/Differences</c> name resolved through the program's
        /// <c>post</c> table); a Type 1 code where a <c>/Differences</c> name selects its glyph in a
        /// <c>/FontFile3</c> CFF program with an exactly read charset (advance from the Type 2
        /// charstring's width operand, <c>nominalWidthX</c>/<c>defaultWidthX</c> and the font matrix)
        /// or a <c>/FontFile</c> Type 1 program (its <c>hsbw</c>/<c>sbw</c> width).</para>
        /// <para><b>Composite fonts with a <c>CIDFontType2</c> descendant and a <c>/FontFile2</c>
        /// program</b> compare each CID <c>/W</c> gives an explicit width (the parse
        /// <c>PdfType0Font</c> already bounds with <see cref="PdfLimits.MaxCidWidthEntries"/>)
        /// against the program's advance for the glyph <c>/CIDToGIDMap</c> selects: the CID itself
        /// under <c>/Identity</c>, the stream's entry otherwise. A CID the stream maps to glyph 0,
        /// or to a glyph past the program's count, is skipped rather than compared against a glyph
        /// that was never selected. A CID left to <c>/DW</c> is not judged, as a simple font's
        /// fallback width is not.</para>
        /// <para><b>Composite fonts with a <c>CIDFontType0</c> descendant and a <c>/FontFile3</c> CFF
        /// program</b> compare the same way through <c>CffFont.TryGetAdvance</c>: a CID-keyed program
        /// reaches the glyph through its charset (a CID is not a glyph index), any other CFF is indexed
        /// by the CID. An advance whose font-matrix scale is uncertain (<c>WidthScaleIsKnown</c>), or a
        /// charstring the width read refuses, is skipped.</para>
        /// <para><b>The vertical sentence</b> — a composite font in vertical writing mode whose program has
        /// <c>vmtx</c> metrics — is <see cref="CheckVerticalMetrics"/>, for <c>CIDFontType2</c> programs.</para>
        /// <para><b>Not attempted:</b> a simple font's code left to its base or built-in encoding (see
        /// <see cref="SimpleFontWidthProbe"/>), CFF2 programs, and vertical metrics of a CFF program.</para>
        /// </remarks>
        private static void CheckFontWidthConsistency(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var fonts = new Dictionary<PdfDictionary, PdfFont>(PdfReferenceEquality.Instance);
            int found = 0;

            foreach (KeyValuePair<PdfDictionary, GlyphUsage> entry in CollectGlyphUsage(doc, fonts))
            {
                if (found > IndividualReportLimit) break;
                if (!entry.Value.IsReferencedForRendering) continue;

                string subtype = (Resolve(doc, entry.Key[PdfName.Subtype]) as PdfName)?.Value;
                if (subtype == "TrueType" || subtype == "Type1" || subtype == "MMType1")
                    CheckSimpleFontWidths(doc, entry.Key, entry.Value, v, ref found);
                else if (subtype == "Type0")
                    CheckCompositeFontWidths(doc, entry.Key, CachedFont(doc, entry.Key, fonts) as PdfType0Font,
                        entry.Value, v, ref found);
            }

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.4.5.6",
                    $"…and at least one more, not listed individually."));
        }

        private static void CheckSimpleFontWidths(
            PdfDocument doc, PdfDictionary fontObj, GlyphUsage usage,
            List<PdfUa2StructureViolation> v, ref int found)
        {
            var widthsArray = Resolve(doc, fontObj[PdfName.Widths]) as PdfArray;
            if (widthsArray == null || widthsArray.Count == 0) return;

            var probe = new SimpleFontWidthProbe(fontObj, doc);
            if (!probe.IsTrueType && !probe.IsType1) return;

            int firstChar = (Resolve(doc, fontObj[PdfName.FirstChar]) as PdfNumber)?.IntValue ?? 0;
            int lastChar = firstChar + widthsArray.Count - 1;

            for (int code = firstChar; code <= lastChar; code++)
            {
                if (found > IndividualReportLimit) return;
                if (usage.IsReferencedOnlyInMode3(code)) continue;
                if (!probe.HasExplicitWidth(code)) continue;
                double embedded;
                try
                {
                    if (!probe.TryGetEmbeddedWidth(code, out embedded)) continue;
                }
                catch (Exception ex)
                {
                    Report("PdfUa2StructureChecker.CheckSimpleFontWidths",
                        "an embedded font program could not be read; its widths were not checked", ex);
                    return;
                }

                double declared = probe.GetWidth(code);
                if (Math.Abs(declared - embedded) <= 1) continue;

                found++;
                if (found > IndividualReportLimit) return;
                v.Add(new PdfUa2StructureViolation("UA2-8.4.5.6",
                    $"Character code {code}'s declared width ({declared:0.##}) does not " +
                    $"match its embedded font program's advance ({embedded:0.##}); §8.4.5.6 " +
                    "allows at most 1/1000 unit of difference (ISO 14289-2 §8.4.5.6)."));
            }
        }

        private static void CheckCompositeFontWidths(
            PdfDocument doc, PdfDictionary type0Dict, PdfType0Font font, GlyphUsage usage,
            List<PdfUa2StructureViolation> v, ref int found)
        {
            if (font == null) return;

            var descendants = Resolve(doc, type0Dict[PdfName.DescendantFonts]) as PdfArray;
            var cidFont = descendants != null && descendants.Count > 0
                ? Resolve(doc, descendants[0]) as PdfDictionary : null;
            string descendantSubtype = (Resolve(doc, cidFont?[PdfName.Subtype]) as PdfName)?.Value;

            TrueTypeFontProgram trueType = null;
            CffFont cff = null;
            if (descendantSubtype == "CIDFontType2")
            {
                trueType = EmbeddedTrueTypeProgram(doc, cidFont, "CheckCompositeFontWidths");
                if (trueType == null || trueType.GlyphCount == 0) return;
            }
            else if (descendantSubtype == "CIDFontType0")
            {
                cff = EmbeddedCffProgram(doc, cidFont, "CheckCompositeFontWidths");
                if (cff == null) return;
            }
            else
            {
                return;
            }

            IReadOnlyDictionary<int, int> cidToGid = trueType != null ? font.GetCidToGid() : null;

            // "If a composite (Type 0) font is used for rendering in vertical writing mode … and if the
            // associated embedded font program contains information about vertical metrics of the
            // glyphs, this information shall be consistent with the values of the DW2 and W2 keys."
            // A CFF program's vertical metrics live in the OpenType wrapper around it, when it has one —
            // and so do a CFF2 program's widths, which its charstrings do not carry.
            TrueTypeFontProgram wrapper = cff != null && (cff.IsCff2 || font.IsVerticalWritingMode)
                ? OpenTypeWrapper(doc, cidFont) : null;
            if (font.IsVerticalWritingMode)
            {
                if (trueType != null)
                    CheckVerticalMetrics(doc, cidFont, font, trueType,
                        cid => TryMapTrueTypeCid(font, trueType, cidToGid, cid, out int g) ? g : -1, usage, v, ref found);
                else if (wrapper != null)
                    CheckVerticalMetrics(doc, cidFont, font, wrapper, cid => CffGlyphOf(cff, cid), usage, v, ref found);
            }

            var cids = new List<int>(font.ExplicitWidthCids);
            cids.Sort();

            foreach (int cid in cids)
            {
                if (found > IndividualReportLimit) return;
                if (usage.IsReferencedOnlyInMode3(cid)) continue;

                int gid;
                double embedded;
                if (trueType != null)
                {
                    if (!TryMapTrueTypeCid(font, trueType, cidToGid, cid, out gid)) continue;
                    embedded = trueType.GetAdvance((ushort)gid);
                }
                else
                {
                    gid = CffGlyphOf(cff, cid);
                    if (gid < 0) continue;
                    if (cff.IsCff2)
                    {
                        if (wrapper == null || gid >= wrapper.GlyphCount || gid > ushort.MaxValue) continue;
                        embedded = wrapper.GetAdvance((ushort)gid);
                    }
                    else if (!cff.TryGetAdvance(gid, out embedded)) continue;
                }

                double declared = font.GetWidth(cid);
                if (Math.Abs(declared - embedded) <= 1) continue;

                found++;
                if (found > IndividualReportLimit) return;
                v.Add(new PdfUa2StructureViolation("UA2-8.4.5.6",
                    $"CID {cid}'s declared /W width ({declared:0.##}) in composite font " +
                    $"\"{font.BaseFont}\" does not match its embedded font program's advance " +
                    $"({embedded:0.##}) for glyph {gid}; §8.4.5.6 allows at most 1/1000 unit of " +
                    "difference (ISO 14289-2 §8.4.5.6)."));
            }
        }

        /// <summary>Which glyphs of one font are shown, and in which text rendering modes.</summary>
        private sealed class GlyphUsage
        {
            /// <summary>Glyphs shown at least once outside rendering mode 3.</summary>
            internal readonly HashSet<int> Rendered = new HashSet<int>();

            /// <summary>Glyphs shown at least once in rendering mode 3.</summary>
            internal readonly HashSet<int> Mode3 = new HashSet<int>();

            /// <summary>
            /// Every character code shown, in any rendering mode — the code itself, not the CID a
            /// composite font's CMap turns it into, since <c>/ToUnicode</c> is keyed by code.
            /// </summary>
            internal readonly HashSet<int> ReferencedCodes = new HashSet<int>();

            internal bool IsReferencedForRendering => Rendered.Count > 0;

            internal bool IsReferencedOnlyInMode3(int glyph) => Mode3.Contains(glyph) && !Rendered.Contains(glyph);
        }

        /// <summary>
        /// Runs <see cref="WalkShownText"/> once and records, per font dictionary, which glyphs are
        /// shown outside and inside text rendering mode 3 — a simple font's glyphs as character
        /// codes (one byte each), a Type 0 font's as CIDs through the font's own CMap.
        /// </summary>
        private static Dictionary<PdfDictionary, GlyphUsage> CollectGlyphUsage(
            PdfDocument doc, Dictionary<PdfDictionary, PdfFont> fonts)
        {
            var usage = new Dictionary<PdfDictionary, GlyphUsage>(PdfReferenceEquality.Instance);
            WalkShownText(doc, () => false, shown =>
            {
                PdfDictionary fontDict = shown.FontDict;
                bool composite = (Resolve(doc, fontDict[PdfName.Subtype]) as PdfName)?.Value == "Type0";
                PdfFont font = composite ? CachedFont(doc, fontDict, fonts) : null;
                if (composite && font == null) return;

                if (!usage.TryGetValue(fontDict, out GlyphUsage glyphs))
                    usage[fontDict] = glyphs = new GlyphUsage();
                HashSet<int> into = shown.RenderMode == 3 ? glyphs.Mode3 : glyphs.Rendered;

                foreach (object segment in shown.Segments)
                {
                    if (!(segment is byte[] bytes) || bytes.Length == 0) continue;
                    if (!composite)
                    {
                        foreach (byte code in bytes) { into.Add(code); glyphs.ReferencedCodes.Add(code); }
                        continue;
                    }

                    IReadOnlyList<CMapCodec.Code> codes;
                    try { codes = font.SplitCodes(bytes); }
                    catch (Exception ex)
                    {
                        Report("PdfUa2StructureChecker.CollectGlyphUsage", "shown bytes could not be decoded", ex);
                        continue;
                    }
                    foreach (CMapCodec.Code code in codes) { into.Add(code.Cid); glyphs.ReferencedCodes.Add(code.Value); }
                }
            });
            return usage;
        }

        // ----------------------------------------------------------------
        //  Clause 8.8 — intra-document destinations must be structure destinations
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.8: "All destinations ... whose target lies within the same
        /// document shall be structure destinations."
        /// </summary>
        /// <remarks>
        /// <para>ISO 32000-2:2020 §12.3.2.3 defines a structure destination as using "the same
        /// syntax as a destination ... except that the first entry in the array shall be an
        /// indirect reference to a structure element dictionary instead of to a page dictionary"
        /// — verified directly against `docs/iso/ISO_32000-2_sponsored_EC3.pdf`. A same-document
        /// destination is therefore checkable as a pure dictionary read: resolve a destination
        /// array's first element, and it is a violation exactly when that resolves to a
        /// dictionary whose <c>/Type</c> is <c>/Page</c> — the one case §12.3.2.3 says is no
        /// longer the correct shape. Anything else (a structure element dictionary, or an
        /// unresolvable/malformed first element) is not concluded either way, the same fail-safe
        /// direction every other clause in this file takes on an ambiguous read — a page
        /// dictionary conventionally carries an explicit <c>/Type /Page</c>, so this only ever
        /// flags an unambiguous case, never a dictionary that merely lacks one.</para>
        /// <para>§12.3.2.3's own text is also why "the same document" needs no separate check:
        /// a cross-document target uses a Remote or Embedded Go-To action instead, which replaces
        /// the array's structure-element reference with a byte string precisely because "the
        /// destination structure element is in a different PDF document" — so an ordinary
        /// destination *array* present in this file is inherently same-document by construction.
        /// </para>
        /// <para><b>Every holder of a destination is read</b> — §8.8 names "all destinations (see
        /// ISO 32000-2:2020, 12.3.2)": Link annotations here, outline items in
        /// <see cref="CheckOutlineDestinationsAreStructureDestinations"/>, and named destinations
        /// and the catalog's <c>/OpenAction</c> in
        /// <see cref="CheckNamedAndOpenActionDestinationsAreStructureDestinations"/>, all through
        /// <see cref="DestinationOf"/> and <see cref="TargetsPageDirectly"/>. A Link or outline
        /// item whose destination is a <i>name</i> is judged where that name is defined, not at
        /// every use.</para>
        /// </remarks>
        private static void CheckLinkDestinationsAreStructureDestinations(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            int found = 0;
            foreach (PageAnnotation entry in AnnotationsOf(doc))
            {
                if (found > IndividualReportLimit) break;

                PdfDictionary annot = entry.Dict;
                if ((Resolve(doc, annot[PdfName.Subtype]) as PdfName)?.Value != PdfAnnotationSubtypes.Link)
                    continue;
                if (!TargetsPageDirectly(doc, DestinationOf(doc, annot))) continue;

                found++;
                if (found > IndividualReportLimit) continue;
                v.Add(new PdfUa2StructureViolation("UA2-8.8",
                    "A Link annotation's destination targets a page directly; §8.8 requires " +
                    "an intra-document destination to be a structure destination (a " +
                    "reference to a structure element, not a page) instead " +
                    "(ISO 14289-2 §8.8)."));
            }

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.8",
                    $"…and at least one more, not listed individually."));
        }

        /// <summary>
        /// §8.8 for the document outline: every outline item's destination — its <c>/Dest</c>, or
        /// a GoTo action's — must be a structure destination. Shares <c>UA2-8.8</c> with
        /// <see cref="CheckLinkDestinationsAreStructureDestinations"/>.
        /// </summary>
        private static void CheckOutlineDestinationsAreStructureDestinations(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            int found = 0;
            WalkOutlineItems(doc, catalog, item =>
            {
                if (found > IndividualReportLimit) return false;
                if (!TargetsPageDirectly(doc, DestinationOf(doc, item))) return true;

                found++;
                if (found <= IndividualReportLimit)
                    v.Add(new PdfUa2StructureViolation("UA2-8.8",
                        "An outline (bookmark) item's destination targets a page directly; §8.8 " +
                        "requires an intra-document destination to be a structure destination " +
                        "(ISO 14289-2 §8.8)."));
                return true;
            });

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.8",
                    $"…and at least one more outline item(s), not listed individually."));
        }

        /// <summary>
        /// §8.8 for the destinations the catalog itself holds: every named destination — the
        /// PDF 1.1 <c>/Dests</c> dictionary and the <c>/Names /Dests</c> name tree — and
        /// <c>/OpenAction</c>, when it is a destination or a GoTo action.
        /// </summary>
        /// <remarks>
        /// A named destination is a destination whether or not anything uses it, and judging it
        /// once where it is defined covers every Link or outline item that names it. The name tree
        /// is read through <see cref="PdfNameTree.TryFlatten"/>, bounded by
        /// <see cref="PdfLimits.MaxNameTreeEntries"/>.
        /// </remarks>
        private static void CheckNamedAndOpenActionDestinationsAreStructureDestinations(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var named = new List<KeyValuePair<string, PdfObject>>();

            if (Resolve(doc, catalog[PdfName.Dests]) is PdfDictionary legacy)
                foreach (var entry in legacy)
                    named.Add(new KeyValuePair<string, PdfObject>(entry.Key.Value, entry.Value));

            PdfObject treeRoot = (Resolve(doc, catalog[PdfName.Names]) as PdfDictionary)?[PdfName.Dests];
            if (treeRoot != null)
            {
                var entries = new List<KeyValuePair<PdfObject, PdfObject>>();
                int budget = PdfLimits.MaxNameTreeEntries;
                PdfNameTree.TryFlatten(treeRoot, doc, entries, ref budget);
                foreach (var entry in entries)
                    named.Add(new KeyValuePair<string, PdfObject>(
                        PdfNameTree.KeyText(Resolve(doc, entry.Key)) ?? "(unreadable name)", entry.Value));
            }

            int found = 0;
            foreach (var entry in named)
            {
                if (!TargetsPageDirectly(doc, Resolve(doc, entry.Value))) continue;
                found++;
                if (found > IndividualReportLimit) continue;
                v.Add(new PdfUa2StructureViolation("UA2-8.8",
                    $"The named destination \"{entry.Key}\" targets a page directly; §8.8 requires an " +
                    "intra-document destination to be a structure destination (ISO 14289-2 §8.8)."));
            }

            PdfObject openAction = Resolve(doc, catalog[PdfName.OpenAction]);
            PdfObject openDestination = openAction is PdfDictionary action
                ? GoToDestinationOf(doc, action)
                : openAction;
            if (TargetsPageDirectly(doc, openDestination))
            {
                found++;
                if (found <= IndividualReportLimit)
                    v.Add(new PdfUa2StructureViolation("UA2-8.8",
                        "The document catalog's /OpenAction destination targets a page directly; §8.8 " +
                        "requires an intra-document destination to be a structure destination " +
                        "(ISO 14289-2 §8.8)."));
            }

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.8",
                    $"…and {found - IndividualReportLimit} more catalog-level destination(s), not listed individually."));
        }

        /// <summary>
        /// The destination <paramref name="holder"/> (a Link annotation or an outline item) jumps
        /// to: its <c>/Dest</c>; else, for a GoTo action in its <c>/A</c>, the action's <c>/SD</c>
        /// when present and otherwise its <c>/D</c>. Null when there is none, or when the action is
        /// not a GoTo — <c>/GoToR</c> and <c>/GoToE</c> target another document.
        /// </summary>
        /// <remarks>
        /// <b><c>/SD</c> before <c>/D</c>.</b> ISO 32000-2:2020 Table 202 makes a GoTo action's
        /// <c>/D</c> <i>required</i> and adds <c>/SD</c> (PDF 2.0), "the structure destination to
        /// jump to … If present, the structure destination should take precedence over destination
        /// in the D entry". A PDF 2.0 file linking by structure therefore still carries a
        /// <c>/D</c> — typically a page destination kept for older readers — and the destination
        /// the action jumps to is its <c>/SD</c>. Reading <c>/D</c> alone reported that file.
        /// </remarks>
        private static PdfObject DestinationOf(PdfDocument doc, PdfDictionary holder)
        {
            PdfObject dest = Resolve(doc, holder[PdfName.Dest]);
            if (dest != null) return dest;
            return GoToDestinationOf(doc, Resolve(doc, holder[PdfName.A]) as PdfDictionary);
        }

        /// <summary>A GoTo action's destination — <c>/SD</c> when present, else <c>/D</c> (see
        /// <see cref="DestinationOf"/>) — or null for any other action.</summary>
        private static PdfObject GoToDestinationOf(PdfDocument doc, PdfDictionary action)
        {
            if ((Resolve(doc, action?[PdfName.S]) as PdfName)?.Value != "GoTo") return null;
            return Resolve(doc, action[PdfName.SD]) ?? Resolve(doc, action[PdfName.D]);
        }

        /// <summary>
        /// True exactly when <paramref name="destination"/> is an explicit destination array whose
        /// first element resolves to a dictionary with <c>/Type /Page</c> — the one shape
        /// ISO 32000-2:2020 §12.3.2.3 says a structure destination replaces. A named destination's
        /// dictionary form (<c>&lt;&lt; /D [...] &gt;&gt;</c>, §12.3.2.3) is unwrapped first. A name
        /// or string (a reference to a named destination), a structure element, or anything
        /// malformed answers false.
        /// </summary>
        private static bool TargetsPageDirectly(PdfDocument doc, PdfObject destination)
        {
            if (destination is PdfDictionary wrapper) destination = Resolve(doc, wrapper[PdfName.D]);
            if (!(destination is PdfArray array) || array.Count == 0) return false;

            var target = Resolve(doc, array[0]) as PdfDictionary;
            return (Resolve(doc, target?[PdfName.Type]) as PdfName)?.Value == "Page";
        }

        // ----------------------------------------------------------------
        //  Clause 8.4.5.9 — the .notdef glyph must not be shown, Type0/CID
        //  fonts and (via /Differences only) simple TrueType fonts
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.4.5.9: "A file … shall not contain a reference to the <c>.notdef</c>
        /// glyph as defined in ISO 32000-2:2020, 9.7.6.3, from any of the text showing operators,
        /// regardless of text rendering mode, in any content stream."
        /// </summary>
        /// <remarks>
        /// <para><b>Type 0 (composite/CID) fonts.</b> ISO 32000-2:2020 §9.7.4.2/§9.7.6.3
        /// (verified directly against `docs/iso/ISO_32000-2_sponsored_EC3.pdf`) define
        /// <c>.notdef</c> for a CID-keyed font as CID 0 itself — "Every CIDFont shall contain a
        /// glyph description for CID 0, which is analogous to the <c>.notdef</c> character name in
        /// simple fonts." So "a text-showing operator references <c>.notdef</c>" reduces to "the
        /// decoded CID sequence contains a 0" — available straight from the CMap decode, with no
        /// <c>/CIDToGIDMap</c> lookup and no embedded-font-program parsing at all.</para>
        /// <para><b>A code the file's CMap maps to CID 0, and a code no codespace range admits.</b>
        /// §9.7.6.3: "If the code is invalid — that is, the bytes extracted from the string to be
        /// shown do not match any codespace range in the CMap — a substitute glyph is chosen as just
        /// described", which is the notdef machinery, so an invalid code references
        /// <c>.notdef</c> whatever CID the bytes would otherwise suggest — a trailing byte too few
        /// for a two-byte codespace among them. The codec already records which codes matched no
        /// range (<c>CMapCodec.Code.CodespaceMatched</c>); this clause once declined the case as
        /// needing a walk of the ranges, which the split itself is. And a code that matches a range
        /// but that no character mapping covers, which §9.7.6.3 sends to a notdef mapping or CID 0:
        /// the codec keeps the code's own value as its CID, for rendering, and says it was unmapped
        /// (<c>CMapCodec.Code.CidMapped</c>).</para>
        /// <para><b>The decision is <see cref="NotdefReferences"/>'s</b>, in Core, which PDF/A's
        /// 6.2.11.8 rule asks too; this clause walks the text and words the findings.</para>
        /// <para><b>Simple TrueType fonts, via <c>/Differences</c> only — the deferred follow-on
        /// from this clause's earlier slice, now closed as far as it safely can be.</b>
        /// <c>PdfTrueTypeFont.TryGetGlyphId</c> — the route §8.4.5.6/§8.4.5.7 already use for a
        /// simple font's glyph id — can never return <see langword="true"/> with a GID of 0: every
        /// rung of its ladder requires <c>gid &gt; 0</c>, so a <see langword="false"/> from it stays
        /// ambiguous between "unresolvable" and "resolves to <c>.notdef</c>." That ambiguity is
        /// real for the symbol/Mac <c>cmap</c> half of §9.6.6.4's ladder — a TrueType <c>cmap</c>
        /// subtable's own "no entry for this code" sentinel is bitwise identical to an explicit
        /// mapping to glyph 0, so no amount of new Core code can tell them apart — but it is not
        /// real for the <c>/Differences</c> half: a code's glyph name is either found in the
        /// embedded <c>post</c> table or spelled out directly as a glyph index by the file's own
        /// author, and either one naming glyph 0 is a deliberate, legible fact.
        /// <see cref="PdfTrueTypeFont.DifferenceResolvesToNotdef"/> answers exactly that narrower
        /// question, and only that one — plus the explicit case every simple subtype shares: a
        /// <c>/Differences</c> entry that names <c>.notdef</c> itself. <b>Type 1, MMType1 and
        /// Type 3</b> select glyphs by name rather than by glyph id, so that explicit name is checked
        /// for all three, and for Type 1 and MMType1 so is a
        /// name the embedded program does not define — which §9.6.5.2 also substitutes with
        /// <c>.notdef</c> — wherever the program's glyph names were read completely.</para>
        /// <para><b>No <c>Tr</c> tracking at all</b> — unlike §8.4.5.6's render-mode-3 exemption,
        /// this clause is explicit ("regardless of text rendering mode"), so the graphics-state
        /// shadow this check needs is a single font-name string plus a <c>q</c>/<c>Q</c> stack of
        /// it — not the full text-state shadow `Base.Operations.Extraction.PdfStructuredTextExtractor`'s
        /// own (unreachable, `internal` in `Base`) <c>TextGs</c> carries for its own, different
        /// purpose.</para>
        /// <para><b>Each content stream is walked with its own fresh font state</b>, matching how
        /// <see cref="PdfXValidator.ScanPage"/> pairs each stream with its own resource dictionary
        /// — so a nested Form XObject or Type 3 glyph procedure that relies on the invoking
        /// context's already-selected font (never issuing its own <c>Tf</c>) is skipped rather
        /// than mis-attributed. Fail-safe: this can only under-report, never over-report.</para>
        /// <para>Font-dictionary resolution mirrors `Base`'s own (unreachable) <c>LookupFontDict</c>
        /// exactly: resolve <c>resources[/Font]</c>, then index it by
        /// <c>new PdfName(currentFontName)</c> — the same two-step dynamic-name read, since a
        /// content-stream font name has no cached <see cref="PdfName"/> field to reuse.</para>
        /// </remarks>
        private static void CheckNoNotdefInFonts(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            int found = 0;
            var readers = new Dictionary<PdfDictionary, NotdefReferences>(PdfReferenceEquality.Instance);
            WalkShownText(doc, () => found > IndividualReportLimit, shown =>
            {
                if (!readers.TryGetValue(shown.FontDict, out NotdefReferences reader))
                {
                    reader = NotdefReferences.For(shown.FontDict, doc);
                    readers[shown.FontDict] = reader;
                    if (reader.Unreadable != null)
                        Report("PdfUa2StructureChecker.CheckNoNotdefInFonts", reader.Unreadable + "; its .notdef references were not all checked");
                }
                string subtype = (Resolve(doc, shown.FontDict[PdfName.Subtype]) as PdfName)?.Value;
                string unreadableBefore = reader.Unreadable;
                foreach (object segment in shown.Segments)
                {
                    if (found > IndividualReportLimit) return;
                    if (!(segment is byte[] bytes) || bytes.Length == 0) continue;
                    foreach (NotdefReference reference in reader.In(bytes))
                    {
                        found++;
                        if (found > IndividualReportLimit) return;
                        v.Add(new PdfUa2StructureViolation("UA2-8.4.5.9", DescribeNotdef(reference, shown.FontName, subtype)));
                    }
                }
                if (unreadableBefore == null && reader.Unreadable != null)
                    Report("PdfUa2StructureChecker.CheckNoNotdefInFonts", reader.Unreadable + "; its .notdef references were not all checked");
            });

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.4.5.9",
                    $"…and at least one more, not listed individually."));
        }

        /// <summary>
        /// Every text-showing operator (<c>Tj</c>, <c>'</c>, <c>"</c>, <c>TJ</c>) in every content
        /// stream a page reaches, with the font dictionary it shows through, that font's resource
        /// name, the text rendering mode in effect, and the shown segments (<c>byte[]</c> strings,
        /// and a <c>TJ</c> array's <see langword="float"/> adjustments). The one content walk the
        /// font clauses share — §8.4.5.5.1, §8.4.5.6 and §8.4.5.9 — so its graphics-state shadow is
        /// written once.
        /// </summary>
        /// <remarks>
        /// <para>Streams come from <see cref="PdfXValidator.ScanPage"/>: page content, annotation
        /// appearances, nested form XObjects, tiling patterns and Type 3 glyph procedures, each
        /// paired with its own resource dictionary and bounded by its byte budgets. <b>Each stream
        /// starts with a fresh shadow</b> — no font, mode 0 — so a nested stream relying on the
        /// invoking context's font is skipped rather than misattributed. <c>q</c>/<c>Q</c> save and
        /// restore the font name and the mode together.</para>
        /// <para>An operator shown with no font selected, or through a name the stream's resources
        /// do not resolve, is not reported to <paramref name="onShow"/>.</para>
        /// </remarks>
        private static void WalkShownText(
            PdfDocument doc, Func<bool> shouldStop, Action<ShownText> onShow)
        {
            if (doc.Pages == null) return;

            long documentBytes = 0;
            for (int p = 0; p < doc.Pages.Count; p++)
            {
                if (shouldStop()) return;

                var scan = PdfXValidator.ScanPage(doc, doc.Pages[p],
                    PdfXValidator.DefaultMaxPageContentBytes, ref documentBytes,
                    PdfXValidator.DefaultMaxDocumentContentBytes, collectContent: true);

                for (int s = 0; s < scan.ContentStreams.Count; s++)
                {
                    PdfDictionary resources = scan.ContentStreamResources[s];
                    string fontName = null;
                    int renderMode = 0;
                    var stack = new Stack<(string FontName, int RenderMode)>();

                    // Marked content, which q/Q do not save: a frame per BMC/BDC, with running
                    // counts so the state at each text operator is O(1) to read.
                    var marked = new Stack<(bool Artifact, bool ActualText, bool HasMcid)>();
                    var mcids = new Stack<int>();
                    int artifactDepth = 0, actualTextDepth = 0;

                    List<ContentStreamOperation> ops;
                    try { ops = ContentStreamParser.Parse(scan.ContentStreams[s]); }
                    catch (Exception ex)
                    {
                        Report("PdfUa2StructureChecker.WalkShownText",
                            "a content stream could not be parsed; its text was not checked", ex);
                        continue;
                    }

                    foreach (ContentStreamOperation op in ops)
                    {
                        if (shouldStop()) return;

                        List<object> segments;
                        switch (op.Operator)
                        {
                            case PdfOperators.Save:
                                stack.Push((fontName, renderMode));
                                continue;
                            case PdfOperators.Restore:
                                if (stack.Count > 0) (fontName, renderMode) = stack.Pop();
                                continue;
                            case PdfOperators.SetFont:
                                fontName = ContentOperand.Name(op, 0);
                                continue;
                            case PdfOperators.SetTextRenderMode:
                                renderMode = ContentOperand.Int(op, 0);
                                continue;
                            case PdfOperators.BeginMarkedContent:
                            case PdfOperators.BeginMarkedContentProps:
                            {
                                bool artifact = ContentOperand.Name(op, 0) == "Artifact";
                                // default(MarkedContentInfo) has Mcid 0, not -1 — only a BDC that
                                // actually carries a property operand may supply either value.
                                bool hasProps = op.Operator == PdfOperators.BeginMarkedContentProps && op.Operands.Length > 1;
                                var info = hasProps ? MarkedContentInfo.Read(op.Operands[1], resources, doc) : default;
                                bool actualText = hasProps && info.ActualText != null;
                                bool hasMcid = hasProps && info.Mcid >= 0;
                                marked.Push((artifact, actualText, hasMcid));
                                if (artifact) artifactDepth++;
                                if (actualText) actualTextDepth++;
                                if (hasMcid) mcids.Push(info.Mcid);
                                continue;
                            }
                            case PdfOperators.EndMarkedContent:
                            {
                                if (marked.Count == 0) continue;
                                var frame = marked.Pop();
                                if (frame.Artifact) artifactDepth--;
                                if (frame.ActualText) actualTextDepth--;
                                if (frame.HasMcid) mcids.Pop();
                                continue;
                            }
                            case PdfOperators.ShowText:
                            case PdfOperators.NextLineShowText:
                                segments = ContentOperand.ShowTextSegments(op, 0);
                                break;
                            case PdfOperators.NextLineSetSpacingShowText:
                                segments = ContentOperand.ShowTextSegments(op, 2);
                                break;
                            case PdfOperators.ShowTextArray:
                                segments = ContentOperand.ShowTextArraySegments(op);
                                break;
                            default:
                                continue;
                        }

                        if (fontName == null || resources == null) continue;
                        var fontResources = Resolve(doc, resources[PdfName.Font]) as PdfDictionary;
                        var fontDict = Resolve(doc, fontResources?[new PdfName(fontName)]) as PdfDictionary;
                        if (fontDict != null)
                            onShow(new ShownText(fontDict, fontName, renderMode, segments, p,
                                s == scan.PageContentIndex, artifactDepth > 0, actualTextDepth > 0,
                                mcids.Count > 0 ? mcids.Peek() : -1));
                    }
                }
            }
        }

        /// <summary>One text-showing operator, as <see cref="WalkShownText"/> reports it.</summary>
        private readonly struct ShownText
        {
            internal ShownText(PdfDictionary fontDict, string fontName, int renderMode, List<object> segments,
                int pageIndex, bool isPageContent, bool inArtifact, bool hasMarkedContentActualText, int mcid)
            {
                FontDict = fontDict;
                FontName = fontName;
                RenderMode = renderMode;
                Segments = segments;
                PageIndex = pageIndex;
                IsPageContent = isPageContent;
                InArtifact = inArtifact;
                HasMarkedContentActualText = hasMarkedContentActualText;
                Mcid = mcid;
            }

            /// <summary>The font dictionary the operator shows through.</summary>
            internal PdfDictionary FontDict { get; }

            /// <summary>The font's resource name.</summary>
            internal string FontName { get; }

            /// <summary>The text rendering mode in effect (§9.3.6).</summary>
            internal int RenderMode { get; }

            /// <summary>The shown strings (<c>byte[]</c>) and a <c>TJ</c> array's adjustments.</summary>
            internal List<object> Segments { get; }

            /// <summary>Zero-based index of the page whose scan reached the stream.</summary>
            internal int PageIndex { get; }

            /// <summary>True for the page's own content stream — the only one whose MCIDs belong to
            /// the page's <c>/StructParents</c> entry.</summary>
            internal bool IsPageContent { get; }

            /// <summary>Inside an <c>/Artifact</c> marked-content sequence at any depth.</summary>
            internal bool InArtifact { get; }

            /// <summary>Inside a marked-content sequence whose property list carries
            /// <c>/ActualText</c> (an empty one included).</summary>
            internal bool HasMarkedContentActualText { get; }

            /// <summary>The innermost enclosing <c>/MCID</c>, or -1.</summary>
            internal int Mcid { get; }
        }

        /// <summary>
        /// <see cref="PdfFont.Create"/> once per font dictionary for the whole check, rather than
        /// once per text-showing operator — construction parses an embedded CMap or font program,
        /// and a page shows through the same font thousands of times. A font that cannot be built
        /// is remembered as null and reported once.
        /// </summary>
        private static PdfFont CachedFont(
            PdfDocument doc, PdfDictionary fontDict, Dictionary<PdfDictionary, PdfFont> cache)
        {
            if (cache.TryGetValue(fontDict, out PdfFont font)) return font;
            try { font = PdfFont.Create(fontDict, doc); }
            catch (Exception ex)
            {
                Report("PdfUa2StructureChecker.CachedFont", "a font dictionary could not be read as a font", ex);
                font = null;
            }
            cache[fontDict] = font;
            return font;
        }

        /// <summary>The finding for one <c>.notdef</c> reference <see cref="NotdefReferences"/> found.</summary>
        private static string DescribeNotdef(NotdefReference reference, string currentFontName, string subtype)
        {
            string opener = $"A text-showing operator using font resource \"{currentFontName}\" draws character code {reference.Code} ";
            const string Prohibits = "; §8.4.5.9 prohibits referencing it regardless of text rendering mode (ISO 14289-2 §8.4.5.9).";
            const string ProhibitsThis = " — a reference to the .notdef glyph; §8.4.5.9 prohibits this regardless of text rendering mode (ISO 14289-2 §8.4.5.9).";
            switch (reference.Cause)
            {
                case NotdefCause.Cid0:
                    return opener + "of a Type0 font, which decodes to CID 0" + ProhibitsThis;
                case NotdefCause.NoCodespace:
                    return opener + "of a Type0 font, which matches no codespace range of its CMap, so a substitute glyph is chosen (ISO 32000-2 §9.7.6.3)" + ProhibitsThis;
                case NotdefCause.NoCidMapping:
                    return opener + "of a Type0 font, which no character mapping of its CMap covers, so a notdef mapping or CID 0 is selected (ISO 32000-2 §9.7.6.3)" + ProhibitsThis;
                case NotdefCause.GlyphZero:
                    return opener + "of a simple TrueType font, whose /Differences entry names .notdef or explicitly resolves to glyph 0" + ProhibitsThis;
                case NotdefCause.UnnamedCode:
                    return opener + $"of a /{subtype} font, whose encoding names no glyph for it, so .notdef is selected" + Prohibits;
                case NotdefCause.NameNotInProgram:
                    return opener + $"of a /{subtype} font, whose /Differences entry names /{reference.GlyphName}, which the embedded font program does not define, so .notdef is substituted (ISO 32000-2 §9.6.5.2)" + Prohibits;
                default:   // NamedNotdef
                    return subtype == "TrueType"
                        ? opener + "of a simple TrueType font, whose /Differences entry names .notdef or explicitly resolves to glyph 0" + ProhibitsThis
                        : opener + $"of a /{subtype} font, whose /Differences entry names the .notdef glyph" + Prohibits;
            }
        }

        // ----------------------------------------------------------------
        //  Clause 6.2 — a file shall adhere to ISO/TS 32005 (structure containment rules)
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §6.2 ("Conforming files"): "a file shall adhere to all requirements
        /// of ISO/TS 32005." ISO/TS 32005:2023 §7.2 in turn: "PDF 1.7 elements and PDF 2.0
        /// elements shall not have child or parent PDF 1.7 elements or PDF 2.0 elements that are
        /// not explicitly listed in [its Table 5]."
        /// </summary>
        /// <remarks>
        /// <para>The <b>twenty-seventh shape</b>, and the first clause number outside Clause 8 —
        /// §6.2 is a general "Conforming files" requirement, not one of the per-structure-type
        /// subclauses every other slice in this file has closed. Verified directly against
        /// `docs/iso/ISO-14289-2-2024-sponsored.pdf` for the UA-2 hook and the doc comment on
        /// <see cref="PdfStructureNamespaceInclusion"/> itself (already built as this exact
        /// prerequisite — GAPS.md §22a — but not yet consumed by any check) for TS 32005's own
        /// text.</para>
        /// <para>Reuses <see cref="PdfStructureNamespaceInclusion.CanContain"/> outright: for
        /// every structure element with a parent (the struct tree root's own <c>null</c> role
        /// sentinel — see <see cref="StructTreeWalker.GetRole"/>'s remarks — is translated to the
        /// literal parent-type name <c>"StructTreeRoot"</c>, matching the table's own row/column
        /// naming), a result of <see cref="StructureContainmentRule.Forbidden"/> — the table's
        /// "∅ — shall not occur" cell, TS 32005's own <c>'0'</c> — is the one unconditional
        /// violation. No other value is flagged: <see cref="StructureContainmentRule.Unknown"/>
        /// covers any custom or role-mapped type name the table's fixed standard-type lists do
        /// not recognise (resolving a role mapping to its standard target first is a separate,
        /// larger piece of work this check does not attempt — TS 32005 "has nothing to say about
        /// an unmapped name directly," per that type's own doc comment), and
        /// <see cref="StructureContainmentRule.ForbiddenUnlessGroupingLevel"/>/
        /// <see cref="StructureContainmentRule.Undocumented"/>/
        /// <see cref="StructureContainmentRule.SeeElementDescription"/>/
        /// <see cref="StructureContainmentRule.RubySpecific"/>/
        /// <see cref="StructureContainmentRule.WarichuSpecific"/> are all either conditional or
        /// need more context than a bare type-name pair carries — the same reasoning
        /// <see cref="PdfStructureNamespaceInclusion.IsDefinitelyAllowed"/>'s own doc comment
        /// already gives for excluding them from "definitely OK," mirrored here for "definitely
        /// not OK."</para>
        /// <para><b>Only structure-element-to-structure-element containment is checked</b> — the
        /// table's 53rd child-only row, "Content Item" (a raw MCID/OBJR leaf named directly in a
        /// <c>/K</c> array rather than a further structure element), is not reached by
        /// <see cref="StructTreeWalker.Walk"/>'s own node visits, which fire only on resolvable
        /// structure element dictionaries.</para>
        /// </remarks>
        private static void CheckStructureContainmentRules(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var structTreeRoot = ResolveStructTreeRoot(doc, catalog);
            if (structTreeRoot == null) return;

            int found = 0;
            StructTreeWalker.Walk(structTreeRoot, doc, index: null, node =>
            {
                if (found > IndividualReportLimit) return;
                if (node.Parent == null) return;

                string childRole = node.Role;
                if (childRole == null) return;
                string parentRole = node.Parent.Role ?? "StructTreeRoot";

                if (PdfStructureNamespaceInclusion.CanContain(parentRole, childRole)
                    != StructureContainmentRule.Forbidden) return;

                found++;
                if (found > IndividualReportLimit) return;
                v.Add(new PdfUa2StructureViolation("UA2-6.2",
                    $"A {childRole} structure element is a child of a {parentRole} structure " +
                    "element; ISO/TS 32005 Table 5 lists this containment as forbidden " +
                    "(ISO 14289-2 §6.2)."));
            });

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-6.2",
                    $"…and at least one more, not listed individually."));
        }

        // ----------------------------------------------------------------
        //  Clause 8.2.4 — a namespace's own /RoleMapNS must not target itself
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.2.4: "Within a given explicitly provided namespace, structure
        /// types shall not be role mapped to other structure types in the same namespace."
        /// </summary>
        /// <remarks>
        /// <para>The narrowest of §8.2.4's several sentences — a pure dictionary read needing no
        /// structure-tree walk at all, unlike the clause's own opening requirement ("all
        /// structure elements shall belong to, or be role mapped to" one of three standard
        /// namespaces), which needs following a role map transitively across namespaces to its
        /// eventual target before answering "does this element ultimately belong somewhere
        /// standard" — that transitive chase is a separate check,
        /// <see cref="CheckElementBelongsToStandardNamespace"/>, closing the clause fully.</para>
        /// <para>ISO 32000-2:2020 Table 356 (verified directly against
        /// `docs/iso/ISO_32000-2_sponsored_EC3.pdf`) gives <c>/RoleMapNS</c> two value shapes for
        /// a mapped structure type: a bare <see cref="PdfName"/>, meaning a target type in "the
        /// default standard structure namespace" — <see cref="Pdf17StandardStructureNamespace"/>,
        /// per §14.8.6.1's own default (the same one <see cref="CheckDocumentStructureType"/>'s
        /// remarks already establish) — or a two-element array
        /// <c>[targetTypeName, targetNamespaceDictionaryReference]</c> naming the target
        /// namespace explicitly. Either way, a violation is exactly "the target namespace's own
        /// <c>/NS</c> string equals the mapping namespace's own <c>/NS</c> string."</para>
        /// <para>Namespace dictionaries are reached through the struct tree root's
        /// <c>/Namespaces</c> array (Table 354) rather than a tree walk — every namespace a
        /// conforming file uses is required to be listed there, and a `/RoleMapNS` lives on the
        /// namespace dictionary itself, not on any structure element.</para>
        /// <para>A malformed or unresolvable target (neither a name nor a proper two-element
        /// array, or a target namespace dictionary with no readable <c>/NS</c> string of its
        /// own) is skipped, not guessed at — the same fail-safe direction every other clause in
        /// this file takes on an ambiguous read.</para>
        /// </remarks>
        private static void CheckRoleMapNsNotSameNamespace(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var structTreeRoot = ResolveStructTreeRoot(doc, catalog);
            if (structTreeRoot == null) return;

            var namespacesArray = Resolve(doc, structTreeRoot[PdfName.Namespaces]) as PdfArray;
            if (namespacesArray == null) return;

            int found = 0;
            foreach (PdfObject rawNs in namespacesArray)
            {
                if (found > IndividualReportLimit) break;

                var nsDict = Resolve(doc, rawNs) as PdfDictionary;
                string ownNamespaceName = (Resolve(doc, nsDict?[PdfName.NS]) as PdfString)?.GetText();
                if (ownNamespaceName == null) continue;

                var roleMapNs = Resolve(doc, nsDict[PdfName.RoleMapNS]) as PdfDictionary;
                if (roleMapNs == null) continue;

                foreach (KeyValuePair<PdfName, PdfObject> entry in roleMapNs)
                {
                    if (found > IndividualReportLimit) break;

                    var value = Resolve(doc, entry.Value);
                    string targetNamespaceName;
                    if (value is PdfName)
                    {
                        targetNamespaceName = Pdf17StandardStructureNamespace;
                    }
                    else if (value is PdfArray arr && arr.Count >= 2)
                    {
                        var targetNsDict = Resolve(doc, arr[1]) as PdfDictionary;
                        targetNamespaceName = (Resolve(doc, targetNsDict?[PdfName.NS]) as PdfString)?.GetText();
                    }
                    else
                    {
                        continue;
                    }

                    if (targetNamespaceName == null || targetNamespaceName != ownNamespaceName) continue;

                    found++;
                    if (found > IndividualReportLimit) continue;
                    v.Add(new PdfUa2StructureViolation("UA2-8.2.4",
                        $"Namespace \"{ownNamespaceName}\"'s /RoleMapNS maps structure type " +
                        $"{entry.Key.Value} to a type in the same namespace; §8.2.4 prohibits " +
                        "this (ISO 14289-2 §8.2.4)."));
                }
            }

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.2.4",
                    $"…and at least one more, not listed individually."));
        }

        // ----------------------------------------------------------------
        //  Clause 8.2.6.4 — ARIA/DPUB-ARIA roles must use the ARIA-1.1 attribute owner
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.2.6.4: "ARIA (version 1.1) and DPUB-ARIA roles, if present, shall
        /// use the ARIA-1.1 attribute owner."
        /// </summary>
        /// <remarks>
        /// <para>ISO 32000-2:2020 §14.8.5.2 Table 377 names <c>ARIA-1.1</c> (verified directly
        /// against `docs/iso/ISO_32000-2_sponsored_EC3.pdf`) as the literal attribute-owner
        /// value for WAI-ARIA/DPUB-ARIA attributes, and §8.2.5.31's own worked example
        /// (`/O /ARIA-1.1 /role /doc-bibliography`) confirms the role key is the lowercase
        /// <c>/role</c> — PDF names are case-sensitive, so this is not cosmetic. No other
        /// standard attribute owner (Layout, List, PrintField, Table, Artifact, or any of the
        /// export-format owners Table 377 also lists) defines a key named <c>role</c>, so a
        /// <c>/role</c> entry is checked unconditionally: wherever it appears in an attribute
        /// object, that object's own <c>/O</c> must be <c>/ARIA-1.1</c>.</para>
        /// <para><b>Both places an attribute can be declared are read</b>, through
        /// <see cref="PdfStructureAttributes.ReadAll(PdfDictionary, IReadOnlyDictionary{string, PdfObject}, Func{PdfObject, PdfObject})"/>: the element's own <c>/A</c> (a single
        /// attribute object, or an array of them interleaved with revision numbers — Table 355)
        /// and the attribute objects its <c>/C</c> classes name in the struct tree root's
        /// <c>/ClassMap</c> (§14.7.6.2). This check first walked <c>/A</c> by hand and so never
        /// saw a <c>/role</c> declared through a class. <c>ReadAll</c> files an attribute object
        /// with no <c>/O</c> under the empty owner, which is reported: "absent" is not
        /// <c>ARIA-1.1</c>.</para>
        /// </remarks>
        private static void CheckAriaRoleUsesAria11Owner(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var structTreeRoot = ResolveStructTreeRoot(doc, catalog);
            if (structTreeRoot == null) return;

            Func<PdfObject, PdfObject> resolve = o => Resolve(doc, o);
            var classMap = PdfStructureAttributes.BuildClassMap(structTreeRoot, resolve);

            int found = 0;
            StructTreeWalker.Walk(structTreeRoot, doc, index: null, node =>
            {
                if (found > IndividualReportLimit) return;
                if (node.Dict == null) return;

                foreach (var byOwner in AttributesOf(node.Dict, classMap, resolve))
                {
                    if (found > IndividualReportLimit) break;
                    if (!byOwner.Value.ContainsKey(PdfName.Role.Value)) continue;
                    if (byOwner.Key == "ARIA-1.1") continue;

                    found++;
                    if (found > IndividualReportLimit) continue;
                    string owner = byOwner.Key.Length == 0 ? "absent" : byOwner.Key;
                    v.Add(new PdfUa2StructureViolation("UA2-8.2.6.4",
                        $"A {node.Role ?? "structure"} element's attribute object has a /role " +
                        $"entry but its /O owner is {owner}, not /ARIA-1.1; " +
                        "§8.2.6.4 requires ARIA/DPUB-ARIA roles to use the ARIA-1.1 owner " +
                        "(ISO 14289-2 §8.2.6.4)."));
                }
            });

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.2.6.4",
                    $"…and at least one more, not listed individually."));
        }

        // ----------------------------------------------------------------
        //  Clause 8.2.4 (opening sentence) — every element belongs to a standard namespace,
        //  possibly by role mapping — bounded, unlike the fully general reading
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.2.4, opening sentence: "All structure elements shall belong to,
        /// or be role mapped to, at least one of the following namespaces … the PDF 1.7
        /// namespace; the PDF 2.0 namespace; the MathML namespace." Shares the <c>UA2-8.2.4</c>
        /// code with <see cref="CheckRoleMapNsNotSameNamespace"/> — the same clause number, a
        /// different sentence, the same "one code per clause number, not per sentence" precedent
        /// §8.7's own two checks already set.
        /// </summary>
        /// <remarks>
        /// <para>This sentence needs following a role map transitively across namespaces to its
        /// eventual target, which is a <b>bounded</b> chase:
        /// <see cref="Tetitulz.Pdf.Core.Document.PdfRoleMap.MaxHops"/> (16) is
        /// reused outright as the hop cap, rather than inventing a second depth constant for the
        /// namespace-scoped role map alongside the classic single-namespace one it already bounds
        /// — the two mechanisms solve the same shaped problem (follow a role map until it either
        /// resolves or the file is being unreasonable) and should share one answer to "how many
        /// hops is enough," not two independently-chosen numbers.</para>
        /// <para><b>Three-valued, not boolean</b> — <see cref="NamespaceMembership"/> distinguishes
        /// <c>Standard</c> (reached a standard namespace), <c>NotStandard</c> (a definite,
        /// unambiguous non-membership: the namespace is not standard and either has no
        /// <c>/RoleMapNS</c> at all or no entry for this specific role), and
        /// <c>CannotDetermine</c> (a malformed or unresolvable step in the chain, or the hop cap
        /// reached). Only <c>NotStandard</c> is reported — the same fail-safe direction every
        /// other clause in this file takes on an ambiguous read; a chain this checker gave up
        /// chasing is not evidence of anything, and reporting it would claim certainty this
        /// checker does not have.</para>
        /// <para>An absent <c>/NS</c> on the element itself is not walked at all — §14.8.6.1's
        /// own default namespace is PDF 1.7 (the same finding <see cref="CheckDocumentStructureType"/>'s
        /// remarks already establish), so it is compliant by definition, trivially.</para>
        /// </remarks>
        private static void CheckElementBelongsToStandardNamespace(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var structTreeRoot = ResolveStructTreeRoot(doc, catalog);
            if (structTreeRoot == null) return;

            int found = 0;
            StructTreeWalker.Walk(structTreeRoot, doc, index: null, node =>
            {
                if (found > IndividualReportLimit) return;
                if (node.Dict == null || node.Role == null) return;

                var nsDict = Resolve(doc, node.Dict[PdfName.NS]) as PdfDictionary;
                if (nsDict == null) return; // absent /NS — defaults to PDF 1.7, compliant

                if (ResolveNamespaceMembership(doc, nsDict, node.Role, 0) != NamespaceMembership.NotStandard)
                    return;

                found++;
                if (found > IndividualReportLimit) return;
                v.Add(new PdfUa2StructureViolation("UA2-8.2.4",
                    $"A {node.Role} structure element's namespace is not one of the PDF 1.7, " +
                    "PDF 2.0 or MathML namespaces, and it has no role map reaching one of them; " +
                    "§8.2.4 requires every structure element to belong to a standard namespace, " +
                    "possibly through role mapping (ISO 14289-2 §8.2.4)."));
            });

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.2.4",
                    $"…and at least one more, not listed individually."));
        }

        private enum NamespaceMembership { Standard, NotStandard, CannotDetermine }

        private static NamespaceMembership ResolveNamespaceMembership(
            PdfDocument doc, PdfDictionary nsDict, string role, int hops)
        {
            if (hops > PdfRoleMap.MaxHops) return NamespaceMembership.CannotDetermine;

            string ownName = (Resolve(doc, nsDict[PdfName.NS]) as PdfString)?.GetText();
            if (ownName == null) return NamespaceMembership.CannotDetermine;
            if (ownName == Pdf17StandardStructureNamespace
                || ownName == Pdf2StandardStructureNamespace
                || ownName == MathMlNamespace)
                return NamespaceMembership.Standard;

            var roleMapNs = Resolve(doc, nsDict[PdfName.RoleMapNS]) as PdfDictionary;
            if (roleMapNs == null) return NamespaceMembership.NotStandard;

            var mapped = Resolve(doc, roleMapNs[new PdfName(role)]);
            if (mapped == null) return NamespaceMembership.NotStandard;
            if (mapped is PdfName) return NamespaceMembership.Standard; // targets the default namespace

            if (mapped is PdfArray arr && arr.Count >= 2)
            {
                string targetRole = (Resolve(doc, arr[0]) as PdfName)?.Value;
                var targetNsDict = Resolve(doc, arr[1]) as PdfDictionary;
                if (targetRole == null || targetNsDict == null) return NamespaceMembership.CannotDetermine;
                return ResolveNamespaceMembership(doc, targetNsDict, targetRole, hops + 1);
            }

            return NamespaceMembership.CannotDetermine;
        }
    }

    // ----------------------------------------------------------------
    //  Result types
    // ----------------------------------------------------------------

    /// <summary>Result of a PDF/UA-2 structure-tree deep check.</summary>
    public sealed class PdfUa2StructureResult
    {
        /// <summary>List of violations found. Empty list means this checker's rules all passed.</summary>
        public IReadOnlyList<PdfUa2StructureViolation> Violations { get; }

        /// <summary>
        /// True when this checker found no violations.
        ///
        /// <para>
        /// <b>Not a PDF/UA-2 conformance certificate.</b> This checker implements these
        /// clauses (§8.2.2 with §8.3.1 for shown text, §8.2.5.29 and a second from §8.2.5.29.1,
        /// §8.10.2.3, a second from §8.6 and a second from §8.4.5.5.1 (glyphs), §8.2.5.2, §8.2.5.12, two from §8.2.5.14.1, §8.2.5.14.2, §8.2.5.23, §8.2.5.24, §8.2.5.27, §8.2.5.28.2,
        /// three from §8.2.5.25, §8.2.5.29.1, §8.2.5.21/§8.9.2.2, §8.2.5.22/§8.10.1, two from §8.9.2.1,
        /// §8.9.3.3, two from §8.10.1, §8.9.4.2, §8.9.2.4.9, §8.9.2.4.11, §8.9.2.4.15, §8.9.2.4.14,
        /// §8.9.2.4.12, §8.9.2.4.19, §8.9.2.4.13, §8.9.2.4.10, §8.9.2.4.8, §8.10.3.3, §8.4.4, two
        /// from §8.7, §8.11.2, §8.14.1, §8.11.1, §8.4.5.3.2, four from §8.4.5.7, §8.4.5.3.1, two from §8.4.5.8,
        /// §8.9.2.3, §8.2.5.20, §8.2.5.8, §8.2.5.26, §8.10.2.4, §8.4.5.4, §8.6, §8.4.5.6, three from §8.8,
        /// §8.4.5.9, §6.2, two from §8.2.4, §8.2.6.4, §8.4.5.5.1) of the standard's full clause
        /// set — every one of the thirty-two subclauses of §8.2.5 is accounted for (checked
        /// outright, or,
        /// for §8.2.5.21/§8.2.5.22, closed via the specific §8.9/§8.10 sub-clause that names the
        /// same structure type — the same deferral shape, applied twice), §8.2.4 is closed in full
        /// (both its narrow same-namespace sentence and its harder transitive-membership opening
        /// sentence), and §8.4.5.9 covers simple TrueType fonts too, via <c>/Differences</c>
        /// only (<see cref="PdfTrueTypeFont.DifferenceResolvesToNotdef"/>) — its symbol/Mac
        /// <c>cmap</c> half stays permanently unchecked, since a TrueType <c>cmap</c> subtable's
        /// own "no entry" sentinel is bitwise identical to an explicit mapping to glyph 0, and
        /// Type 1/Type 3 fonts are checked for a <c>/Differences</c> name that is <c>.notdef</c>
        /// itself (a name their program lacks is not) — leaving only the rest of
        /// Clause 6 and §8.3-8.14's other clauses (§8.4.5.5.1's own §8.4.5.5.2 subset-embedding
        /// sentence is non-normative and not attempted, and its "legally embeddable" sentence has
        /// no file-level signal to check); see this type's own remarks and GAPS.md §22d for what
        /// remains there.
        /// </para>
        /// </summary>
        public bool IsConforming => Violations.Count == 0;

        internal PdfUa2StructureResult(List<PdfUa2StructureViolation> violations)
            => Violations = violations;
    }

    /// <summary>A single PDF/UA-2 structure-tree violation.</summary>
    public sealed class PdfUa2StructureViolation
    {
        /// <summary>Check code (e.g. "UA2-8.2.5.2"), echoing the standard's own clause number —
        /// never in the UA00-UA99 range PDF/UA-1 uses, and never the bare "UA2-5" Clause-5 code
        /// <c>Plugins.Conformance.PdfUa2Checker</c> reports, so a finding is always traceable to
        /// which checker and which clause produced it.</summary>
        public string Code { get; }
        /// <summary>Human-readable description of the violation.</summary>
        public string Message { get; }

        internal PdfUa2StructureViolation(string code, string message)
        {
            Code = code;
            Message = message;
        }

        public override string ToString() => $"[{Code}] {Message}";
    }
}
