// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Forms;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// What a file shares among many nodes of its form, <see cref="PdfFormReader"/> reads once: a
/// string every widget's <c>/MK</c> and every field's <c>/V</c>, <c>/DV</c> and <c>/TU</c> name,
/// an <c>/Opt</c> array every choice field names, and a <c>/Kids</c> array of widgets or of fields
/// several parents name. Read once per reference, the form of a file of a few hundred kilobytes
/// costs its size times its references — counts a file chooses multiply, so the product is what is
/// bounded. The answers stay the file's (the potency halves).
/// </summary>
public sealed class FormReaderSharedObjectsTests
{
    private const int Chars = 256 * 1024;
    private const int Each = 100;

    [Fact]
    public void AdversarialSharedStringsAndOptions_AreDecodedOnce_NotOncePerReference()
    {
        var objects = new List<string>
        {
            null!,                                                       // 1: the catalog, below
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "(" + new string('A', Chars) + ")",                           // 4: the shared string
            "<< /CA 4 0 R /AC 4 0 R /RC 4 0 R >>",                       // 5: the shared /MK
            "[4 0 R (b)]",                                               // 6: the shared /Opt
        };
        var fields = new List<string>();
        for (int i = 0; i < Each; i++)
        {
            fields.Add(Add(objects, $"<< /Type /Annot /Subtype /Widget /FT /Btn /Ff 65536 /T (b{i}) /Rect [0 0 10 10] /MK 5 0 R >>"));
            fields.Add(Add(objects, $"<< /Type /Annot /Subtype /Widget /FT /Tx /T (t{i}) /Rect [0 0 10 10] /V 4 0 R /DV 4 0 R /TU 4 0 R >>"));
            fields.Add(Add(objects, $"<< /Type /Annot /Subtype /Widget /FT /Ch /T (c{i}) /Rect [0 0 10 10] /Opt 6 0 R >>"));
        }
        objects[0] = "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [" + string.Join(" ", fields) + "] >> >>";
        byte[] pdf = Build(objects);

        long before = GC.GetAllocatedBytesForCurrentThread();
        IReadOnlyList<PdfFormFieldInfo> read = PdfFormReader.GetFields(new MemoryStream(pdf));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // The potency half: every field is there, holding the shared text whole.
        Assert.Equal(3 * Each, read.Count);
        Assert.All(read.Where(f => f.PartialName.StartsWith("b", StringComparison.Ordinal)),
                   f => Assert.Equal(Chars, f.Widgets.Single().NormalCaption.Length));
        Assert.All(read.Where(f => f.PartialName.StartsWith("t", StringComparison.Ordinal)),
                   f => { Assert.Equal(Chars, f.Value.Length); Assert.Equal(Chars, f.DefaultValue.Length); Assert.Equal(Chars, f.AlternateName.Length); });
        Assert.All(read.Where(f => f.PartialName.StartsWith("c", StringComparison.Ordinal)),
                   f => { Assert.Equal(2, f.Options.Count); Assert.Equal(Chars, f.Options[0].DisplayText.Length); });

        // Once per reference this is 700 decodes of half a megabyte each (350 MB); once per string, one.
        Assert.True(allocated < 24L * 1024 * 1024, $"reading the form allocated {allocated:N0} bytes");
    }

