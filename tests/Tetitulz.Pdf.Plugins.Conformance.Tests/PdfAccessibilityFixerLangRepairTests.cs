// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Plugins.Conformance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// Checker and fixer must classify the same catalog identically.
///
/// <para><c>PdfUaChecker.CheckLanguage</c> raises UA02 in two cases — <c>/Lang</c> absent, and
/// <c>/Lang</c> present but empty or whitespace, which is what producers emit when a language
/// field was left blank. <see cref="PdfAccessibilityFixer"/>'s condition was
/// <c>catalog[/Lang] == null</c>, so the second case was neither repaired nor warned about: the
/// checker called the document non-conforming, the fixer applied nothing and said nothing, and
/// re-running the checker over the fixer's output reported the same UA02 for ever.</para>
///
/// <para>This is the checker/fixer divergence <c>AcroFormFieldWalk</c> was extracted to end, in
/// the one fix that does not go through it.</para>
/// </summary>
public class PdfAccessibilityFixerLangRepairTests
{
    [Theory]
    [InlineData("()")]
    [InlineData("(   )")]
    public void EmptyLang_IsRepairedSoTheCheckerStopsReportingUa02(string langValue)
    {
        byte[] input = PdfWithLang(langValue);

        // The premise: the checker really does report UA02 for this document.
        Assert.Contains(Validate(input).Violations,
            v => v.Code == "UA02" && v.Message.Contains("present but empty", StringComparison.Ordinal));

        var output = new MemoryStream();
        var result = PdfAccessibilityFixer.Fix(new MemoryStream(input), output);

        Assert.True(result.AnyFixApplied,
            "the fixer reported nothing for a catalog the checker had just failed.");
        Assert.Contains(result.FixesApplied, f => f.Contains("/Lang", StringComparison.Ordinal));
        Assert.DoesNotContain(Validate(output.ToArray()).Violations, v => v.Code == "UA02");
    }

    /// <summary>
    /// The absent-key case, unchanged — without it the rule could be "always rewrite /Lang" and the
    /// theory above would still pass.
    /// </summary>
    [Fact]
    public void MissingLang_IsStillAdded()
    {
        var output = new MemoryStream();
        var result = PdfAccessibilityFixer.Fix(new MemoryStream(PdfWithLang(null)), output);

        Assert.Contains(result.FixesApplied, f => f.Contains("/Lang", StringComparison.Ordinal));
        Assert.DoesNotContain(Validate(output.ToArray()).Violations, v => v.Code == "UA02");
    }

    /// <summary>
    /// The other control: a catalog that already declares a language is left alone. Overwriting a
    /// producer's <c>/Lang</c> with the fixer's default would be a data loss dressed as a fix.
    /// </summary>
    [Fact]
    public void PopulatedLang_IsLeftAlone()
    {
        var output = new MemoryStream();
        var result = PdfAccessibilityFixer.Fix(new MemoryStream(PdfWithLang("(fr-CA)")), output);

        Assert.DoesNotContain(result.FixesApplied, f => f.Contains("/Lang", StringComparison.Ordinal));
        Assert.Contains("fr-CA", Encoding.ASCII.GetString(output.ToArray()), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    //  Fixtures
    // ------------------------------------------------------------------

    private static PdfUaValidationResult Validate(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfUaChecker.Validate(ms);
    }

    private static byte[] PdfWithLang(string langValue)
    {
        var acc = new List<byte>();
        var offsets = new Dictionary<int, int>();
        void Emit(string s) => acc.AddRange(Encoding.ASCII.GetBytes(s));
        void BeginObj(int n) { offsets[n] = acc.Count; Emit($"{n} 0 obj\n"); }

        const string content = "BT /F1 12 Tf 20 100 Td (Hi) Tj ET";
        string lang = langValue == null ? "" : "/Lang " + langValue + " ";

        Emit("%PDF-1.4\n");
        BeginObj(1); Emit($"<< /Type /Catalog /Pages 2 0 R {lang}>>\nendobj\n");
        BeginObj(2); Emit("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        BeginObj(3);
        Emit("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R " +
             "/Resources << >> >>\nendobj\n");
        BeginObj(4);
        Emit($"<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");

        int xref = acc.Count;
        Emit("xref\n0 5\n0000000000 65535 f \n");
        for (int i = 1; i <= 4; i++)
            Emit($"{offsets[i]:D10} 00000 n \n");
        Emit("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        Emit($"startxref\n{xref}\n%%EOF\n");

        return acc.ToArray();
    }
}
