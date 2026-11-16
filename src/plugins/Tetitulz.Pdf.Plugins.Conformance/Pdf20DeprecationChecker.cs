// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// Which deprecated-in-PDF-2.0 construct a <see cref="Pdf20DeprecationFinding"/> names.
    /// </summary>
    public enum Pdf20DeprecatedConstruct
    {
        /// <summary><c>/AcroForm /XFA</c> (ISO 32000-2:2020 §0.3).</summary>
        Xfa,
        /// <summary>A <c>Movie</c>, <c>Sound</c> or <c>TrapNet</c> annotation.</summary>
        MovieSoundOrTrapNetAnnotation,
        /// <summary>The trailer's <c>/Info</c> dictionary — PDF 2.0 prefers XMP metadata.</summary>
        InfoDictionary,
        /// <summary>A resource dictionary's <c>/ProcSet</c> entry.</summary>
        ProcSet,
        /// <summary>A signature's <c>/SubFilter</c> of <c>adbe.pkcs7.sha1</c> or
        /// <c>adbe.x509.rsa_sha1</c>.</summary>
        LegacySignatureSubFilter,
        /// <summary>A non-default <c>/TR</c> or <c>/TR2</c> transfer function in an
        /// <c>/ExtGState</c>.</summary>
        TransferFunctionInGraphicsState,
        /// <summary><c>/AcroForm /NeedAppearances true</c>.</summary>
        NeedAppearances,
        /// <summary>The catalog's <c>/MarkInfo /Suspects</c> entry (any value).</summary>
        SuspectsFlag,
        /// <summary>A prepress <c>/ViewerPreferences</c> entry: <c>/ViewArea</c>, <c>/ViewClip</c>,
        /// <c>/PrintArea</c> or <c>/PrintClip</c>.</summary>
        PrepressViewerPreference,
        /// <summary>An <c>/ExtGState</c>'s <c>/BM</c> given as an array of blend-mode names rather
        /// than a single name.</summary>
        BlendModeArray,
        /// <summary>An <c>/OPI</c> entry on an image or form XObject dictionary.</summary>
        Opi,
        /// <summary>A Type1 font's FontDescriptor <c>/CharSet</c> entry.</summary>
        CharSet,
        /// <summary>A CIDFont's FontDescriptor <c>/CIDSet</c> entry.</summary>
        CidSet,
        /// <summary>The catalog's <c>/Names /AlternatePresentations</c> entry.</summary>
        AlternatePresentations,
        /// <summary>The catalog's <c>/Perms /UR3</c> usage-rights signature.</summary>
        UsageRights,
        /// <summary>The <c>/Encrypt</c> dictionary's <c>/P</c> value clears bit 10 (extract for
        /// accessibility) — restricting assistive-technology text/graphics extraction via DRM.</summary>
        AssistiveTechnologyDrmRestriction,
        /// <summary>A <c>/Name</c> entry directly on an image XObject, form XObject or font
        /// dictionary — the PDF 1.0-era self-naming mechanism, redundant with the resource
        /// dictionary's own key.</summary>
        NamedResource,
        /// <summary>A <c>/DOS</c>, <c>/Mac</c> or <c>/Unix</c> entry on a file specification
        /// dictionary.</summary>
        OsSpecificFileSpecification,
        /// <summary>A <c>Movie</c> or <c>Sound</c> action (<c>/S /Movie</c> or <c>/S /Sound</c>),
        /// reachable from <c>/OpenAction</c>, an <c>/AA</c> trigger, a field <c>/A</c>/<c>/AA</c>,
        /// or a <c>/Next</c> chain — as opposed to the Movie/Sound <i>annotations</i>
        /// <see cref="MovieSoundOrTrapNetAnnotation"/> already covers.</summary>
        MovieOrSoundAction,
        /// <summary>A <c>/Launch</c> action's <c>/Win</c>, <c>/Mac</c> or <c>/Unix</c> entry —
        /// platform-specific launch parameters, deprecated in PDF 2.0 in favour of the
        /// platform-neutral <c>/F</c> entry alone.</summary>
        OsSpecificLaunchAction,
    }

    /// <summary>One deprecated construct found in the document, and where.</summary>
    public readonly struct Pdf20DeprecationFinding
    {
        internal Pdf20DeprecationFinding(Pdf20DeprecatedConstruct construct, int pageIndex, string detail)
        {
            Construct = construct;
            PageIndex = pageIndex;
            Detail = detail;
        }

        /// <summary>Which deprecated construct this is.</summary>
        public Pdf20DeprecatedConstruct Construct { get; }

        /// <summary>
        /// The zero-based page the construct was found on, or <c>-1</c> for a document-level
        /// construct (the trailer's <c>/Info</c> dictionary, or a signature reached through the
        /// AcroForm field tree rather than a specific page).
        /// </summary>
        public int PageIndex { get; }

        /// <summary>A human-readable description, e.g. the annotation subtype or subfilter name.</summary>
        public string Detail { get; }

        public override string ToString()
            => PageIndex >= 0
                ? $"{Construct} (page {PageIndex}): {Detail}"
                : $"{Construct}: {Detail}";
    }

    /// <summary>
    /// Warns a <b>producer</b> against constructs ISO 32000-2:2020 §0.3 lists as deprecated in
    /// PDF 2.0 — a hygiene check, not a conformance gate.
    /// </summary>
    /// <remarks>
    /// <para><b>Reading and verifying an old file using these constructs must not change.</b> A
    /// document written under PDF 1.7 that uses <c>/ProcSet</c> or the legacy signature subfilters
    /// is perfectly conforming for its own version; nothing here marks a document non-conformant,
    /// unlike <see cref="PdfUaChecker"/> or <see cref="PdfXValidator"/>. This is advisory only —
    /// worth surfacing to a caller who is <i>authoring</i> a new PDF 2.0 document and might not know
    /// a construct they reached for has a preferred replacement.</para>
    ///
    /// <para><b>22 of the 23 constructs ISO 32000-2:2020 §0.3 lists</b> —
    /// deliberately scoped to the ones with an unambiguous, single-dictionary-read detection rule,
    /// each verified against the standard's own text. Ten against ISO 32000-2's §0.3
    /// (<c>docs/iso/iso32000-2_intro.txt</c>), two of them cross-checked against
    /// <see href="https://www.pdflib.com/fileadmin/pdflib/pdf/manuals/PDFlib-11.0-migration-guide.pdf">
    /// PDFlib's own migration guide</see>, Table 2.5, which cites ISO 32000-2 by name. OPI, CharSet,
    /// CIDSet, AlternatePresentations and UsageRights against the ISO 32000-1:2008 text
    /// (<c>docs/iso/PDF32000_2008.pdf</c>), each at the table or clause cited at its check method.
    /// <see cref="Pdf20DeprecatedConstruct.AssistiveTechnologyDrmRestriction"/> (the bit
    /// <c>Base.Encryption.PdfPermissions.ExtractForAccessibility</c> models),
    /// <see cref="Pdf20DeprecatedConstruct.NamedResource"/> — which covers both "Names for
    /// XObjects" and "Names for Fonts" — and
    /// <see cref="Pdf20DeprecatedConstruct.OsSpecificFileSpecification"/> against the ISO 32000-2
    /// text, as are <see cref="Pdf20DeprecatedConstruct.MovieOrSoundAction"/> and
    /// <see cref="Pdf20DeprecatedConstruct.OsSpecificLaunchAction"/>
    /// (<c>docs/iso/ISO_32000-2_sponsored_EC3.pdf</c>: Table 201 "Action types" —
    /// <c>Sound</c>/<c>Movie</c>, both "deprecated in PDF 2.0"; Table 207 "Additional entries
    /// specific to a launch action" — <c>Win</c>/<c>Mac</c>/<c>Unix</c>, each "Optional; deprecated
    /// in PDF 2.0"). The resource checks read every resource dictionary through
    /// <see cref="PdfXValidator.ScanPage"/> (see <see cref="Check(PdfDocument)"/>'s per-page
    /// loop); the action checks read
    /// <see cref="Base.Operations.PdfActionDispatcher.ExtractActions(PdfDocument)"/>'s exhaustive,
    /// DoS-guarded action-location walk (catalog <c>/OpenAction</c>, catalog and page-level
    /// <c>/AA</c>, AcroForm field <c>/A</c>/<c>/AA</c>, <c>/Next</c> chains); see
    /// <see cref="CheckActionDeprecations"/>.
    /// Left out: XFA's sibling <c>NeedsRendering</c> (folded into the XFA check, since a document with
    /// no <c>/XFA</c> cannot carry it); and FDF encryption, which is out of scope in kind rather than
    /// merely unimplemented — FDF is a distinct file format, not a construct reachable from inside
    /// the <see cref="PdfDocument"/> or <see cref="Stream"/> this checker is given. Extend this list
    /// rather than starting a second checker.</para>
    /// </remarks>
    public static class Pdf20DeprecationChecker
    {
        /// <summary>Opens <paramref name="input"/> and checks it. See <see cref="Check(PdfDocument)"/>.</summary>
        public static IReadOnlyList<Pdf20DeprecationFinding> Check(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            using var doc = PdfDocument.Open(input);
            return Check(doc);
        }

        /// <summary>
        /// Scans an already-open document for the deprecated constructs this checker covers.
        /// Never throws on malformed input — an unreadable dictionary is simply not counted as a
        /// finding, consistent with this being advisory rather than a pass/fail gate.
        /// </summary>
        public static IReadOnlyList<Pdf20DeprecationFinding> Check(PdfDocument doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));

            var findings = new List<Pdf20DeprecationFinding>();

            CheckXfa(doc, findings);
            CheckInfoDictionary(doc, findings);
            CheckLegacySignatureSubFilters(doc, findings);
            CheckNeedAppearances(doc, findings);
            CheckSuspectsFlag(doc, findings);
            CheckPrepressViewerPreferences(doc, findings);
            CheckAlternatePresentations(doc, findings);
            CheckUsageRights(doc, findings);
            CheckAssistiveTechnologyDrmRestriction(doc, findings);
            CheckOsSpecificFileSpecifications(doc, findings);
            CheckActionDeprecations(doc, findings);

            // Every per-page check below reads scan.Resources — every resource dictionary
            // reachable from the page, not just page.Resources directly — via the same shared
            // walk PdfXValidator's X04/X06/X08/X09 cluster uses (ScanPage is internal, same
            // assembly), so a construct nested inside a Form XObject, a tiling pattern or an
            // annotation appearance stream is reported. collectContent: false because none of
            // these checks need content-stream operators, only resource dictionaries.
            long documentBytes = 0;
            var seenFonts = new HashSet<PdfDictionary>();
            var readOnce = new ReadOnce();
            var seenAnnotations = new HashSet<object>(PdfReferenceEquality.Instance);

            // One scope for the page walks: pages that share their /Resources share the walk below
            // them (ScanReuse.SharedWalk) rather than each walking it again.
            using (ScanReuse.Begin())
            {
                for (int pageIndex = 0; pageIndex < doc.Pages.Count; pageIndex++)
                {
                    CheckAnnotations(doc, pageIndex, seenAnnotations, findings);

                    var scan = PdfXValidator.ScanPage(doc, doc.Pages[pageIndex],
                        PdfXValidator.DefaultMaxPageContentBytes, ref documentBytes,
                        PdfXValidator.DefaultMaxDocumentContentBytes, collectContent: false);

                    // scan.Incomplete (a partial-scan signal) is deliberately not reported here: this
                    // checker's own contract is "never throws on malformed input... simply not counted
                    // as a finding" (see the class remarks) — it has no A99/X99-style diagnostic slot,
                    // and adding one is a separate design decision from extending the construct list.
                    CheckProcSet(scan.Resources, pageIndex, findings);
                    CheckTransferFunctions(doc, scan.Resources, pageIndex, readOnce.TransferFunctions, findings);
                    CheckBlendModeArrays(doc, scan.Resources, pageIndex, readOnce.BlendModes, findings);
                    CheckOpi(doc, scan.Resources, pageIndex, readOnce.Opi, findings);
                    CheckFontCharSetAndCidSet(doc, scan.Resources, pageIndex, readOnce.FontDictionaries, seenFonts, findings);
                    CheckNamedResources(doc, scan.Resources, pageIndex, readOnce.NamedXObjects, readOnce.NamedFonts, findings);
                }
            }

            return findings;
        }

        /// <summary>
        /// <c>/AcroForm /NeedAppearances true</c> — deprecated in PDF 2.0 (ISO 32000-2:2020 §0.3;
        /// confirmed via ISO 32000-1:2008 §12.7.2, Table 225).
        /// </summary>
        /// <remarks>
        /// Presence is not deviation, the same rule this checker's own
        /// <see cref="CheckTransferFunctions"/> applies to <c>/TR</c>/<c>/TR2</c>: the key's own
        /// default is <c>false</c>, and a writer asserting that default explicitly has not invoked
        /// the deprecated behaviour (requiring every viewer to regenerate every field's appearance).
        /// Only an explicit <c>true</c> does.
        /// </remarks>
        private static void CheckNeedAppearances(PdfDocument doc, List<Pdf20DeprecationFinding> findings)
        {
            var acroForm = Resolve<PdfDictionary>(doc, doc.Catalog?.Dictionary?[PdfName.AcroForm]);
            if (acroForm == null) return;

            if (Resolve<PdfBoolean>(doc, acroForm[PdfName.NeedAppearances])?.Value == true)
            {
                findings.Add(new Pdf20DeprecationFinding(
                    Pdf20DeprecatedConstruct.NeedAppearances, -1,
                    "/AcroForm /NeedAppearances true"));
            }
        }

        /// <summary>
        /// The catalog's <c>/MarkInfo /Suspects</c> entry — deprecated in PDF 2.0 (ISO 32000-2:2020
        /// §0.3; confirmed via ISO 32000-1:2008 §14.8.2.3, Table 321). Unlike <c>/NeedAppearances</c>,
        /// the whole key is the deprecated construct (mirroring this checker's own <c>/AcroForm
        /// /XFA</c> and <c>/Info</c> checks) — a PDF 2.0 producer is not expected to need a flag for
        /// "this document's tagging has not been verified" at all, so its mere presence is reported
        /// regardless of value.
        /// </summary>
        private static void CheckSuspectsFlag(PdfDocument doc, List<Pdf20DeprecationFinding> findings)
        {
            var markInfo = Resolve<PdfDictionary>(doc, doc.Catalog?.Dictionary?[PdfName.MarkInfo]);
            if (markInfo == null) return;

            if (markInfo[PdfName.Suspects] != null)
            {
                findings.Add(new Pdf20DeprecationFinding(
                    Pdf20DeprecatedConstruct.SuspectsFlag, -1, "/MarkInfo /Suspects is present"));
            }
        }

        private static readonly PdfName[] PrepressViewerPreferenceKeys =
            { PdfName.ViewArea, PdfName.ViewClip, PdfName.PrintArea, PdfName.PrintClip };

        /// <summary>
        /// <c>/ViewerPreferences</c>' prepress entries — deprecated in PDF 2.0 (ISO 32000-2:2020
        /// §0.3, "Prepress viewer preferences (ViewArea, ViewClip, etc.)"; confirmed via
        /// <see href="https://www.pdflib.com/fileadmin/pdflib/pdf/manuals/PDFlib-11.0-migration-guide.pdf">
        /// PDFlib's migration guide</see> Table 2.5, which names all four keys and cites ISO 32000-2
        /// directly).
        /// </summary>
        private static void CheckPrepressViewerPreferences(PdfDocument doc, List<Pdf20DeprecationFinding> findings)
        {
            var prefs = Resolve<PdfDictionary>(doc, doc.Catalog?.Dictionary?[PdfName.ViewerPreferences]);
            if (prefs == null) return;

            foreach (var key in PrepressViewerPreferenceKeys)
            {
                if (prefs[key] != null)
                {
                    findings.Add(new Pdf20DeprecationFinding(
                        Pdf20DeprecatedConstruct.PrepressViewerPreference, -1,
                        $"/ViewerPreferences /{key.Value} is present"));
                }
            }
        }

        /// <summary>
        /// An <c>/ExtGState</c>'s <c>/BM</c> given as an array — deprecated in PDF 2.0 (ISO
        /// 32000-2:2020 §0.3, "Arrays of Blend Modes"; confirmed via PDFlib's migration guide
        /// Table 2.5: "blendmode accepts only a single keyword, but not a list with multiple
        /// values"). ISO 32000-1 §8.4.5.2 read a <c>/BM</c> array as a preference list — a viewer
        /// tries each name until it finds one it supports; PDF 2.0 requires a single name instead.
        /// </summary>
        private static void CheckBlendModeArrays(
            PdfDocument doc, List<PdfDictionary> resources, int pageIndex, HashSet<PdfDictionary> read,
            List<Pdf20DeprecationFinding> findings)
        {
            foreach (var res in resources)
            {
                var extGStates = Resolve<PdfDictionary>(doc, res[PdfName.ExtGState]);
                if (extGStates == null || !ScanReuse.FirstVisit(read, extGStates)) continue;

                foreach (var kvp in extGStates)
                {
                    var gs = Resolve<PdfDictionary>(doc, kvp.Value);
                    if (gs == null) continue;

                    if ((doc.ResolveObject(gs[PdfName.BM]) ?? gs[PdfName.BM]) is PdfArray)
                    {
                        findings.Add(new Pdf20DeprecationFinding(
                            Pdf20DeprecatedConstruct.BlendModeArray, pageIndex,
                            $"/ExtGState /{kvp.Key.Value} /BM is an array"));
                    }
                }
            }
        }

        /// <summary>
        /// <c>/OPI</c> on an image or form XObject dictionary — deprecated in PDF 2.0 (ISO
        /// 32000-2:2020 §0.3; confirmed via ISO 32000-1:2008 §14.11.7, Table 368, and Table 89
        /// (image XObjects) / Table 95 (form XObjects), both of which carry the entry — so this does
        /// not subtype-filter). Mirrors <c>PdfXValidator</c>'s X06 exactly (same table, same
        /// dictionary key), which is the PDF/X-1a/X-3 conformance version of the same rule.
        /// </summary>
        private static void CheckOpi(
            PdfDocument doc, List<PdfDictionary> resources, int pageIndex, HashSet<PdfDictionary> read,
            List<Pdf20DeprecationFinding> findings)
        {
            foreach (var res in resources)
            {
                var xoDict = Resolve<PdfDictionary>(doc, res[PdfName.XObject]);
                if (xoDict == null || !ScanReuse.FirstVisit(read, xoDict)) continue;

                foreach (var kvp in xoDict)
                {
                    var xo = Resolve<PdfDictionary>(doc, kvp.Value);
                    if (xo != null && xo[PdfName.OPI] != null)
                    {
                        findings.Add(new Pdf20DeprecationFinding(
                            Pdf20DeprecatedConstruct.Opi, pageIndex,
                            $"/XObject /{kvp.Key.Value} /OPI is present"));
                    }
                }
            }
        }

        /// <summary>
        /// A <c>/Name</c> entry directly on an image XObject, form XObject or font dictionary —
        /// deprecated in PDF 2.0 (ISO 32000-2:2020 §0.3, "Names for XObjects"/"Names for Fonts";
        /// confirmed against the raw ISO 32000-2 text itself, not just ISO 32000-1: "(Required in
        /// PDF 1.0; optional otherwise; deprecated in PDF 2.0) The name by which this
        /// [image/form] XObject is referenced in the XObject subdictionary of the current resource
        /// dictionary" and the equivalent line for a font's own <c>/Name</c>). A PDF 1.0-era
        /// self-naming mechanism, redundant with the resource dictionary's own key — the key this
        /// checker already reads to reach the dictionary in the first place.
        /// </summary>
        private static void CheckNamedResources(
            PdfDocument doc, List<PdfDictionary> resources, int pageIndex,
            HashSet<PdfDictionary> readXObjects, HashSet<PdfDictionary> readFonts, List<Pdf20DeprecationFinding> findings)
        {
            foreach (var res in resources)
            {
                var xoDict = Resolve<PdfDictionary>(doc, res[PdfName.XObject]);
                if (xoDict != null && ScanReuse.FirstVisit(readXObjects, xoDict))
                {
                    foreach (var kvp in xoDict)
                    {
                        var xo = Resolve<PdfDictionary>(doc, kvp.Value);
                        if (xo != null && xo[PdfName.Name] != null)
                        {
                            findings.Add(new Pdf20DeprecationFinding(
                                Pdf20DeprecatedConstruct.NamedResource, pageIndex,
                                $"/XObject /{kvp.Key.Value} /Name is present"));
                        }
                    }
                }

                var fontDict = Resolve<PdfDictionary>(doc, res[PdfName.Font]);
                if (fontDict != null && ScanReuse.FirstVisit(readFonts, fontDict))
                {
                    foreach (var kvp in fontDict)
                    {
                        var font = Resolve<PdfDictionary>(doc, kvp.Value);
                        if (font != null && font[PdfName.Name] != null)
                        {
                            findings.Add(new Pdf20DeprecationFinding(
                                Pdf20DeprecatedConstruct.NamedResource, pageIndex,
                                $"/Font /{kvp.Key.Value} /Name is present"));
                        }
                    }
                }
            }
        }

        /// <summary>
        /// A Type1 font's FontDescriptor <c>/CharSet</c> (Table 122) or a CIDFont's
        /// <c>/CIDSet</c> (Table 124, reached through a Type0 font's <c>/DescendantFonts</c>) —
        /// both deprecated in PDF 2.0 (ISO 32000-2:2020 §0.3), confirmed via ISO 32000-1:2008.
        /// </summary>
        /// <remarks>
        /// Drives <see cref="FontEmbeddingProbe.WalkDescriptors"/> (shared with A06/UA25/X04's
        /// embedded-or-not walk) rather than re-deriving the Type0/Type3/cycle-guard/depth-cap
        /// handling a fourth time — see that method's remarks for why. <paramref name="seenFonts"/>
        /// is document-scoped, matching <c>PdfXValidator.CheckFontEmbedding</c>'s own
        /// <c>seenFonts</c>: one font object is normally referenced from several resource
        /// dictionaries and, for a font used on multiple pages, from several pages' scans too — a
        /// finding is attributed to whichever page reaches the font object first.
        /// </remarks>
        private static void CheckFontCharSetAndCidSet(
            PdfDocument doc, List<PdfDictionary> resources, int pageIndex, HashSet<PdfDictionary> read,
            HashSet<PdfDictionary> seenFonts, List<Pdf20DeprecationFinding> findings)
        {
            foreach (var res in resources)
            {
                var fontDict = Resolve<PdfDictionary>(doc, res[PdfName.Font]);
                if (fontDict == null || !ScanReuse.FirstVisit(read, fontDict)) continue;

                foreach (var kvp in fontDict)
                {
                    var fontObj = doc.ResolveObject(kvp.Value) as PdfDictionary ?? kvp.Value as PdfDictionary;
                    if (fontObj == null) continue;
                    if (!seenFonts.Add(fontObj)) continue;

                    string resourceKey = kvp.Key.Value;
                    FontEmbeddingProbe.WalkDescriptors(fontObj, doc, (leaf, subtype, descriptor) =>
                    {
                        if (descriptor == null) return;

                        if (subtype == "Type1" && descriptor[PdfName.CharSet] != null)
                        {
                            findings.Add(new Pdf20DeprecationFinding(
                                Pdf20DeprecatedConstruct.CharSet, pageIndex,
                                $"/{resourceKey} /FontDescriptor /CharSet is present"));
                        }
                        else if ((subtype == "CIDFontType0" || subtype == "CIDFontType2")
                            && descriptor[PdfName.CIDSet] != null)
                        {
                            findings.Add(new Pdf20DeprecationFinding(
                                Pdf20DeprecatedConstruct.CidSet, pageIndex,
                                $"/{resourceKey} /FontDescriptor /CIDSet is present"));
                        }
                    }, diagnostic: null, new HashSet<PdfDictionary>(), 0);
                }
            }
        }

        /// <summary>
        /// The catalog's <c>/Names /AlternatePresentations</c> entry — deprecated in PDF 2.0 (ISO
        /// 32000-2:2020 §0.3; confirmed via ISO 32000-1:2008 §13.5, Table 31). Presence-only: the
        /// entry names a name tree of alternate presentations, and it is the entry itself PDF 2.0
        /// deprecates, not any particular presentation inside it — no need to walk the tree.
        /// </summary>
        private static void CheckAlternatePresentations(PdfDocument doc, List<Pdf20DeprecationFinding> findings)
        {
            var names = Resolve<PdfDictionary>(doc, doc.Catalog?.Dictionary?[PdfName.Names]);
            if (names == null) return;

            if (names[PdfName.AlternatePresentations] != null)
            {
                findings.Add(new Pdf20DeprecationFinding(
                    Pdf20DeprecatedConstruct.AlternatePresentations, -1,
                    "/Names /AlternatePresentations is present"));
            }
        }

        /// <summary>
        /// The catalog's <c>/Perms /UR3</c> entry — deprecated in PDF 2.0 (ISO 32000-2:2020 §0.3,
        /// "UR signatures"; confirmed via ISO 32000-1:2008 §12.8.4, Table 258, where <c>/UR3</c> is
        /// the permissions dictionary's usage-rights signature). Presence-only, matching
        /// <see cref="CheckSuspectsFlag"/>'s reasoning: the signature mechanism itself is what PDF
        /// 2.0 deprecates.
        /// </summary>
        private static void CheckUsageRights(PdfDocument doc, List<Pdf20DeprecationFinding> findings)
        {
            var perms = Resolve<PdfDictionary>(doc, doc.Catalog?.Dictionary?[PdfName.Perms]);
            if (perms == null) return;

            if (perms[PdfName.UR3] != null)
            {
                findings.Add(new Pdf20DeprecationFinding(
                    Pdf20DeprecatedConstruct.UsageRights, -1, "/Perms /UR3 is present"));
            }
        }

        /// <summary>Bit 10 (value 1&lt;&lt;9) of the <c>/Encrypt</c> dictionary's <c>/P</c> value —
        /// "extract text and graphics for accessibility" (ISO 32000-1:2008 §7.6.3.2, Table 22;
        /// already modelled as <c>Tetitulz.Pdf.Base.Encryption.PdfPermissions.ExtractForAccessibility</c>).
        /// Read directly off the raw <c>/P</c> integer here rather than depending on
        /// <c>Base.Encryption</c>, which this plugin does not reference.</summary>
        private const int ExtractForAccessibilityBit = 1 << 9;

        /// <summary>
        /// Clearing bit 10 of <c>/Encrypt /P</c> — restricting assistive-technology text/graphics
        /// extraction — is deprecated in PDF 2.0 (ISO 32000-2:2020 §0.3, "Assistive technology
        /// restrictions via DRM"). <c>/P</c> is a plain, unencrypted integer in the trailer's
        /// <c>/Encrypt</c> dictionary regardless of whether the document's content streams are
        /// themselves encrypted, so this reads it directly without needing a password or a
        /// registered decryptor.
        /// </summary>
        private static void CheckAssistiveTechnologyDrmRestriction(PdfDocument doc, List<Pdf20DeprecationFinding> findings)
        {
            var encrypt = Resolve<PdfDictionary>(doc, doc.Trailer?[PdfName.Encrypt]);
            if (encrypt == null) return;

            // PdfPermissionValue, as both PDF/UA checkers read it: /P is a 32-bit flag word that
            // producers write in its unsigned spelling (/P 4294963392 for -3904), and IntValue
            // would saturate that to int.MaxValue — every bit set, so bit 10 would read as granted
            // on exactly the documents this check exists for.
            int? p = PdfPermissionValue.FromNumber(Resolve<PdfNumber>(doc, encrypt[PdfName.P]));
            if (p == null) return;

            if ((p.Value & ExtractForAccessibilityBit) == 0)
            {
                findings.Add(new Pdf20DeprecationFinding(
                    Pdf20DeprecatedConstruct.AssistiveTechnologyDrmRestriction, -1,
                    "/Encrypt /P clears bit 10 (extract for accessibility)"));
            }
        }

        /// <summary>
        /// <c>/DOS</c>, <c>/Mac</c> or <c>/Unix</c> on a file specification dictionary — obsolescent
        /// since ISO 32000-1:2008 §7.11.3 Table 44 ("This entry is obsolescent and should not be
        /// used by conforming writers") and deprecated outright in PDF 2.0 (ISO 32000-2:2020 §0.3,
        /// "OS-specific file specifications"). Walks the two well-established places a file
        /// specification is reachable from the catalog without a page-content scan: the <c>/AF</c>
        /// array (ISO 32000-2's own recommended inventory of associated files) and the
        /// <c>/Names /EmbeddedFiles</c> name tree, via the shared, budget-guarded
        /// <see cref="Core.Document.PdfNameTree.TryFlatten"/> rather than a new walk. Deliberately
        /// does not chase file specifications reachable only through annotation-level
        /// <c>/FileAttachment</c> or through a Launch action's <c>/F</c> — the same "common case, not
        /// exhaustive" scope this checker already accepts for Movie/Sound annotations versus
        /// actions.
        /// </summary>
        private static void CheckOsSpecificFileSpecifications(PdfDocument doc, List<Pdf20DeprecationFinding> findings)
        {
            var catalog = doc.Catalog?.Dictionary;
            if (catalog == null) return;

            var seen = new HashSet<PdfDictionary>();

            void CheckFilespec(PdfDictionary fs)
            {
                if (fs == null || !seen.Add(fs)) return;
                if (fs[PdfName.DOS] != null || fs[PdfName.Mac] != null || fs[PdfName.Unix] != null)
                {
                    findings.Add(new Pdf20DeprecationFinding(
                        Pdf20DeprecatedConstruct.OsSpecificFileSpecification, -1,
                        "a file specification dictionary carries /DOS, /Mac or /Unix"));
                }
            }

            var af = Resolve<PdfArray>(doc, catalog[PdfName.AF]);
            if (af != null)
            {
                for (int i = 0; i < af.Count; i++)
                    CheckFilespec(Resolve<PdfDictionary>(doc, af[i]));
            }

            var namesDict = Resolve<PdfDictionary>(doc, catalog[PdfName.Names]);
            var efRoot = namesDict?[PdfName.EmbeddedFiles];
            if (efRoot != null)
            {
                var pairs = new List<KeyValuePair<PdfObject, PdfObject>>();
                int budget = PdfLimits.MaxNameTreeEntries;
                Core.Document.PdfNameTree.TryFlatten(efRoot, doc, pairs, ref budget);
                foreach (var kvp in pairs)
                    CheckFilespec(Resolve<PdfDictionary>(doc, kvp.Value));
            }
        }

        /// <summary>
        /// Movie/Sound <i>actions</i> (ISO 32000-2:2020 Table 201 "Action types": <c>Sound</c> —
        /// §12.6.4.9, <c>Movie</c> — §12.6.4.10, both "deprecated in PDF 2.0") and a
        /// <c>/Launch</c> action's platform-specific <c>/Win</c>/<c>/Mac</c>/<c>/Unix</c> entries
        /// (Table 207 "Additional entries specific to a launch action", each "Optional; deprecated
        /// in PDF 2.0" — the platform-neutral <c>/F</c> entry alone is preferred). Both share one
        /// call to <see cref="PdfActionDispatcher.ExtractActions(PdfDocument)"/>, which walks every
        /// action reachable from the document-level scripts, the catalog's <c>/OpenAction</c>,
        /// catalog and page-level <c>/AA</c>, AcroForm field <c>/A</c>/<c>/AA</c>, annotations'
        /// <c>/A</c>/<c>/AA</c>, outline items' <c>/A</c>, and <c>/Next</c> chains, with its own
        /// depth/budget/cycle guards, so a link's Movie action — the commonest place for one — is
        /// reached. <c>/S /Movie</c>/<c>/S /Sound</c>
        /// are read directly off <see cref="PdfAction.Dictionary"/> rather than through
        /// <see cref="PdfAction.ActionType"/>, because neither maps to a named
        /// <see cref="PdfActionType"/> case — they are exactly the two the dispatcher's own switch
        /// treats as <see cref="PdfActionType.Unknown"/>, which is correct for a component that
        /// <i>runs</i> actions (nothing there needs to run a deprecated one) but not for one
        /// reporting on which deprecated construct a document uses.
        /// </summary>
        private static void CheckActionDeprecations(PdfDocument doc, List<Pdf20DeprecationFinding> findings)
        {
            IReadOnlyList<PdfAction> actions;
            try
            {
                actions = PdfActionDispatcher.ExtractActions(doc);
            }
            catch (Exception)
            {
                // Consistent with this checker's own "never throws on malformed input" contract
                // (see the class remarks): a document whose action graph cannot be walked simply
                // contributes no findings from this check, the same as an unreadable dictionary
                // elsewhere in this file.
                return;
            }

            foreach (var action in actions)
            {
                var dict = action.Dictionary;
                if (dict == null) continue;

                // Resolved: GetName reads only a direct name, so `/S 9 0 R` hid a Movie action.
                string subtype = (doc.ResolveObject(dict[PdfName.S]) as PdfName)?.Value;
                // The action's own page: a link or an annotation's, or a page's /AA. -1 otherwise.
                if (subtype == "Movie" || subtype == "Sound")
                {
                    findings.Add(new Pdf20DeprecationFinding(
                        Pdf20DeprecatedConstruct.MovieOrSoundAction, action.PageIndex,
                        $"a /S /{subtype} action ({action.Trigger})"));
                    continue;
                }

                if (action.ActionType == PdfActionType.Launch
                    && (dict[PdfName.Win] != null || dict[PdfName.Mac] != null || dict[PdfName.Unix] != null))
                {
                    findings.Add(new Pdf20DeprecationFinding(
                        Pdf20DeprecatedConstruct.OsSpecificLaunchAction, action.PageIndex,
                        $"a /Launch action carries /Win, /Mac or /Unix ({action.Trigger})"));
                }
            }
        }

        private static void CheckXfa(PdfDocument doc, List<Pdf20DeprecationFinding> findings)
        {
            var acroForm = Resolve<PdfDictionary>(doc, doc.Catalog?.Dictionary?[PdfName.AcroForm]);
            if (acroForm == null) return;

            if (acroForm[PdfName.XFA] != null)
            {
                findings.Add(new Pdf20DeprecationFinding(
                    Pdf20DeprecatedConstruct.Xfa, -1, "/AcroForm /XFA is present"));
            }
        }

        private static void CheckInfoDictionary(PdfDocument doc, List<Pdf20DeprecationFinding> findings)
        {
            if (Resolve<PdfDictionary>(doc, doc.Trailer?[PdfName.Info]) != null)
            {
                findings.Add(new Pdf20DeprecationFinding(
                    Pdf20DeprecatedConstruct.InfoDictionary, -1,
                    "the trailer's /Info dictionary is present — PDF 2.0 prefers XMP metadata"));
            }
        }

        /// <summary>
        /// Reports each Movie, Sound or TrapNet annotation once, at the first page that reaches it: a
        /// shared <c>/Annots</c> array is read once, and every array under the validation's annotation
        /// ceiling (<see cref="ScanReuse.AdmitAnnotations"/>). Read once a page, a thousand pages sharing
        /// a thousand-entry array of one sound annotation made a million findings.
        /// </summary>
        private static void CheckAnnotations(PdfDocument doc, int pageIndex, HashSet<object> seen, List<Pdf20DeprecationFinding> findings)
        {
            var annots = doc.Pages[pageIndex].GetAnnotations();
            if (annots == null || !seen.Add(annots) || !ScanReuse.AdmitAnnotations(annots)) return;

            for (int i = 0; i < annots.Count; i++)
            {
                var annot = Resolve<PdfDictionary>(doc, annots[i]);
                if (annot == null || !seen.Add(annot)) continue;

                string subtype = Resolve<PdfName>(doc, annot[PdfName.Subtype])?.Value;
                if (subtype == PdfAnnotationSubtypes.Movie
                    || subtype == PdfAnnotationSubtypes.Sound
                    || subtype == PdfAnnotationSubtypes.TrapNet)
                {
                    findings.Add(new Pdf20DeprecationFinding(
                        Pdf20DeprecatedConstruct.MovieSoundOrTrapNetAnnotation, pageIndex,
                        $"/Subtype /{subtype} annotation"));
                }
            }
        }

        private static void CheckProcSet(
            List<PdfDictionary> resources, int pageIndex, List<Pdf20DeprecationFinding> findings)
        {
            foreach (var res in resources)
            {
                if (res[PdfName.ProcSet] != null)
                {
                    findings.Add(new Pdf20DeprecationFinding(
                        Pdf20DeprecatedConstruct.ProcSet, pageIndex, "/Resources /ProcSet is present"));
                    return; // one report per page is enough; a second nested one adds no information
                }
            }
        }

        /// <summary>
        /// The sub-dictionaries each resource check has read in this run. A check reads each one once,
        /// however many pages share it, and reports what it finds at the first page that reaches it —
        /// as <see cref="CheckFontCharSetAndCidSet"/> already did for a font. Read once a page, a
        /// dictionary the pages share cost pages × entries and reported each construct once a page:
        /// 800 pages sharing 800 entries took 2.6 s against 173 ms for 200.
        /// </summary>
        private sealed class ReadOnce
        {
            internal readonly HashSet<PdfDictionary> TransferFunctions = new HashSet<PdfDictionary>(PdfReferenceEquality.Instance);
            internal readonly HashSet<PdfDictionary> BlendModes = new HashSet<PdfDictionary>(PdfReferenceEquality.Instance);
            internal readonly HashSet<PdfDictionary> Opi = new HashSet<PdfDictionary>(PdfReferenceEquality.Instance);
            internal readonly HashSet<PdfDictionary> FontDictionaries = new HashSet<PdfDictionary>(PdfReferenceEquality.Instance);
            internal readonly HashSet<PdfDictionary> NamedXObjects = new HashSet<PdfDictionary>(PdfReferenceEquality.Instance);
            internal readonly HashSet<PdfDictionary> NamedFonts = new HashSet<PdfDictionary>(PdfReferenceEquality.Instance);
        }

        private static void CheckTransferFunctions(
            PdfDocument doc, List<PdfDictionary> resources, int pageIndex, HashSet<PdfDictionary> read,
            List<Pdf20DeprecationFinding> findings)
        {
            foreach (var res in resources)
            {
                var extGStates = Resolve<PdfDictionary>(doc, res[PdfName.ExtGState]);
                if (extGStates == null || !ScanReuse.FirstVisit(read, extGStates)) continue;

                foreach (var kvp in extGStates)
                {
                    var gs = Resolve<PdfDictionary>(doc, kvp.Value);
                    if (gs == null) continue;

                    // Presence is not deviation: /Default explicitly means "no transfer function",
                    // the same rule CLAUDE.md's IgnoredParameterReporting.Deviates applies to /HT,
                    // /BG2 and /UCR2. PDF/X's X14 asks the same question through the same helper.
                    foreach (var trKey in TransferFunctionEntries.Declared(doc, gs))
                    {
                        findings.Add(new Pdf20DeprecationFinding(
                            Pdf20DeprecatedConstruct.TransferFunctionInGraphicsState, pageIndex,
                            $"/ExtGState /{kvp.Key.Value} declares /{trKey.Value}"));
                    }
                }
            }
        }

        private static readonly HashSet<string> LegacySubFilters = new HashSet<string>(StringComparer.Ordinal)
        {
            "adbe.pkcs7.sha1",
            "adbe.x509.rsa_sha1",
        };

        private static void CheckLegacySignatureSubFilters(PdfDocument doc, List<Pdf20DeprecationFinding> findings)
        {
            var catalog = doc.Catalog?.Dictionary;
            if (catalog == null) return;

            var walk = AcroFormFieldWalk.Walk(catalog, obj => obj == null ? null : (doc.ResolveObject(obj) ?? obj));
            foreach (var field in walk.Fields)
            {
                if (field.FieldType?.Value != "Sig") continue;

                var sigDict = Resolve<PdfDictionary>(doc, field.Dictionary[PdfName.V]);
                string subFilter = Resolve<PdfName>(doc, sigDict?[PdfName.SubFilter])?.Value;
                if (subFilter != null && LegacySubFilters.Contains(subFilter))
                {
                    findings.Add(new Pdf20DeprecationFinding(
                        Pdf20DeprecatedConstruct.LegacySignatureSubFilter, -1,
                        $"/SubFilter /{subFilter}"));
                }
            }
        }

        private static T Resolve<T>(PdfDocument doc, PdfObject obj) where T : PdfObject
            => obj == null ? null : (doc.ResolveObject(obj) as T ?? obj as T);
    }
}
