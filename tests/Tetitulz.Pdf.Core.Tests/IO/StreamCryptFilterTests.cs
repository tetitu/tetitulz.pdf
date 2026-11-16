// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO;

/// <summary>
/// <see cref="StreamCryptFilter"/>: a stream's own <c>/Crypt</c> filter (ISO 32000-2 §7.4.10) — read
/// only where it is first, its name <c>/Identity</c> by default — and taken out of a decrypted stream
/// with its parameters, the filters after it kept with theirs.
/// </summary>
public class StreamCryptFilterTests
{
    [Fact]
    public void TheNameIsReadFromTheFirstFiltersParameters_IdentityByDefault()
    {
        Assert.True(StreamCryptFilter.TryGetName(Stream(new PdfArray { PdfName.Crypt, PdfName.FlateDecode },
            new PdfArray { Parms("StdCF"), PdfNull.Instance }), out PdfName? named));
        Assert.Equal("StdCF", named!.Value);

        Assert.True(StreamCryptFilter.TryGetName(Stream(PdfName.Crypt, null), out PdfName? unnamed));
        Assert.Equal(PdfName.Identity, unnamed);
    }

    /// <summary>Not first, not a crypt filter (§7.4.10 puts it first): a later <c>/Crypt</c> is not read, nor a stream without one.</summary>
    [Fact]
    public void ACryptFilterNotFirst_IsNotOne()
    {
        Assert.False(StreamCryptFilter.TryGetName(Stream(new PdfArray { PdfName.FlateDecode, PdfName.Crypt }, null), out _));
        Assert.False(StreamCryptFilter.TryGetName(Stream(PdfName.FlateDecode, null), out _));
        Assert.False(StreamCryptFilter.TryGetName(new PdfStream(), out _));
    }

    [Fact]
    public void Removing_KeepsTheFiltersAfterIt_WithTheirParameters()
    {
        var flateParms = new PdfDictionary();
        flateParms.Set(PdfName.Predictor, new PdfNumber(12));
        PdfStream stream = Stream(new PdfArray { PdfName.Crypt, PdfName.FlateDecode }, new PdfArray { Parms("Identity"), flateParms });

        StreamCryptFilter.Remove(stream);

        var filters = Assert.IsType<PdfArray>(stream[PdfName.Filter]);
        Assert.Equal(PdfName.FlateDecode, Assert.Single(filters));
        Assert.Same(flateParms, Assert.Single(Assert.IsType<PdfArray>(stream[PdfName.DecodeParms])));
    }

    [Fact]
    public void Removing_TheOnlyFilter_LeavesNone()
    {
        PdfStream stream = Stream(PdfName.Crypt, Parms("Identity"));

        StreamCryptFilter.Remove(stream);

        Assert.Null(stream[PdfName.Filter]);
        Assert.Null(stream[PdfName.DecodeParms]);
    }

    private static PdfDictionary Parms(string name)
    {
        var d = new PdfDictionary();
        d.Set(PdfName.Name, new PdfName(name));
        return d;
    }

    private static PdfStream Stream(PdfObject filter, PdfObject? parms)
    {
        var s = new PdfStream();
        s.Set(PdfName.Filter, filter);
        if (parms != null) s.Set(PdfName.DecodeParms, parms);
        return s;
    }
}
