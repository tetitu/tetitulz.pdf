// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.DocxToPdf.Rendering
{
    /// <summary>
    /// Thin wrapper around <see cref="PdfContentStreamBuilder"/> that carries the
    /// page dimensions from <see cref="DocxToPdfOptions"/> and assembles the full
    /// set of indirect objects (fonts, images, content stream, page dictionary)
    /// required by <see cref="DocxRenderContext.AssemblePdf"/>.
    /// </summary>
    internal sealed class PdfPageBuilder
    {
        private readonly PdfContentStreamBuilder _builder = new PdfContentStreamBuilder();
        private readonly DocxToPdfOptions _options;
        private readonly double _pageWidth;
        private readonly double _pageHeight;
        private readonly List<(double x0, double y0, double x1, double y1, string uri)> _links =
            new List<(double, double, double, double, string)>();

        internal void AddLink(double x0, double y0, double x1, double y1, string uri)
            => _links.Add((x0, y0, x1, y1, uri));

        /// <summary>How many links the page holds: a mark <see cref="ShiftLinksFrom"/> moves the later ones from.</summary>
        internal int LinkCount => _links.Count;

        /// <summary>Moves every link registered since the <paramref name="index"/>th up by <paramref name="dy"/>.</summary>
        internal void ShiftLinksFrom(int index, double dy)
        {
            for (int i = Math.Max(0, index); i < _links.Count; i++)
            {
                var (x0, y0, x1, y1, uri) = _links[i];
                _links[i] = (x0, y0 + dy, x1, y1 + dy, uri);
            }
        }

        /// <summary>The length of the content written so far: a mark <see cref="TranslateContentFrom"/> moves the later content from.</summary>
        internal int ContentLength => _builder.ContentLength;

        /// <summary>
        /// Moves everything drawn since the mark <paramref name="from"/> by (<paramref name="dx"/>,
        /// <paramref name="dy"/>): the content is put under one translation, in its own graphics state,
        /// so a block laid out at one place is shown at another — a footer measured downward and
        /// shown risen to the footer distance.
        /// </summary>
        internal void TranslateContentFrom(int from, double dx, double dy)
        {
            if (from < 0 || from >= _builder.ContentLength) return;
            int tail = _builder.ContentLength;
            _builder.AppendRaw(string.Format(CultureInfo.InvariantCulture, "q\n1 0 0 1 {0:0.###} {1:0.###} cm\n", dx, dy));
            _builder.MoveTailTo(tail, from);
            _builder.AppendRaw("Q\n");
        }

        internal PdfPageBuilder(DocxToPdfOptions options, double pageWidth, double pageHeight)
        {
            _options    = options;
            _pageWidth  = pageWidth;
            _pageHeight = pageHeight;
        }

        // Delegate surface used by DocxRenderContext / renderers ---

        internal void AppendOperator(string op) => _builder.AppendRaw(op);

        internal string AddFont(string baseFontName) => _builder.AddStandardFont(baseFontName);

        // `softMask` is raw 8-bit alpha, one byte per pixel, or null for an opaque image. It goes
        // straight to the builder's eight-argument overload, which owns the compression and the
        // "a fully opaque mask is not written" rule — this wrapper must not re-decide either.
        internal string AddImage(byte[] data, int width, int height, string filter,
            string colorSpace = PdfColorSpaceNames.DeviceRGB, int bitsPerComponent = 8,
            byte[] softMask = null, double[] decode = null)
            => _builder.AddImage(data, width, height, filter, colorSpace, bitsPerComponent,
                                 decode, softMask);

        /// <summary>
        /// Registers a constant-alpha graphics state and returns its resource name, for the
        /// caller to emit as <c>/GSn gs</c>. Deduplicated by the builder, so asking repeatedly
        /// for one opacity costs one object.
        /// </summary>
        internal string AddExtGState(float? fillAlpha, float? strokeAlpha)
            => _builder.AddExtGState(fillAlpha, strokeAlpha);

        /// <summary>Registers a linear gradient; returns the name to emit as <c>/Shn sh</c>.</summary>
        internal string AddAxialShading(double x0, double y0, double x1, double y1,
            IReadOnlyList<PdfContentStreamBuilder.ShadingStop> stops)
            => _builder.AddAxialShading(x0, y0, x1, y1, stops);

        /// <summary>Registers a radial gradient; returns the name to emit as <c>/Shn sh</c>.</summary>
        internal string AddRadialShading(double cx, double cy, double radius,
            IReadOnlyList<PdfContentStreamBuilder.ShadingStop> stops)
            => _builder.AddRadialShading(cx, cy, radius, stops);

        /// <summary>Records that this page references a document-level embedded font.</summary>
        internal void UseEmbeddedFont(string resName) => _builder.UseEmbeddedFont(resName);

        /// <summary>Raises <paramref name="floor"/> to what this page's content stream uses (its forms' too).</summary>
        internal void RequireContentFeatures(PdfFeatureVersions.Floor floor) => _builder.RequireContentFeatures(floor);

        /// <summary>Deferred field substitution (e.g. NUMPAGES) in this page's content.</summary>
        internal void ReplaceInContent(string from, string to) => _builder.ReplaceInContent(from, to);

        /// <summary>
        /// Builds all indirect objects for this page.
        /// The last element in the returned list is always the page dictionary,
        /// matching the contract expected by <see cref="DocxRenderContext.AssemblePdf"/>.
        /// </summary>
        internal List<(int objNum, PdfObject obj)> BuildObjects(
            ref int nextObjectNumber,
            int pagesTreeObjNum,
            IReadOnlyDictionary<string, int> embeddedFontObjNums = null,
            int structParentsKey = -1,
            Tetitulz.Pdf.Core.Writing.ImageObjectShare images = null)
        {
            var (contentRef, resources, sideObjects) =
                _builder.BuildObjects(ref nextObjectNumber, _options.CompressStreams, embeddedFontObjNums, images);

            // Build the page dictionary and append it as the final element.
            int pageObjNum = nextObjectNumber++;

            var mediaBox = new PdfArray();
            mediaBox.Add(new PdfNumber(0));
            mediaBox.Add(new PdfNumber(0));
            mediaBox.Add(new PdfNumber(_pageWidth));
            mediaBox.Add(new PdfNumber(_pageHeight));

            var pageDict = new PdfDictionary();
            pageDict.Set(PdfName.Type, PdfName.Page);
            pageDict.Set(PdfName.Parent, new PdfReference(pagesTreeObjNum, 0));
            pageDict.Set(PdfName.MediaBox, mediaBox);
            pageDict.Set(PdfName.Contents, contentRef);
            pageDict.Set(PdfName.Resources, resources);
            // Tagged PDF: key into the structure-tree parent number tree for this page's MCIDs.
            if (structParentsKey >= 0)
                pageDict.Set(PdfName.StructParents, new PdfNumber(structParentsKey));
            pageDict.ObjectNumber = pageObjNum;
            pageDict.GenerationNumber = 0;

            // Link annotations (clickable hyperlinks).
            if (_links.Count > 0)
            {
                var annots = new PdfArray();
                foreach (var (x0, y0, x1, y1, uri) in _links)
                {
                    int annNum = nextObjectNumber++;
                    var action = new PdfDictionary();
                    action.Set(PdfName.Type, PdfName.Action);
                    if (uri != null && uri.StartsWith("#"))
                    {
                        // Internal link → GoTo a named destination. /D is a NAME because this
                        // converter writes the catalog /Dests dictionary, whose keys are names;
                        // Core's PdfDocumentBuilder writes the /Names /Dests tree and so uses a
                        // string. See LinkAnnotationBuilder for why they must not be unified.
                        action.Set(PdfName.S, PdfName.GoTo);
                        action.Set(PdfName.D, new PdfName(uri.Substring(1)));
                    }
                    else
                    {
                        action.Set(PdfName.S, PdfName.URI);
                        action.Set(PdfName.URI, new PdfString(uri ?? ""));
                    }
                    var ann = LinkAnnotationBuilder.Build(x0, y0, x1, y1, action, annNum);
                    sideObjects.Add((annNum, ann));
                    annots.Add(new PdfReference(annNum, 0));
                }
                pageDict.Set(PdfName.Annots, annots);
            }

            sideObjects.Add((pageObjNum, pageDict));
            return sideObjects;
        }
    }
}
