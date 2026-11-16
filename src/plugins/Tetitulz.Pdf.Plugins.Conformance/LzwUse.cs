// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// Where a document uses LZW compression: a stream's <c>/Filter</c>, or an inline image's
    /// <c>/F</c> or <c>/Filter</c>. PDF/X (X15) and PDF/A (A30) forbid it, and ask through here.
    /// </summary>
    internal static class LzwUse
    {
        /// <summary>
        /// Every stream object the cross-reference table lists, in its order; streams are never in
        /// object streams (§7.5.7), so this reaches all of them, whether anything refers to one or
        /// not. Returns how many objects the parser could not read, which a caller must report:
        /// neither rule can say what such an object holds.
        /// </summary>
        internal static int EachStream(PdfDocument doc, Action<int, PdfStream> visit)
        {
            int unreadable = 0;
            if (doc.Parser == null) return 0;
            foreach (var kvp in doc.Parser.EnumerateAllObjects((number, ex) => unreadable++))
                if (kvp.Value is PdfStream stream) visit(kvp.Key, stream);
            return unreadable;
        }

        /// <summary>Whether a <c>/Filter</c> value — a name, or an array of them — names LZW, in
        /// either spelling.</summary>
        /// <remarks>
        /// No reference is resolved here, because none can arrive: the parser resolves an indirect
        /// <c>/Filter</c>, and each reference inside a <c>/Filter</c> array, on every stream it
        /// reads (<c>PdfParser</c>, so that decoding never meets one), and an inline image's
        /// dictionary holds none. The resolution this had was a line that did nothing — the sweep's
        /// survivor — and <c>AnIndirectFilter_ReportsA30</c> pins the parser's half.
        /// </remarks>
        internal static bool NamesLzw(PdfObject filter)
        {
            if (filter is PdfName name) return PdfFilterNames.IsLzw(name.Value);
            if (!(filter is PdfArray chain)) return false;
            for (int i = 0; i < chain.Count; i++)
                if (chain[i] is PdfName n && PdfFilterNames.IsLzw(n.Value)) return true;
            return false;
        }

        /// <summary>Whether an inline image in <paramref name="content"/> is LZW-compressed.</summary>
        internal static bool InlineImageUsesLzw(byte[] content)
        {
            foreach (PdfObject filter in InlineImageFilters(content))
                if (NamesLzw(filter)) return true;
            return false;
        }

        /// <summary>
        /// Each inline image's filter value in <paramref name="content"/>, in order — a name, an
        /// array, or <c>null</c> for an unfiltered image. A30 and A31 both read through here.
        /// </summary>
        internal static IEnumerable<PdfObject> InlineImageFilters(byte[] content)
        {
            foreach (var op in ScanReuse.Parse(content))
            {
                if (op.Operator != PdfOperators.BeginInlineImage) continue;
                // The parser hands back the image as a stream whose dictionary holds the keys as
                // written, so the abbreviation /F and the full /Filter both have to be tried.
                var img = op.Operands.Length > 0 ? op.Operands[0] as PdfStream : null;
                if (img != null) yield return img[PdfName.F] ?? img[PdfName.Filter];
            }
        }
    }
}
