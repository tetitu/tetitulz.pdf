// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Plugins.PostScript.Document
{
    /// <summary>
    /// Writes collected pages out as a complete PDF, through <c>Core.Writing.PdfWriter</c>.
    ///
    /// <para><b>Why the low-level writer and not <c>PdfDocumentBuilder</c>.</b> The builder owns its
    /// pages' <c>/Resources</c>: it derives them from what its own content-stream API registered, so
    /// content appended with <c>AppendRaw</c> gets an <i>empty</i> resource dictionary. This
    /// converter's emitter produces the operator text itself — including <c>/F1 1 Tf</c> and
    /// <c>/Im1 Do</c> — so its resources have to travel with it. Building the page objects directly
    /// is what lets them.</para>
    ///
    /// <para>The plugin cannot mint object numbers into a <i>host</i> document, which is why the
    /// fragment path returns direct objects; here the file is ours, so the direct streams and
    /// dictionaries the collector built are <b>promoted to indirect objects</b> as they are
    /// written.</para>
    /// </summary>
    internal static class PostScriptDocumentWriter
    {
        /// <summary>Writes <paramref name="pages"/> to <paramref name="output"/>.</summary>
        /// <returns>The number of pages written.</returns>
        public static int Write(IReadOnlyList<PostScriptPage> pages, Stream output,
                                PsBoundingBox? declaredBox)
        {
            var effectivePages = pages;
            if (effectivePages == null || effectivePages.Count == 0)
            {
                // A source that drew nothing still has to produce a valid PDF — a zero-page file is
                // not one, and a caller of Import expects something it can open.
                PostScriptDiagnostics.Report("PostScriptDocumentWriter",
                    "the PostScript produced no pages; a single blank page was written");
                effectivePages = new[] { new PostScriptPage() };
            }

            // Every object is built before the header, which names what they use: 1.4 raised to the
            // floor read off them (a 16-bit image, `1 1 16 … image`, is PDF 1.5).
            var objects = new List<(int objNum, PdfObject obj)>();
            int nextObjNum = 1;

            // Object numbers are laid out before anything is written: a page dictionary has to
            // reference its content stream and its resources, and the /Pages root has to list every
            // page, so all of them must be known up front.
            int catalogNum   = nextObjNum++;
            int pagesRootNum = nextObjNum++;

            var pageNums    = new int[effectivePages.Count];
            var contentNums = new int[effectivePages.Count];
            var pageResourceNums = new int[effectivePages.Count];

            // One context for the whole file, not one per page: every page holds the SAME resource
            // dictionary instance (PostScriptPageCollector.AttachResources), so memoising by
            // reference identity collapses all N pages onto one set of objects.
            var promotion = new ResourcePromoter.Context { NextObjNum = nextObjNum };

            for (int i = 0; i < effectivePages.Count; i++)
            {
                pageNums[i]    = promotion.NextObjNum++;
                contentNums[i] = promotion.NextObjNum++;
                pageResourceNums[i] = ResourcePromoter.Promote(effectivePages[i].Resources, promotion);
            }

            nextObjNum = promotion.NextObjNum;
            var resourceObjects = promotion.Collected;

            // ---- catalog and page tree ----

            var catalog = new PdfDictionary();
            catalog[PdfName.Type]  = PdfName.Catalog;
            catalog[PdfName.Pages] = new PdfReference(pagesRootNum, 0);
            objects.Add((catalogNum, catalog));

            var kids = new PdfArray();
            foreach (int n in pageNums) kids.Add(new PdfReference(n, 0));

            var pagesRoot = new PdfDictionary();
            pagesRoot[PdfName.Type]  = PdfName.Pages;
            pagesRoot[PdfName.Kids]  = kids;
            pagesRoot[PdfName.Count] = new PdfNumber(effectivePages.Count);
            objects.Add((pagesRootNum, pagesRoot));

            // ---- pages ----

            for (int i = 0; i < effectivePages.Count; i++)
            {
                var page = effectivePages[i];
                var (width, height) = page.Media is { IsValid: true } media ? (media.Width, media.Height)
                    : PageSize(page.DrawnExtent, declaredBox);
                var origin = page.Media is { IsValid: true } ? (0.0, 0.0) : Origin(page.DrawnExtent, declaredBox);

                byte[] content = Turned(WithOriginShift(page.Content, origin), page.Rotate, width, height);
                if (page.Rotate is 90 or 270) (width, height) = (height, width);

                var contentStream = new PdfStream();
                contentStream[PdfName.Length] = new PdfNumber(content.Length);
                contentStream.RawData = content;
                objects.Add((contentNums[i], Deflated(contentStream)));

                var mediaBox = new PdfArray();
                mediaBox.Add(new PdfNumber(0));
                mediaBox.Add(new PdfNumber(0));
                mediaBox.Add(new PdfNumber(width));
                mediaBox.Add(new PdfNumber(height));

                var pageDict = new PdfDictionary();
                pageDict[PdfName.Type]     = PdfName.Page;
                pageDict[PdfName.Parent]   = new PdfReference(pagesRootNum, 0);
                pageDict[PdfName.MediaBox] = mediaBox;
                pageDict[PdfName.Contents] = new PdfReference(contentNums[i], 0);
                pageDict[PdfName.Resources] = pageResourceNums[i] != 0
                    ? new PdfReference(pageResourceNums[i], 0)
                    : (PdfObject)new PdfDictionary();

                objects.Add((pageNums[i], pageDict));
            }

            // ---- promoted resources ----

            foreach (var (num, obj) in resourceObjects)
                objects.Add((num, Deflated(obj)));

            // ---- header, objects, xref and trailer ----

            var trailer = new PdfDictionary();
            trailer[PdfName.Size] = new PdfNumber(nextObjNum);
            trailer[PdfName.Root] = new PdfReference(catalogNum, 0);

            var floor = new PdfFeatureVersions.Floor();
            foreach (var (_, obj) in objects) PdfFeatureVersions.Scan(obj, floor);

            var writer = new PdfWriter(output);
            writer.WriteHeader(floor.HeaderFor(PdfVersion.Pdf14));
            var offsets = new List<(int objNum, long offset)>();
            foreach (var (num, obj) in objects)
                offsets.Add((num, writer.WriteIndirectObject(num, 0, obj)));

            long xrefOffset = writer.WriteXrefTable(offsets);
            writer.WriteTrailer(trailer, xrefOffset);

            // The counter PdfWriter grew for THIS caller, finally read. Its own doc names us:
            // "a caller that builds its own object graph must promote its streams (see
            // ResourcePromoter in the PostScript plugin) and can check this afterwards to know
            // whether it missed one." Nothing read it, so the exact defect it was added for — a
            // /FontFile serialised as dictionary entries with the program dropped, every glyph
            // missing — would have recurred silently, with this method still returning a page
            // count as though it had written a complete file.
            if (writer.NestedStreamsWrittenWithoutData > 0)
                PostScriptDiagnostics.Report("PostScriptDocumentWriter",
                    $"{writer.NestedStreamsWrittenWithoutData} stream(s) were serialised nested and " +
                    "lost their data: a resource was not promoted to an indirect object, so the " +
                    "output is missing content a reader needs (most likely an embedded font " +
                    "program, whose glyphs will not draw)");

            return effectivePages.Count;
        }

        // Promotion is ResourcePromoter's, which descends every level: a walk of one level would
        // promote /XObject -> Im1 (a stream) and not /Font -> F1 -> /FontDescriptor -> /FontFile,
        // leaving the font program direct — and PdfWriter serialises a nested stream as dictionary
        // entries with its data dropped, so every embedded font would ship a /FontFile with no
        // program in it.

        /// <summary>
        /// A stream the conversion built raw — a page's content, an image's samples, a font program —
        /// written deflated (Core's <c>FlateEncodeFilter</c>) where that is smaller: a printed page's
        /// picture is megabytes of samples. One with a <c>/Filter</c> of its own (a JPEG's codestream)
        /// is written as it is; a font program's <c>/Length1</c> is its decoded length, as ISO 32000
        /// Table 125 has it, whatever the stream's filter.
        /// </summary>
        private static PdfObject Deflated(PdfObject value)
        {
            if (value is not PdfStream stream || stream.ContainsKey(PdfName.Filter) || stream.RawData is not { Length: > 0 } raw)
                return value;
            byte[] packed = Core.IO.Filters.FlateEncodeFilter.Encode(raw);
            if (packed.Length >= raw.Length) return value;
            stream.RawData = packed;
            stream[PdfName.Filter] = PdfName.FlateDecode;
            stream[PdfName.Length] = new PdfNumber(packed.Length);
            return stream;
        }

        /// <summary>
        /// <paramref name="content"/>, drawn on a <paramref name="width"/> × <paramref name="height"/>
        /// box, turned clockwise by <paramref name="degrees"/> (0, 90, 180, 270) onto the box that
        /// turn makes — not a <c>/Rotate</c>, which turns only the view: the page's own space is
        /// then the reader's, so its text runs left to right for an extractor as for the eye.
        /// </summary>
        private static byte[] Turned(byte[] content, int degrees, double width, double height)
        {
            string turn = degrees switch
            {
                90  => $"0 -1 1 0 0 {Fmt(width)}",
                180 => $"-1 0 0 -1 {Fmt(width)} {Fmt(height)}",
                270 => $"0 1 -1 0 {Fmt(height)} 0",
                _   => null,
            };
            if (turn == null) return content;
            string prefix = turn + " " + PdfOperators.ConcatMatrix + "\n";
            var combined = new byte[prefix.Length + content.Length];
            for (int i = 0; i < prefix.Length; i++) combined[i] = (byte)prefix[i];
            Buffer.BlockCopy(content, 0, combined, prefix.Length, content.Length);
            return combined;
        }

        /// <summary>
        /// Prepends the origin shift, so artwork whose coordinates sit far from the origin — which
        /// is most EPS — lands on the page instead of off it.
        /// </summary>
        private static byte[] WithOriginShift(byte[] content, (double X, double Y) origin)
        {
            if (origin.X == 0 && origin.Y == 0) return content ?? Array.Empty<byte>();

            string prefix = $"1 0 0 1 {Fmt(-origin.X)} {Fmt(-origin.Y)} {PdfOperators.ConcatMatrix}\n";
            var prefixBytes = new byte[prefix.Length];
            for (int i = 0; i < prefix.Length; i++) prefixBytes[i] = (byte)prefix[i];

            content ??= Array.Empty<byte>();
            var combined = new byte[prefixBytes.Length + content.Length];
            Buffer.BlockCopy(prefixBytes, 0, combined, 0, prefixBytes.Length);
            Buffer.BlockCopy(content, 0, combined, prefixBytes.Length, content.Length);
            return combined;
        }

        /// <summary>
        /// The page size: the declared <c>%%BoundingBox</c> wins, then the measured extent, then
        /// Letter.
        ///
        /// <para>The declared box is preferred even when marks fall outside it, because that is what
        /// the document <i>says</i> its extent is — a viewer that trusted the marks instead would
        /// disagree with every other consumer of the same file.</para>
        /// </summary>
        private static (double Width, double Height) PageSize(PsBoundingBox? extent,
                                                              PsBoundingBox? declared)
        {
            if (declared is { IsValid: true } d) return (d.Width, d.Height);
            if (extent is { IsValid: true } e) return (e.Width, e.Height);
            return (PdfPageSizes.LetterWidth, PdfPageSizes.LetterHeight);
        }

        private static (double X, double Y) Origin(PsBoundingBox? extent, PsBoundingBox? declared)
        {
            if (declared is { IsValid: true } d) return (d.LowerLeftX, d.LowerLeftY);
            if (extent is { IsValid: true } e) return (e.LowerLeftX, e.LowerLeftY);
            return (0, 0);
        }

        private static string Fmt(double v) => PdfContentStreamBuilder.FormatNumber(v);
    }
}
