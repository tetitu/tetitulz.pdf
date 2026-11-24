// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Base.Operations;

namespace Tetitulz.Pdf.Accessibility.Checking
{
    public static partial class PdfDeepAccessibilityChecker
    {
        // ================================================================
        //  Shared state
        // ================================================================

        private sealed class CheckContext
        {
            internal PdfDocument Doc;
            internal PdfObjectIndex Index;
            internal PdfDictionary Catalog;
            internal PdfDictionary StructTree;
            internal int StructTreeObjectNumber = -1;
            internal PdfDictionary MarkInfo;
            internal bool DeclaredMarked;
            internal bool ClaimsTagged;
            internal List<AccessibilityIssue> Issues;

            /// <summary>
            /// The document carries an /Encrypt dictionary, so every string and stream in it is
            /// ciphertext as far as this assembly is concerned — it does not reference
            /// <c>Tetitulz.Pdf.Encryption</c> and holds no key. Independent of UA06: a document
            /// whose permissions grant accessibility extraction is still not decrypted here.
            /// </summary>
            internal bool Encrypted;

            /// <summary>What the audit could not do because of <see cref="Encrypted"/>, in the order discovered.</summary>
            internal readonly List<string> EncryptionLimitations = new List<string>();

            internal readonly HashSet<string> EncryptionLimitationsSeen = new HashSet<string>(StringComparer.Ordinal);

            /// <summary>
            /// Structure-walk ceilings already reported through <see cref="ReportStructureWalkGap"/>,
            /// so one ceiling firing in two places does not produce two identical UA99 issues.
            /// </summary>
            internal readonly HashSet<string> StructureWalkGapsSeen = new HashSet<string>(StringComparer.Ordinal);

            /// <summary>
            /// The <c>/Annots</c> entries read so far, every page's together, against
            /// <see cref="PdfLimits.MaxAnnotationsPerDocument"/> — pages may share one array.
            /// </summary>
            internal long AnnotationEntriesRead;

            /// <summary>Whether that ceiling was reached and its UA99 notice written.</summary>
            internal bool AnnotationCeilingReached;

            internal IReadOnlyList<StructNode> Elements;
            internal Dictionary<StructNode, List<StructNode>> Children = new Dictionary<StructNode, List<StructNode>>();
            internal Dictionary<StructNode, string> Paths = new Dictionary<StructNode, string>();
            internal HashSet<int> StructElementObjectNumbers = new HashSet<int>();

            internal List<PageInfo> Pages = new List<PageInfo>();

            /// <summary>
            /// Tokenised Form XObjects, keyed by object number, for the lifetime of one audit;
            /// a null value records a form that could not be decoded or scanned safely, so a
            /// failure is not retried once per page either.
            ///
            /// <para><b>Why it is document-wide and not per page.</b> The visited-set that guards
            /// form recursion is per page, by necessity — the same form legitimately appears on
            /// every page — so without this cache a form shared by N pages was tokenised N times.
            /// <c>PdfStream.GetDecodedData</c> already caches the decoded bytes; the scan, which
            /// allocates a token list sized <c>length/12</c> and parses the whole stream, was the
            /// part being redone.</para>
            /// </summary>
            internal readonly Dictionary<int, ContentScanResult> FormScans =
                new Dictionary<int, ContentScanResult>();

            /// <summary>
            /// Per-page decoded-content ceiling in force for this audit; <c>0</c> or less disables
            /// it. See <see cref="DefaultMaxPageContentBytes"/>.
            /// </summary>
            internal int MaxPageContentBytes = DefaultMaxPageContentBytes;

            /// <summary>
            /// Remaining document-wide decoded-content budget, spent per page as pages are loaded.
            /// <see cref="long.MaxValue"/> when the caller disabled it.
            /// See <see cref="DefaultMaxDocumentContentBytes"/>.
            /// </summary>
            internal long ContentByteBudget = DefaultMaxDocumentContentBytes;

            /// <summary>
            /// The document-wide budget as configured, retained only so the report can name the
            /// limit rather than the (by then zero) remainder.
            /// </summary>
            internal long MaxDocumentContentBytes = DefaultMaxDocumentContentBytes;

            /// <summary>Remaining <see cref="MaxFormScanBytes"/>.</summary>
            internal long FormScanByteBudget = MaxFormScanBytes;

            /// <summary>Remaining <see cref="MaxFormTokenWalk"/>.</summary>
            internal long FormTokenWalkBudget = MaxFormTokenWalk;

            /// <summary>Set once when either form budget runs out, so it is diagnosed once.</summary>
            internal bool FormBudgetReported;

