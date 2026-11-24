// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests;

/// <summary>
/// An inline equation in a table cell is drawn where it stands in the paragraph, and inside its line — not
/// moved after all the paragraph's text, and not with the line's baseline at its foot, which would hang the
/// equation's depth (a fraction's denominator) below the line and over the row's bottom border.
/// </summary>
public sealed class CellEquationTests
{
    private static string CellWithFraction()
        => Encoding.Latin1.GetString(DocxToPdfConverter.ConvertToBytes(HostileDocxInputTests.RawDocx(
            $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"{HostileDocxInputTests.WordNamespace}\" "
            + "xmlns:m=\"http://schemas.openxmlformats.org/officeDocument/2006/math\"><w:body>"
            + "<w:tbl><w:tblPr><w:tblBorders><w:bottom w:val=\"single\" w:sz=\"4\"/></w:tblBorders></w:tblPr><w:tr><w:tc><w:p>"
            + "<w:r><w:t xml:space=\"preserve\">Let </w:t></w:r>"
            + "<m:oMath><m:f><m:num><m:r><m:t>a</m:t></m:r></m:num><m:den><m:r><m:t>b</m:t></m:r></m:den></m:f></m:oMath>"
            + "<w:r><w:t xml:space=\"preserve\"> be the ratio</w:t></w:r>"
            + "</w:p></w:tc></w:tr></w:tbl><w:p/></w:body></w:document>"),
            new DocxToPdfOptions { CompressStreams = false, UseLegacyRenderer = true }));

    [Fact]
    public void AnInlineEquation_IsDrawnBetweenTheTextAroundIt()
    {
        string pdf = CellWithFraction();

        // The line's text object steps over the equation's slot with a TJ, where the equation stands.
        int before = pdf.IndexOf("(Let ) Tj", StringComparison.Ordinal);
        int after = pdf.IndexOf("( be the ratio) Tj", StringComparison.Ordinal);
        int step = Regex.Match(pdf.Substring(before), @"\[-?[\d.]+\] TJ").Index + before;
        Assert.True(before >= 0 && after > before, "the text was not drawn as two pieces");
        Assert.True(step > before && step < after, "the equation's slot is not between the text around it");
    }

    [Fact]
    public void AnInlineEquationsDepth_StaysAboveTheRowsBottomBorder()
    {
        string pdf = CellWithFraction();

        // The denominator's baseline, and the row's bottom border (the lowest horizontal stroke).
        double denominator = double.Parse(Regex.Match(pdf, @"(-?[\d.]+) (-?[\d.]+) Td\n\(b\) Tj").Groups[2].Value, CultureInfo.InvariantCulture);
        double border = Regex.Matches(pdf, @"(-?[\d.]+) (-?[\d.]+) m\n(-?[\d.]+) (-?[\d.]+) l\nS")
            .Cast<Match>()
            .Where(m => m.Groups[2].Value == m.Groups[4].Value)
            .Min(m => double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));

        Assert.True(denominator > border, $"the denominator's baseline {denominator} is not above the border at {border}");
    }
}
