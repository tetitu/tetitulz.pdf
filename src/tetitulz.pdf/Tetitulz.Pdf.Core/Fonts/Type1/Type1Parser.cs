// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;

namespace Tetitulz.Pdf.Core.Fonts.Type1
{
    /// <summary>
    /// Parses a Type 1 PostScript font program embedded in a PDF /FontFile stream.
    ///
    /// A /FontFile stream has three concatenated regions (ATN #5002 §2):
    ///   [Length1 bytes] ASCII cleartext PostScript
    ///   [Length2 bytes] Binary eexec-encrypted section
    ///
    /// This parser extracts:
    ///   1. The /Encoding vector from the ASCII cleartext section
    ///   2. /Subrs and /CharStrings from the decrypted binary section
    /// </summary>
    internal static class Type1Parser
    {
        // Latin1 (ISO-8859-1) is a 1:1 byte→code-point mapping. Decode it
        // manually so the code compiles on both net10.0 and netstandard2.0
        // (Encoding.Latin1 was only added in .NET 5, and GetEncoding(28591)
        // needs the CodePages provider on .NET Core hosts of netstandard2.0).
        // One implementation, in Core.Text.Latin1Text — six byte-identical copies existed.
        private static string Latin1(byte[] data, int index, int count)
            => Text.Latin1Text.Decode(data, index, count);

        /// <summary>
        /// How many bytes the last <see cref="Parse"/> on this thread examined looking for tokens
        /// in the eexec section — the evidence that its scans are linear in the section. The
        /// <c>/Subrs</c> walk looked for the block's closing <c>def</c> afresh from every
        /// <c>dup</c>, so a block of <i>n</i> subroutines before a <c>def</c> near the end of the
        /// section examined <i>n</i> × the section: 3 000 one-byte subroutines, 180 MB of reads.
        /// </summary>
        internal static long LastScanBytesExamined => _scanBytes;

        [ThreadStatic] private static long _scanBytes;

        // ----------------------------------------------------------------
        //  Public entry point
        // ----------------------------------------------------------------

        public static Type1Font Parse(byte[] data, int length1, int length2)
        {
            _scanBytes = 0;
            if (data == null || data.Length < 4) return null;
            if (length1 <= 0 || length2 <= 0) return null;
            // In long: /Length2 2147483647 wrapped the int sum negative, and the eexec section was
            // then copied into a 2 GB buffer before the copy failed.
            if ((long)length1 + length2 > data.Length) return null;

            try
            {
                // 1. Parse ASCII section for /Encoding and /FontMatrix
                string ascii = Latin1(data, 0, length1);
                string[] encoding = ParseEncoding(ascii);
                float fontMatrixScale = ParseFontMatrixScale(ascii, out bool widthScaleIsKnown);

                // 2. Decrypt the binary section with the eexec key
                byte[] decrypted = EexecDecrypt(data, length1, length2);
                if (decrypted == null) return null;

                // 3. Find lenIV (charstring seed bytes to skip; default 4)
                int lenIV = FindLenIV(decrypted);

                // 4. Extract /Subrs and /CharStrings using forward binary scanning
                var subrs      = ParseSubrs(decrypted, lenIV);
                var charstrings = ParseCharStrings(decrypted, lenIV, out bool charStringsComplete);
                if (charstrings == null || charstrings.Count == 0) return null;

                return new Type1Font(encoding, charstrings, subrs, fontMatrixScale,
                                     widthScaleIsKnown, charStringsComplete);
            }
            catch
            {
                return null;
            }
        }

        // ----------------------------------------------------------------
        //  Encoding extraction from ASCII cleartext section
        // ----------------------------------------------------------------

