// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A multiline field's word wider than the box is broken by character wherever it falls: after
/// other text on the line it was started whole on the next line and clipped. The control, the
/// word alone, is broken as before. Every character is drawn, on lines the box can hold.
/// </summary>
public class FormWrapLongWordAfterTextTests
{
    // Twenty W at 12 pt Helvetica (944 units each) are 226 pt; the box is 100 pt wide, so a line
    // holds at most eight.
    private const string Wide = "WWWWWWWWWWWWWWWWWWWW";

    [Theory]
    [InlineData("a " + Wide)]
    [InlineData(Wide)]
    public void AWordWiderThanTheBox_IsBrokenWhereverItFalls(string value)
    {
        string[] shown = ShownStrings(value);

        Assert.All(shown, s => Assert.True(s.Count(ch => ch == 'W') <= 8, $"a line of {s.Length} characters: {s}"));
        Assert.Equal(20, shown.Sum(s => s.Count(ch => ch == 'W')));
        Assert.Equal(value.Contains('a') ? 1 : 0, shown.Sum(s => s.Count(ch => ch == 'a')));
    }

    private static string[] ShownStrings(string value)
    {
        using var input = new MemoryStream(FormPdfFixtures.TextField(rect: "[100 600 200 660]", fieldEntries: "/DA (/Helv 12 Tf 0 g) /Ff 4096"));
        using var output = new MemoryStream();
        var result = PdfFormFiller.Fill(input, output, new Dictionary<string, string> { ["name"] = value }, new FormFillOptions());
        Assert.Contains("name", result.FilledFields);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var field = Assert.IsAssignableFrom<PdfDictionary>(doc.GetObject(FormPdfFixtures.FirstFieldObj));
        var ap = Assert.IsAssignableFrom<PdfDictionary>(doc.ResolveObject(field[PdfName.AP]));
        var normal = Assert.IsType<PdfStream>(doc.ResolveObject(ap[PdfName.N]));
        string content = System.Text.Encoding.Latin1.GetString(normal.GetDecodedData());
        return Regex.Matches(content, @"\(([^)]*)\) Tj").Select(m => m.Groups[1].Value).ToArray();
    }
}
