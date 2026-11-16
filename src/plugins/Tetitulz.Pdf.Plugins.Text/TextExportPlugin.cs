// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.Text
{
    /// <summary>
    /// <see cref="IExportPlugin"/> that exports all text content from a PDF as plain UTF-8 text
    /// using the built-in <see cref="PdfStructuredTextExtractor"/>.
    ///
    /// <para><b>This plugin has no configurable settings.</b> Plain UTF-8 text has nothing to
    /// choose — no resolution, no embedding, no layout mode — so <see cref="ExportOptions.Extra"/>
    /// recognises no key at all, and passing one raises <see cref="ArgumentException"/> rather than
    /// being dropped. The sibling adapters honour their bags, so a caller reusing one
    /// <see cref="ExportOptions"/> across formats would otherwise have no way to learn that this
    /// one honoured none of it.</para>
    ///
    /// Registration:
    /// <code>
    ///   PdfPluginHost.Instance.Register(new TextExportPlugin());
    /// </code>
    /// </summary>
    public sealed class TextExportPlugin : IExportPlugin
    {
        public string Name           => "Tetitulz.Plugins.TextExport";
        public string Description    => "Exports all text content from a PDF as plain text.";
        public Version Version       => new Version(1, 0, 0);
        public string FormatKey      => "txt";
        public string TargetMimeType => "text/plain";

        public void Initialize() { }
        public void Shutdown()   { }

        /// <exception cref="PdfEncryptedDocumentException">
        /// The document is encrypted. <see cref="PdfDocument"/> loads neither the catalog nor the
        /// page tree of such a file, so the extractor would see <b>zero pages</b> and write a
        /// zero-byte .txt while returning normally — which a batch archiver could not tell from a
        /// PDF that genuinely contains no text.
        /// </exception>
        public void Export(PdfDocument document, Stream output, ExportOptions? options)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (output == null)   throw new ArgumentNullException(nameof(output));
            EncryptedRead.RefuseConversion(document, "plain text");

            // Nothing here is configurable, so any key is a request this plugin cannot honour.
            // Silently dropping it is what made a mistyped setting indistinguishable from an
            // honoured one across the whole adapter set.
            if (options?.Extra is { Count: > 0 } extra)
            {
                var keys = new List<string>(extra.Keys);
                keys.Sort(StringComparer.Ordinal);
                throw new ArgumentException(
                    "TextExportPlugin: option key(s) " +
                    string.Join(", ", keys.ConvertAll(k => "'" + k + "'")) +
                    " were supplied, but plain-text export has no configurable settings and " +
                    "recognises no key. The request is refused rather than ignored.",
                    nameof(options));
            }

            long savedPos = document.BaseStream.Position;
            try
            {
                document.BaseStream.Position = 0;
                var pages = PdfStructuredTextExtractor.Extract(document.BaseStream);
                var sb = new StringBuilder();
                foreach (var page in pages)
                {
                    foreach (var line in page.Lines)
                        sb.AppendLine(line.Text);
                    sb.AppendLine();
                }
                byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
                output.Write(bytes, 0, bytes.Length);
            }
            finally
            {
                document.BaseStream.Position = savedPos;
            }
        }
    }
}
