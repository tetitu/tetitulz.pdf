// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Document;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// A print job wrapped in HP's Printer Job Language (the PJL Technical Reference Manual's job
/// format) — how the Microsoft PS Class Driver and most PostScript printer drivers write one: the
/// Universal Exit Language, PJL lines, <c>@PJL ENTER LANGUAGE = POSTSCRIPT</c>, the program, then UEL,
/// end-of-job PJL and UEL. The program is taken out of the wrapper; another language is refused.
/// </summary>
public sealed class PjlJobTests
{
    private const string Uel = "\u001B%-12345X";
    private const string Program = "%!PS-Adobe-3.0\n0.5 setgray 10 10 80 80 rectfill showpage\n";

    // The Microsoft PS Class Driver's own wrapper, as it spooled a job.
    private const string DriverJob =
        Uel + "@PJL COMMENT MSxpsPS\r\n@PJL ENTER LANGUAGE=POSTSCRIPT\r\n" + Program +
        Uel + Uel + "@PJL EOJ\r\n" + Uel;

    private static (byte[] Pdf, PostScriptDocumentResult Result) Convert(string job)
    {
        using var source = new MemoryStream(Encoding.Latin1.GetBytes(job));
        using var output = new MemoryStream();
        var result = PostScriptConversion.ConvertDocument(source, output);
        return (output.ToArray(), result);
    }

    private static string? Unwrapped(string job, out string? refusal, out string? ignored)
    {
        byte[]? program = PjlJob.PostScriptOf(Encoding.Latin1.GetBytes(job), out refusal, out ignored);
        return program == null ? null : Encoding.Latin1.GetString(program);
    }

    /// <summary>A driver's job converts as its program does: one page, clean — the PJL never reaches the interpreter.</summary>
    [Fact]
    public void ADriversJob_ConvertsAsItsProgram()
    {
        var (pdf, result) = Convert(DriverJob);
        Assert.True(result.IsClean, result.LastError);
        Assert.Equal(1, result.PageCount);
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        Assert.Single(doc.Pages);

        Assert.Equal(Program, Unwrapped(DriverJob, out _, out string? ignored));
        Assert.Null(ignored);
    }

    /// <summary>
    /// The control: the same job with its wrapper read as PostScript is not clean — the PJL lines
    /// scan as undefined names — so the clean conversion above is the unwrapping's.
    /// </summary>
    [Fact]
    public void AnUnwrappedReading_IsNotClean_TheControl()
    {
        var (_, result) = Convert("@PJL COMMENT MSxpsPS\r\n@PJL ENTER LANGUAGE=POSTSCRIPT\r\n" + Program);
        Assert.False(result.IsClean);
    }

    /// <summary>
    /// The rest of the command is not case-sensitive and its white space is spaces or tabs, optional
    /// around <c>=</c>; the carriage return before the line feed is optional. Only <c>@PJL</c> is upper case.
    /// </summary>
    [Theory]
    [InlineData("@PJL ENTER LANGUAGE = POSTSCRIPT\r\n")]
    [InlineData("@PJL enter language=postscript\n")]
    [InlineData("@PJL\tENTER\tLANGUAGE\t=\tPostScript\n")]
    public void TheEnterCommand_IsReadAsTheManualWritesIt(string enter)
        => Assert.Equal(Program, Unwrapped(Uel + "@PJL\r\n@PJL SET RESOLUTION = 600\r\n" + enter + Program + Uel, out _, out _));

    /// <summary>A job in another language is refused by name — its data never reaches the interpreter.</summary>
    [Fact]
    public void AJobInPcl_IsRefusedByName()
    {
        var (_, result) = Convert(Uel + "@PJL ENTER LANGUAGE = PCL\r\n\u001BE\u001B&l0O" + Uel);
        Assert.Contains("the PJL job is in PCL, not PostScript", result.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>With no ENTER, data that opens as PostScript is taken as PostScript, as a printer's sampling would; other data is refused.</summary>
    [Fact]
    public void WithNoEnter_OnlyPostScriptIsTaken()
    {
        Assert.Equal(Program, Unwrapped(Uel + "@PJL JOB NAME = \"x\"\r\n" + Program + Uel, out string? refusal, out _));
        Assert.Null(refusal);

        Assert.Null(Unwrapped(Uel + "@PJL JOB\r\n\u001BE text", out refusal, out _));
        Assert.Equal("the PJL job names no language, and its data is not PostScript", refusal);
    }

    /// <summary>The prefix is upper case: <c>@pjl</c> begins no command, so a job whose ENTER is written so is refused, not guessed at.</summary>
    [Fact]
    public void ALowerCasePrefix_IsNoCommand()
    {
        Assert.Null(Unwrapped(Uel + "@pjl ENTER LANGUAGE = POSTSCRIPT\n" + Program + Uel, out string? refusal, out _));
        Assert.Equal("the PJL job names no language, and its data is not PostScript", refusal);
    }

    /// <summary>A program with no closing UEL runs to the end of the data.</summary>
    [Fact]
    public void AProgramWithNoClosingUel_RunsToTheEnd()
        => Assert.Equal(Program, Unwrapped(Uel + "@PJL ENTER LANGUAGE = POSTSCRIPT\n" + Program, out _, out _));

    /// <summary>A PJL line the data ends inside has no line feed: refused by name.</summary>
    [Fact]
    public void APjlLineWithNoLineFeed_IsRefused()
    {
        Assert.Null(Unwrapped(Uel + "@PJL ENTER LANGUAGE = POSTSCRIPT", out string? refusal, out _));
        Assert.Equal("a PJL command line has no line feed to end it", refusal);
    }

    /// <summary>A second language section after the first one's UEL is not converted, and is said — by the conversion too; end-of-job PJL is not one.</summary>
    [Fact]
    public void ASecondSection_IsSaid()
    {
        string job = DriverJob + "@PJL ENTER LANGUAGE = POSTSCRIPT\n" + Program + Uel;
        Assert.Equal(Program, Unwrapped(job, out _, out string? ignored));
        Assert.Equal("a second section of the PJL job, in POSTSCRIPT, was not converted", ignored);
        Assert.Contains("a second section of the PJL job", Convert(job).Result.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// White space must follow <c>@PJL</c>: <c>@PJLENTER LANGUAGE = PCL</c> is no ENTER, so the
    /// PostScript after it is taken by sampling rather than refused as PCL.
    /// </summary>
    [Fact]
    public void AnEnterWithNoSpaceAfterThePrefix_IsNoCommand()
    {
        Assert.Equal(Program, Unwrapped(Uel + "@PJLENTER LANGUAGE = PCL\n" + Program + Uel, out string? refusal, out _));
        Assert.Null(refusal);
    }
}
