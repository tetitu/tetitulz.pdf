// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

// These tests mutate and assert on FilterPipeline's process-global statics
// (StrictUnknownFilters / LastUnknownFilterName). Run them in a non-parallel
// collection so a concurrent unknown-filter decode in another test class cannot
// clobber LastUnknownFilterName between Decode() and the assertion.
[CollectionDefinition("FilterPipelineStatics", DisableParallelization = true)]
public sealed class FilterPipelineStaticsCollection { }

/// <summary>
/// Locks in §61 (B1): an unrecognised <c>/Filter</c> name now records
/// <see cref="FilterPipeline.LastUnknownFilterName"/> and either passes
/// the bytes through (lenient default) or throws <see cref="PdfFilterException"/>
/// when <see cref="FilterPipeline.StrictUnknownFilters"/> is true.
///
/// Pre-§61 the branch silently set <c>result = new byte[0]</c>, so a
/// PDF whose <c>/Filter</c> entry was a typo of <c>JBIG2Decode</c> lost
/// the whole stream content with no diagnostic.
/// </summary>
[Collection("FilterPipelineStatics")]
public class FilterPipelineUnknownFilterTests
{
    /// <summary>
    /// A <c>/DecodeParms</c> dictionary beside several filters names no stage, so it is reported
    /// rather than dropped in silence; the stages still run.
    /// </summary>
    /// <remarks>
    /// §7.3.8.2 requires an array once there is more than one filter. The one-filter case is the
    /// opposite — the dictionary is the required form, and it is applied — so it is the control, and
    /// an empty dictionary, which loses nothing, is the other.
    /// </remarks>
    [Theory]
    [InlineData(2, "<< /Predictor 12 >>", true)]
    [InlineData(2, "<< >>", false)]
    [InlineData(1, "<< /Predictor 1 >>", false)]
    public void Decode_ADictionaryBesideSeveralFilters_IsReported(int stages, string parms, bool reported)
    {
        FilterPipeline.StrictUnknownFilters = false;
        var filters = new PdfArray();
        for (int i = 0; i < stages; i++) filters.Add(PdfName.ASCIIHexDecode);
        var dict = new PdfDictionary();
        if (parms.Contains("Predictor")) dict.Set(PdfName.Predictor, new PdfNumber(parms.Contains("12") ? 12 : 1));

        // "Hi", hex-encoded once per stage.
        byte[] payload = Encoding.ASCII.GetBytes(stages == 2 ? "343836393E>" : "4869>");
        byte[] result = FilterPipeline.Decode(payload, filters, dict);

        Assert.Equal(Encoding.ASCII.GetBytes("Hi"), result);
        Assert.Equal(reported, FilterPipeline.LastUnknownFilterName?.Contains("/DecodeParms is a dictionary") == true);
    }

    /// <summary>In strict mode the stray dictionary refuses the decode, as a stray element does.</summary>
    [Fact]
    public void Decode_ADictionaryBesideSeveralFilters_StrictMode_Throws()
    {
        var filters = new PdfArray { PdfName.ASCIIHexDecode, PdfName.ASCIIHexDecode };
        var dict = new PdfDictionary();
        dict.Set(PdfName.Predictor, new PdfNumber(12));

        FilterPipeline.StrictUnknownFilters = true;
        try
        {
            Assert.Throws<PdfFilterException>(() =>
                FilterPipeline.Decode(Encoding.ASCII.GetBytes("343836393E>"), filters, dict));
        }
        finally
        {
            FilterPipeline.StrictUnknownFilters = false;
        }
    }

    [Fact]
    public void Decode_UnknownFilter_LenientMode_PassesBytesThrough()
    {
        byte[] payload = Encoding.ASCII.GetBytes("not really compressed");
        FilterPipeline.StrictUnknownFilters = false;

        byte[] result = FilterPipeline.Decode(payload, new PdfName("MysteryDecode"), null);

        Assert.Equal(payload, result);
        Assert.Equal("MysteryDecode", FilterPipeline.LastUnknownFilterName);
    }

    /// <summary>
    /// A decode that used only known filters must not report the PREVIOUS decode's unknown one.
    /// The slot was written on three paths and cleared only by <c>ResetLastUnknownFilterName</c>,
    /// which nothing outside the test suite calls — so after one bad stream every later stream on
    /// that thread looked like it had carried the same unknown filter (GAPS.md §16a).
    /// </summary>
    [Fact]
    public void Decode_KnownFilterAfterUnknown_ClearsTheSlot()
    {
        FilterPipeline.StrictUnknownFilters = false;
        byte[] payload = Encoding.ASCII.GetBytes("not really compressed");

        FilterPipeline.Decode(payload, new PdfName("MysteryDecode"), null);
        Assert.Equal("MysteryDecode", FilterPipeline.LastUnknownFilterName);

        // ASCIIHexDecode is a known filter, so this decode has nothing to report.
        byte[] hex = Encoding.ASCII.GetBytes("48656C6C6F>");
        byte[] result = FilterPipeline.Decode(hex, PdfName.ASCIIHexDecode, null);

        Assert.Equal(Encoding.ASCII.GetBytes("Hello"), result);
        Assert.Null(FilterPipeline.LastUnknownFilterName);
    }

    /// <summary>
    /// The reset happens before the no-<c>/Filter</c> early return, which is the path a plain
    /// uncompressed stream takes — the commonest decode in any document, and the one that would
    /// otherwise leave a stale name in the slot forever.
    /// </summary>
    [Fact]
    public void Decode_NoFilterAfterUnknown_ClearsTheSlot()
    {
        FilterPipeline.StrictUnknownFilters = false;
        byte[] payload = Encoding.ASCII.GetBytes("not really compressed");

        FilterPipeline.Decode(payload, new PdfName("MysteryDecode"), null);
        Assert.Equal("MysteryDecode", FilterPipeline.LastUnknownFilterName);

        byte[] result = FilterPipeline.Decode(payload, null, null);

        Assert.Equal(payload, result);
        Assert.Null(FilterPipeline.LastUnknownFilterName);
    }

    [Fact]
    public void Decode_UnknownFilter_StrictMode_Throws()
    {
        try
        {
            FilterPipeline.StrictUnknownFilters = true;
            byte[] payload = Encoding.ASCII.GetBytes("not really compressed");
            Assert.Throws<PdfFilterException>(() =>
                FilterPipeline.Decode(payload, new PdfName("MysteryDecode"), null));
        }
        finally
        {
            FilterPipeline.StrictUnknownFilters = false;
        }
    }
}
