// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Plugins.Conformance;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// The public facade entry point for conformance validation
/// (<see cref="PdfOperations.Validate(Stream, string)"/>).
/// </summary>
/// <remarks>
/// <para>PDF/X validation shipped with twelve checks and no way to reach it from
/// <c>PdfOperations</c>, which is the API surface CLAUDE.md's gap-closure checklist requires a
/// feature to be exposed through. It dispatches through <c>PdfPluginHost</c> rather than calling
/// the validator: the engines live in a plugin that references <c>Base</c>, so Base referencing
/// them back would be a cycle.</para>
/// <para><b>These tests live in the Conformance test project, not Base's.</b> Base cannot
/// reference the plugin, so a test that registers a real validator cannot live there either — the
/// same dependency rule that shapes the production code shapes where its test can go.</para>
/// </remarks>
[Collection(nameof(PluginHostSerialCollection))]
public sealed class PdfOperationsValidateTests : IDisposable
{
    public PdfOperationsValidateTests() => PdfPluginHost.Instance.ResetForTesting();
    public void Dispose() => PdfPluginHost.Instance.ResetForTesting();

    /// <summary>
    /// A minimal one-page PDF — valid enough to parse, nowhere near conforming.
    /// </summary>
    /// <remarks>
    /// Uses the shared <see cref="MinimalPdfBuilder"/> rather than a local fixture. The first draft
    /// hand-rolled one with <b>hard-coded xref offsets</b>, which is fragile in a way that hides
    /// itself: edit a byte of the body and the offsets are wrong, but the parser's scan-recovery
    /// path finds the objects anyway, so the test still passes while the fixture no longer says
    /// what it claims. Offsets belong in a builder that computes them.
    /// </remarks>
    private static byte[] MinimalPdf() => MinimalPdfBuilder.CreateMinimalPdf();

    [Fact]
    public void WithNoPluginRegistered_ReturnsNullAndSaysWhy()
    {
        // The dangerous shape this guards against: a null result that a caller reads as
        // "nothing wrong". LastValidationError is what separates "not checked" from "clean".
        using var ms = new MemoryStream(MinimalPdf());

        var result = PdfOperations.Validate(ms, "PDF/X-1a");

        Assert.Null(result);
        Assert.NotNull(PdfOperations.LastValidationError);
        Assert.Contains("No validation plugin is registered", PdfOperations.LastValidationError);
    }

    [Fact]
    public void WithPluginRegistered_DispatchesAndReportsViolations()
    {
        PdfXValidationPlugin.RegisterAll(PdfPluginHost.Instance);
        using var ms = new MemoryStream(MinimalPdf());

        var result = PdfOperations.Validate(ms, "PDF/X-1a");

        Assert.NotNull(result);
        Assert.Null(PdfOperations.LastValidationError);
        // A bare page has no output intent, no XMP and no /Trapped, so it cannot conform —
        // asserting on real violations rather than merely "a result came back".
        Assert.False(result.IsConformant);
        Assert.NotEmpty(result.Violations);
    }

    /// <summary>
    /// The caller-visible half of the informational channel: a document the validator could not
    /// fully examine must not come back from the facade looking clean.
    /// </summary>
    /// <remarks>
    /// <c>Validate</c> hands the plugin's result straight back, so a <c>null</c> return with
    /// <see cref="PdfOperations.LastValidationError"/> set is <b>not</b> the only way this method
    /// can mean "not checked" — a non-null, conformant, violation-free result can mean it too.
    /// The fixture comes from <c>PdfXValidatorTests</c> rather than being rebuilt here: two copies
    /// of a fixture this specific are how the two tests would come to assert different documents.
    /// </remarks>
    [Fact]
    public void WithPluginRegistered_ASkippedCheckReachesTheCallerAsPartial()
    {
        PdfXValidationPlugin.RegisterAll(PdfPluginHost.Instance);

        // Otherwise conforming; its annotation appearance declares /FlateDecode over bytes that
        // are not Flate, so the X08 colour scan cannot decode it and is skipped.
        using var ms = new MemoryStream(PdfXValidatorTests.Build(new PdfXValidatorTests.XDoc
        {
            AnnotationAppearanceEntries     = "",
            UndecodableAnnotationAppearance = true,
        }));

        var result = PdfOperations.Validate(ms, "PDF/X-1a");

        Assert.NotNull(result);
        Assert.True(result.IsConformant, string.Join("; ", result.Violations.Select(v => v.Code)));
        Assert.Empty(result.Violations);
        Assert.True(result.IsPartial);
        Assert.Contains(result.Informational, i => i.Code == "X99");
    }

