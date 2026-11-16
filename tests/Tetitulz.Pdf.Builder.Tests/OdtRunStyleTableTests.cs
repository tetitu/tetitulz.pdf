// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// The names an ODT run-style table hands out and the formattings it declines to name, which the
/// 2026-10-10 overnight sweep found unpinned: the first name, the count-based numbering, the case
/// of a font name, and the normalisation of a colour before it is compared with the default.
/// </summary>
public class OdtRunStyleTableTests
{
    [Fact]
    public void TheFirstDistinctFormatting_IsNamedT1()
    {
        var table = new OdtRunStyleTable();

        Assert.Equal("T1", table.NameFor(new TextRun { IsBold = true }));
    }

    [Fact]
    public void EachNewFormatting_TakesTheNextNumber_AndARepeatTakesItsFirst()
    {
        var table = new OdtRunStyleTable();

        Assert.Equal("T1", table.NameFor(new TextRun { IsBold = true }));
        Assert.Equal("T2", table.NameFor(new TextRun { IsItalic = true }));
        Assert.Equal("T1", table.NameFor(new TextRun { IsBold = true }));
    }

    [Fact]
    public void AFontNameThatDiffersOnlyInCase_IsStillADeviationFromHelvetica()
    {
        var table = new OdtRunStyleTable();

        Assert.NotNull(table.NameFor(new TextRun { FontName = "helvetica" }));
        Assert.Contains("style:font-name=\"helvetica\"", Declarations(table), System.StringComparison.Ordinal);
    }

    [Fact]
    public void TheExactDefaultFontName_IsNotADeviation()
    {
        var table = new OdtRunStyleTable();

        Assert.Null(table.NameFor(new TextRun { FontName = StandardFonts.Helvetica }));
    }

    /// <summary>
    /// A colour spelt in its short form is the default colour, so a run set to it carries no
    /// formatting and is not given a style that says nothing.
    /// </summary>
    [Fact]
    public void AShortFormDefaultColour_IsNotADeviation()
    {
        var table = new OdtRunStyleTable();

        Assert.Null(table.NameFor(new TextRun { Color = "#000" }));
    }

    [Fact]
    public void AColourThatDiffersFromTheDefault_IsADeviation()
    {
        var table = new OdtRunStyleTable();

        Assert.NotNull(table.NameFor(new TextRun { Color = "#ff0000" }));
        Assert.Contains("fo:color=\"#ff0000\"", Declarations(table), System.StringComparison.Ordinal);
    }

    private static string Declarations(OdtRunStyleTable table)
    {
        using var ms = new MemoryStream();
        using (var w = new StreamWriter(ms, new UTF8Encoding(false), 1024, leaveOpen: true))
            table.WriteDeclarations(w);
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
