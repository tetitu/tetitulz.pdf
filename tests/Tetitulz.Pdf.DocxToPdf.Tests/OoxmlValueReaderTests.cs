// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using DocumentFormat.OpenXml;
using Tetitulz.Pdf.DocxToPdf.Rendering;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests;

/// <summary>
/// The one reader of an OOXML boolean (<c>ST_OnOff</c> and <c>xsd:boolean</c>) and of an RRGGBB colour, which
/// five and two sites each read their own way.
/// </summary>
public sealed class OoxmlValueReaderTests
{
    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("on", true)]
    [InlineData("On", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("off", false)]
    [InlineData("yes", false)]
    [InlineData(null, false)]
    public void IsTrue_ReadsTheSTOnOffValues(string value, bool expected)
        => Assert.Equal(expected, OoxmlOnOff.IsTrue(value));

    /// <summary>An element present with no value is on; absent it is off; otherwise its value says.</summary>
    [Fact]
    public void ElementIsOn_IsOnWhenPresentWithNoValue()
    {
        static OpenXmlElement Element(string val)
        {
            var e = new OpenXmlUnknownElement("w", "b", "http://schemas.openxmlformats.org/wordprocessingml/2006/main");
            if (val != null) e.SetAttribute(new OpenXmlAttribute("w", "val", "http://schemas.openxmlformats.org/wordprocessingml/2006/main", val));
            return e;
        }

        Assert.False(OoxmlOnOff.ElementIsOn(null));
        Assert.True(OoxmlOnOff.ElementIsOn(Element(null)));
        Assert.True(OoxmlOnOff.ElementIsOn(Element("")));
        Assert.True(OoxmlOnOff.ElementIsOn(Element("on")));
        Assert.False(OoxmlOnOff.ElementIsOn(Element("off")));
    }

    /// <summary>Exactly six ASCII hex digits, in either case; anything else, a white space included, is not a colour.</summary>
    [Theory]
    [InlineData("FF8000", true, 255, 128, 0)]
    [InlineData("ff8000", true, 255, 128, 0)]
    [InlineData("auto", false, 0, 0, 0)]
    [InlineData("FF80001", false, 0, 0, 0)]
    [InlineData("FF800", false, 0, 0, 0)]
    [InlineData(" FF800", false, 0, 0, 0)]
    [InlineData("FF80 0", false, 0, 0, 0)]
    [InlineData("GG8000", false, 0, 0, 0)]
    [InlineData(null, false, 0, 0, 0)]
    public void TryParseHexColor_IsSixAsciiHexDigits(string value, bool ok, int r, int g, int b)
    {
        Assert.Equal(ok, RunRenderer.TryParseHexColor(value, out int rr, out int gg, out int bb));
        Assert.Equal((r, g, b), (rr, gg, bb));
    }
}
