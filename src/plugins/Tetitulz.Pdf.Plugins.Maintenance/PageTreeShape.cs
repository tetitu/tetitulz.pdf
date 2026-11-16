// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Maintenance
{
    /// <summary>
    /// Decides whether a page-tree node is a leaf page, by the same rule
    /// <c>Tetitulz.Pdf.Core.Document.PdfPageTree</c> uses. Core's copy is <c>internal</c> to that
    /// assembly, so a plugin cannot call it. Reached by <see cref="FontGlyphUsageScanner"/>,
    /// <see cref="ImagePlacementScanner"/> and <c>PdfOptimizer.CollectPageObjectNumbers</c> alike,
    /// so those three cannot drift on which pages exist.
    ///
    /// <para><b>Why the test is not simply <c>/Type != /Pages</c>.</b> §7.7.3.2 requires <c>/Type</c>
    /// on an intermediate node, but real producers omit it or write it as a literal string, and Core
    /// reads those files anyway: it recognises a leaf <i>structurally</i>, by the absence of
    /// <c>/Kids</c>. Classifying anything not saying <c>/Type /Pages</c> as a leaf would silently drop
    /// an untyped intermediate node's entire subtree.</para>
    ///
    /// <para><b>That would not be a missed optimization.</b> Both scanners feed decisions taken on the
    /// evidence they gather — the glyphs a font program may keep, the largest size an image is drawn
    /// at — so a page never walked reads as a page that draws nothing, and a font program reached from
    /// both a walked page and a skipped one would be pruned to the walked page's glyphs. The
    /// optimizer's page-scoped options (<c>RemoveThumbnails</c>, <c>ConcatenateContentStreams</c>,
    /// <c>CleanContentStreams</c>) would skip such a page while reporting a successful run.</para>
    /// </summary>
    internal static class PageTreeShape
    {
        /// <summary>
        /// True when this node is a page rather than an intermediate node.
        /// </summary>
        /// <remarks>
        /// <para>An explicit <c>/Type /Page</c> wins outright. Otherwise a node with no
        /// <c>/Kids</c> but with <c>/Contents</c> or a <c>/MediaBox</c> is a page. A node that is
        /// neither — no <c>/Kids</c> and nothing page-shaped — contributes nothing.</para>
        ///
        /// <para><b>And a node that positively declares <c>/Type /Pages</c> is excluded from the
        /// structural clause</b>, which exists for files that OMIT <c>/Type</c>. Without that,
        /// <c>&lt;&lt; /Type /Pages /Count 0 /MediaBox […] &gt;&gt;</c> — an emptied branch, what a
        /// page-removal tool leaves behind when it hoists the box — is counted as a page.</para>
        ///
        /// <para><b>A forwarder.</b> The rule is <c>Core.Document.PdfPageTreeShape</c>'s, which is
        /// public so a plugin can reach it; <c>PageTreeShapeParityTests</c> guards the
        /// forwarding.</para>
        /// </remarks>
        internal static bool IsLeafPage(PdfName type, PdfArray kids, bool hasContents, bool hasMediaBox)
            => Core.Document.PdfPageTreeShape.IsLeafPage(type, kids, hasContents, hasMediaBox);
    }
}
