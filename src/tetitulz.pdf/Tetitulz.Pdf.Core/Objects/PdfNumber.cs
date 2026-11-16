// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Objects
{
    public sealed class PdfNumber : PdfObject
    {
        private readonly double _value;

        public PdfNumber(int value) { _value = value; IsInteger = true; }
        public PdfNumber(long value) { _value = value; IsInteger = true; }
        public PdfNumber(double value)
        {
            _value = value;
            // Only flag as integer when the whole value also fits in a long;
            // otherwise the integer-formatting / IntValue / LongValue casts below
            // would silently truncate (e.g. 1e30 → long.MinValue).
            IsInteger = Math.Abs(value % 1) < double.Epsilon
                        && value >= long.MinValue && value <= long.MaxValue;
        }

        public bool IsInteger { get; }
        /// <summary>
        /// The number as an <c>int</c>: truncated toward zero inside the range, the nearer bound
        /// outside it, 0 for NaN.
        /// </summary>
        /// <remarks><see cref="SaturatingCast.ToInt"/>, not a cast: a <c>double</c> to <c>int</c> cast
        /// out of range saturates on .NET 9 and later but yields <c>int.MinValue</c> on .NET Framework,
        /// which the netstandard2.0 build runs on — <c>/Columns 1e10</c> read as <c>int.MinValue</c>
        /// there. The package-consumption net462 run is the check that sees the difference.</remarks>
        public int IntValue => SaturatingCast.ToInt(_value);

        /// <summary>The number as a <c>long</c>, by <see cref="IntValue"/>'s rule (<see cref="SaturatingCast.ToLong"/>).</summary>
        public long LongValue => SaturatingCast.ToLong(_value);
        public double DoubleValue => _value;

        /// <summary>
        /// The value read as a 32-bit <b>flag word</b> — the low-order 32 bits of an integer — or
        /// <c>null</c> when it is not an integer at all.
        /// </summary>
        /// <remarks>
        /// <para><b>The one reading of a PDF bit field</b>, for the encryption dictionary's
        /// <c>/P</c> (<see cref="Document.PdfPermissionValue"/>) and an annotation's <c>/F</c>
        /// (<see cref="Constants.PdfAnnotationFlags.FromNumber"/>). Both are 32-bit words that
        /// producers write in either the signed or the unsigned spelling of one bit pattern, and the
        /// low 32 bits accept both.</para>
        ///
        /// <para><b>Not <see cref="IntValue"/></b>, whose <c>(int)</c> cast on a <c>double</c>
        /// <b>saturates</b>: <c>/F 2147483648</c> — bit 32 alone, no named flag — came back as
        /// <c>int.MaxValue</c>, every named flag set, and the annotation hidden; the readers that went
        /// through <see cref="LongValue"/> instead saw bit 32 and drew it. <b>Not an unchecked
        /// <see cref="LongValue"/> either</b> when the number is not an integer: <c>1e30</c>
        /// saturates to <c>long.MaxValue</c>, whose low word is all ones — every permission granted,
        /// every flag set, from a value no producer writes. A number that is not an integer is not
        /// a flag word, and <c>null</c> lets each caller say what "unreadable" means for it.</para>
        /// </remarks>
        public int? FlagWord => IsInteger ? unchecked((int)SaturatingCast.ToLong(_value)) : (int?)null;

        public override string ToString()
        {
            if (IsInteger)
                return (SaturatingCast.ToLong(_value)).ToString(CultureInfo.InvariantCulture);
            // PdfRealFormat, not a local copy of "F6 + trim". This was one of three copies of that
            // rule and one of the two that wrote `NaN` / `Infinity` — tokens §7.3.3 has no syntax
            // for — straight into the output with no exception and no signal.
            return PdfRealFormat.Format(_value);
        }
    }
}
