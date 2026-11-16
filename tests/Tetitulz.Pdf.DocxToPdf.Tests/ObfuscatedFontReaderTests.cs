// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.DocxToPdf.Rendering;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// Unit tests for the OPC "obfuscated font" (ODTTF) XOR de-obfuscation used for fonts
    /// embedded inside a DOCX (ECMA-376 §17.8.1).
    /// </summary>
    public class ObfuscatedFontReaderTests
    {
        private const string Key = "{52F7CD7B-1A3C-4E2F-9B8A-0123456789AB}";

        private static byte[] Sample(int len)
        {
            var b = new byte[len];
            for (int i = 0; i < len; i++) b[i] = (byte)(i * 7 + 3);
            return b;
        }

        [Fact]
        public void Deobfuscate_IsInvolutive_RoundTripsToOriginal()
        {
            byte[] original = Sample(64);
            byte[] obf = ObfuscatedFontReader.Deobfuscate(original, Key);
            byte[] back = ObfuscatedFontReader.Deobfuscate(obf, Key);

            Assert.Equal(original, back);
            // The first 32 bytes must actually change (the key isn't all-zero).
            Assert.NotEqual(original[..32], obf[..32]);
        }

        [Fact]
        public void Deobfuscate_OnlyTouchesFirst32Bytes()
        {
            byte[] original = Sample(80);
            byte[] obf = ObfuscatedFontReader.Deobfuscate(original, Key);

            // Everything from byte 32 on is untouched.
            Assert.Equal(original[32..], obf[32..]);
        }

        [Fact]
        public void Deobfuscate_ShortData_DoesNotThrow()
        {
            byte[] obf = ObfuscatedFontReader.Deobfuscate(Sample(10), Key);
            Assert.Equal(10, obf.Length); // fewer than 32 bytes: masks what's present, no overrun
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not-a-guid")]          // wrong digit count → treated as no key
        public void Deobfuscate_MissingOrInvalidKey_ReturnsUnchangedCopy(string? key)
        {
            byte[] original = Sample(48);
            byte[] result = ObfuscatedFontReader.Deobfuscate(original, key);

            Assert.Equal(original, result);
            Assert.NotSame(original, result); // a copy, not the same instance
        }

        [Fact]
        public void Deobfuscate_GuidWithoutBraces_ParsesSameAsBraced()
        {
            byte[] original = Sample(64);
            byte[] a = ObfuscatedFontReader.Deobfuscate(original, "{52F7CD7B-1A3C-4E2F-9B8A-0123456789AB}");
            byte[] b = ObfuscatedFontReader.Deobfuscate(original, "52F7CD7B1A3C4E2F9B8A0123456789AB");
            Assert.Equal(a, b);
        }
    }
}
