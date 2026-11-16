// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Imaging
{
    /// <summary>
    /// ICC.1:2004-10 Table 19 — the data colour space signatures an ICC profile header carries at
    /// offset 16 — and how many components one colour has in each.
    /// </summary>
    /// <remarks>
    /// <b>The one table for the question.</b> Base's public <c>IccProfileComponents</c> answers a
    /// PDF/A rule with it, <see cref="Jpx.Jp2ColourDescription"/> reads the arity of a <c>colr</c>
    /// box's embedded profile with it, and the PostScript plugin an <c>/ICCBased</c> space's. Shared downward into Core because the
    /// JP2 reader is here and cannot see Base, and public because a plugin's only reference is Core.
    /// </remarks>
    public static class IccDataColourSpaces
    {
        /// <summary>
        /// The component count for a four-character signature, or 0 for an unrecognised or
        /// <see langword="null"/> one (a header that could not be read).
        /// </summary>
        /// <param name="signature">The four characters at profile header offset 16, e.g. <c>"RGB "</c>.</param>
        public static int ComponentCount(string signature) => signature switch
        {
            "GRAY" => 1,
            "RGB " => 3,
            "CMYK" => 4,
            "CMY " => 3,
            "Lab " => 3,
            "XYZ " => 3,
            "Luv " => 3,
            "YCbr" => 3,
            "Yxy " => 3,
            "HSV " => 3,
            "HLS " => 3,
            "2CLR" => 2,
            "3CLR" => 3,
            "4CLR" => 4,
            "5CLR" => 5,
            "6CLR" => 6,
            "7CLR" => 7,
            "8CLR" => 8,
            "9CLR" => 9,
            "ACLR" => 10,
            "BCLR" => 11,
            "CCLR" => 12,
            "DCLR" => 13,
            "ECLR" => 14,
            "FCLR" => 15,
            _ => 0,
        };

        /// <summary>
        /// The component count <paramref name="profile"/>'s header names, or 0 where the bytes are no
        /// profile header: fewer than ICC.1's 128 header bytes, or no <c>acsp</c> signature at offset
        /// 36 — the gate before the data colour space at offset 16 is believed.
        /// </summary>
        /// <param name="profile">An ICC profile's bytes.</param>
        public static int ComponentCountOf(byte[] profile)
        {
            if (profile == null || profile.Length < 128) return 0;
            if (profile[36] != (byte)'a' || profile[37] != (byte)'c' || profile[38] != (byte)'s' || profile[39] != (byte)'p')
                return 0;
            var signature = new char[4];
            for (int k = 0; k < 4; k++) signature[k] = (char)profile[16 + k];
            return ComponentCount(new string(signature));
        }
    }
}
