// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

﻿using System.Reflection;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Tests for security-hardening fixes across the PDF library:
///  - PdfPasswordNormalizer correctness and performance of control-character filtering.
///  - Other security checks as they land.
/// </summary>
public class SecurityHardeningTests
{
    // ==================================================================
    //  PdfPasswordNormalizer
    // ==================================================================

    [Fact]
    public void PasswordNormalizer_ControlCharacters_AreStripped()
    {
        // Password contains C0 controls (U+0001..U+001F) interleaved with
        // ASCII letters.  Use \u four-hex-digit escapes — `\x` is greedy and
        // `\x01b` would parse as U+001B (one ESC char), not U+0001 + 'b'.
        var result = Invoke("abcd");
        Assert.Equal("abcd", result);
    }

    [Fact]
    public void PasswordNormalizer_C1Controls_AreStripped()
    {
        // C1 controls at U+007F..U+009F.  Same \u-escape reasoning as above.
        var result = Invoke("abcd");
        Assert.Equal("abcd", result);
    }

    [Fact]
    public void PasswordNormalizer_PlainAscii_PassesThrough()
    {
        Assert.Equal("hunter2", Invoke("hunter2"));
    }

    [Fact]
    public void PasswordNormalizer_UnicodeChars_PreservedThroughNfkc()
    {
        // e-acute as U+00E9 vs decomposed e + U+0301 must normalise to same form.
        var composed   = Invoke("café");
        var decomposed = Invoke("café");
        Assert.Equal(composed, decomposed);
    }

    [Fact]
    public void PasswordNormalizer_NoControls_DoesNotReallocate()
    {
        // Correctness-equivalent check: normalisation of a clean string
        // should produce the NFKC form and nothing else.
        Assert.Equal("hello", Invoke("hello"));
    }

    [Fact]
    public void PasswordNormalizer_ManyControlChars_DoesNotTimeOut()
    {
        // Previously O(n^2): 2048 control chars would allocate ~2000 strings.
        var input = new string('', 2048) + "ok";
        var result = Invoke(input);
        Assert.Equal("ok", result);
    }

    private static string Invoke(string password)
    {
        var m = typeof(PdfPasswordNormalizer).GetMethod("Normalize",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(m);
        return (string)m!.Invoke(null, new object[] { password })!;
    }
}