    [Fact]
    public void UnknownLevel_NamesTheLevelsThatAreRegistered()
    {
        PdfXValidationPlugin.RegisterAll(PdfPluginHost.Instance);
        using var ms = new MemoryStream(MinimalPdf());

        var result = PdfOperations.Validate(ms, "PDF/A-1b");

        Assert.Null(result);
        Assert.Contains("PDF/X-1a", PdfOperations.LastValidationError);
    }

    /// <summary>
    /// A mis-cased level resolves to the plugin for <b>that</b> level — not merely to some plugin.
    /// </summary>
    /// <remarks>
    /// Two earlier drafts were each too weak, in different ways, and both are worth recording
    /// because they look convincing:
    /// <list type="number">
    ///   <item><c>Assert.NotNull(Validate(ms, "pdf/x-1a"))</c> — passes for any dispatch that
    ///     returns anything. The pattern-3 guard rejected it, correctly.</item>
    ///   <item>Comparing the lowercase result against the exact-case result for equality. Stronger,
    ///     but still blind to the failure that matters: <c>RegisterAll</c> registers three levels,
    ///     and a dispatcher that ignored the requested level and returned the <b>first</b> plugin
    ///     would satisfy it, because the first happens to be X-1a.</item>
    /// </list>
    /// Asserting the returned <c>ConformanceLevel</c> across all three levels closes both: a
    /// first-plugin-wins dispatcher fails on X-3 and X-4, and a case-sensitive one fails on every
    /// row.
    /// </remarks>
    [Theory]
    [InlineData("pdf/x-1a", "PDF/X-1a")]
    [InlineData("PDF/x-3", "PDF/X-3")]
    [InlineData("pdf/X-4", "PDF/X-4")]
    public void MisCasedLevel_ResolvesToThatLevelsPlugin(string requested, string expected)
    {
        PdfXValidationPlugin.RegisterAll(PdfPluginHost.Instance);
        using var ms = new MemoryStream(MinimalPdf());

        var result = PdfOperations.Validate(ms, requested);

        Assert.NotNull(result);
        Assert.Equal(expected, result.ConformanceLevel);
    }

    [Fact]
    public void GetSupportedConformanceLevels_ListsEveryRegisteredValidator()
    {
        Assert.Empty(PdfOperations.GetSupportedConformanceLevels());

        PdfXValidationPlugin.RegisterAll(PdfPluginHost.Instance);
        var levels = PdfOperations.GetSupportedConformanceLevels();

        Assert.Contains("PDF/X-1a", levels);
        Assert.Contains("PDF/X-3", levels);
        Assert.Contains("PDF/X-4", levels);
    }

    [Fact]
    public void NullStreamOrLevel_Throws()
    {
        using var ms = new MemoryStream(MinimalPdf());
        Assert.Throws<ArgumentNullException>(() => PdfOperations.Validate(null, "PDF/X-1a"));
        Assert.Throws<ArgumentException>(() => PdfOperations.Validate(ms, "  "));
    }
}

/// <summary>
/// <c>PdfPluginHost.Instance</c> is process-wide, so tests that register into it must not run
/// beside each other.
/// </summary>
[CollectionDefinition(nameof(PluginHostSerialCollection), DisableParallelization = true)]
public sealed class PluginHostSerialCollection { }
