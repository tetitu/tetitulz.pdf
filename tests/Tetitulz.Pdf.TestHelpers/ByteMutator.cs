// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.TestHelpers;

/// <summary>
/// Seeded mutations of a whole file for the hostile-input suites: a byte, a bit, a two- or
/// four-byte field in either byte order set to a value a length, offset or count breaks on, a cut.
/// </summary>
public static class ByteMutator
{
    private static readonly uint[] Interesting =
    {
        0, 1, 2, 3, 4, 5, 7, 8, 12, 15, 16, 17, 24, 31, 32, 33, 47, 48, 51, 52, 63, 64, 127, 128, 255, 256,
        0x7FFF, 0x8000, 32773, 0xFFFF, 0x10000, 0x00FFFFFF, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFE, 0xFFFFFFFF,
    };

    /// <summary>A copy of <paramref name="seed"/> with one to three mutations.</summary>
    public static byte[] Mutate(byte[] seed, Random random)
    {
        byte[] data = (byte[])seed.Clone();
        int edits = 1 + random.Next(3);
        for (int e = 0; e < edits && data.Length > 0; e++)
        {
            switch (random.Next(7))
            {
                case 0:
                    data[random.Next(data.Length)] = (byte)random.Next(256);
                    break;
                case 1:
                    data[random.Next(data.Length)] ^= (byte)(1 << random.Next(8));
                    break;
                case 2:
                case 3:
                {
                    if (data.Length < 4) break;
                    int at = random.Next(data.Length - 3);
                    uint v = Interesting[random.Next(Interesting.Length)];
                    bool big = random.Next(2) == 0;
                    for (int i = 0; i < 4; i++) data[at + i] = (byte)(v >> (8 * (big ? 3 - i : i)));
                    break;
                }
                case 4:
                case 5:
                {
                    if (data.Length < 2) break;
                    int at = random.Next(data.Length - 1);
                    uint v = Interesting[random.Next(Interesting.Length)];
                    bool big = random.Next(2) == 0;
                    data[at] = (byte)(big ? v >> 8 : v);
                    data[at + 1] = (byte)(big ? v : v >> 8);
                    break;
                }
                default:
                    if (data.Length > 1) Array.Resize(ref data, 1 + random.Next(data.Length - 1));
                    break;
            }
        }
        return data;
    }
}
