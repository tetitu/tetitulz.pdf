// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.HtmlToPdf
{
    /// <summary>
    /// Specifies which browser engine Playwright should use for rendering HTML to PDF.
    /// </summary>
    public enum BrowserEngine
    {
        /// <summary>
        /// Chromium-based engine. Best PDF fidelity. Default.
        /// </summary>
        Chromium = 0,

        /// <summary>
        /// Firefox engine. Good standards compliance.
        /// </summary>
        Firefox = 1,

        /// <summary>
        /// WebKit engine (Safari). Lightweight and fast.
        /// </summary>
        WebKit = 2
    }
}
