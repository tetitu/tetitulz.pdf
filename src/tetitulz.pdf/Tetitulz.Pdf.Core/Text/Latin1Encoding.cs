// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Tetitulz.Pdf.Core.Text
{
    /// <summary>
    /// <see cref="Latin1Text"/> as an <see cref="Encoding"/>, for the one place that needs the
    /// object rather than the two static methods: a decoder that calls <c>GetString</c> repeatedly
    /// through a stored <see cref="Encoding"/> (<c>PdfFont</c>'s CP-1252 fallback-of-a-fallback).
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists.</b> <c>Encoding.GetEncoding("iso-8859-1")</c> compiles on every
    /// target but throws on a .NET Core host of the <c>netstandard2.0</c> leg with no CodePages
    /// provider, and <c>Encoding.Latin1</c> is .NET 5+ — CLAUDE.md's rule for Latin-1 is
    /// <see cref="Latin1Text"/>, whose static methods cannot be stored where an <see cref="Encoding"/>
    /// is.</para>
    /// <para>One byte per character both ways; a character above U+00FF encodes as <c>?</c>
    /// (0x3F), as the framework's Latin-1 encoder does by default. No static state.</para>
    /// </remarks>
    public sealed class Latin1Encoding : Encoding
    {
        /// <summary>The replacement for a character Latin-1 cannot hold.</summary>
        private const byte Replacement = (byte)'?';

        public override int GetByteCount(char[] chars, int index, int count) => count;

        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
        {
            for (int i = 0; i < charCount; i++)
            {
                char c = chars[charIndex + i];
                bytes[byteIndex + i] = c <= 'ÿ' ? (byte)c : Replacement;
            }
            return charCount;
        }

        public override int GetCharCount(byte[] bytes, int index, int count) => count;

        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
        {
            for (int i = 0; i < byteCount; i++) chars[charIndex + i] = (char)bytes[byteIndex + i];
            return byteCount;
        }

        public override string GetString(byte[] bytes, int index, int count) => Latin1Text.Decode(bytes, index, count);

        public override int GetMaxByteCount(int charCount) => charCount;

        public override int GetMaxCharCount(int byteCount) => byteCount;

        /// <summary>ISO-8859-1's IANA name.</summary>
        public override string WebName => "iso-8859-1";
    }
}
