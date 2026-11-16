// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Core.Tests.Plugins;

/// <summary>
/// Adversarial-input guard: one <see cref="IImageDecodePlugin"/> returning a null or
/// null-containing <see cref="IImageDecodePlugin.SupportedFilters"/> must not poison the
/// process-wide registry.
///
/// <para><c>RebuildDecodeCache</c> enumerated <c>dp.SupportedFilters</c> — arbitrary plugin code
/// — from inside the registry lock, in <c>Register</c>'s commit phase and therefore AFTER
/// <c>_plugins.Add(plugin)</c>. A null list threw <see cref="NullReferenceException"/>; a null
/// entry threw <see cref="ArgumentNullException"/> out of <c>ContainsKey(null)</c>. Either way
/// the exception escaped <c>Register</c> with the broken plugin already committed, so every
/// LATER <c>Register</c> and <c>Unregister</c> in the process threw from the same rebuild and no
/// further plugin could ever be registered. <c>RegisterFromAssembly</c> catches, records and
/// continues — which is exactly how the poison gets in unnoticed.</para>
/// </summary>
[Collection(nameof(PluginHostCollection))]
public sealed class PdfPluginHostDecodeFilterValidationTests : IDisposable
{
    public PdfPluginHostDecodeFilterValidationTests() => PdfPluginHost.Instance.ResetForTesting();
    public void Dispose() => PdfPluginHost.Instance.ResetForTesting();

    // ---------------------------------------------------------------- test doubles

    private class Decoder : IImageDecodePlugin
    {
        private readonly IReadOnlyList<string> _filters;
        public Decoder(string name, IReadOnlyList<string> filters)
        {
            Name = name;
            _filters = filters;
        }
        public string Name { get; }
        public string Description => "test";
        public Version Version => new(1, 0, 0);
        public virtual IReadOnlyList<string> SupportedFilters => _filters;
        public void Initialize() { }
        public void Shutdown() { }
        public bool TryDecode(string filterName, byte[] data, PdfDictionary parameters,
            out int width, out int height, out int components, out byte[] pixels)
        {
            width = height = components = 0;
            pixels = Array.Empty<byte>();
            return false;
        }
    }

    /// <summary>
    /// A plugin whose filter list is computed rather than stored, and whose computation fails —
    /// the config-file-is-missing case. Returning null and throwing are equally likely from the
    /// same implementation, so the guard has to cover both.
    /// </summary>
    private sealed class ThrowingFilterList : Decoder
    {
        public ThrowingFilterList() : base("Test.ThrowingFilters", Array.Empty<string>()) { }
        public override IReadOnlyList<string> SupportedFilters =>
            throw new InvalidOperationException("config unavailable");
    }

    /// <summary>A plugin whose list is usable at registration and unusable afterwards.</summary>
    private sealed class MutableFilterList : IImageDecodePlugin
    {
        public IReadOnlyList<string> Filters = new[] { "JBIG2Decode" };
        public string Name => "Test.Mutable";
        public string Description => "test";
        public Version Version => new(1, 0, 0);
        public IReadOnlyList<string> SupportedFilters => Filters;
        public void Initialize() { }
        public void Shutdown() { }
        public bool TryDecode(string filterName, byte[] data, PdfDictionary parameters,
            out int width, out int height, out int components, out byte[] pixels)
        {
            width = height = components = 0;
            pixels = Array.Empty<byte>();
            return false;
        }
    }

    // ---------------------------------------------------------------- tests

    [Fact]
    public void Register_DecodePluginWithNullSupportedFilters_IsRefusedWithATypedException()
    {
        var host = PdfPluginHost.Instance;

        var ex = Assert.Throws<ArgumentException>(
            () => host.Register(new Decoder("Test.NullFilters", null!)));

        Assert.Contains("Test.NullFilters", ex.Message);
        Assert.Contains("SupportedFilters", ex.Message);
    }

    [Fact]
    public void Register_DecodePluginWithANullFilterName_IsRefusedWithATypedException()
    {
        var host = PdfPluginHost.Instance;

        var ex = Assert.Throws<ArgumentException>(
            () => host.Register(new Decoder("Test.NullEntry", new[] { "JPXDecode", null! })));

        Assert.Contains("Test.NullEntry", ex.Message);
        Assert.Contains("index 1", ex.Message);
    }

