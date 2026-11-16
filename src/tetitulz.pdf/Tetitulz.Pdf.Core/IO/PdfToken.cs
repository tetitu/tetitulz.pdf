// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.IO
{
    public sealed class PdfToken
    {
        public PdfTokenType Type { get; }
        public string Value { get; }
        public long Position { get; }

        public PdfToken(PdfTokenType type, string value, long position)
        {
            Type = type;
            Value = value;
            Position = position;
        }

        /// <summary>A token whose name bytes were not UTF-8 when <paramref name="nameIsNotUtf8"/>.</summary>
        public PdfToken(PdfTokenType type, string value, long position, bool nameIsNotUtf8)
            : this(type, value, position)
        {
            NameIsNotUtf8 = nameIsNotUtf8;
        }

        /// <summary>
        /// A name token whose bytes were not valid UTF-8, so <see cref="Value"/> is their Latin-1 reading
        /// (<see cref="Objects.PdfNameEscape.Decode(byte[], int, out bool)"/>); false for every other token.
        /// </summary>
        public bool NameIsNotUtf8 { get; }

        // Overflow/format-safe numeric accessors. Token values come from untrusted input;
        // a digit run longer than int/long must NOT throw an uncaught OverflowException out
        // of the xref/parse path (it would crash document open and defeat scan-recovery).
        // Oversized integers saturate to the int/long range; unparseable values yield 0.
        public int IntValue
        {
            get
            {
                long v = SaturatedLong(Value);
                return v > int.MaxValue ? int.MaxValue : v < int.MinValue ? int.MinValue : (int)v;
            }
        }

        public long LongValue => SaturatedLong(Value);

        /// <summary>
        /// The integer <paramref name="value"/> spells, saturated to the <see cref="long"/> range;
        /// 0 for anything that is not an optionally signed digit run.
        /// </summary>
        /// <remarks>
        /// A digit run is saturated whatever its length, and only a value that is not an integer at
        /// all reads as 0. Resting both accessors on one <c>long.TryParse</c> would hold the
        /// saturation only between <c>int</c> and <c>long</c>: a twenty-digit integer would read as 0
        /// through <b>both</b> — a <c>/Count</c> of none, a subsection starting at object 0.
        /// </remarks>
        private static long SaturatedLong(string value)
        {
            if (long.TryParse(value, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out long v))
                return v;
            if (string.IsNullOrEmpty(value)) return 0;

            int i = value[0] == '-' || value[0] == '+' ? 1 : 0;
            if (i == value.Length) return 0;
            for (; i < value.Length; i++)
                if (value[i] < '0' || value[i] > '9') return 0;
            return value[0] == '-' ? long.MinValue : long.MaxValue;
        }
        public double DoubleValue => double.TryParse(Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 0;

        public bool BoolValue
        {
            get
            {
                if (Type != PdfTokenType.Boolean)
                    throw new System.InvalidOperationException("Token is not a boolean.");
                return Value == "true";
            }
        }

        public override string ToString() => $"{Type}: {Value}";
    }
}