        private static string[] ParseEncoding(string ascii)
        {
            var enc = new string[256];
            for (int i = 0; i < 256; i++) enc[i] = ".notdef";

            int encIdx = ascii.IndexOf("/Encoding ", StringComparison.Ordinal);
            if (encIdx < 0) return enc;

            // "/Encoding StandardEncoding def" (Type 1 Font Format §2.3) names the standard table
            // instead of building an array.
            int after = encIdx + "/Encoding ".Length;
            SkipWS(ascii, ref after);
            if (string.CompareOrdinal(ascii, after, "StandardEncoding", 0, "StandardEncoding".Length) == 0)
            {
                var table = AdobeStandardEncodingNames.Table;
                for (int i = 0; i < 256; i++) enc[i] = table[i] ?? ".notdef";
                return enc;
            }

            // Scan for all "dup N /name put" patterns in the encoding block.
            // Stop at "currentdict" which follows the encoding definition.
            int stopIdx = ascii.IndexOf("currentdict", encIdx, StringComparison.Ordinal);
            int searchEnd = stopIdx > 0 ? stopIdx : ascii.Length;
            int pos = encIdx;
            while (pos < searchEnd)
            {
                int dupIdx = ascii.IndexOf("dup ", pos, StringComparison.Ordinal);
                if (dupIdx < 0 || dupIdx >= searchEnd) break;
                pos = dupIdx + 4;
                SkipWS(ascii, ref pos);
                if (!ReadInt(ascii, ref pos, out int code)) continue;
                SkipWS(ascii, ref pos);
                if (pos >= ascii.Length || ascii[pos] != '/') continue;
                pos++;
                string name = ReadToken(ascii, ref pos);
                if (code >= 0 && code < 256 && name.Length > 0)
                    enc[code] = name;
            }
            return enc;
        }

        /// <param name="ascii">The cleartext portion of the program.</param>
        /// <param name="known">
        /// False when a <c>/FontMatrix</c> is declared but could not be read or was outside the accepted
        /// range, so the 0.001 default stands in for it; true when none is declared (Type 1's 1000-unit
        /// em) or the declared scale was used.
        /// </param>
        private static float ParseFontMatrixScale(string ascii, out bool known)
        {
            known = false;
            int idx = ascii.IndexOf("/FontMatrix", StringComparison.Ordinal);
            if (idx < 0) { known = true; return 0.001f; }
            int lb = ascii.IndexOf('[', idx);
            if (lb < 0) return 0.001f;
            int rb = ascii.IndexOf(']', lb);
            if (rb < 0) return 0.001f;
            int p = lb + 1;
            SkipWS(ascii, ref p);
            // Clamp to a sane range so a corrupt /FontMatrix (e.g. [100 0 0 100 …]) can't
            // blow up glyph sizes; anything outside falls back to the 1000-unit em default.
            if (ReadFloat(ascii, ref p, out float v) && v > 1e-6f && v <= 0.1f) { known = true; return v; }
            return 0.001f;
        }

        // ----------------------------------------------------------------
        //  eexec decryption (ATN #5002 §7.1)
        // ----------------------------------------------------------------

        private const ushort EexecKey = 55665;
        private const ushort CsKey    = 4330;
        private const ushort C1       = 52845;
        private const ushort C2       = 22719;

        private static byte[] EexecDecrypt(byte[] data, int start, int length)
        {
            if (start + length > data.Length) return null;

            // Determine if the section is hex-encoded or raw binary.
            // Count printable hex chars in the first 20 bytes; if ≥ 90% are hex/ws, it's hex.
            int sample  = Math.Min(20, length);
            int hexCount = 0;
            for (int i = 0; i < sample; i++)
            {
                byte b = data[start + i];
                if ((b >= '0' && b <= '9') || (b >= 'A' && b <= 'F') ||
                    (b >= 'a' && b <= 'f') || b == '\n' || b == '\r' || b == ' ' || b == '\t')
                    hexCount++;
            }
            bool isHex = hexCount >= sample * 9 / 10;

            byte[] cipher = isHex ? HexDecode(data, start, length) : ExtractBinary(data, start, length);
            if (cipher == null || cipher.Length < 5) return null;

            // Decrypt using eexec key; discard first 4 output bytes (random seed)
            ushort r = EexecKey;
            var plain = new byte[cipher.Length];
            for (int i = 0; i < cipher.Length; i++)
            {
                byte c = cipher[i];
                plain[i] = (byte)(c ^ (r >> 8));
                r = (ushort)(unchecked((c + r) * (uint)C1 + C2));
            }
            var result = new byte[plain.Length - 4];
            Array.Copy(plain, 4, result, 0, result.Length);
            return result;
        }

        private static byte[] ExtractBinary(byte[] data, int start, int length)
        {
            var buf = new byte[length];
            Array.Copy(data, start, buf, 0, length);
            return buf;
        }

        private static byte[] HexDecode(byte[] data, int start, int length)
        {
            var result = new List<byte>(length / 2);
            int end = start + length;
            int pos = start;
            while (pos < end - 1)
            {
                while (pos < end && !IsHexByte(data[pos])) pos++;
                if (pos + 1 >= end) break;
                int hi = PdfConstants.HexToNibble(data[pos++]);
                int lo = PdfConstants.HexToNibble(data[pos++]);
                if (hi >= 0 && lo >= 0) result.Add((byte)((hi << 4) | lo));
            }
            return result.ToArray();
        }

