// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    public static partial class PdfAValidator
    {
        /// <summary>
        /// The XObject rules. A58 — an image: no <c>/Alternates</c>, no <c>/OPI</c>, no
        /// <c>/Interpolate true</c> (an inline image's <c>/I</c> too), a <c>/BitsPerComponent</c> of 1,
        /// 2, 4 or 8 (16 from PDF/A-2), and an image mask's, or a <c>/Mask</c> stencil's, of 1 (ISO
        /// 19005-1 6.2.4, 19005-2/3 6.2.8, 19005-4 6.2.7.1; the validation rules of those numbers, -1 to
        /// -5). A59 — a form: no <c>/OPI</c>, no <c>/Ref</c> (a reference XObject), and before PDF/A-4
        /// no <c>/PS</c> or <c>/Subtype2 /PS</c>; and before PDF/A-4 no PostScript XObject (19005-1
        /// 6.2.4-2, 6.2.5 to 6.2.7; 19005-2/3 6.2.9; 19005-4 6.2.8.1, 6.2.8.2). The XObjects are
        /// those the page's resource dictionaries name, used or not, as the profiles read them; the inline
        /// images those the page draws.
        /// </summary>
        private static void CheckXObjects(PdfDocument doc, PdfAConformancePart part, List<PdfAViolation> v)
        {
            if (doc?.Pages == null) return;
            bool part1 = part == PdfAConformancePart.Part1;
            bool part4 = part == PdfAConformancePart.Part4;
            var images = new Occurrences();
            var forms = new Occurrences();
            var seenXObjects = new HashSet<PdfStream>();
            var seenResources = new HashSet<PdfDictionary>();
            var seenStreams = new HashSet<PdfStream>();
            long documentBytes = 0;

            for (int p = 0; p < doc.Pages.Count; p++)
            {
                string page = "page " + (p + 1).ToString(CultureInfo.InvariantCulture);
                var scan = PdfXValidator.ScanPage(doc, doc.Pages[p],
                    PdfXValidator.DefaultMaxPageContentBytes, ref documentBytes,
                    PdfXValidator.DefaultMaxDocumentContentBytes);
                if (scan.Incomplete != null)
                    ReportPartial($"Page {p + 1}: the A58-A59 XObject checks did not run in full — {scan.Incomplete}.", "A58", "A59");

                foreach (var resources in scan.Resources)
                {
                    if (!ScanReuse.FirstVisit(seenResources, resources) || !(doc.ResolveObject(resources[PdfName.XObject]) is PdfDictionary xobjects))
                        continue;
                    foreach (var kvp in xobjects)
                    {
                        if (!(doc.ResolveObject(kvp.Value) is PdfStream xobject) || !seenXObjects.Add(xobject)) continue;
                        string where = page + ": /" + kvp.Key.Value;
                        string subtype = (doc.ResolveObject(xobject[PdfName.Subtype]) as PdfName)?.Value;
                        if (subtype == PdfName.Image.Value)
                            CheckImageDictionary(doc, xobject, where, part1, inline: false, images);
                        else if (subtype == PdfName.Form.Value)
                        {
                            if (xobject[PdfName.OPI] != null) forms.Add(where + " /OPI");
                            if (xobject[PdfName.Ref] != null) forms.Add(where + " /Ref (a reference XObject)");
                            if (!part4 && xobject[PdfName.PS] != null) forms.Add(where + " /PS");
                            if (!part4 && (doc.ResolveObject(xobject[PdfName.Subtype2]) as PdfName)?.Value == PdfName.PS.Value)
                                forms.Add(where + " /Subtype2 /PS");
                        }
                        else if (subtype == PdfName.PS.Value && !part4)
                            forms.Add(where + " is a PostScript XObject");
                    }
                }

                bool skip = false;
                string streamWhere = null;
                WalkDrawnOperations(doc, scan,
                    (i, initialState) =>
                    {
                        var source = scan.ContentStreamSources[i];
                        skip = source != null && !seenStreams.Add(source);
                        streamWhere = source == null ? page + " content"
                            : page + ", object " + source.ObjectNumber.ToString(CultureInfo.InvariantCulture);
                        return true;
                    },
                    (i, op) =>
                    {
                        if (!skip && op.Operator == PdfOperators.BeginInlineImage && op.Operands.Length > 0
                            && op.Operands[0] is PdfStream inlineImage)
                            CheckImageDictionary(doc, inlineImage, streamWhere + ": inline image", part1, inline: true, images);
                        return true;
                    });
            }

            string clause = part1 ? "ISO 19005-1 6.2.4; rules 6.2.4-1 to -5"
                : part4 ? "ISO 19005-4 6.2.7.1; rules 6.2.7.1-1 to -5"
                : $"ISO 19005-{(int)part} 6.2.8; rules 6.2.8-1 to -5";
            if (images.Count > 0)
                v.Add(new PdfAViolation("A58", $"An image dictionary holds what the part forbids: {images} ({clause})."));
            string formClause = part1 ? "ISO 19005-1 6.2.4 to 6.2.7; rules 6.2.4-2, 6.2.5-1, 6.2.6-1, 6.2.7-1"
                : part4 ? "ISO 19005-4 6.2.8.1, 6.2.8.2; rules 6.2.8.1-1, 6.2.8.2-1"
                : $"ISO 19005-{(int)part} 6.2.9; rules 6.2.9-1 to -3";
            if (forms.Count > 0)
                v.Add(new PdfAViolation("A59", $"An XObject is of a kind, or holds a key, the part forbids: {forms} ({formClause})."));
        }

        /// <summary>One image's dictionary — an XObject's, or an inline image's under its abbreviated keys too.</summary>
        private static void CheckImageDictionary(PdfDocument doc, PdfStream image, string where, bool part1, bool inline,
            Occurrences problems)
        {
            if (!inline)
            {
                if (image[PdfName.Alternates] != null) problems.Add(where + " /Alternates");
                if (image[PdfName.OPI] != null) problems.Add(where + " /OPI");
            }
            if ((doc.ResolveObject(image[PdfName.Interpolate] ?? (inline ? image[PdfName.I] : null)) as PdfBoolean)?.Value == true)
                problems.Add(where + (inline ? " /I true" : " /Interpolate true"));

            bool mask = (doc.ResolveObject(image[PdfName.ImageMask] ?? (inline ? image[PdfName.IM] : null)) as PdfBoolean)?.Value == true;
            int? depth = BitsPerComponentOf(doc, image, inline);
            if (mask)
            {
                if (depth != null && depth != 1) problems.Add($"{where} an image mask's /BitsPerComponent {Shown(depth.Value)}");
            }
            else if (depth != null && depth != 1 && depth != 2 && depth != 4 && depth != 8 && (part1 || depth != 16))
                problems.Add($"{where} /BitsPerComponent {Shown(depth.Value)}");

            if (!inline && doc.ResolveObject(image[PdfName.Mask]) is PdfStream stencil)
            {
                int? stencilDepth = BitsPerComponentOf(doc, stencil, inline: false);
                if (stencilDepth != null && stencilDepth != 1)
                    problems.Add($"{where} /Mask's /BitsPerComponent {Shown(stencilDepth.Value)}");
            }
        }

        private static int? BitsPerComponentOf(PdfDocument doc, PdfStream image, bool inline)
            => (doc.ResolveObject(image[PdfName.BitsPerComponent] ?? (inline ? image[PdfName.BPC] : null)) as PdfNumber)?.IntValue;

        private static string Shown(int value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
