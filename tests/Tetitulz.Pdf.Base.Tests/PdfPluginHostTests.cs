// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Plugins.Maintenance;

namespace Tetitulz.Pdf.Base.Tests;

[Collection(TestInfrastructure.PluginHostCollection.Name)]
public class PdfPluginHostTests
{
    // Minimal test plugin implementation
    private class TestPlugin : IPdfPlugin
    {
        public string Name        => "Test.Plugin";
        public string Description => "Unit-test plugin";
        public Version Version    => new Version(1, 0, 0);
        public bool Initialized   { get; private set; }
        public bool ShutDown      { get; private set; }
        public void Initialize()  => Initialized = true;
        public void Shutdown()    => ShutDown    = true;
    }

    private class AnotherPlugin : IPdfPlugin
    {
        public string Name        => "Test.Another";
        public string Description => "Another plugin";
        public Version Version    => new Version(1, 0, 0);
        public void Initialize()  { }
        public void Shutdown()    { }
    }

    private class ProcessingPlugin : IPdfPlugin, IPdfProcessingHook
    {
        public string Name        => "Test.Processing";
        public string Description => "Hook plugin";
        public Version Version    => new Version(1, 0, 0);
        public void Initialize()  { }
        public void Shutdown()    { }
        public byte[] OnBeforeProcess(string op, byte[] b,
            System.Threading.CancellationToken ct = default) => b;
        public byte[] OnAfterProcess(string op, byte[] b,
            System.Threading.CancellationToken ct = default)  => b;
    }

    [Fact]
    public void Register_ValidPlugin_CallsInitialize()
    {
        var host   = PdfPluginHost.Instance;
        var plugin = new TestPlugin();
        try
        {
            host.Register(plugin);
            Assert.True(plugin.Initialized);
        }
        finally { host.Unregister(plugin.Name); }
    }

    /// <summary>
    /// Registering a duplicate name now throws <see cref="DuplicatePluginException"/> rather
    /// than a bare <see cref="InvalidOperationException"/>.
    /// <para>
    /// This is a deliberate contract change, not a relaxed assertion. The old type made
    /// duplicates indistinguishable from a plugin whose <c>Initialize()</c> threw
    /// <c>InvalidOperationException</c> for bad configuration — and <c>RegisterFromAssembly</c>
    /// swallowed exactly that type as "duplicate, skip silently", so real init failures were
    /// dropped with no diagnostic. The assertion below is strictly stronger than the one it
    /// replaces: it pins the specific type and the offending name.
    /// </para>
    /// </summary>
    [Fact]
    public void Register_DuplicateName_Throws()
    {
        var host = PdfPluginHost.Instance;
        var p1   = new TestPlugin();
        var p2   = new TestPlugin();
        try
        {
            host.Register(p1);
            var ex = Assert.Throws<DuplicatePluginException>(() => host.Register(p2));
            Assert.Equal(p1.Name, ex.PluginName);
        }
        finally { host.Unregister(p1.Name); }
    }

    [Fact]
    public void Unregister_ExistingPlugin_ReturnsTrueAndCallsShutdown()
    {
        var host   = PdfPluginHost.Instance;
        var plugin = new TestPlugin();
        host.Register(plugin);
        bool removed = host.Unregister(plugin.Name);
        Assert.True(removed);
        Assert.True(plugin.ShutDown);
    }

    [Fact]
    public void Unregister_NonExistentPlugin_ReturnsFalse()
    {
        var host = PdfPluginHost.Instance;
        bool result = host.Unregister("No.Such.Plugin.XYZ");
        Assert.False(result);
    }

    [Fact]
    public void IsRegistered_AfterRegister_ReturnsTrue()
    {
        var host   = PdfPluginHost.Instance;
        var plugin = new TestPlugin();
        try
        {
            host.Register(plugin);
            Assert.True(host.IsRegistered(plugin.Name));
        }
        finally { host.Unregister(plugin.Name); }
    }

    [Fact]
    public void IsRegistered_AfterUnregister_ReturnsFalse()
    {
        var host   = PdfPluginHost.Instance;
        var plugin = new TestPlugin();
        host.Register(plugin);
        host.Unregister(plugin.Name);
        Assert.False(host.IsRegistered(plugin.Name));
    }

    [Fact]
    public void GetPlugins_ReturnsRegisteredPlugins()
    {
        var host = PdfPluginHost.Instance;
        var p1   = new TestPlugin();
        var p2   = new AnotherPlugin();
        try
        {
            host.Register(p1);
            host.Register(p2);
            var list = host.GetPlugins();
            Assert.Contains(list, p => p.Name == p1.Name);
            Assert.Contains(list, p => p.Name == p2.Name);
        }
        finally
        {
            host.Unregister(p1.Name);
            host.Unregister(p2.Name);
        }
    }