        private static bool IsHexByte(byte b)
            => (b >= '0' && b <= '9') || (b >= 'A' && b <= 'F') || (b >= 'a' && b <= 'f');

        // ----------------------------------------------------------------
        //  Charstring decryption (ATN #5002 §7.2)
        // ----------------------------------------------------------------

        /// <remarks>
        /// <b>A <paramref name="lenIV"/> of -1 means the charstrings are not encrypted</b> (as
        /// font engines read the Private dictionary); the bytes are returned as they are.
        /// </remarks>
        internal static byte[] CsDecrypt(byte[] data, int start, int length, int lenIV)
        {
            if (start < 0 || length <= 0 || start + length > data.Length) return null;
            if (lenIV < 0)
            {
                var plain = new byte[length];
                Array.Copy(data, start, plain, 0, length);
                return plain;
            }
            int outputLen = Math.Max(0, length - lenIV);
            var result = new byte[outputLen];
            ushort r = CsKey;
            int outIdx = 0;
            for (int i = 0; i < length; i++)
            {
                byte c = data[start + i];
                byte p = (byte)(c ^ (r >> 8));
                r = (ushort)(unchecked((c + r) * (uint)C1 + C2));
                if (i >= lenIV) result[outIdx++] = p;
            }
            return result;
        }

        // ----------------------------------------------------------------
        //  lenIV (charstring seed-byte count; default 4)
        // ----------------------------------------------------------------

        private static int FindLenIV(byte[] decrypted)
        {
            string text = Latin1(decrypted, 0, Math.Min(1000, decrypted.Length));
            int idx = text.IndexOf("/lenIV", StringComparison.Ordinal);
            if (idx < 0) return 4;
            int p = idx + 6;
            SkipWS(text, ref p);
            // -1 is a value of its own: no encryption. ReadInt reads no sign, so read as 0 —
            // "encrypted, no seed bytes" — every plain charstring would decrypt into garbage.
            if (p < text.Length && text[p] == '-')
            {
                p++;
                return ReadInt(text, ref p, out int negated) && negated == 1 ? -1 : 4;
            }
            ReadInt(text, ref p, out int v);
            return v >= 0 && v <= 8 ? v : 4;
        }

        // ----------------------------------------------------------------
        //  Subrs extraction (forward binary scan)
        //
        //  Pattern: dup I N RD [N bytes] NP  (or |-  instead of RD)
        // ----------------------------------------------------------------

        /// <summary>The fewest bytes a <c>/Subrs</c> entry takes: <c>dup 0 0 RD </c> and <c>NP</c>.</summary>
        private const int MinSubrEntryBytes = 10;

        private static IReadOnlyList<byte[]> ParseSubrs(byte[] decrypted, int lenIV)
        {
            // Find "/Subrs" in the text
            int subrsOff = FindToken(decrypted, "/Subrs");
            if (subrsOff < 0) return Array.Empty<byte[]>();

            // Read count after "/Subrs"
            int p = subrsOff + 6;
            SkipWSBytes(decrypted, ref p);
            if (!ReadIntBytes(decrypted, ref p, out int count) || count <= 0) return Array.Empty<byte[]>();
            // Every entry takes bytes of the section — "dup i n RD " and its "NP", ten at the least —
            // so the section's length over ten bounds the count; a declared count is never allocated
            // as it stands ("/Subrs 2000000000 array" would be 16 GB, and one reference a byte of the
            // section would still be eight times a program of 64 MB).
            count = Math.Min(count, decrypted.Length / MinSubrEntryBytes + 1);
            SkipToToken(decrypted, ref p, "array");
            p += 5;

            var subrs = new byte[count][];
            for (int i = 0; i < count; i++) subrs[i] = Array.Empty<byte>();

            // Scan forward: find each "dup" then index, then length, then RD, then binary
            int defOff = NotYetSought;
            while (p < decrypted.Length)
            {
                int dupOff = IndexOfToken(decrypted, "dup", p);
                if (dupOff < 0) break;

                // Check for "def" before next "dup" — end of Subrs block. The def is sought once,
                // and again only once the scan has passed it: sought afresh from every dup, a block
                // of n subroutines read n times the section (see LastScanBytesExamined).
                if (defOff != -1 && defOff < p) defOff = IndexOfToken(decrypted, "def", p);
                if (defOff >= 0 && defOff < dupOff) break;

                p = dupOff + 3;
                SkipWSBytes(decrypted, ref p);
                if (!ReadIntBytes(decrypted, ref p, out int idx)) { p = dupOff + 3; continue; }
                SkipWSBytes(decrypted, ref p);
                if (!ReadIntBytes(decrypted, ref p, out int len)) { p = dupOff + 3; continue; }
                SkipWSBytes(decrypted, ref p);
                if (!MatchRD(decrypted, ref p)) { p = dupOff + 3; continue; }

                // Binary starts exactly here
                if (idx >= 0 && idx < count && (long)p + len <= decrypted.Length)
                {
                    byte[] cs = CsDecrypt(decrypted, p, len, lenIV);
                    if (cs != null) subrs[idx] = cs;
                }
                p = SkipBinary(decrypted, p, len);
            }
            return subrs;
        }