    /// <summary>
    /// A filter name of spaces is no PDF filter name either, and is refused like an empty one.
    /// </summary>
    [Fact]
    public void Register_DecodePluginWithAWhitespaceFilterName_IsRefusedWithATypedException()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => PdfPluginHost.Instance.Register(new Decoder("Test.BlankEntry", new[] { "  " })));

        Assert.Contains("index 0", ex.Message);
        Assert.False(PdfPluginHost.Instance.IsRegistered("Test.BlankEntry"));
    }

    /// <summary>
    /// The getter may THROW as readily as it may return null, and the same implementation produces
    /// both. Without a catch around the single read the plugin's own exception escapes
    /// <c>Register</c> — a caller catching the documented <see cref="ArgumentException"/> does not
    /// catch it, and nothing in the message says which plugin was responsible.
    /// </summary>
    [Fact]
    public void Register_DecodePluginWhoseSupportedFiltersThrows_IsRefusedWithATypedException()
    {
        var host = PdfPluginHost.Instance;

        var ex = Assert.Throws<ArgumentException>(() => host.Register(new ThrowingFilterList()));

        Assert.Contains("Test.ThrowingFilters", ex.Message);
        Assert.Contains("config unavailable", ex.Message);
        Assert.False(host.IsRegistered("Test.ThrowingFilters"));

        // …and the registry is still usable, which is the whole point of refusing up front.
        host.Register(new Decoder("Test.Good", new[] { "JPXDecode" }));
        Assert.True(host.IsRegistered("Test.Good"));
    }

    /// <summary>
    /// A plugin whose list becomes unusable AFTER it was registered must not break the next
    /// <c>Register</c> either — that is the path the up-front validation cannot cover.
    /// </summary>
    /// <remarks>
    /// The host reads <c>SupportedFilters</c> exactly once, in <c>Register</c>, and serves the
    /// snapshot from then on; <c>RebuildDecodeCache</c> never calls back into plugin code. So the
    /// mutation has no effect at all rather than being tolerated — the registered plugin keeps
    /// answering for the filters it declared when it was accepted, which is the contract that
    /// makes the cache rebuild unable to throw.
    /// </remarks>
    [Fact]
    public void Register_AfterAPluginTurnedBad_StillRegistersOtherPlugins()
    {
        var host = PdfPluginHost.Instance;
        var mutable = new MutableFilterList();
        host.Register(mutable);
        Assert.Same(mutable, host.FindDecodePlugin("JBIG2Decode"));

        mutable.Filters = null!; // config file vanished mid-process

        var good = new Decoder("Test.Good", new[] { "JPXDecode" });
        host.Register(good); // must not throw
        Assert.Same(good, host.FindDecodePlugin("JPXDecode"));

        // The snapshot taken at registration still stands, so the mutation changed nothing.
        Assert.Same(mutable, host.FindDecodePlugin("JBIG2Decode"));
        Assert.True(host.Unregister("Test.Mutable"));
        Assert.Null(host.FindDecodePlugin("JBIG2Decode"));
    }

    [Fact]
    public void Register_RefusedDecodePlugin_LeavesTheRegistryExactlyAsItWas()
    {
        var host = PdfPluginHost.Instance;
        int before = host.GetPlugins().Count;

        Assert.Throws<ArgumentException>(
            () => host.Register(new Decoder("Test.NullFilters", null!)));

        Assert.Equal(before, host.GetPlugins().Count);
        Assert.False(host.IsRegistered("Test.NullFilters"));
    }

    [Fact]
    public void Register_AfterARefusedDecodePlugin_StillSucceedsForAnUnrelatedPlugin()
    {
        // The poisoning symptom: before the fix the broken plugin sat in _plugins and every
        // subsequent Register threw from RebuildDecodeCache.
        var host = PdfPluginHost.Instance;
        Assert.Throws<ArgumentException>(
            () => host.Register(new Decoder("Test.NullFilters", null!)));

        host.Register(new Decoder("Test.Good", new[] { "JPXDecode" }));

        Assert.True(host.IsRegistered("Test.Good"));
        Assert.Same(host.GetPlugins()[0], host.FindDecodePlugin("JPXDecode"));
    }

    [Fact]
    public void Unregister_AfterARefusedDecodePlugin_StillSucceeds()
    {
        var host = PdfPluginHost.Instance;
        host.Register(new Decoder("Test.Good", new[] { "JBIG2Decode" }));
        Assert.Throws<ArgumentException>(
            () => host.Register(new Decoder("Test.NullEntry", new[] { (string)null! })));

        Assert.True(host.Unregister("Test.Good"));
        Assert.Null(host.FindDecodePlugin("JBIG2Decode"));
    }

    [Fact]
    public void Register_DecodePluginWithAnEmptyFilterList_IsAcceptedAndDeclaresNothing()
    {
        // "I decode nothing" is a legal thing for a plugin to say, and must stay distinct from
        // the null the guard rejects.
        var host = PdfPluginHost.Instance;

        host.Register(new Decoder("Test.NoFilters", Array.Empty<string>()));

        Assert.True(host.IsRegistered("Test.NoFilters"));
        Assert.Null(host.FindDecodePlugin("JPXDecode"));
    }

    [Fact]
    public void FindDecodePlugin_AfterUnregister_NoLongerServesTheStaleCache()
    {
        var host = PdfPluginHost.Instance;
        var first = new Decoder("Test.First", new[] { "JPXDecode" });
        host.Register(first);
        Assert.Same(first, host.FindDecodePlugin("JPXDecode"));

        host.Unregister("Test.First");
        Assert.Null(host.FindDecodePlugin("JPXDecode"));

        // And the per-plugin snapshot really was dropped: re-registering a DIFFERENT plugin for
        // the same filter must serve the new one.
        var second = new Decoder("Test.Second", new[] { "JPXDecode" });
        host.Register(second);
        Assert.Same(second, host.FindDecodePlugin("JPXDecode"));
    }
}
