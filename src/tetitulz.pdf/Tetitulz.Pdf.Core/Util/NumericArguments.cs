// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Core.Util
{
    /// <summary>
    /// The checks a public entry point makes on a caller's number before it reaches a file or a
    /// render — one implementation for Core's writers, Base's operations and renderers, and the
    /// producers above them.
    /// </summary>
    /// <remarks>
    /// <para><b>Finite first.</b> NaN is neither <c>&lt;= 0</c> nor <c>&gt; 0</c>, so a size or
    /// position check written as a comparison lets it through; it then reaches
    /// <c>PdfRealFormat</c>, which writes <c>0</c> for a non-finite value (so the file stays
    /// well-formed), or <c>Math.Max(…, 1)</c>, which returns NaN. Either way the caller's argument
    /// error comes back as a silently degenerate result — a 1×1 tile, a header drawn at the
    /// origin, a page with no size — where a zero would have thrown. This is the one place the rule
    /// is written, for the tile entries, the ink-coverage dpi, the operations' numeric options and
    /// <c>PdfDocumentBuilder.AddPage</c> alike.</para>
    /// <para><b>Naming.</b> <c>paramName</c> is the parameter the exception names; an options
    /// object's member is named in the message through <c>member</c>
    /// (<c>options.FontSize must be positive.</c>), so a caller reads which value was wrong.</para>
    /// <para><b>Public, for the plugins.</b> A plugin does not see Core's internals, and the rule
    /// forbids a private copy of this check (a copy nothing tests); <c>Plugins.PostScript</c> is the
    /// first to refuse a caller's matrix through it.</para>
    /// </remarks>
    public static class NumericArguments
    {
        /// <summary>Neither NaN nor an infinity. The netstandard2.0 build has no <c>double.IsFinite</c>.</summary>
        public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        /// <summary>Refuses NaN and ±∞.</summary>
        /// <param name="value">The caller's number.</param>
        /// <param name="paramName">The parameter the exception names.</param>
        /// <param name="member">The options member the value came from, for the message; null for a plain parameter.</param>
        /// <exception cref="ArgumentOutOfRangeException">The value is NaN or an infinity.</exception>
        public static void RequireFinite(double value, string paramName, string member = null)
        {
            if (!IsFinite(value))
                throw new ArgumentOutOfRangeException(paramName, value, Label(paramName, member) + " must be a finite number.");
        }

        /// <summary>Refuses NaN, ±∞ and anything not above 0.</summary>
        /// <inheritdoc cref="RequireFinite"/>
        /// <exception cref="ArgumentOutOfRangeException">The value is non-finite or not positive.</exception>
        public static void RequirePositive(double value, string paramName, string member = null)
        {
            RequireFinite(value, paramName, member);
            if (value <= 0)
                throw new ArgumentOutOfRangeException(paramName, value, Label(paramName, member) + " must be positive.");
        }

        /// <summary>Refuses NaN, ±∞ and anything below 0.</summary>
        /// <inheritdoc cref="RequireFinite"/>
        /// <exception cref="ArgumentOutOfRangeException">The value is non-finite or negative.</exception>
        public static void RequireNonNegative(double value, string paramName, string member = null)
        {
            RequireFinite(value, paramName, member);
            if (value < 0)
                throw new ArgumentOutOfRangeException(paramName, value, Label(paramName, member) + " must be at least 0.");
        }

        private static string Label(string paramName, string member)
            => member == null ? paramName : paramName + "." + member;
    }
}
