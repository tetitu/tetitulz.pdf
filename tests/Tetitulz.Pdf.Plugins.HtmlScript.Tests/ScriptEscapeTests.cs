// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace Tetitulz.Pdf.Plugins.HtmlScript.Tests;

/// <summary>
/// An inlined script's <c>&lt;/script</c> is escaped by a scan, where a pattern with no timeout ran
/// over the page's code: every spelling, a lookalike's too, answers as the pattern did.
/// </summary>
public sealed class ScriptEscapeTests
{
    [Theory]
    [InlineData("var a = 1;")]
    [InlineData("document.write('</script>');")]
    [InlineData("'</SCRIPT>' + '</ScRiPt >'")]
    [InlineData("</script</script")]
    [InlineData("</scrip")]
    [InlineData("<//script> </ script> </scripted>")]
    [InlineData("x</script")]
    [InlineData("")]
    [InlineData("</ſcript> </scrıpt>")]   // lookalikes: the pattern folded none, and no HTML parser does
    public void AnInlinedScript_IsEscapedAsThePatternEscapedIt(string code)
    {
        string expected = new Regex("</script", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Replace(code, "<\\/script");

        Assert.Equal(expected, ScriptInliner.Escape(code));
    }
}
