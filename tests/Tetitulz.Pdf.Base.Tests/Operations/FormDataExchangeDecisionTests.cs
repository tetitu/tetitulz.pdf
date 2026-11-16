// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Forms;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfFormDataExchange"/>'s own policy decisions and <see cref="FormData"/>'s
/// last-one-wins rule — found unreached by a mutation sweep.
/// </summary>
/// <remarks>
/// The existing suite asserts the values that survive a round trip, which is blind to every option
/// on the way in and to what happens when a file names one field twice.
/// </remarks>
public class FormDataExchangeDecisionTests
{
    /// <summary>
    /// A file that lists one name twice resolves to the <b>last</b> value, which is how a reader
    /// resolves a repeated key and what the XFDF writer's own tree-building assumes.
    /// </summary>
    [Fact]
    public void ARepeatedFieldNameResolvesToTheLastValue()
    {
        var data = new FormData();
        data.Fields.Add(new FormDataField("name", "FIRST"));
        data.Fields.Add(new FormDataField("name", "LAST"));

        Assert.Equal("LAST", data.ToDictionary()["name"]);
    }

    /// <summary>
    /// The document identifier is recorded by default and omitted when the caller says so — it is
    /// what lets the receiving end tell that data is coming back to the document it left.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheDocumentIdIsRecordedOnlyWhenAsked(bool include)
    {
        using var input = new MemoryStream(FormWithDocumentId());

        FormData data = PdfFormDataExchange.Export(
            input, new FormDataExportOptions { IncludeDocumentId = include });

        Assert.Equal(include ? "0123456789ABCDEF" : null, data.OriginalId);
    }

    /// <summary>
    /// A stream the caller has already read is rewound before the form is read — an export from a
    /// stream left at its end would otherwise find no document at all.
    /// </summary>
    [Fact]
    public void AStreamLeftAtItsEndIsRewoundBeforeExport()
    {
        using var input = new MemoryStream(FormPdfFixtures.ExchangeForm());
        input.Position = input.Length;

        FormData data = PdfFormDataExchange.Export(input);

        Assert.Equal("Ada", data.Fields.Single(f => f.Name == "name").Value);
    }

    /// <summary>
    /// A field the file gave several values to is filled with all of them, and nothing is warned
    /// about: the filler writes the array <c>/V</c> a multi-select list box takes (§12.7.4.4), so
    /// there is no narrowing left to report, for it or for the single-valued field beside it.
    /// </summary>
    [Fact]
    public void AMultiValuedFieldIsFilledWhole_AndNothingIsWarnedAboutOnImport()
    {
        var data = new FormData();
        data.Fields.Add(new FormDataField("name", "Ada"));
        data.Fields.Add(new FormDataField("langs", "en", "he"));

        using var input = new MemoryStream(FormPdfFixtures.ExchangeForm());
        using var output = new MemoryStream();
        FormFillResult result = PdfFormDataExchange.Import(input, data, output);

        Assert.Equal(new[] { "langs", "name" }, result.FilledFields.OrderBy(n => n, StringComparer.Ordinal));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("selections", StringComparison.Ordinal));
        var fields = PdfFormReader.GetFields(new MemoryStream(output.ToArray()));
        Assert.Equal(new[] { "en", "he" }, fields.Single(f => f.FullyQualifiedName == "langs").SelectedValues);
    }

    /// <summary>A one-field form whose trailer carries an <c>/ID</c>.</summary>
    private static byte[] FormWithDocumentId()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [6 0 R] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Fields [6 0 R] /DA (/Helv 12 Tf 0 g) /DR << /Font << /Helv 4 0 R >> >> >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V (Ada) "
            + "/Rect [100 700 300 720] /DA (/Helv 12 Tf 0 g) >>",
        };

        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.7\n");
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int size = bodies.Count + 1;
        int xref = sb.Length;
        sb.Append($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {size} /Root 1 0 R "
                  + "/ID [<0123456789ABCDEF> <FEDCBA9876543210>] >>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
