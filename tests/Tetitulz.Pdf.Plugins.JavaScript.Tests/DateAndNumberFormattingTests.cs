// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Threading;
using Tetitulz.Pdf.Base.Scripting;

namespace Tetitulz.Pdf.Plugins.JavaScript.Tests;

/// <summary>
/// A common viewer date tokens, number overflow, and the culture the engine parses dates in.
///
/// <para>Three separate MEDIUM findings meet here because they all decide what a form field
/// <i>displays</i>, and all three were silent: no exception, no <c>ctx.Error</c>, just a wrong
/// string in the document.</para>
/// </summary>
public class DateAndNumberFormattingTests
{
    private static readonly DateTime Aug13 = new DateTime(2026, 8, 13, 14, 5, 9, DateTimeKind.Utc);

    /// <summary>
    /// Runs <paramref name="body"/> under a comma-decimal, day-first culture. Without this the
    /// culture assertions cannot fail on an en-US developer machine — which is the whole reason
    /// the defect survived.
    /// </summary>
    private static void InCommaDecimalCulture(Action body)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            // de-DE: "," is the decimal separator and dates are d.M.yyyy — so a "/"-separated
            // date is read day-first, the opposite of the invariant reading.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            body();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    /// <summary>
    /// Formats <see cref="Aug13"/> through the engine's public surface (<c>util.printd</c>), which
    /// is how a document reaches the formatter. Going through the script keeps the test on the path
    /// a PDF actually takes rather than on an internal helper. The format is passed in a
    /// double-quoted JS string so an a common viewer single-quoted literal survives.
    /// </summary>
    private static string Printd(string format)
    {
        var ctx = new PdfJsExecutionContext();
        PdfJsEngine.Execute("event.value = util.printd(\"" + format + "\", \"2026-08-13T14:05:09\");", ctx);
        Assert.Null(ctx.Error);
        return ctx.EventValue;
    }

    // ────────────────────────────────────────────────────────────────────
    //  Date tokens
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The archetype from the finding. Under the old chained <c>String.Replace</c>,
    /// <c>mmm</c> fired inside <c>mmmm</c> and left a stray <c>m</c> that the <c>m</c> step then
    /// turned into the month number: "mmmm d, yyyy" produced "Aug8 13, 2026".
    /// </summary>
    [Fact]
    public void FullMonthToken_IsNotEatenByItsOwnPrefix()
    {
        Assert.Equal("August 13, 2026", Printd("mmmm d, yyyy"));
    }

    /// <summary>
    /// <c>dddd</c> was matched as <c>dd</c> twice, giving "1313, Aug 13" for "dddd, mmm d".
    /// </summary>
    [Fact]
    public void WeekdayTokens_ProduceWeekdaysRatherThanRepeatedDayNumbers()
    {
        Assert.Equal("Thursday, Aug 13", Printd("dddd, mmm d"));
        Assert.Equal("Thu 13", Printd("ddd d"));
    }

    /// <summary>The short forms that already worked must keep working.</summary>
    [Theory]
    [InlineData("yyyy-mm-dd", "2026-08-13")]
    [InlineData("mm/dd/yy",   "08/13/26")]
    [InlineData("m/d/yyyy",   "8/13/2026")]
    [InlineData("HH:MM:ss",   "14:05:09")]
    [InlineData("h:MM tt",    "2:05 pm")]   // Acrobat writes am and pm in lower case
    public void ShortTokens_AreUnchanged(string format, string expected)
    {
        Assert.Equal(expected, Printd(format));
    }

    /// <summary>
    /// A quote is an ordinary character in Acrobat's <c>printd</c>, and the tokens between quotes are
    /// still written (<c>'d' d</c> is <c>'13' 13</c>, Fixtures/Acrobat round 2); a letter that starts no
    /// token is copied (Acrobat drops a lone <c>y</c>); a backslash copies the one character after it,
    /// which is how a format spells a token letter out.
    /// </summary>
    [Fact]
    public void QuotesAreCharacters_TheirTokensWritten_AndABackslashEscapesOneCharacter()
    {
        Assert.Equal("'13ay' 13 'of' August", Printd("'day' d 'of' mmmm"));
        Assert.Equal("d=13", Printd("\\\\d=d"));
    }

