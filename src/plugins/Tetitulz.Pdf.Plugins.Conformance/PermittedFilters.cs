// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// Which filters PDF/A-2, -3 and -4 permit beyond the LZW ban (A31). the published validation profiles state the
    /// rules:
    /// <list type="bullet">
    ///   <item>Streams (rule 6.1.7.2-1, PDF/A-4 6.1.6.2-1): "All standard stream filters listed in
    ///   ISO 32000-1:2008, 7.4, Table 6 may be used, with the exception of LZWDecode. In addition, the
    ///   Crypt filter shall not be used unless the value of the Name key in the decode parameters
    ///   dictionary is Identity. Filters that are not listed in ISO 32000-1:2008, 7.4, Table 6 shall
    ///   not be used." The test condition admits the full names only.</item>
    ///   <item>Inline images (rule 6.1.10-1, PDF/A-4 6.1.9-1): "The value of the F key in the Inline
    ///   Image dictionary shall not be LZW, LZWDecode, Crypt, a value not listed in ISO 32000-1:2008,
    ///   Table 6, or an array containing any such value." The test condition admits ASCIIHex,
    ///   ASCII85, Flate, RunLength, CCITTFax and DCT, in either spelling — not JBIG2 or JPX.</item>
    /// </list>
    /// LZW is left out of both answers: A30 reports it, through <see cref="LzwUse"/>.
    /// </summary>
    internal static class PermittedFilters
    {
        /// <summary>The first filter in <paramref name="stream"/>'s chain the rule forbids, or
        /// <c>null</c>. A <c>/Crypt</c> is permitted only where <see cref="StreamCryptFilter"/> reads
        /// it — first in the chain, §7.4.10's own requirement — and names <c>/Identity</c>.</summary>
        internal static string FirstUnpermittedInStream(PdfStream stream)
        {
            PdfObject filter = stream[PdfName.Filter];
            if (filter is PdfName single) return UnpermittedInStream(single.Value, stream);
            if (!(filter is PdfArray chain)) return null;
            for (int i = 0; i < chain.Count; i++)
            {
                if (!(chain[i] is PdfName n)) continue;
                string found = UnpermittedInStream(n.Value, stream);
                if (found != null) return found;
            }
            return null;
        }

        private static string UnpermittedInStream(string name, PdfStream stream)
        {
            switch (name)
            {
                case PdfFilterNames.AsciiHexDecode:
                case PdfFilterNames.Ascii85Decode:
                case PdfFilterNames.LzwDecode:      // A30's
                case PdfFilterNames.FlateDecode:
                case PdfFilterNames.RunLengthDecode:
                case PdfFilterNames.CcittFaxDecode:
                case PdfFilterNames.Jbig2Decode:
                case PdfFilterNames.DctDecode:
                case PdfFilterNames.JpxDecode:
                    return null;
                case PdfFilterNames.Crypt:
                    return StreamCryptFilter.TryGetName(stream, out PdfName cf) && cf == PdfName.Identity
                        ? null
                        : name;
                default:
                    return name;
            }
        }

        /// <summary>The first filter an inline image in <paramref name="content"/> names that the rule
        /// forbids, or <c>null</c>, reading each image's filter through
        /// <see cref="LzwUse.InlineImageFilters"/>.</summary>
        internal static string FirstUnpermittedInInlineImage(byte[] content)
        {
            foreach (PdfObject filter in LzwUse.InlineImageFilters(content))
            {
                if (filter is PdfName single && !PermittedInline(single.Value)) return single.Value;
                if (filter is PdfArray chain)
                    for (int i = 0; i < chain.Count; i++)
                        if (chain[i] is PdfName n && !PermittedInline(n.Value)) return n.Value;
            }
            return null;
        }

        private static bool PermittedInline(string name)
        {
            switch (name)
            {
                case PdfFilterNames.AsciiHexDecode: case PdfFilterNames.AsciiHexAbbrev:
                case PdfFilterNames.Ascii85Decode: case PdfFilterNames.Ascii85Abbrev:
                case PdfFilterNames.LzwDecode: case PdfFilterNames.LzwAbbrev:   // A30's
                case PdfFilterNames.FlateDecode: case PdfFilterNames.FlateAbbrev:
                case PdfFilterNames.RunLengthDecode: case PdfFilterNames.RunLengthAbbrev:
                case PdfFilterNames.CcittFaxDecode: case PdfFilterNames.CcittFaxAbbrev:
                case PdfFilterNames.DctDecode: case PdfFilterNames.DctAbbrev:
                    return true;
                default:
                    return false;
            }
        }
    }
}
