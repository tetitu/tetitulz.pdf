// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Threading;
using System.Threading.Tasks;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Core.Tests.Plugins;

/// <summary>
/// Registration-path tests for <see cref="PdfPluginHost"/>.
///
/// <para>
/// These cover the defects the previous host had. The existing suite tested the registry
/// (register / unregister / hooks) but never the failure paths, which is why all of this
/// survived: a plugin whose <c>Initialize()</c> threw was reported as a duplicate and dropped
/// silently; <c>Initialize()</c> ran while the process-wide lock was held; and the singleton
/// implemented <see cref="IDisposable"/>, so a <c>using</c> block would permanently empty it.
/// </para>
///
/// <para>
/// The host is a process-wide singleton, so every test resets it — xUnit runs classes in
/// parallel across collections, so this class is pinned to its own collection.
/// </para>
/// </summary>
[Collection(nameof(PluginHostCollection))]
public sealed class PdfPluginHostRegistrationTests : IDisposable
{
    public PdfPluginHostRegistrationTests() => PdfPluginHost.Instance.ResetForTesting();
    public void Dispose() => PdfPluginHost.Instance.ResetForTesting();

    // ---------------------------------------------------------------- test doubles

    private class Basic : IPdfPlugin
    {
        public Basic(string name = "Test.Basic") => Name = name;
        public string Name { get; }
        public string Description => "test";
        public Version Version => new(1, 0, 0);
        public bool Initialized { get; private set; }
        public bool ShutDown { get; private set; }
        public virtual void Initialize() => Initialized = true;
        public void Shutdown() => ShutDown = true;
    }

    private sealed class ThrowsInvalidOperationOnInit : Basic
    {
        public ThrowsInvalidOperationOnInit() : base("Test.BadConfig") { }
        public override void Initialize() =>
            throw new InvalidOperationException("configuration is invalid");
    }

    private sealed class RecordsLockHeldDuringInit : IPdfPlugin
    {
        public string Name => "Test.LockProbe";
        public string Description => "test";
        public Version Version => new(1, 0, 0);
        public bool OtherThreadProgressedDuringInit { get; private set; }

        public void Initialize()
        {
            // If Initialize runs while the registry lock is held, another thread cannot
            // complete a registry call for the duration of this method.
            var done = new ManualResetEventSlim(false);
            var t = new Thread(() => { PdfPluginHost.Instance.GetPlugins(); done.Set(); })
            { IsBackground = true };
            t.Start();
            OtherThreadProgressedDuringInit = done.Wait(TimeSpan.FromSeconds(5));
        }

        public void Shutdown() { }
    }

    // ---------------------------------------------------------------- duplicate vs init failure

    [Fact]
    public void Register_DuplicateName_ThrowsDuplicatePluginException()
    {
        PdfPluginHost.Instance.Register(new Basic());

        var ex = Assert.Throws<DuplicatePluginException>(
            () => PdfPluginHost.Instance.Register(new Basic()));

        Assert.Equal("Test.Basic", ex.PluginName);
        Assert.IsAssignableFrom<PdfException>(ex);
    }

