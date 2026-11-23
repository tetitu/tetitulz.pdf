// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Signing;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// The region a <c>/ByteRange</c> leaves out is held to the file before anything is sized from it. A hole naming
/// bytes past the end allocated up to 2 GB before the read failed, and a negative start reached
/// <c>Stream.Position</c>, which throws no <c>PdfException</c>. The control is a hole inside the file, which finds
/// its <c>/Contents</c>.
/// </summary>
public sealed class ContentsHoleBoundsTests
{
    private static readonly byte[] File = Encoding.ASCII.GetBytes("0123456789<ABCD>0123456789");

    [Fact]
    public void AdversarialHolePastTheFile_IsRefusedAsOutsideIt()
    {
        var ex = Assert.Throws<PdfSigningException>(() =>
            SigningHelpers.FindContentsDelimiters(new MemoryStream(File), 10, 2_000_000_000L, out _, out _));
        Assert.Contains("not inside the file", ex.Message);
    }

    [Fact]
    public void AdversarialNegativeHoleStart_IsRefusedAsOutsideIt()
    {
        var ex = Assert.Throws<PdfSigningException>(() =>
            SigningHelpers.FindContentsDelimiters(new MemoryStream(File), -5, 16, out _, out _));
        Assert.Contains("not inside the file", ex.Message);
    }

    [Fact]
    public void AHoleInsideTheFile_FindsItsContents()
    {
        SigningHelpers.FindContentsDelimiters(new MemoryStream(File), 10, 16, out long start, out long end);
        Assert.Equal(11, start);
        Assert.Equal(15, end);
    }
}
