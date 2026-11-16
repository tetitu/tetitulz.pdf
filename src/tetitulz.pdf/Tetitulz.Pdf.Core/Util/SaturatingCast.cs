// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;

namespace Tetitulz.Pdf.Core.Util
{
    /// <summary>
    /// A floating-point value as an integer type, the same on every runtime: truncated toward zero
    /// inside the type's range, the nearer bound outside it, and 0 for NaN.
    /// </summary>
    /// <remarks>
    /// <para><b>Not a cast.</b> An explicit <c>(int)</c> of a <c>double</c> outside the range is
    /// saturated by .NET 9 and later and is <c>int.MinValue</c> on .NET Framework, which the
    /// netstandard2.0 build runs on — so a clamp after it, <c>Math.Min(width, (int)x)</c>, held on
    /// one runtime and let a negative through on the other. These are .NET 10's answers, written out,
    /// so a value a file supplies reaches the same integer everywhere.</para>
    /// <para>Every explicit conversion from <c>float</c> or <c>double</c> to an integer type in the
    /// library goes through here; <c>SaturatingCastGuardTests</c> finds any other by the compiler's
    /// own types, under both target frameworks' symbols.</para>
    /// </remarks>
    public static class SaturatingCast
    {
        /// <summary>The value as an <see cref="int"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ToInt(double value)
            => value != value ? 0                          // NaN
             : value >= int.MaxValue ? int.MaxValue
             : value <= int.MinValue ? int.MinValue
             : (int)value;

        /// <summary>The value as a <see cref="long"/>; 2⁶³, not <see cref="long.MaxValue"/>, is the first value past the range.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long ToLong(double value)
            => value != value ? 0L
             : value >= 9223372036854775808.0 ? long.MaxValue
             : value <= long.MinValue ? long.MinValue
             : (long)value;

        /// <summary>The value as a <see cref="uint"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint ToUInt(double value)
            => value != value || value <= 0 ? 0u
             : value >= uint.MaxValue ? uint.MaxValue
             : (uint)value;

        /// <summary>The value as a <see cref="ushort"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort ToUShort(double value)
            => value != value || value <= 0 ? (ushort)0
             : value >= ushort.MaxValue ? ushort.MaxValue
             : (ushort)value;

        /// <summary>The value as a <see cref="byte"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte ToByte(double value)
            => value != value || value <= 0 ? (byte)0
             : value >= byte.MaxValue ? byte.MaxValue
             : (byte)value;
    }
}
