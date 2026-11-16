// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.DocxToPdf.Rendering.Drawings
{
    /// <summary>
    /// The colour scheme Word applies when a document has no theme part.
    ///
    /// <para>
    /// Every DOCX built in memory by a test, and plenty produced by non-Word tools, has no
    /// <c>ThemePart</c>. Word still renders those documents in colour, because the Office theme is
    /// baked into the application rather than required in the file — so falling back to these
    /// values reproduces what the author sees, and returning nothing would not. The fallback is
    /// <b>reported</b> so it can never be mistaken for a colour the file actually specified.
    /// </para>
    ///
    /// <para>
    /// Provenance: the Office theme shipped with Word 2013 and later
    /// (<c>theme1.xml</c> of a default <c>.docx</c>). Pinned by a test so a future edit cannot
    /// drift it silently.
    /// </para>
    /// </summary>
    internal static class OfficeThemeDefaults
    {
        private static readonly Dictionary<string, string> Scheme =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["dk1"] = "000000",
                ["lt1"] = "FFFFFF",
                ["dk2"] = "44546A",
                ["lt2"] = "E7E6E6",
                ["accent1"] = "4472C4",
                ["accent2"] = "ED7D31",
                ["accent3"] = "A5A5A5",
                ["accent4"] = "FFC000",
                ["accent5"] = "5B9BD5",
                ["accent6"] = "70AD47",
                ["hlink"] = "0563C1",
                ["folHlink"] = "954F72",
            };

        /// <summary>
        /// The default colour for a DrawingML scheme token (<c>tx1</c>, <c>bg1</c>,
        /// <c>accent1</c>…), or <c>null</c> if the token is not a scheme colour.
        /// </summary>
        internal static string Hex(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            string key = token switch
            {
                "tx1" => "dk1",
                "bg1" => "lt1",
                "tx2" => "dk2",
                "bg2" => "lt2",
                _ => token,
            };
            return Scheme.TryGetValue(key, out string hex) ? hex : null;
        }
    }
}
