// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

namespace Tetitulz.Pdf.Plugins.PostScript
{
    /// <summary>Numeric predicates the interpreter needs on both target frameworks.</summary>
    internal static class PsNumeric
    {
        /// <summary>True when <paramref name="value"/> is neither NaN nor an infinity.</summary>
        /// <remarks>
        /// <para><c>double.IsFinite</c> would say this in one call, but it is .NET Core 2.1+ and this
        /// assembly also ships a <c>netstandard2.0</c> leg. The spelling here is the one
        /// <c>Core.Objects.PdfRealFormat</c> already uses for the same question, so the two agree by
        /// construction.</para>
        /// <para>One helper rather than the double negative written out at each of the twenty-one
        /// call sites: <c>!IsNaN(x) &amp;&amp; !IsInfinity(x)</c> repeated inline reads as noise, and a
        /// single spelling is what stops one site quietly disagreeing with the rest.</para>
        /// </remarks>
        public static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>
        /// The finite numbers of an array, or <c>null</c> when an element is not a number or not finite.
        /// </summary>
        public static double[] Finite(Interpreter.PsArray array)
        {
            var values = new double[array.Length];
            for (int k = 0; k < array.Length; k++)
            {
                if (!array[k].IsNumber || !IsFinite(array[k].Num)) return null;
                values[k] = array[k].Num;
            }
            return values;
        }

        /// <summary>
        /// A dictionary's array of finite numbers under <paramref name="key"/>: <paramref name="count"/>
        /// of them, or any non-empty count when it is negative. <paramref name="present"/> says whether
        /// the key is there; the answer is <c>null</c> when it is absent or malformed. The one reader of
        /// the CIE spaces', the shadings' and the tint functions' number arrays — three copies, one of
        /// which let NaN through.
        /// </summary>
        public static double[] FiniteArray(Interpreter.PsDict dict, string key, int count, out bool present)
        {
            present = dict.TryGetName(key, out Interpreter.PsObject value);
            if (!present || value.Type != Interpreter.PsType.Array) return null;
            Interpreter.PsArray array = value.AsArray();
            if (count >= 0 ? array.Length != count : array.Length == 0) return null;
            return Finite(array);
        }
    }
}
