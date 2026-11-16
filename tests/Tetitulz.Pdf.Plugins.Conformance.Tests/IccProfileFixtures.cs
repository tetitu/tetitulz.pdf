// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// Header-only ICC profiles that the output-intent writer's gate accepts and PDF/A's header checks
/// pass: version 2, the device class and data colour space given, <c>'acsp'</c> at byte 36.
/// </summary>
/// <remarks>
/// <b>The converters' tests once supplied arbitrary bytes</b> — a counting ramp, or the ASCII of
/// "fake-icc-profile". The plugin's output-intent writer embedded them unchecked, with the caller's
/// <c>/N</c>, and the validators, which read a profile they cannot parse as "cannot verify" rather
/// than "wrong", declared the result conformant. The writer now shares Base's profile gate, so a
/// fixture has to be a profile at least as far as its header.
/// </remarks>
internal static class IccProfileFixtures
{
    /// <summary>An RGB display profile — the class the PDF/A converter's sRGB intent describes.</summary>
    internal static byte[] Rgb() => Header("mntr", "RGB ");

    /// <summary>A CMYK output profile — what a PDF/X press condition describes.</summary>
    internal static byte[] Cmyk() => Header("prtr", "CMYK");

    internal static byte[] Header(string deviceClass, string dataColorSpace, int majorVersion = 2)
    {
        var profile = new byte[132];
        profile[3] = 132;                 // declared size, big-endian
        profile[8] = (byte)majorVersion;
        Encoding.ASCII.GetBytes(deviceClass).CopyTo(profile, 12);
        Encoding.ASCII.GetBytes(dataColorSpace).CopyTo(profile, 16);
        Encoding.ASCII.GetBytes("XYZ ").CopyTo(profile, 20);
        Encoding.ASCII.GetBytes("acsp").CopyTo(profile, 36);
        return profile;
    }
}
