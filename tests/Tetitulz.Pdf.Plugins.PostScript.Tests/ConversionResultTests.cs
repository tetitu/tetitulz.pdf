// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Text;
using Tetitulz.Pdf.Plugins.PostScript;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// What a fragment conversion reports about its extent.
///
/// <para><see cref="PostScriptConversionResult.BoundingBox"/> is documented as the declared
/// <c>%%BoundingBox</c> when there is one, "else measured from the marks". The measured half was
/// never wired: a fragment with no DSC comment answered null, so a host sizing a form XObject's
/// <c>/BBox</c> from the result had nothing to size it with.</para>
/// </summary>
public sealed class ConversionResultTests
{
    private static PostScriptConversionResult Convert(string ps)
        => PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(ps));

    [Fact]
    public void BoundingBox_WithNoDeclaration_IsTheDrawnExtent()
    {
        var r = Convert("newpath 10 20 moveto 110 20 lineto 110 70 lineto closepath fill");

        Assert.NotNull(r.BoundingBox);
        Assert.Equal(10.0,  r.BoundingBox[0], 6);
        Assert.Equal(20.0,  r.BoundingBox[1], 6);
        Assert.Equal(110.0, r.BoundingBox[2], 6);
        Assert.Equal(70.0,  r.BoundingBox[3], 6);
    }

    /// <summary>A declared box wins over the marks, as the document's own statement of its extent.</summary>
    [Fact]
    public void BoundingBox_Declared_WinsOverTheDrawnExtent()
    {
        var r = Convert("%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: 0 0 300 300\n" +
                        "newpath 10 20 moveto 110 20 lineto 110 70 lineto closepath fill");

        Assert.Equal(new[] { 0.0, 0.0, 300.0, 300.0 }, r.BoundingBox);
    }

    /// <summary>A fragment that draws nothing has no extent to report.</summary>
    [Fact]
    public void BoundingBox_WithNoMarks_IsNull()
    {
        var r = Convert("1 2 add pop");

        Assert.Null(r.BoundingBox);
    }
}
