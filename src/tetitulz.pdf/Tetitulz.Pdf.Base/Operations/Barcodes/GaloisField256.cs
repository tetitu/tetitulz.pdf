// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Base.Operations.Barcodes
{
    /// <summary>
    /// GF(256) log/antilog tables, Reed–Solomon generator polynomials and the RS remainder
    /// encoder — the arithmetic every 2-D barcode in this folder needs.
    ///
    /// <para>One field for <c>QrCodeEncoder</c> and <c>DataMatrixEncoder</c>. The <b>only</b>
    /// difference between them is the primitive polynomial, and that difference is correct rather
    /// than drift: 0x11D is ISO/IEC 18004 (QR) and 0x12D is ISO/IEC 16022 ECC-200 (Data Matrix). It
    /// is a constructor argument, so the two encoders state which standard they follow instead of
    /// each carrying a copy of the arithmetic.</para>
    /// </summary>
    /// <remarks>
    /// <b>An instance, immutable after construction, and neither of those is incidental.</b> Two
    /// hazards were documented at the sites this replaces and a naive extraction would reintroduce
    /// both:
    ///
    /// <list type="number">
    /// <item><b>Generators are precomputed, never lazily cached.</b> The QR encoder once filled a
    /// <c>Dictionary&lt;int,int[]&gt;</c> on demand, reached from the <b>public static</b>
    /// <c>PdfBarcodeWriter.GeneratePdf</c> with nothing declaring thread affinity and nothing
    /// serialising the write. Two server threads encoding payloads that select different versions
    /// could insert concurrently during a resize: either a torn read — a wrong generator, so a
    /// silently unscannable symbol — or the classic infinite loop inside <c>TryGetValue</c>. There
    /// are only ever a handful of distinct degrees, so precomputing removes the shared mutable
    /// state rather than locking around it. <b>Do not add a cache here.</b></item>
    /// <item><b>The tables are built before the generators, in that order, inside the
    /// constructor.</b> At the static sites this had to be spelled out because field initializers
    /// run before a static constructor body, so a generator built in one would have multiplied
    /// through all-zero tables. An instance constructor makes the ordering ordinary rather than a
    /// trap — but the ordering is still load-bearing, so it is stated.</item>
    /// </list>
    ///
    /// <para>No static fields, so no type initializer: a leaf by construction, per CLAUDE.md.</para>
    /// </remarks>
    internal sealed class GaloisField256
    {
        /// <summary>ISO/IEC 18004 — QR Code.</summary>
        internal const int QrPrimitive = 0x11D;

        /// <summary>ISO/IEC 16022 — Data Matrix ECC-200.</summary>
        internal const int DataMatrixPrimitive = 0x12D;

        /// <summary>QR's generator roots start at α⁰ (ISO/IEC 18004).</summary>
        internal const int QrGeneratorBase = 0;

        /// <summary>
        /// Data Matrix's start at α¹ (ISO/IEC 16022). An encoder and reader both using α⁰ would pass
        /// every round trip while an independent reader finds no symbol in what the library writes.
        /// </summary>
        internal const int DataMatrixGeneratorBase = 1;

        /// <summary>The power of α the generator's roots start at — what the syndromes and Forney's factor follow too.</summary>
        internal int GeneratorBase { get; }

        private readonly int[] _exp = new int[512];
        private readonly int[] _log = new int[256];
        private readonly int[][] _generators;

        /// <param name="primitivePolynomial">
        /// The field's primitive polynomial — <see cref="QrPrimitive"/> or
        /// <see cref="DataMatrixPrimitive"/>.
        /// </param>
        /// <param name="maxGeneratorDegree">
        /// Generators for every degree from 0 to this are computed up front; see the remarks on the
        /// type for why they are not computed on demand.
        /// </param>
        /// <param name="generatorBase">
        /// The power of α the generator's roots start at: 0 for QR (<see cref="QrGeneratorBase"/>),
        /// 1 for Data Matrix (<see cref="DataMatrixGeneratorBase"/>) — as independent implementations
        /// gives the two.
        /// </param>
        internal GaloisField256(int primitivePolynomial, int maxGeneratorDegree, int generatorBase = QrGeneratorBase)
        {
            GeneratorBase = generatorBase;
            int x = 1;
            for (int i = 0; i < 255; i++)
            {
                _exp[i] = x;
                _log[x] = i;
                x <<= 1;
                if ((x & 0x100) != 0) x ^= primitivePolynomial;
            }
            for (int i = 255; i < 512; i++) _exp[i] = _exp[i - 255];

            // After the tables, never before: a generator is computed THROUGH them.
            _generators = new int[maxGeneratorDegree + 1][];
            for (int degree = 0; degree <= maxGeneratorDegree; degree++)
                _generators[degree] = ComputeGenerator(degree);
        }

        /// <summary>
        /// The generator polynomial of the given degree — precomputed where possible, computed on
        /// the spot for a degree beyond the table (which is never cached; see the remarks).
        /// </summary>
        internal int[] Generator(int degree)
            => degree >= 0 && degree < _generators.Length ? _generators[degree] : ComputeGenerator(degree);

        /// <summary>Builds (x − α^b)(x − α^(b+1))…(x − α^(b+degree−1)), b the field's <see cref="GeneratorBase"/>.</summary>
        private int[] ComputeGenerator(int degree)
        {
            int[] g = { 1 };
            for (int i = 0; i < degree; i++)
            {
                var next = new int[g.Length + 1];
                for (int j = 0; j < g.Length; j++)
                {
                    next[j] ^= g[j];                                            // shift up = multiply by x
                    int prod = g[j] == 0 ? 0 : _exp[(_log[g[j]] + i + GeneratorBase) % 255];
                    next[j + 1] ^= prod;                                        // + α^i · g[j]
                }
                g = next;
            }
            return g;
        }

        /// <summary>
        /// The <paramref name="ecLen"/> Reed–Solomon error-correction codewords for
        /// <paramref name="data"/>.
        /// </summary>
        /// <remarks>
        /// Returns <c>int[]</c> because Data Matrix works in ints. Every GF(256) element is 0–255,
        /// so the QR encoder's narrowing to <c>byte</c> at its call site is lossless — which is why
        /// the two element types were never a divergence.
        /// </remarks>
        internal int[] ErrorCorrection(int[] data, int ecLen)
        {
            int[] gen = Generator(ecLen);
            var buf = new int[data.Length + ecLen];
            for (int i = 0; i < data.Length; i++) buf[i] = data[i];

            for (int i = 0; i < data.Length; i++)
            {
                int coef = buf[i];
                if (coef == 0) continue;
                int lc = _log[coef];
                for (int j = 0; j < gen.Length; j++)
                {
                    int g = gen[j];
                    if (g == 0) continue;
                    buf[i + j] ^= _exp[(_log[g] + lc) % 255];
                }
            }

            var ec = new int[ecLen];
            for (int i = 0; i < ecLen; i++) ec[i] = buf[data.Length + i];
            return ec;
        }

        // ==============================================================
        //  Field arithmetic, for the decode side
        // ==============================================================
        //
        //  The encoder above needs only "multiply, in a loop", and reaches into _exp/_log
        //  directly to get it. Reed-Solomon *decoding* needs the field as a field -- inverses,
        //  division, evaluation at a point -- so those are named here rather than open-coded at
        //  the call sites, where an off-by-one in a `% 255` is invisible.

        /// <summary>α raised to <paramref name="power"/>.</summary>
        /// <remarks>
        /// The table is doubled to 512 entries precisely so a sum of two logarithms needs no
        /// reduction; a negative power still does, and that is what the addition of 255 is for.
        /// </remarks>
        internal int Exp(int power) => _exp[((power % 255) + 255) % 255];

        /// <summary>The discrete logarithm of a non-zero field element.</summary>
        internal int Log(int value) => _log[value];

        /// <summary>Product of two field elements.</summary>
        internal int Multiply(int a, int b)
            => a == 0 || b == 0 ? 0 : _exp[(_log[a] + _log[b]) % 255];

        /// <summary>
        /// The multiplicative inverse of a non-zero element.
        /// </summary>
        /// <remarks>
        /// Zero has no inverse and is not passed one: every caller here divides by a value it has
        /// already established is non-zero — an error-locator derivative at a root, or a leading
        /// coefficient. Returning something for zero would turn that established fact into an
        /// assumption nobody checks.
        /// </remarks>
        internal int Inverse(int a) => _exp[255 - _log[a]];

        /// <summary>
        /// Evaluates a polynomial at <paramref name="x"/>, coefficients highest degree first.
        /// </summary>
        internal int Evaluate(int[] polynomial, int x)
        {
            int result = 0;
            for (int i = 0; i < polynomial.Length; i++)
                result = Multiply(result, x) ^ polynomial[i];
            return result;
        }
    }
}
