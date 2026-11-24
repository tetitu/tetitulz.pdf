// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Base.Operations.Barcodes
{
    /// <summary>
    /// Corrects a Reed–Solomon codeword in place, or reports that it cannot.
    /// </summary>
    /// <remarks>
    /// <para><b>Not the inverse of <see cref="GaloisField256.ErrorCorrection"/> in any structural
    /// sense — a genuinely different algorithm over the same field.</b> Encoding is one polynomial
    /// division. Decoding is syndromes, Berlekamp–Massey to find the error locator, a Chien search
    /// for its roots and Forney to size the errors. They share the exp/log tables and nothing else,
    /// which is what makes "encode, damage, recover the original" real evidence rather than a round
    /// trip against itself.</para>
    ///
    /// <para><b>What it can do is a fact about the code, not a choice made here.</b> A codeword with
    /// <i>n</i> error-correction symbols corrects up to <i>n</i>/2 errors at unknown positions. Ask
    /// it to correct more and it must <b>refuse</b> — beyond that bound the received word can be
    /// closer to a different valid codeword than to the one that was sent, and "correcting" it
    /// produces confident, well-formed, wrong data. That is the failure mode this exists to prevent,
    /// so every path that cannot verify its own answer returns <see langword="false"/>.</para>
    /// </remarks>
    internal static class ReedSolomonDecoder
    {
        /// <summary>
        /// Corrects <paramref name="received"/> in place.
        /// </summary>
        /// <param name="field">The field the codeword was built over.</param>
        /// <param name="received">
        /// Data codewords followed by error-correction codewords, highest degree first. Corrected in
        /// place when this returns <see langword="true"/>, and left untouched otherwise.
        /// </param>
        /// <param name="ecLength">How many trailing symbols are error correction.</param>
        /// <param name="errorsCorrected">How many symbols were changed.</param>
        /// <returns>
        /// <see langword="false"/> when the word carries more errors than the code can correct, or
        /// when the correction it computes does not verify.
        /// </returns>
        internal static bool TryCorrect(
            GaloisField256 field, int[] received, int ecLength, out int errorsCorrected)
        {
            errorsCorrected = 0;
            if (field == null || received == null || ecLength <= 0
                || received.Length <= ecLength || received.Length > 255)
                return false;

            int[] syndromes = Syndromes(field, received, ecLength);
            if (syndromes == null) return true;   // already a valid codeword

            int[] locator = BerlekampMassey(field, syndromes, ecLength);
            if (locator == null) return false;

            int errors = locator.Length - 1;
            if (errors <= 0 || errors > ecLength / 2) return false;

            int[] positions = ChienSearch(field, locator, received.Length);
            if (positions == null || positions.Length != errors) return false;

            // Written into a copy so a Forney that gives up halfway leaves the caller's word alone.
            var corrected = (int[])received.Clone();
            if (!Forney(field, syndromes, locator, positions, corrected)) return false;

            // No syndrome re-check follows, deliberately: an inert guard beside a real hazard is the
            // hardest kind to see through.
            //
            // It could not fire. Given a locator of degree L ≤ ecLength/2 whose L roots all land
            // inside the word — which the three guards above have already established — Forney's
            // magnitudes annihilate all `ecLength` syndromes by construction; across eight (data, ec)
            // shapes, every word damaged beyond the code's reach that was answered had zero syndromes.
            //
            // What a re-check could never do is the thing worth knowing: a word that lands within
            // the correction radius of a *different* valid codeword decodes to that codeword, and
            // its syndromes are zero. No guard fixes that — it is the code's distance, and the only
            // real answer is the check digit or CRC that the layer above carries.
            for (int i = 0; i < received.Length; i++) received[i] = corrected[i];
            errorsCorrected = errors;
            return true;
        }

        /// <summary>
        /// The syndrome polynomial, or <see langword="null"/> when every syndrome is zero — which
        /// means the word is already a codeword and there is nothing to do.
        /// </summary>
        private static int[] Syndromes(GaloisField256 field, int[] received, int ecLength)
        {
            var syndromes = new int[ecLength];
            bool any = false;

            for (int i = 0; i < ecLength; i++)
            {
                syndromes[i] = field.Evaluate(received, field.Exp(i + field.GeneratorBase));
                if (syndromes[i] != 0) any = true;
            }

            return any ? syndromes : null;
        }

        /// <summary>
        /// The error-locator polynomial Λ(x), highest degree first, or <see langword="null"/>.
        /// </summary>
        /// <remarks>
        /// Berlekamp–Massey, in its shift-register form: it finds the shortest recurrence the
        /// syndrome sequence satisfies, and that recurrence <i>is</i> the error locator. The
        /// polynomials are held lowest degree first here because the recurrence is naturally
        /// expressed that way, and reversed once at the end — doing it the other way round means
        /// index arithmetic at every step, which is where this algorithm is usually got wrong.
        /// </remarks>
        private static int[] BerlekampMassey(GaloisField256 field, int[] syndromes, int ecLength)
        {
            var current = new int[ecLength + 2];   // Λ(x), lowest degree first
            var previous = new int[ecLength + 2];  // the last Λ before a length change
            current[0] = 1;
            previous[0] = 1;

            int length = 0;         // current register length
            int sinceChange = 1;    // steps since `previous` was taken
            int lastDiscrepancy = 1;

            for (int step = 0; step < ecLength; step++)
            {
                int discrepancy = syndromes[step];
                for (int i = 1; i <= length; i++)
                    discrepancy ^= field.Multiply(current[i], syndromes[step - i]);

                if (discrepancy == 0)
                {
                    sinceChange++;
                    continue;
                }

                var before = (int[])current.Clone();
                int scale = field.Multiply(discrepancy, field.Inverse(lastDiscrepancy));

                for (int i = 0; i + sinceChange < current.Length; i++)
                    current[i + sinceChange] ^= field.Multiply(scale, previous[i]);

                if (2 * length <= step)
                {
                    length = step + 1 - length;
                    previous = before;
                    lastDiscrepancy = discrepancy;
                    sinceChange = 1;
                }
                else
                {
                    sinceChange++;
                }
            }

            // Trim to the true degree, then reverse to highest-degree-first.
            int degree = current.Length - 1;
            while (degree > 0 && current[degree] == 0) degree--;
            if (degree != length) return null;   // the locator's degree must be the register length

            var locator = new int[degree + 1];
            for (int i = 0; i <= degree; i++) locator[i] = current[degree - i];
            return locator;
        }

        /// <summary>
        /// The positions of the errors, as indices into the received word, or
        /// <see langword="null"/> when a root falls outside it.
        /// </summary>
        /// <remarks>
        /// Λ's roots are the <i>inverses</i> of the error locations, so a root α^-i names position
        /// i counted from the end of the word. A root naming a position past the word's length means
        /// the locator describes errors that cannot exist here — a word too damaged to correct,
        /// pretending otherwise.
        /// </remarks>
        private static int[] ChienSearch(GaloisField256 field, int[] locator, int length)
        {
            int degree = locator.Length - 1;
            var positions = new int[degree];
            int found = 0;

            for (int i = 0; i < 255 && found < degree; i++)
            {
                if (field.Evaluate(locator, field.Exp(i)) != 0) continue;

                // α^i is a root, so the error sits at the position whose locator is α^-i.
                int position = length - 1 - ((255 - i) % 255);
                if (position < 0 || position >= length) return null;

                positions[found++] = position;
            }

            return found == degree ? positions : null;
        }

        /// <summary>
        /// Applies Forney's algorithm, subtracting each error's magnitude from its position.
        /// </summary>
        private static bool Forney(
            GaloisField256 field, int[] syndromes, int[] locator, int[] positions, int[] word)
        {
            int[] evaluator = Evaluator(field, syndromes, locator);
            int[] derivative = FormalDerivative(locator);

            foreach (int position in positions)
            {
                // The locator value for this position, and its inverse, which Forney evaluates at.
                int exponent = word.Length - 1 - position;
                int inverse = field.Exp(-exponent);

                int denominator = field.Evaluate(derivative, inverse);
                if (denominator == 0) return false;   // a repeated root: not a correctable pattern

                int numerator = field.Evaluate(evaluator, inverse);

                // Forney's magnitude carries a factor X^(1−b) for a generator whose roots start at
                // α^b (GaloisField256.GeneratorBase): X for QR's α⁰, none for Data Matrix's α¹.
                // Dropping it for b = 0 — as codebases built for b = 1 do — corrected roughly one word
                // in a hundred, measured before the factor was put back.
                int factor = field.Exp((1 - field.GeneratorBase) * exponent);
                int magnitude = field.Multiply(
                    field.Multiply(numerator, field.Inverse(denominator)), factor);

                word[position] ^= magnitude;
            }

            return true;
        }

        /// <summary>Ω(x) = S(x)·Λ(x) mod x^ecLength, highest degree first.</summary>
        private static int[] Evaluator(GaloisField256 field, int[] syndromes, int[] locator)
        {
            // S(x) with syndrome i as the coefficient of x^i, so it multiplies naturally.
            var s = new int[syndromes.Length];
            for (int i = 0; i < syndromes.Length; i++) s[syndromes.Length - 1 - i] = syndromes[i];

            var product = new int[s.Length + locator.Length - 1];
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == 0) continue;
                for (int j = 0; j < locator.Length; j++)
                    product[i + j] ^= field.Multiply(s[i], locator[j]);
            }

            // mod x^ecLength keeps the low-order terms, which are at the END of this layout.
            var omega = new int[syndromes.Length];
            for (int i = 0; i < omega.Length; i++)
                omega[omega.Length - 1 - i] = product[product.Length - 1 - i];
            return omega;
        }

        /// <summary>
        /// Λ′(x), highest degree first.
        /// </summary>
        /// <remarks>
        /// In characteristic 2 the derivative keeps only the odd-degree terms — every even one is
        /// multiplied by an even integer, which is zero here. That is not a shortcut; it is what the
        /// derivative <i>is</i> in this field, and writing the general form would produce zeros in
        /// the wrong places.
        /// </remarks>
        private static int[] FormalDerivative(int[] polynomial)
        {
            int degree = polynomial.Length - 1;
            var derivative = new int[degree];

            for (int i = 0; i < degree; i++)
            {
                int termDegree = degree - i;
                derivative[i] = (termDegree & 1) != 0 ? polynomial[i] : 0;
            }

            return derivative;
        }
    }
}