    [Fact]
    public void InvokeBeforeProcess_NoHooks_ReturnsSameBytes()
    {
        var host  = PdfPluginHost.Instance;
        byte[] bytes = new byte[] { 1, 2, 3 };
        var result = host.InvokeBeforeProcess("test", bytes);
        Assert.Equal(bytes, result);
    }

    [Fact]
    public void InvokeAfterProcess_WithHook_PassesThroughBytes()
    {
        var host   = PdfPluginHost.Instance;
        var plugin = new ProcessingPlugin();
        try
        {
            host.Register(plugin);
            byte[] bytes  = new byte[] { 37, 80, 68, 70 }; // %PDF
            byte[] result = host.InvokeAfterProcess("render", bytes);
            Assert.Equal(bytes, result);
        }
        finally { host.Unregister(plugin.Name); }
    }

    /// <summary>
    /// A processing hook that <b>records which of its two methods was called</b>, with what
    /// operation name, and that transforms the bytes rather than returning them.
    /// </summary>
    /// <remarks>
    /// <para><b><see cref="ProcessingPlugin"/> can see none of that.</b> It returns its input from
    /// both methods and records nothing, so every existing assertion about the pair holds equally
    /// whichever method the host calls, and equally if the host discards what the hook returned.
    /// The two invoke methods have identical bodies but for the interface method named, which is a
    /// consolidation waiting to happen — and the one mistake that consolidation can make silently
    /// is calling the wrong one.</para>
    /// <para>The transform is deliberately <b>not</b> an identity and not symmetric: the two
    /// methods append different bytes, so "which hook ran" is readable off the result.</para>
    /// </remarks>
    private class RecordingProcessingPlugin : IPdfPlugin, IPdfProcessingHook
    {
        public const byte BeforeMark = 0xB0;
        public const byte AfterMark  = 0xA0;

        public string Name        => "Test.RecordingProcessing";
        public string Description => "Records which processing hook ran";
        public Version Version    => new Version(1, 0, 0);
        public void Initialize()  { }
        public void Shutdown()    { }

        public List<string> Calls { get; } = new List<string>();

        public byte[] OnBeforeProcess(string op, byte[] b,
            System.Threading.CancellationToken ct = default)
        {
            Calls.Add("before:" + op);
            return Append(b, BeforeMark);
        }

        public byte[] OnAfterProcess(string op, byte[] b,
            System.Threading.CancellationToken ct = default)
        {
            Calls.Add("after:" + op);
            return Append(b, AfterMark);
        }

        private static byte[] Append(byte[] b, byte mark)
        {
            var result = new byte[b.Length + 1];
            Array.Copy(b, result, b.Length);
            result[b.Length] = mark;
            return result;
        }
    }

    /// <summary>A hook that returns null, which the host documents as "keep the input".</summary>
    private class NullReturningProcessingPlugin : IPdfPlugin, IPdfProcessingHook
    {
        public string Name        => "Test.NullProcessing";
        public string Description => "Returns null from both processing hooks";
        public Version Version    => new Version(1, 0, 0);
        public void Initialize()  { }
        public void Shutdown()    { }

        public byte[] OnBeforeProcess(string op, byte[] b,
            System.Threading.CancellationToken ct = default) => null;
        public byte[] OnAfterProcess(string op, byte[] b,
            System.Threading.CancellationToken ct = default) => null;
    }

    [Fact]
    public void InvokeBeforeProcess_CallsOnBeforeProcessOnly_AndReturnsWhatTheHookProduced()
    {
        var host   = PdfPluginHost.Instance;
        var plugin = new RecordingProcessingPlugin();
        try
        {
            host.Register(plugin);

            byte[] result = host.InvokeBeforeProcess("op-b", new byte[] { 1, 2 });

            Assert.Equal(new[] { "before:op-b" }, plugin.Calls);
            Assert.Equal(
                new byte[] { 1, 2, RecordingProcessingPlugin.BeforeMark }, result);
        }
        finally { host.Unregister(plugin.Name); }
    }

    [Fact]
    public void InvokeAfterProcess_CallsOnAfterProcessOnly_AndReturnsWhatTheHookProduced()
    {
        var host   = PdfPluginHost.Instance;
        var plugin = new RecordingProcessingPlugin();
        try
        {
            host.Register(plugin);

            byte[] result = host.InvokeAfterProcess("op-a", new byte[] { 1, 2 });

            Assert.Equal(new[] { "after:op-a" }, plugin.Calls);
            Assert.Equal(
                new byte[] { 1, 2, RecordingProcessingPlugin.AfterMark }, result);
        }
        finally { host.Unregister(plugin.Name); }
    }

