// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Forms;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The FDF and XFDF readers name a field the same way when a partial name on its path is empty.
/// </summary>
/// <remarks>
/// <para>A field's fully qualified name joins the partial names on its path with periods
/// (§12.7.4.2). <c>PdfFormField.FullyQualifiedName</c> skips an empty one, and the name a form
/// reader lists is that one. The FDF reader skipped it too. The XFDF reader kept it, so
/// <c>&lt;field name=""&gt;</c> between <c>a</c> and <c>b</c> gave <c>a..b</c>, which no field in
/// the document is called.</para>
/// <para>The dispatcher kept an empty segment too, while the runner, the FDF reader and the field
/// model dropped it. <c>Core.Forms.FieldNames.Qualify</c> is now the one join for all of them.</para>
/// </remarks>
public class FieldNameQualificationTests
{
    [Fact]
    public void FdfAndXfdfSkipAnEmptyPartialNameAlike()
    {
        string xfdf =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<xfdf xmlns=\"http://ns.adobe.com/xfdf/\"><fields>" +
            "<field name=\"a\"><field name=\"\"><field name=\"b\"><value>x</value></field></field></field>" +
            "</fields></xfdf>";
        string fdf =
            "%FDF-1.2\n" +
            "1 0 obj\n<< /FDF << /Fields [<< /T (a) /Kids [<< /T () /Kids [<< /T (b) /V (x) >>] >>] >>] >> >>\nendobj\n" +
            "trailer\n<< /Root 1 0 R >>\n%%EOF\n";

        FormData fromXfdf = PdfFormDataExchange.ReadXfdf(new MemoryStream(Encoding.UTF8.GetBytes(xfdf)));
        FormData fromFdf = PdfFormDataExchange.ReadFdf(new MemoryStream(Encoding.Latin1.GetBytes(fdf)));

        Assert.Equal("a.b", fromFdf.Fields.Single().Name);
        Assert.Equal("a.b", fromXfdf.Fields.Single().Name);
    }

    /// <summary>The control: a path with no empty name joins every segment.</summary>
    [Fact]
    public void XfdfJoinsEveryNonEmptySegment()
    {
        string xfdf =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<xfdf xmlns=\"http://ns.adobe.com/xfdf/\"><fields>" +
            "<field name=\"a\"><field name=\"m\"><field name=\"b\"><value>x</value></field></field></field>" +
            "</fields></xfdf>";

        FormData data = PdfFormDataExchange.ReadXfdf(new MemoryStream(Encoding.UTF8.GetBytes(xfdf)));

        Assert.Equal("a.m.b", data.Fields.Single().Name);
    }
}
