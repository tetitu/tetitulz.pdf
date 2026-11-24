// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Plugins.Docx
{
    /// <summary>
    /// Removes the characters XML 1.0 forbids from text on its way into an Open XML part.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists.</b> <c>DocumentFormat.OpenXml</c> serialises through an
    /// <c>XmlWriter</c> with <c>CheckCharacters</c> on, so a single forbidden character in
    /// extracted text would abort the whole conversion — thrown from the package's
    /// <c>Dispose</c>, after the <c>using</c> block, so the converter would return nothing. The
    /// route in is ordinary: a font dictionary with no <c>/Subtype</c> falls back to
    /// <c>Core.Fonts.FallbackFont</c>, whose decode is a straight CP1252 mapping, so
    /// <c>(\001\002) Tj</c> extracts as U+0001 U+0002; a <c>/ToUnicode</c> CMap mapping a code to
    /// <c>&lt;0001&gt;</c> gets there on a well-formed file too.</para>
    /// <para>One implementation for all three converters (DOCX, XLSX, PPTX): they read the same
    /// extractor and were broken by the same input, so a per-converter copy would only give them
    /// three chances to disagree.</para>
    /// </remarks>
    internal static class XmlTextSanitizer
    {
        /// <summary>
        /// Drops every character outside XML 1.0 §2.2's <c>Char</c> production, keeping the text
        /// around them.
        /// </summary>
        /// <remarks>
        /// <b>Tab, line feed and carriage return are kept</b> — they are legal, and the converters
        /// split paragraphs on them, so stripping the C0 range wholesale would collapse every
        /// document to one line. Valid surrogate <i>pairs</i> are kept (astral characters are legal);
        /// only unpaired halves, which cannot be encoded, are dropped. The rule is the one the SVG export
        /// applies to text read out of a PDF, <c>Core.Text.XmlLegalText.Remove</c>.
        /// </remarks>
        internal static string Sanitize(string text) => XmlLegalText.Remove(text);

        /// <summary>
        /// <see cref="Sanitize"/>, then the escape a spreadsheet cell's text needs: the underscore
        /// that opens a <c>_xHHHH_</c> sequence written as <c>_x005F_</c>.
        /// </summary>
        /// <remarks>
        /// A spreadsheet's string (ECMA-376 <c>ST_Xstring</c>) decodes <c>_xHHHH_</c> as the
        /// character it names, so text extracted as <c>AB_x0031_</c> would show as <c>AB1</c>.
        /// Escaping each opening underscore leaves the text as written, overlapping sequences
        /// included. Only for spreadsheet cells: a word-processing or slide run is no
        /// <c>ST_Xstring</c>.
        /// </remarks>
        internal static string ForSpreadsheet(string text)
        {
            string clean = Sanitize(text);
            var sb = new StringBuilder(clean.Length);
            for (int i = 0; i < clean.Length; i++)
            {
                if (clean[i] == '_' && OpensXstringEscape(clean, i)) sb.Append("_x005F_");
                else sb.Append(clean[i]);
            }
            return sb.ToString();
        }

        /// <summary>Whether the underscore at <paramref name="i"/> opens <c>_xHHHH_</c>. Scanned by hand: the text is the file's.</summary>
        private static bool OpensXstringEscape(string text, int i)
        {
            if (i + 6 >= text.Length || text[i + 1] != 'x' || text[i + 6] != '_') return false;
            for (int k = i + 2; k < i + 6; k++)
            {
                char c = text[k];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
            }
            return true;
        }
    }
}
