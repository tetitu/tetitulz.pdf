// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Objects;

/// <summary>
/// A parsed document's streams share one <see cref="DecodeMemoBudget"/>, so the decoded bytes the
/// document keeps memoized are bounded: a whole-document pass that decodes every stream once no
/// longer holds the sum of them all (DoS finding F3).
/// </summary>
/// <remarks>
/// Each stream caches its decode for the document's life. Under the shared ceiling a repeated read
/// returns the same cached array; once the ceiling is reached a stream decodes without caching, so a
/// later read re-decodes (a fresh array) and the retained total stops growing. A stream the parser
/// did not build has no budget and always caches, as it always did.
/// </remarks>
public class PdfStreamMemoBudgetTests
{
    private const int DecodedSize = 600;

    private static PdfStream Flate(DecodeMemoBudget budget)
    {
        var s = new PdfStream { MemoBudget = budget };
        s.Set(PdfName.Filter, PdfName.FlateDecode);
        s.RawData = FlateEncodeFilter.Encode(new byte[DecodedSize]);
        return s;
    }

    [Fact]
    public void StreamsCacheUnderTheSharedCeilingAndStopCachingPastIt()
    {
        // Ceiling 1000 bytes; each stream decodes to 600. Streams one and two fit (600, 1200); the
        // third is over the ceiling when it decodes, so it is not cached.
        var budget = new DecodeMemoBudget(1000);
        var s1 = Flate(budget);
        var s2 = Flate(budget);
        var s3 = Flate(budget);

        // s1: 0 < 1000 -> cached. A repeated read is the same array.
        byte[] a1 = s1.GetDecodedData();
        Assert.Same(a1, s1.GetDecodedData());
        Assert.Equal(DecodedSize, a1.Length);

        // s2: 600 < 1000 -> cached (now 1200 retained).
        Assert.Same(s2.GetDecodedData(), s2.GetDecodedData());

        // s3: 1200 >= 1000 -> NOT cached. A repeated read re-decodes to a fresh, equal array —
        // this is the bound: the retained total cannot keep growing with the stream count.
        byte[] b3 = s3.GetDecodedData();
        Assert.NotSame(b3, s3.GetDecodedData());
        Assert.Equal(DecodedSize, b3.Length);
    }

    [Fact]
    public void AStreamWithNoBudgetAlwaysCaches()
    {
        // The control: a stream the parser did not build (a writer's) has no budget and caches every
        // decode, as before — so the ceiling above is the budget working, not caching being removed.
        var s = Flate(null);
        Assert.Same(s.GetDecodedData(), s.GetDecodedData());
    }
}
