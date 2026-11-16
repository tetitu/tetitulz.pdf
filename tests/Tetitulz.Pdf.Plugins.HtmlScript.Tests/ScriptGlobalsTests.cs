// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Plugins.HtmlScript.Tests;

/// <summary>
/// The DOM members the vendored AngleSharp.Js adds itself — <c>console</c>, <c>atob</c>,
/// <c>DOMParser</c>, the layout properties — which its assembly filter dropped once the code was
/// vendored into an assembly whose name does not start with "AngleSharp": a page's
/// <c>console.log</c> threw and stopped its script. And the layout properties, which answer 0 with
/// no layout, reported once per page.
/// </summary>
[Collection(PluginHostCollection.Name)]
public sealed class ScriptGlobalsTests
{
    private static (string Text, List<string> Reports) Run(string script) =>
        PageScriptTests.Convert("<html><body><p id='a'>BEFORE</p><script>var p = document.getElementById('a');" +
                                script + "</script></body></html>");

    private static bool Threw(List<string> reports) =>
        reports.Any(r => r.Contains("threw", StringComparison.Ordinal));

    private static List<string> LayoutReports(List<string> reports) =>
        reports.Where(r => r.Contains("read layout", StringComparison.Ordinal)).ToList();

    /// <summary>
    /// A <c>console.log</c> does not stop the script: the line after it runs. The control: an
    /// undefined function does stop it, and says so.
    /// </summary>
    [Fact]
    public void AConsoleCallDoesNotStopTheScript()
    {
        var (text, reports) = Run("console.log('x'); p.textContent = 'AFTER';");
        Assert.Contains("AFTER", text, StringComparison.Ordinal);
        Assert.False(Threw(reports), string.Join(" | ", reports));

        var (control, controlReports) = Run("noSuchFunction(); p.textContent = 'AFTER';");
        Assert.DoesNotContain("AFTER", control, StringComparison.Ordinal);
        Assert.True(Threw(controlReports), "the control script did not throw, so this proves nothing");
    }

    /// <summary>The window members the vendored library adds answer: <c>atob</c> decodes, <c>DOMParser</c> parses.</summary>
    [Fact]
    public void TheVendoredWindowMembersAnswer()
    {
        var (text, reports) = Run(
            "p.textContent = atob('WFk=') + new DOMParser().parseFromString('<b>ZW</b>', 'text/html').body.textContent;");
        Assert.Contains("XYZW", text, StringComparison.Ordinal);
        Assert.False(Threw(reports), string.Join(" | ", reports));
    }

    /// <summary>
    /// A layout property answers 0 — a number, not <c>undefined</c> — and the page says, once, which
    /// ones its scripts read, in the order first read: two reads of one name are one entry.
    /// </summary>
    [Fact]
    public void ALayoutReadAnswersZeroAndIsReportedOnce()
    {
        var (text, reports) = Run(
            "p.textContent = 'W' + p.offsetWidth + 'H' + p.clientHeight + 'S' + (p.offsetWidth + 1);");
        Assert.Contains("W0H0S1", text, StringComparison.Ordinal);

        var layout = LayoutReports(reports);
        Assert.Single(layout);
        Assert.Contains("(offsetWidth, clientHeight)", layout[0], StringComparison.Ordinal);
    }

    /// <summary>Every one of the twelve layout getters reports, by its own name.</summary>
    [Theory]
    [InlineData("scrollLeft")]
    [InlineData("scrollTop")]
    [InlineData("scrollWidth")]
    [InlineData("scrollHeight")]
    [InlineData("clientLeft")]
    [InlineData("clientTop")]
    [InlineData("clientWidth")]
    [InlineData("clientHeight")]
    [InlineData("offsetLeft")]
    [InlineData("offsetTop")]
    [InlineData("offsetWidth")]
    [InlineData("offsetHeight")]
    public void EachLayoutGetterReportsItsName(string name)
    {
        var (_, reports) = Run("var v = p." + name + ";");
        Assert.Contains(LayoutReports(reports), r => r.Contains("(" + name + ")", StringComparison.Ordinal));
    }

    /// <summary>
    /// A read inside a handler the event loop runs — a zero-delay timer — is reported too; a page
    /// whose scripts read no layout reports none (the control).
    /// </summary>
    [Fact]
    public void AReadInATimerIsReportedAndNoReadIsNot()
    {
        var (text, reports) = Run("setTimeout(function () { p.textContent = 'T' + p.scrollTop; }, 0);");
        Assert.Contains("T0", text, StringComparison.Ordinal);
        Assert.Single(LayoutReports(reports));

        var (_, quiet) = Run("p.textContent = 'AFTER';");
        Assert.Empty(LayoutReports(quiet));
    }

    /// <summary>
    /// <c>getBoundingClientRect</c> is not there at all — the tag never had it — so calling it
    /// throws, and the throw is reported naming it.
    /// </summary>
    [Fact]
    public void GetBoundingClientRectThrowsAndIsReported()
    {
        var (_, reports) = Run("var r = p.getBoundingClientRect();");
        Assert.Contains(reports, r => r.Contains("threw", StringComparison.Ordinal)
                                   && r.Contains("getBoundingClientRect", StringComparison.Ordinal));
    }
}
