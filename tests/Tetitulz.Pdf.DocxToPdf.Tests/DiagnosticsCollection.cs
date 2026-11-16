// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// Serialises the test classes that subscribe to <c>DocxToPdfConverter.DiagnosticReported</c>.
    ///
    /// <para>
    /// That event is <b>process-wide</b>, unlike <c>DocxDiagnostics.LastError</c> which is
    /// <c>[ThreadStatic]</c>. xUnit runs test classes in parallel by default, so two classes each
    /// collecting diagnostics around their own conversion will see each other's — and a collector
    /// can be invoked after its own test has removed it. The result is a test that passes alone and
    /// fails in a full run, which is the exact shape of flakiness this repository has been bitten by
    /// before (GAPS.md §8a).
    /// </para>
    ///
    /// <para>
    /// Putting the affected classes in one collection makes them run one after another. It is a
    /// narrow fix: no other class is slowed down, and nothing about the production code changes —
    /// a host that subscribes really does receive events from every thread, which is the documented
    /// contract.
    /// </para>
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class DiagnosticsCollection
    {
        internal const string Name = "DocxToPdf diagnostics";
    }
}
