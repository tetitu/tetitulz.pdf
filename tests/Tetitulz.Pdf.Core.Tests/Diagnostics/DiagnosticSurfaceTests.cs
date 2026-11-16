// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Functions;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Fonts.Cff;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Diagnostics;

/// <summary>
/// Locks in the contract of the <c>Last*Error</c> / <c>Last*</c> diagnostic
/// properties added across several remediation rounds.  These properties are the
/// difference between "silently fell back to a default" and "actively failed
/// with a specific reason"; without test coverage they tend to silently break
/// when the silent-catch sites are refactored.
/// </summary>
public class DiagnosticSurfaceTests
{
    // ==================================================================
    //  CffParser.LastFailureReason — distinguishes CFF2 / unsupported
    //  feature from generic malformed-data.
    // ==================================================================

    [Fact]
    public void CffParser_EmptyData_ReportsEmptyData()
    {
        var result = CffParser.Parse(System.Array.Empty<byte>());
        Assert.Null(result);
        Assert.Equal(CffParser.CffFailure.EmptyData, CffParser.LastFailureReason);
    }

    [Fact]
    public void CffParser_Cff2Major_ReportsUnsupportedFeature()
    {
        // CFF2 starts with major == 2.  We sniff the version before parsing.
        var cff2Header = new byte[] { 2, 0, 5, 2 };
        var result = CffParser.Parse(cff2Header);
        Assert.Null(result);
        Assert.Equal(CffParser.CffFailure.UnsupportedFeature, CffParser.LastFailureReason);
    }

    [Fact]
    public void CffParser_UnknownMajor_ReportsUnsupportedVersion()
    {
        // Major == 3 isn't a known CFF version.
        var bogusHeader = new byte[] { 3, 0, 4, 2 };
        var result = CffParser.Parse(bogusHeader);
        Assert.Null(result);
        Assert.Equal(CffParser.CffFailure.UnsupportedVersion, CffParser.LastFailureReason);
    }

    // ==================================================================
    //  PdfFunctionEvaluator.LastUnknownPostScriptOps — surfaces unknown
    //  Type 4 PostScript operators.
    // ==================================================================

    [Fact]
    public void PdfFunctionEvaluator_UnknownOp_RecordedInLastUnknownOps()
    {
        // Build a Type 4 stream that uses an operator the evaluator doesn't know.
        // "frobnicate" is definitely not a real PostScript op.
        var streamBody = "{ 1 frobnicate }";
        var stream = new PdfStream();
        stream.Set(new PdfName("FunctionType"), new PdfNumber(4));
        stream.Set(new PdfName("Domain"),       new PdfArray { new PdfNumber(0), new PdfNumber(1) });
        stream.Set(new PdfName("Range"),        new PdfArray { new PdfNumber(0), new PdfNumber(1) });
        stream.RawData = System.Text.Encoding.ASCII.GetBytes(streamBody);

        PdfFunctionEvaluator.Evaluate(stream, new[] { 0.5f });

        Assert.Contains("frobnicate", PdfFunctionEvaluator.LastUnknownPostScriptOps);
    }

    // ==================================================================
    //  Jbig2DecodeFilter.LastFallbackReason — captures decoder errors so
    //  callers can diagnose "image is garbage" reports.
    // ==================================================================

    [Fact]
    public void Jbig2DecodeFilter_RawPassthroughOnEmpty_ResetsLastFallbackReason()
    {
        // Passing empty data should round-trip without setting a failure.
        var filter = new Jbig2DecodeFilter();
        var result = filter.Decode(System.Array.Empty<byte>(), null);
        Assert.NotNull(result);
        Assert.Empty(result);
        Assert.Null(Jbig2DecodeFilter.LastFallbackReason);
    }

    // ==================================================================
    //  CMapParser.LastUsecmapName — exposed predefined-CMap chain target.
    // ==================================================================

    [Fact]
    public void CMapParser_PlainCMapWithoutUsecmap_LeavesLastUsecmapNameNull()
    {
        var cmapText = "/CIDInit /ProcSet findresource begin\n12 dict begin\nbeginbfchar\n<0001><0041>\nendbfchar\nend\nend";
        var bytes = System.Text.Encoding.ASCII.GetBytes(cmapText);
        var result = CMapParser.Parse(bytes);
        Assert.NotNull(result);
        Assert.Null(CMapParser.LastUsecmapName);
    }

    // ==================================================================
    //  PdfFunctionEvaluator.LastType0Failure — surfaces why Type 0
    //  evaluation produced the [0f] sentinel.
    // ==================================================================

    [Fact]
    public void PdfFunctionEvaluator_Type0WithNonStreamDict_ReportsFailure()
    {
        // A Type 0 function dict that's NOT a stream (no sample table at all).
        var dict = new PdfDictionary();
        dict.Set(new PdfName("FunctionType"), new PdfNumber(0));
        dict.Set(new PdfName("Domain"), new PdfArray { new PdfNumber(0), new PdfNumber(1) });
        dict.Set(new PdfName("Size"),   new PdfArray { new PdfNumber(2) });
        dict.Set(new PdfName("BitsPerSample"), new PdfNumber(8));

        var result = PdfFunctionEvaluator.Evaluate(dict, new[] { 0.5f });
        Assert.NotNull(result);
        Assert.NotNull(PdfFunctionEvaluator.LastType0Failure);
        Assert.Contains("not a stream", PdfFunctionEvaluator.LastType0Failure);
    }

    // ==================================================================
    //  FallbackFont.Win1252FallbackReason — null when CP-1252 is reachable.
    // ==================================================================

    [Fact]
    public void FallbackFont_Win1252FallbackReason_IsNullOnHealthyRuntime()
    {
        // On every supported runtime CP-1252 must be reachable, either
        // directly or after registering CodePagesEncodingProvider.
        // Force the static-initialiser to run by touching the type.
        _ = new FallbackFont();
        Assert.Null(FallbackFont.Win1252FallbackReason);
    }

    // ==================================================================
    //  CffParser failure resets between calls — protects callers who
    //  read LastFailureReason after each Parse.
    // ==================================================================

    [Fact]
    public void CffParser_FailureReasonResetsBetweenCalls()
    {
        // Empty data sets EmptyData…
        CffParser.Parse(System.Array.Empty<byte>());
        Assert.Equal(CffParser.CffFailure.EmptyData, CffParser.LastFailureReason);

        // …then a CFF2 header should overwrite, not leak EmptyData.
        CffParser.Parse(new byte[] { 2, 0, 5, 2 });
        Assert.Equal(CffParser.CffFailure.UnsupportedFeature, CffParser.LastFailureReason);
    }
}
