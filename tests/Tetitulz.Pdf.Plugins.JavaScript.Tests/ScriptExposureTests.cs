// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Scripting;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.JavaScript.Tests;

/// <summary>
/// What a host is exposed to by default: no document script runs until it registers the engine by
/// name — discovery never does — and a script sees a document's attachments only when its context
/// asks, each decoded under a bound.
/// </summary>
[Collection(PluginHostCollection.Name)]
public sealed class ScriptExposureTests
{
    /// <summary>
    /// Discovery passes the engine over (<see cref="PdfPluginExplicitRegistrationAttribute"/>): scanning
    /// its assembly registers nothing, and no script runs; registering it by name runs one (the control).
    /// </summary>
    [Fact]
    public void DiscoveryNeverEnablesTheEngine_OnlyRegisteringItByNameDoes()
    {
        var plugin = new JintJavaScriptPlugin();
        PdfPluginHost.Instance.Unregister(plugin.Name);

        Assert.Equal(0, PdfPluginHost.Instance.RegisterFromAssembly(typeof(JintJavaScriptPlugin).Assembly));
        var ctx = new PdfJsExecutionContext();
        Assert.False(PluginJavaScriptRunner.TryExecute("event.value = 'ran';", ctx));
        Assert.NotEqual("ran", ctx.EventValue);

        PdfPluginHost.Instance.Register(plugin);
        try
        {
            Assert.True(PluginJavaScriptRunner.TryExecute("event.value = 'ran';", ctx));
            Assert.Equal("ran", ctx.EventValue);
        }
        finally { PdfPluginHost.Instance.Unregister(plugin.Name); }
    }

    /// <summary>
    /// An attachment whose data inflates past the bound a script is handed (1 MiB) is listed and not
    /// decoded for it — a deflate bomb of a few kilobytes costs no more than the bound — while a small
    /// one beside it is read whole (the control).
    /// </summary>
    [Fact]
    public void AdversarialInflatingAttachment_IsNotHandedToAScript_AndASmallOneIs()
    {
        byte[] pdf = TwoAttachments(small: "hello", bombBytes: 3 * 1024 * 1024);
        var plugin = new JintJavaScriptPlugin();
        PdfPluginHost.Instance.Register(plugin);
        try
        {
            var ctx = PdfOperations.ExecuteOpenScripts(new MemoryStream(pdf), new PdfJsExecutionContext { ExposeAttachments = true });

            Assert.Null(ctx.Error);
            Assert.Equal(2, ctx.Attachments.Count);
            Assert.Equal("hello", Encoding.ASCII.GetString(ctx.AttachmentContents["small.txt"]));
            Assert.False(ctx.AttachmentContents.ContainsKey("bomb.bin"));
            Assert.Equal("hello|null|2", ctx.EventValue);
        }
        finally { PdfPluginHost.Instance.Unregister(plugin.Name); }
    }

    /// <summary>
    /// Many attachments that each inflate past the bound share the document's decoding budget: a
    /// decode that does not fit is charged the bound it ran to, so fifty bombs cost four bounds, not
    /// fifty — and an attachment after the budget is spent is listed and not decoded. Fifty
    /// attachments decoded the bound each, fifty megabytes for a file of kilobytes.
    /// </summary>
    [Fact]
    public void AdversarialManyInflatingAttachments_ShareOneDecodingBudget()
    {
        var attachments = new List<(string Name, byte[] Data, bool Deflated)>();
        byte[] bomb = Deflate(new byte[2 * 1024 * 1024]);
        for (int i = 0; i < 50; i++) attachments.Add(($"bomb{i:D2}.bin", bomb, true));
        attachments.Add(("zz-small.txt", Encoding.ASCII.GetBytes("hello"), false));
        byte[] pdf = Attachments(attachments);

        var ctx = Run(pdf, out long allocated);

        Assert.Equal(51, ctx.Attachments.Count);
        Assert.Empty(ctx.AttachmentContents);                    // the bombs spent the budget first
        Assert.True(allocated < 32L * 1024 * 1024, $"handing the attachments over allocated {allocated:N0} bytes");
    }

    /// <summary>
    /// Every attachment's contents are found in one walk of the document: three thousand empty
    /// attachments, each found by a walk of the whole listing, cost three thousand walks of three
    /// thousand entries. The control: each one is handed over, empty.
    /// </summary>
    [Fact]
    public void AdversarialManyAttachments_AreFoundInOneWalk()
    {
        var attachments = new List<(string Name, byte[] Data, bool Deflated)>();
        for (int i = 0; i < 3000; i++) attachments.Add(($"f{i:D4}.txt", new byte[0], false));
        byte[] pdf = Attachments(attachments);

        var ctx = Run(pdf, out long allocated);

        Assert.Equal(3000, ctx.Attachments.Count);
        Assert.Equal(3000, ctx.AttachmentContents.Count);
        Assert.True(allocated < 64L * 1024 * 1024, $"handing the attachments over allocated {allocated:N0} bytes");
    }

