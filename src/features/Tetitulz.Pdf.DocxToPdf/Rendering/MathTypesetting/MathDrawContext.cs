// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace Tetitulz.Pdf.DocxToPdf.Rendering.MathTypesetting
{
    /// <summary>
    /// The narrow surface a <see cref="MathBox"/> may touch.
    ///
    /// <para>
    /// Deliberately not the whole <see cref="DocxRenderContext"/>: a box lays itself out relative to
    /// a baseline it is handed, and must never reach the flow cursor or start a page. Passing the
    /// full context would make that a matter of discipline; passing this makes it a matter of what
    /// compiles.
    /// </para>
    /// </summary>
    internal sealed class MathDrawContext
    {
        private readonly DocxRenderContext _context;

        internal MathDrawContext(DocxRenderContext context) => _context = context;

        internal string RegisterFont(string baseFontName) => _context.RegisterFont(baseFontName);

        internal void UseEmbeddedFont(string resourceName)
        {
            if (!string.IsNullOrEmpty(resourceName))
                _context.UseEmbeddedFontOnCurrentPage(resourceName);
        }

        // One implementation, in RunRenderer.AppendFill. Kept as an instance method because
        // callers reach it through the draw context.
        internal void AppendFill(StringBuilder ops, string hex)
            => RunRenderer.AppendFill(ops, hex);
    }
}


