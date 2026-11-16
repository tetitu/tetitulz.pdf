// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Tetitulz.Pdf.Core.Plugins;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// <see cref="IRenderHook"/> and <see cref="ITextExtractionHook"/> are <b>live</b> extension points:
/// register an implementation and the library really calls it. These pin that, because until
/// 2026-08-16 nothing did.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> Both hooks are dispatched by <c>PdfOperations</c> — once for render
/// (<c>InvokePageRendered</c>) and twice for extraction (<c>InvokeTextExtracted</c>) — but the tree
/// has <b>zero implementers</b> of either, so deleting the dispatch call would have broken every
/// host that registered one while the whole suite stayed green.</para>
/// <para>That is not hypothetical. GAPS §8c described all three hook interfaces as unreached dead
/// surface and named them "the cheapest thing to delete"; two of the three are live, and no test
/// would have objected. A dispatch nobody asserts is indistinguishable from a dispatch that was
/// removed.</para>
/// <para><b><see cref="IPdfProcessingHook"/> is deliberately excluded</b>, and asserting it is
/// <i>not</i> called is the point. Its <c>InvokeBeforeProcess</c>/<c>InvokeAfterProcess</c> pair is
/// for a <b>host</b> to call around its own pipeline; the library never does, as the interface's own
/// doc comment states. Testing it here would either fail or, worse, tempt someone to "fix" the
/// library into calling it.</para>
/// <para>The plugin host is a process-wide singleton, so every test unregisters in a
/// <c>finally</c> and asserts only on its own recorded calls.</para>
/// </remarks>
// The host is a process-wide singleton and this class registers on it: without the collection
// another class's ResetForTesting can unregister these hooks mid-test. That is the intermittent
// PluginHostCollection was created for, and this class was outside it.
[Collection(TestInfrastructure.PluginHostCollection.Name)]
public sealed class PluginHookDispatchTests
{
    [Fact]
    public void ARegisteredRenderHook_IsCalledWhenAPageIsRendered()
    {
        var hook = new RecordingRenderHook();
        PdfPluginHost.Instance.Register(hook);
        try
        {
            using var input = new MemoryStream(OnePagePdf(), writable: false);
            PdfOperations.RenderPageToPng(input, 0, 72);
        }
        finally { PdfPluginHost.Instance.Unregister(hook.Name); }

        (int index, int w, int h) = Assert.Single(hook.Pages);
        Assert.Equal(0, index);
        // The real rendered size, not a placeholder: 612x792 at 72 dpi is the page itself.
        Assert.True(w > 0 && h > 0, $"hook was told the page is {w}x{h}");
        Assert.True(h > w, $"expected a portrait page, got {w}x{h}");
    }

    [Fact]
    public void ARegisteredTextExtractionHook_IsCalledWithThePageText()
    {
        var hook = new RecordingTextHook();
        PdfPluginHost.Instance.Register(hook);
        try
        {
            using var input = new MemoryStream(OnePagePdf(), writable: false);
            PdfOperations.ExtractText(input, 1);
        }
        finally { PdfPluginHost.Instance.Unregister(hook.Name); }

        (int index, string text) = Assert.Single(hook.Pages);
        Assert.Equal(0, index);
        // The text itself, not merely that the hook fired -- a hook handed "" would pass a
        // fired-or-not assertion while telling a host nothing.
        Assert.Contains("Hooked", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unregistered hook is not called — the control that stops the two tests above from passing
    /// because something else in the process happens to invoke a hook.
    /// </summary>
    [Fact]
    public void AnUnregisteredRenderHook_IsNotCalled()
    {
        var hook = new RecordingRenderHook();
        PdfPluginHost.Instance.Register(hook);
        PdfPluginHost.Instance.Unregister(hook.Name);

        using var input = new MemoryStream(OnePagePdf(), writable: false);
        PdfOperations.RenderPageToPng(input, 0, 72);

        Assert.Empty(hook.Pages);
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Base for the recording hooks. <b>Records only calls made on the thread that registered it.</b>
    /// </summary>
    /// <remarks>
    /// <c>PdfPluginHost</c> is a process-wide singleton, so while one of these is registered EVERY
    /// concurrently running test that renders or extracts calls it too — and <c>Assert.Single</c>
    /// then asserts something about the whole process. This is the same coupling as an unfiltered
    /// <c>RenderDiagnostics.OnEvent</c> handler, and these tests shipped with it: the render case
    /// failed in the very next whole-solution run and passed alone every time. Same fix, same
    /// reason — filtering removes the coupling, serialising the list would only hide it.
    /// </remarks>
    private abstract class HookBase : IPdfPlugin
    {
        protected readonly int OwnThread = Environment.CurrentManagedThreadId;

        protected bool OnOwnThread => Environment.CurrentManagedThreadId == OwnThread;

        public abstract string Name { get; }
        public string Description => "test hook";
        public Version Version => new Version(1, 0);
        public void Initialize() { }
        public void Shutdown() { }
    }

    private sealed class RecordingRenderHook : HookBase, IRenderHook
    {
        // Unique per instance: the host is a singleton and rejects duplicate names, so a fixed
        // name would make these tests order-dependent.
        public override string Name { get; } = "test-render-hook-" + Guid.NewGuid().ToString("N");

        public List<(int Index, int Width, int Height)> Pages { get; } = new();

        public void OnPageRendered(int pageIndex, int widthPx, int heightPx,
            CancellationToken cancellationToken = default)
        {
            if (!OnOwnThread) return;
            Pages.Add((pageIndex, widthPx, heightPx));
        }
    }

    private sealed class RecordingTextHook : HookBase, ITextExtractionHook
    {
        public override string Name { get; } = "test-text-hook-" + Guid.NewGuid().ToString("N");

        public List<(int Index, string Text)> Pages { get; } = new();

        public void OnTextExtracted(int pageIndex, string text,
            CancellationToken cancellationToken = default)
        {
            if (!OnOwnThread) return;
            Pages.Add((pageIndex, text));
        }
    }

    /// <summary>One portrait page showing the word "Hooked", so both hooks have something real.</summary>
    private static byte[] OnePagePdf()
    {
        const string content = "BT /F1 24 Tf 72 700 Td (Hooked) Tj ET";
        var sb = new System.Text.StringBuilder();
        var offsets = new List<int>();

        sb.Append("%PDF-1.7\n");

        offsets.Add(sb.Length);
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets.Add(sb.Length);
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        offsets.Add(sb.Length);
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                  "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");

        offsets.Add(sb.Length);
        sb.Append("4 0 obj\n<< /Length ").Append(content.Length).Append(" >>\nstream\n")
          .Append(content).Append("\nendstream\nendobj\n");

        offsets.Add(sb.Length);
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(offsets.Count + 1).Append('\n');
        sb.Append("0000000000 65535 f \n");
        foreach (int off in offsets)
            sb.Append(off.ToString("D10", System.Globalization.CultureInfo.InvariantCulture))
              .Append(" 00000 n \n");

        sb.Append("trailer\n<< /Size ").Append(offsets.Count + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }
}
