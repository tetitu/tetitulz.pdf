// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.HtmlToPdf
{
    /// <summary>
    /// A common paper size for <see cref="HtmlToPdfOptions.PageSize"/>, portrait. Set
    /// <see cref="HtmlToPdfOptions.Landscape"/> to turn it; set <see cref="HtmlToPdfOptions.PageWidth"/>
    /// and <see cref="HtmlToPdfOptions.PageHeight"/> for any other size.
    /// </summary>
    /// <remarks>Members are appended, never inserted: a value is compiled into every caller.</remarks>
    public enum HtmlPageSize
    {
        /// <summary>ISO 216 A3, 297 × 420 mm.</summary>
        A3 = 1,

        /// <summary>ISO 216 A4, 210 × 297 mm.</summary>
        A4 = 2,

        /// <summary>ISO 216 A5, 148 × 210 mm.</summary>
        A5 = 3,

        /// <summary>ISO 216 B4, 250 × 353 mm.</summary>
        B4 = 4,

        /// <summary>ISO 216 B5, 176 × 250 mm.</summary>
        B5 = 5,

        /// <summary>US Letter, 8.5 × 11 in — the options' default.</summary>
        Letter = 6,

        /// <summary>US Legal, 8.5 × 14 in.</summary>
        Legal = 7,

        /// <summary>Tabloid (ANSI B), 11 × 17 in.</summary>
        Tabloid = 8,

        /// <summary>Executive, 7.25 × 10.5 in.</summary>
        Executive = 9,
    }
}