    /// <summary>
    /// A <c>/Kids</c> array several fields name — of widgets, or of field nodes written directly in
    /// it — is read once: each widget and each field is the first parent's, as a node has one
    /// parent, and a thousand parents of a hundred kids describe a hundred, not a hundred thousand.
    /// </summary>
    [Fact]
    public void AdversarialSharedKids_AreDescribedOnce_UnderTheirFirstParent()
    {
        const int parents = 1000, kids = 100;
        var widgets = new StringBuilder("[");
        for (int k = 0; k < kids; k++) widgets.Append("<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] >> ");
        var fieldKids = new StringBuilder("[");
        for (int k = 0; k < kids; k++) fieldKids.Append($"<< /FT /Tx /T (k{k}) /V (v) >> ");

        var objects = new List<string>
        {
            null!,
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            widgets.Append(']').ToString(),                              // 4: shared widget kids
            fieldKids.Append(']').ToString(),                            // 5: shared field kids
        };
        var fields = new List<string>();
        for (int p = 0; p < parents; p++)
        {
            fields.Add(Add(objects, $"<< /FT /Btn /T (w{p}) /Kids 4 0 R >>"));
            fields.Add(Add(objects, $"<< /T (g{p}) /Kids 5 0 R >>"));
        }
        objects[0] = "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [" + string.Join(" ", fields) + "] >> >>";

        IReadOnlyList<PdfFormFieldInfo> read = PdfFormReader.GetFields(new MemoryStream(Build(objects)));

        // The widgets are the first button's; the text fields are the first group's.
        var buttons = read.Where(f => f.FullyQualifiedName.StartsWith("w", StringComparison.Ordinal)).ToList();
        Assert.Equal(parents, buttons.Count);
        Assert.Equal(kids, buttons.Single(f => f.FullyQualifiedName == "w0").Widgets.Count);
        Assert.Equal(kids, buttons.Sum(f => f.Widgets.Count));
        Assert.Equal(Enumerable.Range(0, kids).Select(k => "g0.k" + k),
                     read.Where(f => f.FullyQualifiedName.StartsWith("g", StringComparison.Ordinal)).Select(f => f.FullyQualifiedName));
    }

    /// <summary>
    /// An <c>/Opt</c> array, a <c>/V</c> array and an appearance-state dictionary a thousand fields
    /// name are each one list every field holds: listed per field, ten thousand options, selections
    /// and states made thirty million entries. A state dictionary is read for its first 256 names.
    /// </summary>
    [Fact]
    public void AdversarialSharedListsAndStates_AreOneListEveryFieldHolds()
    {
        const int fields = 1000, entries = 10_000;
        var opt = new StringBuilder("[");
        var states = new StringBuilder("<<");
        for (int i = 0; i < entries; i++)
        {
            opt.Append("(o").Append(i).Append(") ");
            states.Append(" /s").Append(i).Append(" 7 0 R");
        }
        var objects = new List<string>
        {
            null!,
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            opt.Append(']').ToString(),                                   // 4: the shared /Opt and /V
            states.Append(" >>").ToString(),                              // 5: the shared /N
            "<< /N 5 0 R >>",                                             // 6: the shared /AP
            "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] /Length 0 >>\nstream\n\nendstream", // 7
        };
        var refs = new List<string>();
        for (int f = 0; f < fields; f++)
        {
            refs.Add(Add(objects, $"<< /Type /Annot /Subtype /Widget /FT /Ch /Ff 2097152 /T (l{f}) /Rect [0 0 10 10] /Opt 4 0 R /V 4 0 R >>"));
            refs.Add(Add(objects, $"<< /Type /Annot /Subtype /Widget /FT /Btn /T (c{f}) /Rect [0 0 10 10] /AP 6 0 R >>"));
        }
        objects[0] = "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [" + string.Join(" ", refs) + "] >> >>";
        byte[] pdf = Build(objects);

        long before = GC.GetAllocatedBytesForCurrentThread();
        IReadOnlyList<PdfFormFieldInfo> read = PdfFormReader.GetFields(new MemoryStream(pdf));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        var lists = read.Where(f => f.PartialName.StartsWith("l", StringComparison.Ordinal)).ToList();
        var boxes = read.Where(f => f.PartialName.StartsWith("c", StringComparison.Ordinal)).ToList();
        Assert.Equal(fields, lists.Count);
        Assert.All(lists, f => { Assert.Equal(entries, f.Options.Count); Assert.Equal(entries, f.SelectedValues.Count); Assert.Equal("o9999", f.Options[entries - 1].DisplayText); });
        Assert.All(boxes, f => Assert.Equal(PdfFormReader.MaxAppearanceStatesPerWidget, f.Widgets.Single().OnStates.Count));
        Assert.True(allocated < 96L * 1024 * 1024, $"reading the form allocated {allocated:N0} bytes");
    }

    private static string Add(List<string> objects, string body)
    {
        objects.Add(body);
        return objects.Count + " 0 R";
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
