// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// <see cref="FilterPipeline.TryGetStages"/> and <see cref="FilterPipeline.DecodeStages"/>: the stages
/// <see cref="FilterPipeline.Decode(byte[], PdfObject, PdfObject)"/> runs, for a caller that runs some of
/// them itself (the raster renderer's decimating decode) — paired as Decode pairs them, and
/// refused wherever Decode would report or refuse.
/// </summary>
/// <remarks>Joins the limits collection: the chain-length case reads <see cref="PdfLimits.MaxFilterChainLength"/>.</remarks>
[Collection(Constants.PdfLimitsCollection.Name)]
public sealed class FilterPipelineStagesTests
{
    private static PdfDictionary Predictor(int n) => new PdfDictionary { [PdfName.Predictor] = new PdfNumber(n) };

    /// <summary>A clean chain: each name with the dictionary its position pairs with it, a <c>null</c> element as no parameters.</summary>
    [Fact]
    public void AChain_IsPairedByPosition()
    {
        var png = Predictor(12);
        Assert.True(FilterPipeline.TryGetStages(new PdfArray { PdfName.AHx, PdfName.FlateDecode },
            new PdfArray { PdfNull.Instance, png }, out var filters, out var parameters));
        Assert.Equal(new[] { "AHx", "FlateDecode" }, filters);
        Assert.Null(parameters[0]);
        Assert.Same(png, parameters[1]);

        // One name: the dictionary is its (§7.3.8.2); an array beside it is not.
        Assert.True(FilterPipeline.TryGetStages(PdfName.FlateDecode, png, out _, out parameters));
        Assert.Same(png, parameters[0]);
        Assert.True(FilterPipeline.TryGetStages(PdfName.FlateDecode, new PdfArray { png }, out _, out parameters));
        Assert.Null(parameters[0]);
        // A one-element array of filters takes a dictionary as well as an array.
        Assert.True(FilterPipeline.TryGetStages(new PdfArray { PdfName.FlateDecode }, png, out _, out parameters));
        Assert.Same(png, parameters[0]);
    }

    /// <summary>
    /// Wherever Decode reports or refuses, there are no stages: a dictionary beside several filters,
    /// a parameter element that is not a dictionary, a filter element that is not a name, a name it
    /// does not know, no stage, a <c>/Filter</c> that is neither a name nor an array, a chain past the cap.
    /// </summary>
    [Fact]
    public void WhereDecodeWouldReportOrRefuse_ThereAreNoStages()
    {
        var two = new PdfArray { PdfName.AHx, PdfName.FlateDecode };
        Assert.False(FilterPipeline.TryGetStages(two, Predictor(12), out _, out _));
        Assert.True(FilterPipeline.TryGetStages(two, new PdfDictionary(), out _, out _));   // an empty one loses nothing
        Assert.False(FilterPipeline.TryGetStages(two, new PdfArray { PdfNull.Instance, new PdfNumber(12) }, out _, out _));
        Assert.False(FilterPipeline.TryGetStages(new PdfArray { PdfName.AHx, new PdfNumber(1) }, null, out _, out _));
        Assert.False(FilterPipeline.TryGetStages(new PdfArray { PdfName.AHx, new PdfName("FooDecode") }, null, out _, out _));
        Assert.False(FilterPipeline.TryGetStages(new PdfArray(), null, out _, out _));
        Assert.False(FilterPipeline.TryGetStages(new PdfNumber(3), null, out _, out _));
        Assert.False(FilterPipeline.TryGetStages(null, null, out _, out _));

        var atCap = new PdfArray();
        for (int i = 0; i < PdfLimits.MaxFilterChainLength; i++) atCap.Add(PdfName.AHx);
        Assert.True(FilterPipeline.TryGetStages(atCap, null, out _, out _));
        atCap.Add(PdfName.AHx);
        Assert.False(FilterPipeline.TryGetStages(atCap, null, out _, out _));
    }

    /// <summary>The first stages run as Decode runs them, their output counted; the rest left alone.</summary>
    [Fact]
    public void DecodeStages_RunsTheFirstStages_AndCountsTheirOutput()
    {
        // "Hi" hex-encoded twice: two stages make it; one leaves it hex-encoded once.
        byte[] twice = Encoding.ASCII.GetBytes("343836393E>");
        var filters = new List<string> { "AHx", "AHx" };
        var parameters = new List<PdfDictionary> { null, null };

        long cumulative = 0;
        byte[] one = FilterPipeline.DecodeStages(twice, filters, parameters, 1, PdfLimits.MaxDecompressedBytes, null, ref cumulative, out bool applied);
        Assert.Equal(Encoding.ASCII.GetBytes("4869>"), one);
        Assert.Equal(5, cumulative);
        Assert.True(applied);

        cumulative = 0;
        byte[] both = FilterPipeline.DecodeStages(twice, filters, parameters, 2, PdfLimits.MaxDecompressedBytes, null, ref cumulative, out _);
        Assert.Equal(Encoding.ASCII.GetBytes("Hi"), both);
        Assert.Equal(7, cumulative);

        cumulative = 0;
        Assert.Same(twice, FilterPipeline.DecodeStages(twice, filters, parameters, 0, PdfLimits.MaxDecompressedBytes, null, ref cumulative, out applied));
        Assert.False(applied);
    }
}
