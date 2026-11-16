// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// An ICC named-colour profile (class <c>nmcl</c>, <c>ncl2</c>) read through
/// <see cref="PdfOperations.ReadNamedColors"/> and <see cref="PdfOperations.TryResolveNamedColor(string, byte[], out IccNamedColor)"/>.
/// The expected values are two independent CMMs' (<see cref="NamedColorProfile"/>), CMMs this repository did not write.
/// </summary>
public class IccNamedColorTests
{
    private static byte Level(double v) => (byte)Math.Round(Math.Max(0, Math.Min(1, v)) * 255);

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void EachColourDecodesAsTheReferenceDecodesIt(int version)
    {
        var colours = PdfOperations.ReadNamedColors(NamedColorProfile.Build(version));
        Assert.Equal(NamedColorProfile.Colours.Length, colours.Count);
        for (int i = 0; i < colours.Count; i++)
        {
            var c = colours[i];
            var (root, _, _, _, cmyk) = NamedColorProfile.Colours[i];
            Assert.Equal("SPOT " + root + " C", c.Name);
            Assert.Equal(root, c.Root);
            Assert.Equal(NamedColorProfile.ReferenceLab[i][0], c.L, 3);
            Assert.Equal(NamedColorProfile.ReferenceLab[i][1], c.A, 3);
            Assert.Equal(NamedColorProfile.ReferenceLab[i][2], c.B, 3);
            Assert.Equal("CMYK", c.DeviceColorSpace);
            Assert.Equal(cmyk.Select(v => v / 100), c.DeviceComponents.Select(v => (double)v), new Near(1e-4));
            // sRGB within one level of the second CMM's, clipped to the gamut as a byte is.
            byte[] want = NamedColorProfile.ReferenceSrgb[i].Select(Level).ToArray();
            byte[] got = { Level(c.Red), Level(c.Green), Level(c.Blue) };
            for (int k = 0; k < 3; k++)
                Assert.True(Math.Abs(want[k] - got[k]) <= 1, $"{c.Name} channel {k}: {got[k]}, the second CMM {want[k]}");
        }
    }

    /// <summary>An XYZ PCS decodes as u1Fixed15 (the reference dumper's reading), to the same colours as the Lab profile.</summary>
    [Fact]
    public void AnXyzPcsDecodesToTheSameColours()
    {
        var lab = PdfOperations.ReadNamedColors(NamedColorProfile.Build());
        var xyz = PdfOperations.ReadNamedColors(NamedColorProfile.Build(xyz: true));
        for (int i = 0; i < lab.Count; i++)
        {
            Assert.True(Math.Abs(lab[i].L - xyz[i].L) < 0.05, $"L {xyz[i].L}");
            Assert.True(Math.Abs(lab[i].A - xyz[i].A) < 0.05, $"a {xyz[i].A}");
            Assert.True(Math.Abs(lab[i].B - xyz[i].B) < 0.05, $"b {xyz[i].B}");
            Assert.True(Math.Abs(Level(lab[i].Red) - Level(xyz[i].Red)) <= 1);
            Assert.True(Math.Abs(Level(lab[i].Blue) - Level(xyz[i].Blue)) <= 1);
        }
    }

    /// <summary>A name is found whole or by its root, exactly or ignoring case; a name it lacks is not, nor in the approximate table's place.</summary>
    [Fact]
    public void ANameIsFoundWholeOrByItsRoot()
    {
        byte[] profile = NamedColorProfile.Build();
        Assert.True(PdfOperations.TryResolveNamedColor("SPOT 286 C", profile, out var whole));
        Assert.Equal("286", whole.Root);
        Assert.True(PdfOperations.TryResolveNamedColor("286", profile, out var root));
        Assert.Equal(whole.Name, root.Name);
        Assert.True(PdfOperations.TryResolveNamedColor("Spot 286 c", profile, out var folded));
        Assert.Equal("286", folded.Root);

        Assert.False(PdfOperations.TryResolveNamedColor("SPOT 300 C", profile, out var missing));
        Assert.Null(missing);
    }

    /// <summary>Bytes that are not a named-colour profile are refused by name, beside the accepted one.</summary>
    [Fact]
    public void AnotherClassOrATruncatedTagIsRefused()
    {
        Assert.NotEmpty(PdfOperations.ReadNamedColors(NamedColorProfile.Build()));

        var wrongClass = Assert.Throws<ArgumentException>(() => PdfOperations.ReadNamedColors(NamedColorProfile.Build(deviceClass: "prtr")));
        Assert.Contains("not a named-colour profile", wrongClass.Message, StringComparison.Ordinal);

        byte[] lying = NamedColorProfile.Build();
        int ncl2 = FindTag(lying, "ncl2");
        lying[ncl2 + 12] = 0x10;   // the colour count's high byte: far more colours than the tag holds
        var truncated = Assert.Throws<ArgumentException>(() => PdfOperations.ReadNamedColors(lying));
        Assert.Contains("more than its", truncated.Message, StringComparison.Ordinal);
    }

    private static int FindTag(byte[] profile, string sig)
    {
        int count = (profile[128] << 24) | (profile[129] << 16) | (profile[130] << 8) | profile[131];
        for (int i = 0; i < count; i++)
        {
            int e = 132 + 12 * i;
            if (System.Text.Encoding.ASCII.GetString(profile, e, 4) == sig)
                return (profile[e + 4] << 24) | (profile[e + 5] << 16) | (profile[e + 6] << 8) | profile[e + 7];
        }
        throw new InvalidOperationException(sig);
    }

    private sealed class Near : System.Collections.Generic.IEqualityComparer<double>
    {
        private readonly double _tolerance;
        public Near(double tolerance) => _tolerance = tolerance;
        public bool Equals(double x, double y) => Math.Abs(x - y) <= _tolerance;
        public int GetHashCode(double obj) => 0;
    }
}
