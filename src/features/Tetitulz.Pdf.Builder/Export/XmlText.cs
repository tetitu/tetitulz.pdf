// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Builder.Export
{
    /// <summary>
    /// Makes a model string safe to put in an XML document — the one place both the ODT writer (raw
    /// string concatenation) and the DOCX writer (OpenXML, which escapes markup itself but throws on an
    /// invalid character) clean their text.
    /// </summary>
    /// <remarks>
    /// A run may hold a code point XML 1.0 forbids — the vertical tab U+000B that Word uses for a manual
    /// line break in copied text, a stray control character, a lone surrogate, U+FFFF. Written raw into
    /// <c>content.xml</c> it makes the part not well-formed, so an office application refuses the file;
    /// handed to OpenXML it throws <c>ArgumentException</c> out of the exporter and leaves the caller's
    /// stream holding a partial zip. Either way the character is replaced with U+FFFD here first, and the
    /// alteration is reported once per document.
    /// </remarks>
    internal static class XmlText
    {
        // Whether the "characters were replaced" diagnostic has fired for the document being written on
        // this thread. [ThreadStatic] as the exporter's other per-write state is (OdtExporter._footnotesSlot),
        // so the static write methods can share it; reset by BeginDocument at the start of each export.
        [ThreadStatic] private static bool _replacementReported;

        /// <summary>Called at the start of an export so the once-per-document report can fire again.</summary>
        internal static void BeginDocument() => _replacementReported = false;

        /// <summary>
        /// Strips invalid characters and escapes the five XML delimiters, for the ODT writer, which
        /// concatenates raw strings into both element text and attribute values (so <c>"</c> and <c>'</c>
        /// matter too). Reports once if anything was replaced.
        /// </summary>
        internal static string ForOdt(string s)
        {
            string stripped = StripInvalidChars(s, out bool changed);
            ReportIfChanged(changed);
            return stripped
                .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                .Replace("\"", "&quot;").Replace("'", "&apos;");
        }

        /// <summary>
        /// Strips invalid characters only, for the DOCX writer — OpenXML escapes the markup itself, but
        /// throws on a forbidden character. Reports once if anything was replaced.
        /// </summary>
        internal static string ForOpenXml(string s)
        {
            string stripped = StripInvalidChars(s, out bool changed);
            ReportIfChanged(changed);
            return stripped;
        }

        private static void ReportIfChanged(bool changed)
        {
            if (!changed || _replacementReported) return;
            _replacementReported = true;
            BuilderDiagnostics.Report(BuilderDiagnosticKind.ContentApproximated,
                "A character XML forbids (for example a control character or a lone surrogate) was " +
                "replaced with U+FFFD so the output stays well-formed.");
        }

        /// <summary>Replaces every code point XML 1.0 does not allow with U+FFFD.</summary>
        internal static string StripInvalidChars(string s, out bool changed)
            => Core.Text.XmlLegalText.Replace(s, out changed);
    }
}
