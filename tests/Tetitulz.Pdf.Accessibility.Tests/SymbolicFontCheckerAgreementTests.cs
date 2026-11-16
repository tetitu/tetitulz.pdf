// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Checking;
using Tetitulz.Pdf.Plugins.Conformance;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// §9.6.6.4's symbolic test decides a Unicode-mapping verdict in <b>both</b> checkers — UA20 in
/// <see cref="PdfDeepAccessibilityChecker"/> and UA26 in <c>PdfUaChecker</c> — and they must reach
/// the same answer about the same font.
/// </summary>
/// <remarks>
/// <para><b>They did not, and the assembly boundary is why.</b> The deep checker lives in this
/// feature, the other in the Conformance plugin, and neither may reference the other; Core is their
/// only shared floor, so three rounds each fixed one copy and left the other. Core's
/// <c>PdfSimpleFont</c> tested Table 123 bit 3 alone, the plugin tested "bit 3 <i>and</i> not bit 6",
/// and the deep checker tested bit 3 with no descriptor-less fallback at all.
/// <c>Core.Fonts.FontFlags</c> is now the one implementation, and this asserts the two checkers
/// against each other rather than either against a constant.</para>
///
/// <para><b>The discriminating fixture is a descriptor-less font whose name only <i>starts</i>
/// with a symbolic standard-14 face.</b> The deep checker catches the exact names <c>Symbol</c> and
/// <c>ZapfDingbats</c> in an earlier, exact-match branch, so those never disagreed — but
/// <c>SymbolMT</c> reached the flag test, found no descriptor, and answered "nonsymbolic", while the
/// plugin's prefix test called it symbolic. Both now report.</para>
///
/// <para>The non-symbolic cases are asserted alongside so neither direction can be satisfied by a
/// checker that has simply started reporting on every font.</para>
/// </remarks>
public class SymbolicFontCheckerAgreementTests
{
    /// <summary>
    /// A descriptor-less symbolic face has a built-in encoding private to the font program, so
    /// neither checker can recover Unicode from it and both must say so.
    /// </summary>
    [Theory]
    [InlineData("/Symbol")]         // exact name — both always agreed
    [InlineData("/ZapfDingbats")]
    [InlineData("/SymbolMT")]       // prefix only — the case they disagreed about
    [InlineData("/ABCDEF+Symbol")]  // a real §9.6.4 subset tag, stripped before the test
    public void BothCheckersReportADescriptorlessSymbolicFont(string baseFont)
    {
        byte[] pdf = FontPdf(baseFont);

        Assert.Contains(DeepCodes(pdf), c => c == "UA20");
        Assert.Contains(PluginCodes(pdf), c => c == "UA26");
    }

    /// <summary>
    /// The floor. A non-symbolic standard-14 face falls back to StandardEncoding, which is
    /// Unicode-recoverable — so neither checker reports, and the theory above cannot be met by a
    /// checker that flags everything.
    /// </summary>
    [Theory]
    [InlineData("/Helvetica")]
    [InlineData("/ABCDEF+Times-Roman")]
    public void NeitherCheckerReportsANonSymbolicFont(string baseFont)
    {
        byte[] pdf = FontPdf(baseFont);

        Assert.DoesNotContain(DeepCodes(pdf), c => c == "UA20");
        Assert.DoesNotContain(PluginCodes(pdf), c => c == "UA26");
    }

    // ------------------------------------------------------------------

    private static List<string> DeepCodes(byte[] pdf)
        => PdfDeepAccessibilityChecker.Check(new MemoryStream(pdf, writable: false))
            .Select(i => i.Code).ToList();

    private static List<string> PluginCodes(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        return PdfUaChecker.Validate(ms).Violations.Select(v => v.Code).ToList();
    }

    /// <summary>
    /// One page showing one simple font with <b>no</b> <c>/Encoding</c>, <b>no</b>
    /// <c>/FontDescriptor</c> and <b>no</b> <c>/ToUnicode</c> — the only shape that reaches the
    /// <c>/BaseFont</c> name test in either checker.
    /// </summary>
    private static byte[] FontPdf(string baseFont)
    {
        const string content = "BT /F0 12 Tf 10 10 Td (x) Tj ET";

        var objs = new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R "
                + "/Resources << /Font << /F0 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
            $"5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont {baseFont} >>\nendobj\n",
            "6 0 obj\n<< /Type /StructTreeRoot >>\nendobj\n",
        };
        return Build(objs);
    }

    private static byte[] Build(List<string> objects)
    {
        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));

        Emit("%PDF-1.7\n");
        foreach (var o in objects) { offsets.Add(acc.Count); Emit(o); }

        int xref = acc.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }
}