    /// <summary>
    /// The <c>?? pdfBytes</c> in each invoke method: a hook returning null keeps the input rather
    /// than threading a null into the next hook and out to the caller.
    /// </summary>
    [Fact]
    public void AProcessingHookReturningNull_KeepsTheInputBytes()
    {
        var host   = PdfPluginHost.Instance;
        var plugin = new NullReturningProcessingPlugin();
        try
        {
            host.Register(plugin);
            byte[] bytes = new byte[] { 9, 8, 7 };

            Assert.Equal(bytes, host.InvokeBeforeProcess("op", bytes));
            Assert.Equal(bytes, host.InvokeAfterProcess("op", bytes));
        }
        finally { host.Unregister(plugin.Name); }
    }

    [Fact]
    public void Register_NullPlugin_Throws()
    {
        var host = PdfPluginHost.Instance;
        Assert.Throws<ArgumentNullException>(() => host.Register(null));
    }

    [Fact]
    public void Unregister_NullName_Throws()
    {
        var host = PdfPluginHost.Instance;
        Assert.Throws<ArgumentNullException>(() => host.Unregister(null));
    }

    [Fact]
    public void Instance_IsSingleton()
    {
        Assert.Same(PdfPluginHost.Instance, PdfPluginHost.Instance);
    }

    // ---------------------------------------------------------------
    //  Plugin wiring through PdfOperations
    // ---------------------------------------------------------------

    /// <summary>Processing plugin that records every invocation.</summary>
    private class RecordingPlugin : IPdfPlugin, IPdfProcessingHook, ITextExtractionHook, IRenderHook
    {
        public string Name        => "Test.Recording";
        public string Description => "Records hook calls";
        public Version Version    => new Version(1, 0, 0);
        public void Initialize()  { }
        public void Shutdown()    { }

        private readonly object _obsLock = new object();
        private readonly List<string> _beforeOps   = new List<string>();
        private readonly List<string> _afterOps    = new List<string>();
        private readonly List<(int Page, string Text)> _textObs    = new List<(int, string)>();
        private readonly List<(int Page, int W, int H)> _renderObs = new List<(int, int, int)>();

        // All snapshot reads return a thread-safe copy so Assert.Contains
        // can iterate while OnPageRendered fires concurrently on other threads.
        public IReadOnlyList<string> BeforeOps   { get { lock (_obsLock) return _beforeOps.ToList(); } }
        public IReadOnlyList<string> AfterOps    { get { lock (_obsLock) return _afterOps.ToList(); } }
        public IReadOnlyList<(int Page, string Text)>  TextObs   { get { lock (_obsLock) return _textObs.ToList(); } }
        public IReadOnlyList<(int Page, int W, int H)> RenderObs { get { lock (_obsLock) return _renderObs.ToList(); } }

        public byte[] OnBeforeProcess(string op, byte[] b,
            System.Threading.CancellationToken ct = default) { lock (_obsLock) _beforeOps.Add(op); return b; }
        public byte[] OnAfterProcess(string op, byte[] b,
            System.Threading.CancellationToken ct = default)  { lock (_obsLock) _afterOps.Add(op);  return b; }
        public void OnTextExtracted(int page, string text,
            System.Threading.CancellationToken ct = default) { lock (_obsLock) _textObs.Add((page, text)); }
        public void OnPageRendered(int page, int w, int h,
            System.Threading.CancellationToken ct = default) { lock (_obsLock) _renderObs.Add((page, w, h)); }
    }

