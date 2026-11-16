// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// The graphics state rules: A56 (transfer functions, every part) and A57 (halftones, PDF/A-2 onwards)
/// — ISO 19005-1 6.2.8, ISO 19005-2/3/4 6.2.5, the published profile's rules of those numbers.
/// </summary>
public class PdfAGraphicsStateRuleTests
{
    private const string Fn = "<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >>";
    private const string Screen = "/Frequency 60 /Angle 45 /SpotFunction /Round";

    private static byte[] Document(string gs)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << /ExtGState << /G0 << " + gs + " >> >> >> >>",
            "<< /Length 6 >>\nstream\n/G0 gs\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((objects.Count + 1).ToString(CultureInfo.InvariantCulture)).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append((objects.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static List<PdfAViolation> Coded(string gs, int part, string code)
    {
        using var ms = new MemoryStream(Document(gs));
        return PdfAValidator.Validate(ms, (PdfAConformancePart)part).Violations.Where(v => v.Code == code).ToList();
    }

    // -------------------------------------------------------------- A56

    [Theory]
    [InlineData("/TR " + Fn, "/G0 /TR")]
    [InlineData("/TR /Identity", "/G0 /TR")]                         // stricter than PDF/X: an identity counts
    [InlineData("/TR2 " + Fn, "/G0 /TR2")]
    [InlineData("/TR2 /Default", null)]
    [InlineData("/LW 1", null)]
    public void A56_ATransferFunction_IsEveryPartsFinding(string gs, string? expected)
    {
        foreach (int part in new[] { 1, 2, 4 })
        {
            var a56 = Coded(gs, part, "A56");
            if (expected == null)
                Assert.Empty(a56);
            else
                Assert.Contains(expected, Assert.Single(a56).Message, StringComparison.Ordinal);
        }
    }

    // -------------------------------------------------------------- A57

    [Theory]
    [InlineData("/HTP [0 0]", 2, "/G0 /HTP")]
    [InlineData("/HTP [0 0]", 4, null)]
    [InlineData("/HTO [0 0]", 4, "/G0 /HTO")]
    [InlineData("/HTO [0 0]", 2, null)]
    [InlineData("/HT << /Type /Halftone /HalftoneType 6 /Width 1 /Height 1 >>", 2, "/HalftoneType 6")]
    [InlineData("/HT << /Type /Halftone /HalftoneType 1 " + Screen + " >>", 2, null)]
    [InlineData("/HT << /Type /Halftone /HalftoneType 1 " + Screen + " /HalftoneName (n) >>", 4, "/HalftoneName")]
    [InlineData("/HT /Default", 2, null)]
    public void A57_AHalftone_IsOneThePartAdmits(string gs, int part, string? expected)
    {
        var a57 = Coded(gs, part, "A57");
        if (expected == null)
            Assert.Empty(a57);
        else
            Assert.Contains(expected, Assert.Single(a57).Message, StringComparison.Ordinal);
        Assert.Empty(Coded(gs, 1, "A57"));
    }

    [Fact]
    public void A57_PdfA2_ATransferFunctionOnlyWhereIso32000RequiresOne()
    {
        string Type5(string red, string cyan)
            => "/HT << /Type /Halftone /HalftoneType 5 /Default << /HalftoneType 1 " + Screen + " >> " +
               "/Red << /HalftoneType 1 " + Screen + red + " >> /Cyan << /HalftoneType 1 " + Screen + cyan + " >> >>";
        Assert.Empty(Coded(Type5(" /TransferFunction /Identity", ""), 2, "A57"));
        Assert.Contains("/Red no /TransferFunction", Assert.Single(Coded(Type5("", ""), 2, "A57")).Message, StringComparison.Ordinal);
        Assert.Contains("/Cyan /TransferFunction", Assert.Single(Coded(Type5(" /TransferFunction /Identity", " /TransferFunction /Identity"), 2, "A57")).Message,
            StringComparison.Ordinal);
        // A top-level halftone has no colorant: no /TransferFunction either.
        Assert.Single(Coded("/HT << /HalftoneType 1 " + Screen + " /TransferFunction /Identity >>", 2, "A57"));
        // PDF/A-4 has no such rule.
        Assert.Empty(Coded(Type5("", ""), 4, "A57"));
    }
}
