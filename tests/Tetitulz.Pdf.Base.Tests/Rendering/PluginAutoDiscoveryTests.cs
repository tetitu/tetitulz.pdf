// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Plugins;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// <see cref="PdfPluginHost.RegisterFromLoadedAssemblies"/> — "referenced means available".
///
/// <para><b>Why this exists.</b> Referencing a plugin package did nothing on its own.
/// <c>Tetitulz.Pdf.Base</c> cannot reference a plugin (every plugin references <c>Base</c>, so the
/// edge would be a <b>cycle</b>), so
/// the host application had to call <see cref="PdfPluginHost.Register"/> itself. For JPEG 2000
/// the consequence was invisible and format-specific: <c>Core</c>'s <c>JpxDecodeFilter</c> passes
/// the codestream through by design, so with nothing registered the image just did not appear and
/// nothing said why.</para>
/// </summary>
[Collection(TestInfrastructure.PluginHostCollection.Name)]
public sealed class PluginAutoDiscoveryTests : IDisposable
{
    public PluginAutoDiscoveryTests() => PdfPluginHost.Instance.ResetForTesting();
    public void Dispose() => PdfPluginHost.Instance.ResetForTesting();

    /// <summary>
    /// This test assembly references <c>Tetitulz.Pdf.Plugins.PostScript</c> (a marked plugin
    /// assembly) but never registers it, which is exactly the host situation the method is for.
    /// It was <c>Plugins.Jpeg2000</c>'s decode plugin until the old external codec retired on 2026-09-26.
    /// </summary>
    [Fact]
    public void RegisterFromLoadedAssemblies_MakesAReferencedPluginAssemblyReachable()
    {
        const string referenced = "Tetitulz.Pdf.Plugins.PostScript";
        Assert.DoesNotContain(PdfPluginHost.Instance.GetPlugins(), p => p.GetType().Assembly.GetName().Name == referenced);

        int registered = PdfPluginHost.Instance.RegisterFromLoadedAssemblies();

        Assert.True(registered > 0, "no plugins were discovered in the loaded assemblies");
        // The specific assembly, not just a count — a count is satisfied by any plugin at all.
        Assert.Contains(PdfPluginHost.Instance.GetPlugins(), p => p.GetType().Assembly.GetName().Name == referenced);
    }

    /// <summary>Calling it twice must not throw on the duplicate or double-register anything.</summary>
    [Fact]
    public void RegisterFromLoadedAssemblies_IsIdempotent()
    {
        PdfPluginHost.Instance.RegisterFromLoadedAssemblies();
        int afterFirst = PdfPluginHost.Instance.GetPlugins().Count;

        int registeredAgain = PdfPluginHost.Instance.RegisterFromLoadedAssemblies();

        Assert.Equal(0, registeredAgain);
        Assert.Equal(afterFirst, PdfPluginHost.Instance.GetPlugins().Count);
    }

    /// <summary>
    /// Only assemblies carrying <c>PdfPluginAssemblyAttribute</c> are scanned, so the scan must not
    /// sweep up arbitrary types from this test assembly or from Base/Core.
    /// </summary>
    [Fact]
    public void RegisterFromLoadedAssemblies_OnlyRegistersMarkedPluginAssemblies()
    {
        PdfPluginHost.Instance.RegisterFromLoadedAssemblies();

        foreach (var p in PdfPluginHost.Instance.GetPlugins())
        {
            string asmName = p.GetType().Assembly.GetName().Name;
            Assert.StartsWith("Tetitulz.Pdf.Plugins.", asmName, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A JPX image nothing could decode used to fail completely silently. With no plugin
    /// registered the in-house decoder is the only codec, and when it declines the renderer must
    /// say so — the decoder's reason, and that no plugin was there to ask next.
    /// </summary>
    /// <remarks>
    /// This was "no JPEG 2000 codec is available", a configuration report, until the in-house
    /// decoder arrived (Wave 15g): there is always a codec now, so the report names the
    /// codestream's refusal instead. Before that it was "either decodes or reports why not", with
    /// an early return when a decode succeeded, because a host with the native <c>libopenjp2</c>
    /// installed could decode it; that probe is gone too.
    /// </remarks>
    [Fact]
    public void JpxDecode_UndecodableWithNoPlugin_ReportsTheDecoderRefusalAndThatNoPluginIsRegistered()
    {
        // Nothing registered (the constructor reset the host).
        var reported = new List<string>();
        void Sink(RenderDiagnosticKind kind, string detail, Exception ex) =>
            reported.Add(detail ?? string.Empty);

        RenderDiagnostics.OnEvent += Sink;
        SkiaSharp.SKBitmap bitmap;
        try
        {
            // A minimal well-formed J2C header (SOC marker) — enough to reach codec dispatch, and
            // nothing the decoder can read.
            byte[] j2c = { 0xFF, 0x4F, 0xFF, 0x51, 0x00, 0x00, 0x00, 0x00 };
            bitmap = JpxImageDecoder.DecodeToBitmap(j2c, null, null, SkiaSharp.SKAlphaType.Premul, out _);
        }
        finally
        {
            RenderDiagnostics.OnEvent -= Sink;
        }

        Assert.Null(bitmap);
        Assert.NotNull(JpxImageDecoder.LastDecodeError);
        Assert.Contains(reported, d =>
            d.IndexOf("the decoder declined this codestream", StringComparison.Ordinal) >= 0
            && d.IndexOf("no IImageDecodePlugin is registered", StringComparison.Ordinal) >= 0);
    }
}
