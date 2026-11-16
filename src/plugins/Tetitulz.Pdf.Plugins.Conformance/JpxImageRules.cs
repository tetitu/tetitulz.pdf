// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// What a <c>JPXDecode</c> image's own header says to the rules PDF/A and PDF/X state for one —
    /// PDF/A's A24–A29 and PDF/X-4's X20–X23 alike: which images the pages reach, and for each its
    /// colour channels, its colour specifications (how many, their <c>APPROX</c> and <c>METH</c>,
    /// CIEJab), its bit depths, and whether it uses HT code-blocks. Each validator decides which
    /// answers are violations under its own clause and words its own message; the reading is one.
    /// Headers only: no sample is decoded.
    /// </summary>
    internal static class JpxImageRules
    {
        /// <summary>One image's answers.</summary>
        internal sealed class Reading
        {
            /// <summary>The colour channels: the header's count when it declares one (a <c>cdef</c>
            /// box typing a channel as opacity, a palette), else every component.</summary>
            internal int Channels;

            /// <summary>Every component's bit depth, from the SIZ.</summary>
            internal int[] Precision;

            /// <summary>Whether the colour-specification rules apply: a JP2 file whose dictionary
            /// names no <c>/ColorSpace</c>. Where the dictionary names one, "the colour space
            /// specifications in the JPEG 2000 data shall be ignored" (ISO 32000-2 §7.4.9).</summary>
            internal bool SpecificationsApply;

            /// <summary>The JP2 header's <c>colr</c> boxes, in file order; empty for a raw codestream.</summary>
            internal JpxColourSpecificationSummary[] Specifications;

            /// <summary>A JP2 <c>bpcc</c> box: the components declared at varying depths.</summary>
            internal bool BitsPerComponentBox;

            /// <summary>HT code-blocks (ITU-T T.814): CAP's Pcap15 or code-block style bit 6.</summary>
            internal bool HighThroughput;

            /// <summary>One, three or four colour channels.</summary>
            internal bool ChannelCountAllowed => Channels == 1 || Channels == 3 || Channels == 4;

            /// <summary>How many of <see cref="Specifications"/> carry <c>APPROX</c> 1.</summary>
            internal int ApproxOne
            {
                get
                {
                    int n = 0;
                    foreach (JpxColourSpecificationSummary s in Specifications) if (s.Approximation == 1) n++;
                    return n;
                }
            }

            /// <summary>Where they apply, several specifications carry exactly one with <c>APPROX</c> 1.</summary>
            internal bool SpecificationCountAllowed => !SpecificationsApply || Specifications.Length <= 1 || ApproxOne == 1;

            /// <summary>Where they apply, the first <c>METH</c> outside the enumerated, restricted-ICC
            /// and any-ICC methods (1, 2, 3); otherwise <c>null</c>.</summary>
            internal int? MethodOutsideTheThree
            {
                get
                {
                    if (!SpecificationsApply) return null;
                    foreach (JpxColourSpecificationSummary s in Specifications)
                        if (s.Method < 1 || s.Method > 3) return s.Method;
                    return null;
                }
            }

            /// <summary>Where they apply, an enumerated specification naming colour space 19 (CIEJab).</summary>
            internal bool NamesCieJab
            {
                get
                {
                    if (!SpecificationsApply) return false;
                    foreach (JpxColourSpecificationSummary s in Specifications)
                        if (s.Method == 1 && s.EnumeratedSpace == 19) return true;
                    return false;
                }
            }

            /// <summary>The colour channels share one bit depth of 1 to 38, and no <c>bpcc</c> box
            /// declares varying ones.</summary>
            internal bool OneDepthInRange
            {
                get
                {
                    if (BitsPerComponentBox) return false;
                    int compare = Channels > 0 && Channels <= Precision.Length ? Channels : Precision.Length;
                    for (int c = 0; c < compare; c++)
                        if (Precision[c] < 1 || Precision[c] > 38 || Precision[c] != Precision[0]) return false;
                    return true;
                }
            }

            /// <summary>The depths as a message states them, after "the image".</summary>
            internal string Depths => BitsPerComponentBox
                ? "declares varying depths in a bpcc box"
                : $"has depths {string.Join(", ", Precision)}";
        }

        /// <summary>
        /// Calls <paramref name="image"/> for every <c>JPXDecode</c> image XObject the pages reach —
        /// through <see cref="PdfXValidator.ScanPage"/>'s resource walk, so nested forms, patterns,
        /// Type 3 fonts and annotation appearances — once per stream, with its resource name and
        /// one-based page. A page whose scan stopped short is handed to <paramref name="incomplete"/>
        /// with the reason. <paramref name="finished"/> is asked before each page: a caller that has
        /// reported every rule it holds stops the walk.
        /// </summary>
        internal static void EachImage(PdfDocument doc, int maxPageContentBytes, long maxDocumentContentBytes,
            Func<bool> finished, Action<int, string> incomplete, Action<PdfStream, PdfName, int> image)
        {
            var seen = new HashSet<PdfStream>(PdfReferenceEquality.Instance);
            var seenResources = new HashSet<PdfDictionary>(PdfReferenceEquality.Instance);   // pages share them
            long documentBytes = 0;
            for (int i = 0; i < doc.Pages.Count && !finished(); i++)
            {
                var scan = PdfXValidator.ScanPage(doc, doc.Pages[i],
                    maxPageContentBytes, ref documentBytes, maxDocumentContentBytes);
                if (scan.Incomplete != null) incomplete(i + 1, scan.Incomplete);

                foreach (PdfDictionary res in scan.Resources)
                {
                    if (!seenResources.Add(res)) continue;
                    if (!(doc.ResolveObject(res[PdfName.XObject]) is PdfDictionary xoDict)) continue;
                    foreach (var kvp in xoDict)
                    {
                        if (!(doc.ResolveObject(kvp.Value) is PdfStream xo) || !seen.Add(xo)) continue;
                        if ((doc.ResolveObject(xo[PdfName.Subtype]) as PdfName)?.Value != "Image") continue;
                        if (!DeclaresJpx(doc, xo)) continue;
                        image(xo, kvp.Key, i + 1);
                    }
                }
            }
        }

        private static bool DeclaresJpx(PdfDocument doc, PdfStream xo)
        {
            PdfObject filter = doc.ResolveObject(xo[PdfName.Filter]);
            if (filter is PdfName one) return one.Value == PdfFilterNames.JpxDecode;
            if (filter is PdfArray many)
                foreach (PdfObject f in many)
                    if ((doc.ResolveObject(f) as PdfName)?.Value == PdfFilterNames.JpxDecode) return true;
            return false;
        }

        /// <summary>
        /// <paramref name="xo"/>'s answers, or <c>null</c> with <paramref name="why"/> saying what
        /// could not be read — its stream, or its SIZ.
        /// </summary>
        internal static Reading TryRead(PdfDocument doc, PdfStream xo, out string why)
        {
            why = null;
            byte[] data;
            try { data = xo.GetDecodedData(); }
            catch (PdfException ex)
            {
                why = $"its stream could not be decoded ({ex.Message})";
                return null;
            }
            if (!JpxHeader.TryReadImageSize(data, out _, out _, out int csiz)
                || !JpxHeader.TryReadComponentPrecision(data, out int[] precision, out _))
            {
                why = "its JPEG 2000 SIZ could not be read";
                return null;
            }

            bool jp2 = JpxHeader.TryReadColourSpecifications(data, out JpxColourSpecificationSummary[] specs, out bool bpccBox);
            bool hasColorSpace = doc.ResolveObject(xo[PdfName.ColorSpace] ?? xo[PdfName.CS]) != null;
            return new Reading
            {
                Channels = JpxHeader.TryReadColourComponents(data, out int declared) ? declared : csiz,
                Precision = precision,
                SpecificationsApply = jp2 && !hasColorSpace,
                Specifications = jp2 ? specs : Array.Empty<JpxColourSpecificationSummary>(),
                BitsPerComponentBox = jp2 && bpccBox,
                HighThroughput = JpxHeader.TryReadHighThroughput(data, out bool highThroughput) && highThroughput,
            };
        }
    }
}