    // ────────────────────────────────────────────────────────────────────
    //  Date parsing culture
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>AFDate_Format</c> parsed with <c>CultureInfo.CurrentCulture</c> while every numeric path
    /// in the same file parsed invariantly, so a batch service moved between regions changed the
    /// dates it stamped. "03/04/2026" under an explicit <c>mm/dd/yyyy</c> is 4 March in every
    /// region, because the document said so.
    /// </summary>
    [Fact]
    public void AFDateFormat_ReadsTheFormatTheDocumentDeclared_NotTheHostsCulture()
    {
        InCommaDecimalCulture(() =>
        {
            var ctx = new PdfJsExecutionContext { EventValue = "03/04/2026" };

            PdfJsEngine.Execute("AFDate_Format('mmm d, yyyy');", ctx);

            Assert.Null(ctx.Error);
            Assert.Equal("Mar 4, 2026", ctx.EventValue);
        });
    }

    /// <summary>
    /// The same call on an en-US host must give the same answer — that agreement is the property
    /// the fix buys, and asserting only one culture could not detect its loss.
    /// </summary>
    [Fact]
    public void AFDateFormat_GivesTheSameAnswerInEveryCulture()
    {
        string Format()
        {
            var ctx = new PdfJsExecutionContext { EventValue = "03/04/2026" };
            PdfJsEngine.Execute("AFDate_Format('mmm d, yyyy');", ctx);
            return ctx.EventValue;
        }

        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("en-US");
            string enUs = Format();
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            string deDe = Format();
            CultureInfo.CurrentCulture = new CultureInfo("en-GB");
            string enGb = Format();

            Assert.Equal(enUs, deDe);
            Assert.Equal(enUs, enGb);
            Assert.Equal("Mar 4, 2026", enUs);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    /// <summary>
    /// A document declaring a day-first format gets a day-first reading — the declared pattern is
    /// tried as an exact parse before the invariant guess, so "03/04/2026" under <c>dd/mm/yyyy</c>
    /// is 3 April rather than the invariant month-first 4 March. That the <b>declared</b> order
    /// wins is the whole point: it is the one piece of ordering information the file carries.
    /// </summary>
    [Fact]
    public void ADayFirstFormat_IsReadDayFirst()
    {
        InCommaDecimalCulture(() =>
        {
            var ctx = new PdfJsExecutionContext { EventValue = "03/04/2026" };
            PdfJsEngine.Execute("AFDate_Format('dd/mm/yyyy');", ctx);
            Assert.Equal("03/04/2026", ctx.EventValue);

            // The same input under a month-first declaration reads the other way round, which is
            // what makes the assertion above about the FORMAT rather than about the parser's
            // default.
            var monthFirst = new PdfJsExecutionContext { EventValue = "03/04/2026" };
            PdfJsEngine.Execute("AFDate_Format('mm/dd/yyyy');", monthFirst);
            Assert.Equal("03/04/2026", monthFirst.EventValue);

        });
    }

    /// <summary>
    /// Single-letter fields fix the order too: <c>d/m/yyyy</c> reads "3/4/2026" as 3 April, and a
    /// leading weekday (<c>ddd</c>) does not throw the order off. Observed through
    /// <c>util.scand</c>, where the month is visible (<c>getMonth()</c> is 0-based: 3 = April).
    /// </summary>
    [Theory]
    [InlineData("d/m/yyyy", "3/4/2026")]
    [InlineData("ddd d/m/yyyy", "Fri 3/4/2026")]
    public void SingleLetterDayFirstFormats_AreReadDayFirst(string format, string text)
    {
        var ctx = new PdfJsExecutionContext();
        PdfJsEngine.Execute(
            $"var d = util.scand('{format}', '{text}'); event.value = d.getMonth() + '-' + d.getDate();", ctx);

        Assert.Equal("3-3", ctx.EventValue);
    }

    /// <summary>
    /// A letter that is no date token is text: the format's tokens still fix the order its fields
    /// are read in, so <c>d/m/yyyy x</c> reads "3/4/2026" as 3 April.
    /// </summary>
    [Fact]
    public void AFormatWithAnUnknownLetter_StillReadsItsFieldsInTheFormatsOrder()
    {
        var ctx = new PdfJsExecutionContext();
        PdfJsEngine.Execute(
            "var d = util.scand('d/m/yyyy x', '3/4/2026'); event.value = d.getMonth() + '-' + d.getDate();", ctx);

        Assert.Equal("3-3", ctx.EventValue);
    }

    /// <summary>
    /// The declared order really is used, observed where it is visible: <c>util.scand</c> hands
    /// the parsed instant back to the script, so the month can be read directly. Under
    /// <b>en-US</b> — where the old <c>CurrentCulture</c> parse read "03/04/2026" month-first —
    /// a <c>dd/mm/yyyy</c> declaration must still give April.
    /// </summary>
    [Fact]
    public void Scand_UsesTheDeclaredFieldOrderRatherThanTheHostsCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("en-US");

            var ctx = new PdfJsExecutionContext();
            // getMonth() is 0-based: 3 = April (day-first), 2 = March (month-first).
            PdfJsEngine.Execute(
                "var d = util.scand('dd/mm/yyyy', '03/04/2026'); event.value = '' + d.getMonth();", ctx);

            Assert.Null(ctx.Error);
            Assert.Equal("3", ctx.EventValue);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    /// <summary><c>util.printd</c> and <c>util.scand</c> reach the same parser.</summary>
    [Fact]
    public void UtilPrintd_IsCultureIndependentToo()
    {
        InCommaDecimalCulture(() =>
        {
            var ctx = new PdfJsExecutionContext();

            PdfJsEngine.Execute("event.value = util.printd('mmmm d, yyyy', '03/04/2026');", ctx);

            Assert.Null(ctx.Error);
            Assert.Equal("March 4, 2026", ctx.EventValue);
        });
    }

    // ────────────────────────────────────────────────────────────────────
    //  Number overflow
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A value past a <c>long</c>, and past the 10²¹ where <c>toFixed</c> turns to exponent form,
    /// formats as its digits and keeps its sign. Set as a number: <c>AFMakeNumber</c>, as Acrobat,
    /// reads the text "1e30" as the digit groups 1 and 30, which is 1.3.
    /// </summary>
    [Theory]
    [InlineData("1e30", "1")]
    [InlineData("-1e30", "-1")]
    public void AVeryLargeValue_FormatsAsItsDigitsWithItsSign(string number, string leading)
    {
        var ctx = new PdfJsExecutionContext();

        PdfJsEngine.Execute($"event.value = {number}; AFNumber_Format(0, 1, 0, 0, '', false);", ctx);

        Assert.Null(ctx.Error);
        Assert.StartsWith(leading, ctx.EventValue!);
        Assert.Equal(31 + (leading.Length - 1), ctx.EventValue!.Length);   // 31 digits, and the sign
        Assert.DoesNotContain("e", ctx.EventValue!);
    }

    /// <summary><c>AFMakeNumber</c> reads the text "1e30" as Acrobat does — 1.3, so 1 at no places.</summary>
    [Fact]
    public void TheText1e30_IsReadAsTheDigitGroupsOneAndThirty()
    {
        var ctx = new PdfJsExecutionContext { EventValue = "1e30" };
        PdfJsEngine.Execute("AFNumber_Format(1, 1, 0, 0, '', false);", ctx);
        Assert.Equal("1.3", ctx.EventValue);
    }

    /// <summary>
    /// Every value rounds half away from zero, as Acrobat's <c>%.0f</c> does (2.5 is 3, Fixtures/Acrobat),
    /// computed here by <see cref="MidpointRounding.AwayFromZero"/> rather than from a table.
    /// </summary>
    [Fact]
    public void EveryValue_RoundsHalfAwayFromZero_AsAcrobatDoes()
    {
        double[] values =
        {
            0, 0.4, 0.5, 1.5, 2.5, -0.5, 7, 999, 1000, 1234.4, 1234.5, 1234.6,
            -1234.6, 1e15, -1e15, 9.2e18,
        };

        foreach (double v in values)
        {
            var ctx = new PdfJsExecutionContext
            {
                EventValue = v.ToString("0.###", CultureInfo.InvariantCulture),
            };
            PdfJsEngine.Execute("AFNumber_Format(0, 1, 0, 0, '', false);", ctx);

            double rounded = Math.Round(Math.Abs(v), MidpointRounding.AwayFromZero);
            string expected = rounded.ToString("0", CultureInfo.InvariantCulture);
            if (v < 0 && rounded != 0) expected = "-" + expected;

            Assert.Equal(expected, ctx.EventValue);
        }
    }

    /// <summary>Grouping and the decimal branch are untouched by the nDec == 0 change.</summary>
    [Theory]
    [InlineData("1234.6",   0, "1,235")]
    [InlineData("1234.4",   0, "1,234")]
    [InlineData("-1234.6",  0, "-1,235")]
    [InlineData("1234.567", 2, "1,234.57")]
    [InlineData("1234567",  0, "1,234,567")]       // a group of three every three digits, not four
    [InlineData("-9876543.21", 1, "-9,876,543.2")]
    public void GroupingAndDecimals_AreUnchanged(string input, int nDec, string expected)
    {
        var ctx = new PdfJsExecutionContext { EventValue = input };

        PdfJsEngine.Execute($"AFNumber_Format({nDec}, 0, 0, 0, '', false);", ctx);

        Assert.Equal(expected, ctx.EventValue);
    }
}
