// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.Docx
{
    /// <summary>
    /// <see cref="IExportPlugin"/> that converts a PDF document to DOCX using the
    /// built-in <see cref="PdfToDocxConverter"/>.
    /// Dispatched by <c>PdfOperations.Export(input, output, "docx")</c>.
    ///
    /// Registration:
    /// <code>
    ///   PdfPluginHost.Instance.Register(new DocxExportPlugin());
    /// </code>
    /// </summary>
    public sealed class DocxExportPlugin : IExportPlugin
    {
        public string Name        => "Tetitulz.Plugins.DocxExport";
        public string Description => "Exports PDF documents to DOCX using the built-in converter.";
        public Version Version    => new Version(1, 0, 0);
        public string FormatKey   => "docx";
        public string TargetMimeType =>
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

        public void Initialize() { }
        public void Shutdown()   { }

        /// <exception cref="PdfEncryptedDocumentException">
        /// The document is encrypted. <see cref="PdfDocument"/> loads neither the catalog nor the
        /// page tree of such a file, so a converter would see <b>zero pages</b> and return a
        /// structurally valid, completely <b>empty</b> .docx while reporting success — which a
        /// batch job archiving a folder could not tell from a PDF with no content. Every export
        /// plugin refuses encrypted input, and each has its own fails-closed test.
        /// </exception>
        /// <remarks>
        /// <para><b>This adapter has no configurable options.</b>
        /// <c>PdfToDocxConverter.Convert(Stream)</c> takes no options object — there is nothing to
        /// map — so an <see cref="ExportOptions.Extra"/> entry is <b>refused</b> rather than
        /// silently ignored: a caller who writes <c>Extra["EmbedImages"] = false</c> would otherwise
        /// get embedded images and no indication the request was dropped.</para>
        ///
        /// <para>Refusing is deliberately stricter than defaulting. If <c>PdfToDocxConverter</c>
        /// ever grows options, the keys move from "unrecognised" to "recognised" and no caller who
        /// was previously silently ignored is surprised by a behaviour change — because none of
        /// them can exist.</para>
        /// </remarks>
        public void Export(PdfDocument document, Stream output, ExportOptions? options)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (output == null)   throw new ArgumentNullException(nameof(output));
            DocxPluginOptionReader.RejectUnknownKeys("Tetitulz.Plugins.DocxExport", options?.Extra);
            Tetitulz.Pdf.Base.Operations.EncryptedRead.RefuseConversion(document, "DOCX");

            long savedPos = document.BaseStream.Position;
            try
            {
                document.BaseStream.Position = 0;
                byte[] docxBytes = PdfToDocxConverter.Convert(document.BaseStream)
                    ?? throw new InvalidOperationException("DOCX conversion produced no output.");
                output.Write(docxBytes, 0, docxBytes.Length);
            }
            finally
            {
                document.BaseStream.Position = savedPos;
            }
        }
    }
}
