// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Accessibility.Editing;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Accessibility.Fonts
{
    public static partial class PdfFontEmbeddingRemediator
    {
        // ================================================================
        //  Request construction
        // ================================================================

        private static FontEmbeddingRequest BuildRequest(PdfDocument doc, FontSite site, UsageScan usage)
        {
            var request = new FontEmbeddingRequest
            {
                BaseFont       = site.BaseFont,
                Standard14Name = site.Standard14Name,
                Subtype        = site.Subtype,
                IsSymbolic     = site.IsSymbolic,
                ResourceName   = site.ResourceName,
                PageIndex      = site.PageIndex,
            };

            // This flag is strictly about the *scan*: did every content stream that can reach this
            // font tokenise cleanly? Whether a code can be named as a character is a separate
            // question, settled later against the encoding the output will actually carry — see
            // TrySubset. Conflating the two produced a warning that blamed an unreadable stream for
            // an encoding gap.
            request.UsedTextIsComplete =
                !usage.Truncated && !usage.Incomplete.Contains(site.FontDict);

            if (!usage.CodesByFont.TryGetValue(site.FontDict, out HashSet<int> codes) || codes.Count == 0)
                return request;

            var ordered = new List<int>(codes);
            ordered.Sort();
            request.UsedCharacterCodes = ordered;

            // Codes are only meaningful as characters for a simple font, where one byte is one code.
            if (site.IsType0) return request;

            Dictionary<int, char> encoding = ResolveEffectiveEncoding(doc, site, out bool known);
            if (!known || encoding == null) return request;

            var chars = new List<char>(ordered.Count);
            foreach (int code in ordered)
                if (encoding.TryGetValue(code, out char c)) chars.Add(c);

            chars.Sort();
            request.UsedText = new string(chars.ToArray());
            return request;
        }
    }
}
