// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Accessibility.Tagging
{
    /// <summary>
    /// Heuristic auto-tagger for untagged PDFs. Produces a <b>genuinely tagged</b> document:
    /// marked content in the page streams, structure elements that reference it by MCID, and a
    /// parent number tree that resolves each (page, MCID) pair back to its owning element.
    ///
    /// <para><b>What it emits, per document:</b></para>
    /// <list type="number">
    ///   <item><description><b>Per page</b> — the content stream is rewritten (via
    ///     <see cref="PageContentRewriter"/>) so each logical run is wrapped in
    ///     <c>/P &lt;&lt;/MCID n&gt;&gt; BDC … EMC</c> (or <c>/H1</c>…<c>/H6</c>), and running
    ///     headers/footers in <c>/Artifact &lt;&lt;/Type /Pagination /Subtype /Header&gt;&gt; BDC …
    ///     EMC</c>. The page dictionary gains <c>/StructParents k</c>.</description></item>
    ///   <item><description><b>One StructElem per logical block</b> with <c>/S</c>, <c>/P</c>,
    ///     <c>/Pg</c> and — the part that makes the tree real — <c>/K</c> listing that block's
    ///     MCIDs (a bare integer when the MCID is on the element's own <c>/Pg</c>, an
    ///     <c>/MCR</c> dictionary otherwise).</description></item>
    ///   <item><description><b>One StructElem per annotation</b> that belongs in the structure
    ///     tree — <c>/Link</c> for a link, <c>/Form</c> for a widget, <c>/Annot</c> otherwise —
    ///     whose <c>/K</c> is an <c>/OBJR</c> naming the annotation, with a matching
    ///     <c>/StructParent</c> on the annotation itself (ISO 32000-1 §14.7.4.3 / §14.8.4.4).
    ///     </description></item>
    ///   <item><description><b>A <c>/ParentTree</c></b> number tree whose <c>/Nums</c> maps each
    ///     page's <c>/StructParents</c> key to an array indexed by MCID of the owning element
    ///     references, <em>and</em> each annotation's <c>/StructParent</c> key to a single
    ///     reference to its owning element, plus <c>/ParentTreeNextKey</c>.</description></item>
    ///   <item><description><b>A <c>/StructTreeRoot</c></b> holding a single <c>/Document</c>
    ///     element, and catalog wiring: <c>/StructTreeRoot</c>, <c>/MarkInfo &lt;&lt;/Marked
    ///     true&gt;&gt;</c>, <c>/Lang</c> (when absent) and <c>/ViewerPreferences
    ///     &lt;&lt;/DisplayDocTitle true&gt;&gt;</c>.</description></item>
    ///   <item><description>All of it in <b>one</b> incremental update
    ///     (<see cref="DocumentRevisionWriter"/>).</description></item>
    /// </list>
    ///
    /// <para><b>Why the layout comes from the content stream, not from extracted text.</b>
    /// Extracted text gives lines but no byte offsets, so a splice derived from it can only ever
    /// approximate where a run begins and ends. <see cref="ContentStreamTagger"/> derives baseline
    /// Y and effective font size directly from the stream (<c>Tm</c>/<c>Td</c>/<c>TD</c>/<c>T*</c>/
    /// <c>Tf</c>/<c>cm</c>) and returns the exact byte range plus the MCID it assigned.
    /// The document-wide median body font size is measured from the same source, in linear time
    /// and on the same basis as the values it is compared against (see
    /// <see cref="ComputeMedianFontSize"/>).</para>
    ///
    /// <para><b>Failure policy.</b> A page the tagger refuses — already-marked content, an
    /// undecodable stream, an unsafe scan, a q/Q imbalance — is skipped, named in
    /// <see cref="AutoTagResult.SkippedPageReasons"/>, and does not abort the document. If
    /// <em>no</em> page could be marked, no structure tree is written at all (a tree with no MCIDs
    /// is exactly the defect this type exists to fix); the catalog metadata fixes still apply and
    /// <see cref="AutoTagResult.ContentMarked"/> reports false.</para>
    /// </summary>
    public static class PdfAutoTagger
    {
        private const string Operation = "PdfAutoTagger.AutoTag";

        /// <summary>Fallback body font size when neither measurement source produces one.</summary>
        private const float DefaultMedianFontSize = PageTagOptions.FallbackMedianFontSize;

        /// <summary>
        /// Font-size samples the median pass will retain across the whole document.
        /// </summary>
        /// <remarks>
        /// The sample exists only to produce one median, and a quarter of a million lines is already
        /// two orders of magnitude more than any real document's body text — so the cap costs
        /// nothing on a genuine file while turning the one unbounded, document-lifetime allocation in
        /// this pass into a fixed one.
        /// </remarks>
        private const int MaxMedianFontSizeSamples = 250_000;

        /// <summary>
        /// The greatest parent-tree key this pass counts up from: half the range an <c>int</c> holds, far
        /// past any real document and leaving more keys than a file can ask for.
        /// </summary>
        private const long MaxParentTreeKeyBase = int.MaxValue / 2;

        private static readonly PdfName McrType = PdfName.MCR;

        /// <summary>
        /// <c>/Stm</c> — the content stream a marked-content reference points into, when it is not
        /// the page's own (ISO 32000-1 Table 324).
        /// </summary>
        private static readonly PdfName Stm = PdfName.Stm;

        /// <summary>Object-reference kid type (ISO 32000-1 §14.7.4.3, Table 325).</summary>
        private static readonly PdfName ObjrType = new PdfName("OBJR");

        /// <summary>
        /// The attribute owner Table 384 gives the table attributes. Not a cached
        /// <c>PdfName</c> field: <c>/Table</c> is a structure <em>role</em> everywhere else in this
        /// tree, and one shared constant would read as if the two were the same thing.
        /// </summary>
        private static readonly PdfName OwnerTable = new PdfName("Table");

        // ================================================================
        //  Public API
        // ================================================================

        /// <summary>
        /// Auto-tags a PDF file and writes the result to <paramref name="outputPath"/>.
        /// Streams input via <see cref="File.OpenRead"/> rather than buffering the whole file
        /// through <see cref="File.ReadAllBytes"/>.
        /// </summary>
        public static AutoTagResult AutoTag(string inputPath, string outputPath, AutoTagOptions options = null)
        {
            if (inputPath  == null) throw new ArgumentNullException(nameof(inputPath));
            if (outputPath == null) throw new ArgumentNullException(nameof(outputPath));

            using var inFs  = File.OpenRead(inputPath);
            using var outFs = File.Create(outputPath);
            return AutoTag(inFs, outFs, options);
        }

        /// <summary>Auto-tags the PDF in <paramref name="input"/> and writes to <paramref name="output"/>.</summary>
        public static AutoTagResult AutoTag(Stream input, Stream output, AutoTagOptions options = null)
        {
            if (input  == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            options = options ?? new AutoTagOptions();

            AccessibilityDiagnostics.Clear();

            byte[] bytes    = ReadAll(input);
            var warnings    = new List<string>();
            var skipReasons = new List<string>();

            if (bytes.Length == 0)
            {
                AccessibilityDiagnostics.Report(Operation, "the input stream contained no bytes; nothing was tagged");
                warnings.Add("The input stream contained no bytes; nothing was tagged.");
                return BuildFailureResult(0, warnings, skipReasons);
            }

            TagPlan plan  = null;
            bool outputOwned = false;   // true once the incremental writer owns `output`
            int pagesSeen = 0;          // page count to report when the plan is discarded

            try
            {
                using (var source = new MemoryStream(bytes))
                using (var doc = PdfDocument.Open(source))
                {
                    PdfObjectIndex index = PdfObjectIndex.Build(doc);
                    plan = BuildPlan(doc, index, options, warnings, skipReasons);

                    if (plan != null)
                    {
                        pagesSeen = plan.PagesProcessed;

                        // From here on the writer is responsible for every byte of `output`
                        // (it writes the original document first, then the appended revision).
                        outputOwned = true;
                        if (!WriteRevision(output, bytes, doc, index, plan, warnings))
                            plan = null;
                    }
                }
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report(Operation,
                    "auto-tagging failed; the document was copied through unchanged", ex);
                warnings.Add("Auto-tagging failed (" + ex.Message + "); the document was "
                             + (outputOwned ? "written in a possibly incomplete state." : "copied through unchanged."));
                if (outputOwned) plan = null;
            }

            if (!outputOwned)
                output.Write(bytes, 0, bytes.Length);

            // `pagesSeen` is 0 unless a plan was actually built, so a document that never got that
            // far still reports 0/0 exactly as before; a plan that was discarded because its
            // revision was not written reports the pages it had examined, all of them skipped.
            return plan != null
                ? plan.ToResult(warnings, skipReasons)
                : BuildFailureResult(pagesSeen, warnings, skipReasons);
        }

        /// <summary>
        /// Computes the tagging revision <b>without writing it</b>, so a caller that owns the
        /// write can append it to bytes this method never sees.
        ///
        /// <para>
        /// <b>What this is for.</b> <see cref="AutoTag(Stream,Stream,AutoTagOptions)"/> appends a
        /// clear-text revision and therefore refuses an encrypted document — a clear-text
        /// revision does not match the encryption dictionary and is unreadable. This assembly
        /// cannot decrypt (it does not reference <c>Tetitulz.Pdf.Encryption</c>, and must not),
        /// so the operation travels to the decryptor instead: hand the result to
        /// <c>Encryption.PdfEncryptedIncrementalUpdate.Run</c>, which decrypts a copy to plan
        /// against, encrypts these objects with the document's own file key, and appends them to
        /// the original bytes.
        /// </para>
        ///
        /// <code>
        /// PdfEncryptedIncrementalUpdate.Run(input, output, password,
        ///     (decrypted, firstFree) => PdfAutoTagger.Plan(decrypted, firstFree, options).Objects);
        /// </code>
        ///
        /// <para>
        /// <b><paramref name="firstFreeObjectNumber"/> is not a convenience.</b> The document
        /// handed in is a <i>decrypted copy</i> of the file the revision is appended to, and
        /// decryption unpacks object streams and drops the cross-reference stream — so the copy's
        /// highest object number can be <i>lower</i> than the original's. Allocating from the
        /// copy's own ceiling would mint a number that is live in the file being written, and the
        /// appended object would silently supersede it. Every number this plan allocates is
        /// therefore at least <paramref name="firstFreeObjectNumber"/>.
        /// </para>
        ///
        /// <para>
        /// The reverse case — an <i>unencrypted</i> document — needs none of this and should keep
        /// using <see cref="AutoTag(Stream,Stream,AutoTagOptions)"/>, which owns the write and so
        /// can verify it landed.
        /// </para>
        /// </summary>
        /// <param name="doc">The document to plan against. Never modified.</param>
        /// <param name="firstFreeObjectNumber">
        /// The lowest object number the plan may allocate — the object-number ceiling of the file
        /// the revision will be appended to, plus one.
        /// </param>
        /// <param name="options">Tagging options; defaults apply when null.</param>
        /// <exception cref="ArgumentNullException"><paramref name="doc"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="firstFreeObjectNumber"/> is below 1 — object number 0 is the free-list
        /// head and can never be written.
        /// </exception>
        public static AutoTagPlan Plan(PdfDocument doc, int firstFreeObjectNumber, AutoTagOptions options = null)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (firstFreeObjectNumber < 1)
                throw new ArgumentOutOfRangeException(nameof(firstFreeObjectNumber),
                    "Object numbering starts at 1; 0 is the free-list head and can never be written.");

            options = options ?? new AutoTagOptions();

            AccessibilityDiagnostics.Clear();

            var warnings    = new List<string>();
            var skipReasons = new List<string>();

            PdfObjectIndex index = PdfObjectIndex.Build(doc);

            // Lift the allocator to the *target* file's ceiling before anything is minted. See the
            // remarks: the index was built from a decrypted copy whose ceiling can be lower.
            index.ReserveObjectNumber(firstFreeObjectNumber - 1);

            TagPlan plan = BuildPlan(doc, index, options, warnings, skipReasons);

            if (plan == null)
            {
                AutoTagResult nothing = BuildFailureResult(0, warnings, skipReasons);
                return new AutoTagPlan(Array.Empty<IndirectObject>(), -1, nothing, nothing);
            }

            if (plan.NewInfoObjectNumber > 0)
            {
                warnings.Add("The document has no /Info dictionary, so the title was written into a "
                    + "new one (object " + plan.NewInfoObjectNumber.ToString(CultureInfo.InvariantCulture)
                    + "). The appended trailer must be pointed at it — see "
                    + "AutoTagPlan.NewInfoObjectNumber — or the object is unreferenced and the "
                    + "title is not set.");
            }

            IReadOnlyList<IndirectObject> objects = ToIndirectObjects(plan, index);

            // Built before the discarded copy so the applied result can never observe the extra
            // warning: AutoTagResult keeps the list it is handed.
            AutoTagResult applied = plan.ToResult(warnings, skipReasons);

            var discardedWarnings = new List<string>(warnings)
            {
                "The revision carrying the structure tree was not written, so the structure "
                + "elements, MCIDs and marked content this plan describes are NOT present in the "
                + "output. It is reported as a failure rather than as a tagged document."
            };

            return new AutoTagPlan(
                objects,
                plan.NewInfoObjectNumber,
                applied,
                BuildFailureResult(plan.PagesProcessed, discardedWarnings, skipReasons));
        }

        /// <summary>
        /// Flattens the plan's two object maps into the flat, generation-carrying list Core's
        /// incremental writer takes.
        ///
        /// <para>
        /// This is <see cref="DocumentRevisionWriter"/>'s merge rule, applied here because that
        /// type owns the *write* and this path does not perform one: a revised object wins a
        /// collision with a freshly allocated one, and the generation must be the one the
        /// document already records or a strict reader drops the whole appended section as stale.
        /// </para>
        /// </summary>
        private static IReadOnlyList<IndirectObject> ToIndirectObjects(TagPlan plan, PdfObjectIndex index)
        {
            var list = RevisionObjects.From(
                plan.New, plan.Modified, index, "PdfAutoTagger.ToIndirectObjects");

            return list;
        }

        /// <summary>
        /// Hands the plan to <see cref="DocumentRevisionWriter"/> and reports whether the
        /// appended revision <em>actually reached</em> <paramref name="output"/>.
        ///
        /// <para>
        /// <b>Why this check exists.</b> The writer is documented never to throw: it catches every
        /// failure — an <see cref="IOException"/> from the caller's stream, an object it had to
        /// skip, a trailer it could not rebuild — records it on
        /// <see cref="AccessibilityDiagnostics"/> and leaves the output holding just the original
        /// bytes. Returning <c>plan.ToResult(...)</c> unconditionally therefore reported
        /// <see cref="AutoTagResult.ElementsAdded"/>, <see cref="AutoTagResult.McidCount"/> and —
        /// worst of all — <see cref="AutoTagResult.ContentMarked"/> <c>true</c> about a file that
        /// is byte-for-byte the untagged input. Downstream that is not a cosmetic lie:
        /// <c>PdfAccessibility</c>'s pipeline reads <c>ContentMarked</c> to decide that AutoTag ran,
        /// skips the artifact and reading-order steps as superseded, and stamps
        /// <c>pdfuaid:part=1</c> onto a document that was never tagged.
        /// </para>
        ///
        /// <para>
        /// <b>The mechanism itself is <see cref="VerifiedWrite"/>.</b> Counting the bytes
        /// <paramref name="output"/> actually accepted is exact where reading
        /// <see cref="AccessibilityDiagnostics.LastError"/> across the call is a lossy proxy; that
        /// reasoning, and the counting stream, live in one place for all four writers in this
        /// assembly. What stays here is the <em>message</em>, which has to name what a caller of
        /// <b>this</b> operation is not getting.
        /// </para>
        /// </summary>
        /// <returns>
        /// <c>true</c> when the revision was appended and the plan may be reported as applied;
        /// <c>false</c> when the output holds only (or less than) the original document.
        /// </returns>
        private static bool WriteRevision(
            Stream output, byte[] bytes, PdfDocument doc, PdfObjectIndex index,
            TagPlan plan, List<string> warnings)
        {
            // BuildPlan always revises at least the catalog, so a revision is always expected here;
            // the empty-plan passthrough case is handled by the shared helper regardless.
            if (VerifiedWrite.Write(
                    output, bytes, doc, index, plan.Modified, plan.New, plan.NewInfoObjectNumber,
                    out bool truncated, out string cause))
            {
                return true;
            }

            string detail = "The incremental revision carrying the structure tree never reached the "
                + "output stream (" + cause + "), so the output holds "
                + (truncated ? "only part of the original document" : "the original, untagged document")
                + " and nothing was tagged. The structure elements, MCIDs and marked content this "
                + "call planned are NOT present in the result, so it is reported as a failure rather "
                + "than as a tagged document.";

            AccessibilityDiagnostics.Report(Operation, detail);
            warnings.Add(detail);
            return false;
        }

        // ================================================================
        //  Planning
        // ================================================================

        /// <summary>
        /// Everything the incremental writer needs, computed without touching
        /// <c>output</c>. Building the plan first is what lets a failure part-way leave the
        /// caller's stream holding exactly the original document.
        /// </summary>
        private sealed class TagPlan
        {
            internal readonly Dictionary<int, PdfObject> Modified = new Dictionary<int, PdfObject>();
            internal readonly Dictionary<int, PdfObject> New      = new Dictionary<int, PdfObject>();
            internal readonly Dictionary<int, int> HeadingsByLevel = new Dictionary<int, int>();

            internal int NewInfoObjectNumber = -1;
            internal int ElementsAdded;
            internal int PagesProcessed;
            internal int PagesSkipped;

            /// <summary>Pages left to an existing tree that already reaches them.</summary>
            internal int PagesAlreadyTagged;

            /// <summary>Whether an existing tree was extended rather than superseded.</summary>
            internal bool MergedIntoExistingTree;
            internal int PagesPartiallyTagged;

            /// <summary>Form XObjects whose text this pass marked, each with its own MCID space.</summary>
            internal int FormsTagged;

            /// <summary>Lists this pass inferred and wrapped; 0 unless the caller opted in.</summary>
            internal int ListCount;

            /// <summary>Tables this pass inferred and wrapped; 0 unless the caller opted in.</summary>
            internal int TableCount;

            /// <summary>Images wrapped in a /Figure; 0 unless the caller supplied alternate text.</summary>
            internal int FigureCount;

            /// <summary>Images offered to the caller that it returned no alternate text for.</summary>
            internal int FiguresDeclined;

            /// <summary>Each <c>/XObject</c> dictionary's images, read once a run however many pages and forms share it.</summary>
            internal readonly ImageCatalogs Images = new ImageCatalogs();

            /// <summary>Shared forms given a separate copy per invoking page.</summary>
            internal int SharedFormsDuplicated;
            internal int ParagraphCount;
            internal int ArtifactCount;
            internal int McidCount;
            internal int AnnotationsTagged;
            internal bool ContentMarked;

            internal AutoTagResult ToResult(IReadOnlyList<string> warnings, IReadOnlyList<string> skipReasons)
            {
                // The constructor keeps its existing arity and parameter order — widening it (even
                // with an optional parameter) would be a binary break for an assembly already
                // compiled against it. AnnotationsTagged is carried by an added property instead.
                var result = new AutoTagResult(
                    ElementsAdded,
                    PagesProcessed,
                    HeadingsByLevel,
                    ParagraphCount,
                    ArtifactCount,
                    warnings,
                    McidCount,
                    PagesSkipped,
                    skipReasons,
                    ContentMarked,
                    PagesPartiallyTagged);

                result.AnnotationsTagged      = AnnotationsTagged;
                result.ListsInferred          = ListCount;
                result.TablesInferred         = TableCount;
                result.FiguresTagged          = FigureCount;
                result.FiguresDeclined        = FiguresDeclined;
                result.ImageCatalogsRead      = Images.Read;
                result.FormsTagged            = FormsTagged;
                result.SharedFormsDuplicated  = SharedFormsDuplicated;
                result.MergedIntoExistingTree = MergedIntoExistingTree;
                result.PagesAlreadyTagged     = PagesAlreadyTagged;
                return result;
            }
        }

        /// <summary>
        /// One content stream that was successfully re-marked — a page's own <c>/Contents</c>, or a
        /// Form XObject it invokes.
        /// </summary>
        /// <remarks>
        /// MCIDs are numbered <b>per content stream</b> (ISO 32000-1 §14.7.4.4), so each of these
        /// is its own MCID space and its own <c>/StructParents</c> key. <see cref="ScopeId"/> is
        /// what keeps two streams' MCID 0 apart; it is not a page index, because a form has one.
        /// </remarks>
        private sealed class TaggedContentStream
        {
            /// <summary>Unique per tagged stream. Pages take their page index; forms continue after.</summary>
            internal int ScopeId;

            /// <summary>Index of the page this content is drawn on — the <c>/Pg</c> of its elements.</summary>
            internal int PageIndex;

            internal int PageObjectNumber;

            /// <summary>
            /// The Form XObject's object number, or <c>0</c> when this is the page's own content
            /// stream. Non-zero is what makes an element's <c>/K</c> need an <c>/MCR</c> with
            /// <c>/Stm</c>.
            /// </summary>
            internal int StreamObjectNumber;

            internal int ParentTreeKey;
            internal int McidCount;
            internal IReadOnlyList<TaggedRun> Runs;
        }

        /// <summary>One structure element: a paragraph or a heading, and the MCIDs it owns.</summary>
        private sealed class LogicalElement
        {
            internal string Role;
            internal int ObjectNumber;
            internal readonly List<McidRef> Mcids = new List<McidRef>();
            internal StringBuilder Text;

            /// <summary>
            /// Structure elements listed in this block's <c>/K</c> after its own marked content —
            /// currently only the annotation wrappers this pass adds.
            /// </summary>
            internal readonly List<int> ChildElementObjectNumbers = new List<int>();

            /// <summary>
            /// The element this one is a child of, or null when it hangs off <c>/Document</c>.
            /// </summary>
            /// <remarks>
            /// <b>Not every element is a direct child of <c>/Document</c></b>, or no role needing a
            /// container — <c>L</c> → <c>LI</c> → <c>LBody</c>, <c>Table</c> → <c>TR</c> →
            /// <c>TD</c> — could be emitted however well it was detected. A parent here plus
            /// <see cref="ChildElementObjectNumbers"/> is the whole mechanism: the writer appends
            /// those to <c>/K</c>, and the document's kid list takes only the roots.
            /// </remarks>
            internal LogicalElement Parent;

            /// <summary>
            /// Structure elements nested inside this one, in reading order.
            /// </summary>
            /// <remarks>
            /// Held as elements rather than as object numbers because the wrapping pass runs
            /// <b>before</b> numbers are allocated — a list's items exist as soon as they are
            /// grouped, and their numbers do not. <c>BuildStructElement</c> resolves them at write
            /// time, by which point every element has one.
            /// </remarks>
            internal readonly List<LogicalElement> ChildElements = new List<LogicalElement>();

            /// <summary>Zero-based index of the page this block was marked on; -1 until a run is added.</summary>
            internal int PageIndex = -1;

            /// <summary>
            /// The inferred table this element is a cell of, unique within its page, or -1.
            /// </summary>
            internal int TableId = -1;

            /// <summary>Zero-based row of this cell within its table, or -1.</summary>
            internal int TableRowIndex = -1;

            /// <summary>
            /// The <c>/Alt</c> this element must carry, or null.
            /// </summary>
            /// <remarks>
            /// Distinct from <see cref="Text"/>, which becomes <c>/ActualText</c>. §14.9.3 makes
            /// them different claims: <c>/ActualText</c> says <em>this content IS that text</em>
            /// (a ligature, a stylised word), while <c>/Alt</c> says <em>this content is not text
            /// and here is a description of it</em>. Writing a figure's description into
            /// <c>/ActualText</c> would tell a reader the image is those words, and a copy would
            /// silently paste them.
            /// </remarks>
            internal string AlternateText;

            /// <summary>
            /// Value of this header cell's <c>/Scope</c> table attribute, or null.
            /// </summary>
            /// <remarks>
            /// <b>Set by the pass that knows what the cell heads, not by the classifier.</b>
            /// <c>WrapTableCells</c> writes <c>Column</c> on a first-row header because that is
            /// what a first row heads; a <c>/TH</c> a caller's own classifier produced somewhere
            /// else carries none, because nothing here knows whether it heads a column, a row or
            /// both — and a wrong <c>/Scope</c> associates every data cell with the wrong header,
            /// which is worse than the UA12 warning that an unscoped one earns.
            /// </remarks>
            internal string HeaderScope;

            /// <summary>
            /// Vertical band the block occupies in default user space, used only to decide which
            /// block an annotation's <c>/Rect</c> falls inside. Left inverted
            /// (<c>MinY &gt; MaxY</c>) while no run has been measured.
            /// </summary>
            internal float MinY = float.MaxValue;

            /// <summary>Top of the band; see <see cref="MinY"/>.</summary>
            internal float MaxY = float.MinValue;
        }

        /// <summary>
        /// One annotation that this pass makes reachable from the structure tree: a wrapper
        /// element (<c>/Link</c>, <c>/Form</c> or <c>/Annot</c>) whose <c>/K</c> is an
        /// <c>/OBJR</c> naming the annotation, plus the <c>/StructParent</c> key stamped onto the
        /// annotation itself.
        /// </summary>
        private sealed class AnnotationElement
        {
            /// <summary>The annotation dictionary as parsed. Never modified; cloned before editing.</summary>
            internal PdfDictionary Dict;

            /// <summary>Structure type of the wrapper: <c>Link</c>, <c>Form</c> or <c>Annot</c>.</summary>
            internal string Role;

            /// <summary>Object number of the wrapper element (allocated by this pass).</summary>
            internal int ObjectNumber;

            internal int AnnotObjectNumber;
            internal int AnnotGeneration;
            internal int PageObjectNumber;
            internal int PageGeneration;
            internal int PageIndex;

            /// <summary>
            /// The annotation's <c>/StructParent</c> value — a key of the parent number tree in
            /// its own right, <em>not</em> an index into a page's <c>/StructParents</c> array.
            /// </summary>
            internal int ParentTreeKey = -1;

            /// <summary>Enclosing block, or null when the wrapper hangs off the <c>/Document</c> element.</summary>
            internal LogicalElement Parent;
        }

        private readonly struct McidRef
        {
            internal McidRef(int scopeId, int pageIndex, int pageObjectNumber, int streamObjectNumber, int mcid)
            {
                ScopeId            = scopeId;
                PageIndex          = pageIndex;
                PageObjectNumber   = pageObjectNumber;
                StreamObjectNumber = streamObjectNumber;
                Mcid               = mcid;
            }

            /// <summary>Which content stream's MCID space this number belongs to.</summary>
            internal int ScopeId { get; }

            internal int PageIndex { get; }
            internal int PageObjectNumber { get; }

            /// <summary>Form XObject object number, or 0 for the page's own content stream.</summary>
            internal int StreamObjectNumber { get; }

            internal int Mcid { get; }
        }

        /// <summary>
        /// Marks the text inside Form XObjects, giving each tagged form its own
        /// <c>/StructParents</c> and MCID space, and reports the forms it could not tag.
        /// </summary>
        /// <remarks>
        /// <para>Runs after the page loop because the governing question — how many pages invoke a
        /// given form — is document-wide. A Form XObject has one stream dictionary and therefore one
        /// <c>/StructParents</c>, so a form reached from two pages cannot carry structure for both
        /// and is reported rather than tagged. See GAPS.md §5c.</para>
        /// <para><b>Header/footer artifact detection is switched off for forms.</b> Those bands are
        /// measured in <i>page</i> space, and a form's text is in form space, transformed by its
        /// <c>/Matrix</c> and by whatever CTM was in effect at the <c>Do</c>. Applying page bands
        /// here would mark body text as a running header on the strength of a coordinate that means
        /// something else.</para>
        /// </remarks>
        private static void TagForms(
            PdfDocument doc, PdfObjectIndex index, IReadOnlyList<PdfPage> pages, int pageCount,
            List<PageInvocations> pageInvocations,
            AutoTagOptions options, BodyText median, TagPlan plan, List<string> warnings,
            List<TaggedContentStream> taggedStreams, ref int nextParentTreeKey,
            out IReadOnlyList<FormUsage> inventoriedForms)
        {
            inventoriedForms = Array.Empty<FormUsage>();
            if (pageInvocations.Count == 0) return;

            FormInventory inventory;
            try
            {
                inventory = FormXObjectInventory.Collect(
                    doc, index, pageInvocations, options.MaxPageContentBytes);
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report(Operation, "the Form XObject inventory failed", ex);
                return;
            }

            // Handed to PurgeStaleStructureLinks, which has to know about every form this pass
            // *saw* — including the ones it declined, since those are exactly the forms that can
            // keep a /StructParents key the new parent tree does not define.
            inventoriedForms = inventory.Forms;

            // Scope ids continue past the pages so a form's MCID 0 cannot collide with a page's.
            int nextScopeId = pageCount;
            var partiallyTagged = new HashSet<int>();

            // An invocation that could not be followed is possible untagged text, not proof of none.
            for (int u = 0; u < inventory.Unreadable.Count; u++)
            {
                KeyValuePair<int, string> entry = inventory.Unreadable[u];
                string detail = "Page " + (entry.Key + 1).ToString(CultureInfo.InvariantCulture) +
                    " is only partially tagged: " + entry.Value + ".";
                warnings.Add(detail);
                AccessibilityDiagnostics.Report(Operation, detail);
                partiallyTagged.Add(entry.Key);
            }

            long duplicationBudget = SharedFormDuplicator.MaxDuplicatedFormBytes;

            List<FormUsage> forms = inventory.Forms;
            for (int i = 0; i < forms.Count; i++)
            {
                FormUsage form = forms[i];

                // Drawn only inside /Artifact sequences — a watermark, a running head a producer
                // marked: what it draws is artifact content (§14.8.2.2), not text left untagged.
                if (form.ArtifactOnly) continue;

                // No text and the scan got far enough to know it: nothing to tag and nothing
                // missing. Not a gap, so not reported.
                //
                // The order of these two tests matters. FormXObjectInventory.Measure sets HasText on
                // its LAST line, so every refusal path leaves it false: tested first, `!form.HasText`
                // would make the SkipReason branch below unreachable, and a Form XObject full of
                // untagged text would produce no warning, no SkippedPageReasons entry and no
                // PagesPartiallyTagged increment, while the catalog would still assert /MarkInfo
                // /Marked true. TextUndetermined keeps the fast path
                // honest for the one refusal that really does prove there is no text ("it has no
                // content"), so this does not become a channel that fires on conformant input.
                if (form.SkipReason == null && !form.HasText) continue;
                if (form.SkipReason != null && !form.HasText && !form.TextUndetermined) continue;

                if (form.SkipReason != null)
                {
                    ReportUntaggedForm(form, plan, warnings, partiallyTagged);
                    continue;
                }

                bool shared = form.Invocations.Count > 1;
                if (shared && !options.DuplicateSharedFormXObjects)
                {
                    form.SkipReason = FormXObjectInventory.SharedFormSkipReason(form);
                    ReportUntaggedForm(form, plan, warnings, partiallyTagged);
                    continue;
                }

                // One page's options serve every copy: BuildFormTagOptions switches off the only
                // page-space measurement, so what remains is document-level.
                PageTagOptions formOptions = BuildFormTagOptions(
                    doc, pages, form.Invocations[0].PageIndex, options, median, warnings, form, plan);

                FormDuplicationPlan duplication;
                try
                {
                    duplication = SharedFormDuplicator.Plan(
                        doc, index, form, formOptions, options.CompressRewrittenStreams, duplicationBudget);
                }
                catch (Exception ex)
                {
                    AccessibilityDiagnostics.Report(Operation,
                        "Form XObject " + form.ObjectNumber.ToString(CultureInfo.InvariantCulture) +
                        " could not be rewritten", ex);
                    form.SkipReason = "it could not be rewritten";
                    ReportUntaggedForm(form, plan, warnings, partiallyTagged);
                    continue;
                }

                if (!duplication.Succeeded)
                {
                    // A shared form that cannot be duplicated is reported with the constraint that
                    // made duplication necessary, not merely with the mechanical obstacle: the
                    // caller's question is why the text is untagged.
                    form.SkipReason = shared
                        ? FormXObjectInventory.SharedFormSkipReason(form) + ", and " + duplication.SkipReason
                        : duplication.SkipReason ?? "the tagger declined to modify it";
                    ReportUntaggedForm(form, plan, warnings, partiallyTagged);
                    continue;
                }

                // Keys are consumed only once the whole form is known to be committable, so every
                // key the ParentTree declares has a stream pointing at it and vice versa.
                var parentTreeKeys = new List<int>(duplication.Copies.Count);
                for (int c = 0; c < duplication.Copies.Count; c++)
                    parentTreeKeys.Add(nextParentTreeKey + c);

                if (!SharedFormDuplicator.Commit(
                        doc, index, form, duplication, parentTreeKeys, plan.Modified, plan.New))
                {
                    form.SkipReason = "its copies could not be attached to the pages that draw it";
                    ReportUntaggedForm(form, plan, warnings, partiallyTagged);
                    continue;
                }

                for (int c = 0; c < duplication.Copies.Count; c++)
                {
                    FormCopy copy = duplication.Copies[c];

                    taggedStreams.Add(new TaggedContentStream
                    {
                        ScopeId            = nextScopeId++,
                        StreamObjectNumber = copy.ObjectNumber,
                        PageIndex          = copy.Invocation.PageIndex,
                        PageObjectNumber   = copy.Invocation.PageObjectNumber,
                        ParentTreeKey      = parentTreeKeys[c],
                        McidCount          = Math.Max(0, duplication.Tagged.NextMcid),
                        Runs               = duplication.Tagged.Runs,
                    });
                }

                nextParentTreeKey += duplication.Copies.Count;
                // Clamped at zero as well as computed in long: a budget that can go negative is a
                // budget the next form is measured against wrongly, and the whole point of this
                // ceiling is that it cannot be talked out of firing.
                duplicationBudget = Math.Max(0L, duplicationBudget - duplication.BudgetCost);
                plan.FormsTagged++;
                if (shared) plan.SharedFormsDuplicated++;
            }

            plan.PagesPartiallyTagged += partiallyTagged.Count;
        }

        /// <summary>Records one form that holds text this pass could not mark.</summary>
        private static void ReportUntaggedForm(
            FormUsage form, TagPlan plan, List<string> warnings, HashSet<int> partiallyTagged)
        {
            string where = form.InvokingPageIndexes.Count == 1
                ? "Page " + (form.InvokingPageIndexes[0] + 1).ToString(CultureInfo.InvariantCulture)
                : "Pages " + string.Join(", ", form.InvokingPageIndexes.ConvertAll(p =>
                      (p + 1).ToString(CultureInfo.InvariantCulture)));

            // "Draws text" when the scan counted some, "may draw text" when it never got far enough
            // to count: the second is a coverage statement, and overstating it would be the same
            // dishonesty in the other direction.
            string what = form.HasText ? " draws text inside Form XObject " : " may draw text inside Form XObject ";

            string detail = where + what +
                form.ObjectNumber.ToString(CultureInfo.InvariantCulture) +
                " that is still untagged: " + (form.SkipReason ?? "it could not be tagged") +
                ". The page is only partially tagged.";

            warnings.Add(detail);
            AccessibilityDiagnostics.Report(Operation, detail);

            for (int i = 0; i < form.InvokingPageIndexes.Count; i++)
                partiallyTagged.Add(form.InvokingPageIndexes[i]);
        }

        /// <summary>
        /// Tagging options for a form: the invoking page's thresholds, with header/footer artifact
        /// detection disabled because those bands are page-space measurements.
        /// </summary>
        private static PageTagOptions BuildFormTagOptions(
            PdfDocument doc, IReadOnlyList<PdfPage> pages, int pageIndex,
            AutoTagOptions options, BodyText median, List<string> warnings,
            FormUsage form, TagPlan plan)
        {
            PdfPage page = pageIndex >= 0 && pageIndex < pages.Count ? pages[pageIndex] : null;

            // plan: null — the page's own images must not be offered here. The bytes being tagged
            // are the FORM's content stream, so a `/Im0 Do` inside it resolves through the form's
            // resources; handing it the invoking page's names would ask about a different
            // dictionary and could wrap the form's text in a /Figure named after a picture beside
            // it. The form's own seam is attached below instead.
            PageTagOptions formOptions = BuildPageTagOptions(
                doc, page, options, median, pageIndex, warnings, plan: null);
            formOptions.MarkHeadersFootersAsArtifacts = false;

            AttachFormFigureSeam(doc, pages, page, options, pageIndex, formOptions, form, plan);
            return formOptions;
        }

        /// <summary>
        /// Gives a Form XObject's tagging options the figure seam, resolved against the form's own
        /// resources.
        /// </summary>
        /// <remarks>
        /// <para><b>§8.10.1: a form without <c>/Resources</c> resolves names against the page's.</b>
        /// So the fallback is not a guess — it is what a conformant consumer does, and a form that
        /// omits the key is common in output from producers that emit one resource dictionary per
        /// page.</para>
        ///
        /// <para><b>A form drawn on several pages reports no page rather than the wrong one.</b>
        /// One <c>PageTagOptions</c> serves every duplicated copy — sound for the rest of it,
        /// because the rest is document-level — but a page index is not document-level, and every
        /// copy would otherwise report the first invoking page. A caller keying a cache or a lookup
        /// on (page, name) would be handed the wrong page for all but one. So
        /// <see cref="FigureCandidate.PageIndex"/> is <c>-1</c> there, which the candidate
        /// documents as "drawn on several pages"; the picture still gets described, and nobody is
        /// told something false.</para>
        ///
        /// <para><b>A shared form with no <c>/Resources</c> of its own is offered the names every
        /// drawing page binds to the same image</b>, and refused the rest. Its names resolve against
        /// whichever page draws it, so a name two pages bind to different images has no single
        /// answer — the callback would be asked about a picture that is not the one every copy draws
        /// — and is reported; a name every page binds to one object (the usual case: one image
        /// referenced from each page's dictionary, or resources the pages inherit) has exactly one.
        /// The form is not refused whole.</para>
        /// </remarks>
        private static void AttachFormFigureSeam(
            PdfDocument doc, IReadOnlyList<PdfPage> pages, PdfPage page, AutoTagOptions options, int pageIndex,
            PageTagOptions formOptions, FormUsage form, TagPlan plan)
        {
            if (options.FigureAlternateText == null) return;
            // No null guard on `form` or `plan`: this has exactly one caller, the form loop, where
            // both are the loop's own non-null values. A mutation deleting such a guard survives
            // the whole suite, and "I deleted it and everything stayed green" is identical evidence
            // for no coverage and for dead code — here it was dead. The live null-plan check is one
            // level up in BuildPageTagOptions, where the form path really does pass null.
            bool shared = form.Invocations.Count > 1;
            List<int> drawnOn = form.InvokingPageIndexes;   // one per page, in page order

            // §8.10.1's fallback is page-dependent, so a form several pages draw is offered only
            // the names on which every one of them agrees.
            ImageCatalog images = shared && form.Resources == null
                ? ImagesEveryPageAgreesOn(doc, pages, drawnOn, plan.Images)
                : plan.Images.Of(doc, form.Resources ?? page?.Resources);
            AttachFigureSeam(images, options, shared ? -1 : pageIndex, drawnOn.AsReadOnly(), formOptions, plan);
        }

        /// <summary>
        /// The image XObjects a form with no <c>/Resources</c> of its own may be offered when several
        /// pages draw it: the names each of those pages binds to the same object.
        /// </summary>
        /// <remarks>
        /// An entry is the same image on two pages when both are references to one object, or both
        /// are the one dictionary entry (resources the pages inherit from their <c>/Pages</c>
        /// node). A name missing from any page, or bound to two objects, is left out and reported —
        /// two objects may be byte-identical, but deciding that would mean comparing images, and the
        /// fail-safe direction is an untagged image rather than a description of the wrong one.
        /// Pages that reach the first page's own <c>/XObject</c> dictionary agree on every name
        /// without a lookup, so a form drawn on every page of a document sharing one resource
        /// dictionary costs one catalogue, not the pages × the images. Any other dictionary is
        /// compared once, at the first page that reaches it: whether one of its names binds what the
        /// first page's binds does not change, so pages alternating between two dictionaries cost the
        /// two dictionaries' names, not the pages × the names.
        /// </remarks>
        internal static ImageCatalog ImagesEveryPageAgreesOn(
            PdfDocument doc, IReadOnlyList<PdfPage> pages, List<int> drawnOn, ImageCatalogs catalogs)
        {
            ImageCatalog first = null;
            PdfDictionary firstXObjects = null;
            HashSet<string> agreed = null;   // made only when a page's dictionary is another one
            var compared = new HashSet<object>(PdfReferenceEquality.Instance);
            var disagreeing = new SortedSet<string>(StringComparer.Ordinal);

            foreach (int pi in drawnOn)
            {
                PdfDictionary xobjects = ImageCatalogs.XObjectsOf(doc, pi >= 0 && pi < pages.Count ? pages[pi]?.Resources : null);
                if (first == null)
                {
                    first = catalogs.OfXObjects(doc, xobjects);
                    firstXObjects = xobjects;
                    continue;
                }
                if (ReferenceEquals(xobjects, firstXObjects)) continue;
                if (xobjects != null && !compared.Add(xobjects)) continue;

                agreed ??= new HashSet<string>(first.Names, StringComparer.Ordinal);
                foreach (string name in new List<string>(agreed))
                {
                    var key = new PdfName(name);
                    catalogs.EntriesCompared++;
                    if (SameEntry(xobjects?.Get(key), firstXObjects?.Get(key))) continue;
                    agreed.Remove(name);
                    disagreeing.Add(name);
                }
            }

            if (disagreeing.Count > 0)
                AccessibilityDiagnostics.Report("PdfAutoTagger.FigureAlternateText",
                    "a Form XObject with no /Resources of its own is drawn by pages " +
                    string.Join(", ", drawnOn.ConvertAll(p => (p + 1).ToString(CultureInfo.InvariantCulture))) +
                    " that bind /" + string.Join(", /", disagreeing) +
                    " to different images (§8.10.1), so those were not offered");

            if (first == null) return ImageCatalog.Empty;
            return agreed == null ? first : first.Restricted(agreed);
        }

        /// <summary>Whether two resource entries name one object: equal references, or the one entry.</summary>
        private static bool SameEntry(PdfObject a, PdfObject b)
            => a != null && (ReferenceEquals(a, b) || (a is PdfReference ra && ra.Equals(b as PdfReference)));

        /// <summary>
        /// Builds the whole edit. Returns null when the document cannot be tagged at all, in which
        /// case the caller copies the input through unchanged.
        /// </summary>
        private static TagPlan BuildPlan(
            PdfDocument doc, PdfObjectIndex index, AutoTagOptions options,
            List<string> warnings, List<string> skipReasons)
        {
            if (doc.IsEncrypted)
            {
                AccessibilityDiagnostics.Report(Operation,
                    "the document is encrypted, so its content streams and structure cannot be rewritten");
                warnings.Add("Document is encrypted; auto-tagging skipped.");
                return null;
            }

            PdfDictionary catalog = doc.Catalog?.Dictionary;
            if (catalog == null)
            {
                AccessibilityDiagnostics.Report(Operation,
                    "the document has no catalog, so there is nowhere to attach a structure tree");
                warnings.Add("No document catalog found; auto-tagging skipped.");
                return null;
            }

            int catalogObjectNumber = ResolveObjectNumber(index, catalog);
            if (catalogObjectNumber <= 0)
            {
                AccessibilityDiagnostics.Report(Operation,
                    "the catalog has no object number, so /StructTreeRoot could not be attached to it");
                warnings.Add("The document catalog has no object number; auto-tagging skipped.");
                return null;
            }

            IReadOnlyList<PdfPage> pages;
            try
            {
                pages = doc.Pages;
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report(Operation, "the page tree could not be read", ex);
                warnings.Add("The page tree could not be read (" + ex.Message + "); auto-tagging skipped.");
                return null;
            }

            int pageCount = pages != null ? pages.Count : 0;

            var plan = new TagPlan { PagesProcessed = pageCount };
            if (pageCount == 0)
            {
                warnings.Add("The document has no pages; nothing was tagged.");
                ApplyCatalogEdits(doc, index, catalog, catalogObjectNumber, options, plan,
                                  structTreeRootNumber: -1, markContent: false);
                ApplyTitle(doc, index, catalog, catalogObjectNumber, options, plan);
                return plan;
            }

            // ---- existing structure tree -------------------------------------------
            // An unreadable tree is an existing one (Core.Document.EntryRead). Read as none, it was
            // superseded with no warning and none of its links purged, and a merge silently became
            // a fresh tree.
            PdfDictionary existingTree = EntryRead.Resolve(doc.ResolveObject, catalog[PdfName.StructTreeRoot],
                out string unreadableTree) as PdfDictionary;
            bool hadExistingTree = existingTree != null || unreadableTree != null;

            if (unreadableTree != null && options.MergeIntoExistingTree)
            {
                warnings.Add("MergeIntoExistingTree was requested but the existing /StructTreeRoot could not "
                             + "be read (" + unreadableTree + "), so there is nothing to extend; the document "
                             + "was left untagged.");
                plan.PagesSkipped = pageCount;
                return plan;
            }

            // What the existing tree already owns, read once. Only needed to MERGE -- superseding
            // needs one boolean, which is `hadExistingTree` -- so it is read only when asked for.
            Internal.ExistingStructureTree existing = null;
            bool merging = false;

            if (options.MergeIntoExistingTree && hadExistingTree)
            {
                existing = Internal.ExistingStructureTree.Read(doc, catalog);

                if (existing == null || existing.RootObjectNumber <= 0)
                {
                    // A /StructTreeRoot written inline in the catalog has no object number to
                    // modify, so there is nothing to append to.
                    warnings.Add("MergeIntoExistingTree was requested but the existing structure "
                                 + "tree is not an indirect object, so it cannot be extended; the "
                                 + "document was left untagged.");
                    plan.PagesSkipped = pageCount;
                    return plan;
                }

                if (!existing.IsComplete)
                {
                    // Refusing beats truncating: a merge built on a partial read would write a
                    // parent tree missing the entries it could not see, turning links this pass
                    // merely failed to follow into links that no longer exist.
                    warnings.Add("MergeIntoExistingTree was requested but the existing /ParentTree "
                                 + "could not be read to the end (its size or nesting exceeded the "
                                 + "walk's limits), so merging would drop the entries this pass "
                                 + "could not see; the document was left untagged.");
                    AccessibilityDiagnostics.Report(Operation,
                        "the existing /ParentTree could not be flattened; the merge was refused");
                    plan.PagesSkipped = pageCount;
                    return plan;
                }

                merging = true;
                plan.MergedIntoExistingTree = true;
            }

            // Whether a page that already carries marked content may be COMPLETED rather than
            // skipped.
            //
            // Adopting a sequence means writing an element that claims its MCID, so it is safe
            // exactly when no element already does. The test is the whole tree rather than the
            // page, deliberately: one predicate, asked of the document, is something this pass can
            // answer from what it already holds, whereas "which MCIDs does page n's structure
            // claim" needs a /ParentTree walk and a second, page-indexed reading of the same tree.
            // Conservative in the safe direction — a document with any linked element keeps the old
            // refusal — and it costs nothing where it matters, because RemediateWithReport only
            // reaches this pass when the tree names no content at all.
            // Merging keeps the existing tree, so a sequence it already claims has an owner and
            // adopting it would give that content two. The per-PAGE test below is what allows the
            // rest of the document to be tagged anyway; this whole-tree one is the superseding
            // pass's question and stays as it was.
            // An unreadable tree may claim content this pass cannot see, so it adopts none.
            bool adoptMarkedContent = merging
                || !hadExistingTree
                || (existingTree != null && !PdfAccessibility.AnyElementReferencesContent(
                       StructTreeWalker.Collect(existingTree, doc, index), doc));

            long parentTreeKeyBase = 0;
            if (hadExistingTree)
            {
                // Parent-tree keys of the tree we are about to supersede must not be reused, or a
                // page we did not touch would resolve its marked content to one of our elements.
                // (The warning belongs further down: until at least one page is really marked, the
                // existing tree is left in place and nothing is superseded.)
                int? existingNextKey = existingTree?.GetInteger(PdfName.ParentTreeNextKey);
                if (existingNextKey.HasValue && existingNextKey.Value > 0) parentTreeKeyBase = existingNextKey.Value;

                // /ParentTreeNextKey is the file's claim about itself and a file may be wrong about
                // it; the keys actually in the tree are the fact. Taking the greater of the two is
                // what stops a new key landing on an existing entry and silently re-owning that
                // page's content.
                if (existing != null && existing.HighestKey >= parentTreeKeyBase)
                    parentTreeKeyBase = existing.HighestKey + 1;
            }

            // Every key this pass mints counts up from the base, in an int: a tree that claims keys near
            // the top of that range leaves none to mint, and carrying on writes negative ones.
            if (parentTreeKeyBase > MaxParentTreeKeyBase)
            {
                warnings.Add("The existing structure tree claims parent-tree keys up to " +
                             parentTreeKeyBase.ToString(CultureInfo.InvariantCulture) +
                             ", which leaves no room to add any; no marked content was produced and no structure tree was written.");
                AccessibilityDiagnostics.Report(Operation, "the existing parent tree's keys leave no room for new ones; no structure tree was written");
                plan.PagesSkipped = pageCount;
                for (int pi = 0; pi < pageCount; pi++)
                    skipReasons.Add(DescribeSkip(pi, "the existing structure tree's parent-tree keys leave no room for new ones."));
                ApplyCatalogEdits(doc, index, catalog, catalogObjectNumber, options, plan,
                                  structTreeRootNumber: -1, markContent: false);
                ApplyTitle(doc, index, catalog, catalogObjectNumber, options, plan);
                return plan;
            }

            if (!options.RewriteContentStreams)
            {
                // Emitting a structure tree here would recreate the exact defect this type exists
                // to fix: elements with no /K, no MCIDs, no ParentTree — invisible to AT while
                // looking conformant to a shallow audit.
                warnings.Add("AutoTagOptions.RewriteContentStreams is false, so no marked content was "
                             + "produced and no structure tree was written; only the catalog metadata "
                             + "(/Lang, /ViewerPreferences, /Info /Title) was updated.");
                AccessibilityDiagnostics.Report(Operation,
                    "content-stream rewriting is disabled; no structure tree was written");

                plan.PagesSkipped = pageCount;
                for (int pi = 0; pi < pageCount; pi++)
                    skipReasons.Add(DescribeSkip(pi, "content-stream rewriting is disabled (AutoTagOptions.RewriteContentStreams = false)."));

                ApplyCatalogEdits(doc, index, catalog, catalogObjectNumber, options, plan,
                                  structTreeRootNumber: -1, markContent: false);
                ApplyTitle(doc, index, catalog, catalogObjectNumber, options, plan);
                return plan;
            }

            // ---- median body font size ----------------------------------------------
            BodyText median = ComputeMedianFontSize(doc, pages, options);

            // ---- per-page marked content ---------------------------------------------
            var taggedStreams = new List<TaggedContentStream>(pageCount);
            var pageInvocations = new List<PageInvocations>();
            int nextParentTreeKey = (int)parentTreeKeyBase;

            for (int pi = 0; pi < pageCount; pi++)
            {
                PdfPage page = null;
                PdfDictionary pageDict = null;
                try
                {
                    page = pages[pi];
                    pageDict = page?.Dictionary;
                }
                catch (Exception ex)
                {
                    AccessibilityDiagnostics.Report(Operation,
                        "page " + pi.ToString(CultureInfo.InvariantCulture) + " could not be read", ex);
                }

                if (pageDict == null)
                {
                    plan.PagesSkipped++;
                    skipReasons.Add(DescribeSkip(pi, "the page dictionary could not be read."));
                    continue;
                }

                // A page the tree being extended already reaches is left ENTIRELY alone: not
                // re-marked, not adopted, not given a new key. Its content already has an owner,
                // and a second one is exactly the duplicate-ownership the whole-tree refusal exists
                // to prevent -- this is that same rule asked per page, which is what lets the rest
                // of the document be tagged rather than the whole of it refused.
                if (merging && existing.ClaimsPage(doc, pageDict))
                {
                    plan.PagesSkipped++;
                    plan.PagesAlreadyTagged++;
                    skipReasons.Add(DescribeSkip(pi,
                        "the existing structure tree already reaches this page's content, and "
                        + "merging leaves it to its owner."));
                    continue;
                }

                PageTagOptions tagOptions = BuildPageTagOptions(doc, page, options, median, pi, warnings, plan);
                tagOptions.AdoptExistingMarkedContent = adoptMarkedContent;
                tagOptions.Document                   = doc;
                tagOptions.MarkedContentProperties    =
                    ResolveDictionary(doc, page?.Resources?[PdfName.Properties]);

                PageRewriteResult rewrite = PageContentRewriter.RewritePage(
                    doc, index, pageDict, tagOptions, nextParentTreeKey,
                    plan.Modified, plan.New, options.CompressRewrittenStreams);

                // Form XObjects are handled after this loop, in TagForms — whether a form may be
                // tagged depends on how many pages invoke it, which is not knowable here. The
                // names are carried over rather than rescanned: this pass already read the page
                // under MaxPageContentBytes, and re-reading it there would reintroduce the
                // decompression-bomb inflation that ceiling exists to prevent.
                //
                // Recorded BEFORE the skip check, and for a skipped page too. "How many pages draw
                // this form" is what decides whether it may be tagged at all, so a page missing
                // from this list makes a shared form look private: it would then be rewritten in
                // place, and the skipped page would render marked content whose owning element
                // names a different page as its /Pg — a wrong tree that validates, the failure
                // class /Stm exists to prevent. PageContentRewriter fills InvokedXObjectNames in
                // before its own decline returns, precisely so this can be recorded here.
                if (rewrite.InvokedXObjectNames != null && rewrite.InvokedXObjectNames.Count > 0)
                {
                    pageInvocations.Add(new PageInvocations
                    {
                        PageIndex        = pi,
                        PageObjectNumber = rewrite.PageObjectNumber,

                        // The page's EFFECTIVE resources, not its own /Resources key. A page
                        // inheriting from a /Pages node has no such key, and reading it directly
                        // would yield null — the walk would find no /XObject dictionary and record
                        // the invocation as unreadable, and form tagging would do nothing at all for
                        // a document laid out that way, which is common and entirely valid.
                        Resources        = page?.Resources,
                        Names            = rewrite.InvokedXObjectNames,
                        NamesInsideArtifacts  = rewrite.NamesInsideArtifacts,
                        NamesInsideSequences  = rewrite.NamesInsideSequences,
                        NamesOutsideSequences = rewrite.NamesOutsideSequences,
                        PageWasReMarked  = rewrite.Changed,
                        PageDictionary   = pageDict,
                    });
                }

                if (!rewrite.Changed)
                {
                    plan.PagesSkipped++;
                    skipReasons.Add(DescribeSkip(pi, rewrite.SkipReason ?? "the tagger declined to modify this page."));
                    continue;
                }

                taggedStreams.Add(new TaggedContentStream
                {
                    ScopeId          = pi,
                    StreamObjectNumber = 0,
                    PageIndex        = pi,
                    PageObjectNumber = rewrite.PageObjectNumber,
                    ParentTreeKey    = nextParentTreeKey,
                    McidCount        = Math.Max(0, rewrite.NextMcid),
                    Runs             = rewrite.Runs,
                });

                // A key is consumed only for a page that really was re-marked, so every key the
                // ParentTree declares has a page pointing at it and vice versa.
                nextParentTreeKey++;
            }

            // ---- Form XObjects --------------------------------------------------------
            TagForms(doc, index, pages, pageCount, pageInvocations, options, median, plan, warnings,
                     taggedStreams, ref nextParentTreeKey, out IReadOnlyList<FormUsage> inventoriedForms);

            // ---- logical blocks -------------------------------------------------------
            List<LogicalElement> elements = BuildElements(taggedStreams, options, plan);

            if (elements.Count == 0)
            {
                string detail = taggedStreams.Count == 0
                    ? "No page could be marked, so no structure tree was written."
                    : "Marked content was written but no page produced a taggable text run, so no "
                      + "structure tree was written.";
                warnings.Add(detail + " See SkippedPageReasons for the per-page cause. "
                             + "/MarkInfo /Marked was deliberately left unset, so the document "
                             + "still reports as untagged (UA01/UA05) rather than claiming to be "
                             + "a Tagged PDF it is not.");
                AccessibilityDiagnostics.Report(Operation, detail);

                // Any page that was re-marked here carries artifacts only. Artifacts never appear
                // in the parent tree, and no parent tree is written, so the /StructParents key
                // those pages were handed would dangle — strip it back off.
                for (int i = 0; i < taggedStreams.Count; i++)
                {
                    if (plan.Modified.TryGetValue(taggedStreams[i].PageObjectNumber, out PdfObject revised)
                        && revised is PdfDictionary revisedPage)
                    {
                        revisedPage.Remove(PageContentRewriter.StructParents);
                    }
                }

                // /MarkInfo /Marked is NOT written here, even though pages really were re-marked.
                //
                // /Marked true does not mean "some marked content exists"; per ISO 32000-1 §14.7.1
                // (Table 321) it asserts the document follows the Tagged PDF conventions, and those
                // require a structure tree. This branch writes none — the only marked content is
                // artifacts, which never appear in a structure tree at all.
                //
                // Setting it anyway is not a harmless overstatement: every PDF/UA checker keys
                // "does this document claim to be tagged" off /StructTreeRoot || /MarkInfo /Marked.
                // Claiming it here silenced UA01/UA05 ("not tagged" — the true finding) and in
                // exchange switched ON the structure-dependent checks, so an artifact-only page
                // carrying an annotation had UA23 reported against it. Remediation manufactured a
                // violation while hiding the real one. The metadata step in the pipeline already
                // refuses exactly this trade for exactly this reason; this is the same rule applied
                // at the other place that can write the key.
                ApplyCatalogEdits(doc, index, catalog, catalogObjectNumber, options, plan,
                                  structTreeRootNumber: -1, markContent: false);
                ApplyTitle(doc, index, catalog, catalogObjectNumber, options, plan);
                plan.ContentMarked = false;
                return plan;
            }

            // ---- annotations that belong in the structure tree ------------------------
            // Collected *before* the purge below so that pass can tell a /StructParent this one is
            // about to make valid from one that is genuinely dangling. Emitting no /OBJR at all, the
            // purge would strip every annotation's key and the audit then report UA23 on output
            // produced from an input that had no such finding: remediation manufacturing a
            // violation.
            List<AnnotationElement> annotations =
                CollectAnnotations(doc, index, pages, pageCount, elements, warnings,
                    options.LinkDescriptionsFollow, merging ? existing : null);

            // From here a real structure tree is written, so an existing one is genuinely
            // superseded — which is the point at which saying so is accurate.
            if (hadExistingTree && merging)
            {
                // Nothing is superseded, so nothing is purged. The existing keys stay valid
                // precisely because the entries that define them are carried into the merged
                // parent tree below -- which is what PurgeStaleStructureLinks would otherwise be
                // repairing, and what it would destroy if it ran here.
                AccessibilityDiagnostics.Report(Operation,
                    "the document already had a /StructTreeRoot; the generated elements extend it");
            }
            else
            {
                if (hadExistingTree)
                {
                    warnings.Add("The document already had a structure tree; it has been superseded by the "
                                 + "generated one, so any structure element it contained is no longer reachable.");
                    AccessibilityDiagnostics.Report(Operation,
                        "the document already had a /StructTreeRoot; the generated tree supersedes it");
                }

                // Leaving old keys behind would produce an *invalid* file, not merely a lossy one:
                // ISO 32000-1 §14.7.4.4 requires every /StructParent(s) value to be a key in the
                // ParentTree, and the generated tree only defines keys for pages this pass re-marked.
                // A dangling key is what makes the library's own UA14/UA23 checks fire on output that
                // was produced from conformant input. A document with no tree can carry such keys too
                // — a split leaves the page dictionaries as they were — and the keys this pass mints
                // count from 0 there, so a page it skipped would share its key with one it re-marked.
                PurgeStaleStructureLinks(doc, index, pages, pageCount, taggedStreams, annotations,
                                         inventoriedForms, plan, warnings);
            }

            // ---- object numbers -------------------------------------------------------
            // Merging keeps the existing root object rather than minting one, so its /RoleMap,
            // /ClassMap and /IDTree survive without being copied -- and every element already
            // naming it as /P still names the right object.
            int structTreeRootNumber = merging
                ? existing.RootObjectNumber
                : index.AllocateObjectNumber();
            int documentElementNumber = index.AllocateObjectNumber();
            int parentTreeNumber      = index.AllocateObjectNumber();

            // PDF 2.0's standard structure namespace, when the caller is authoring for PDF/UA-2.
            // Zero when not: a PDF/UA-1 document must not start carrying PDF 2.0 namespaces because
            // the tagger learned how to write them.
            int namespaceNumber = options.Target == PdfUaVersion.Ua2 ? index.AllocateObjectNumber() : 0;
            for (int i = 0; i < elements.Count; i++)
                elements[i].ObjectNumber = index.AllocateObjectNumber();
            for (int i = 0; i < annotations.Count; i++)
                annotations[i].ObjectNumber = index.AllocateObjectNumber();

            // ---- annotation wrappers --------------------------------------------------
            // Built before the blocks so each block already knows the child elements its /K must
            // list, and after the page keys are final so no annotation key can collide with one.
            List<AnnotationElement> taggedAnnotations = BuildAnnotationElements(
                index, annotations, documentElementNumber, plan, warnings, ref nextParentTreeKey);

            // ---- structure elements ---------------------------------------------------
            var documentKids = new PdfArray();
            for (int i = 0; i < elements.Count; i++)
            {
                LogicalElement element = elements[i];

                // A nested element names its own parent and is reached through that parent's /K,
                // which BuildStructElement already writes from ChildElementObjectNumbers. Listing
                // it under /Document as well would give it two parents: /K would reach it twice and
                // its /P would agree with only one of them.
                int parentNumber = element.Parent?.ObjectNumber ?? documentElementNumber;

                plan.New[element.ObjectNumber] =
                    BuildStructElement(element, parentNumber, index, options, namespaceNumber);

                if (element.Parent == null)
                    documentKids.Add(new PdfReference(element.ObjectNumber, 0));
            }

            // An annotation that falls inside no block hangs off /Document directly. They follow
            // the blocks rather than interleaving with them: the tagger knows a block's vertical
            // band but not its horizontal extent, so any finer claim about reading position would
            // be invented rather than measured.
            for (int i = 0; i < taggedAnnotations.Count; i++)
                if (taggedAnnotations[i].Parent == null)
                    documentKids.Add(new PdfReference(taggedAnnotations[i].ObjectNumber, 0));

            var documentElement = new PdfDictionary(4);
            documentElement.Set(PdfName.Type, PdfName.StructElem);
            documentElement.Set(PdfName.S,    PdfName.Document);
            documentElement.Set(PdfName.P,    new PdfReference(structTreeRootNumber, index.GetGeneration(structTreeRootNumber)));
            documentElement.Set(PdfName.K,    documentKids);
            SetStructureNamespace(documentElement, namespaceNumber);
            plan.New[documentElementNumber] = documentElement;

            if (namespaceNumber > 0) plan.New[namespaceNumber] = Pdf2StructureNamespace();

            // ---- parent number tree ---------------------------------------------------
            plan.New[parentTreeNumber] = BuildParentTree(
                taggedStreams, elements, taggedAnnotations, existing);

            // ---- structure tree root --------------------------------------------------
            PdfDictionary structTreeRoot;
            PdfArray rootKids;

            if (merging)
            {
                // A CLONE of the existing root, so /RoleMap and anything else it carries survives
                // and the document being read is never mutated. Its kids are appended to rather
                // than replaced: /K may be a single element rather than an array, which
                // CloneKids promotes.
                structTreeRoot = PdfDictionary.CloneShallow(existing.Root);
                rootKids = existing.CloneKids(doc);
                rootKids.Add(new PdfReference(documentElementNumber, 0));
                structTreeRoot.Set(PdfName.Type, PdfName.StructTreeRoot);
                structTreeRoot.Set(PdfName.K, rootKids);
                structTreeRoot.Set(PdfName.ParentTree, new PdfReference(parentTreeNumber, 0));
                structTreeRoot.Set(PdfName.ParentTreeNextKey, new PdfNumber(nextParentTreeKey));
                DeclareStructureNamespace(doc, structTreeRoot, namespaceNumber);
                plan.Modified[structTreeRootNumber] = structTreeRoot;
            }
            else
            {
                structTreeRoot = new PdfDictionary(4);
                structTreeRoot.Set(PdfName.Type, PdfName.StructTreeRoot);
                rootKids = new PdfArray();
                rootKids.Add(new PdfReference(documentElementNumber, 0));
                structTreeRoot.Set(PdfName.K, rootKids);
                structTreeRoot.Set(PdfName.ParentTree, new PdfReference(parentTreeNumber, 0));
                // One greater than the greatest key in use across BOTH page /StructParents keys and
                // annotation /StructParent keys -- the counter has been advanced by both.
                structTreeRoot.Set(PdfName.ParentTreeNextKey, new PdfNumber(nextParentTreeKey));
                DeclareStructureNamespace(doc, structTreeRoot, namespaceNumber);
                plan.New[structTreeRootNumber] = structTreeRoot;
            }

            // /Document, one element per logical block, one wrapper per tagged annotation.
            plan.ElementsAdded     = elements.Count + taggedAnnotations.Count + 1;
            plan.AnnotationsTagged = taggedAnnotations.Count;
            plan.ContentMarked     = true;

            // ---- catalog + /Info ------------------------------------------------------
            ApplyCatalogEdits(doc, index, catalog, catalogObjectNumber, options, plan,
                              structTreeRootNumber, markContent: true);
            ApplyTitle(doc, index, catalog, catalogObjectNumber, options, plan);

            if (plan.McidCount == 0)
                warnings.Add("The structure tree was written but references no MCIDs; assistive "
                             + "technology will not reach the page content.");

            // An image nobody could describe is content a screen reader will not reach, and the
            // count alone is not enough: RemediateWithReport surfaces a step's WARNINGS, not its
            // AutoTagResult, so a caller using the facade would otherwise have no way to learn
            // this happened at all.
            //
            // It cannot fire on conformant input, which is the bar a new channel has to clear:
            // FiguresDeclined only ever moves when the caller supplied a callback AND that
            // callback returned nothing for an image it was offered. A caller not using the
            // feature sees the seam unattached and the count at zero.
            if (plan.FiguresDeclined > 0)
                warnings.Add(plan.FiguresDeclined.ToString(CultureInfo.InvariantCulture)
                             + " image(s) were left untagged because AutoTagOptions."
                             + "FigureAlternateText supplied no alternate text for them; they carry "
                             + "no /Figure and assistive technology will not reach them.");

            return plan;
        }

        // ================================================================
        //  Logical blocks
        // ================================================================

        /// <summary>
        /// Groups the tagger's runs into structure elements. Runs sharing a
        /// <see cref="TaggedRun.ParagraphId"/> on the same page belong to one element, so a
        /// paragraph drawn across several text objects yields one <c>/P</c> with several MCIDs —
        /// which is exactly how a real tagged PDF represents it. Artifacts are counted but never
        /// become elements: they carry no MCID and must stay outside the structure tree.
        /// </summary>
        private static List<LogicalElement> BuildElements(
            List<TaggedContentStream> taggedStreams, AutoTagOptions options, TagPlan plan)
        {
            var elements = new List<LogicalElement>();
            var byParagraph = new Dictionary<int, LogicalElement>();

            for (int p = 0; p < taggedStreams.Count; p++)
            {
                TaggedContentStream page = taggedStreams[p];
                IReadOnlyList<TaggedRun> runs = page.Runs;
                if (runs == null) continue;

                // Paragraph ids restart at 0 on every page, so the grouping map does too.
                byParagraph.Clear();

                for (int r = 0; r < runs.Count; r++)
                {
                    TaggedRun run = runs[r];
                    if (run == null) continue;

                    if (run.IsArtifact)
                    {
                        plan.ArtifactCount++;
                        continue;
                    }

                    if (run.Mcid < 0) continue;   // defensive: a content run always has an MCID

                    LogicalElement element;
                    if (run.ParagraphId < 0 || !byParagraph.TryGetValue(run.ParagraphId, out element))
                    {
                        element = new LogicalElement
                        {
                            Role = NormalizeRole(run.Role),
                            Text = options.EmitActualText ? new StringBuilder() : null,
                        };
                        element.TableId       = run.TableId;
                        element.TableRowIndex = run.TableRowIndex;
                        element.AlternateText = run.AlternateText;

                        if (run.AlternateText != null) plan.FigureCount++;

                        if (run.ParagraphId >= 0) byParagraph[run.ParagraphId] = element;
                        elements.Add(element);
                    }

                    element.Mcids.Add(new McidRef(page.ScopeId, page.PageIndex, page.PageObjectNumber,
                                                 page.StreamObjectNumber, run.Mcid));
                    plan.McidCount++;

                    if (element.PageIndex < 0) element.PageIndex = page.PageIndex;
                    ExtendBand(element, run);

                    if (element.Text != null && !string.IsNullOrEmpty(run.Text))
                    {
                        if (element.Text.Length > 0) element.Text.Append(' ');
                        element.Text.Append(run.Text);
                    }
                }
            }

            elements = WrapListItems(elements, plan);
            elements = WrapTableCells(elements, plan);

            for (int i = 0; i < elements.Count; i++)
            {
                string role = elements[i].Role;

                // HeadingLevel returns 0 for a bare "H" — a PDF/UA "strong structure" heading
                // whose level comes from its /Sect nesting, not from its name. The default
                // classifier never produces one, but counting it as an H1 would be wrong, so it
                // is recorded under key 0 rather than silently folded into level 1 or dropped.
                int level = StructTreeWalker.HeadingLevel(role);
                if (level >= 0)
                {
                    plan.HeadingsByLevel.TryGetValue(level, out int count);
                    plan.HeadingsByLevel[level] = count + 1;
                }
                else if (string.Equals(role, StructureTypes.Paragraph, StringComparison.Ordinal))
                {
                    plan.ParagraphCount++;
                }
            }

            return elements;
        }

        /// <summary>
        /// Wraps each run of consecutive <c>LBody</c> elements in <c>LI</c> parents and one
        /// <c>L</c>, returning the elements in the order <c>/Document</c>'s kid list needs them.
        /// </summary>
        /// <remarks>
        /// <para><b>Three levels, because ISO 14289-1 §7.6 has three.</b> An <c>/L</c>'s
        /// children must be <c>/LI</c> or <c>/Caption</c> and an <c>/LI</c>'s must be <c>/Lbl</c>
        /// or <c>/LBody</c> — so an <c>/LBody</c> hanging directly off <c>/Document</c>, which
        /// is what the classifier alone produces, is a finding rather than a list. This is the pass
        /// that turns a role into a structure.</para>
        ///
        /// <para><b>No <c>/Lbl</c>, and that is a decision rather than an omission.</b> Separating
        /// the marker from the body means splitting one line's marked content at a byte boundary
        /// inside a show operator, which the splicer cannot do without re-serialising it — and
        /// §7.6 is satisfied by an <c>/LI</c> carrying a body alone. The marker stays inside
        /// the body text, as it does in a great many hand-authored documents.</para>
        ///
        /// <para><b>Order is preserved and the container takes its items' place.</b> The list is
        /// inserted where its first item stood, so reading order is unchanged; the items leave the
        /// top level because their <c>Parent</c> now points at an <c>LI</c>, and the
        /// <c>/Document</c> loop lists only roots.</para>
        /// </remarks>
        private static List<LogicalElement> WrapListItems(List<LogicalElement> elements, TagPlan plan)
        {
            bool any = false;
            for (int i = 0; i < elements.Count; i++)
                if (IsListBody(elements[i])) { any = true; break; }

            // The overwhelmingly common page has no inferred list, and it must come out of here as
            // the same list object it went in as rather than as a copy.
            if (!any) return elements;

            var result = new List<LogicalElement>(elements.Count);

            for (int i = 0; i < elements.Count;)
            {
                if (!IsListBody(elements[i])) { result.Add(elements[i]); i++; continue; }

                int end = i;
                while (end < elements.Count && IsListBody(elements[end])
                       && elements[end].PageIndex == elements[i].PageIndex)
                    end++;

                var list = new LogicalElement { Role = StructureTypes.List };
                result.Add(list);

                for (int k = i; k < end; k++)
                {
                    LogicalElement body = elements[k];

                    var item = new LogicalElement { Role = StructureTypes.ListItem, Parent = list };
                    item.ChildElements.Add(body);
                    body.Parent = item;

                    list.ChildElements.Add(item);

                    // The band and the page travel up with the content, so an annotation whose
                    // /Rect falls on a list still finds a block to hang off.
                    CarryUp(item, body);
                    CarryUp(list, body);

                    result.Add(item);
                    result.Add(body);
                }

                plan.ListCount++;
                i = end;
            }

            return result;
        }

        /// <summary>
        /// Wraps each run of consecutive table cells in <c>TR</c> rows and one <c>Table</c>,
        /// returning the elements in the order <c>/Document</c>'s kid list needs them.
        /// </summary>
        /// <remarks>
        /// <para><b>Three levels, because ISO 14289-1 requires three.</b> A <c>/TD</c> or
        /// <c>/TH</c> hanging off <c>/Document</c> — which the classifier alone produces — is not
        /// a table; UA12 requires a <c>/TR</c> whose children are only cells, inside a
        /// <c>/Table</c>. This is the pass that turns a role into a structure, exactly as
        /// <see cref="WrapListItems"/> does for a list.</para>
        ///
        /// <para><b>The grouping keys are carried, not recomputed.</b> A cell knows its table and
        /// its row because <c>ContentStreamTagger</c> settled the grid before it classified
        /// anything; deriving the rows here from the cells' Y bands would be re-deciding, on
        /// coarser evidence, a question already answered.</para>
        ///
        /// <para><b>A run is broken at a page boundary and at a change of table</b>, so a
        /// <c>/Table</c> never spans two pages. One table continued across a page break is legal
        /// and is not something a heuristic reading each page independently has evidence for.</para>
        ///
        /// <para><b>Row 0's cells get <c>/Scope /Column</c>.</b> A <c>/TH</c> with neither a
        /// <c>/Scope</c> nor an <c>/ID</c> that some data cell references is a UA12 finding in its
        /// own right — a header no cell is associated with — so emitting the header without the
        /// scope would trade one finding for another.</para>
        /// </remarks>
        private static List<LogicalElement> WrapTableCells(List<LogicalElement> elements, TagPlan plan)
        {
            bool any = false;
            for (int i = 0; i < elements.Count; i++)
                if (IsTableCell(elements[i])) { any = true; break; }

            // The overwhelmingly common page has no inferred table, and must come out of here as
            // the same list object it went in as rather than as a copy.
            if (!any) return elements;

            var result = new List<LogicalElement>(elements.Count);

            for (int i = 0; i < elements.Count;)
            {
                if (!IsTableCell(elements[i])) { result.Add(elements[i]); i++; continue; }

                int end = i;
                while (end < elements.Count && IsTableCell(elements[end])
                       && elements[end].TableId == elements[i].TableId
                       && elements[end].PageIndex == elements[i].PageIndex)
                    end++;

                var table = new LogicalElement { Role = StructureTypes.Table };
                result.Add(table);

                LogicalElement row = null;
                int openRowIndex = int.MinValue;

                for (int k = i; k < end; k++)
                {
                    LogicalElement cell = elements[k];

                    if (row == null || cell.TableRowIndex != openRowIndex)
                    {
                        row = new LogicalElement { Role = StructureTypes.TableRow, Parent = table };
                        openRowIndex = cell.TableRowIndex;
                        table.ChildElements.Add(row);
                        result.Add(row);
                    }

                    if (string.Equals(cell.Role, StructureTypes.TableHeaderCell, StringComparison.Ordinal))
                        cell.HeaderScope = ScopeColumn;

                    cell.Parent = row;
                    row.ChildElements.Add(cell);

                    // The band and the page travel up with the content, so an annotation whose
                    // /Rect falls on a table still finds a block to hang off.
                    CarryUp(row, cell);
                    CarryUp(table, cell);

                    result.Add(cell);
                }

                plan.TableCount++;
                i = end;
            }

            return result;
        }

        /// <summary>The <c>/Scope</c> value a first-row header cell gets.</summary>
        private const string ScopeColumn = "Column";

        private static bool IsTableCell(LogicalElement element)
            => element != null
               && element.TableId >= 0
               && (string.Equals(element.Role, StructureTypes.TableDataCell, StringComparison.Ordinal)
                   || string.Equals(element.Role, StructureTypes.TableHeaderCell, StringComparison.Ordinal));

        private static bool IsListBody(LogicalElement element)
            => element != null
               && string.Equals(element.Role, StructureTypes.ListBody, StringComparison.Ordinal);

        /// <summary>Extends <paramref name="ancestor"/>'s page and band to cover <paramref name="child"/>.</summary>
        /// <remarks>
        /// A run of items is broken at a page boundary before this is reached, so an ancestor's
        /// band never spans two pages. One <c>/L</c> across a page break is legal and is not
        /// something a heuristic reading each page independently has evidence for: the items after
        /// the break may continue the list or start a new one, and the tree must not claim to know.
        /// </remarks>
        private static void CarryUp(LogicalElement ancestor, LogicalElement child)
        {
            if (ancestor.PageIndex < 0) ancestor.PageIndex = child.PageIndex;
            if (child.MinY < ancestor.MinY) ancestor.MinY = child.MinY;
            if (child.MaxY > ancestor.MaxY) ancestor.MaxY = child.MaxY;
        }

        /// <summary>
        /// Widens a block's vertical band to include one run.
        ///
        /// <para>
        /// The content-stream tagger records a baseline <c>Y</c> and the run's largest effective
        /// font size — not a bounding box — so the band is derived from those: one em above the
        /// baseline for the ascender and three tenths of an em below it for the descender. It is
        /// used for one decision only, which block (if any) an annotation's <c>/Rect</c> falls
        /// inside, and a wrong answer costs the annotation its reading <em>position</em>, never
        /// its reachability: it simply becomes a child of <c>/Document</c> instead.
        /// </para>
        /// </summary>
        private static void ExtendBand(LogicalElement element, TaggedRun run)
        {
            float size   = run.FontSize > 0f ? run.FontSize : 0f;
            float bottom = run.Y - size * 0.3f;
            float top    = run.Y + size;

            if (bottom < element.MinY) element.MinY = bottom;
            if (top    > element.MaxY) element.MaxY = top;
        }

        /// <summary>
        /// The PDF 2.0 standard structure namespace dictionary (ISO 32000-2 §14.8.6).
        /// </summary>
        /// <remarks>
        /// <b>PDF/UA-2 requires it to be present and explicit</b>, because an absent <c>/NS</c> does
        /// not mean "unspecified" — ISO 32000-2 §14.8.6 defaults it to the PDF 1.7 namespace. So a
        /// tree of correctly named PDF 2.0 elements with no namespace declares itself to be PDF 1.7
        /// structure, and ISO 14289-2 §8.2.5.2 rejects exactly that. The auto-tagger's output used
        /// to fail that check for this reason and no other.
        /// </remarks>
        private static PdfDictionary Pdf2StructureNamespace()
        {
            var ns = new PdfDictionary(2);
            ns.Set(PdfName.Type, PdfName.Namespace);
            ns.Set(PdfName.NS, new PdfString(Pdf2StructureNamespaceUri));
            return ns;
        }

        /// <summary>ISO 32000-2 §14.8.6's standard structure namespace for PDF 2.0.</summary>
        private const string Pdf2StructureNamespaceUri = "http://iso.org/pdf2/ssn";

        /// <summary>Points one structure element at the namespace, when there is one.</summary>
        private static void SetStructureNamespace(PdfDictionary element, int namespaceNumber)
        {
            if (namespaceNumber > 0) element.Set(PdfName.NS, new PdfReference(namespaceNumber, 0));
        }

        /// <summary>
        /// Adds the namespace to the structure tree root's <c>/Namespaces</c>, keeping any the
        /// document already declares.
        /// </summary>
        /// <remarks>
        /// Appending rather than replacing matters when merging into an existing tree: that array is
        /// the document's, and the elements already in it name entries by reference — so it is
        /// resolved (an indirect array read raw is no array, and was replaced) and copied before the
        /// namespace is added, since the root being written is a shallow clone and the document being
        /// read is never mutated.
        /// </remarks>
        private static void DeclareStructureNamespace(PdfDocument doc, PdfDictionary structTreeRoot, int namespaceNumber)
        {
            if (namespaceNumber <= 0) return;

            var namespaces = new PdfArray();
            if (doc.ResolveObject(structTreeRoot[PdfName.Namespaces]) is PdfArray declared)
                foreach (PdfObject entry in declared) namespaces.Add(entry);
            bool present = false;
            foreach (PdfObject entry in namespaces)
                if (entry is PdfReference r && r.ReferenceObjectNumber == namespaceNumber) present = true;
            if (!present) namespaces.Add(new PdfReference(namespaceNumber, 0));
            structTreeRoot.Set(PdfName.Namespaces, namespaces);
        }

        private static PdfDictionary BuildStructElement(
            LogicalElement element, int parentObjectNumber, PdfObjectIndex index, AutoTagOptions options,
            int namespaceNumber)
        {
            var dict = new PdfDictionary(6);
            dict.Set(PdfName.Type, PdfName.StructElem);
            dict.Set(PdfName.S,    new PdfName(element.Role));
            dict.Set(PdfName.P,    new PdfReference(parentObjectNumber, 0));
            SetStructureNamespace(dict, namespaceNumber);

            int firstPageObjectNumber = element.Mcids.Count > 0 ? element.Mcids[0].PageObjectNumber : -1;
            if (firstPageObjectNumber > 0)
                dict.Set(PdfName.Pg, new PdfReference(firstPageObjectNumber, index.GetGeneration(firstPageObjectNumber)));

            // /K: a bare integer names an MCID in the content stream of this element's own /Pg.
            // Anything else needs a marked-content reference (ISO 32000-1 §14.7.4.3, Table 324):
            //   - content on another page          → /MCR with /Pg
            //   - content inside a Form XObject    → /MCR with /Stm naming that stream
            //
            // /Stm is the entry that is easy to omit and impossible to notice: without it the
            // reference still parses, still has the right shape, and addresses MCID n of the
            // PAGE's content stream — different content, in a tree that validates.
            var kids = new PdfArray();
            for (int i = 0; i < element.Mcids.Count; i++)
            {
                McidRef mcid = element.Mcids[i];
                bool samePage = mcid.PageObjectNumber == firstPageObjectNumber;

                if (samePage && mcid.StreamObjectNumber <= 0)
                {
                    kids.Add(new PdfNumber(mcid.Mcid));
                    continue;
                }

                var mcr = new PdfDictionary(4);
                mcr.Set(PdfName.Type, McrType);
                mcr.Set(PdfName.Pg,   new PdfReference(mcid.PageObjectNumber, index.GetGeneration(mcid.PageObjectNumber)));
                if (mcid.StreamObjectNumber > 0)
                    mcr.Set(Stm, new PdfReference(mcid.StreamObjectNumber, index.GetGeneration(mcid.StreamObjectNumber)));
                mcr.Set(PdfName.MCID, new PdfNumber(mcid.Mcid));
                kids.Add(mcr);
            }

            // Nested structure elements — a list's items, an item's body — come before the
            // annotation wrappers: they ARE this element's content, where an annotation merely sits
            // on top of it. A container has no marked content of its own, so for one of those this
            // is the whole of /K.
            for (int i = 0; i < element.ChildElements.Count; i++)
                kids.Add(new PdfReference(element.ChildElements[i].ObjectNumber, 0));

            // Child elements — the annotation wrappers whose /Rect falls inside this block — come
            // after the block's own marked content, so the block reads as its text followed by the
            // annotations sitting in it.
            for (int i = 0; i < element.ChildElementObjectNumbers.Count; i++)
                kids.Add(new PdfReference(element.ChildElementObjectNumbers[i], 0));

            if (kids.Count == 1) dict.Set(PdfName.K, kids[0]);
            else if (kids.Count > 1) dict.Set(PdfName.K, kids);

            // §14.9.3: /Alt describes content that is not text. A /Figure without one is a UA07
            // finding, which is why an image the caller could not describe was never tagged at all.
            if (element.AlternateText != null)
                dict.Set(PdfName.Alt, MakeTextString(element.AlternateText));

            // §14.7.5: a table attribute lives in an attribute dictionary whose /O names the
            // owner. /Scope on a bare TH would be read by nothing — the checker looks it up
            // through /A and /C, both of which are owner-qualified.
            if (element.HeaderScope != null)
            {
                var attributes = new PdfDictionary(2);
                attributes.Set(PdfName.O, OwnerTable);
                attributes.Set(PdfName.Scope, new PdfName(element.HeaderScope));
                dict.Set(PdfName.A, attributes);
            }

            if (options.EmitActualText && element.Text != null)
            {
                string text = element.Text.ToString().Trim();
                if (text.Length > 0) dict.Set(PdfName.ActualText, MakeTextString(text));
            }

            return dict;
        }

        /// <summary>
        /// Builds the parent number tree.
        ///
        /// <para>
        /// <b>The two key kinds have different value shapes and must not be confused</b>
        /// (ISO 32000-1 §14.7.4.4). A <em>page</em> key — the page's <c>/StructParents</c> — maps
        /// to an <b>array</b> indexed by MCID holding a reference to the element that owns each
        /// marked-content sequence; gaps (an MCID no element claimed — which this tagger never
        /// produces, but a malformed input could) become <c>null</c> rather than shifting every
        /// later index. An <em>annotation</em> key — the annotation's <c>/StructParent</c>, singular
        /// — maps to a <b>direct reference</b> to the single element that owns it. Wrapping that
        /// reference in an array is the classic mistake here, and the library's own UA14 check
        /// reports it.
        /// </para>
        /// </summary>
        /// <param name="taggedStreams">
        /// One entry per content stream this pass marked, carrying its parent-tree key and how
        /// many MCIDs it emitted.
        /// </param>
        /// <param name="elements">The logical elements, each naming the MCIDs it owns.</param>
        /// <param name="annotations">
        /// The annotation wrappers, each of which owns a key of its own rather than a run of MCIDs.
        /// </param>
        /// <param name="existing">
        /// The tree being extended, whose entries are carried into the result, or <c>null</c> when
        /// this pass is writing a fresh tree.
        /// </param>
        /// <remarks>
        /// <b>The existing entries go in first and the new keys are all above them</b>, so the
        /// defensive sort below has nothing to do — but it is what makes that a property of the
        /// data rather than of the order two loops happen to run in. An unsorted <c>/Nums</c> is one
        /// a conformant reader is entitled to binary-search and get the wrong answer from.
        /// </remarks>
        private static PdfDictionary BuildParentTree(
            List<TaggedContentStream> taggedStreams, List<LogicalElement> elements,
            List<AnnotationElement> annotations, Internal.ExistingStructureTree existing = null)
        {
            // (page index, MCID) → owning element object number.
            var owner = new Dictionary<long, int>();
            for (int e = 0; e < elements.Count; e++)
            {
                LogicalElement element = elements[e];
                for (int i = 0; i < element.Mcids.Count; i++)
                {
                    McidRef mcid = element.Mcids[i];
                    owner[PackKey(mcid.ScopeId, mcid.Mcid)] = element.ObjectNumber;
                }
            }

            var byKey = new List<KeyValuePair<int, PdfObject>>(
                taggedStreams.Count + (annotations != null ? annotations.Count : 0));

            // Everything the tree being extended already defines. Carried forward unresolved: these
            // are other elements' entries and this pass has no business rewriting them.
            if (existing != null)
            {
                for (int i = 0; i < existing.Entries.Count; i++)
                {
                    KeyValuePair<PdfObject, PdfObject> entry = existing.Entries[i];
                    if (!Core.Document.PdfNumberTree.TryKey(entry.Key, out long key)) continue;
                    if (key < 0 || key > int.MaxValue) continue;

                    byKey.Add(new KeyValuePair<int, PdfObject>((int)key, entry.Value));
                }
            }

            for (int p = 0; p < taggedStreams.Count; p++)
            {
                TaggedContentStream page = taggedStreams[p];
                var entries = new PdfArray();
                for (int mcid = 0; mcid < page.McidCount; mcid++)
                {
                    entries.Add(owner.TryGetValue(PackKey(page.ScopeId, mcid), out int objectNumber)
                        ? (PdfObject)new PdfReference(objectNumber, 0)
                        : PdfNull.Instance);
                }

                byKey.Add(new KeyValuePair<int, PdfObject>(page.ParentTreeKey, entries));
            }

            if (annotations != null)
            {
                for (int a = 0; a < annotations.Count; a++)
                {
                    AnnotationElement annotation = annotations[a];
                    if (annotation.ParentTreeKey < 0) continue;   // never stamped; nothing points at it

                    byKey.Add(new KeyValuePair<int, PdfObject>(
                        annotation.ParentTreeKey, new PdfReference(annotation.ObjectNumber, 0)));
                }
            }

            // /Nums must be sorted by key. Page keys are handed out in ascending page order and
            // annotation keys continue from the highest of them, so the list is already sorted —
            // but sort defensively rather than rely on it: an unsorted number tree is one a
            // conformant reader is entitled to binary-search and get the wrong answer from.
            byKey.Sort((x, y) => x.Key.CompareTo(y.Key));

            var nums = new PdfArray();
            for (int i = 0; i < byKey.Count; i++)
            {
                nums.Add(new PdfNumber(byKey[i].Key));
                nums.Add(byKey[i].Value);
            }

            var parentTree = new PdfDictionary(1);
            parentTree.Set(PdfName.Nums, nums);
            return parentTree;
        }

        /// <summary>
        /// (content-stream scope, MCID) → one key. <b>Scope, not page index</b>: a Form XObject has
        /// its own MCID space, so a form's MCID 0 and its page's MCID 0 are different content and
        /// must not collide here.
        /// </summary>
        private static long PackKey(int scopeId, int mcid) => ((long)scopeId << 32) | (uint)mcid;

        // ================================================================
        //  Annotations (ISO 32000-1 §14.7.4.3 / §14.8.4.4)
        // ================================================================

        /// <summary>
        /// Finds every annotation that belongs in the structure tree and decides which block, if
        /// any, encloses it. Nothing is written here — object numbers and parent-tree keys are not
        /// allocated yet — so the result can be handed to
        /// <see cref="PurgeStaleStructureLinks"/> as the set of annotations whose
        /// <c>/StructParent</c> must be kept.
        ///
        /// <para>
        /// <b>Annotations on pages this pass could not re-mark are included.</b> An <c>/OBJR</c>
        /// does not depend on the page carrying marked content — it names the annotation, not an
        /// MCID — so excluding them would leave exactly the UA23 findings this work exists to
        /// prevent. Such an annotation simply finds no enclosing block and hangs off
        /// <c>/Document</c>.
        /// </para>
        /// </summary>
        /// <param name="doc">The document being tagged.</param>
        /// <param name="index">Its object index, for the page and annotation object numbers.</param>
        /// <param name="pages">Its pages.</param>
        /// <param name="pageCount">How many of them the pass covers.</param>
        /// <param name="elements">The logical blocks found, which an annotation is parented under when one encloses it.</param>
        /// <param name="warnings">Where a direct annotation or a link without a description is said.</param>
        /// <param name="linkDescriptionsFollow">
        /// <see cref="AutoTagOptions.LinkDescriptionsFollow"/>: a later step writes the links'
        /// descriptions and reports those it leaves out, so a link without one is not said here.
        /// </param>
        /// <param name="existing">
        /// The tree being merged into, or null when the pass supersedes or there is none: an annotation
        /// on a page that tree claims, or one whose <c>/StructParent</c> is a key it defines, already has
        /// a parent and is left to it.
        /// </param>
        private static List<AnnotationElement> CollectAnnotations(
            PdfDocument doc, PdfObjectIndex index, IReadOnlyList<PdfPage> pages, int pageCount,
            List<LogicalElement> elements, List<string> warnings, bool linkDescriptionsFollow,
            ExistingStructureTree existing = null)
        {
            var result = new List<AnnotationElement>();
            var seen   = new HashSet<int>();

            Dictionary<int, List<LogicalElement>> blocksByPage = GroupBlocksByPage(elements);

            int directAnnotations      = 0;
            int linksWithoutDescription = 0;

            for (int pi = 0; pi < pageCount; pi++)
            {
                PdfDictionary pageDict;
                try
                {
                    pageDict = pages[pi]?.Dictionary;
                }
                catch (Exception ex)
                {
                    AccessibilityDiagnostics.Report(Operation,
                        "page " + pi.ToString(CultureInfo.InvariantCulture) +
                        " could not be read while collecting annotations", ex);
                    continue;
                }
                if (pageDict == null) continue;

                // /OBJR carries a /Pg, and the wrapper element does too, so a page we cannot name
                // is a page whose annotations cannot be referenced correctly.
                int pageObjectNumber = ResolveObjectNumber(index, pageDict);
                if (pageObjectNumber <= 0) continue;

                // A page the tree being extended reaches is left to it, annotations included, as the
                // page loop leaves its content.
                if (existing != null && existing.ClaimsPage(doc, pageDict)) continue;

                PdfArray annots = ResolveArray(doc, pageDict[PdfName.Annots]);
                if (annots == null) continue;

                blocksByPage.TryGetValue(pi, out List<LogicalElement> blocks);

                for (int ai = 0; ai < annots.Count; ai++)
                {
                    PdfObject raw = annots[ai];
                    PdfDictionary annot = ResolveDictionary(doc, raw);
                    if (annot == null) continue;
                    if (existing != null && existing.ClaimsAnnotation(doc, annot)) continue;

                    string subtype = ResolveNameValue(doc, annot[PdfName.Subtype]);
                    if (!BelongsInStructureTree(doc, annot, subtype,
                            pi < doc.Pages.Count ? doc.Pages[pi].CropBox : null)) continue;

                    int annotNumber = raw is PdfReference reference
                        ? reference.ReferenceObjectNumber
                        : ResolveObjectNumber(index, annot);

                    if (annotNumber <= 0)
                    {
                        // A direct dictionary in /Annots has no object number, so no /OBJR can
                        // name it and no revision of it can be written. Say so rather than
                        // silently producing a document the audit will report.
                        directAnnotations++;
                        continue;
                    }

                    // The same annotation listed on two pages gets one wrapper, on the first.
                    if (!seen.Add(annotNumber)) continue;

                    if (string.Equals(subtype, PdfAnnotationSubtypes.Link, StringComparison.Ordinal)
                        && !HasLinkDescription(doc, annot))
                    {
                        linksWithoutDescription++;
                    }

                    result.Add(new AnnotationElement
                    {
                        Dict              = annot,
                        Role              = AnnotationRoleFor(subtype),
                        AnnotObjectNumber = annotNumber,
                        AnnotGeneration   = index.GetGeneration(annotNumber),
                        PageObjectNumber  = pageObjectNumber,
                        PageGeneration    = index.GetGeneration(pageObjectNumber),
                        PageIndex         = pi,
                        Parent            = FindEnclosingBlock(doc, blocks, annot),
                    });
                }
            }

            if (directAnnotations > 0)
            {
                string detail = directAnnotations.ToString(CultureInfo.InvariantCulture) +
                    " annotation(s) are stored as direct dictionaries inside /Annots rather than as " +
                    "indirect objects, so no /OBJR can reference them and they could not be added to " +
                    "the structure tree. An audit will report them as unreachable (UA23); rewrite them " +
                    "as indirect objects first.";
                warnings.Add(detail);
                AccessibilityDiagnostics.Report(Operation, detail);
            }

            if (linksWithoutDescription > 0 && !linkDescriptionsFollow)
            {
                // Deliberately not invented: /Contents is the link's alternate description
                // (ISO 14289-1 §7.18.5) and only the caller knows where the link goes. Writing the
                // URI, or the visible text, would produce an announcement that is wrong rather than
                // missing — and UA22 would then pass on a document that still fails a human review.
                string detail = linksWithoutDescription.ToString(CultureInfo.InvariantCulture) +
                    " Link annotation(s) have no /Contents alternate description. They were added to " +
                    "the structure tree, but a link description cannot be derived from the document " +
                    "and must be supplied by the caller; until it is, an audit will continue to " +
                    "report UA22 for them.";
                warnings.Add(detail);
                AccessibilityDiagnostics.Report(Operation, detail);
            }

            return result;
        }

        /// <summary>
        /// Whether an annotation is one PDF/UA expects to find in the structure tree.
        /// </summary>
        /// <remarks>
        /// <see cref="Plugins.Conformance.PdfUaAnnotationScope"/>, the scope the deep checker
        /// walks too, not a copy that "must stay identical" to the checker's: if the producer and
        /// the checker disagree, the tagger either emits an
        /// <c>/OBJR</c> the checker never asks about or — far worse — skips something the checker
        /// then reports, which is remediation manufacturing a violation. One predicate cannot.
        /// </remarks>
        internal static bool BelongsInStructureTree(PdfDocument doc, PdfDictionary annot, string subtype,
            PdfArray cropBox)
            => Plugins.Conformance.PdfUaAnnotationScope.IsPresentedToReader(
                subtype, PdfAnnotationFlags.FromNumber(ResolveNumber(doc, annot[PdfName.F])),
                ResolveArray(doc, annot[PdfName.Rect]), cropBox, doc.ResolveObject);

        /// <summary>
        /// The structure type that wraps an annotation of this subtype: <c>/Link</c> for a link,
        /// <c>/Form</c> for a widget (a form field's on-page appearance, ISO 32000-1 Table 333),
        /// <c>/Annot</c> for everything else.
        /// </summary>
        private static string AnnotationRoleFor(string subtype)
        {
            if (string.Equals(subtype, PdfAnnotationSubtypes.Link,   StringComparison.Ordinal))
                return StructureTypes.Link;
            if (string.Equals(subtype, PdfAnnotationSubtypes.Widget, StringComparison.Ordinal))
                return StructureTypes.Form;
            return StructureTypes.Annot;
        }

        private static Dictionary<int, List<LogicalElement>> GroupBlocksByPage(List<LogicalElement> elements)
        {
            var byPage = new Dictionary<int, List<LogicalElement>>();
            if (elements == null) return byPage;

            for (int i = 0; i < elements.Count; i++)
            {
                LogicalElement element = elements[i];
                if (element.PageIndex < 0) continue;

                if (!byPage.TryGetValue(element.PageIndex, out List<LogicalElement> list))
                {
                    list = new List<LogicalElement>();
                    byPage[element.PageIndex] = list;
                }
                list.Add(element);
            }

            return byPage;
        }

        /// <summary>
        /// The block whose vertical band contains the centre of the annotation's <c>/Rect</c>, or
        /// null when none does — in which case the wrapper becomes a child of <c>/Document</c>.
        /// Blocks are tested in content order, so the first (i.e. earliest-read) match wins.
        /// </summary>
        private static LogicalElement FindEnclosingBlock(
            PdfDocument doc, List<LogicalElement> blocks, PdfDictionary annot)
        {
            if (blocks == null || blocks.Count == 0) return null;

            PdfArray rect = ResolveArray(doc, annot[PdfName.Rect]);
            if (rect == null || rect.Count < 4) return null;

            float? lower = ResolveNumberValue(doc, rect[1]);
            float? upper = ResolveNumberValue(doc, rect[3]);
            if (!lower.HasValue || !upper.HasValue) return null;

            // /Rect is not required to be normalised (ISO 32000-1 §12.5.2).
            float centre = (lower.Value + upper.Value) / 2f;

            for (int i = 0; i < blocks.Count; i++)
            {
                LogicalElement block = blocks[i];
                if (block.MinY > block.MaxY) continue;   // no run was measured for it
                if (centre >= block.MinY && centre <= block.MaxY) return block;
            }

            return null;
        }

        /// <summary>
        /// Registers one wrapper element per annotation and stamps the matching
        /// <c>/StructParent</c> onto the annotation itself, consuming one parent-tree key each.
        ///
        /// <para>
        /// The two edits are made together on purpose. An <c>/OBJR</c> pointing at an annotation
        /// that carries no key, or a key with no <c>/OBJR</c> behind it, is a half-link — precisely
        /// the state that makes UA14 or UA23 fire — so an annotation whose revision cannot be
        /// registered gets no wrapper and consumes no key either.
        /// </para>
        /// </summary>
        private static List<AnnotationElement> BuildAnnotationElements(
            PdfObjectIndex index,
            List<AnnotationElement> annotations,
            int documentElementNumber,
            TagPlan plan,
            List<string> warnings,
            ref int nextParentTreeKey)
        {
            var tagged = new List<AnnotationElement>(annotations.Count);
            int unwritable = 0;

            for (int i = 0; i < annotations.Count; i++)
            {
                AnnotationElement annotation = annotations[i];

                PdfDictionary revisedAnnot = GetOrClonePlanned(
                    plan, index, annotation.Dict, annotation.AnnotObjectNumber);

                if (revisedAnnot == null)
                {
                    unwritable++;
                    continue;
                }

                annotation.ParentTreeKey = nextParentTreeKey++;

                // /StructParent — singular — on the ANNOTATION, keyed to the element that owns it.
                // (/StructParents, plural, is the page-level key whose value is an array indexed by
                // MCID; putting one where the other belongs is the classic defect here.)
                revisedAnnot.Set(StructParentKey, new PdfNumber(annotation.ParentTreeKey));

                int parentNumber = annotation.Parent != null
                    ? annotation.Parent.ObjectNumber
                    : documentElementNumber;

                plan.New[annotation.ObjectNumber] = BuildAnnotationStructElement(annotation, parentNumber);

                if (annotation.Parent != null)
                    annotation.Parent.ChildElementObjectNumbers.Add(annotation.ObjectNumber);

                tagged.Add(annotation);
            }

            if (unwritable > 0)
            {
                string detail = unwritable.ToString(CultureInfo.InvariantCulture) +
                    " annotation(s) could not be revised, so no /StructParent could be written for " +
                    "them and they were left out of the structure tree rather than being referenced " +
                    "by an /OBJR they cannot answer.";
                warnings.Add(detail);
                AccessibilityDiagnostics.Report(Operation, detail);
            }

            return tagged;
        }

        /// <summary>
        /// Builds the wrapper: a structure element whose single <c>/K</c> is an <c>/OBJR</c>
        /// naming the annotation. <c>/Pg</c> is written on both the element and the <c>/OBJR</c>
        /// so the reference is self-describing (Table 325 requires it on the <c>/OBJR</c> only
        /// when it differs from the parent's, and permits it otherwise).
        /// </summary>
        private static PdfDictionary BuildAnnotationStructElement(
            AnnotationElement annotation, int parentObjectNumber)
        {
            var objr = new PdfDictionary(3);
            objr.Set(PdfName.Type, ObjrType);
            objr.Set(PdfName.Pg,   new PdfReference(annotation.PageObjectNumber, annotation.PageGeneration));
            objr.Set(PdfName.Obj,       new PdfReference(annotation.AnnotObjectNumber, annotation.AnnotGeneration));

            var dict = new PdfDictionary(5);
            dict.Set(PdfName.Type, PdfName.StructElem);
            dict.Set(PdfName.S,    new PdfName(annotation.Role));

            // Every parent here is an object this pass writes, so generation 0 is exact.
            dict.Set(PdfName.P,  new PdfReference(parentObjectNumber, 0));
            dict.Set(PdfName.Pg, new PdfReference(annotation.PageObjectNumber, annotation.PageGeneration));
            dict.Set(PdfName.K,  objr);
            return dict;
        }

        /// <summary>
        /// Whether a Link annotation already carries the alternate description PDF/UA requires.
        /// Only the annotation's own <c>/Contents</c> counts (ISO 14289-1 §7.18.5) — the same test
        /// UA22 applies, so the warning this drives cannot disagree with the audit.
        /// </summary>
        private static bool HasLinkDescription(PdfDocument doc, PdfDictionary annot)
        {
            PdfObject contents = annot[PdfName.Contents];
            if (contents is PdfReference)
            {
                try
                {
                    contents = doc.ResolveObject(contents);
                }
                catch (Exception ex)
                {
                    AccessibilityDiagnostics.Report("PdfAutoTagger.HasLinkDescription",
                        "a link's indirect /Contents could not be resolved; it is treated as absent", ex);
                    return false;
                }
            }

            return contents is PdfString text && !string.IsNullOrWhiteSpace(text.GetText());
        }

        // ================================================================
        //  Catalog / Info
        // ================================================================

        private static void ApplyCatalogEdits(
            PdfDocument doc, PdfObjectIndex index, PdfDictionary catalog, int catalogObjectNumber,
            AutoTagOptions options, TagPlan plan, int structTreeRootNumber, bool markContent)
        {
            PdfDictionary newCatalog = CloneDictionary(catalog, catalogObjectNumber, index.GetGeneration(catalogObjectNumber));

            if (structTreeRootNumber > 0)
                newCatalog.Set(PdfName.StructTreeRoot, new PdfReference(structTreeRootNumber, index.GetGeneration(structTreeRootNumber)));

            // /MarkInfo /Marked asserts that the page content carries marked content. Setting it
            // when nothing was marked is a lie that PDF/UA checkers key off, so it is written
            // only when a page really was re-marked.
            if (markContent)
                SetFlagInSubDictionary(doc, index, catalog, newCatalog, PdfName.MarkInfo, PdfName.Marked, plan);

            if (catalog[PdfName.Lang] == null && !string.IsNullOrEmpty(options.Language))
                newCatalog.Set(PdfName.Lang, MakeTextString(options.Language));

            SetFlagInSubDictionary(doc, index, catalog, newCatalog,
                                   PdfName.ViewerPreferences, PdfName.DisplayDocTitle, plan);

            plan.Modified[catalogObjectNumber] = newCatalog;
        }

        /// <summary>
        /// Sets <paramref name="flagKey"/> to <c>true</c> inside the catalog's
        /// <paramref name="subKey"/> dictionary. When that dictionary is an indirect object it is
        /// revised in place (keeping the reference), otherwise a direct copy carrying the flag is
        /// written into the new catalog — either way nothing else in it is lost.
        /// </summary>
        private static void SetFlagInSubDictionary(
            PdfDocument doc, PdfObjectIndex index, PdfDictionary catalog, PdfDictionary newCatalog,
            PdfName subKey, PdfName flagKey, TagPlan plan)
        {
            PdfObject raw = catalog[subKey];
            PdfDictionary resolved = ResolveDictionary(doc, raw);

            if (raw is PdfReference reference && resolved != null && reference.ReferenceObjectNumber > 0)
            {
                int number = reference.ReferenceObjectNumber;
                PdfDictionary clone = CloneDictionary(resolved, number, index.GetGeneration(number));
                clone.Set(flagKey, PdfBoolean.True);
                plan.Modified[number] = clone;
                newCatalog.Set(subKey, new PdfReference(number, reference.ReferenceGenerationNumber));
                return;
            }

            PdfDictionary direct = resolved != null ? CloneDictionary(resolved, 0, 0) : new PdfDictionary(2);
            direct.Set(flagKey, PdfBoolean.True);
            newCatalog.Set(subKey, direct);
        }

        private static void ApplyTitle(
            PdfDocument doc, PdfObjectIndex index, PdfDictionary catalog, int catalogObjectNumber,
            AutoTagOptions options, TagPlan plan)
        {
            if (options.Title == null) return;

            ApplyInfoTitle(doc, index, options, plan);

            // ISO 14289-1 7.1 reads the title from the XMP dc:title alone; a new /Metadata stream is
            // referenced from the catalog ApplyCatalogEdits put in the plan.
            Metadata.DocumentTitleXmp.Stage(doc, index, catalog,
                () => plan.Modified.TryGetValue(catalogObjectNumber, out PdfObject revised) ? revised as PdfDictionary : null,
                options.Title, plan.Modified, plan.New, "PdfAutoTagger.ApplyTitle");
        }

        private static void ApplyInfoTitle(PdfDocument doc, PdfObjectIndex index, AutoTagOptions options, TagPlan plan)
        {
            PdfObject infoObj = null;
            try
            {
                infoObj = doc.Trailer?[PdfName.Info];
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report("PdfAutoTagger.ApplyTitle",
                    "the trailer's /Info entry could not be read; a fresh /Info dictionary is written", ex);
            }

            PdfDictionary info = ResolveDictionary(doc, infoObj);
            int infoObjectNumber = info != null ? ResolveObjectNumber(index, info) : -1;

            if (info != null && infoObjectNumber > 0)
            {
                PdfDictionary clone = CloneDictionary(info, infoObjectNumber, index.GetGeneration(infoObjectNumber));
                clone.Set(PdfName.Title, MakeTextString(options.Title));
                plan.Modified[infoObjectNumber] = clone;
                return;
            }

            // No /Info at all (or one we cannot name): write a new one and tell the incremental
            // writer to point the appended trailer at it, so /Info /Title is set on a document
            // without an /Info.
            PdfDictionary fresh = info != null ? CloneDictionary(info, 0, 0) : new PdfDictionary(2);
            fresh.Set(PdfName.Title, MakeTextString(options.Title));

            int number = index.AllocateObjectNumber();
            fresh.ObjectNumber = number;
            plan.New[number] = fresh;
            plan.NewInfoObjectNumber = number;
        }

        // ================================================================
        //  Page tagging options
        // ================================================================

        private static PageTagOptions BuildPageTagOptions(
            PdfDocument doc, PdfPage page, AutoTagOptions options, BodyText medianFontSize,
            int pageIndex, List<string> warnings, TagPlan plan)
        {
            PageTagOptions pageOptions = NewPageTagOptions(
                doc, page, options, medianFontSize,
                options.TagHeadersFootersAsArtifacts
                && HeaderFooterBandsAreTrustworthy(page, pageIndex, warnings));

            // §7.7.3.4 makes /Resources inheritable, so this must be PdfPage.Resources and not
            // page.Dictionary[/Resources]: a page that inherits its resources from the /Pages node
            // carries no key of its own, and reading the dictionary directly finds nothing and
            // offers no images at all.
            if (plan != null)
                AttachFigureSeam(doc, page?.Resources, options, pageIndex, pageOptions, plan);
            return pageOptions;
        }

        /// <summary>
        /// Gives the page's tagging options the two things figure inference needs: which XObject
        /// names are images, and a way to ask the caller for each one's alternate text.
        /// </summary>
        /// <remarks>
        /// <para><b>The resource read happens here because the tagger has no resources.</b> It is
        /// handed raw content bytes, so a <c>/Im0 Do</c> tells it nothing about whether <c>/Im0</c>
        /// is an image or a Form XObject — and wrapping a form as a <c>/Figure</c> would declare a
        /// page of text to be a picture. Only the caller holds the document.</para>
        ///
        /// <para><b>The declined count is kept here rather than in the tagger</b>, because the
        /// tagger's contract is "null means do not tag" and it has no opinion about why. Counting
        /// on this side keeps that clean and gives the caller a number that spans every page.</para>
        ///
        /// <para><b>Nothing is attached when the caller supplied no callback</b>, so a page with a
        /// hundred images costs one null check rather than a resource walk.</para>
        /// </remarks>
        private static void AttachFigureSeam(
            PdfDocument doc, PdfDictionary resources, AutoTagOptions options, int pageIndex,
            PageTagOptions pageOptions, TagPlan plan)
        {
            if (options.FigureAlternateText == null) return;
            AttachFigureSeam(plan.Images.Of(doc, resources), options, pageIndex, new[] { pageIndex }, pageOptions, plan);
        }

        /// <summary>
        /// Gives <paramref name="pageOptions"/> the figure seam over <paramref name="images"/>: the
        /// catalogue's own name set, and a candidate made for a name only when the stream draws it.
        /// </summary>
        private static void AttachFigureSeam(
            ImageCatalog images, AutoTagOptions options, int pageIndex, IReadOnlyList<int> drawnOn,
            PageTagOptions pageOptions, TagPlan plan)
        {
            // Inline images are offered as figures too (the owner's decision), through
            // the same callback, a candidate naming the image by its place among the stream's.
            pageOptions.InlineImageAlternateText = (index, parameters) =>
                AskForFigure(options, new FigureCandidate(pageIndex,
                        pageIndex >= 0 ? new[] { pageIndex } : new int[0], index,
                        InlineDimension(parameters, PdfName.Width, "W"), InlineDimension(parameters, PdfName.Height, "H")),
                    "the inline image #" + index.ToString(CultureInfo.InvariantCulture), pageIndex, plan);

            if (images.Names.Count == 0) return;

            pageOptions.ImageXObjectNames = images.Names;
            pageOptions.FigureAlternateText = name =>
            {
                if (!images.TryGetSize(name, out int width, out int height)) return null;
                return AskForFigure(options, new FigureCandidate(pageIndex, drawnOn, name, width, height),
                                    "/" + name, pageIndex, plan);
            };
        }

        /// <summary>
        /// The caller's alternate text for one figure candidate, or <c>null</c>: a declined one is
        /// counted, and a callback that throws costs that image its <c>/Figure</c> and nothing more.
        /// </summary>
        private static string AskForFigure(AutoTagOptions options, FigureCandidate candidate, string what, int pageIndex, TagPlan plan)
        {
            string alt;
            try
            {
                alt = options.FigureAlternateText(candidate);
            }
            catch (Exception ex)
            {
                // A caller's callback may reach a network, a model or a database. One that
                // throws must cost that image its /Figure, not the whole document its tagging —
                // the page is mid-splice and abandoning it would lose every paragraph too.
                AccessibilityDiagnostics.Report(
                    "PdfAutoTagger.FigureAlternateText",
                    "the caller's alternate-text callback threw for " + what +
                    (pageIndex >= 0
                         ? " on page " + (pageIndex + 1).ToString(CultureInfo.InvariantCulture)
                         : " in a Form XObject shared by several pages") +
                    "; the image was left untagged", ex);
                alt = null;
            }

            if (string.IsNullOrEmpty(alt)) { plan.FiguresDeclined++; return null; }
            return alt;
        }

        /// <summary>An inline image's <c>/Width</c> or <c>/Height</c> under its full or abbreviated key (§8.9.7, Table 92), or 0.</summary>
        private static int InlineDimension(PdfDictionary parameters, PdfName key, string abbreviation)
        {
            PdfNumber number = (parameters?.Get(key) ?? parameters?.Get(new PdfName(abbreviation))) as PdfNumber;
            if (number == null) return 0;
            double value = number.DoubleValue;
            return double.IsNaN(value) || value <= 0d || value > int.MaxValue ? 0 : SaturatingCast.ToInt(value);
        }

        /// <summary>
        /// The image XObjects of one <c>/XObject</c> dictionary by resource name, with the pixel
        /// dimensions a caller may key a decision on.
        /// </summary>
        /// <remarks>
        /// <para><b>Only <c>/Subtype /Image</c>.</b> A Form XObject is a container of more content, and
        /// this pass tags what is inside a page's own stream rather than what a form draws — so a
        /// form named here would produce a <c>/Figure</c> around content that may be entirely text.
        /// A missing or unreadable <c>/Subtype</c> is not an image: the fail-safe direction is to
        /// leave content untagged rather than to describe it wrongly.</para>
        /// <para><b>Read once a run</b> (<see cref="ImageCatalogs"/>) and shared read-only by every
        /// page and form that reaches the dictionary: each page read every image of the resources it
        /// shares, and a document of pages × images was that many resolves — twenty thousand of each
        /// from a few megabytes. A page's candidate is made only for a name its content draws.</para>
        /// </remarks>
        internal sealed class ImageCatalog
        {
            internal static readonly ImageCatalog Empty = new ImageCatalog(new Dictionary<string, (int, int)>(StringComparer.Ordinal));

            private readonly Dictionary<string, (int Width, int Height)> _sizes;

            private ImageCatalog(Dictionary<string, (int Width, int Height)> sizes)
            {
                _sizes = sizes;
                Names = new HashSet<string>(sizes.Keys, StringComparer.Ordinal);
            }

            /// <summary>The image names, for the tagger's <see cref="PageTagOptions.ImageXObjectNames"/>; not to be changed.</summary>
            internal HashSet<string> Names { get; }

            internal bool TryGetSize(string name, out int width, out int height)
            {
                bool found = _sizes.TryGetValue(name, out (int Width, int Height) size);
                width = size.Width; height = size.Height;
                return found;
            }

            /// <summary>The catalogue of these images that <paramref name="names"/> keeps.</summary>
            internal ImageCatalog Restricted(HashSet<string> names)
            {
                var sizes = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
                foreach (string name in names)
                    if (_sizes.TryGetValue(name, out (int, int) size)) sizes[name] = size;
                return new ImageCatalog(sizes);
            }

            internal static ImageCatalog Read(PdfDocument doc, PdfDictionary xobjects)
            {
                var sizes = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
                foreach (PdfName key in xobjects.Keys)
                {
                    PdfDictionary xobject = Resolve.Dict(
                        doc, xobjects.Get(key), "PdfAutoTagger.ReadImageXObjects");
                    if (xobject == null) continue;

                    PdfName subtype = Resolve.Name(
                        doc, xobject.Get(PdfName.Subtype), "PdfAutoTagger.ReadImageXObjects");
                    if (subtype == null || !string.Equals(subtype.Value, "Image", StringComparison.Ordinal))
                        continue;

                    sizes[key.Value] = (ReadPixelCount(doc, xobject, PdfName.Width),
                                        ReadPixelCount(doc, xobject, PdfName.Height));
                }
                return new ImageCatalog(sizes);
            }
        }

        /// <summary>One run's <see cref="ImageCatalog"/>s, by <c>/XObject</c> dictionary identity.</summary>
        internal sealed class ImageCatalogs
        {
            private readonly Dictionary<object, ImageCatalog> _byDictionary = new Dictionary<object, ImageCatalog>(PdfReferenceEquality.Instance);

            /// <summary>How many dictionaries were read; every other ask was answered from the cache.</summary>
            internal int Read { get; private set; }

            /// <summary>Entries a shared form's pages were compared on: none for pages reaching one dictionary.</summary>
            internal long EntriesCompared { get; set; }

            /// <summary>The <c>/XObject</c> dictionary of <paramref name="resources"/>, or null.</summary>
            internal static PdfDictionary XObjectsOf(PdfDocument doc, PdfDictionary resources)
                => resources == null ? null : Resolve.Dict(doc, resources.Get(PdfName.XObject), "PdfAutoTagger.ReadImageXObjects");

            internal ImageCatalog Of(PdfDocument doc, PdfDictionary resources) => OfXObjects(doc, XObjectsOf(doc, resources));

            internal ImageCatalog OfXObjects(PdfDocument doc, PdfDictionary xobjects)
            {
                if (xobjects == null) return ImageCatalog.Empty;
                if (_byDictionary.TryGetValue(xobjects, out ImageCatalog known)) return known;
                ImageCatalog read = ImageCatalog.Read(doc, xobjects);
                Read++;
                _byDictionary[xobjects] = read;
                return read;
            }
        }

        /// <summary>One of an image's sample dimensions, or 0 when it cannot be read.</summary>
        private static int ReadPixelCount(PdfDocument doc, PdfDictionary image, PdfName key)
        {
            PdfNumber number = Resolve.Number(doc, image.Get(key), "PdfAutoTagger.ReadImageXObjects");
            if (number == null) return 0;

            double value = number.DoubleValue;
            if (double.IsNaN(value) || value <= 0d || value > int.MaxValue) return 0;
            return SaturatingCast.ToInt(value);
        }

        /// <summary>
        /// Builds the per-page options with the edge classification decided by the caller. The
        /// measuring pass (<see cref="ComputeMedianFontSize"/>) uses this directly with
        /// <paramref name="markEdges"/> false: it marks nothing, so consulting <c>/Rotate</c> there
        /// would only emit the same diagnostic twice per rotated page.
        ///
        /// <para>
        /// Both vertical edges come from
        /// <see cref="ContentStreamTagger.TryGetPageVerticalExtent"/>, which prefers <c>/CropBox</c>
        /// and sorts the corners. Supplying only the upper edge — and thereby leaving the lower one
        /// at the implicit 0 — mislocates one of the two bands on any page whose box does not start
        /// at the origin: for a box such as <c>[0 -100 612 692]</c> the footer band grows from
        /// 50 pt to 150 pt and swallows body text, which is then emitted as <c>/Artifact</c> and
        /// disappears from the structure tree. When the extent cannot be established both values
        /// stay 0, which the classifier reads as "geometry unknown" and disables edge classification
        /// altogether — the safe direction, since over-tagging a footer as a paragraph is
        /// recoverable and losing body text is not.
        /// </para>
        /// </summary>
        private static PageTagOptions NewPageTagOptions(
            PdfDocument doc, PdfPage page, AutoTagOptions options, BodyText medianFontSize, bool markEdges)
        {
            float top, bottom;
            if (!ContentStreamTagger.TryGetPageVerticalExtent(doc, page, out top, out bottom))
            {
                top    = 0f;
                bottom = 0f;
            }

            return new PageTagOptions
            {
                PageHeight                    = top,
                PageBottom                    = bottom,
                HeaderMargin                  = options.HeaderFooterMargin,
                FooterMargin                  = options.HeaderFooterMargin,
                MarkHeadersFootersAsArtifacts = markEdges,
                MedianFontSize                = medianFontSize.Median,
                HeadingSizes                  = medianFontSize.HeadingSizes,
                H1Ratio                       = options.H1Ratio,
                H2Ratio                       = options.H2Ratio,
                H3Ratio                       = options.H3Ratio,
                ParagraphGapThreshold         = options.ParagraphGapThreshold,
                InferLists                    = options.InferLists,
                InferTables                   = options.InferTables,
                MarkPathsAsArtifacts          = true,
                FirstMcid                     = 0,   // MCIDs are per-page and start at 0

                // Both memory ceilings come from the caller's AutoTagOptions rather than from the
                // PageTagOptions defaults, so a host that legitimately processes very large pages
                // can raise them and one taking untrusted upload can lower them. The AutoTagOptions
                // defaults are the same numbers, so an unconfigured caller is unaffected.
                MaxContentBytes               = options.MaxPageContentBytes,
                MaxConcatenatedContentBytes   = options.MaxConcatenatedContentBytes,
            };
        }

        /// <summary>
        /// Whether the header/footer bands — which are horizontal strips of <em>user space</em> —
        /// actually correspond to the visual top and bottom of the page.
        ///
        /// <para>
        /// They do not when the page is displayed rotated by 90° or 270°: ISO 32000-1 §7.7.3.3
        /// <c>/Rotate</c> turns the user-space left and right edges into the visual top and bottom,
        /// so a Y-band test classifies text running down the *side* of the page as a running header.
        /// That text is then emitted as <c>/Artifact</c>, which removes it from the structure tree
        /// entirely — the one failure mode that hides real content from assistive technology, and
        /// strictly worse than not marking an artifact at all.
        /// </para>
        ///
        /// <para>
        /// The page is still tagged; only the edge classification is switched off, so headings and
        /// paragraphs are unaffected and a genuine header merely becomes a <c>&lt;P&gt;</c>.
        /// Over-tagging is recoverable; losing content is not. <c>/Rotate 180</c> keeps its bands:
        /// it maps top to bottom, and this type applies a single
        /// <see cref="AutoTagOptions.HeaderFooterMargin"/> to both, so the band geometry is
        /// symmetric and only the <c>/Subtype</c> label would differ — both are
        /// <c>/Artifact /Pagination</c> and are excluded from the structure tree identically.
        /// </para>
        ///
        /// <para>
        /// This mirrors the guard in <c>PdfArtifactMarker</c>, which refuses such a page outright.
        /// </para>
        /// </summary>
        private static bool HeaderFooterBandsAreTrustworthy(
            PdfPage page, int pageIndex, List<string> warnings)
        {
            if (page == null) return false;

            int rotation;
            try
            {
                rotation = page.Rotate ?? 0;
            }
            catch (Exception ex)
            {
                WarnPage(warnings, pageIndex,
                    "/Rotate could not be read, so the visual top of the page is unknown; "
                    + "header/footer artifact detection was disabled for it and its edge text was "
                    + "tagged as ordinary content", ex);
                return false;
            }

            rotation = PdfPageRotation.Reduce(rotation);

            if (PdfPageRotation.IsMalformed(rotation))
            {
                WarnPage(warnings, pageIndex,
                    "/Rotate " + rotation.ToString(CultureInfo.InvariantCulture)
                    + " is not a multiple of 90, which is malformed; header/footer artifact "
                    + "detection was disabled for it and its edge text was tagged as ordinary content");
                return false;
            }

            if (PdfPageRotation.SwapsAxes(rotation))
            {
                WarnPage(warnings, pageIndex,
                    "the page is rotated " + rotation.ToString(CultureInfo.InvariantCulture)
                    + "°, so its visual top and bottom edges are not the user-space ones; "
                    + "header/footer artifact detection was disabled for it and its edge text was "
                    + "tagged as ordinary content rather than being hidden from assistive technology");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Records a per-page anomaly in both places it belongs: the assembly-wide diagnostics sink
        /// and the result's warning list.
        /// </summary>
        private static void WarnPage(List<string> warnings, int pageIndex, string detail, Exception ex = null)
        {
            string message = "page " + (pageIndex + 1).ToString(CultureInfo.InvariantCulture)
                             + ": " + detail;
            AccessibilityDiagnostics.Report(Operation, message, ex);
            warnings?.Add(message);
        }

        /// <summary>
        /// Estimates the document's body font size — the denominator for every heading ratio.
        ///
        /// <para>
        /// The sizes come straight from the page content streams, measured by the very same
        /// scanner that will later classify the runs
        /// (<see cref="MeasureFontSizesFromContentStreams"/>). That is deliberate on two counts:
        /// it is linear in the number of content-stream bytes, and it yields exactly the
        /// <see cref="RunCandidate.FontSize"/> values the heading ratios are compared against, so
        /// the denominator and the numerator can never be measured on different bases.
        /// </para>
        ///
        /// <para>
        /// <b>Why <see cref="PdfOperations.ExtractStructuredText(string)"/> is not used here.</b>
        /// As the primary source it would run a full, structured text extraction of the <em>whole
        /// document</em> on every AutoTag call purely to produce one number. That extraction groups
        /// glyphs into lines and is superlinear in the amount of text sharing a baseline band, with
        /// no page cap, no character cap and no timeout: a 119 KB single-page file built from a
        /// thousand text-showing operators on two alternating baselines takes over three minutes,
        /// a 383 KB one over nine — a fallback for when it throws never triggers, because it does
        /// not fail, it takes forever, and <c>AutoTag</c> (and therefore
        /// <c>PdfAccessibility.RemediateWithReport</c>) would hang on a small attacker-supplied
        /// file. Nothing is lost without it: the extractor's per-line <c>Height</c> and the
        /// scanner's effective font size measure the same quantity, and the extractor's one extra
        /// reach — text inside Form XObjects — is content this pass does not tag anyway, so it
        /// could only ever skew the median away from the runs being classified.
        /// </para>
        /// </summary>
        private static BodyText ComputeMedianFontSize(
            PdfDocument doc, IReadOnlyList<PdfPage> pages, AutoTagOptions options)
        {
            var sizes = new List<float>();
            // The ranking counts only lines the classifier can make headings: a running header in a
            // large face is tagged an artifact, and ranking its size would leave H1 unused.
            var bodySizes = options.HeadingLevelsBySize ? new List<float>() : null;
            MeasureFontSizesFromContentStreams(doc, pages, options, sizes, bodySizes);

            if (sizes.Count == 0)
            {
                AccessibilityDiagnostics.Report("PdfAutoTagger.ComputeMedianFontSize",
                    "no font size could be measured; " +
                    DefaultMedianFontSize.ToString(CultureInfo.InvariantCulture) + " pt is assumed");
                return new BodyText(DefaultMedianFontSize, null);
            }

            sizes.Sort();
            float median = sizes[sizes.Count / 2];
            if (!(median > 0f)) median = DefaultMedianFontSize;
            return new BodyText(median, bodySizes != null ? RankHeadingSizes(bodySizes, median, options.H3Ratio) : null);
        }

        /// <summary>
        /// The document's heading sizes, largest first: every distinct size — rounded to half a point,
        /// so 13.98 and 14.02 are one size — at or above <paramref name="h3Ratio"/> × the median. The
        /// classifier gives the first H1, the second H2, and so down to H6, which every smaller size
        /// shares; so a document with five heading sizes gets H1–H5 and skips no level.
        /// </summary>
        internal static float[] RankHeadingSizes(List<float> sizes, float median, float h3Ratio)
        {
            var distinct = new SortedSet<float>();
            foreach (float size in sizes)
            {
                float rounded = HeadingSizeKey(size);
                if (rounded >= h3Ratio * median) distinct.Add(rounded);
            }
            var ranked = new List<float>(distinct);
            ranked.Reverse();
            return ranked.ToArray();
        }

        /// <summary>
        /// Whether a measured line falls in the header or footer band the classifier tags as artifacts
        /// (<see cref="AutoTagOptions.HeaderFooterMargin"/> from the page's real edges, when the bands are
        /// on and do not meet) — the same test, so the ranking and the tagging agree on what is content.
        /// </summary>
        private static bool InEdgeBand(RunCandidate candidate, AutoTagOptions options)
        {
            if (!options.TagHeadersFootersAsArtifacts) return false;
            float top = candidate.PageHeight, bottom = candidate.PageBottom, margin = options.HeaderFooterMargin;
            if (float.IsNaN(top) || float.IsInfinity(top) || float.IsNaN(bottom) || float.IsInfinity(bottom)
                || float.IsNaN(candidate.Y) || float.IsInfinity(candidate.Y) || !(margin > 0f)) return false;
            float height = top - bottom;
            if (!(height > 0f) || 2 * margin >= height) return false;
            return top - candidate.Y <= margin || candidate.Y - bottom <= margin;
        }

        /// <summary>A size rounded to half a point — the unit heading sizes are told apart in.</summary>
        internal static float HeadingSizeKey(float size) => (float)(Math.Round(size * 2, MidpointRounding.AwayFromZero) / 2);

        /// <summary>The document's body-text measurement: the median size and, when ranked, its heading sizes largest first.</summary>
        private readonly struct BodyText
        {
            internal BodyText(float median, float[] headingSizes) { Median = median; HeadingSizes = headingSizes; }
            internal float Median { get; }
            internal float[] HeadingSizes { get; }
        }

        /// <summary>
        /// Runs the content-stream tagger over every page with a classifier that marks nothing,
        /// purely to observe the effective font size of each line. Nothing is spliced and no page
        /// is modified — <see cref="RunClassification.Unmarked"/> makes the tagger produce zero
        /// runs, so it returns early with the original bytes.
        /// </summary>
        private static void MeasureFontSizesFromContentStreams(
            PdfDocument doc, IReadOnlyList<PdfPage> pages, AutoTagOptions options, List<float> sizes,
            List<float> bodySizes = null)
        {
            if (pages == null) return;

            Func<RunCandidate, RunClassification> measure = candidate =>
            {
                // Bounded, because the sample is the one allocation in AutoTag proportional to
                // (pages × lines) rather than to one page: each page's contribution is capped by
                // MaxPageContentBytes, but nothing capped their sum. A thousand pages of
                // `BT /F1 1 Tf 0 -1 Td (a) Tj ET` repeated to just under the 4 MiB page ceiling
                // reached ~250 million floats — about a gigabyte on the large object heap — and then
                // sorted it, all to produce one median. A few hundred thousand samples give the same
                // denominator on any real document, and on a hostile one they give a median of the
                // part that was measured rather than an out-of-memory exception.
                if (candidate != null && candidate.FontSize > 0f && sizes.Count < MaxMedianFontSizeSamples)
                    sizes.Add(candidate.FontSize);
                if (bodySizes != null && candidate != null && candidate.FontSize > 0f
                    && bodySizes.Count < MaxMedianFontSizeSamples && !InEdgeBand(candidate, options))
                    bodySizes.Add(candidate.FontSize);
                return RunClassification.Unmarked;
            };

            for (int pi = 0; pi < pages.Count; pi++)
            {
                PdfPage page = null;
                try
                {
                    page = pages[pi];
                }
                catch (Exception ex)
                {
                    AccessibilityDiagnostics.Report("PdfAutoTagger.MeasureFontSizesFromContentStreams",
                        "page " + pi.ToString(CultureInfo.InvariantCulture) +
                        " could not be read while measuring font sizes", ex);
                    continue;
                }

                PdfDictionary pageDict = page?.Dictionary;
                if (pageDict == null) continue;

                // The measuring pass decodes the same pages the tagging pass will, so it has to
                // honour the same concatenation ceiling — otherwise the caller's limit would be
                // bypassed by the pass that runs first.
                byte[] content = ContentStreamTagger.ConcatenatePageContent(
                    doc, pageDict, options.MaxConcatenatedContentBytes);
                if (content == null || content.Length == 0) continue;

                // markEdges: false — measure every line, edges included.
                PageTagOptions probe = NewPageTagOptions(
                    doc, page, options, new BodyText(DefaultMedianFontSize, null), markEdges: false);
                probe.Classifier = measure;
                ContentStreamTagger.TagPage(content, probe);
            }
        }

        // ================================================================
        //  Small helpers
        // ================================================================

        private static AutoTagResult BuildFailureResult(
            int pagesProcessed, IReadOnlyList<string> warnings, IReadOnlyList<string> skipReasons)
            => new AutoTagResult(0, pagesProcessed, new Dictionary<int, int>(), 0, 0, warnings,
                                 mcidCount: 0, pagesSkipped: pagesProcessed,
                                 skippedPageReasons: skipReasons, contentMarked: false);

        private static string DescribeSkip(int pageIndex, string reason)
            => "Page " + (pageIndex + 1).ToString(CultureInfo.InvariantCulture) + ": " + reason;

        /// <summary>
        /// Returns a structure type that can be written as a PDF name. The tagger already refuses
        /// a page whose classifier produced an unwritable role, so this only guards against a null
        /// or empty value reaching the element builder.
        /// </summary>
        private static string NormalizeRole(string role)
            => string.IsNullOrEmpty(role) ? StructureTypes.Paragraph : role;

        // One implementation, in Internal.Resolve — these four were the fullest of the eight copies
        // and the survivor is modelled on them, so this is a move rather than a change.
        private static PdfDictionary ResolveDictionary(PdfDocument doc, PdfObject obj)
            => Internal.Resolve.Dict(doc, obj, "PdfAutoTagger.ResolveDictionary");

        private static PdfArray ResolveArray(PdfDocument doc, PdfObject obj)
            => Internal.Resolve.Array(doc, obj, "PdfAutoTagger.ResolveArray");

        /// <summary>
        /// Resolves an object that should be a name and returns its value, or null. Annotation
        /// entries are routinely indirect, so the direct-only <c>PdfDictionary.GetName</c> would
        /// silently report "no /Subtype" for a perfectly ordinary file.
        /// </summary>
        private static string ResolveNameValue(PdfDocument doc, PdfObject obj)
            => Internal.Resolve.Name(doc, obj, "PdfAutoTagger.ResolveNameValue")?.Value;

        private static float? ResolveNumberValue(PdfDocument doc, PdfObject obj)
        {
            PdfNumber number = ResolveNumber(doc, obj);
            return number != null ? (float)number.DoubleValue : (float?)null;
        }

        private static PdfNumber ResolveNumber(PdfDocument doc, PdfObject obj)
            => Internal.Resolve.Number(doc, obj, "PdfAutoTagger.ResolveNumber");

        // One implementation, Internal.StagedObject, which clones through
        // PdfDictionary.CloneShallow.
        private static PdfDictionary CloneDictionary(PdfDictionary source, int objectNumber, int generation)
            => Internal.StagedObject.CloneWithIdentity(source, objectNumber, generation);

        private static int ResolveObjectNumber(PdfObjectIndex index, PdfDictionary dict)
            => Internal.StagedObject.ObjectNumberOf(index, dict);

        /// <summary>The annotation-level counterpart of <c>/StructParents</c> (ISO 32000-1 Table 328).</summary>
        private static readonly PdfName StructParentKey = PdfName.StructParent;

        /// <summary>
        /// Removes every <c>/StructParent</c> and <c>/StructParents</c> key that the freshly
        /// generated parent tree does not define, so the superseded tree leaves no dangling
        /// reference behind.
        ///
        /// <para>
        /// Two classes of key can go stale after a re-tag: a page this pass could not re-mark keeps
        /// the <c>/StructParents</c> value of the old tree, and an annotation the generated tree
        /// does not reference keeps its <c>/StructParent</c>. Both are removed. Dropping the key
        /// makes the object honestly untagged (which the audit will then report); leaving it makes
        /// the file invalid, and invalid is worse than untagged.
        /// </para>
        ///
        /// <para>
        /// <b>An annotation this pass is about to give a fresh key is left alone.</b> The generated
        /// tree contains <c>/OBJR</c>s, so stripping <em>every</em> annotation's <c>/StructParent</c>
        /// would remove live references.
        /// <paramref name="annotations"/> names the annotations
        /// <see cref="BuildAnnotationElements"/> will re-key, so only genuinely dangling references
        /// are removed and the reported counts stay true.
        /// </para>
        ///
        /// <para>
        /// <b>Form XObjects are the third class.</b> A Form XObject is the one object class this
        /// tagger itself writes <c>/StructParents</c> onto, so a walk covering pages and their
        /// <c>/Annots</c> only would let a form carrying a key from the superseded tree, which
        /// <see cref="TagForms"/> then declined (it already holds marked content, its stream would
        /// not decode, it is shared and duplication was refused …), keep a value the freshly
        /// generated <c>/ParentTree</c> never defines. That is the same invalid state this method
        /// exists to prevent, produced from conformant input:
        /// ISO 32000-1 §14.7.4.4 requires every <c>/StructParent(s)</c> value to be a key in the
        /// ParentTree, so independent checkers report it against the output.
        /// <paramref name="inventoriedForms"/> is every form <see cref="TagForms"/> saw, and
        /// <paramref name="taggedStreams"/> names the ones it re-emitted; the difference is exactly
        /// the set to strip.
        /// </para>
        /// </summary>
        private static void PurgeStaleStructureLinks(
            PdfDocument doc,
            PdfObjectIndex index,
            IReadOnlyList<PdfPage> pages,
            int pageCount,
            List<TaggedContentStream> taggedStreams,
            List<AnnotationElement> annotations,
            IReadOnlyList<FormUsage> inventoriedForms,
            TagPlan plan,
            List<string> warnings)
        {
            var reMarked = new HashSet<int>();

            // A form copy's stream object number, which is how a re-emitted form is told from a
            // declined one. The first copy reuses the original's number, so a tagged form is always
            // in here under the number its /StructParents would otherwise dangle from.
            var reEmittedStreams = new HashSet<int>();

            for (int i = 0; i < taggedStreams.Count; i++)
            {
                if (taggedStreams[i].PageObjectNumber > 0) reMarked.Add(taggedStreams[i].PageObjectNumber);
                if (taggedStreams[i].StreamObjectNumber > 0)
                    reEmittedStreams.Add(taggedStreams[i].StreamObjectNumber);
            }

            var reKeyed = new HashSet<int>();
            if (annotations != null)
                for (int i = 0; i < annotations.Count; i++)
                    if (annotations[i].AnnotObjectNumber > 0) reKeyed.Add(annotations[i].AnnotObjectNumber);

            int pagesCleared = 0;
            int annotationsCleared = 0;
            int formsCleared = ClearStaleFormStructParents(inventoriedForms, reEmittedStreams, plan, index);

            for (int pi = 0; pi < pageCount; pi++)
            {
                PdfDictionary pageDict;
                try
                {
                    pageDict = pages[pi]?.Dictionary;
                }
                catch (Exception ex)
                {
                    AccessibilityDiagnostics.Report(Operation,
                        "page " + pi.ToString(CultureInfo.InvariantCulture) +
                        " could not be read while clearing superseded structure links", ex);
                    continue;
                }
                if (pageDict == null) continue;

                int pageObjectNumber = ResolveObjectNumber(index, pageDict);

                // A page that was NOT re-marked has no key in the new parent tree.
                if (pageObjectNumber > 0
                    && !reMarked.Contains(pageObjectNumber)
                    && pageDict[PageContentRewriter.StructParents] != null)
                {
                    PdfDictionary revised = GetOrClonePlanned(plan, index, pageDict, pageObjectNumber);
                    if (revised != null)
                    {
                        revised.Remove(PageContentRewriter.StructParents);
                        pagesCleared++;
                    }
                }

                PdfArray annots = ResolveArray(doc, pageDict[PdfName.Annots]);
                if (annots == null) continue;

                for (int ai = 0; ai < annots.Count; ai++)
                {
                    if (annots[ai] is not PdfReference annotRef) continue;   // direct annots cannot be revised
                    PdfDictionary annot = ResolveDictionary(doc, annotRef);
                    if (annot?[StructParentKey] == null) continue;

                    int annotNumber = annotRef.ReferenceObjectNumber;
                    if (annotNumber <= 0) continue;

                    // This one is being re-keyed into the generated tree, so its /StructParent is
                    // about to be valid rather than dangling.
                    if (reKeyed.Contains(annotNumber)) continue;

                    PdfDictionary revisedAnnot = GetOrClonePlanned(plan, index, annot, annotNumber);
                    if (revisedAnnot == null) continue;

                    revisedAnnot.Remove(StructParentKey);
                    annotationsCleared++;
                }
            }

            if (pagesCleared == 0 && annotationsCleared == 0 && formsCleared == 0) return;

            string detail = "Superseding the existing structure tree left " +
                pagesCleared.ToString(CultureInfo.InvariantCulture) + " page /StructParents key(s), " +
                formsCleared.ToString(CultureInfo.InvariantCulture) +
                " Form XObject /StructParents key(s) and " +
                annotationsCleared.ToString(CultureInfo.InvariantCulture) +
                " annotation /StructParent key(s) pointing at elements that no longer exist; they were " +
                "removed so the output stays valid. Those objects are now untagged and an audit will " +
                "report them as such.";
            warnings.Add(detail);
            AccessibilityDiagnostics.Report(Operation, detail);
        }

        /// <summary>
        /// Strips <c>/StructParents</c> from every inventoried Form XObject that this pass did
        /// <b>not</b> re-emit, and returns how many were cleared.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The clone is a <see cref="PdfStream"/>, not a <see cref="PdfDictionary"/>, and that is
        /// load-bearing: <c>PdfStream</c> derives from <c>PdfDictionary</c>, so
        /// <see cref="CloneDictionary"/> would happily accept one and emit a dictionary with the
        /// form's content silently gone — the page would then draw nothing. The payload is shared
        /// by reference and never mutated; only the key is removed.
        /// </para>
        /// <para>
        /// A form already present in <see cref="TagPlan.Modified"/> is left alone. That can only be
        /// a form some other pass has already rewritten, and the only such pass is the duplicator,
        /// whose output is re-emitted by definition — so reaching it here would mean stripping the
        /// key from a stream that legitimately just received one.
        /// </para>
        /// </remarks>
        private static int ClearStaleFormStructParents(
            IReadOnlyList<FormUsage> inventoriedForms,
            HashSet<int> reEmittedStreams,
            TagPlan plan,
            PdfObjectIndex index)
        {
            if (inventoriedForms == null) return 0;

            int cleared = 0;

            for (int i = 0; i < inventoriedForms.Count; i++)
            {
                FormUsage form = inventoriedForms[i];
                if (form?.Stream == null || form.ObjectNumber <= 0) continue;
                if (reEmittedStreams.Contains(form.ObjectNumber)) continue;
                if (form.Stream[PageContentRewriter.StructParents] == null) continue;
                if (plan.Modified.ContainsKey(form.ObjectNumber)) continue;

                var clone = new PdfStream
                {
                    ObjectNumber     = form.ObjectNumber,
                    GenerationNumber = index.GetGeneration(form.ObjectNumber),
                    RawData          = form.Stream.RawData,
                };
                foreach (KeyValuePair<PdfName, PdfObject> entry in form.Stream)
                    clone.Set(entry.Key, entry.Value);

                clone.Remove(PageContentRewriter.StructParents);

                plan.Modified[form.ObjectNumber] = clone;
                cleared++;
            }

            return cleared;
        }

        /// <summary>
        /// Returns the plan's editable copy of an object, creating it if this is the first edit.
        /// Extending an existing clone matters: two steps revising the same page must not each
        /// register a clone built from the original, or the second silently discards the first.
        /// </summary>
        private static PdfDictionary GetOrClonePlanned(
            TagPlan plan, PdfObjectIndex index, PdfDictionary source, int objectNumber)
        {
            if (plan.Modified.TryGetValue(objectNumber, out PdfObject existing))
                return existing as PdfDictionary;

            PdfDictionary clone = CloneDictionary(source, objectNumber, index.GetGeneration(objectNumber));
            plan.Modified[objectNumber] = clone;
            return clone;
        }

        /// <summary>
        /// Builds a PDF text string (ISO 32000-1 §7.9.2.2).
        ///
        /// <para><b>One implementation, in Core</b> — <see cref="PdfString(string)"/>, which is its
        /// own inverse under <see cref="PdfString.GetText"/>: a character PDFDocEncoding would
        /// reinterpret (0xA0, 0x18-0x1F, 0x80-0x9E) goes out as UTF-16BE.</para>
        ///
        /// <para>A Latin-1-representable character outside the PDFDocEncoding-divergent ranges —
        /// <c>é</c>, say — goes out as the single
        /// byte 0xE9 instead of a UTF-16BE pair, which is what a reader expects and is smaller.
        /// Anything the two encodings disagree about still goes out as UTF-16BE with the mandatory
        /// <c>FE FF</c> marker.</para>
        ///
        /// <para>Null and empty become an empty string rather than throwing.</para>
        /// </summary>
        private static PdfString MakeTextString(string text) => new PdfString(text ?? string.Empty);

        // One implementation, in Core.IO.StreamBytes. It rewinds a seekable stream before
        // reading, where a copy reading from the current position would see only the rest.
        private static byte[] ReadAll(Stream stream) => StreamBytes.ReadAll(stream);
    }
}

