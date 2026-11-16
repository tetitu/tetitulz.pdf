// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Base.Operations.Images;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    /// <summary>What kind of transparency a page reaches.</summary>
    public enum TransparencyKind
    {
        /// <summary>The page declares a transparency group (<c>/Group</c>, §11.4.7).</summary>
        PageGroup,

        /// <summary>A form XObject or annotation appearance the page draws declares a transparency group.</summary>
        ContainerGroup,

        /// <summary>An <c>/ExtGState</c> sets a soft mask (<c>/SMask</c> other than <c>/None</c>).</summary>
        SoftMask,

        /// <summary>An <c>/ExtGState</c> sets a constant alpha below 1 (<c>/CA</c> or <c>/ca</c>).</summary>
        ConstantAlpha,

        /// <summary>An <c>/ExtGState</c> names a blend mode other than <c>/Normal</c> or <c>/Compatible</c>.</summary>
        BlendMode,

        /// <summary>An image XObject carries its own soft mask (<c>/SMask</c>).</summary>
        ImageSoftMask,

        /// <summary>A JPEG 2000 image asks for the opacity channel in its file (<c>/SMaskInData</c> 1 or 2).</summary>
        ImageOpacityInData,

        /// <summary>A markup annotation is drawn at a constant opacity below 1 (<c>/CA</c>, §12.5.6.2).</summary>
        AnnotationOpacity,

        /// <summary>An annotation is composited with a blend mode other than Normal (<c>/BM</c>, ISO 32000-2 §12.5.2).</summary>
        AnnotationBlendMode,
    }

    /// <summary>One transparency construct a page reaches.</summary>
    public sealed class TransparencyFinding
    {
        internal TransparencyFinding(int pageIndex, TransparencyKind kind, string detail)
        {
            PageIndex = pageIndex;
            Kind = kind;
            Detail = detail;
        }

        /// <summary>Zero-based index of the page that reaches it.</summary>
        public int PageIndex { get; }

        /// <summary>What it is.</summary>
        public TransparencyKind Kind { get; }

        /// <summary>Where it is — the resource name or object that carries it.</summary>
        public string Detail { get; }

        /// <inheritdoc/>
        public override string ToString() => $"page {PageIndex + 1}: {Kind} ({Detail})";
    }

    /// <summary>
    /// Every transparency construct each page reaches — through its own resources and every form,
    /// tiling pattern, Type 3 font and annotation appearance under them (<see cref="DocumentImageIndex"/>'s
    /// walk) — which is the question "does this page need flattening?".
    /// </summary>
    /// <remarks>
    /// <para><b>The broader of the two validators' readings, on purpose.</b> PDF/A-1 (A09–A14) and
    /// PDF/X (X09) word the rule differently — every <c>/BM</c> entry against the first, any page
    /// <c>/Group</c> against a transparency one — and a flattener that missed a page either rejects
    /// would hand a converter a document still failing its validator. Flattening a page that did not
    /// strictly need it costs sharpness, never correctness. It also reads two constructs neither
    /// validator does: a shading pattern's own <c>/ExtGState</c> (§8.7.4.3), a markup annotation's
    /// <c>/CA</c> opacity, and an annotation's <c>/BM</c> (ISO 32000-2 §12.5.2; PDF/A-4's 6.2.9 reads
    /// it).</para>
    /// <para><b>A resource counts whether or not the content draws it</b>, as the validators read
    /// it: an unused transparent <c>/ExtGState</c> still fails them, so a page holding one is
    /// flattened — which also removes it.</para>
    /// </remarks>
    internal static class TransparencyScan
    {
        internal static List<TransparencyFinding> Scan(PdfDocument doc)
        {
            var findings = new List<TransparencyFinding>();

            for (int p = 0; p < doc.Pages.Count; p++)
            {
                PdfDictionary page = doc.Pages[p].Dictionary;
                if (doc.ResolveObject(page[PdfName.Group]) is PdfDictionary)
                    findings.Add(new TransparencyFinding(p, TransparencyKind.PageGroup, "page /Group"));

                if (doc.ResolveObject(page[PdfName.Annots]) is PdfArray annots)
                    for (int i = 0; i < annots.Count; i++)
                    {
                        if (!(doc.ResolveObject(annots[i]) is PdfDictionary annot)) continue;
                        // Both opacity entries: the renderers composite an appearance by /ca first,
                        // then /CA (AnnotationAppearance.CompositeAlpha), and the synthesised
                        // fallback by /CA, so either below 1 is transparency a reader shows.
                        if (PdfResolve.Number(doc, annot, PdfName.CA) is PdfNumber ca && ca.DoubleValue < 1)
                            findings.Add(new TransparencyFinding(p, TransparencyKind.AnnotationOpacity,
                                $"annotation {i} /CA {ca.DoubleValue.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
                        else if (PdfResolve.Number(doc, annot, PdfName.ca) is PdfNumber fillAlpha && fillAlpha.DoubleValue < 1)
                            findings.Add(new TransparencyFinding(p, TransparencyKind.AnnotationOpacity,
                                $"annotation {i} /ca {fillAlpha.DoubleValue.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
                        if (NamesASeparableOrOtherBlend(doc.ResolveObject(annot[PdfName.BM])))
                            findings.Add(new TransparencyFinding(p, TransparencyKind.AnnotationBlendMode, $"annotation {i} /BM"));
                    }
            }

            // A dictionary pages share is scanned once and its findings given to each page that reaches
            // it: scanned on every page it was pages × entries — a thousand pages over one dictionary
            // of forty thousand entries was most of the PDF/A validator's thirteen seconds.
            var byResources = new Dictionary<object, List<TransparencyFinding>>(PdfReferenceEquality.Instance);
            DocumentImageIndex.Build(doc,
                onResources: (p, resources) =>
                {
                    if (!byResources.TryGetValue(resources, out List<TransparencyFinding> found))
                    {
                        found = new List<TransparencyFinding>();
                        ScanResources(doc, -1, resources, found);
                        byResources[resources] = found;
                    }
                    foreach (TransparencyFinding f in found)
                        findings.Add(new TransparencyFinding(p, f.Kind, f.Detail));
                },
                onContainer: (p, container) =>
                {
                    if (doc.ResolveObject(container[PdfName.Group]) is PdfDictionary)
                        findings.Add(new TransparencyFinding(p, TransparencyKind.ContainerGroup,
                            $"object {container.ObjectNumber} /Group"));
                });

            return findings;
        }

        /// <summary>
        /// Whether drawing <paramref name="container"/> — a form XObject, a tiling pattern, a Type 3
        /// font — reaches any transparency construct: a group of its own, or one in the resources it
        /// or anything under it paints with. The page scan's reading, from one container.
        /// </summary>
        internal static bool Reaches(PdfDocument doc, PdfDictionary container)
        {
            var findings = new List<TransparencyFinding>();
            DocumentImageIndex.WalkContainer(doc, container,
                onResources: (p, resources) => ScanResources(doc, p, resources, findings),
                onContainer: (p, c) =>
                {
                    if (doc.ResolveObject(c[PdfName.Group]) is PdfDictionary)
                        findings.Add(new TransparencyFinding(p, TransparencyKind.ContainerGroup, "/Group"));
                });
            return findings.Count > 0;
        }

        /// <summary>Whether a graphics state dictionary sets a soft mask, an alpha below 1 or a blend mode other than Normal.</summary>
        internal static bool IsTransparent(PdfDocument doc, PdfDictionary gs)
        {
            var findings = new List<TransparencyFinding>();
            ScanExtGState(doc, -1, null, gs, findings);
            return findings.Count > 0;
        }

        /// <summary>Whether an image XObject carries opacity of its own: a soft mask, or a JPEG 2000 opacity channel asked for.</summary>
        internal static bool IsTransparentImage(PdfDocument doc, PdfStream image)
            => doc.ResolveObject(image[PdfName.SMask]) is PdfStream || PdfResolve.Int(doc, image, PdfName.SMaskInData, 0) > 0;

        private static void ScanResources(PdfDocument doc, int page, PdfDictionary resources, List<TransparencyFinding> findings)
        {
            if (doc.ResolveObject(resources[PdfName.ExtGState]) is PdfDictionary states)
                foreach (var kvp in states)
                    if (doc.ResolveObject(kvp.Value) is PdfDictionary gs)
                        ScanExtGState(doc, page, "/" + kvp.Key.Value, gs, findings);

            if (doc.ResolveObject(resources[PdfName.XObject]) is PdfDictionary xobjects)
                foreach (var kvp in xobjects)
                {
                    if (!(doc.ResolveObject(kvp.Value) is PdfStream xobject)) continue;
                    if (doc.ResolveObject(xobject[PdfName.SMask]) is PdfStream)
                        findings.Add(new TransparencyFinding(page, TransparencyKind.ImageSoftMask, "/" + kvp.Key.Value));
                    if (PdfResolve.Int(doc, xobject, PdfName.SMaskInData, 0) > 0)
                        findings.Add(new TransparencyFinding(page, TransparencyKind.ImageOpacityInData, "/" + kvp.Key.Value));
                }

            // A shading pattern carries a graphics state of its own (§8.7.4.3, Table 74).
            if (doc.ResolveObject(resources[PdfName.Pattern]) is PdfDictionary patterns)
                foreach (var kvp in patterns)
                    if (doc.ResolveObject(kvp.Value) is PdfDictionary pattern
                        && PdfResolve.Int(doc, pattern, PdfName.PatternType, 0) == 2
                        && doc.ResolveObject(pattern[PdfName.ExtGState]) is PdfDictionary patternGs)
                        ScanExtGState(doc, page, $"pattern /{kvp.Key.Value}", patternGs, findings);
        }

        private static void ScanExtGState(PdfDocument doc, int page, string where, PdfDictionary gs, List<TransparencyFinding> findings)
        {
            PdfObject smask = doc.ResolveObject(gs[PdfName.SMask]);
            if (smask != null && !(smask is PdfName none && none == PdfName.None))
                findings.Add(new TransparencyFinding(page, TransparencyKind.SoftMask, where));

            if (PdfResolve.Number(doc, gs, PdfName.CA) is PdfNumber stroke && stroke.DoubleValue < 1
                || PdfResolve.Number(doc, gs, PdfName.ca) is PdfNumber fill && fill.DoubleValue < 1)
                findings.Add(new TransparencyFinding(page, TransparencyKind.ConstantAlpha, where));

            if (NamesASeparableOrOtherBlend(doc.ResolveObject(gs[PdfName.BM])))
                findings.Add(new TransparencyFinding(page, TransparencyKind.BlendMode, where));
        }

        /// <summary>
        /// True when <paramref name="bm"/> names any mode other than <c>/Normal</c> or
        /// <c>/Compatible</c> — every entry of an array, since a reader takes the first it supports
        /// and any entry may be that one.
        /// </summary>
        internal static bool NamesASeparableOrOtherBlend(PdfObject bm)
        {
            if (bm is PdfName name) return !IsNormal(name);
            if (bm is PdfArray modes)
                for (int i = 0; i < modes.Count; i++)
                    if (!(modes[i] is PdfName mode) || !IsNormal(mode)) return true;
            return false;
        }

        private static bool IsNormal(PdfName mode)
            => mode.Value == PdfBlendModes.Normal || mode.Value == PdfBlendModes.Compatible;
    }
}
