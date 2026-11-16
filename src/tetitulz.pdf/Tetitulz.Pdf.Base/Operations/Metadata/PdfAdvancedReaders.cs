// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Operations.Metadata
{
    /// <summary>Read-only accessors for advanced PDF 2.0 features:
    /// document parts (DPart), unencrypted wrappers, and GeoPDF (Measure/VP).</summary>
    public static class PdfDPartReader
    {
        public sealed class DPartNode
        {
            public PdfDictionary Dictionary { get; }
            public PdfDictionary Metadata  { get; }
            public IReadOnlyList<DPartNode> Children { get; }

            /// <summary>
            /// Zero-based index of the part's first page, from its <c>/Start</c>; null when the part
            /// declares none or it names no page of this document.
            /// </summary>
            public int? StartPage { get; }

            /// <summary>
            /// Zero-based index of the part's last page, from its <c>/End</c>; null when the part
            /// declares none or it names no page of this document.
            /// </summary>
            public int? EndPage { get; }

            public DPartNode(PdfDictionary dict, PdfDictionary metadata, int? start, int? end, IReadOnlyList<DPartNode> children)
            {
                Dictionary = dict;
                Metadata   = metadata;
                StartPage  = start;
                EndPage    = end;
                Children   = children;
            }
        }

        /// <summary>Reads the document-parts hierarchy (ISO 32000-2 §14.12).
        /// Returns null when the PDF has no <c>/DPartRoot</c> entry.</summary>
        public static DPartNode ReadRoot(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            using var doc = PdfDocument.Open(input);
            var catalog = doc.Catalog?.Dictionary;
            if (catalog == null) return null;
            if (!(doc.ResolveObject(catalog[PdfName.DPartRoot]) is PdfDictionary dpRoot)) return null;

            var treeRoot = doc.ResolveObject(dpRoot[PdfName.DPartRootNode]) as PdfDictionary ?? dpRoot;
            return Build(treeRoot, doc);
        }

        /// <summary>Builds the DPart subtree rooted at <paramref name="dict"/>.</summary>
        /// <remarks>
        /// <b>The depth cap is not the cycle guard, and here it is worse than in the sibling reader.</b>
        /// A node reached twice doubles the work at every level, so <c>MaxTreeDepth</c> = 100 permits
        /// 2<sup>100</sup> visits — and unlike a pure walk, <i>each visit allocates</i> a
        /// <c>DPartNode</c> and a <c>List&lt;DPartNode&gt;</c> that the returned tree retains. So
        /// <c>/DPartRoot</c> with <c>1 0 obj &lt;&lt; /DParts [1 0 R 1 0 R] &gt;&gt;</c> exhausts memory
        /// rather than merely spinning, and it cannot even be interrupted cheaply.
        /// <paramref name="visited"/> is keyed on the resolved instance, matching
        /// <c>PdfUsageRightsReader.CollectSignatureFields</c>.
        /// </remarks>
        private static DPartNode Build(PdfDictionary dict, PdfDocument doc, int depth = 0,
            HashSet<PdfObject> visited = null)
        {
            var meta = doc.ResolveObject(dict[PdfName.DPM]) as PdfDictionary;
            int? start = PageIndexOf(dict, PdfName.Start, doc);
            int? end   = PageIndexOf(dict, PdfName.End, doc);

            visited ??= new HashSet<PdfObject>(PdfObjectIndex.ReferenceIdentityComparer);

            var children = new List<DPartNode>();
            if (depth >= PdfLimits.MaxTreeDepth || !visited.Add(dict))
                return new DPartNode(dict, meta, start, end, children);

            if (doc.ResolveObject(dict[PdfName.DParts]) is PdfArray arr)
            {
                // ISO 32000-2 §14.12, the DPart dictionary's /DParts: "An array of arrays. Each
                // element in the array is an array of indirect references to immediate descendant
                // DPart dictionaries." Casting each ELEMENT straight to a dictionary would return a
                // root with no children for every PDF/VT document written to the standard. The flat
                // spelling is accepted too, because some producers write it.
                for (int i = 0; i < arr.Count; i++)
                {
                    var element = doc.ResolveObject(arr[i]);
                    if (element is PdfArray group)
                    {
                        for (int j = 0; j < group.Count; j++)
                            if (doc.ResolveObject(group[j]) is PdfDictionary grouped)
                                children.Add(Build(grouped, doc, depth + 1, visited));
                    }
                    else if (element is PdfDictionary flat)
                    {
                        children.Add(Build(flat, doc, depth + 1, visited));
                    }
                }
            }
            return new DPartNode(dict, meta, start, end, children);
        }

        /// <summary>
        /// A DPart's <c>/Start</c> or <c>/End</c> as a zero-based page index, or null when the entry
        /// is absent or names no page of this document.
        /// </summary>
        /// <remarks>
        /// <para><b>§14.12 Table 409 makes both "an indirect reference to the page object"</b>, and
        /// this read never accepted one. It resolved the entry and asked for a number — so a
        /// conforming <c>/Start 12 0 R</c> resolved to a page <i>dictionary</i> and came back null,
        /// for every part of every document written to the standard, while the tests passed on
        /// fixtures that wrote a page number instead. The reference is now matched through
        /// <see cref="PdfDestinationPage.IndexOfPage"/>, the reader a destination's page goes
        /// through, so the two cannot disagree about what a page reference names.</para>
        ///
        /// <para><b>The number spelling is read too</b>, direct or behind a reference, because some
        /// producers write it — and it is range-checked the way a
        /// destination's page number is, so a part cannot report a page the document does not
        /// have.</para>
        /// </remarks>
        private static int? PageIndexOf(PdfDictionary dict, PdfName key, PdfDocument doc)
        {
            PdfObject raw = dict[key];
            if (raw == null) return null;

            int index = PdfDestinationPage.IndexOfPage(doc, raw);
            if (index == PdfDestinationPage.NotFound && raw is PdfReference)
                index = PdfDestinationPage.IndexOfPage(doc, doc.ResolveObject(raw) as PdfNumber);

            return index == PdfDestinationPage.NotFound ? (int?)null : index;
        }
    }

    /// <summary>
    /// Detects an unencrypted wrapper document (ISO 32000-2 §7.6.7) — a clear-text PDF carrying an
    /// encrypted payload document that only a custom security handler can open — and names the
    /// payload. It does NOT decrypt it.
    /// </summary>
    /// <remarks>
    /// <para><b>What makes a wrapper, per §7.6.7.</b> The payload's file specification carries
    /// <c>/AFRelationship /EncryptedPayload</c> and an encrypted payload dictionary <c>/EP</c> whose
    /// <c>/Subtype</c> names the cryptographic filter (Table 28), and it sits both in the catalog's
    /// <c>/AF</c> array and in the <c>/EmbeddedFiles</c> name tree. The wrapper is also a hidden
    /// collection — but a collection alone is a portfolio, not a wrapper.</para>
    ///
    /// <para><b>Not any portfolio is a wrapper</b>, and <c>/UR3</c> and <c>/EFF</c> live in the
    /// permissions and encryption dictionaries, not the catalog. A document is a wrapper when an encrypted payload is found, and <see cref="WrapperInfo.Type"/>
    /// is the filter a reader needs to open it.</para>
    /// </remarks>
    public static class PdfUnencryptedWrapperReader
    {
        public sealed class WrapperInfo
        {
            /// <summary>True when the document carries an encrypted payload (§7.6.7).</summary>
            public bool IsWrapper { get; }

            /// <summary>The payload file specification's name — <c>/UF</c>, else <c>/F</c>.</summary>
            public string PayloadFilename { get; }

            /// <summary>
            /// The cryptographic filter the payload is encrypted with — its <c>/EP /Subtype</c>
            /// (Table 28, required) — or null when the payload's file specification has no
            /// encrypted payload dictionary to say.
            /// </summary>
            public string Type { get; }

            public WrapperInfo(bool isWrapper, string filename, string type)
            {
                IsWrapper = isWrapper;
                PayloadFilename = filename;
                Type = type;
            }
        }

        public static WrapperInfo Inspect(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            using var doc = PdfDocument.Open(input);
            var catalog = doc.Catalog?.Dictionary;
            if (catalog == null) return new WrapperInfo(false, null, null);

            PdfDictionary payload = FindEncryptedPayload(doc, catalog);
            if (payload == null) return new WrapperInfo(false, null, null);

            string filter = doc.ResolveObject(payload[PdfName.EP]) is PdfDictionary ep
                ? PdfResolve.Name(doc, ep, PdfName.Subtype)?.Value
                : null;
            return new WrapperInfo(true, PdfAttachmentManager.FileSpecName(doc, payload), filter);
        }

        /// <summary>
        /// The file specification marked <c>/AFRelationship /EncryptedPayload</c>: the catalog's
        /// <c>/AF</c> first, where §7.6.7 requires it, then the <c>/EmbeddedFiles</c> name tree,
        /// where it is required too — a producer that wrote only one of the two still made a wrapper.
        /// </summary>
        private static PdfDictionary FindEncryptedPayload(PdfDocument doc, PdfDictionary catalog)
        {
            if (doc.ResolveObject(catalog[PdfName.AF]) is PdfArray af)
            {
                for (int i = 0; i < af.Count; i++)
                    if (doc.ResolveObject(af[i]) is PdfDictionary fs && IsEncryptedPayload(doc, fs))
                        return fs;
            }

            foreach (var fs in PdfAttachmentManager.EmbeddedFileSpecs(doc, catalog))
                if (IsEncryptedPayload(doc, fs)) return fs;

            return null;
        }

        private static bool IsEncryptedPayload(PdfDocument doc, PdfDictionary fileSpec)
            => PdfResolve.Name(doc, fileSpec, PdfName.AFRelationship) == PdfName.EncryptedPayload;
    }

    /// <summary>Reads GeoPDF viewports — a page's <c>/VP</c> array (ISO 32000-2 §12.9.1,
    /// Table 265) with each viewport's optional <c>/Measure</c> (§12.9, §12.10 for the
    /// geospatial subtype). Returns structured records; coordinate transformation is out of
    /// scope.</summary>
    public static class PdfGeoReader
    {
        public sealed class Viewport
        {
            public int PageIndex { get; }
            public float[] Bbox { get; }
            public PdfDictionary Measure { get; }
            public string MeasureSubtype { get; }

            public Viewport(int pageIndex, float[] bbox, PdfDictionary measure, string subtype)
            {
                PageIndex = pageIndex;
                Bbox = bbox;
                Measure = measure;
                MeasureSubtype = subtype;
            }
        }

        public static IReadOnlyList<Viewport> ReadViewports(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            using var doc = PdfDocument.Open(input);
            var list = new List<Viewport>();
            for (int i = 0; i < doc.Pages.Count; i++)
            {
                var vpArr = doc.ResolveObject(doc.Pages[i].Dictionary[PdfName.VP]) as PdfArray;
                if (vpArr == null) continue;
                for (int k = 0; k < vpArr.Count; k++)
                {
                    var vp = doc.ResolveObject(vpArr[k]) as PdfDictionary;
                    if (vp == null) continue;
                    var bboxArr = doc.ResolveObject(vp[PdfName.BBox]) as PdfArray;
                    float[] bbox = null;
                    if (bboxArr != null && bboxArr.Count >= 4)
                        bbox = new[]
                        {
                            AsFloat(doc, bboxArr[0]),
                            AsFloat(doc, bboxArr[1]),
                            AsFloat(doc, bboxArr[2]),
                            AsFloat(doc, bboxArr[3])
                        };
                    var measure = doc.ResolveObject(vp[PdfName.Measure]) as PdfDictionary;
                    // Resolved, like every other read in this walk. The indexer does not follow a
                    // reference, so `/Subtype 9 0 R` — legal under §7.3.10 — came back null and the
                    // viewport was reported with no measure subtype at all, which is
                    // indistinguishable from a /Measure that never named one.
                    string subtype = measure == null
                        ? null
                        : doc.ResolveObject(measure[PdfName.Subtype]) is PdfName sn ? sn.Value : null;
                    list.Add(new Viewport(i, bbox, measure, subtype));
                }
            }
            return list;
        }

        /// <summary>
        /// A <c>/BBox</c> coordinate, resolved — the last unresolved read in this walk: an indirect
        /// coordinate read as 0, which moved the viewport's corner to the page origin.
        /// </summary>
        private static float AsFloat(PdfDocument doc, PdfObject o)
            => PdfResolve.Number(doc, o) is PdfNumber n ? (float)n.DoubleValue : 0f;
    }
}
