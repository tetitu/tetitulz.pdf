// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// A filter chain's refusal is a <see cref="PdfException"/>: LZW, RunLength and ASCII85 refused a
/// ceiling or corrupt data as <see cref="InvalidDataException"/>, which escaped every public door that
/// catches <see cref="PdfException"/> — the same stream under Flate came back as a refusal.
/// </summary>
public sealed class PipelineRefusalTypeTests
{
    [Fact]
    public void AdversarialLzwPastTheCeiling_IsAPdfFilterException()
    {
        byte[] bomb = LzwEncodeFilter.Encode(new byte[64 * 1024]);
        var ex = Assert.Throws<PdfFilterException>(() =>
            FilterPipeline.Decode(bomb, PdfName.LZWDecode, null, 4096));
        Assert.Contains("/LZWDecode", ex.Message, StringComparison.Ordinal);

        // Control: under a ceiling it fits, the stream decodes.
        Assert.Equal(64 * 1024, FilterPipeline.Decode(bomb, PdfName.LZWDecode, null, 1024 * 1024).Length);
    }

    [Fact]
    public void AdversarialAscii85ZerosPastTheCeiling_IsAPdfFilterException()
    {
        // Each 'z' is four zero bytes.
        byte[] zeros = System.Text.Encoding.ASCII.GetBytes(new string('z', 2000) + "~>");
        Assert.Throws<PdfFilterException>(() => FilterPipeline.Decode(zeros, PdfName.ASCII85Decode, null, 4096));
        Assert.Equal(8000, FilterPipeline.Decode(zeros, PdfName.ASCII85Decode, null, 1024 * 1024).Length);
    }
}