    /// <summary>
    /// The core regression: an <see cref="InvalidOperationException"/> from <c>Initialize()</c>
    /// must reach the caller, not be mistaken for a duplicate registration.
    /// </summary>
    [Fact]
    public void Register_InitializeThrowsInvalidOperation_PropagatesAndIsNotADuplicate()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => PdfPluginHost.Instance.Register(new ThrowsInvalidOperationOnInit()));

        Assert.Equal("configuration is invalid", ex.Message);
        Assert.IsNotType<DuplicatePluginException>(ex);
        Assert.False(PdfPluginHost.Instance.IsRegistered("Test.BadConfig"));
    }

    /// <summary>A failed Initialize must release the name, so a later retry can succeed.</summary>
    [Fact]
    public void Register_AfterFailedInitialize_NameIsFreedForRetry()
    {
        Assert.Throws<InvalidOperationException>(
            () => PdfPluginHost.Instance.Register(new ThrowsInvalidOperationOnInit()));

        // Same name, this time with a plugin that initialises cleanly.
        PdfPluginHost.Instance.Register(new Basic("Test.BadConfig"));

        Assert.True(PdfPluginHost.Instance.IsRegistered("Test.BadConfig"));
    }

    [Fact]
    public void Register_NullOrEmptyName_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => PdfPluginHost.Instance.Register(new Basic("")));
        Assert.Throws<ArgumentException>(() => PdfPluginHost.Instance.Register(new Basic(null!)));
    }

    /// <summary>
    /// No plugin can be registered without a name, so asking for a null one answers false rather
    /// than throwing out of the name index. The control: a registered name answers true.
    /// </summary>
    [Fact]
    public void IsRegistered_NullName_AnswersFalse()
    {
        PdfPluginHost.Instance.Register(new Basic("Test.Named"));

        Assert.False(PdfPluginHost.Instance.IsRegistered(null!));
        Assert.True(PdfPluginHost.Instance.IsRegistered("Test.Named"));
    }

    // ---------------------------------------------------------------- lock discipline

    /// <summary>
    /// <c>Initialize()</c> must not hold the registry lock: a plugin that blocks in Initialize
    /// would otherwise stall every Register/Unregister/Find* call process-wide.
    /// </summary>
    [Fact]
    public void Register_DoesNotHoldTheRegistryLockWhileInitializing()
    {
        var probe = new RecordsLockHeldDuringInit();
        PdfPluginHost.Instance.Register(probe);

        Assert.True(probe.OtherThreadProgressedDuringInit,
            "Another thread could not complete GetPlugins() while Initialize was running, " +
            "so Initialize is still being called inside the registry lock.");
    }

    // ---------------------------------------------------------------- teardown

    /// <summary>
    /// The host must not be <see cref="IDisposable"/>: it is a Lazy-backed process-wide
    /// singleton, so `using (var h = PdfPluginHost.Instance)` would silently and permanently
    /// unregister every plugin in the process.
    /// </summary>
    [Fact]
    public void Host_IsNotDisposable()
    {
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(PdfPluginHost)),
            "PdfPluginHost must not implement IDisposable — see ShutdownAll().");
    }

    [Fact]
    public void ShutdownAll_ShutsDownEveryPluginAndClearsRegistry()
    {
        var a = new Basic("Test.A");
        var b = new Basic("Test.B");
        PdfPluginHost.Instance.Register(a);
        PdfPluginHost.Instance.Register(b);

        PdfPluginHost.Instance.ShutdownAll();

        Assert.True(a.ShutDown);
        Assert.True(b.ShutDown);
        Assert.Empty(PdfPluginHost.Instance.GetPlugins());
    }

    /// <summary>The host stays usable after teardown — it is not a one-shot object.</summary>
    [Fact]
    public void ShutdownAll_HostRemainsUsable()
    {
        PdfPluginHost.Instance.Register(new Basic());
        PdfPluginHost.Instance.ShutdownAll();

        PdfPluginHost.Instance.Register(new Basic());
        Assert.True(PdfPluginHost.Instance.IsRegistered("Test.Basic"));
    }

    // ---------------------------------------------------------------- concurrency

    /// <summary>
    /// Concurrent Registers of the same name must produce exactly one registration and
    /// exactly one failure — the name is claimed before Initialize runs outside the lock.
    /// </summary>
    [Fact]
    public void Register_ConcurrentSameName_ExactlyOneWins()
    {
        int succeeded = 0, duplicates = 0;

        Parallel.For(0, 8, _ =>
        {
            try
            {
                PdfPluginHost.Instance.Register(new Basic("Test.Race"));
                Interlocked.Increment(ref succeeded);
            }
            catch (DuplicatePluginException)
            {
                Interlocked.Increment(ref duplicates);
            }
        });

        Assert.Equal(1, succeeded);
        Assert.Equal(7, duplicates);
        Assert.Single(PdfPluginHost.Instance.GetPlugins());
    }
}

[CollectionDefinition(nameof(PluginHostCollection), DisableParallelization = true)]
public sealed class PluginHostCollection { }
