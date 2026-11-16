// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace Tetitulz.Pdf.Core.Objects
{
    /// <summary>
    /// How a PDF name object's value is written (§7.3.5): UTF-8 encoded, then every byte outside the
    /// printable ASCII range — and every delimiter — replaced by <c>#XX</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>A type of its own, for the same reason <see cref="PdfStringEscape"/> is.</b>
    /// <see cref="PdfName"/> has static fields, so calling into it from a writer that runs during
    /// type initialization adds an initialization edge; this class holds only a pure function.</para>
    ///
    /// <para><b>The escape must happen on UTF-8 BYTES, not on characters.</b> Casting a
    /// <see cref="char"/> to <see cref="byte"/> truncates a UTF-16 code unit — U+20AC € becomes
    /// 0xAC — which is a name no reader maps back.</para>
    ///
    /// <para><b>The output is pure printable ASCII</b>, because everything else has become
    /// <c>#XX</c>. That is what lets a caller emit it through an ASCII writer without loss, and it
    /// is why this can be a <c>string</c> function serving both the byte-level writer and the
    /// content-stream serialiser that had its own, wrong, copy.</para>
    /// </remarks>
    internal static class PdfNameEscape
    {
        /// <summary>
        /// The escaped body of a name — <b>without</b> the leading <c>/</c>, which the caller
        /// supplies.
        /// </summary>
        public static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return EscapeBytes(Encoding.UTF8.GetBytes(value));
        }

        /// <summary>
        /// <see cref="Escape(string)"/> for a name object: one read from bytes that are not UTF-8
        /// (<see cref="PdfName.BytesNotUtf8"/>) is written from its Latin-1 bytes, so it round-trips
        /// to the bytes the file holds — a content stream copied byte for byte still names the
        /// resource <c>/F#E9</c>, and under §7.3.5 <c>/F#C3#A9</c> is another name.
        /// </summary>
        public static string Escape(PdfName name)
        {
            if (name == null) return string.Empty;
            return name.BytesNotUtf8 ? EscapeBytes(Text.Latin1Text.Encode(name.Value)) : Escape(name.Value);
        }

        private static string EscapeBytes(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length);
            foreach (byte b in bytes)
            {
                if (NeedsEscape(b))
                    sb.Append('#').Append(b.ToString("X2", CultureInfo.InvariantCulture));
                else
                    sb.Append((char)b);
            }
            return sb.ToString();
        }

        /// <summary>
        /// The inverse of <see cref="Escape(string)"/>: the bytes of a name, <b>after</b> its <c>#XX</c>
        /// sequences have been expanded, become the name's value.
        /// </summary>
        /// <remarks>
        /// <para><b>UTF-8, falling back to Latin-1 when the bytes are not valid UTF-8.</b> Both
        /// halves are load-bearing. UTF-8 is what <see cref="Escape(string)"/> produces, so without it a
        /// name this library wrote does not survive being read back. The fallback is what makes
        /// changing the reader safe: a legacy file whose names are arbitrary bytes still parses
        /// exactly as it did before, because those bytes are not valid UTF-8 and the byte-per-char
        /// reading is what they get.</para>
        ///
        /// <para><b>The two readings agree on every ASCII name</b>, which is very nearly all of
        /// them - <c>/Type</c>, <c>/Font</c>, <c>/F1</c> - so the change cannot reach them at all.
        /// The ASCII scan below is that fact used as a fast path, not a third rule.</para>
        ///
        /// <para><b>Consistency never depended on which reading was chosen.</b> A name used as a
        /// dictionary key and a name used to look it up come through here alike, so they matched
        /// each other either way. What a Latin-1 reading breaks is the trip through
        /// <see cref="Escape(string)"/> and back: <c>/Caf#C3#A9</c> read as two Latin-1 characters would
        /// re-escape to <c>/Caf#C3#83#C2#A9</c>, the name growing on every read-modify-write.</para>
        ///
        /// <para>The strict decoder is built per call rather than held in a field, because this
        /// class deliberately has no static fields - see the type remarks - and the branch that
        /// needs it is only reached by a name carrying a byte above 0x7F.</para>
        /// </remarks>
        public static string Decode(byte[] bytes, int count) => Decode(bytes, count, out _);

        /// <summary>
        /// <see cref="Decode(byte[], int)"/>, saying whether the bytes were not UTF-8 — the value then
        /// their Latin-1 reading. The value alone cannot say it: <c>é</c> is both the UTF-8 reading of
        /// <c>C3 A9</c> and the Latin-1 reading of <c>E9</c>. ISO 32000-2 §7.3.5 makes a name's bytes
        /// UTF-8, and PDF/A-4 (ISO 19005-4 6.1.7) checks it.
        /// </summary>
        /// <param name="bytes">The name's bytes, its <c>#XX</c> sequences expanded.</param>
        /// <param name="count">How many of <paramref name="bytes"/> are the name's.</param>
        /// <param name="notUtf8">True when the bytes are not valid UTF-8.</param>
        public static string Decode(byte[] bytes, int count, out bool notUtf8)
        {
            notUtf8 = false;
            if (bytes == null || count <= 0) return string.Empty;

            for (int i = 0; i < count; i++)
            {
                if (bytes[i] < 0x80) continue;

                try
                {
                    return new UTF8Encoding(false, throwOnInvalidBytes: true)
                        .GetString(bytes, 0, count);
                }
                catch (DecoderFallbackException)
                {
                    notUtf8 = true;
                    return Text.Latin1Text.Decode(bytes, 0, count);
                }
            }

            return Text.Latin1Text.Decode(bytes, 0, count);
        }

        /// <summary>
        /// White space, anything outside printable ASCII, and the §7.2.2 delimiters - plus
        /// <c>#</c> itself, which would otherwise be read as the start of an escape.
        /// </summary>
        public static bool NeedsEscape(byte b)
            => b < 0x21 || b > 0x7E
            || b == (byte)'#' || b == (byte)'(' || b == (byte)')'
            || b == (byte)'<' || b == (byte)'>'
            || b == (byte)'[' || b == (byte)']'
            || b == (byte)'{' || b == (byte)'}'
            || b == (byte)'/' || b == (byte)'%';
    }
}
