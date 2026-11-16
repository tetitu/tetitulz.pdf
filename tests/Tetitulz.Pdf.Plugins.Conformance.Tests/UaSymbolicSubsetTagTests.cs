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
/// UA26 asks whether a font's text can be recovered as Unicode. For a standard-14 font carrying
/// neither <c>/Encoding</c> nor <c>/FontDescriptor</c>, the answer turns on whether the font is
/// <b>symbolic</b> — and that is decided by matching <c>/BaseFont</c> against Symbol and
/// ZapfDingbats <i>after</i> stripping any §9.6.4 subset tag.
/// </summary>
/// <remarks>
/// <para><b>Written because a mutation proved this call site had no test.</b> Neutering
/// <c>Core.Fonts.SubsetTag.Strip</c> reddened eight tests across Core and Base and <b>none</b> in
/// this assembly, though the strip decides a real UA26 verdict here.</para>
///
/// <para>The copy this replaced used <c>IndexOf('+')</c> with no test of the tag's position or its
/// case, so <b>any</b> plus split the name. That got the answer wrong in both directions, which is
/// what the two theories below pin:</para>
/// <list type="bullet">
/// <item><c>/BaseFont /Symbol+Bold</c> — "Symbol" is not six <i>uppercase</i> letters, so this is
/// not a tag and the font really is named "Symbol+Bold". The old code cut it to "Bold", concluded
/// non-symbolic, and <b>missed</b> a genuine UA26 violation.</item>
/// <item><c>/BaseFont /abcdef+Symbol</c> — a lowercase prefix is not a tag either, so this font is
/// named "abcdef+Symbol" and is not the Symbol face. The old code cut it to "Symbol" and
/// <b>invented</b> a UA26 violation against a conformant file.</item>
/// </list>
///
/// <para>The well-formed cases are asserted alongside so neither theory can pass by the checker
/// having simply stopped, or started, reporting UA26 unconditionally.</para>
/// </remarks>
public class UaSymbolicSubsetTagTests
{
    /// <summary>
    /// A genuinely symbolic standard-14 font with no <c>/Encoding</c> is not Unicode-recoverable,
    /// whether or not it carries a well-formed subset tag.
    /// </summary>
    [Theory]
    [InlineData("/Symbol")]
    [InlineData("/ZapfDingbats")]
    [InlineData("/ABCDEF+Symbol")]        // a real tag: strips to Symbol, still symbolic
    [InlineData("/ABCDEF+ZapfDingbats")]
    [InlineData("/Symbol+Bold")]          // NOT a tag - "Symbol" is not uppercase; the missed case
    public void ASymbolicFontWithNoEncodingIsReported(string baseFont)
        => Assert.Contains("UA26", Validate(UntaggedFontPdf(baseFont)));

    /// <summary>
    /// A non-symbolic font's built-in encoding defaults to StandardEncoding, which is
    /// Unicode-recoverable, so no UA26.
    /// </summary>
    [Theory]
    [InlineData("/Helvetica")]
    [InlineData("/ABCDEF+Helvetica")]     // a real tag: strips to Helvetica
    [InlineData("/abcdef+Symbol")]        // NOT a tag - lowercase; the invented case
    [InlineData("/Sym+bolMT")]            // plus is not at position 6
    public void ANonSymbolicFontWithNoEncodingIsNotReported(string baseFont)
        => Assert.DoesNotContain("UA26", Validate(UntaggedFontPdf(baseFont)));

    // ------------------------------------------------------------------

    private static List<string> Validate(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfUaChecker.Validate(ms).Violations.Select(v => v.Code).ToList();
    }

    /// <summary>
    /// One page drawing one simple font that has <b>no</b> <c>/Encoding</c> and <b>no</b>
    /// <c>/FontDescriptor</c> — the only shape that reaches the <c>/BaseFont</c> name test, since a
    /// descriptor would answer from its <c>/Flags</c> instead.
    /// </summary>
    private static byte[] UntaggedFontPdf(string baseFont)
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
