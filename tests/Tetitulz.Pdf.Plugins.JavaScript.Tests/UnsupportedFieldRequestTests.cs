// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Base.Scripting;

namespace Tetitulz.Pdf.Plugins.JavaScript.Tests;

/// <summary>
/// Field-API requests this engine accepts but does not carry into the document must be
/// <b>recorded</b>, not dropped.
///
/// <para>A calculate script of the shape
/// <c>var f = this.getField('Total'); f.readonly = true; f.value = sum;</c> lands its value in
/// <c>ModifiedFieldValues</c>; the read-only request, which this engine does not carry into the
/// document, is reported on <c>PdfJsEngine.LastUnsupportedRequest</c>, so a caller can tell the two
/// outcomes apart. <c>getArray()</c> reaches the field itself when it has no kids.</para>
/// </summary>
public class UnsupportedFieldRequestTests
{
    private static PdfJsExecutionContext WithTotal()
    {
        var ctx = new PdfJsExecutionContext();
        ctx.FieldValues["Total"] = "0";
        return ctx;
    }

    /// <summary>
    /// The archetype: the value write succeeds and the read-only request is reported, so the two
    /// outcomes are distinguishable.
    /// </summary>
    [Fact]
    public void ReadOnlyRequest_IsRecordedWhileTheValueWriteStillLands()
    {
        var ctx = WithTotal();

        PdfJsEngine.Execute(
            "var f = this.getField('Total'); f.readonly = true; f.value = '42';", ctx);

        Assert.Null(ctx.Error);
        Assert.Equal("42", ctx.ModifiedFieldValues["Total"]);

        Assert.NotNull(PdfJsEngine.LastUnsupportedRequest);
        Assert.Contains("Total.readonly = true", PdfJsEngine.LastUnsupportedRequest!,
            StringComparison.Ordinal);
    }

    [Fact]
    public void HiddenRequest_IsRecorded()
    {
        var ctx = WithTotal();

        PdfJsEngine.Execute("this.getField('Total').hidden = true;", ctx);

        Assert.Contains("Total.hidden = true", PdfJsEngine.LastUnsupportedRequest!,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SetFocus_IsRecorded()
    {
        var ctx = WithTotal();

        PdfJsEngine.Execute("this.getField('Total').setFocus();", ctx);

        Assert.Contains("Total.setFocus()", PdfJsEngine.LastUnsupportedRequest!,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Several dropped requests in one script are all reported, not just the last — a single-slot
    /// overwrite would tell the caller about one of them and hide the rest.
    /// </summary>
    [Fact]
    public void SeveralDroppedRequests_AreAllRecorded()
    {
        var ctx = WithTotal();

        PdfJsEngine.Execute(
            "var f = this.getField('Total'); f.readonly = true; f.hidden = true;", ctx);

        string report = PdfJsEngine.LastUnsupportedRequest!;
        Assert.Contains("readonly", report, StringComparison.Ordinal);
        Assert.Contains("hidden",   report, StringComparison.Ordinal);
    }

    /// <summary>
    /// The report is <b>capped</b> (Wave 17 R12.1): the script decides how often a request is made,
    /// and a loop setting <c>hidden</c> five thousand times appended five thousand entries, each
    /// append copying the whole report. The first 32 (<c>PdfJsEngine.MaxRecordedUnsupportedRequests</c>)
    /// are kept verbatim and the rest are counted into one trailing note — so the report still says
    /// every request happened, in bounded space.
    /// </summary>
    [Fact]
    public void AScriptRepeatingARequest_IsRecordedUpToTheCapAndCountedBeyondIt()
    {
        var ctx = WithTotal();

        PdfJsEngine.Execute(
            "var f = this.getField('Total'); for (var i = 0; i < 5000; i++) f.hidden = true;", ctx);

        Assert.Null(ctx.Error);
        string report = PdfJsEngine.LastUnsupportedRequest!;
        // PdfJsEngine.MaxRecordedUnsupportedRequests, pinned here: the plugin exposes no internals
        // to its tests, and the cap is part of what the report promises a caller.
        const int Cap = 32;
        Assert.Equal(Cap, Occurrences(report, "Total.hidden = true"));
        Assert.Contains("(" + (5000 - Cap) + " further request(s) not recorded)", report,
                        StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(report, "not recorded"));
    }

    /// <summary>…and a script under the cap is reported in full, with no note — the control.</summary>
    [Fact]
    public void AScriptUnderTheCap_IsRecordedInFullWithNoNote()
    {
        var ctx = WithTotal();

        PdfJsEngine.Execute(
            "var f = this.getField('Total'); for (var i = 0; i < 5; i++) f.hidden = true;", ctx);

        string report = PdfJsEngine.LastUnsupportedRequest!;
        Assert.Equal(5, Occurrences(report, "Total.hidden = true"));
        Assert.DoesNotContain("not recorded", report, StringComparison.Ordinal);
    }

    private static int Occurrences(string text, string token)
    {
        int count = 0;
        for (int at = text.IndexOf(token, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(token, at + token.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>
    /// <c>getArray()</c> returned an empty array, so
    /// <c>var k = f.getArray(); for (var i = 0; i &lt; k.length; i++) k[i].value = 0;</c> was a
    /// loop that zeroed nothing. A common viewer returns the field itself when it has no kids.
    /// </summary>
    [Fact]
    public void GetArray_ReachesTheFieldTheScriptNamed()
    {
        var ctx = WithTotal();

        PdfJsEngine.Execute(
            "var k = this.getField('Total').getArray();" +
            "for (var i = 0; i < k.length; i++) { k[i].value = '7'; }", ctx);

        Assert.Null(ctx.Error);
        Assert.True(ctx.ModifiedFieldValues.ContainsKey("Total"),
            "an empty getArray() makes the loop body unreachable, which is how this silently did nothing");
        Assert.Equal("7", ctx.ModifiedFieldValues["Total"]);
    }

    /// <summary>
    /// The slot is per-operation: a script that asks for nothing unsupported must leave it null,
    /// or a caller would attribute the previous document's request to this one.
    /// </summary>
    [Fact]
    public void AScriptThatAsksForNothingUnsupported_LeavesTheSlotNull()
    {
        var first = WithTotal();
        PdfJsEngine.Execute("this.getField('Total').readonly = true;", first);
        Assert.NotNull(PdfJsEngine.LastUnsupportedRequest);

        var second = WithTotal();
        PdfJsEngine.Execute("this.getField('Total').value = '1';", second);

        Assert.Null(PdfJsEngine.LastUnsupportedRequest);
    }
}
