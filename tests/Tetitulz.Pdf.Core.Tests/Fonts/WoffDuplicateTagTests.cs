// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// A WOFF 1.0 directory naming one tag twice is refused. Its two tables shared one offset in the
/// reassembled sfnt, so both directory records pointed at the second table's bytes.
/// </summary>
public class WoffDuplicateTagTests
{
    /// <summary>
    /// Malformed container: the second directory entry's tag is overwritten with the first's.
    /// The control, the same container untouched, decodes.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Malformed_ADuplicatedTableTag_IsRefused(bool duplicate)
    {
        byte[] woff = MinimalWoff.Wrap(MinimalTrueType.Build("ABC"));
        if (duplicate)
        {
            const int directory = 44, entry = 20;
            for (int i = 0; i < 4; i++) woff[directory + entry + i] = woff[directory + i];
        }

        byte[] sfnt = WoffDecoder.Decode(woff);

        Assert.Equal(duplicate, sfnt == null);
        if (!duplicate) Assert.NotNull(TrueTypeFontProgram.Parse(sfnt));
    }
}
