// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Globalization;

namespace Tetitulz.Pdf.Plugins.PostScript.Interpreter
{
    /// <summary>
    /// Parses the three PostScript number syntaxes (PLRM §3.2.1) and nothing else — a run of
    /// characters that is not one of them is a name, which is why this returns a bool rather than
    /// throwing.
    ///
    /// <list type="bullet">
    ///   <item>integer — <c>123</c>, <c>-98</c>, <c>+17</c></item>
    ///   <item>real — <c>-1.5</c>, <c>.002</c>, <c>4.</c>, <c>1E6</c>, <c>1.0E-3</c></item>
    ///   <item>radix — <c>16#FFFE</c>, <c>2#1000</c>, <c>8#777</c> (base 2..36, always an integer)</item>
    /// </list>
    /// </summary>
    internal static class PsNumberParser
    {
        /// <summary>
        /// Scratch buffer for widening a token's bytes to characters, sized to the token cap.
        /// </summary>
        /// <remarks>
        /// <c>[ThreadStatic]</c>, matching the six other per-thread scratch slots in this plugin: the
        /// interpreter runs concurrently, and a shared buffer would let two threads shred each
        /// other's token. Allocated once per thread on first use.
        /// </remarks>
        [ThreadStatic]
        private static char[] _scratch;

        /// <summary>
        /// Attempts to read <paramref name="span"/> as a number.
        /// </summary>
        /// <param name="span">The whole token; a trailing non-numeric character makes it a name.</param>
        /// <param name="isInteger">True when the result is an integer, false for a real.</param>
        /// <param name="value">The parsed value.</param>
        public static bool TryParse(ReadOnlySpan<byte> span, out bool isInteger, out double value)
        {
            isInteger = false;
            value     = 0;
            if (span.Length == 0) return false;

            int hash = span.IndexOf((byte)'#');
            if (hash > 0) return TryParseRadix(span, hash, out isInteger, out value);

            // A bound is still needed — the buffer below is on the stack — but it must not be the
            // thing that decides a token's TYPE. At 64 a longer numeric-looking token silently
            // became a NAME, so a legal literal surfaced as an inexplicable `undefined` from
            // somewhere else entirely. The cap is now named, generous enough that no real literal
            // reaches it (a double round-trips in 24 characters), and refusal is reported.
            if (span.Length > PsLimits.MaxNumericTokenChars)
            {
                PostScriptDiagnostics.ReportOversizedNumericToken(span.Length);
                return false;
            }

            // A reused per-thread buffer rather than `stackalloc char[span.Length]`.
            //
            // Not a style choice: the widened characters have to become a `string` below, because
            // netstandard2.0 has neither `new string(ReadOnlySpan<char>)` nor the span overloads of
            // long.TryParse / double.TryParse — and a package cannot add a constructor to
            // System.String. Given a string is unavoidable, `new string(buf, 0, n)` works on both
            // legs, whereas the stackalloc form compiles on neither without one.
            //
            // Still allocation-free per token: the length is already capped a few lines above, so
            // one fixed buffer serves every call on this thread. [ThreadStatic] because the
            // interpreter is used concurrently and this must not be shared across threads.
            char[] chars = _scratch ?? (_scratch = new char[PsLimits.MaxNumericTokenChars]);
            bool sawDigit = false, sawDot = false, sawExp = false;

            for (int i = 0; i < span.Length; i++)
            {
                byte b = span[i];
                chars[i] = (char)b;

                if (b >= (byte)'0' && b <= (byte)'9') { sawDigit = true; continue; }
                if (b == (byte)'.')
                {
                    // A second '.' or one after the exponent makes it a name, not a number.
                    if (sawDot || sawExp) return false;
                    sawDot = true;
                    continue;
                }
                if (b == (byte)'e' || b == (byte)'E')
                {
                    // An exponent needs a mantissa before it and may appear once.
                    if (sawExp || !sawDigit) return false;
                    sawExp = true;
                    continue;
                }
                if (b == (byte)'+' || b == (byte)'-')
                {
                    // A sign is legal only leading, or immediately after the exponent marker.
                    bool leading = i == 0;
                    bool afterExp = i > 0 && (span[i - 1] == (byte)'e' || span[i - 1] == (byte)'E');
                    if (!leading && !afterExp) return false;
                    continue;
                }
                return false;   // any other character — this is a name
            }

            if (!sawDigit) return false;   // ".", "-", "+", "e" and friends are names

            // Length-bounded: the buffer is the cap-sized scratch, not the token, so the count has
            // to come from the token. `new string(char[], int, int)` exists on both legs.
            var text = new string(chars, 0, span.Length);

            if (!sawDot && !sawExp
                && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture,
                                 out long l))
            {
                isInteger = true;
                value     = l;
                return true;
            }

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            {
                // An integer literal too large for long is a real in PostScript too (PLRM §3.2.1
                // says an out-of-range integer is converted to a real), so this is not a fallback
                // hack — it is the specified behaviour.
                isInteger = false;
                value     = d;
                return true;
            }

            return false;
        }

        /// <summary>Radix form: <c>base#digits</c>, base 2..36, always yielding an integer.</summary>
        private static bool TryParseRadix(ReadOnlySpan<byte> span, int hash,
                                          out bool isInteger, out double value)
        {
            isInteger = false;
            value     = 0;

            int radix = 0;
            for (int i = 0; i < hash; i++)
            {
                byte b = span[i];
                if (b < (byte)'0' || b > (byte)'9') return false;
                radix = radix * 10 + (b - '0');
                if (radix > 36) return false;
            }
            if (radix < 2) return false;

            int digits = span.Length - hash - 1;
            if (digits <= 0) return false;
            if (digits > PsLimits.MaxNumericTokenChars) return false;

            // Accumulate in ulong: PLRM says the digits are interpreted as an unsigned quantity, so
            // 16#FFFFFFFF is -1 rather than an overflow.
            //
            // Bounded by VALUE, not by digit count. A 16-digit cap rejected `2#` followed by 33 ones
            // — a legal 32-bit literal — while accepting `16#1FFFFFFFF`, which does not fit and was
            // silently truncated to a DIFFERENT number by the cast below. Leading zeros make digit
            // count meaningless: `2#000…0001` is 1 however long it is.
            ulong acc = 0;
            for (int i = hash + 1; i < span.Length; i++)
            {
                int d = DigitValue(span[i]);
                if (d < 0 || d >= radix) return false;

                acc = acc * (ulong)radix + (ulong)d;
                if (acc > uint.MaxValue) return false;   // does not fit 32 bits: a name, not a number
            }

            isInteger = true;
            value     = unchecked((int)(uint)acc);
            return true;
        }

        private static int DigitValue(byte b)
        {
            if (b >= (byte)'0' && b <= (byte)'9') return b - '0';
            if (b >= (byte)'a' && b <= (byte)'z') return b - 'a' + 10;
            if (b >= (byte)'A' && b <= (byte)'Z') return b - 'A' + 10;
            return -1;
        }
    }
}
