// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text;
using Tetitulz.Pdf.Accessibility.Internal;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The tagger marks each path it paints, and each shading, as a layout artifact — every piece of content is
/// in the structure tree or an artifact (ISO 14289-1 7.1, ISO 14289-2 §8.2.2, §8.3.1) — and wraps exactly the
/// operators of the path, never across a boundary a marked-content sequence must not cut.
/// </summary>
public sealed class PathArtifactTaggingTests
{
    private const string Text = "BT /F1 10 Tf 72 400 Td (Body line.) Tj ET\n";
    private const string Open = "\n/Artifact <</Type /Layout>> BDC\n";
    private const string Close = "\nEMC\n";

    private static string Tagged(string content, out PageTagResult result)
    {
        result = ContentStreamTagger.TagPage(Encoding.Latin1.GetBytes(content), new PageTagOptions { PageHeight = 792f, MarkPathsAsArtifacts = true });
        Assert.True(result.Changed, result.SkipReason);
        return Encoding.Latin1.GetString(result.NewContent);
    }

    private static int Count(string haystack, string needle)
    {
        int n = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }

    /// <summary>A filled path, a stroked one and a shading each become a layout artifact, wrapping exactly their operators.</summary>
    [Theory]
    [InlineData("0 0 10 10 re f")]
    [InlineData("0 0 m 10 10 l 5 0 l h S")]
    [InlineData("0 0 m 1 0 0 RG 10 10 l S")]                 // a state operator inside a path goes with it
    [InlineData("0 0 10 10 re W n 0 0 5 5 re f")]            // a clip-only path is passed over, the next one wrapped
    [InlineData("/Sh0 sh")]
    public void APaintedPathOrShading_IsALayoutArtifact(string drawing)
    {
        string expectedPart = drawing.Contains("W n", StringComparison.Ordinal) ? "0 0 5 5 re f" : drawing;
        string spliced = Tagged(Text + drawing + "\n", out _);
        Assert.Contains(Open + expectedPart + Close, spliced, StringComparison.Ordinal);
        Assert.Equal(1, Count(spliced, "/Artifact"));
    }

    /// <summary>Paths painted one after the other are one sequence; paths a q…Q separates are one each, inside it.</summary>
    [Fact]
    public void AdjacentPathsShareASequence_PathsInSeparateSavesDoNot()
    {
        string adjacent = Tagged(Text + "0 0 10 10 re f 0 0 m 5 5 l S\n", out _);
        Assert.Contains(Open + "0 0 10 10 re f 0 0 m 5 5 l S" + Close, adjacent, StringComparison.Ordinal);
        Assert.Equal(1, Count(adjacent, "/Artifact"));

        string saved = Tagged(Text + "q 0 0 10 10 re f Q q 0 0 5 5 re S Q\n", out _);
        Assert.Contains("q " + Open + "0 0 10 10 re f" + Close + " Q", saved, StringComparison.Ordinal);
        Assert.Contains("q " + Open + "0 0 5 5 re S" + Close + " Q", saved, StringComparison.Ordinal);
        Assert.Equal(2, Count(saved, "/Artifact"));

        // A q inside a path (malformed, and met) ends it unwrapped: the sequence starts after it.
        string cut = Tagged(Text + "0 0 m q 10 10 l S Q\n", out _);
        Assert.Contains("q " + Open + "10 10 l S" + Close + " Q", cut, StringComparison.Ordinal);
        Assert.Equal(1, Count(cut, "/Artifact"));
    }

    /// <summary>A path ended by n paints nothing, and is not wrapped.</summary>
    [Theory]
    [InlineData("0 0 10 10 re n")]
    [InlineData("0 0 10 10 re W n")]
    public void AnUnpaintedPath_IsNotWrapped(string drawing)
    {
        string spliced = Tagged(Text + drawing + "\n", out _);
        Assert.DoesNotContain("/Artifact <<", spliced, StringComparison.Ordinal);
        Assert.Contains("/P <</MCID 0>> BDC", spliced, StringComparison.Ordinal);
    }

    /// <summary>
    /// Completing a page, a path an existing artifact already marks is left as it is; the unmarked path beside
    /// it is wrapped.
    /// </summary>
    [Fact]
    public void APathAnExistingArtifactMarks_IsLeftAsItIs()
    {
        var options = new PageTagOptions { PageHeight = 792f, MarkPathsAsArtifacts = true, AdoptExistingMarkedContent = true };
        PageTagResult result = ContentStreamTagger.TagPage(
            Encoding.Latin1.GetBytes(Text + "/Artifact BMC 0 0 10 10 re f EMC 0 0 5 5 re S\n"), options);
        Assert.True(result.Changed, result.SkipReason);
        string spliced = Encoding.Latin1.GetString(result.NewContent);
        Assert.Contains("/Artifact BMC 0 0 10 10 re f EMC", spliced, StringComparison.Ordinal);
        Assert.Contains(Open + "0 0 5 5 re S" + Close, spliced, StringComparison.Ordinal);
        Assert.Equal(1, Count(spliced, "/Artifact <<"));
    }

    /// <summary>Without <see cref="PageTagOptions.MarkPathsAsArtifacts"/> — the header and footer marker's pass — no path is wrapped.</summary>
    [Fact]
    public void WithoutTheOption_NoPathIsWrapped()
    {
        PageTagResult result = ContentStreamTagger.TagPage(Encoding.Latin1.GetBytes(Text + "0 0 10 10 re f\n"), new PageTagOptions { PageHeight = 792f });
        Assert.True(result.Changed, result.SkipReason);
        Assert.DoesNotContain("/Artifact", Encoding.Latin1.GetString(result.NewContent), StringComparison.Ordinal);

        PageTagResult drawingOnly = ContentStreamTagger.TagPage(Encoding.Latin1.GetBytes("0 0 10 10 re f\n"), new PageTagOptions { PageHeight = 792f });
        Assert.False(drawingOnly.Changed);
    }

    /// <summary>A page that only draws is tagged too — its paths become artifacts — where it was skipped as having no text.</summary>
    [Fact]
    public void APageOfDrawingsAlone_HasItsPathsMarked()
    {
        string spliced = Tagged("0 0 10 10 re f\n", out PageTagResult result);
        Assert.Contains(Open + "0 0 10 10 re f" + Close, spliced, StringComparison.Ordinal);
        TaggedRun run = Assert.Single(result.Runs);
        Assert.True(run.IsArtifact);
        Assert.Equal(-1, run.Mcid);
    }

    /// <summary>A page that only invokes a form keeps the reason that names the form: nothing on it is the tagger's to mark.</summary>
    [Fact]
    public void APageThatOnlyDrawsAForm_IsStillSkippedForItsForm()
    {
        PageTagResult result = ContentStreamTagger.TagPage(Encoding.Latin1.GetBytes("/Fm0 Do\n"), new PageTagOptions { PageHeight = 792f, MarkPathsAsArtifacts = true });
        Assert.False(result.Changed);
        Assert.Contains("Form XObject", result.SkipReason, StringComparison.Ordinal);
    }
}
