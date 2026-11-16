// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.IO.Filters
{
    public sealed class AsciiHexDecodeFilter : StreamFilter
    {
        public override byte[] Decode(byte[] data, PdfDictionary parameters)
        {
            // Two input characters produce at most one output byte, so the exact ceiling is known
            // up front. Written into a right-sized array rather than a List&lt;byte&gt;, which paid
            // a capacity check per output byte and then copied the whole buffer again in ToArray().
            var output = new byte[data.Length / 2 + 1];
            int count = 0;
            int highNibble = -1;

            for (int i = 0; i < data.Length; i++)
            {
                byte b = data[i];

                // End of data marker
                if (b == (byte)'>')
                    break;

                // Skip whitespace
                if (PdfConstants.IsWhitespace(b))
                    continue;

                int nibble = PdfConstants.HexToNibble(b);
                if (nibble < 0)
                    continue; // Skip invalid characters (lenient).

                if (highNibble == -1)
                {
                    highNibble = nibble;
                    continue;
                }

                output[count++] = (byte)((highNibble << 4) | nibble);
                highNibble = -1;
            }

            // If odd number of hex digits, last nibble is assumed to be 0
            if (highNibble != -1)
            {
                output[count++] = (byte)(highNibble << 4);
            }

            if (count == output.Length)
                return output;

            var exact = new byte[count];
            Buffer.BlockCopy(output, 0, exact, 0, count);
            return exact;
        }
    }
}
