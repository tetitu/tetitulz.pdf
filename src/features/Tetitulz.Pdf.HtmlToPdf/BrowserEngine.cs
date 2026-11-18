// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.HtmlToPdf
{
    /// <summary>
    /// Specifies which browser engine Playwright should use for rendering HTML to PDF.
    /// </summary>
    /// <remarks>
    /// Chromium only: Playwright prints a PDF from headless Chromium alone ("PDF generation is
    /// only supported for Headless Chromium"), so no other browser is offered.
    /// </remarks>
    public enum BrowserEngine
    {
        /// <summary>
        /// Chromium, the one engine Playwright prints a PDF from. Default.
        /// </summary>
        Chromium = 0
    }
}
