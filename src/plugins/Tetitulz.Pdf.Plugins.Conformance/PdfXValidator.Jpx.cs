// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    public static partial class PdfXValidator
    {
        /// <summary>The source every X20–X23 message names.</summary>
        private const string JpxRuleSource =
            "(a published PDF/X JPEG 2000 preflight check, its limits as ISO 19005-2 §6.2.8.3 states them)";

        /// <summary>
        /// X20–X23 (PDF/X-4) — a <c>JPXDecode</c> image's header: its colour channels number 1, 3
        /// or 4 (X20); with no <c>/ColorSpace</c> in the dictionary, several colour specifications
        /// carry exactly one with <c>APPROX</c> 1 (X21) and none names enumerated colour space 19,
        /// CIEJab (X22); the colour channels share one bit depth of 1 to 38, with no <c>bpcc</c>
        /// box (X23). Each code is reported once, at its first image; a page whose scan is partial
        /// reports X99.
        /// </summary>
        /// <remarks>
        /// <para><b>The source.</b> ISO 15930-7's own clause (§6.27) is not available. The
        /// owner allows this rule from a reference checker alone: a commercial preflight tool's
        /// preflight report help, "JPEG2000 compressed object is not PDF/X compliant"
        /// (its published help page),
        /// names the four conditions — "an invalid number of color channels", "an invalid number of
        /// color spaces", "an invalid enumerated color space (CIEJab)", "invalid bit depth" — without
        /// their limits. The limits are PDF/A-2's (ISO 19005-2 §6.2.8.3, as the published validation profile quotes
        /// it), whose five rules include these four, read by the same <see cref="JpxImageRules"/> as
        /// A24, A25, A27 and A28. PDF/A-2's fifth, the <c>METH</c> rule (A26), is not among the
        /// conditions that check names and is not checked here. CIEJab is excluded from PDF's JPX by
        /// ISO 32000-2 §7.4.9 as well.</para>
        /// <para><b>X-4 only.</b> PDF/X-1a and PDF/X-3 are PDF 1.3 and 1.4, which have no
        /// <c>JPXDecode</c>. Every image the pages reach is read, printing or not, as PDF/A's walk
        /// does: the rule is about the file's images, and that check names none it leaves out.</para>
        /// </remarks>
        private static void CheckJpxImages(PdfDocument doc, List<PdfXViolation> v, List<PdfXViolation> info,
            int maxPageContentBytes, long maxDocumentContentBytes)
        {
            var reported = new HashSet<string>(StringComparer.Ordinal);
            JpxImageRules.EachImage(doc, maxPageContentBytes, maxDocumentContentBytes,
                finished: () => reported.Count == 4,
                incomplete: (page, why) => info.Add(new PdfXViolation("X99",
                    $"Page {page}: the X20-X23 JPEG 2000 check did not run in full — {why}.")),
                image: (xo, name, page) => CheckOneJpxImage(doc, xo, name, page, reported, v));
        }

        private static void CheckOneJpxImage(PdfDocument doc, PdfStream xo, PdfName name, int page,
            HashSet<string> reported, List<PdfXViolation> v)
        {
            JpxImageRules.Reading r = JpxImageRules.TryRead(doc, xo, out string why);
            if (r == null)
            {
                Report("PdfXValidator.CheckJpxImages", $"page {page}, image /{name.Value}: {why}; X20-X23 not checked for it");
                return;
            }
            string where = $"JPEG 2000 image /{name.Value} on page {page}";

            if (!r.ChannelCountAllowed)
                Once(reported, v, "X20", $"PDF/X-4 requires a JPEG 2000 image's colour channels to number 1, 3 or 4; the {where} has {r.Channels} {JpxRuleSource}.");
            if (!r.SpecificationCountAllowed)
                Once(reported, v, "X21", $"PDF/X-4 requires exactly one of several colour space specifications in a JPEG 2000 image to have APPROX 1 when the dictionary names no /ColorSpace; the {where} has {r.Specifications.Length} specifications, {r.ApproxOne} with APPROX 1 {JpxRuleSource}.");
            if (r.NamesCieJab)
                Once(reported, v, "X22", $"PDF/X-4 forbids JPEG 2000 enumerated colour space 19 (CIEJab); the {where} names it {JpxRuleSource}.");
            if (!r.OneDepthInRange)
                Once(reported, v, "X23", $"PDF/X-4 requires a JPEG 2000 image's colour channels to share one bit depth of 1 to 38; the {where} {r.Depths} {JpxRuleSource}.");
        }

        private static void Once(HashSet<string> reported, List<PdfXViolation> v, string code, string message)
        {
            if (reported.Add(code)) v.Add(new PdfXViolation(code, message));
        }
    }
}
