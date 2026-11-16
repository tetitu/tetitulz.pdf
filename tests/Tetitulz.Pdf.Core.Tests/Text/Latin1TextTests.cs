// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text
{
    /// <summary>
    /// The single Latin-1 byte-range decoder, which replaced six byte-identical copies across Core
    /// and <c>Plugins.PostScript</c>.
    /// </summary>
    /// <remarks>
    /// <b>The high half is what makes this a test rather than a formality.</b> Bytes 0x00–0x7F
    /// decode the same under Latin-1, ASCII and UTF-8, so a fixture built from readable text passes
    /// with the rule replaced by any of them. Only 0x80–0xFF separates them: ASCII yields <c>?</c>
    /// and UTF-8 yields U+FFFD, where Latin-1 must yield the code point equal to the byte.
    /// </remarks>
    public class Latin1TextTests
    {
        [Fact]
        public void EveryByteMapsToTheCodePointEqualToIt()
        {
            var all = new byte[256];
            for (int i = 0; i < 256; i++) all[i] = (byte)i;

            string decoded = Latin1Text.Decode(all);

            Assert.Equal(256, decoded.Length);
            for (int i = 0; i < 256; i++)
                Assert.Equal((char)i, decoded[i]);
        }

        /// <summary>
        /// The three bytes an ASCII or UTF-8 decoder would get wrong, named individually so a
        /// failure says which rule crept in rather than just "the loop broke".
        /// </summary>
        [Theory]
        [InlineData(0x80, '')]   // ASCII would give '?'
        [InlineData(0xA9, '©')]   // copyright sign
        [InlineData(0xFF, 'ÿ')]   // UTF-8 would give U+FFFD — 0xFF is never a valid lead byte
        public void AHighByteIsItsOwnCodePoint(int b, char expected)
        {
            Assert.Equal(expected.ToString(), Latin1Text.Decode(new[] { (byte)b }));
        }

        [Fact]
        public void TheRangeIsRespected()
        {
            byte[] data = { (byte)'x', (byte)'A', (byte)'B', (byte)'C', (byte)'y' };

            Assert.Equal("ABC", Latin1Text.Decode(data, 1, 3));
        }

        /// <summary>
        /// A non-positive length yields the empty string. Three of the six copies guarded this and
        /// three did not — the unguarded ones would have thrown on a negative length, which was
        /// never a contract. The merge took the guarded behaviour after checking that no unguarded
        /// call site could reach a negative length.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void ANonPositiveLengthYieldsEmpty(int length)
        {
            Assert.Equal("", Latin1Text.Decode(new byte[] { 1, 2, 3 }, 0, length));
        }

        [Fact]
        public void ANullBufferYieldsEmpty()
        {
            Assert.Equal("", Latin1Text.Decode(null));
            Assert.Equal("", Latin1Text.Decode(null, 0, 5));
        }
    }
}
