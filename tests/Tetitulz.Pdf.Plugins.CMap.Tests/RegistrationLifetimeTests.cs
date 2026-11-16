// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Plugins.CMap;
using Xunit;

namespace Tetitulz.Pdf.Plugins.CMap.Tests;

/// <summary>
/// Pins the registration lifetime the plugin's doc comment now states, so the documentation and
/// the behaviour cannot drift apart silently.
///
/// <para><b>The finding.</b> <c>Initialize</c> eagerly materialises all 167 embedded CMaps —
/// ~6.5 MB of permanently-retained <c>byte[]</c> — whether or not the host ever opens a CJK
/// document, and <c>Shutdown</c> is a no-op: a host that calls
/// <c>PdfPluginHost.Unregister("Tetitulz.Plugins.AdobeCMap")</c> gets <c>true</c> back while every
/// CMap stays registered and every byte stays alive. Making either half better needs
/// <see cref="PredefinedCMapRegistry"/> to grow an <c>Unregister</c> and a lazy
/// <c>name → bytes</c> resolver, which live in <c>Tetitulz.Pdf.Core.Fonts</c>. Until then the
/// honest move is to state the cost and pin it.</para>
///
/// <para>These are characterization tests: if someone later makes registration lazy or Shutdown
/// reversible, they go red and force the doc comment to be corrected with the code — which is
/// exactly the coupling CLAUDE.md warns is missing whenever prose stands in for a test.</para>
/// </summary>
public class RegistrationLifetimeTests
{
    /// <summary>A CMap from each of the four bundled orderings, so the assertion is not resting on
    /// one lucky resource name.</summary>
    private static readonly string[] SampleCMaps =
    {
        "UniJIS-UCS2-H",     // Adobe-Japan1
        "UniGB-UCS2-H",      // Adobe-GB1
        "UniCNS-UCS2-H",     // Adobe-CNS1
        "UniKS-UCS2-H",      // Adobe-Korea1
    };

    [Fact]
    public void Initialize_RegistersTheBundledCMaps()
    {
        new AdobeCMapPlugin().Initialize();

        foreach (string name in SampleCMaps)
            Assert.True(PredefinedCMapRegistry.IsRegistered(name),
                $"{name} should be registered after Initialize");
    }

    /// <summary>
    /// <b>Shutdown does not unregister, and that is documented rather than fixed.</b> A host
    /// reading <c>Unregister</c> returning true as "this plugin is gone" is wrong, which is the
    /// point of saying so on the type.
    /// </summary>
    [Fact]
    public void Shutdown_LeavesTheCMapsRegistered()
    {
        var plugin = new AdobeCMapPlugin();
        plugin.Initialize();

        plugin.Shutdown();

        foreach (string name in SampleCMaps)
            Assert.True(PredefinedCMapRegistry.IsRegistered(name),
                $"{name} is still registered after Shutdown — see AdobeCMapPlugin's remarks. " +
                "If this now fails, Shutdown became reversible and the doc comment must be updated.");
    }

    /// <summary>
    /// Registering twice is safe and adds nothing: <c>Initialize</c> skips a name already in the
    /// registry, so the eager cost is paid once even when a host registers the plugin repeatedly.
    /// </summary>
    [Fact]
    public void Initialize_IsIdempotent()
    {
        var plugin = new AdobeCMapPlugin();
        plugin.Initialize();
        plugin.Initialize();

        foreach (string name in SampleCMaps)
            Assert.True(PredefinedCMapRegistry.IsRegistered(name));
    }

    /// <summary>
    /// The registry is not a catch-all: a name the plugin does not ship stays unregistered, so the
    /// assertions above are about these CMaps rather than about <c>IsRegistered</c> answering true
    /// for everything.
    /// </summary>
    [Fact]
    public void AnUnbundledName_IsNotRegistered()
    {
        new AdobeCMapPlugin().Initialize();

        Assert.False(PredefinedCMapRegistry.IsRegistered("Not-A-Real-CMap-Name"));
    }
}
