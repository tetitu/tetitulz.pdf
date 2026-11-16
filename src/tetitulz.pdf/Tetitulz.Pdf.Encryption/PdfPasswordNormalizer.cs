// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Tetitulz.Pdf.Encryption
{
    /// <summary>
    /// Unicode password normalisation for PDF 2.0 AES-256 (V=5, R=6) encryption.
    /// Per ISO 32000-2 §7.6.4.3.2, passwords must be processed through SASLprep
    /// (RFC 4013) before key derivation. This pragmatic implementation applies
    /// the subset of SASLprep that matters in practice:
    ///   - Unicode NFKC (compatibility-composition) normalisation
    ///   - Map a few common non-ASCII whitespace code points to U+0020
    ///   - Reject prohibited C0/C1 control characters
    ///   - Replace a lone surrogate with U+FFFD (the 127-byte cut is TruncateUtf8's)
    /// </summary>
    internal static class PdfPasswordNormalizer
    {
        public static string Normalize(string password)
        {
            if (string.IsNullOrEmpty(password)) return password ?? string.Empty;

            // Step 1 (SASLprep §2.1): map certain code points.
            var sb = new StringBuilder(password.Length);
            for (int i = 0; i < password.Length; i++)
            {
                char c = password[i];
                // A lone surrogate makes String.Normalize throw; it is no character, so it becomes
                // U+FFFD on both sides, and every door answers "does not match" rather than throwing.
                if (char.IsHighSurrogate(c) && i + 1 < password.Length && char.IsLowSurrogate(password[i + 1]))
                {
                    sb.Append(c).Append(password[++i]);
                    continue;
                }
                if (char.IsSurrogate(c)) sb.Append('\uFFFD');
                else if (IsMappedToSpace(c)) sb.Append(' ');
                else if (IsSoftIgnored(c)) { /* drop */ }
                else sb.Append(c);
            }

            // Step 2 (SASLprep §2.2): Unicode normalisation (NFKC).
            string nfkc = sb.ToString().Normalize(NormalizationForm.FormKC);

            // Step 3 (SASLprep §2.3): prohibit certain code points.  We reject only
            // the C0/C1 controls; everything else passes through (we're permissive,
            // unlike strict SASLprep which would reject non-ASCII categories).
            // The cleaned form is built in one pass: replacing each disallowed character in turn
            // (`nfkc.Replace(c.ToString(), "")`) would be O(n²) and would iterate the ORIGINAL
            // string while mutating a different one.
            bool needsFilter = false;
            foreach (char c in nfkc)
                if (c < 0x20 || (c >= 0x7F && c <= 0x9F)) { needsFilter = true; break; }
            if (needsFilter)
            {
                var cleaned = new StringBuilder(nfkc.Length);
                foreach (char c in nfkc)
                    if (!(c < 0x20 || (c >= 0x7F && c <= 0x9F))) cleaned.Append(c);
                nfkc = cleaned.ToString();
            }

            // Step 4, the 127-byte limit, is the caller's: PdfEncryptionPrimitives.TruncateUtf8 cuts
            // the UTF-8 bytes at exactly 127 (ISO 32000-2 §7.6.4.3.3 step b, "truncate the UTF-8
            // representation to 127 bytes"), mid-character if that is where 127 falls.
            return nfkc;
        }

        private static bool IsMappedToSpace(char c) =>
            c == '\u00A0' || c == '\u1680' ||
            (c >= '\u2000' && c <= '\u200A') ||
            c == '\u202F' || c == '\u205F' || c == '\u3000';

        private static bool IsSoftIgnored(char c) =>
            c == '\u00AD' || c == '\u034F' || c == '\u1806' ||
            (c >= '\u180B' && c <= '\u180D') ||
            (c >= '\u200B' && c <= '\u200D') ||
            c == '\u2060' || c == '\uFEFF' ||
            (c >= '\uFE00' && c <= '\uFE0F');
    }
}
