// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="SvgToPdfConverter"/> reports a <c>clip-path</c>, <c>mask</c> or <c>filter</c> it cannot
/// resolve (GAPS.md §5d).
/// </summary>
/// <remarks>
/// The converter wrote no filter until batch 53, so it drew the element as if the property were
/// absent, and <see cref="SvgToPdfConverter.LastError"/> stayed <c>null</c> until batch 17. Opacity and
/// a colour's alpha were reported here too until batch 21 applied them (<c>SvgOpacityTests</c>); a
/// <c>clip-path</c> is applied since batch 27 (<c>SvgClipPathTests</c>) and a <c>mask</c> since batch
/// 38 (<c>SvgMaskTests</c>) and a <c>filter</c> since batch 53 (<c>SvgFilterTests</c>), and the
/// <c>url(#c)</c>, <c>url(#m)</c> and <c>url(#f)</c> these rows use name none, so they are still reported. Markers are drawn since batch 28 and apply to no
/// rect, so their rows went (<c>SvgMarkerTests</c> reports a missing marker).
/// </remarks>
public class SvgUnappliedPropertyTests
{
    private static readonly SvgConversionOptions Unflipped =
        new SvgConversionOptions { PageWidth = 200, PageHeight = 200, FlipY = false };

    [Theory]
    [InlineData(@"clip-path=""url(#c)""", "clip-path")]
    [InlineData(@"mask=""url(#m)""", "mask")]
    [InlineData(@"filter=""url(#f)""", "filter")]
    [InlineData(@"style=""clip-path: url(#c)""", "clip-path")]
    public void AnUnappliedPropertyIsReported(string attribute, string property)
    {
        string c = Content(@"<rect width=""10"" height=""10"" " + attribute + "/>");

        Assert.Contains($"'{property}'", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
        Assert.Single(Lines(c), l => l.EndsWith(" re", StringComparison.Ordinal));   // still drawn, without it
    }

    /// <summary>A group's property is reported too: it covers everything the group draws.</summary>
    [Fact]
    public void AGroupsClipPathIsReported()
    {
        Content(@"<g clip-path=""url(#c)""><rect width=""10"" height=""10""/></g>");

        Assert.Contains("'clip-path'", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// A value that asks for nothing is not reported: <c>none</c> and <c>inherit</c>. The control,
    /// the same element with a clip, is.
    /// </summary>
    [Fact]
    public void AValueThatAppliesNothingIsNotReported()
    {
        Content(@"<rect width=""10"" height=""10"" clip-path=""none"" mask=""inherit"" filter=""none"" marker-end=""none""/>");
        string quiet = SvgToPdfConverter.LastError;
        Content(@"<rect width=""10"" height=""10"" clip-path=""url(#c)""/>");
        string control = SvgToPdfConverter.LastError;

        Assert.Null(quiet);
        Assert.Contains("'clip-path'", control ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// Two elements with an unresolved clip, or an unresolved filter, are one report each: each is
    /// reported where it is resolved (the clip since batch 27, the filter since batch 53).
    /// </summary>
    [Theory]
    [InlineData("clip-path")]
    [InlineData("filter")]
    public void AnUnappliedPropertyIsReportedOnce(string property)
    {
        Content($@"<rect width=""10"" height=""10"" {property}=""url(#a)""/><rect width=""10"" height=""10"" {property}=""url(#b)""/>");
        string error = SvgToPdfConverter.LastError ?? "";

        Assert.Equal(1, error.Split(new[] { $"'{property}'" }, StringSplitOptions.None).Length - 1);
    }

    // ------------------------------------------------------------------ helpers

    private static string Content(string body)
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" + body + "</svg>", Unflipped);
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData());
    }

    private static string[] Lines(string content) => content.Split('\n').Select(l => l.Trim()).ToArray();
}
