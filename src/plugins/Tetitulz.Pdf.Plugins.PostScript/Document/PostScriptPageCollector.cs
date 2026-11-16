// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.PostScript.Document
{
    /// <summary>One converted page: its content stream, its resources and the extent actually drawn.</summary>
    internal sealed class PostScriptPage
    {
        public byte[] Content { get; init; } = Array.Empty<byte>();
        public PdfDictionary Resources { get; set; }
        public PsBoundingBox? DrawnExtent { get; init; }

        /// <summary>The medium a page device named for this page, or null; see <c>PsGraphicsEngine.PageMedia</c>.</summary>
        public PsBoundingBox? Media { get; init; }

        /// <summary>The clockwise turn, in degrees, that makes the page's text read left to right: 0, 90, 180 or 270.</summary>
        public int Rotate { get; init; }
    }

    /// <summary>
    /// Collects the pages a document conversion produces, one per <c>showpage</c>.
    ///
    /// <para>Bounded by <see cref="PsLimits.MaxPages"/> and
    /// <see cref="PsLimits.MaxEmittedDocumentBytes"/>: <c>{ showpage } loop</c> is a page bomb, and
    /// each page costs a content stream, a page dictionary and a resource dictionary even when it
    /// is empty.</para>
    /// </summary>
    internal sealed class PostScriptPageCollector
    {
        private readonly List<PostScriptPage> _pages = new();
        private readonly int _maxPages;
        private readonly long _maxTotalBytes;
        private long _totalBytes;

        public PostScriptPageCollector(int maxPages = -1,
                                       long maxTotalBytes = -1)
        {
            _maxPages = maxPages < 0 ? PsLimits.MaxPages : maxPages;
            _maxTotalBytes = maxTotalBytes < 0 ? PsLimits.MaxEmittedDocumentBytes : maxTotalBytes;
        }

        public IReadOnlyList<PostScriptPage> Pages => _pages;

        /// <summary>True once a cap stopped further pages being collected.</summary>
        public bool LimitReached { get; private set; }

        /// <summary>
        /// Gives every collected page the same resource dictionary.
        ///
        /// <para>Called once at the end because the graphics engine accumulates resources across the
        /// whole conversion, not per page. Over-declaring is legal and cheap; the alternative —
        /// tracking which page first named each font or image — buys nothing a reader can see.</para>
        /// </summary>
        public void AttachResources(PdfDictionary resources)
        {
            if (resources == null) return;
            foreach (var page in _pages) page.Resources = resources;
        }

        /// <summary>
        /// Adds a page. An <b>empty</b> page is kept rather than dropped: a document that calls
        /// <c>showpage</c> without drawing meant to emit a blank sheet, and silently dropping it
        /// would renumber everything after it.
        /// </summary>
        public void Add(byte[] content, PsBoundingBox? extent, PdfDictionary resources = null, PsBoundingBox? media = null,
                        int rotate = 0)
        {
            if (LimitReached) return;

            if (_pages.Count >= _maxPages)
            {
                LimitReached = true;
                PostScriptDiagnostics.Report("PostScriptPageCollector",
                    $"the {_maxPages}-page cap was reached; later pages were discarded");
                return;
            }

            content ??= Array.Empty<byte>();
            if (_totalBytes + content.Length > _maxTotalBytes)
            {
                LimitReached = true;
                PostScriptDiagnostics.Report("PostScriptPageCollector",
                    $"the {_maxTotalBytes}-byte document cap was reached at page {_pages.Count + 1}; " +
                    "later pages were discarded");
                return;
            }

            _totalBytes += content.Length;
            _pages.Add(new PostScriptPage
            {
                Content = content,
                Resources = resources,
                DrawnExtent = extent,
                Media = media,
                Rotate = rotate
            });
        }
    }
}
