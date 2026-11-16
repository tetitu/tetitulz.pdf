// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// Serializes every test class that touches <c>PdfPluginHost</c>.
///
/// <para>
/// The host is a <b>process-wide singleton</b>. xUnit runs test classes in parallel across
/// collections by default, so two classes registering, unregistering or calling
/// <c>ResetForTesting()</c> on it concurrently will corrupt each other's state — a plugin
/// registered by one class disappears mid-test when another resets the registry.
/// </para>
/// <para>
/// This surfaced as a genuinely intermittent failure: the plugin-dispatch tests passed when run
/// alone and failed roughly one run in six as part of the full suite. Any new test that registers
/// a plugin must join this collection.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PluginHostCollection
{
    public const string Name = "PdfPluginHost (process-wide singleton)";
}
