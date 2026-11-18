// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.HtmlToPdf
{
    /// <summary>
    /// Selects how HTML is rendered to PDF.
    /// </summary>
    public enum HtmlRenderEngine
    {
        /// <summary>
        /// Render with a headless browser (Playwright driving headless Chromium). Full CSS,
        /// JavaScript and web-font fidelity. Requires the Playwright browsers to be installed
        /// (<c>playwright install</c>) and can fetch remote resources. <b>Not the default</b> — see the remarks on <c>HtmlToPdfOptions.RenderEngine</c>.
        /// </summary>
        Browser,

        /// <summary>
        /// Render with the pure-managed engine: the HTML is parsed (AngleSharp) and mapped to
        /// the centralized <c>Tetitulz.Pdf.Builder</c> document model, then rendered by the
        /// shared PDF exporter (the "html2openxml, but to PDF" path). No browser, no
        /// JavaScript, no network. Covers static HTML + inline CSS to roughly html2openxml's
        /// level: headings, paragraphs, <c>div</c>/<c>section</c>, lists (<c>ol type/start</c>,
        /// <c>ul</c> bullet styles, nesting), tables (<c>colspan</c>, <c>rowspan</c>, borders,
        /// cell vertical-align / shading / width, <c>caption</c>, header-row repeat),
        /// <c>blockquote</c>, <c>pre</c>/<c>code</c> (monospace + preserved whitespace),
        /// <c>hr</c>, <c>dl</c>/<c>dt</c>/<c>dd</c>, <c>display:flex</c>, <c>float</c>/<c>clear</c>,
        /// <c>b/strong</c>, <c>i/em</c>, <c>u/ins</c>,
        /// <c>s/strike/del</c>, <c>sup/sub</c>, <c>mark</c>, <c>small/big</c>, the legacy
        /// <c>font</c> tag, links, data-URI images and RTL. Inline CSS: colour,
        /// background-color, font size/weight/style/family, text-decoration, text-transform,
        /// font-variant (small-caps), letter-spacing, vertical-align, text-align, direction,
        /// and block margin/padding/border. Use <see cref="Browser"/> for complex pages
        /// (external/＜style＞ CSS cascade, flexbox/grid/float, JavaScript, web fonts, SVG).
        /// </summary>
        Managed,
    }
}
