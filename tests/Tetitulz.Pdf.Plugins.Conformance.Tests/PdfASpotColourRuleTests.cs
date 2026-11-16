// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// PDF/A-2 to -4's spot colour rules: A54 (a DeviceN spot colorant has a <c>/Colorants</c> entry) and
/// A55 (Separations of one name agree in tint transform and alternate space, compared as PDF objects)
/// — ISO 19005-2/3/4 6.2.4.4, the published profiles rules 6.2.4.4-1/-2.
/// </summary>
public class PdfASpotColourRuleTests
{
    private const string Tint = "<< /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [1 0 0] /N 1 >>";
    private const string OtherTint = "<< /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [0 1 0] /N 1 >>";

    /// <summary>The page names <paramref name="spaces"/> as its colour spaces; objects 5 on are <paramref name="extras"/>.</summary>
    private static byte[] Document(string spaces, params byte[][] extras)
    {
        var objects = new List<byte[]>
        {
            Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>"),
            Encoding.ASCII.GetBytes("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Encoding.ASCII.GetBytes("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << /ColorSpace << " + spaces + " >> >> >>"),
            Encoding.ASCII.GetBytes("<< /Length 0 >>\nstream\n\nendstream"),
        };
        objects.AddRange(extras);
        using var ms = new MemoryStream();
        void Write(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }
        Write("%PDF-1.7\n");
        var offsets = new List<long>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(ms.Position);
            Write((i + 1).ToString(CultureInfo.InvariantCulture) + " 0 obj\n");
            ms.Write(objects[i], 0, objects[i].Length);
            Write("\nendobj\n");
        }
        long xref = ms.Position;
        Write("xref\n0 " + (objects.Count + 1).ToString(CultureInfo.InvariantCulture) + "\n0000000000 65535 f \n");
        foreach (long o in offsets) Write(o.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Write("trailer\n<< /Size " + (objects.Count + 1).ToString(CultureInfo.InvariantCulture) + " /Root 1 0 R >>\nstartxref\n" +
              xref.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");
        return ms.ToArray();
    }

    private static byte[] Text(string s) => Encoding.ASCII.GetBytes(s);

    private static List<PdfAViolation> Coded(byte[] pdf, int part, string code)
    {
        using var ms = new MemoryStream(pdf);
        return PdfAValidator.Validate(ms, (PdfAConformancePart)part).Violations.Where(v => v.Code == code).ToList();
    }

    // -------------------------------------------------------------- A54

    [Theory]
    [InlineData("/CS0 [/DeviceN [/Spot1 /Spot2] /DeviceRGB " + "{T}]", "/Spot1; /Spot2")]
    [InlineData("/CS0 [/DeviceN [/Spot1 /Spot2] /DeviceRGB {T} << /Colorants << /Spot1 [/Separation /Spot1 /DeviceRGB {T}] >> >>]", "/Spot2")]
    [InlineData("/CS0 [/DeviceN [/Cyan /Black /None] /DeviceCMYK {T}]", null)]
    [InlineData("/CS0 [/DeviceN [/Spot1] /DeviceRGB {T} << /Colorants << /Spot1 [/Separation /Spot1 /DeviceRGB {T}] >> >>]", null)]
    public void A54_ADeviceNSpotColorant_HasAColorantsEntry(string spaces, string? expected)
    {
        byte[] pdf = Document(spaces.Replace("{T}", Tint, StringComparison.Ordinal));
        var a54 = Coded(pdf, 2, "A54");
        if (expected == null)
            Assert.Empty(a54);
        else
            Assert.Contains(expected, Assert.Single(a54).Message, StringComparison.Ordinal);
        Assert.Empty(Coded(pdf, 1, "A54"));
    }

    // -------------------------------------------------------------- A55

    [Fact]
    public void A55_TwoSeparationsOfOneName_MustAgree()
    {
        Assert.Contains("/Red", Assert.Single(Coded(Document(
            $"/CS0 [/Separation /Red /DeviceRGB {Tint}] /CS1 [/Separation /Red /DeviceRGB {OtherTint}]"), 2, "A55")).Message,
            StringComparison.Ordinal);
        Assert.Single(Coded(Document(
            $"/CS0 [/Separation /Red /DeviceRGB {Tint}] /CS1 [/Separation /Red /DeviceCMYK {Tint}]"), 2, "A55"));
        // Equal objects agree, one direct and one indirect.
        Assert.Empty(Coded(Document($"/CS0 [/Separation /Red /DeviceRGB {Tint}] /CS1 [/Separation /Red /DeviceRGB 5 0 R]", Text(Tint)), 2, "A55"));
        // Different names are no concern; nor is PDF/A-1.
        Assert.Empty(Coded(Document($"/CS0 [/Separation /Red /DeviceRGB {Tint}] /CS1 [/Separation /Green /DeviceRGB {OtherTint}]"), 2, "A55"));
        Assert.Empty(Coded(Document($"/CS0 [/Separation /Red /DeviceRGB {Tint}] /CS1 [/Separation /Red /DeviceRGB {OtherTint}]"), 1, "A55"));
    }

    [Fact]
    public void A55_AColorantsEntry_IsComparedToo()
    {
        string spaces = $"/CS0 [/Separation /Spot1 /DeviceRGB {Tint}] " +
                        $"/CS1 [/DeviceN [/Spot1] /DeviceRGB {Tint} << /Colorants << /Spot1 [/Separation /Spot1 /DeviceRGB {OtherTint}] >> >>]";
        Assert.Single(Coded(Document(spaces), 4, "A55"));
    }

    [Fact]
    public void A55_ATintTransformStream_IsComparedByItsDecodedBytes_NotItsEncoding()
    {
        const string program = "{ dup 0 exch }";
        byte[] plain = Text("<< /FunctionType 4 /Domain [0 1] /Range [0 1 0 1] /Length " + program.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + program + "\nendstream");
        byte[] deflated = Deflate(program);
        byte[] compressed = Text("<< /FunctionType 4 /Domain [0 1] /Range [0 1 0 1] /Filter /FlateDecode /Length " + deflated.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n")
            .Concat(deflated).Concat(Text("\nendstream")).ToArray();
        const string differing = "{ dup 1 exch }";
        byte[] other = Text("<< /FunctionType 4 /Domain [0 1] /Range [0 1 0 1] /Length " + differing.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + differing + "\nendstream");

        const string spaces = "/CS0 [/Separation /Red /DeviceGray 5 0 R] /CS1 [/Separation /Red /DeviceGray 6 0 R]";
        Assert.Empty(Coded(Document(spaces, plain, compressed), 2, "A55"));
        Assert.Single(Coded(Document(spaces, plain, other), 2, "A55"));
    }

    private static byte[] Deflate(string text)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            var b = Encoding.ASCII.GetBytes(text);
            z.Write(b, 0, b.Length);
        }
        return ms.ToArray();
    }
}