    // Minimal valid PDF (content stream = save/restore — no graphics) for hook tests.
    private static byte[] BuildMinimalPdf()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length; sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length; sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length; sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>\nendobj\n");
        string cs = "q Q";
        int o4 = sb.Length; sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", cs.Length, cs);
        int xref = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.Append("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Fact]
    /// <summary>
    /// The built-in optimizer must produce a document that still opens and still has its page.
    /// "Non-empty output" is satisfied by a truncated file.
    /// </summary>
    public void Optimize_ProducesADocumentThatStillOpens()
    {
        byte[] pdf = BuildMinimalPdf();
        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();

        PdfOptimizer.Optimize(input, output);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        Assert.Single(doc.Pages);
    }

    [Fact]
    /// <summary>
    /// Linearizing must actually linearize: the output carries a <c>/Linearized</c> dictionary,
    /// which the input does not, and still opens with its page intact. The predecessor would have
    /// passed on a plain copy of the input.
    /// </summary>
    public void Linearize_WritesALinearizedDictionary()
    {
        byte[] pdf = BuildMinimalPdf();
        Assert.DoesNotContain("/Linearized", Encoding.Latin1.GetString(pdf));

        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfLinearizer.Linearize(input, output);

        byte[] produced = output.ToArray();
        Assert.Contains("/Linearized", Encoding.Latin1.GetString(produced));

        using var doc = PdfDocument.Open(new MemoryStream(produced));
        Assert.Single(doc.Pages);
    }

    [Fact]
    public void TextExtractionHook_ExtractText_PerPageNotificationFired()
    {
        var host   = PdfPluginHost.Instance;
        var plugin = new RecordingPlugin();
        try
        {
            host.Register(plugin);
            byte[] pdf = BuildMinimalPdf();
            using var input = new MemoryStream(pdf);
            PdfOperations.ExtractText(input);  // 1-page document

            // Hook must be called once per page (page index 0).
            var textSnapshot = plugin.TextObs;
            Assert.Single(textSnapshot);
            Assert.Equal(0, textSnapshot[0].Page);
        }
        finally { host.Unregister(plugin.Name); }
    }

    [Fact]
    public void RenderHook_RenderPageToPng_PageRenderedFired()
    {
        var host   = PdfPluginHost.Instance;
        var plugin = new RecordingPlugin();
        try
        {
            host.Register(plugin);
            byte[] pdf = BuildMinimalPdf();
            using var input = new MemoryStream(pdf);
            PdfOperations.RenderPageToPng(input, 0, 72);

            // Parallel tests may also call RenderPageToPng; assert the hook fired at least
            // once for page 0 with valid dimensions (wiring correctness, not exact count).
            Assert.Contains(plugin.RenderObs, o => o.Page == 0 && o.W > 0 && o.H > 0);
        }
        finally { host.Unregister(plugin.Name); }
    }

    [Fact]
    public void RenderHook_RenderAllPagesToPng_FiredForEachPage()
    {
        var host   = PdfPluginHost.Instance;
        var plugin = new RecordingPlugin();
        try
        {
            host.Register(plugin);
            byte[] pdf = BuildMinimalPdf();
            using var input = new MemoryStream(pdf);
            PdfOperations.RenderAllPagesToPng(input, 72);

            // Minimal PDF has 1 page — at least one observation for page 0 must exist.
            Assert.Contains(plugin.RenderObs, o => o.Page == 0);
        }
        finally { host.Unregister(plugin.Name); }
    }

    [Fact]
    /// <summary>
    /// <c>IOptimizePlugin</c> is a <b>discovery contract, not an auto-dispatch hook</b> (GAPS.md
    /// §5b): a host resolves it explicitly through <c>GetFirstPlugin&lt;T&gt;()</c>, and
    /// <c>PdfOptimizer.Optimize</c> must go on using the built-in implementation even when one is
    /// registered. Auto-dispatching would be circular — the built-in is what such a plugin wraps.
    ///
    /// <para>This replaces a test that was a byte-for-byte duplicate of
    /// <see cref="Optimize_ProducesADocumentThatStillOpens"/> and asserted only that bytes came
    /// back, so it verified nothing about plugins at all despite living in this file. The design
    /// rule it now pins had no test.</para>
    /// </summary>
    public void Optimize_RegisteredOptimizePlugin_IsNotAutoDispatched()
    {
        byte[] pdf = BuildMinimalPdf();

        byte[] withoutPlugin;
        using (var input = new MemoryStream(pdf))
        using (var output = new MemoryStream())
        {
            PdfOptimizer.Optimize(input, output);
            withoutPlugin = output.ToArray();
        }

        var host   = PdfPluginHost.Instance;
        var plugin = new HijackingOptimizePlugin();
        try
        {
            host.Register(plugin);

            using var input  = new MemoryStream(pdf);
            using var output = new MemoryStream();
            PdfOptimizer.Optimize(input, output);

            Assert.False(plugin.WasCalled,
                "IOptimizePlugin must not be auto-dispatched — see GAPS.md §5b");
            Assert.Equal(withoutPlugin, output.ToArray());
        }
        finally { host.Unregister(plugin.Name); }
    }

    /// <summary>An optimize plugin that would be obvious if it ever ran.</summary>
    private sealed class HijackingOptimizePlugin : IPdfPlugin, IOptimizePlugin
    {
        public bool WasCalled { get; private set; }

        public string Name        => "Test.HijackingOptimize";
        public string Description => "Records whether auto-dispatch reached it";
        public Version Version    => new Version(1, 0, 0);
        public void Initialize()  { }
        public void Shutdown()    { }

        public void Optimize(Stream input, Stream output)
        {
            WasCalled = true;
            // Deliberately writes nothing resembling a PDF, so a dispatched call cannot be
            // mistaken for the built-in's output.
            byte[] junk = Encoding.ASCII.GetBytes("HIJACKED");
            output.Write(junk, 0, junk.Length);
        }
    }

    // ---------------------------------------------------------------
    //  CC-3: CancellationToken propagation through hook interfaces
    // ---------------------------------------------------------------

    private class CancellingPlugin : IPdfPlugin, IPdfProcessingHook, ITextExtractionHook, IRenderHook
    {
        public string Name        => "Test.Cancelling";
        public string Description => "Cancellation test plugin";
        public Version Version    => new Version(1, 0, 0);
        public void Initialize()  { }
        public void Shutdown()    { }

        public System.Threading.CancellationToken LastBeforeCt { get; private set; }
        public System.Threading.CancellationToken LastAfterCt  { get; private set; }
        public System.Threading.CancellationToken LastTextCt   { get; private set; }
        public System.Threading.CancellationToken LastRenderCt { get; private set; }

        public byte[] OnBeforeProcess(string op, byte[] b,
            System.Threading.CancellationToken ct = default)
            { LastBeforeCt = ct; return b; }
        public byte[] OnAfterProcess(string op, byte[] b,
            System.Threading.CancellationToken ct = default)
            { LastAfterCt = ct; return b; }
        public void OnTextExtracted(int page, string text,
            System.Threading.CancellationToken ct = default)
            => LastTextCt = ct;
        public void OnPageRendered(int page, int w, int h,
            System.Threading.CancellationToken ct = default)
            => LastRenderCt = ct;
    }

    [Fact]
    public void InvokeBeforeProcess_WithCancellationToken_TokenReachesPlugin()
    {
        var host   = PdfPluginHost.Instance;
        var plugin = new CancellingPlugin();
        using var cts = new System.Threading.CancellationTokenSource();
        try
        {
            host.Register(plugin);
            host.InvokeBeforeProcess("op", new byte[] { 1 }, cts.Token);
            Assert.Equal(cts.Token, plugin.LastBeforeCt);
        }
        finally { host.Unregister(plugin.Name); }
    }

    [Fact]
    public void InvokeAfterProcess_WithCancellationToken_TokenReachesPlugin()
    {
        var host   = PdfPluginHost.Instance;
        var plugin = new CancellingPlugin();
        using var cts = new System.Threading.CancellationTokenSource();
        try
        {
            host.Register(plugin);
            host.InvokeAfterProcess("op", new byte[] { 1 }, cts.Token);
            Assert.Equal(cts.Token, plugin.LastAfterCt);
        }
        finally { host.Unregister(plugin.Name); }
    }

    [Fact]
    public void InvokeTextExtracted_WithCancellationToken_TokenReachesPlugin()
    {
        var host   = PdfPluginHost.Instance;
        var plugin = new CancellingPlugin();
        using var cts = new System.Threading.CancellationTokenSource();
        try
        {
            host.Register(plugin);
            host.InvokeTextExtracted(0, "hello", cts.Token);
            Assert.Equal(cts.Token, plugin.LastTextCt);
        }
        finally { host.Unregister(plugin.Name); }
    }

    [Fact]
    public void InvokePageRendered_WithCancellationToken_TokenReachesPlugin()
    {
        var host   = PdfPluginHost.Instance;
        var plugin = new CancellingPlugin();
        using var cts = new System.Threading.CancellationTokenSource();
        try
        {
            host.Register(plugin);
            host.InvokePageRendered(0, 100, 200, cts.Token);
            Assert.Equal(cts.Token, plugin.LastRenderCt);
        }
        finally { host.Unregister(plugin.Name); }
    }

    [Fact]
    public void InvokeBeforeProcess_AlreadyCancelled_ThrowsBeforeCallingPlugin()
    {
        var host   = PdfPluginHost.Instance;
        var plugin = new CancellingPlugin();
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();
        try
        {
            host.Register(plugin);
            Assert.Throws<System.OperationCanceledException>(
                () => host.InvokeBeforeProcess("op", new byte[] { 1 }, cts.Token));
            // Plugin was never reached (token was already cancelled).
            Assert.Equal(default, plugin.LastBeforeCt);
        }
        finally { host.Unregister(plugin.Name); }
    }
}