        // ----------------------------------------------------------------
        //  CharStrings extraction (forward binary scan)
        //
        //  Pattern: /name N RD [N bytes] ND  (or |-  instead of RD)
        // ----------------------------------------------------------------

        /// <param name="decrypted">The eexec-decrypted portion.</param>
        /// <param name="lenIV">Charstring seed bytes to skip.</param>
        /// <param name="complete">
        /// True only when the scan reached the dictionary's <c>end</c> and read every entry it met —
        /// no name, length or <c>RD</c> token it had to skip, no charstring running past the data.
        /// The declared <c>/CharStrings N dict</c> size cannot say this: a subset font keeps its
        /// original capacity.
        /// </param>
        private static IReadOnlyDictionary<string, byte[]> ParseCharStrings(byte[] decrypted, int lenIV, out bool complete)
        {
            complete = false;
            bool clean = true;
            int csOff = FindToken(decrypted, "/CharStrings");
            if (csOff < 0) return null;

            // Skip past the count and "dict"
            int p = csOff + 12;
            SkipToToken(decrypted, ref p, "begin");
            p += 5;

            var dict = new Dictionary<string, byte[]>(StringComparer.Ordinal);

            // Forward scan: /name N RD [N bytes] ND
            while (p < decrypted.Length)
            {
                SkipWSBytes(decrypted, ref p);
                if (p >= decrypted.Length) break;

                // Look for '/' marking start of a glyph name
                if (decrypted[p] != (byte)'/')
                {
                    // Could be "end" — stop scanning
                    if (p + 3 <= decrypted.Length &&
                        decrypted[p] == 'e' && decrypted[p+1] == 'n' && decrypted[p+2] == 'd')
                    {
                        complete = clean;
                        break;
                    }
                    p++;
                    continue;
                }
                p++; // skip '/'
                string name = ReadNameBytes(decrypted, ref p);
                if (name.Length == 0) { clean = false; continue; }

                SkipWSBytes(decrypted, ref p);
                if (!ReadIntBytes(decrypted, ref p, out int len)) { clean = false; continue; }
                SkipWSBytes(decrypted, ref p);
                if (!MatchRD(decrypted, ref p)) { clean = false; continue; }

                // Binary starts exactly here (no separator)
                if ((long)p + len <= decrypted.Length)
                {
                    byte[] cs = CsDecrypt(decrypted, p, len, lenIV);
                    if (cs != null) dict[name] = cs;
                    else clean = false;
                }
                else
                {
                    clean = false;
                }
                p = SkipBinary(decrypted, p, len); // crucially, we do NOT re-scan inside it
            }
            return dict;
        }

        /// <summary>
        /// The position after <paramref name="len"/> bytes of binary data, clamped to the section. In
        /// <c>int</c>, a length near <c>int.MaxValue</c> wrapped the position negative, the next read
        /// threw, and the font was lost with every glyph already read.
        /// </summary>
        private static int SkipBinary(byte[] data, int p, int len)
            => (int)Math.Min((long)p + len, data.Length);

        // ----------------------------------------------------------------
        //  Binary stream helpers
        // ----------------------------------------------------------------

        private static void SkipWSBytes(byte[] data, ref int pos)
        {
            while (pos < data.Length)
            {
                byte b = data[pos];
                if (b == ' ' || b == '\t' || b == '\r' || b == '\n') pos++;
                else break;
            }
        }

