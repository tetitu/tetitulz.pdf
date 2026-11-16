// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Forms;

/// <summary>
/// The XFDF writer writes XML whatever the form holds: a value with a character XML 1.0 forbids made
/// XmlWriter throw an <see cref="ArgumentException"/> no door catches. Such text is made legal; an
/// annotation subtype that is no XML name is refused with a <see cref="PdfException"/>.
/// </summary>
public sealed class XfdfWriterLegalityTests
{
    [Fact]
    public void AdversarialXmlIllegalCharacters_AreReplaced_AndTheFileReadsBack()
    {
        var data = new FormData();
        data.Fields.Add(new FormDataField("a\u0002b", "x\u0001y"));
        using var output = new MemoryStream();
        PdfFormDataExchange.WriteXfdf(data, output);

        var back = PdfFormDataExchange.ReadXfdf(new MemoryStream(output.ToArray()));
        var field = Assert.Single(back.Fields);
        Assert.Equal("x�y", field.Values.Single());

        // Control: ordinary text is written as it is.
        var plain = new FormData();
        plain.Fields.Add(new FormDataField("a", "xy"));
        using var plainOut = new MemoryStream();
        PdfFormDataExchange.WriteXfdf(plain, plainOut);
        Assert.Equal("xy", PdfFormDataExchange.ReadXfdf(new MemoryStream(plainOut.ToArray())).Fields.Single().Values.Single());
    }

    [Fact]
    public void AdversarialSubtypeThatIsNoXmlName_IsRefusedAsAPdfException()
    {
        var data = new FormData();
        data.Annotations.Add(new FormDataAnnotation("3D"));
        var ex = Assert.Throws<PdfException>(() => PdfFormDataExchange.WriteXfdf(data, new MemoryStream()));
        Assert.Contains("3D", ex.Message, StringComparison.Ordinal);

        var ok = new FormData();
        ok.Annotations.Add(new FormDataAnnotation("Text"));
        using var output = new MemoryStream();
        PdfFormDataExchange.WriteXfdf(ok, output);
        Assert.Contains("<text", Encoding.UTF8.GetString(output.ToArray()), StringComparison.Ordinal);
    }
}