            // The /ParentTree is read once and shared: UA14 and UA22/UA23 both need it, and on a
            // large document it is the most expensive structure either of them touches.
            internal Dictionary<int, PdfObject> ParentTreeEntries;
            internal string ParentTreeError;
            internal bool ParentTreeRead;

            private Dictionary<int, StructNode> _objrOwners;

            /// <summary>
            /// Object numbers reachable through an /OBJR anywhere in the structure tree, each with the
            /// first element (in document order) whose /K holds that /OBJR.
            /// </summary>
            internal Dictionary<int, StructNode> ObjrOwners => _objrOwners ??= CollectObjrOwners(this);

            private Dictionary<string, string> _roleMap;

            /// <summary>
            /// The structure tree's <c>/RoleMap</c>, read once. Empty when there is none.
            /// Shared so per-element role resolution does not re-resolve the dictionary.
            /// </summary>
            internal Dictionary<string, string> RoleMap => _roleMap ??= ReadRoleMap(this);

            private Dictionary<string, PdfObject> _classMap;

            /// <summary>
            /// The structure tree's <c>/ClassMap</c>, read once. Empty when there is none. Each
            /// value is the attribute object the class name maps to, left exactly as stored — a
            /// single attribute dictionary or an array of them (ISO 32000-1 §14.7.5.2) — because
            /// <see cref="PdfStructureAttributes.Lookup"/> already handles both shapes for
            /// <c>/A</c>, which §14.7.5.2 gives exactly the same form.
            /// </summary>
            internal Dictionary<string, PdfObject> ClassMap => _classMap ??=
                PdfStructureAttributes.BuildClassMap(StructTree, o => ResolveObject(Doc, o));

            private Dictionary<StructNode, string> _resolvedRoles;

            /// <summary>
            /// Memoised <c>/S</c>-through-<c>/RoleMap</c> resolution, keyed by node identity. Only
            /// non-standard roles ever reach it — see <see cref="RoleOf"/>.
            /// </summary>
            internal Dictionary<StructNode, string> ResolvedRoles =>
                _resolvedRoles ??= new Dictionary<StructNode, string>();

            /// <summary>
            /// Pages whose content stream could not be read, with the reason and the checks that
            /// consequently did not run for them, in discovery order. Emitted by
            /// <see cref="ReportContentCoverage"/>; see <see cref="NoteContentCoverageGap"/> for
            /// why an empty list is the only acceptable alternative to saying so in the report.
            /// </summary>
            internal readonly List<ContentCoverageGap> ContentCoverageGaps = new List<ContentCoverageGap>();

            internal readonly Dictionary<int, ContentCoverageGap> ContentCoverageGapsByPage =
                new Dictionary<int, ContentCoverageGap>();

            /// <summary>
            /// Pages whose own <c>/Contents</c> read cleanly but whose scan is nevertheless known to
            /// be short — a Form XObject that would not resolve or decode, or a named marked-content
            /// property list that is not in <c>/Properties</c>. Emitted by
            /// <see cref="ReportPartialContentCoverage"/>; see <see cref="NotePartialContentGap"/>
            /// for why <see cref="ContentCoverageGaps"/> cannot cover them.
            /// </summary>
            internal readonly List<ContentCoverageGap> PartialContentGaps = new List<ContentCoverageGap>();

            internal readonly Dictionary<int, ContentCoverageGap> PartialContentGapsByPage =
                new Dictionary<int, ContentCoverageGap>();

            internal IReadOnlyList<StructNode> ChildrenOf(StructNode node)
                => node != null && Children.TryGetValue(node, out var kids)
                    ? (IReadOnlyList<StructNode>)kids
                    : Array.Empty<StructNode>();

            internal string PathOf(StructNode node)
                => node != null && Paths.TryGetValue(node, out string p) ? p : null;
        }

        private sealed class PageInfo
        {
            internal int PageNumber;              // 1-based, as the report model requires
            internal int PageIndex;               // 0-based
            internal PdfDictionary Dict;
            internal int ObjectNumber = -1;
            internal PdfDictionary Resources;
            internal PdfArray Annots;

            /// <summary>
            /// The annotations of <see cref="Annots"/> a reader is presented with, read once by
            /// <c>EnumerateAnnotations</c> for every check that asks; null until then.
            /// </summary>
            internal List<AnnotationInfo> PresentedAnnotations;

            /// <summary>
            /// The decoded page content, held only while it is being summarised and then released
            /// — see the comment at the end of CollectPages. Null afterwards; use
            /// <see cref="ContentLength"/> for the size.
            /// </summary>
            internal byte[] Content;

