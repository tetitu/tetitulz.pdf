// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// The byte writers the hand-built WOFF 2.0 fixtures share — big-endian integers and the format's
/// own UIntBase128.
/// </summary>
/// <remarks>
/// Hand-built containers are the point of those fixtures: what a directory declares and what its
/// stream produces have to be settable independently, which no encoder will do on purpose.
/// </remarks>
internal static class Woff2Bytes
{
    /// <summary>WOFF2's UIntBase128: big-endian, seven bits a byte, the high bit set on all but the last.</summary>
    internal static byte[] UIntBase128(uint value)
    {
        var digits = new System.Collections.Generic.List<byte>();
        do { digits.Insert(0, (byte)(value & 0x7F)); value >>= 7; } while (value != 0);
        for (int i = 0; i < digits.Count - 1; i++) digits[i] |= 0x80;
        return digits.ToArray();
    }

    internal static void WriteU32(byte[] target, int at, uint value)
    {
        target[at] = (byte)(value >> 24);
        target[at + 1] = (byte)(value >> 16);
        target[at + 2] = (byte)(value >> 8);
        target[at + 3] = (byte)value;
    }

    internal static void WriteU16(byte[] target, int at, int value)
    {
        target[at] = (byte)(value >> 8);
        target[at + 1] = (byte)value;
    }
}
