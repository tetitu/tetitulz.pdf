// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A field's fully qualified name joins its ancestors' partial names, so one parent named by a string
/// of a megabyte over ten thousand fields is ten thousand names of a megabyte — some 20 GB from a
/// file of under two. Under <see cref="PdfLimits.MaxQualifiedFieldNameLength"/> each is its
/// fixed-length stand-in, still one name a field, and the field is filled by it.
/// </summary>
[Collection(TestInfrastructure.PdfLimitsCollection.Name)]
public sealed class FieldNameProductTests
{
    private const int NameChars = 1 << 20;
    private const int Fields = 10_000;

    [Fact]
    public void AdversarialLongParentName_OverManyFields_CostsAFixedLengthAName_AndTheNamesStayDistinct()
    {
        byte[] pdf = LongParentForm();

        long before = GC.GetAllocatedBytesForCurrentThread();
        IReadOnlyList<PdfFormFieldInfo> read = PdfFormReader.GetFields(new MemoryStream(pdf));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // The potency half: every field is listed, each under its own name.
        Assert.Equal(Fields, read.Count);
        Assert.Equal(Fields, read.Select(f => f.FullyQualifiedName).Distinct(StringComparer.Ordinal).Count());
        string parent = read[0].FullyQualifiedName.Substring(0, read[0].FullyQualifiedName.LastIndexOf('.'));
        Assert.Equal(new string('N', 64) + "#", parent.Substring(0, 65));
        Assert.All(read, f => Assert.Equal(parent + "." + f.PartialName, f.FullyQualifiedName));

        // Joined in full it is 20 GB; as stand-ins, the file's own size a few times over.
        Assert.True(allocated < 96L * 1024 * 1024, $"reading the form allocated {allocated:N0} bytes");
    }

    [Fact]
    public void AFieldUnderALongName_IsFilledByTheNameTheReaderLists()
    {
        byte[] pdf = LongParentForm(fields: 3, indirect: true);
        string name = PdfFormReader.GetFields(new MemoryStream(pdf)).Single(f => f.PartialName == "k1").FullyQualifiedName;

        var output = new MemoryStream();
        FormFillResult result = PdfFormFiller.Fill(new MemoryStream(pdf), output,
            new Dictionary<string, string> { [name] = "filled" }, new FormFillOptions());

        Assert.Equal(new[] { name }, result.FilledFields);
        IReadOnlyList<PdfFormFieldInfo> after = PdfFormReader.GetFields(new MemoryStream(output.ToArray()));
        Assert.Equal("filled", after.Single(f => f.PartialName == "k1").Value);
        Assert.Equal("v", after.Single(f => f.PartialName == "k0").Value);   // the control: its sibling is untouched
    }

    /// <summary>
    /// One parent named by <see cref="NameChars"/> characters over <paramref name="fields"/> text
    /// fields <c>k0</c>, <c>k1</c>, …: written directly in its <c>/Kids</c> for the large form, as
    /// objects of their own when <paramref name="indirect"/> (a fill replaces a field by its number).
    /// </summary>
    private static byte[] LongParentForm(int fields = Fields, bool indirect = false)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            null!,
        };
        var kids = new StringBuilder("[");
        for (int k = 0; k < fields; k++)
        {
            string field = $"<< /FT /Tx /T (k{k}) /V (v) /Parent 4 0 R /Type /Annot /Subtype /Widget /Rect [0 0 10 10] >>";
            if (indirect) { objects.Add(field); kids.Append(objects.Count).Append(" 0 R "); }
            else kids.Append(field).Append(' ');
        }
        kids.Append(']');
        objects[3] = "<< /T (" + new string('N', NameChars) + ") /Kids " + kids + " >>";
        return Build(objects);
    }

    private static byte[] Build(IReadOnlyList<string> objects)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
