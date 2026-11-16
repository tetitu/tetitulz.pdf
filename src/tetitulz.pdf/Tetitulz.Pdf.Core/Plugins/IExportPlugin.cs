// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

// The plugin contracts moved here from Tetitulz.Pdf.Plugins.SDK, whose csproj set
// <Nullable>annotations</Nullable>. Core does not enable nullable globally (that would mark
// its ~1000 existing public members non-nullable in metadata, which is not true), so the
// setting is preserved per-file instead.
#nullable enable annotations

using System.IO;
using Tetitulz.Pdf.Core.Document;

namespace Tetitulz.Pdf.Core.Plugins
{
    /// <summary>
    /// Plugin that converts a PDF document to another file format.
    /// Dispatched by <c>PdfOperations.Export(stream, output, formatKey)</c>.
    /// </summary>
    public interface IExportPlugin : IPdfPlugin
    {
        /// <summary>
        /// Short format key used for dispatch (case-insensitive).
        /// Examples: "docx", "epub", "html", "txt", "xlsx".
        /// </summary>
        string FormatKey { get; }

        /// <summary>MIME type of the export target (e.g. "application/vnd.openxmlformats-officedocument.wordprocessingml.document").</summary>
        string TargetMimeType { get; }

        /// <summary>
        /// Export <paramref name="document"/> to <paramref name="output"/>.
        /// The caller is responsible for opening/closing both streams.
        /// <paramref name="options"/> may be <c>null</c>; plugins must treat null as
        /// "use all defaults".
        /// </summary>
        void Export(PdfDocument document, Stream output, ExportOptions? options);
    }
}
