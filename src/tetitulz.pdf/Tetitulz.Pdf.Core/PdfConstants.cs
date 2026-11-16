// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core
{
    internal static class PdfConstants
    {
        public const byte CarriageReturn = (byte)'\r';
        public const byte LineFeed = (byte)'\n';
        public const byte Space = (byte)' ';
        public const byte Tab = (byte)'\t';
        public const byte FormFeed = 12;
        public const byte NullChar = 0;
        public const byte LeftParen = (byte)'(';
        public const byte RightParen = (byte)')';
        public const byte LessThan = (byte)'<';
        public const byte GreaterThan = (byte)'>';
        public const byte LeftBracket = (byte)'[';
        public const byte RightBracket = (byte)']';
        public const byte LeftBrace = (byte)'{';
        public const byte RightBrace = (byte)'}';
        public const byte Solidus = (byte)'/';
        public const byte Percent = (byte)'%';
        public const byte Backslash = (byte)'\\';
        public const byte NumberSign = (byte)'#';

        public static bool IsWhitespace(byte b)
        {
            return b == Space || b == Tab || b == CarriageReturn || b == LineFeed
                || b == FormFeed || b == NullChar;
        }

        public static bool IsDelimiter(byte b)
        {
            return b == LeftParen || b == RightParen || b == LessThan || b == GreaterThan
                || b == LeftBracket || b == RightBracket || b == LeftBrace || b == RightBrace
                || b == Solidus || b == Percent;
        }

        public static bool IsEndOfToken(byte b)
        {
            return IsWhitespace(b) || IsDelimiter(b);
        }

        public static bool IsDigit(byte b)
        {
            return b >= (byte)'0' && b <= (byte)'9';
        }

        public static bool IsHexDigit(byte b)
        {
            return (b >= (byte)'0' && b <= (byte)'9')
                || (b >= (byte)'a' && b <= (byte)'f')
                || (b >= (byte)'A' && b <= (byte)'F');
        }

        public static int HexToNibble(byte b)
        {
            if (b >= (byte)'0' && b <= (byte)'9') return b - (byte)'0';
            if (b >= (byte)'a' && b <= (byte)'f') return b - (byte)'a' + 10;
            if (b >= (byte)'A' && b <= (byte)'F') return b - (byte)'A' + 10;
            return -1;
        }

        /// <summary>
        /// The digits of a PDF hexadecimal string (§7.3.4.3) as bytes, with an odd digit count
        /// completed by a trailing <c>0</c>: <c>&lt;901FA&gt;</c> is three bytes, the last
        /// <c>A0</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>Half of this was already shared, which is what made the other half easy to
        /// miss.</b> The parser and the content-stream parser both called
        /// <see cref="HexToNibble"/> — the lookup — while each kept its own copy of the padding
        /// rule, which is the part the <i>spec</i> decides. A shared lookup under two copies of a
        /// spec rule reads as consolidated and is not.</para>
        /// <para>The caller is expected to have kept only hex digits; a non-digit yields
        /// <see cref="HexToNibble"/>'s <c>-1</c> and therefore whatever that composes to, exactly as
        /// both copies did.</para>
        /// </remarks>
        public static byte[] HexStringToBytes(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return new byte[0];
            var digits = new byte[hex.Length];
            for (int i = 0; i < hex.Length; i++) digits[i] = (byte)hex[i];
            return HexDigitsToBytes(digits, digits.Length);
        }

        /// <summary>
        /// The first <paramref name="count"/> hex digits of <paramref name="digits"/> as bytes — the
        /// rule <see cref="HexStringToBytes"/> applies to text, for the tokenizer, which holds a hex
        /// string's digits as bytes.
        /// </summary>
        internal static byte[] HexDigitsToBytes(byte[] digits, int count)
        {
            if (count <= 0) return new byte[0];

            // §7.3.4.3: "If the final digit ... is missing ... it shall be assumed to be 0."
            var bytes = new byte[(count + 1) / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                int high = HexToNibble(digits[i * 2]);
                int low = i * 2 + 1 < count ? HexToNibble(digits[i * 2 + 1]) : 0;
                bytes[i] = (byte)((high << 4) | low);
            }
            return bytes;
        }
    }
}
