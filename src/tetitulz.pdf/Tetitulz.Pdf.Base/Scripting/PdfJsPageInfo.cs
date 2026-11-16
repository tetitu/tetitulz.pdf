// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Tetitulz.Pdf.Base.Scripting
{
    /// <summary>
    /// What a document's scripts read of one page — <c>getPageRotation</c> and <c>getPageBox</c>:
    /// its <c>/Rotate</c>, and its five boxes as the document gives them, each defaulted as
    /// §14.11.2 defaults it (the crop box to the media box, the rest to the crop box) — the four
    /// numbers left, bottom, right, top, or null when the page states no box at all.
    /// </summary>
    public sealed class PdfJsPageInfo
    {
        /// <summary>The page's rotation: 0, 90, 180 or 270 (a value that is none of them reads as 0).</summary>
        public int Rotate { get; set; }

        /// <summary>The media box, left, bottom, right, top; null when the page tree states none.</summary>
        public IReadOnlyList<double> MediaBox { get; set; }

        /// <summary>The crop box, defaulted to the media box.</summary>
        public IReadOnlyList<double> CropBox { get; set; }

        /// <summary>The bleed box, defaulted to the crop box.</summary>
        public IReadOnlyList<double> BleedBox { get; set; }

        /// <summary>The trim box, defaulted to the crop box.</summary>
        public IReadOnlyList<double> TrimBox { get; set; }

        /// <summary>The art box, defaulted to the crop box.</summary>
        public IReadOnlyList<double> ArtBox { get; set; }
    }
}
