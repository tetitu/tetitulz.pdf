// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// The other half of UA26's symbolic test: a font that <b>has</b> a <c>/FontDescriptor</c>, so the
/// answer comes from Table 123's <c>/Flags</c> rather than from the <c>/BaseFont</c> name that
/// <see cref="UaSymbolicSubsetTagTests"/> covers.
/// </summary>
/// <remarks>
/// <para><b>Three implementations of §9.6.6.4's one question gave three different answers.</b> Core's
/// <c>PdfSimpleFont</c> tested bit 3 alone; this checker tested "bit 3 set <i>and</i> bit 6 clear";
/// the Accessibility feature's deep checker tested bit 3 alone but had no standard-14 fallback. The
/// plugin and the feature cannot reference each other, so the drift survived every round that fixed
/// one copy — <c>Core.Fonts.FontFlags</c> is now the shared floor.</para>
///
/// <para><b><c>/Flags 36</c> is the case that separates the two rules, and it had no test.</b>
/// Table 123 forbids Symbolic and Nonsymbolic both being set, so the extra clause is dead code in a
/// conforming file — it can only change the answer for a descriptor that contradicts itself. Core
/// settled that bit 3 wins (<c>SymbolicFlagAgreementTests</c>, where the same flag decides which
/// base encoding applies and therefore what a symbol font <i>draws</i>). Under the old rule this
/// checker called such a font non-symbolic and <b>missed</b> the UA26 violation.</para>
///
/// <para>The conforming spellings are asserted alongside, so neither theory can be satisfied by the
/// checker having simply started, or stopped, reporting UA26 for every font.</para>
/// </remarks>
public class UaSymbolicFlagsTests
{
    /// <summary>Table 123 bit 3 alone — Symbolic, the conforming spelling.</summary>
    private const string SymbolicOnly = "4";

    /// <summary>Table 123 bit 6 alone — Nonsymbolic, the conforming spelling.</summary>
    private const string NonsymbolicOnly = "32";

    /// <summary>
    /// Bits 3 and 6 together, which Table 123 forbids — and the only <c>/Flags</c> value the old
    /// two-clause rule and Core's one-clause rule could disagree about.
    /// </summary>
    private const string BothFlagsSet = "36";

    /// <summary>
    /// A symbolic font's built-in encoding is private to the font program, so its text is not
    /// Unicode-recoverable and UA26 applies. <c>36</c> is the case the old rule got wrong.
    /// </summary>
    [Theory]
    [InlineData(SymbolicOnly)]
    [InlineData(BothFlagsSet)]
    [InlineData("7 0 R")]       // an indirect /Flags holding 4 — the resolve must still happen
    public void ASymbolicFlaggedFontWithNoEncodingIsReported(string flags)
        => Assert.Contains("UA26", Validate(DescriptorFontPdf(flags)));

    /// <summary>
    /// A non-symbolic font falls back to StandardEncoding, which is Unicode-recoverable — the floor
    /// that stops the theory above passing for a checker that reports UA26 unconditionally.
    /// </summary>
    [Fact]
    public void ANonSymbolicFlaggedFontWithNoEncodingIsNotReported()
        => Assert.DoesNotContain("UA26", Validate(DescriptorFontPdf(NonsymbolicOnly)));

    /// <summary>
    /// An explicit <c>/WinAnsiEncoding</c> is Unicode-recoverable whatever the flags say — the flag
    /// decides only what happens when the font names no encoding.
    /// </summary>
    [Theory]
    [InlineData(SymbolicOnly)]
    [InlineData(BothFlagsSet)]
    public void AnExplicitEncodingIsRecoverableWhateverTheFlagsSay(string flags)
        => Assert.DoesNotContain("UA26",
            Validate(DescriptorFontPdf(flags, " /Encoding /WinAnsiEncoding")));

    // ------------------------------------------------------------------

    private static List<string> Validate(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfUaChecker.Validate(ms).Violations.Select(v => v.Code).ToList();
    }

    /// <summary>
    /// One page drawing one simple font that carries a <c>/FontDescriptor</c> with the given
    /// <c>/Flags</c> — the shape that reaches the flag test rather than the <c>/BaseFont</c> name
    /// test. Object 7 holds <c>4</c> so <c>/Flags</c> can be written indirectly.
    /// </summary>
    private static byte[] DescriptorFontPdf(string flags, string encodingEntry = null)
    {
        const string content = "BT /F0 12 Tf 10 10 Td (x) Tj ET";

        var objs = new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R "
                + "/Resources << /Font << /F0 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Custom /FontDescriptor 8 0 R"
                + encodingEntry + " >>\nendobj\n",
            "6 0 obj\n<< /Type /StructTreeRoot >>\nendobj\n",
            "7 0 obj\n4\nendobj\n",
            $"8 0 obj\n<< /Type /FontDescriptor /FontName /Custom /Flags {flags} /ItalicAngle 0 "
                + "/Ascent 700 /Descent -200 /CapHeight 700 /StemV 80 "
                + "/FontBBox [0 -200 1000 700] >>\nendobj\n",
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