        private static bool ReadIntBytes(byte[] data, ref int pos, out int value)
        {
            value = 0;
            if (pos >= data.Length || data[pos] < '0' || data[pos] > '9') return false;
            int start = pos;
            while (pos < data.Length && data[pos] >= '0' && data[pos] <= '9') pos++;
            string s = Encoding.ASCII.GetString(data, start, pos - start);
            return int.TryParse(s, out value);
        }

        private static string ReadNameBytes(byte[] data, ref int pos)
        {
            int start = pos;
            while (pos < data.Length)
            {
                byte b = data[pos];
                if (b == ' ' || b == '\t' || b == '\r' || b == '\n' || b == '/') break;
                pos++;
            }
            return Encoding.ASCII.GetString(data, start, pos - start);
        }

        /// <summary>
        /// Matches the RD operator ("RD" or "|-") at the current position and
        /// skips an optional single whitespace byte that some generators insert
        /// between the RD token and the binary charstring data.
        /// Advances pos to the first byte of the binary data.
        /// Returns false if not found.
        /// </summary>
        private static bool MatchRD(byte[] data, ref int pos)
        {
            if (pos + 2 > data.Length) return false;
            bool matched = false;
            if (data[pos] == 'R' && data[pos + 1] == 'D')
            {
                pos += 2; matched = true;
            }
            else if (data[pos] == '|' && data[pos + 1] == '-')
            {
                pos += 2; matched = true;
            }
            if (matched)
            {
                // Some generators emit one whitespace byte (space or newline) between
                // the RD token and the binary charstring data. Skip it if present.
                if (pos < data.Length && (data[pos] == '\n' || data[pos] == '\r' || data[pos] == ' '))
                    pos++;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Finds the first occurrence of a string token in the decrypted byte array.
        /// Searches byte-by-byte in the ASCII range; safe for binary data since the
        /// search string uses only printable ASCII bytes.
        /// </summary>
        private static int FindToken(byte[] data, string token)
        {
            byte[] needle = Encoding.ASCII.GetBytes(token);
            for (int i = 0; i <= data.Length - needle.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                    if (data[i + j] != needle[j]) { match = false; break; }
                if (match) return i;
            }
            return -1;
        }

        /// <summary>A position a token has not been sought from yet; -1 once sought and absent.</summary>
        private const int NotYetSought = -2;

        private static int IndexOfToken(byte[] data, string token, int start)
        {
            byte[] needle = Encoding.ASCII.GetBytes(token);
            for (int i = start; i <= data.Length - needle.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                    if (data[i + j] != needle[j]) { match = false; break; }
                if (match) { _scanBytes += i - start + 1; return i; }
            }
            _scanBytes += Math.Max(0, data.Length - start);
            return -1;
        }

        private static void SkipToToken(byte[] data, ref int pos, string token)
        {
            int idx = IndexOfToken(data, token, pos);
            if (idx >= 0) pos = idx;
        }

        // ----------------------------------------------------------------
        //  ASCII string helpers (for encoding / FontMatrix parsing)
        // ----------------------------------------------------------------

        private static void SkipWS(string s, ref int pos)
        {
            while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t' || s[pos] == '\r' || s[pos] == '\n'))
                pos++;
        }

        private static bool ReadInt(string s, ref int pos, out int value)
        {
            value = 0;
            SkipWS(s, ref pos);
            if (pos >= s.Length || !char.IsDigit(s[pos])) return false;
            int start = pos;
            while (pos < s.Length && char.IsDigit(s[pos])) pos++;
            return int.TryParse(s.Substring(start, pos - start), out value);
        }

        private static bool ReadFloat(string s, ref int pos, out float value)
        {
            value = 0;
            SkipWS(s, ref pos);
            int start = pos;
            while (pos < s.Length && (char.IsDigit(s[pos]) || s[pos] == '.' || s[pos] == 'e' || s[pos] == 'E' || s[pos] == '-'))
                pos++;
            return float.TryParse(s.Substring(start, pos - start),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out value);
        }

        private static string ReadToken(string s, ref int pos)
        {
            int start = pos;
            while (pos < s.Length && s[pos] != ' ' && s[pos] != '\t' && s[pos] != '\r' &&
                   s[pos] != '\n' && s[pos] != '/' && s[pos] != '[' && s[pos] != ']')
                pos++;
            return s.Substring(start, pos - start);
        }
    }
}
