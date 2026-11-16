// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

namespace Tetitulz.Pdf.Plugins.PostScript
{
    /// <summary>Combines field hashes into one, on both target frameworks.</summary>
    /// <remarks>
    /// <para><c>System.HashCode</c> would do this, but it is .NET Core 2.1+ and this assembly also
    /// ships a <c>netstandard2.0</c> leg.</para>
    /// <para>The multiply-and-xor shape matches <c>Core.Objects.PdfReference.GetHashCode</c>, the
    /// in-repo idiom for the same job. <b>The algorithm is not load-bearing</b>: the two callers hash
    /// <c>readonly struct</c>s whose hashes are never persisted, ordered or compared across
    /// processes, so any combiner is equivalent. What matters is only that each type's
    /// <c>Equals</c> and <c>GetHashCode</c> agree on which fields participate, which they do.</para>
    /// <para><c>unchecked</c> because overflow is the intent, not an error.</para>
    /// </remarks>
    internal static class PsHash
    {
        private const int Seed = 17;
        private const int Step = 397;

        public static int Combine(int a, int b, int c, int d, int e)
        {
            unchecked
            {
                int h = Seed;
                h = (h * Step) ^ a;
                h = (h * Step) ^ b;
                h = (h * Step) ^ c;
                h = (h * Step) ^ d;
                h = (h * Step) ^ e;
                return h;
            }
        }

        public static int Combine(int a, int b, int c, int d, int e, int f)
        {
            unchecked
            {
                return (Combine(a, b, c, d, e) * Step) ^ f;
            }
        }
    }
}
