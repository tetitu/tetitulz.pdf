// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.PageEdits
{
    /// <summary>
    /// <see cref="ISplitPlugin"/> that splits a PDF into individual pages or page ranges
    /// using the built-in <see cref="PdfSplitter"/>.
    ///
    /// Registration:
    /// <code>
    ///   PdfPluginHost.Instance.Register(new SplitPlugin());
    /// </code>
    /// </summary>
    /// <remarks>
    /// <b>The two contracts this type sits between are numbered differently, and it is this class
    /// that translates.</b> <see cref="ISplitPlugin"/> documents zero-based page indices — matching
    /// the rest of the plugin SDK, e.g. <c>RedactionRegion.PageIndex</c> — while
    /// <see cref="PdfSplitter"/> is one-based and is called that way by the WPF viewer, the MAUI
    /// app and its own tests. Forwarding an index unchanged would make <see cref="Split"/> throw on
    /// its first page and <see cref="SplitRange"/> return the page before the one asked for. The
    /// <c>+ 1</c> is here rather than in either contract.
    /// </remarks>
    public sealed class SplitPlugin : ISplitPlugin
    {
        public string Name        => "Tetitulz.Plugins.Split";
        public string Description => "Splits a PDF into individual pages or page ranges.";
        public Version Version    => new Version(1, 0, 0);

        public void Initialize() { }
        public void Shutdown()   { }

        public IReadOnlyList<byte[]> Split(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            // Opened once: every page is extracted from the one document.
            Stream seekable = Tetitulz.Pdf.Core.IO.SeekableStream.Ensure(input, rewind: true, out bool ownsSeekable);
            try
            {
                using (var doc = PdfDocument.Open(seekable))
                    return PdfSplitter.EachPage(doc);
            }
            finally
            {
                if (ownsSeekable) seekable.Dispose();
            }
        }

        public byte[] SplitRange(Stream input, int startPage, int endPage)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (startPage < 0)          throw new ArgumentOutOfRangeException(nameof(startPage), "Must be >= 0.");
            if (endPage < startPage)    throw new ArgumentOutOfRangeException(nameof(endPage), "Must be >= startPage.");

            using var ms = new MemoryStream();
            // Zero-based in, one-based out. An index past the last page is still refused by
            // PdfSplitter's own /Count check, so the translation cannot turn an out-of-range
            // request into a valid one.
            PdfSplitter.SplitRange(input, ms, startPage + 1, endPage + 1);
            return ms.ToArray();
        }
    }
}
