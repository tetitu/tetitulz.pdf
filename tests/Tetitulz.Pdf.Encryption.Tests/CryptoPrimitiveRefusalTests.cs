// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// <see cref="PdfCryptoPrimitives"/>' argument refusals, each with its acceptance control. Without
/// the guards the platform still throws, but something else: a <see cref="DivideByZeroException"/>
/// from RC4's key index, and a <c>CryptographicException</c> from a partial AES block.
/// </summary>
public class CryptoPrimitiveRefusalTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void Rc4_AnEmptyKey_IsRefusedAsAnArgument(int keyLength, bool refused)
    {
        var data = new byte[] { 1, 2, 3 };
        if (refused)
            Assert.Throws<ArgumentException>(() => PdfCryptoPrimitives.Rc4(new byte[keyLength], data));
        else
            Assert.Equal(3, PdfCryptoPrimitives.Rc4(new byte[keyLength], data).Length);
    }

    [Theory]
    [InlineData(17, true)]
    [InlineData(16, false)]
    public void AesEcb_APartialBlock_IsRefusedAsAnArgument(int length, bool refused)
    {
        var key = new byte[16];
        if (refused)
            Assert.Throws<ArgumentException>(() => PdfCryptoPrimitives.AesEcbNoPadding(true, key, new byte[length]));
        else
            Assert.Equal(16, PdfCryptoPrimitives.AesEcbNoPadding(true, key, new byte[length]).Length);
    }
}
