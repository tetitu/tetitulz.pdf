// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Text
{
    /// <summary>
    /// Latin-1 (ISO-8859-1) in both directions — the 1:1 byte-to-code-point mapping PDF and
    /// PostScript use wherever bytes stand in for text.
    /// </summary>
    /// <remarks>
    /// <b>Hand-rolled on purpose, and this is the reason.</b> <c>Encoding.Latin1</c> arrived only
    /// in .NET 5, and <c>Encoding.GetEncoding(28591)</c> needs the CodePages provider registered on
    /// a .NET Core host of <c>netstandard2.0</c> — so neither compiles-and-runs across every target
    /// this assembly ships for. The loop does.
    ///
    /// <para><b>It is public because a plugin has to reach it.</b> Core's
    /// <c>InternalsVisibleTo</c> names no plugin, so an internal helper would have left the three
    /// <c>Plugins.PostScript</c> copies exactly where they were and consolidated nothing. Same
    /// reasoning as <see cref="XmlEscape"/>.</para>
    ///
    /// <para><b>No static fields</b>, so no type initializer and no chance of drawing a caller into
    /// an initialization cycle — the hazard that once crashed the Base test host mid-run.</para>
    ///
    /// <para><b>Six copies existed</b>, three of them guarding a non-positive length and three not.
    /// The guarded behaviour is taken here: it is strictly safer, and the unguarded call sites were
    /// verified unreachable with a negative length rather than assumed to be.</para>
    /// </remarks>
    public static class Latin1Text
    {
        /// <summary>
        /// <paramref name="length"/> bytes from <paramref name="start"/>, as text. A non-positive
        /// length — or a null buffer — yields the empty string.
        /// </summary>
        public static string Decode(byte[] data, int start, int length)
        {
            if (data == null || length <= 0) return string.Empty;

            var chars = new char[length];
            for (int i = 0; i < length; i++) chars[i] = (char)data[start + i];
            return new string(chars);
        }

        /// <summary>The whole buffer as Latin-1 text; a null buffer yields the empty string.</summary>
        public static string Decode(byte[] data)
            => data == null ? string.Empty : Decode(data, 0, data.Length);

        /// <summary>
        /// <paramref name="text"/> as Latin-1 bytes, one byte per code point. A code point above
        /// <c>U+00FF</c> is written as <c>?</c>; a null string yields an empty array.
        /// </summary>
        /// <remarks>
        /// <para><b>The <c>?</c> is not a shortcut — it is what the framework encoder does.</b>
        /// <c>Encoding.GetEncoding("iso-8859-1")</c> substitutes exactly that for an unrepresentable
        /// character. A naive <c>(byte)c</c> truncation would not: it would turn <c>U+0141</c> into
        /// <c>A</c> rather than <c>?</c>, silently inventing a different letter.</para>
        ///
        /// <para><b>One case is not byte-identical, and it is stated rather than glossed.</b> This
        /// walks <c>char</c>s, so a surrogate <i>pair</i> — one code point, two chars — yields two
        /// <c>?</c> where the framework encoder yields one. No caller can reach it:
        /// <c>PdfString</c> routes any character above <c>U+00FF</c> to UTF-16BE before this is
        /// called, and the two parsers build their strings from raw file bytes, so every character
        /// they pass is already <c>≤ U+00FF</c>. The two content-stream writers could in principle
        /// be handed one, and there the output is wrong under either reading.</para>
        ///
        /// <para><b>Seven sites in this assembly built their own encoder</b> — three as
        /// <c>static readonly Encoding</c> fields and four inline — all reaching for
        /// <c>Encoding.GetEncoding("iso-8859-1")</c>, the construct this type's own remarks argue
        /// against: it is the API that does not compile-and-run across every target this assembly
        /// ships for, and each field is a type initializer on a class that may be touched early.
        /// The decode direction was consolidated here first and the encode direction was left
        /// behind, which is the half-consolidation CLAUDE.md warns is more dangerous than none.</para>
        /// </remarks>
        public static byte[] Encode(string text)
        {
            if (string.IsNullOrEmpty(text)) return new byte[0];

            var bytes = new byte[text.Length];
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                bytes[i] = c <= 0xFF ? (byte)c : (byte)'?';
            }
            return bytes;
        }
    }
}
