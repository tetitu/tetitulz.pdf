// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// Packs a packet header written out as bits — spaces are for the reader's eye — into bytes the
/// way T.800 §B.10.1 requires: MSB first, a zero bit stuffed after every 0xFF byte, the last byte
/// padded, and a stuffed byte appended when the last byte would otherwise be 0xFF.
/// </summary>
internal static class JpxPacketBits
{
    internal static byte[] Bytes(string bits)
    {
        var bytes = new List<byte>();
        int current = 0, count = 0, capacity = 8;
        foreach (char ch in bits)
        {
            if (ch != '0' && ch != '1') continue;
            current = (current << 1) | (ch - '0');
            if (++count < capacity) continue;
            bytes.Add((byte)current);
            capacity = current == 0xFF ? 7 : 8;
            current = 0;
            count = 0;
        }
        if (count > 0)
        {
            current <<= capacity - count;
            bytes.Add((byte)current);
            if (current == 0xFF) bytes.Add(0);
        }
        else if (capacity == 7)
        {
            bytes.Add(0);   // the header ended on 0xFF: its stuffed byte must follow
        }
        return bytes.ToArray();
    }
}
