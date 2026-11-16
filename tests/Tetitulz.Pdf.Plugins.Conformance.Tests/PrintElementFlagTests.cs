// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// PDF/X examines an annotation's appearance only when the annotation is a <b>print element</b>
/// (ISO 15930-9 §6.2.2) — decided from its <c>/F</c>, read as a flag word.
/// </summary>
/// <remarks>
/// <para><b>The fixture's only font is inside the annotation's appearance, and it is not
/// embedded</b>, so X04 fires exactly when that appearance is examined — which is exactly when the
/// annotation counts as printing. The page itself draws nothing.</para>
///
/// <para><b>Both directions are pinned.</b> <c>2147483652</c> is Print plus bit 32 in the unsigned
/// spelling: <c>IntValue</c> saturated it to every flag, Hidden among them, and the appearance went
/// unexamined. <c>4.5</c> is not a flag word and reads as 0, Print clear: every old reader
/// truncated it to 4, Print, and examined an appearance that does not print.</para>
/// </remarks>
public class PrintElementFlagTests
{
    [Theory]
    [InlineData("4", true)]            // Print — the control that it fires at all
    [InlineData("2", false)]           // Hidden — the control that it can stay quiet
    [InlineData("2147483652", true)]   // Print plus bit 32: prints
    [InlineData("4.5", false)]         // not a flag word: the default, 0, does not print
    public void AnAnnotationsAppearanceIsExaminedExactlyWhenItPrints(string flags, bool examined)
    {
        bool x04 = PdfXValidator.Validate(new MemoryStream(Pdf(flags))).Violations.Any(v => v.Code == "X04");

        Assert.Equal(examined, x04);
    }

    private static byte[] Pdf(string flags)
    {
        const string appearance = "BT /F1 10 Tf (x) Tj ET";
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Annots [5 0 R] >>",
            "<< /Length 3 >>\nstream\nq Q\nendstream",
            "<< /Type /Annot /Subtype /Stamp /Rect [10 10 60 60] /F " + flags + " /AP << /N 6 0 R >> >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 50 50] /Resources << /Font << /F1 7 0 R >> >> " +
            $"/Length {appearance.Length} >>\nstream\n{appearance}\nendstream",
            "<< /Type /Font /Subtype /TrueType /BaseFont /OnlyInTheAppearance /FontDescriptor 8 0 R >>",
            "<< /Type /FontDescriptor /FontName /OnlyInTheAppearance /Flags 32 >>",
        };

        var bytes = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => bytes.AddRange(Encoding.ASCII.GetBytes(s));
        Emit("%PDF-1.4\n");
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets.Add(bytes.Count);
            Emit($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }
        int xref = bytes.Count;
        var sb = new StringBuilder();
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", bodies.Length + 1);
        foreach (int o in offsets) sb.AppendFormat("{0:D10} 00000 n \n", o);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n", bodies.Length + 1, xref);
        Emit(sb.ToString());
        return bytes.ToArray();
    }
}
