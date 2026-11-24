// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Base.Scripting;
using Tetitulz.Pdf.Plugins.JavaScript;
using Xunit;

namespace Tetitulz.Pdf.Plugins.JavaScript.Tests;

/// <summary>
/// The sandbox limits a document script runs under, pinned at their values, since a changed limit
/// is otherwise invisible: the recursion and memory limits are what stand between a hostile script
/// and the host process. The time limit is not here, because it can only be
/// observed by a clock, which a DoS-guard test must not assert.
/// </summary>
public class SandboxLimitTests
{
    private static PdfJsExecutionContext Run(string script)
    {
        var ctx = new PdfJsExecutionContext();
        PdfJsEngine.Execute(script, ctx);
        return ctx;
    }

    private static string Recurse(int depth)
        => $"function f(n){{ return n <= 0 ? 0 : 1 + f(n - 1); }} event.value = '' + f({depth});";

    /// <summary>
    /// The recursion limit is 64 frames of script: a function recursing to depth 64 completes and
    /// one recursing to 65 is stopped, as a failure the host can see rather than a stack overflow
    /// that would end the process.
    /// </summary>
    [Fact]
    public void RecursionStopsJustPastSixtyFourFrames()
    {
        var within = Run(Recurse(64));
        Assert.Null(within.Error);
        Assert.Equal("64", within.EventValue);

        var beyond = Run(Recurse(65));
        Assert.NotNull(beyond.Error);
        Assert.False(beyond.ValidationPassed);
    }

    /// <summary>
    /// The memory limit is 32 MiB of allocation on the script's thread. 100,000 short strings fit;
    /// 200,000 do not. The limit trips at about 150,000, so twice the limit would admit both.
    /// </summary>
    [Fact]
    public void MemoryIsLimitedToThirtyTwoMebibytes()
    {
        const string fill = "var a = []; for (var i = 0; i < {0}; i++) a.push('k' + i);";

        Assert.Null(Run(string.Format(fill, 100_000)).Error);

        var over = Run(string.Format(fill, 200_000));
        Assert.Contains("memory limit", over.ValidationMessage);
        Assert.Contains("33554432", over.ValidationMessage);
    }

    /// <summary>
    /// The decimal places a script asks <c>AFNumber_Format</c> for are bounded before they reach a
    /// fixed-point format: a billion of them was two gigabytes in one call, which the memory limit
    /// checks only between statements. A hundred are formatted; one more is refused, failing closed.
    /// </summary>
    [Fact]
    public void AdversarialDecimalPlaces_AreRefusedPastAHundred_AndAHundredAreFormatted()
    {
        var hundred = new PdfJsExecutionContext { EventValue = "1.5" };
        PdfJsEngine.Execute("AFNumber_Format(100, 1, 0, 0, '', false);", hundred);
        Assert.Null(hundred.Error);
        Assert.Equal("1." + "5" + new string('0', 99), hundred.EventValue);

        foreach (int nDec in new[] { 101, 999_999_999 })
        {
            var refused = new PdfJsExecutionContext { EventValue = "1.5" };
            PdfJsEngine.Execute($"AFNumber_Format({nDec}, 1, 0, 0, '', false);", refused);
            Assert.NotNull(refused.Error);
            Assert.False(refused.ValidationPassed);
            Assert.Contains("decimal places", refused.ValidationMessage);
            Assert.Equal("1.5", refused.EventValue);
        }
    }

    /// <summary>
    /// <c>util.printf</c> is C-style, as Acrobat's, and its width and precision are the script's: a
    /// width or precision past three digits, and a precision past a hundred, is copied as written
    /// rather than padded to. Under the bound, the item is formatted at full size.
    /// </summary>
    [Theory]
    [InlineData("%.2f", "1.50", 0)]
    [InlineData("%8.3f", "   1.500", 0)]
    [InlineData("%010.2f", "0000001.50", 0)]
    [InlineData("%.100f", null, 102)]                 // a hundred places are formatted
    [InlineData("%999d", null, 999)]                  // a width of 999 is padded to
    [InlineData("%999s", "1.5", 0)]                   // %s takes no width, as Acrobat answers
    [InlineData("%.101f", "%.101f", 0)]
    [InlineData("%.999999999f", "%.999999999f", 0)]
    [InlineData("%1000000d", "%1000000d", 0)]
    [InlineData("%,01000.2f", "%,01000.2f", 0)]
    public void AdversarialPrintfWidthAndPrecision_AreCopiedAsWrittenPastTheBound(string format, string? expected, int length)
    {
        var ctx = Run($"event.value = util.printf('{format}', 1.5);");
        Assert.Null(ctx.Error);
        if (expected == null) Assert.Equal(length, ctx.EventValue.Length);
        else Assert.Equal(expected, ctx.EventValue);
    }

    /// <summary>
    /// A recorded request carries the field name the script passed, and is cut to a length: a name of
    /// a hundred thousand characters recorded thirty-two times made a report copied at every addition.
    /// </summary>
    [Fact]
    public void AdversarialFieldNamesInRecordedRequests_AreCut_AndAShortOneIsKeptWhole()
    {
        Run("var n = 'x'; while (n.length < 100000) n = n + n; " +
            "for (var i = 0; i < 40; i++) this.getField(n).readonly = true;");
        string report = PdfJsEngine.LastUnsupportedRequest;
        Assert.True(report.Length < 32 * (200 + 3) + 64,
            $"{report.Length} characters recorded");
        Assert.Contains("further request(s) not recorded", report);

        Run("this.getField('Total').readonly = true;");
        Assert.Equal("Total.readonly = true", PdfJsEngine.LastUnsupportedRequest);
    }

    /// <summary>
    /// A script that rejects a value without saying why still leaves a reason; one that gives its
    /// own reason keeps it.
    /// </summary>
    [Fact]
    public void ARejectionWithoutAMessageGetsADefaultOne()
    {
        Assert.Equal("Validation failed.", Run("event.rc = false;").ValidationMessage);

        var ctx = new PdfJsExecutionContext { EventValue = "50" };
        PdfJsEngine.Execute("AFRange_Validate(true, 0, true, 10);", ctx);
        Assert.False(ctx.ValidationPassed);
        Assert.NotEqual("Validation failed.", ctx.ValidationMessage);
        Assert.NotNull(ctx.ValidationMessage);
    }
}
