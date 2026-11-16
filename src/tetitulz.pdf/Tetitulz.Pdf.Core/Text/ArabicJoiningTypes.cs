// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Text
{
    /// <summary>
    /// Unicode's joining type of a character in the Arabic blocks (the Unicode Standard §9.2,
    /// <c>ArabicShaping.txt</c>): <c>D</c> dual-joining, <c>R</c> right-joining, <c>C</c>
    /// join-causing, <c>T</c> transparent, <c>U</c> non-joining.
    /// </summary>
    /// <remarks>
    /// <para><b>Generated, not typed</b>, from the Unicode Character Database 18.0: each character
    /// <c>ArabicShaping.txt</c> lists takes its listed type, and every other character is
    /// transparent when its general category (<c>UnicodeData.txt</c>) is Mn, Me or Cf, else
    /// non-joining — the derivation the file's own header gives. Only the three Arabic blocks are
    /// held; the zero width joiner (join-causing) and non-joiner are answered by name, and
    /// everything else is non-joining.</para>
    /// <para><see cref="OpenTypeShaper"/>'s presentation-form table still decides how a letter it
    /// holds joins, because that is what is drawn: alef maksura is <c>D</c> here, but the table
    /// keeps its Arabic right-joining forms (GAPS.md §6). This type answers for every other letter,
    /// and for which characters a join passes through.</para>
    /// </remarks>
    internal static class ArabicJoiningTypes
    {
        internal const char DualJoining = 'D';
        internal const char RightJoining = 'R';
        internal const char JoinCausing = 'C';
        internal const char Transparent = 'T';
        internal const char NonJoining = 'U';

        private const int ZeroWidthNonJoiner = 0x200C, ZeroWidthJoiner = 0x200D;

        /// <summary>The joining type of <paramref name="cp"/>.</summary>
        internal static char Of(int cp)
        {
            if (cp >= 0x0600 && cp <= 0x06FF) return Arabic[cp - 0x0600];
            if (cp >= 0x0750 && cp <= 0x077F) return Supplement[cp - 0x0750];
            if (cp >= 0x0870 && cp <= 0x08FF) return Extended[cp - 0x0870];
            if (cp == ZeroWidthJoiner) return JoinCausing;
            return NonJoining;   // the non-joiner among them
        }

        /// <summary>True when a character of this type connects to the character after it.</summary>
        internal static bool JoinsNext(char type) => type == DualJoining || type == JoinCausing;

        /// <summary>True when a character of this type connects to the character before it.</summary>
        internal static bool JoinsPrevious(char type)
            => type == DualJoining || type == RightJoining || type == JoinCausing;

        // Arabic: U+0600–U+06FF
        private const string Arabic =
            "UUUUUUUUUUUUUUUUTTTTTTTTTTTUTUUU" + // U+0600
            "DURRRRDRDRDDDDDRRRRDDDDDDDDDDDDD" + // U+0620
            "CDDDDDDDRDDTTTTTTTTTTTTTTTTTTTTT" + // U+0640
            "UUUUUUUUUUUUUUDDTRRRURRRDDDDDDDD" + // U+0660
            "DDDDDDDDRRRRRRRRRRRRRRRRRRDDDDDD" + // U+0680
            "DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD" + // U+06A0
            "RDDRRRRRRRRRDRDRDDRRURTTTTTTTUUT" + // U+06C0
            "TTTTTUUTTUTTTTRRUUUUUUUUUUDDDUUD";  // U+06E0

        // Arabic Supplement: U+0750–U+077F
        private const string Supplement =
            "DDDDDDDDDRRRDDDDDDDDDDDDDDDRRDDD" + // U+0750
            "DRDRRDDDRRDDDDDD";                  // U+0770

        // Arabic Extended-B and Extended-A: U+0870–U+08FF
        private const string Extended =
            "RRRRRRRRRRRRRRRRRRRCCCDUUDDDDDRD" + // U+0870
            "UUUUUUUTTTTTTTTTDDDDDDDDDDRRRURD" + // U+0890
            "DRRDDDDDDRDDDDDDDDDDDDDDDUTTTTTT" + // U+08B0
            "TTTTTTTTTTTTTTTTTTUTTTTTTTTTTTTT" + // U+08D0
            "TTTTTTTTTTTTTTTT";                  // U+08F0
    }
}
