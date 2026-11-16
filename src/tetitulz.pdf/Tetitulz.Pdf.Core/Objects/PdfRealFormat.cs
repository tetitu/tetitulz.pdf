// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace Tetitulz.Pdf.Core.Objects
{
    /// <summary>
    /// How a PDF real number is written (§7.3.3): fixed-point, at most six decimal places, trailing
    /// zeros stripped, invariant culture, never scientific notation, never a non-finite keyword.
    ///
    /// <para><b>The rule had three byte-identical implementations</b> — <see cref="PdfNumber"/>'s
    /// <c>ToString</c>, <c>PdfContentStreamBuilder.FormatNumber</c> and the number branch of
    /// <c>PdfWriter</c> — and only the last of the three refused a non-finite value. That is the
    /// shape of duplication that costs most: two of the copies were exactly right about the parts
    /// anyone thinks to check (culture, notation, precision) and silently wrong about the part that
    /// corrupts a file.</para>
    ///
    /// <para><b>Why a non-finite value is not a theoretical case.</b> <c>"F6"</c> formats one as the
    /// bare keyword <c>NaN</c> / <c>Infinity</c> / <c>-Infinity</c>, which no reader accepts, and it
    /// does so without throwing. NaN propagates rather than trapping, and comparisons against it are
    /// false — so an upstream guard written as <c>if (width &lt;= 0) return;</c> passes NaN straight
    /// through. A layout that divided by zero shipped <c>/MediaBox [0 0 NaN 842]</c> as a file that
    /// parsed nowhere.</para>
    ///
    /// <para><b>Zero is the coercion, and the caller is told.</b> Clamping an infinity to §C.1's
    /// ±3.403×10<sup>38</sup> would write a 39-digit fixed-point number that is technically legal
    /// and certainly not what the producer meant; zero at least keeps the object well-formed.
    /// <see cref="TryFormat(double, out string)"/> reports it so a writer can count the coercions instead of hiding
    /// them — <c>PdfWriter.NonFiniteNumbersCoercedToZero</c> is the caller that does.</para>
    ///
    /// <para><b>No static fields</b>, only <c>const</c>s and pure functions, so the type has no
    /// initializer. <see cref="PdfNumber"/> calls it from its <c>ToString</c>, and adding a
    /// type-initialization edge to a type on that path is the failure this repository has already
    /// paid for once (see <c>PdfStringEscape</c>, split out of <see cref="PdfString"/> for the same
    /// reason).</para>
    /// </summary>
    public static class PdfRealFormat
    {
        /// <summary>Decimal places kept. Beyond this is output bloat, not precision a viewer uses.</summary>
        public const int MaxDecimals = 6;

        private const string FixedPoint = "F6";

        /// <summary>What a non-finite value is written as.</summary>
        public const string NonFiniteSubstitute = "0";

        /// <summary>
        /// Formats <paramref name="v"/>, substituting <see cref="NonFiniteSubstitute"/> for NaN and
        /// ±Infinity. Use <see cref="TryFormat(double, out string)"/> where the substitution needs to be reported.
        /// </summary>
        public static string Format(double v) => TryFormat(v, out string s) ? s : s;

        /// <summary>
        /// Appends <paramref name="v"/> to <paramref name="sb"/> in exactly the form
        /// <see cref="Format(double)"/> returns, <b>without allocating the intermediate string</b>.
        /// </summary>
        /// <param name="sb">The destination.</param>
        /// <param name="v">The value.</param>
        /// <remarks>
        /// <para><b>Why a second entry point rather than a faster caller.</b> A content-stream
        /// writer emitting <c>x y m</c> allocated one string per number purely to copy it into a
        /// builder and drop it. The obvious fix — format into a span at the call site — would put a
        /// second copy of the §7.3.3 rule in the emitter, which is what this type's own summary
        /// records having cost this repository three times. The rule stays here; only the
        /// destination changes.</para>
        ///
        /// <para><b>The two paths must agree byte for byte</b>, and are asserted to by
        /// <c>RealFormatTrimEquivalenceTests</c> over the same sweep that pins
        /// <see cref="TryFormat(double, out string)"/>. The trim is duplicated below rather than
        /// shared because sharing it would mean materialising the string this exists to avoid — so
        /// the equivalence test is not optional tidiness, it is the only thing holding the two
        /// halves together.</para>
        ///
        /// <para>On <c>netstandard2.0</c> there is no <c>double.TryFormat</c>, so that leg simply
        /// forwards to <see cref="Format(double)"/> — correct, just without the saving. The
        /// stack buffer is sized for ordinary coordinates; a value needing more (<c>"F6"</c> of a
        /// number near <c>double.MaxValue</c> runs to ~317 characters) falls back the same way
        /// rather than being truncated.</para>
        /// </remarks>
        public static void AppendTo(StringBuilder sb, double v)
        {
            if (sb == null) return;

            if (double.IsNaN(v) || double.IsInfinity(v))
            {
                sb.Append(NonFiniteSubstitute);
                return;
            }

#if NET
            System.Span<char> buffer = stackalloc char[64];
            if (v.TryFormat(buffer, out int written, FixedPoint, CultureInfo.InvariantCulture))
            {
                int end = written;
                while (end > 0 && buffer[end - 1] == '0') end--;
                if (end > 0 && buffer[end - 1] == '.') end--;

                if (end == 0 || (end == 1 && buffer[0] == '-')) sb.Append(NonFiniteSubstitute);
                else sb.Append(buffer.Slice(0, end));
                return;
            }
#endif
            sb.Append(Format(v));
        }

        /// <summary>
        /// Formats <paramref name="v"/> with the caller's own numeric format string, substituting
        /// <see cref="NonFiniteSubstitute"/> for NaN and ±Infinity.
        /// </summary>
        /// <param name="v">The value.</param>
        /// <param name="format">
        /// A .NET numeric format string — <c>"F4"</c>, <c>"0.###"</c> and so on. It must not produce
        /// scientific notation, which §7.3.3 forbids; every fixed-point and custom-digit specifier
        /// satisfies that.
        /// </param>
        /// <remarks>
        /// <para><b>Why precision is a parameter here and fixed in <see cref="TryFormat(double, out string)"/>.</b>
        /// The content-stream writers write at different precisions — <c>"0.##"</c>, <c>"0.###"</c>,
        /// <c>"0.####"</c>, <c>"0.######"</c>, <c>"F2"</c>, <c>"F3"</c>, <c>"F4"</c>. Those
        /// differences are deliberate and their output bytes are what this project ships, so
        /// collapsing them onto <c>F6</c>-then-trim would rewrite every one of those files for no
        /// correctness gain.</para>
        ///
        /// <para><b>What was <i>not</i> deliberate is that none of the nine tested for a non-finite
        /// value</b>, so each emitted the bare keyword <c>NaN</c> or <c>Infinity</c> — not a §7.3.3
        /// number, and a strict reader may reject the whole content stream rather than one operand.
        /// This overload lets a caller keep its precision and lose that defect, which is the only
        /// part of the rule they all needed to share.</para>
        /// </remarks>
        /// <param name="text">
        /// The §7.3.3 number literal, or <c>null</c> when <paramref name="v"/> is non-finite and
        /// therefore has no representation as one.
        /// </param>
        public static bool TryFormat(double v, string format, out string text)
        {
            if (double.IsNaN(v) || double.IsInfinity(v))
            {
                text = NonFiniteSubstitute;
                return false;
            }

            text = v.ToString(format, CultureInfo.InvariantCulture);
            return true;
        }

        /// <summary>
        /// Formats <paramref name="v"/> with the caller's own format string, substituting
        /// <see cref="NonFiniteSubstitute"/> for a non-finite value.
        /// </summary>
        public static string Format(double v, string format)
            => TryFormat(v, format, out string s) ? s : s;

        /// <summary>
        /// Formats <paramref name="v"/> into <paramref name="text"/> and returns <c>false</c> when
        /// the value was non-finite and had to be replaced.
        /// </summary>
        /// <remarks>
        /// <paramref name="text"/> is always set to something writable, so a caller that ignores the
        /// return value still emits a well-formed file — the boolean is for reporting, not for
        /// deciding whether output exists.
        /// </remarks>
        public static bool TryFormat(double v, out string text)
        {
            if (double.IsNaN(v) || double.IsInfinity(v))
            {
                text = NonFiniteSubstitute;
                return false;
            }

            string s = v.ToString(FixedPoint, CultureInfo.InvariantCulture);

            // Find the trim boundary rather than allocating a string per trim. This is the same
            // rule the two chained TrimEnd calls expressed - strip trailing zeros, then a point
            // left bare - computed in one pass so the common case allocates nothing beyond the
            // ToString above.
            //
            // WHY IT IS SAFE TO STOP AT THE POINT: "F6" always emits a digit before the separator
            // and exactly six after it, so the '.' blocks the zero scan. Without that guarantee
            // this would eat the zeros of "100.000000" down to "1" - which is also why the
            // original's second TrimEnd could not be folded into the first.
            int end = s.Length;
            while (end > 0 && s[end - 1] == '0') end--;
            if (end > 0 && s[end - 1] == '.') end--;

            // Trimming empties the string for any |v| below half the sixth decimal: "0.000000"
            // loses its zeros and then its point. The `"-"` arm is defensive - "F6" always emits a
            // digit before the point, so a negative underflow arrives as "-0", which is a legal
            // §7.3.3 real and is left alone rather than normalised, because normalising it would
            // change bytes this project has been emitting for its whole history.
            text = end == 0 || (end == 1 && s[0] == '-')
                ? NonFiniteSubstitute
                : end == s.Length ? s : s.Substring(0, end);
            return true;
        }
    }
}
