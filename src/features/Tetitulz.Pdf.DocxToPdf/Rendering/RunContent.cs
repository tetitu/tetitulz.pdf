// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Tetitulz.Pdf.DocxToPdf.Rendering
{
    /// <summary>
    /// What a run's child other than <c>w:t</c>, <c>w:tab</c> and <c>w:br</c> contributes to the
    /// run's text — the one reading for the body renderer, the cell renderer and the Builder mapper,
    /// so the three paths draw the same run alike. A non-breaking hyphen is a hyphen, a carriage
    /// return a line break, an absolute-position tab a tab; a symbol character names a glyph of a
    /// symbol font this converter does not embed, so it is reported and draws nothing; an optional
    /// hyphen is a break opportunity and draws nothing.
    /// </summary>
    internal static class RunContent
    {
        internal enum Kind { None, Text, LineBreak, Tab }

        /// <summary>
        /// Classifies <paramref name="child"/>, with the text it stands for in <paramref name="text"/>
        /// when it is <see cref="Kind.Text"/>; <see cref="Kind.None"/> for a child that is no content
        /// (run properties, a bookmark, a symbol character, an optional hyphen).
        /// </summary>
        internal static Kind Classify(OpenXmlElement child, out string text)
        {
            text = null;
            switch (child)
            {
                case NoBreakHyphen:
                    // U+2011 is outside WinAnsi; the hyphen it stands for is drawn, and the line is
                    // not broken at it, since the wrap breaks at spaces.
                    text = "-";
                    return Kind.Text;
                case CarriageReturn:
                    return Kind.LineBreak;
                case PositionalTab:
                    return Kind.Tab;
                case SymbolChar sym:
                    DocxDiagnostics.Report("RunContent.Symbol", string.Format(CultureInfo.InvariantCulture,
                        "a w:sym character (code {0} of the font '{1}') was not drawn: the symbol font is not embedded " +
                        "and the code names a glyph of it, not a character",
                        sym.Char?.Value ?? "?", sym.Font?.Value ?? "(unnamed)"));
                    return Kind.None;
                default:
                    return Kind.None;
            }
        }
    }
}