            /// <summary>Length of the decoded /Contents, retained after <see cref="Content"/> is released.</summary>
            internal int ContentLength;

            internal string ContentError;         // non-null when the content could not be decoded

            /// <summary>
            /// Scan facts for the page. Its <c>Tokens</c> list is released once the summary is
            /// built; every later check reads scalars only.
            /// </summary>
            internal ContentScanResult Scan;

            internal int TextShowingOperators;
            internal int ImageXObjectInvocations;
            internal bool HasInlineImage;

            /// <summary>
            /// Path-painting and shading operators — marks that are neither text nor an image.
            /// Counted so "blank" can mean <em>nothing was painted</em>; without it a page of vector
            /// artwork is indistinguishable from an empty one.
            /// </summary>
            internal int PaintingOperators;

            /// <summary>
            /// Text-showing operators that are inside no marked-content sequence at all — neither
            /// tagged nor marked as an artifact. This, not "the page has zero BDC", is what
            /// ISO 14289-1 §7.1 forbids.
            /// </summary>
            internal int UnmarkedTextShowingOperators;

            /// <summary>
            /// Marked-content sequences that are structural content <em>items</em>: not artifacts,
            /// not enclosed by an artifact, and naming an <c>/MCID</c>. Table 30 makes
            /// <c>/StructParents</c> required only when the page has some.
            ///
            /// <para>The <c>/MCID</c> condition is part of the definition, not an optimisation: a
            /// sequence with no identifier cannot be named from the structure tree, so it is not an
            /// item Table 30 speaks of. Counting every non-artifact BDC instead made UA18 report an
            /// <c>/OC</c> wrapper round a watermark as untracked structural content.</para>
            /// </summary>
            internal int StructuralMarkedSequences;

            /// <summary>
            /// The marked-content identifiers of every <em>non-artifact</em>
            /// <c>BDC</c> reached while summarising this page — its own content stream and,
            /// because a bare <c>/K</c> integer is not the only way an MCID is named, the Form
            /// XObjects it invokes.
            ///
            /// <para><b>What this is for.</b> ISO 32000-1 §14.7.4.4 defines the whole tagging
            /// relation in terms of these numbers: a structure element names MCID <i>i</i> on a
            /// page, and that page's <c>/ParentTree</c> array must name that element at index
            /// <i>i</i>. Without the set of MCIDs the page really contains, every pointer can be
            /// individually well-formed while the mapping is nonsense — an element pointing at
            /// MCID 42 on a page whose only sequence is MCID 0 would pass every other check in this
            /// file. Nothing else in the summary can substitute: a bare integer satisfies UA16 and
            /// UA30, the text <i>is</i> inside a BDC so UA17 is silent, and <c>/StructParents</c>
            /// is present so UA18 and UA14 are silent too.</para>
            ///
            /// <para>Artifact sequences are excluded on purpose. They are deliberately
            /// unreachable from the structure tree (§14.8.2), so an artifact MCID is neither
            /// claimable by an element nor an orphan when no element claims it.</para>
            /// </summary>
            internal readonly HashSet<int> MarkedContentIds = new HashSet<int>();

            /// <summary>
            /// True when at least one marked-content sequence's MCID could not be read — a
            /// <c>/Tag /PropertyName BDC</c> whose property list is not in the resource
            /// dictionary's <c>/Properties</c>. <see cref="MarkedContentIds"/> is then a lower
            /// bound, and must never be used to accuse the document of anything.
            /// </summary>
            internal bool MarkedContentIdsIncomplete;

            /// <summary>True when at least one <c>Do</c> resolved to a Form XObject.</summary>
            internal bool InvokesFormXObject;

            /// <summary>
            /// True when a Form XObject's content could not be included in these counts (it would
            /// not decode, or the nesting cap was hit), so the numbers are lower bounds.
            /// </summary>
            internal bool FormXObjectScanIncomplete;

            internal bool ContentUsable => ContentError == null && Scan != null && Scan.IsSafe;
            internal bool HasText => TextShowingOperators > 0;

            /// <summary>
            /// The page draws a raster, by either route. Beside <see cref="HasText"/> so that UA24
            /// and <c>ClassifyPageContent</c> ask one question rather than each spelling
            /// <c>ImageXObjectInvocations &gt; 0 || HasInlineImage</c> — the shape that drifts.
            /// </summary>
            internal bool HasImage => ImageXObjectInvocations > 0 || HasInlineImage;

            /// <summary>The page puts marks that are neither text nor a raster.</summary>
            internal bool HasVectorMarks => PaintingOperators > 0;
        }
    }
}
