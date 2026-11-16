// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Plugins.HtmlScript.Tests;

/// <summary>
/// A page's scripts run after <c>Core.Scripting.ScriptBuiltinBounds</c>' prelude (the engine hook,
/// VENDORED.md item 6): one call of a string built-in that would build past the script's memory
/// limit is refused, where Jint, asking its limit only when a statement starts, let it build.
/// </summary>
[Collection(PluginHostCollection.Name)]
public sealed class ScriptBuiltinBoundsTests
{
    private const string Page =
        "<html><body><p id='o'>UNTOUCHED</p><script>var big = 'x'.repeat(1000000); " +
        "document.getElementById('o').textContent = 'BUILT' + new Array({0}).fill(big).join('').length;</script></body></html>";

    /// <summary>
    /// Forty megabyte references joined is forty million characters, past the 32 million a 64 MiB
    /// script may build: the script fails, is reported, and the page keeps what it had. Two are built.
    /// </summary>
    [Fact]
    public void AdversarialJoinPastThePageScriptsLimit_IsRefused_AndAJoinUnderItIsBuilt()
    {
        var (refused, reports) = PageScriptTests.Convert(string.Format(Page, 40));
        Assert.Contains("UNTOUCHED", refused, StringComparison.Ordinal);
        Assert.DoesNotContain("BUILT", refused, StringComparison.Ordinal);
        Assert.Contains(reports, r => r.Contains("characters; a script may build", StringComparison.Ordinal));

        var (built, _) = PageScriptTests.Convert(string.Format(Page, 2));
        Assert.Contains("BUILT2000000", built, StringComparison.Ordinal);
    }
}
