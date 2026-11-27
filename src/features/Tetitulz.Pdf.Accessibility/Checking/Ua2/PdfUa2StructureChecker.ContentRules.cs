// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.Plugins.Conformance;

namespace Tetitulz.Pdf.Accessibility.Checking.Ua2
{
    // §8.2.2 and §8.3.1 (shown text, paths, images, shadings and forms are tagged or an artifact), §8.4.5.5.1's glyph sentence, §8.2.5.29
    // (a formula's representation), §8.10.2.3 (a widget's actions described) and §8.6 over the text
    // strings of structure elements and annotations.
    public static partial class PdfUa2StructureChecker
    {
        // ----------------------------------------------------------------
        //  Clauses 8.2.2 and 8.3.1 — content is real content in the tree, or an artifact
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.2.2: "All real content … shall be enclosed within semantically
        /// appropriate structure elements … Content that is not considered real content shall be an
        /// artifact in accordance with 8.3"; §8.3.1: "Any content, except Annotations (see 8.9), that
        /// is not real content … shall be explicitly identified" as one. Whichever the content is, it
        /// is in marked content a structure element claims or it is an artifact, so text a page's own
        /// content stream shows that is neither — outside every <c>/Artifact</c> sequence, and either
        /// in no marked-content sequence with an <c>/MCID</c> or in one no element claims — is reported.
        /// </summary>
        /// <remarks>
        /// <para>Judged for text, through <see cref="WalkShownText"/>, in any render mode (invisible text
        /// is content too), and in the page's own stream only: the walk starts each nested stream with a
        /// fresh marked-content shadow, so a form XObject's text takes its marking from the sequence that
        /// invokes it, which <see cref="CheckPaintedContentIsTaggedOrArtifact"/> reads for a form drawn
        /// outside marked content, with the page's paths, images and shadings; an annotation's appearance
        /// is exempt by §8.3.1. An MCID is claimed when a structure element names it on that page
        /// (<see cref="MapMarkedContentOwners"/>). One finding a page and MCID.</para>
        /// </remarks>
        private static void CheckShownTextIsTaggedOrArtifact(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            Dictionary<(int Page, int Mcid), StructNode> owners = null;
            var reported = new HashSet<(int Page, int Mcid)>();
            int found = 0;

            WalkShownText(doc, () => found > IndividualReportLimit, shown =>
            {
                if (shown.InArtifact || !shown.IsPageContent || !ShowsAnything(shown.Segments)) return;
                if (shown.Mcid >= 0)
                {
                    owners ??= MapMarkedContentOwners(doc, catalog);
                    if (owners.ContainsKey((shown.PageIndex, shown.Mcid))) return;
                }

                if (!reported.Add((shown.PageIndex, shown.Mcid))) return;
                found++;
                if (found > IndividualReportLimit) return;
                v.Add(new PdfUa2StructureViolation("UA2-8.2.2",
                    $"Page {shown.PageIndex + 1}: text " +
                    (shown.Mcid >= 0
                        ? $"in marked content {shown.Mcid}, which no structure element claims,"
                        : "that no marked-content sequence with an /MCID encloses") +
                    " is neither in the structure tree nor marked as an artifact; real content is enclosed in " +
                    "structure elements and other content is an artifact (ISO 14289-2 §8.2.2, §8.3.1)."));
            });

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.2.2",
                    "…and at least one more, not listed individually."));
        }

        /// <summary>Whether a shown operator's segments hold at least one byte: an empty string draws nothing.</summary>
        private static bool ShowsAnything(List<object> segments)
        {
            foreach (object segment in segments)
                if (segment is byte[] bytes && bytes.Length > 0) return true;
            return false;
        }

        /// <summary>
        /// ISO 14289-2:2024 §8.2.2 and §8.3.1 for the content that is not text: a path the page's own content
        /// stream strokes or fills, an image it draws (an image XObject or an inline image), a shading it paints
        /// with <c>sh</c>, and a form XObject it draws — each in marked content a structure element claims or in
        /// an artifact. Reported as <see cref="CheckShownTextIsTaggedOrArtifact"/> reports text, one finding a
        /// page, MCID and kind of content.
        /// </summary>
        /// <remarks>
        /// <para>A path ended by <c>n</c> paints nothing, and is not content. A form drawn inside marked content
        /// takes its marking from it. A form drawn outside every marked-content sequence holds content of its
        /// own, and is reported when something it paints — text, a path, an image, a shading, or a form it draws
        /// in turn — stands outside every artifact and MCID of its own stream (<see cref="FormPaintsUnmarked"/>);
        /// an MCID in a form is read as marking, its claim through the form's <c>/StructParents</c> not
        /// followed. Annotation appearances are exempt by §8.3.1.</para>
        /// </remarks>
        private static void CheckPaintedContentIsTaggedOrArtifact(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            if (doc.Pages == null) return;

            Dictionary<(int Page, int Mcid), StructNode> owners = null;
            var reported = new HashSet<(int Page, int Mcid, string Kind)>(KeyedHash.Comparer<(int Page, int Mcid, string Kind)>(
                k => unchecked((k.Page * 31 + k.Mcid) * 31 + KeyedHash.Of(k.Kind))));
            var forms = new FormReading();
            int found = 0;
            long documentBytes = 0;

            for (int p = 0; p < doc.Pages.Count && found <= IndividualReportLimit; p++)
            {
                var scan = PdfXValidator.ScanPage(doc, doc.Pages[p],
                    PdfXValidator.DefaultMaxPageContentBytes, ref documentBytes,
                    PdfXValidator.DefaultMaxDocumentContentBytes, collectContent: true);
                if (scan.PageContentIndex < 0) continue;

                PdfDictionary resources = scan.ContentStreamResources[scan.PageContentIndex];
                List<ContentStreamOperation> ops;
                try { ops = ScanReuse.Parse(scan.ContentStreams[scan.PageContentIndex]); }
                catch (Exception ex)
                {
                    Report("PdfUa2StructureChecker.CheckPaintedContentIsTaggedOrArtifact",
                        "a page's content stream could not be parsed; its paths and images were not checked", ex);
                    continue;
                }

                var marking = new MarkedContentShadow();
                foreach (ContentStreamOperation op in ops)
                {
                    if (marking.Observe(op, resources, doc)) continue;

                    string kind = PaintedKind(doc, op, resources, out PdfStream form);
                    if (kind == null || marking.InArtifact) continue;
                    bool unmarkedForm = form != null && !marking.IsMarked;
                    if (unmarkedForm && !FormPaintsUnmarked(doc, form, resources, 0, forms)) continue;
                    if (marking.Mcid >= 0)
                    {
                        owners ??= MapMarkedContentOwners(doc, catalog);
                        if (owners.ContainsKey((p, marking.Mcid))) continue;
                    }

                    if (!reported.Add((p, marking.Mcid, kind))) continue;
                    found++;
                    if (found > IndividualReportLimit) break;
                    string what = unmarkedForm
                        ? "a form XObject drawn outside every marked-content sequence paints content that"
                        : kind + (marking.Mcid >= 0
                            ? $" in marked content {marking.Mcid}, which no structure element claims,"
                            : " that no marked-content sequence with an /MCID encloses");
                    v.Add(new PdfUa2StructureViolation("UA2-8.2.2",
                        $"Page {p + 1}: {what} is neither in the structure tree nor marked as an artifact; real " +
                        "content is enclosed in structure elements and other content is an artifact " +
                        "(ISO 14289-2 §8.2.2, §8.3.1)."));
                }
            }

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.2.2",
                    "…and at least one more, not listed individually."));
            if (forms.Cut != null)
                v.Add(new PdfUa2StructureViolation("UA2-99",
                    "§8.2.2 was not judged in full for the forms pages draw outside marked content: " + forms.Cut + "."));
        }

        /// <summary>
        /// What a painting operator draws, for a finding — "a path", "an image", "a shading", "a form XObject" —
        /// or <see langword="null"/> for one that paints no content of its own (text is
        /// <see cref="CheckShownTextIsTaggedOrArtifact"/>'s); <paramref name="form"/> is the form a <c>Do</c> draws.
        /// </summary>
        private static string PaintedKind(PdfDocument doc, ContentStreamOperation op, PdfDictionary resources, out PdfStream form)
        {
            form = null;
            switch (op.Operator)
            {
                case PdfOperators.Stroke:
                case PdfOperators.CloseStroke:
                case PdfOperators.Fill:
                case PdfOperators.FillObsolete:
                case PdfOperators.FillEvenOdd:
                case PdfOperators.FillStroke:
                case PdfOperators.FillStrokeEvenOdd:
                case PdfOperators.CloseFillStroke:
                case PdfOperators.CloseFillStrokeEvenOdd:
                    return "a path";
                case PdfOperators.ShadingFill:
                    return "a shading";
                case PdfOperators.BeginInlineImage:
                    return "an image";
                case PdfOperators.InvokeXObject:
                {
                    string name = ContentOperand.Name(op, 0);
                    if (name == null) return null;
                    var xobjects = Resolve(doc, resources?[PdfName.XObject]) as PdfDictionary;
                    if (!(Resolve(doc, xobjects?[new PdfName(name)]) is PdfStream xobject)) return null;
                    string subtype = (Resolve(doc, xobject[PdfName.Subtype]) as PdfName)?.Value;
                    if (subtype == "Image") return "an image";
                    if (subtype != "Form") return null;
                    form = xobject;
                    return "a form XObject";
                }
                default:
                    return null;
            }
        }

        /// <summary>What <see cref="FormPaintsUnmarked"/> has read: each form's answer, and the bytes it may still decode.</summary>
        private sealed class FormReading
        {
            internal readonly Dictionary<object, bool> Answers = new Dictionary<object, bool>(PdfReferenceEquality.Instance);
            internal long BytesLeft = PdfXValidator.DefaultMaxDocumentContentBytes;

            /// <summary>Why a form was not read, the first time one was not; <see langword="null"/> while every one was.</summary>
            internal string Cut;
        }

        /// <summary>
        /// Whether a form XObject paints anything outside every artifact and MCID of its own content stream —
        /// text, a path, an image, a shading, or a form it draws outside marking in turn. Each form is read
        /// once (a form that draws itself reads as painting nothing more), its nesting bounded by
        /// <see cref="PdfLimits.MaxFormXObjectDepth"/> and its bytes by the document's content budget; a form not
        /// read answers false, and <see cref="FormReading.Cut"/> says why.
        /// </summary>
        private static bool FormPaintsUnmarked(PdfDocument doc, PdfStream form, PdfDictionary invokingResources,
            int depth, FormReading reading)
        {
            if (reading.Answers.TryGetValue(form, out bool known)) return known;
            if (depth >= PdfLimits.MaxFormXObjectDepth)
            {
                reading.Cut ??= $"forms nested deeper than {PdfLimits.MaxFormXObjectDepth} were not read";
                return false;
            }
            reading.Answers[form] = false;

            if (!form.TryGetDecodedData(Math.Min(reading.BytesLeft, PdfXValidator.DefaultMaxPageContentBytes),
                    out byte[] content, out string reason))
            {
                reading.Cut ??= "a form's content was not decoded (" + reason + ")";
                return false;
            }
            reading.BytesLeft -= content.Length;

            List<ContentStreamOperation> ops;
            try { ops = ContentStreamParser.Parse(content); }
            catch (Exception ex)
            {
                reading.Cut ??= "a form's content could not be parsed (" + ex.Message + ")";
                return false;
            }

            var resources = Resolve(doc, form[PdfName.Resources]) as PdfDictionary ?? invokingResources;
            var marking = new MarkedContentShadow();
            bool paints = false;
            foreach (ContentStreamOperation op in ops)
            {
                if (marking.Observe(op, resources, doc) || marking.IsMarked) continue;
                string kind = PaintedKind(doc, op, resources, out PdfStream nested);
                if (kind == null) paints = ShowsText(op);
                else paints = nested == null || FormPaintsUnmarked(doc, nested, resources, depth + 1, reading);
                if (paints) break;
            }
            reading.Answers[form] = paints;
            return paints;
        }

        /// <summary>Whether an operator shows text with at least one byte in it (<c>Tj</c>, <c>'</c>, <c>"</c>, <c>TJ</c>).</summary>
        private static bool ShowsText(ContentStreamOperation op)
        {
            switch (op.Operator)
            {
                case PdfOperators.ShowText:
                case PdfOperators.NextLineShowText:
                    return ShowsAnything(ContentOperand.ShowTextSegments(op, 0));
                case PdfOperators.NextLineSetSpacingShowText:
                    return ShowsAnything(ContentOperand.ShowTextSegments(op, 2));
                case PdfOperators.ShowTextArray:
                    return ShowsAnything(ContentOperand.ShowTextArraySegments(op));
                default:
                    return false;
            }
        }

        /// <summary>
        /// The marked-content sequences open at a point of one content stream (§14.6) — whether one is an
        /// artifact, whether one carries <c>/ActualText</c>, the innermost <c>/MCID</c> — the state both content
        /// walks read. <c>q</c>/<c>Q</c> do not save it; an <c>EMC</c> with none open is passed over.
        /// </summary>
        private sealed class MarkedContentShadow
        {
            private readonly Stack<(bool Artifact, bool ActualText, bool HasMcid)> _frames = new Stack<(bool, bool, bool)>();
            private readonly Stack<int> _mcids = new Stack<int>();
            private int _artifactDepth, _actualTextDepth;

            /// <summary>Inside an <c>/Artifact</c> sequence at any depth.</summary>
            internal bool InArtifact => _artifactDepth > 0;

            /// <summary>Inside a sequence whose property list carries <c>/ActualText</c>, an empty one included.</summary>
            internal bool HasActualText => _actualTextDepth > 0;

            /// <summary>The innermost enclosing <c>/MCID</c>, or -1.</summary>
            internal int Mcid => _mcids.Count > 0 ? _mcids.Peek() : -1;

            /// <summary>Inside an artifact, or inside a sequence with an <c>/MCID</c>.</summary>
            internal bool IsMarked => InArtifact || Mcid >= 0;

            /// <summary>Takes in a <c>BMC</c>, <c>BDC</c> or <c>EMC</c> and answers true; any other operator, false.</summary>
            internal bool Observe(ContentStreamOperation op, PdfDictionary resources, PdfDocument doc)
            {
                switch (op.Operator)
                {
                    case PdfOperators.BeginMarkedContent:
                    case PdfOperators.BeginMarkedContentProps:
                    {
                        bool artifact = ContentOperand.Name(op, 0) == "Artifact";
                        // default(MarkedContentInfo) has Mcid 0, not -1 — only a BDC that actually carries a
                        // property operand may supply either value.
                        bool hasProps = op.Operator == PdfOperators.BeginMarkedContentProps && op.Operands.Length > 1;
                        var info = hasProps ? MarkedContentInfo.Read(op.Operands[1], resources, doc) : default;
                        bool actualText = hasProps && info.ActualText != null;
                        bool hasMcid = hasProps && info.Mcid >= 0;
                        _frames.Push((artifact, actualText, hasMcid));
                        if (artifact) _artifactDepth++;
                        if (actualText) _actualTextDepth++;
                        if (hasMcid) _mcids.Push(info.Mcid);
                        return true;
                    }
                    case PdfOperators.EndMarkedContent:
                    {
                        if (_frames.Count == 0) return true;
                        var frame = _frames.Pop();
                        if (frame.Artifact) _artifactDepth--;
                        if (frame.ActualText) _actualTextDepth--;
                        if (frame.HasMcid) _mcids.Pop();
                        return true;
                    }
                    default:
                        return false;
                }
            }
        }

        // ----------------------------------------------------------------
        //  Clause 8.4.5.5.1 — embedded fonts define every glyph referenced for rendering
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.4.5.5.1: "Embedded fonts shall define all glyphs referenced for
        /// rendering within the conforming file." The sentence ISO 14289-1 7.21.4.1 and ISO 19005
        /// state too, so it is the reading PDF/UA-1's UA57 and PDF/A's A84 share: the codes each font
        /// paints (<c>PdfUaFontUse.PaintedCodes</c>, text rendering mode 3 painting nothing), each judged
        /// by <c>FontGlyphPresence</c>. Shares <c>UA2-8.4.5.5.1</c> with
        /// <see cref="CheckFontsUsedForRenderingAreEmbedded"/>. A font whose codes could not be judged,
        /// or a document whose painted glyphs could not be established, is a <c>UA2-99</c>, not a
        /// verdict.
        /// </summary>
        private static void CheckEmbeddedFontsDefineShownGlyphs(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var codes = PdfUaFontUse.PaintedCodes(doc, PdfUaChecker.DefaultMaxMcidScanBytesPerPage,
                PdfUaChecker.DefaultMaxMcidScanBytesPerDocument);
            if (codes == null)
            {
                v.Add(new PdfUa2StructureViolation("UA2-99",
                    "§8.4.5.5.1's glyph sentence was not judged: the glyphs used for rendering could not be " +
                    "established" + (PdfUaFontUse.LastError != null ? " (" + PdfUaFontUse.LastError + ")" : "") + "."));
                return;
            }

            var missing = new SortedSet<string>(StringComparer.Ordinal);
            var unjudged = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var entry in codes)
            {
                string what = FontGlyphPresence.WhatIsMissing(doc, entry.Key, entry.Value, out string notJudged);
                string label = FontLabel(doc, entry.Key);
                if (what != null) missing.Add(label + ": " + what);
                if (notJudged != null) unjudged.Add(label + ": " + notJudged);
            }
            if (missing.Count > 0)
                v.Add(new PdfUa2StructureViolation("UA2-8.4.5.5.1",
                    "A font used for rendering shows a glyph its embedded program does not define; every glyph " +
                    "referenced for rendering is defined by the embedded font (ISO 14289-2 §8.4.5.5.1): " +
                    string.Join("; ", missing) + "."));
            if (unjudged.Count > 0)
                v.Add(new PdfUa2StructureViolation("UA2-99",
                    "§8.4.5.5.1's glyph sentence was not judged for a font: " + string.Join("; ", unjudged) + "."));
        }

        // ----------------------------------------------------------------
        //  Clause 8.2.5.29 — a formula's representation
        // ----------------------------------------------------------------

        /// <summary>The MIME types of a presentation MathML file: MathML's own and its presentation-only form.</summary>
        private static readonly string[] MathMlMimeTypes = { "application/mathml+xml", "application/mathml-presentation+xml" };

        /// <summary>
        /// ISO 14289-2:2024 §8.2.5.29. §8.2.5.29.1: a mathematical expression is represented "using either
        /// structure types defined in presentation MathML … or as presentation MathML via the Associated
        /// Files mechanism … or both", and an associated file that represents a Formula in presentation
        /// MathML "shall be associated with the respective Formula structure element and shall include an
        /// AFRelationship entry with the value of Supplement". §8.2.5.29.2: a non-mathematical formula
        /// "shall have either an Alt entry or an ActualText entry". So an element that means
        /// <c>Formula</c> with neither MathML — a child in the MathML namespace, or a MathML file among
        /// its <c>/AF</c> — nor an <c>/Alt</c> or <c>/ActualText</c> breaks one clause or the other,
        /// whichever kind of formula it is (<c>UA2-8.2.5.29</c>); and a MathML file a Formula's
        /// <c>/AF</c> names without <c>/AFRelationship /Supplement</c> breaks §8.2.5.29.1
        /// (<c>UA2-8.2.5.29.1</c>, with <see cref="CheckMathIsChildOfFormula"/>).
        /// </summary>
        /// <remarks>
        /// A MathML file is one whose embedded file stream's <c>/Subtype</c> names a MathML MIME type
        /// (<see cref="MathMlMimeTypes"/>, compared without regard to case, as MIME types are). Whether
        /// the MathML is presentation MathML, and whether a Formula's content is mathematics, are not
        /// judged.
        /// </remarks>
        private static void CheckFormulaRepresentation(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            var structTreeRoot = ResolveStructTreeRoot(doc, catalog);
            if (structTreeRoot == null) return;

            var formulas = new List<StructNode>();
            var withMathMlChild = new HashSet<StructNode>();
            StructTreeWalker.Walk(structTreeRoot, doc, index: null, node =>
            {
                if (IsStandardStructureType(doc, node, "Formula", inPdf2Namespace: true)) formulas.Add(node);
                if (node.Parent != null && node.Dict != null && IsInNamespace(doc, node.Dict, MathMlNamespace))
                    withMathMlChild.Add(node.Parent);
            });

            int unrepresented = 0, unrelated = 0;
            foreach (StructNode formula in formulas)
            {
                bool mathMl = withMathMlChild.Contains(formula);
                if (Resolve(doc, formula.Dict[PdfName.AF]) is PdfArray files)
                    for (int i = 0; i < files.Count; i++)
                    {
                        if (!(Resolve(doc, files[i]) is PdfDictionary spec) || !IsMathMlFile(doc, spec)) continue;
                        mathMl = true;
                        if ((Resolve(doc, spec[PdfName.AFRelationship]) as PdfName)?.Value == "Supplement") continue;
                        if (++unrelated > IndividualReportLimit) continue;
                        v.Add(new PdfUa2StructureViolation("UA2-8.2.5.29.1",
                            "A MathML file associated with a Formula structure element has no /AFRelationship " +
                            "/Supplement; §8.2.5.29.1 requires that value (ISO 14289-2 §8.2.5.29.1)." +
                            (formula.ObjectNumber > 0 ? $" Object {formula.ObjectNumber}." : "")));
                    }

                if (mathMl || formula.Dict[PdfName.Alt] != null || formula.Dict[PdfName.ActualText] != null) continue;
                if (++unrepresented > IndividualReportLimit) continue;
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.29",
                    "A Formula structure element has no MathML — no child in the MathML namespace, no MathML " +
                    "associated file — and neither /Alt nor /ActualText: a mathematical expression is " +
                    "represented in MathML (§8.2.5.29.1) and a non-mathematical formula has /Alt or " +
                    "/ActualText (§8.2.5.29.2) (ISO 14289-2 §8.2.5.29)." +
                    (formula.ObjectNumber > 0 ? $" Object {formula.ObjectNumber}." : "")));
            }

            if (unrelated > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.29.1", "…and at least one more, not listed individually."));
            if (unrepresented > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.2.5.29", "…and at least one more, not listed individually."));
        }

        /// <summary>Whether <paramref name="element"/>'s <c>/NS</c> names the namespace <paramref name="namespaceName"/>.</summary>
        private static bool IsInNamespace(PdfDocument doc, PdfDictionary element, string namespaceName)
        {
            var nsDict = Resolve(doc, element[PdfName.NS]) as PdfDictionary;
            return (Resolve(doc, nsDict?[PdfName.NS]) as PdfString)?.GetText() == namespaceName;
        }

        /// <summary>Whether a file specification embeds a file whose <c>/Subtype</c> is a MathML MIME type.</summary>
        private static bool IsMathMlFile(PdfDocument doc, PdfDictionary spec)
        {
            var ef = Resolve(doc, spec[PdfName.EF]) as PdfDictionary;
            var file = Resolve(doc, ef?[PdfName.UF]) as PdfStream ?? Resolve(doc, ef?[PdfName.F]) as PdfStream;
            string subtype = (Resolve(doc, file?[PdfName.Subtype]) as PdfName)?.Value;
            if (subtype == null) return false;
            foreach (string mime in MathMlMimeTypes)
                if (string.Equals(subtype, mime, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ----------------------------------------------------------------
        //  Clause 8.10.2.3 — a widget's additional actions are described
        // ----------------------------------------------------------------

        /// <summary>
        /// ISO 14289-2:2024 §8.10.2.3: "If an additional action (AA) entry … is present in a widget
        /// annotation dictionary, the respective widget's Contents entry shall describe the action's
        /// intent." A widget whose dictionary holds a non-empty <c>/AA</c> and no non-empty
        /// <c>/Contents</c> — a field merged with its widget too, whose one <c>/AA</c> entry is the
        /// widget dictionary's. What the description says is not judged. A widget §8.9.2.2 makes an
        /// artifact (Invisible, or NoView without ToggleNoView) is not judged: §8.10.2 is the context a
        /// widget presented to the user is given, and an artifact presents nothing.
        /// </summary>
        private static void CheckWidgetActionsAreDescribed(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
            => ReportAnnotations(doc, "UA2-8.10.2.3",
                "A widget annotation has an /AA additional-actions entry but no /Contents describing the " +
                "actions' intent; §8.10.2.3 requires one (ISO 14289-2 §8.10.2.3).", v, dict =>
                    HasSubtype(doc, dict, PdfAnnotationSubtypes.Widget)
                    && Resolve(doc, dict[PdfName.AA]) is PdfDictionary actions && actions.Count > 0
                    && !HasNonEmptyString(doc, dict, PdfName.Contents)
                    && !IsRequiredArtifactByFlags(doc, dict));

        // ----------------------------------------------------------------
        //  Clause 8.6 — the text strings of structure elements and annotations
        // ----------------------------------------------------------------

        /// <summary>
        /// The structure element entries whose text strings are for a person: <c>/Alt</c>,
        /// <c>/ActualText</c> and <c>/E</c>, which a reader is given in place of or beside the content
        /// (ISO 32000-2 §14.9.3 to §14.9.5; §8.4.3's NOTE: "ActualText and Alt entries cannot use the
        /// Unicode PUA (see 8.6)"), and <c>/T</c>, the element's title "in human-readable form"
        /// (Table 355).
        /// </summary>
        private static readonly PdfName[] ElementTextStrings = { PdfName.Alt, PdfName.ActualText, PdfName.E, PdfName.T };

        /// <summary>
        /// ISO 14289-2:2024 §8.6: "Text strings … intended to be human readable shall not use the Unicode
        /// PUA." Over the text strings whose definition makes them human-readable: a structure element's
        /// <see cref="ElementTextStrings"/>, and an annotation's <c>/Contents</c> (Table 166: the text
        /// displayed for the annotation, or its alternative description). Shares <c>UA2-8.6</c> with
        /// <see cref="CheckOutlineTitlesNoPua"/>. A marked-content property list's <c>/ActualText</c> and
        /// <c>/Alt</c>, which live in a content stream, are not read here.
        /// </summary>
        private static void CheckElementAndAnnotationTextNoPua(
            PdfDocument doc, PdfDictionary catalog, List<PdfUa2StructureViolation> v)
        {
            int found = 0;
            var structTreeRoot = ResolveStructTreeRoot(doc, catalog);
            if (structTreeRoot != null)
                StructTreeWalker.Walk(structTreeRoot, doc, index: null, node =>
                {
                    if (node.Dict == null || found > IndividualReportLimit) return;
                    foreach (PdfName key in ElementTextStrings)
                    {
                        if (!(Resolve(doc, node.Dict[key]) is PdfString text) || !ContainsUnicodePua(text.GetText())) continue;
                        if (++found > IndividualReportLimit) return;
                        v.Add(new PdfUa2StructureViolation("UA2-8.6",
                            $"A {node.Role ?? "structure"} element's /{key.Value} uses a Unicode Private Use Area " +
                            "codepoint; §8.6 prohibits it in human-readable text strings (ISO 14289-2 §8.6)." +
                            (node.ObjectNumber > 0 ? $" Object {node.ObjectNumber}." : "")));
                    }
                });

            foreach (PageAnnotation annot in AnnotationsOf(doc))
            {
                if (!(Resolve(doc, annot.Dict[PdfName.Contents]) is PdfString contents) || !ContainsUnicodePua(contents.GetText())) continue;
                if (++found > IndividualReportLimit) continue;
                v.Add(new PdfUa2StructureViolation("UA2-8.6",
                    "An annotation's /Contents uses a Unicode Private Use Area codepoint; §8.6 prohibits it in " +
                    "human-readable text strings (ISO 14289-2 §8.6)." +
                    (annot.ObjectNumber > 0 ? $" Object {annot.ObjectNumber}." : "")));
            }

            if (found > IndividualReportLimit)
                v.Add(new PdfUa2StructureViolation("UA2-8.6", "…and at least one more, not listed individually."));
        }
    }
}
