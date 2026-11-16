// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.JavaScript.Tests;

/// <summary>
/// A field commit runs an event for every calculation the form lists, through the real engine. Each
/// event finds its field and gets a context; found by a walk of the field tree and given a context
/// copying every field the document describes, three thousand calculations cost three thousand
/// walks and copies of three thousand fields — outside the scripts' budget, which counts script time.
/// One field index a runner and one context an operation make it linear. The answers stay the
/// scripts' (the potency halves), and a field the caller did not pass is read at the document's value.
/// </summary>
[Collection(PluginHostCollection.Name)]
public sealed class FieldCommitCostTests : IDisposable
{
    private readonly JintJavaScriptPlugin _plugin = new();

    public FieldCommitCostTests() => PdfPluginHost.Instance.Register(_plugin);

    public void Dispose() => PdfPluginHost.Instance.Unregister(_plugin.Name);

    [Fact]
    public void AdversarialManyCalculations_CostLinearly_AndEachRuns()
    {
        const int fields = 3000;
        byte[] pdf = Form(fields, calculate: i => "event.value = " + i + ";");
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var values = new Dictionary<string, string> { ["f0"] = "" };

        long before = GC.GetAllocatedBytesForCurrentThread();
        FieldCommitResult result = PdfFieldScripts.Commit(doc, "f0", "x", values, TimeSpan.FromSeconds(60));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(result.Accepted);
        Assert.Equal(fields - 1, result.OtherChanges.Count);
        Assert.Equal("2999", result.OtherChanges["f2999"]);
        // A walk and a copy of 3 000 fields for each of 3 000 events allocated over a gigabyte.
        Assert.True(allocated < 400L * 1024 * 1024, $"the commit allocated {allocated:N0} bytes");
    }

    /// <summary>
    /// A calculation reads the value of a field the caller did not pass from the document — the
    /// document's own <c>/V</c> — as a script reads every field a viewer has not changed.
    /// </summary>
    [Fact]
    public void AFieldTheCallerDidNotPass_IsReadAtTheDocumentsValue()
    {
        byte[] pdf = Form(3, calculate: i => i == 2 ? "AFSimple_Calculate(\"SUM\", [\"f0\", \"f1\"]);" : null,
                          value: i => i == 1 ? "4" : null);
        using var doc = PdfDocument.Open(new MemoryStream(pdf));

        FieldCommitResult result = PdfFieldScripts.Commit(doc, "f0", "5", new Dictionary<string, string>());

        Assert.True(result.Accepted);
        Assert.Equal("9", result.OtherChanges["f2"]);
    }

    /// <summary>
    /// Text fields f0 … f(n − 1) on one page, every one in <c>/CO</c>; field i carries
    /// <paramref name="calculate"/>(i) as its Calculate script and <paramref name="value"/>(i) as its
    /// <c>/V</c>, where those are not null.
    /// </summary>
    private static byte[] Form(int n, Func<int, string?> calculate, Func<int, string?>? value = null)
    {
        static string Js(string s) => "(" + s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)") + ")";
        var objects = new List<string> { null!, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null! };
        var refs = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            string? script = calculate(i), v = value?.Invoke(i);
            objects.Add("<< /Type /Annot /Subtype /Widget /FT /Tx /T (f" + i + ") /P 3 0 R /Rect [0 0 10 10]" +
                        (v == null ? "" : " /V (" + v + ")") +
                        (script == null ? "" : " /AA << /C << /S /JavaScript /JS " + Js(script) + " >> >>") + " >>");
            refs.Append(objects.Count).Append(" 0 R ");
        }
        objects[0] = "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [" + refs + "] /CO [" + refs + "] >> >>";
        objects[2] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [" + refs + "] >>";

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