    private static PdfJsExecutionContext Run(byte[] pdf, out long allocated)
    {
        var plugin = new JintJavaScriptPlugin();
        PdfPluginHost.Instance.Register(plugin);
        try
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var ctx = PdfOperations.ExecuteOpenScripts(new MemoryStream(pdf), new PdfJsExecutionContext { ExposeAttachments = true });
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Null(ctx.Error);
            Assert.Equal("ran", ctx.EventValue);
            return ctx;
        }
        finally { PdfPluginHost.Instance.Unregister(plugin.Name); }
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
            z.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    /// <summary>A one-page document whose name tree lists <paramref name="attachments"/> in order, and whose open action sets "ran".</summary>
    private static byte[] Attachments(IReadOnlyList<(string Name, byte[] Data, bool Deflated)> attachments)
    {
        var parts = new List<byte[]>();
        var offsets = new List<int>();
        int length = 0;
        void Text(string s) { byte[] b = Encoding.ASCII.GetBytes(s); parts.Add(b); length += b.Length; }
        void Bytes(byte[] b) { parts.Add(b); length += b.Length; }
        void Begin() { offsets.Add(length); Text(offsets.Count + " 0 obj\n"); }

        var names = new StringBuilder();
        for (int i = 0; i < attachments.Count; i++) names.Append('(').Append(attachments[i].Name).Append(") ").Append(5 + 2 * i).Append(" 0 R ");
        Text("%PDF-1.7\n");
        Begin(); Text("<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [" + names + "] >> >> /OpenAction 4 0 R >>\nendobj\n");
        Begin(); Text("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Begin(); Text("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>\nendobj\n");
        Begin(); Text("<< /S /JavaScript /JS (event.value = 'ran';) >>\nendobj\n");
        for (int i = 0; i < attachments.Count; i++)
        {
            Begin(); Text("<< /Type /Filespec /F (" + attachments[i].Name + ") /EF << /F " + (6 + 2 * i) + " 0 R >> >>\nendobj\n");
            Begin();
            Text("<< /Type /EmbeddedFile " + (attachments[i].Deflated ? "/Filter /FlateDecode " : "") + "/Length " + attachments[i].Data.Length + " >>\nstream\n");
            Bytes(attachments[i].Data);
            Text("\nendstream\nendobj\n");
        }
        int xref = length;
        var sb = new StringBuilder("xref\n0 " + (offsets.Count + 1) + "\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(offsets.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        Text(sb.ToString());
        return parts.SelectMany(p => p).ToArray();
    }

    /// <summary>A one-page document whose open action reads both attachments; one deflates to <paramref name="bombBytes"/> zeros.</summary>
    private static byte[] TwoAttachments(string small, int bombBytes)
    {
        byte[] bomb;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
                z.Write(new byte[bombBytes], 0, bombBytes);
            bomb = ms.ToArray();
        }
        const string script = "var s = this.getDataObjectContents('small.txt'), b = this.getDataObjectContents('bomb.bin'); " +
                              "event.value = util.stringFromStream(s) + '|' + b + '|' + this.dataObjects.length;";

        var parts = new List<byte[]>();
        void Text(string s) => parts.Add(Encoding.ASCII.GetBytes(s));
        var offsets = new List<int>();
        int length() => parts.Sum(p => p.Length);
        Text("%PDF-1.7\n");
        string[] objects =
        {
            "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(bomb.bin) 4 0 R (small.txt) 6 0 R] >> >> /OpenAction 8 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
            "<< /Type /Filespec /F (bomb.bin) /EF << /F 5 0 R >> >>",
            null,
            "<< /Type /Filespec /F (small.txt) /EF << /F 7 0 R >> >>",
            "<< /Type /EmbeddedFile /Length " + small.Length + " >>\nstream\n" + small + "\nendstream",
            "<< /S /JavaScript /JS (" + script.Replace("(", "\\(").Replace(")", "\\)") + ") >>",
        };
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(length());
            Text((i + 1) + " 0 obj\n");
            if (objects[i] != null) Text(objects[i]);
            else
            {
                Text("<< /Type /EmbeddedFile /Filter /FlateDecode /Length " + bomb.Length + " >>\nstream\n");
                parts.Add(bomb);
                Text("\nendstream");
            }
            Text("\nendobj\n");
        }
        int xref = length();
        var sb = new StringBuilder("xref\n0 " + (objects.Length + 1) + "\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        Text(sb.ToString());
        return parts.SelectMany(p => p).ToArray();
    }
}
