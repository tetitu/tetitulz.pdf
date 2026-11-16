// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text;

namespace Tetitulz.Pdf.Core.Objects
{
    /// <summary>
    /// The single implementation of PDF literal-string escaping (ISO 32000-1 §7.3.4.2).
    ///
    /// <para><b>Why this exists.</b> One escape for every writer across the assemblies, handling
    /// the full control-byte range — an escaper of only <c>(</c>, <c>)</c> and <c>\</c> is the copy
    /// that drifts. The difference is not cosmetic: §7.3.4.2
    /// says a reader <i>shall</i> treat an end-of-line marker inside a literal string as a single
    /// <c>0Ah</c>, so a string containing a carriage return written by a weak escaper comes back as
    /// a line feed — silent text corruption — and an unescaped NUL truncates some viewers' parsers.
    /// </para>
    ///
    /// <para><b>Why a type of its own rather than a method on <see cref="PdfString"/>.</b>
    /// <c>PdfString</c> has static fields (an encoding and a lazily built code-page table), so it
    /// carries a type initializer. Two of this method's callers write PDF bytes without ever
    /// constructing a <c>PdfString</c>, and this repository has already been bitten once by adding
    /// a type-initialization edge between rendering types — the Base test host crashed part-way
    /// through a parallel run and reported "Passed!" for the tests that had finished, silently
    /// dropping about 190. This type has no static fields at all, so there is no initializer to
    /// run.</para>
    ///
    /// <para><b>Not for every string.</b> <c>DocxToPdf</c>'s <c>EncodeWinAnsiLiteral</c> is
    /// deliberately separate: it escapes <i>and</i> transcodes to CP1252 for a stream declared
    /// <c>/WinAnsiEncoding</c>. Routing it here would emit UTF-16 code units and mojibake every
    /// smart quote.</para>
    /// </summary>
    internal static class PdfStringEscape
    {
        /// <summary>
        /// Escapes <paramref name="s"/> for use inside a PDF literal string's parentheses.
        /// The delimiters and the backslash are backslash-escaped; the five characters with named
        /// escapes use them; every other C0 control and <c>DEL</c> becomes an <b>octal</b>
        /// <c>\ddd</c>.
        /// </summary>
        /// <param name="s">
        /// May be <c>null</c> or empty, in which case the empty string is returned. Returning
        /// rather than throwing matches what seven of the eight replaced copies did; the eighth
        /// threw <see cref="NullReferenceException"/>, which is not a contract anyone can rely on.
        /// </param>
        public static string EscapeLiteral(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;

            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append(@"\\"); break;
                    case '(': sb.Append(@"\("); break;
                    case ')': sb.Append(@"\)"); break;
                    case '\r': sb.Append(@"\r"); break;
                    case '\n': sb.Append(@"\n"); break;
                    case '\t': sb.Append(@"\t"); break;
                    case '\b': sb.Append(@"\b"); break;
                    case '\f': sb.Append(@"\f"); break;
                    default:
                        if (c < 0x20 || c == 0x7F)
                        {
                            // §7.3.4.2's \ddd is OCTAL. A predecessor formatted it with "{0:D3}"
                            // (decimal) and emitted wrong bytes for every value >= 8: 0x1F became
                            // "\031", which a parser reads as octal 031 = 0x19, and 0x09 became
                            // "\009", where '9' is not an octal digit and aborts the escape early.
                            sb.Append('\\');
                            sb.Append(Convert.ToString(c, 8).PadLeft(3, '0'));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
