// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.HtmlToPdf
{
    /// <summary>
    /// The medium a page's stylesheets are applied for — which <c>@media</c> rules, <c>media</c>
    /// attributes and <c>@import</c> conditions are in force.
    /// </summary>
    public enum HtmlMediaType
    {
        /// <summary>
        /// Print, as a browser's "Save as PDF" applies it: <c>print</c> and <c>all</c> match,
        /// <c>screen</c> does not. The default.
        /// </summary>
        Print = 0,

        /// <summary>
        /// Screen: the page as it looks in the browser window, on the same paper. <c>screen</c> and
        /// <c>all</c> match, <c>print</c> does not.
        /// </summary>
        Screen = 1,
    }
}
