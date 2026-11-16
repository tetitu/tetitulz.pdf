// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

// The plugin contracts moved here from Tetitulz.Pdf.Plugins.SDK, whose csproj set
// <Nullable>annotations</Nullable>. Core does not enable nullable globally (that would mark
// its ~1000 existing public members non-nullable in metadata, which is not true), so the
// setting is preserved per-file instead.
#nullable enable annotations

using System.IO;

namespace Tetitulz.Pdf.Core.Plugins
{
    /// <summary>
    /// Plugin that converts another file format to PDF.
    /// Dispatched by <c>PdfOperations.Import(source, pdfOutput, formatKey)</c>.
    /// </summary>
    public interface IImportPlugin : IPdfPlugin
    {
        /// <summary>
        /// Short format key used for dispatch (case-insensitive).
        /// Examples: "docx", "html", "svg", "img", "xlsx".
        /// </summary>
        string FormatKey { get; }

        /// <summary>MIME type of the source format (e.g. "application/vnd.openxmlformats-officedocument.wordprocessingml.document").</summary>
        string SourceMimeType { get; }

        /// <summary>
        /// Convert the content of <paramref name="source"/> to PDF and write to <paramref name="pdfOutput"/>.
        /// The caller is responsible for opening/closing both streams.
        /// </summary>
        void Import(Stream source, Stream pdfOutput, ImportOptions options);
    }
}
