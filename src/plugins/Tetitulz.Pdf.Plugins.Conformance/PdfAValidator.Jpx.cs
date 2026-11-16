// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    public static partial class PdfAValidator
    {
        /// <summary>
        /// A24–A28 — ISO 19005-2/3 §6.2.8.3's rules for a <c>JPXDecode</c> image, as the published
        /// profile quotes them (rules 6.2.8.3-1 to -5), and ISO 19005-4 §6.2.7.3's, which the
        /// PDF/A-4 profile quotes in the same words (rules 6.2.7.3-1 to -5, "reader" become
        /// "processor"), for PDF/A-2, -3 and -4: the colour
        /// channels are 1, 3 or 4; with no <c>/ColorSpace</c> in the dictionary, several colour
        /// specifications carry exactly one with <c>APPROX</c> 1, every <c>METH</c> is 1, 2 or 3,
        /// and EnumCS 19 (CIEJab) is not used; the components share one bit depth of 1 to 38 (no
        /// <c>bpcc</c> box). Each code is reported once, at its first image; a page whose scan is
        /// partial reports A99 for the six.
        /// <para><b>A29 — HT code-blocks.</b> §6.2.8.3 is, as the profile quotes it, "JPEG2000 compression
        /// shall be used as specified in ISO 32000-1:2008"; ISO 32000-1 §7.4.9 limits PDF image data
        /// "to the JPX baseline set of features"; and T.801 (08/2002) M.9.2.3 has the baseline's
        /// codestream "compressed using the JPEG 2000 compression algorithm, as defined by ITU-T
        /// Rec. T.800 | ISO/IEC 15444-1", needing no extension but two Part 2 transforms. An HTJ2K
        /// codestream (ITU-T T.814) needs Part 15's block decoder: CAP's Pcap15, or code-block style
        /// bit 6, read by <see cref="JpxHeader.TryReadHighThroughput"/>. the published validation profile has no
        /// rule for it — the chain above is the authority, each step quoted from its text. At
        /// PDF/A-4 the same limit is PDF 2.0's own: ISO 32000-2 §7.4.9 keeps the sentence, "Data used
        /// in PDF image XObjects shall be limited to the JPX baseline set of features", so it is a
        /// base-PDF rule PDF/A-4 inherits, as PDF/X's X13 is (GAPS §12b).</para>
        /// The images and their headers are read by <see cref="JpxImageRules"/>, which PDF/X's
        /// X20–X23 read too — no sample is decoded.
        /// </summary>
        private static void CheckJpxImages(PdfDocument doc, PdfAConformancePart part, List<PdfAViolation> v)
        {
            var reported = new HashSet<string>();
            JpxImageRules.EachImage(doc, PdfXValidator.DefaultMaxPageContentBytes,
                PdfXValidator.DefaultMaxDocumentContentBytes,
                finished: () => reported.Count == 6,
                incomplete: (page, why) => ReportPartial(
                    $"Page {page}: the A24-A29 JPEG 2000 check did not run in full — {why}.",
                    "A24", "A25", "A26", "A27", "A28", "A29"),
                image: (xo, name, page) => CheckOneJpxImage(doc, xo, name, page, part, reported, v));
        }

        private static void CheckOneJpxImage(PdfDocument doc, PdfStream xo, PdfName name, int page,
            PdfAConformancePart part, HashSet<string> reported, List<PdfAViolation> v)
        {
            string level = part == PdfAConformancePart.Part4 ? "PDF/A-4" : part == PdfAConformancePart.Part3 ? "PDF/A-3" : "PDF/A-2";
            // "(ISO 19005-n §clause, rule clause-k)" for rule k of the five.
            string Rule(int k) => part == PdfAConformancePart.Part4
                ? $"(ISO 19005-4 §6.2.7.3, rule 6.2.7.3-{k})"
                : $"(ISO 19005-{(part == PdfAConformancePart.Part3 ? 3 : 2)} §6.2.8.3, rule 6.2.8.3-{k})";
            string baseline = part == PdfAConformancePart.Part4
                ? "ISO 32000-2 §7.4.9, ITU-T T.801 M.9.2.3"
                : $"ISO 19005-{(part == PdfAConformancePart.Part3 ? 3 : 2)} §6.2.8.3, ISO 32000-1 §7.4.9, ITU-T T.801 M.9.2.3";
            JpxImageRules.Reading r = JpxImageRules.TryRead(doc, xo, out string why);
            if (r == null)
            {
                Report("PdfAValidator.CheckJpxImages", $"page {page}, image /{name.Value}: {why}; A24-A28 not checked for it");
                return;
            }
            string where = $"JPEG 2000 image /{name.Value} on page {page}";

            // A29: HT code-blocks are outside the JPX baseline (see CheckJpxImages' remarks).
            if (r.HighThroughput)
                Once(reported, v, "A29", $"{level} admits only JPX-baseline JPEG 2000, whose codestreams are T.800's ({baseline}); the {where} uses HT code-blocks (ITU-T T.814 | ISO/IEC 15444-15, HTJ2K).");

            // 6.2.8.3-1: the colour channels — the header's count when it says, else every component.
            if (!r.ChannelCountAllowed)
                Once(reported, v, "A24", $"{level} requires a JPEG 2000 image's colour channels to number 1, 3 or 4; the {where} has {r.Channels} {Rule(1)}.");

            // 6.2.8.3-2 to -4 apply to a JP2 file's specifications when the dictionary names no /ColorSpace.
            // 6.2.8.3-2: several specifications, exactly one with APPROX 1.
            if (!r.SpecificationCountAllowed)
                Once(reported, v, "A25", $"{level} requires exactly one of several colour space specifications in a JPEG 2000 image to have APPROX 1 when the dictionary names no /ColorSpace; the {where} has {r.Specifications.Length} specifications, {r.ApproxOne} with APPROX 1 {Rule(2)}.");
            // 6.2.8.3-3: METH 1, 2 or 3.
            if (r.MethodOutsideTheThree is int method)
                Once(reported, v, "A26", $"{level} requires a JPEG 2000 image's colr box METH to be 1, 2 or 3 when the dictionary names no /ColorSpace; the {where} has METH {method} {Rule(3)}.");
            // 6.2.8.3-4: no CIEJab.
            if (r.NamesCieJab)
                Once(reported, v, "A27", $"{level} forbids JPEG 2000 enumerated colour space 19 (CIEJab); the {where} names it {Rule(4)}.");

            // 6.2.8.3-5: one bit depth, 1 to 38, across the colour channels; a bpcc box declares varying depths.
            if (!r.OneDepthInRange)
                Once(reported, v, "A28", $"{level} requires a JPEG 2000 image's colour channels to share one bit depth of 1 to 38; the {where} {r.Depths} {Rule(5)}.");
        }

        private static void Once(HashSet<string> reported, List<PdfAViolation> v, string code, string message)
        {
            if (reported.Add(code)) v.Add(new PdfAViolation(code, message));
        }
    }
}
