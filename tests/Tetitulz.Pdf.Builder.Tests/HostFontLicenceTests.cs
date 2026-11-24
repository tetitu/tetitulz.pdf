// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// The <c>OS/2</c> <c>fsType</c> gate on fonts <see cref="BuilderFontResolver"/> picks up off the
/// host.
///
/// <para><b>What these exist for.</b> Embedding a font redistributes it, and <c>fsType</c> is where
/// the foundry records what it permits. <see cref="PdfExportOptions.AllowSystemFontSubstitution"/>
/// defaults to <b>true</b>, so a resolver that did not read the field would make a Builder export,
/// and everything layered on it (<c>HtmlToPdf</c>, the Templating plugin), embed a
/// Restricted-License host font by default.</para>
///
/// <para><b>Why a refusal file needs its acceptance control.</b> A resolution can return
/// <c>null</c> for want of coverage, for an unparseable file, or for the licence. Every refusal
/// here therefore asserts the <i>reason</i>, and
/// <see cref="HostFont_DeclaringNoRestriction_IsEmbedded"/> pins the same fixture succeeding with
/// only the two <c>fsType</c> bytes changed — without it the whole file would pass with the
/// fixture never reaching the gate at all.</para>
///
/// <para><b>Host-independent.</b> The candidate files are named through
/// <c>BuilderFontResolver.HostFontFilesForTest</c>, so the search sees exactly the synthetic fonts
/// written below and nothing the runner happens to have installed.</para>
/// </summary>
public sealed class HostFontLicenceTests : IDisposable
{
    private const string Hebrew = "א";        // א — nothing standard-14 can encode
    private const int HebrewAlef = 0x05D0;

    /// <summary>Bits 0–3 == 2: "must not be modified, embedded or exchanged".</summary>
    private const int FsTypeRestricted = 0x0002;

    /// <summary>Bit 9: bitmaps only, so the outlines may not be embedded.</summary>
    private const int FsTypeBitmapOnly = 0x0200;

    /// <summary>Bit 8: embeddable whole, but not subsettable.</summary>
    private const int FsTypeNoSubsetting = 0x0100;

    /// <summary>Bits 0–3 == 6: Preview &amp; Print. The classic misread, as "restricted | 4".</summary>
    private const int FsTypePreviewAndPrint = 0x0006;

    private readonly List<string> _written = new List<string>();

    public void Dispose()
    {
        foreach (string path in _written)
            try { File.Delete(path); } catch { /* a leftover temp file is not a test failure */ }
    }

    /// <summary>A synthetic Hebrew-covering font on disk, under a path no other test uses.</summary>
    private string HostFontFile(int fsType)
    {
        string path = Path.Combine(Path.GetTempPath(),
            "tetitulz-host-licence-" + Guid.NewGuid().ToString("N") + ".ttf");
        File.WriteAllBytes(path, MinimalTrueType.BuildWithFsType(Hebrew, fsType, "HostLicenceProbe"));
        _written.Add(path);
        return path;
    }

    private static (TrueTypeFontProgram? program, List<BuilderDiagnostic> reported) ResolveFromHost(
        string path)
    {
        var reported = new List<BuilderDiagnostic>();
        var options = new PdfExportOptions
        {
            AllowSystemFontSubstitution = true,
            FontProvider = null,
            OnDiagnostic = reported.Add,
        };

        var resolver = new BuilderFontResolver(options) { HostFontFilesForTest = new[] { path } };
        var program = resolver.Resolve(familyName: null, bold: false, italic: false,
                                       requiredCodePoints: new[] { HebrewAlef });
        return (program, reported);
    }

    private static string Details(List<BuilderDiagnostic> reported)
        => string.Join(" | ", reported.ConvertAll(d => d.Kind + ": " + d.Detail));

    // ------------------------------------------------------------------ the acceptance control

    [Fact]
    public void HostFont_DeclaringNoRestriction_IsEmbedded()
    {
        string path = HostFontFile(fsType: 0x0000);

        var (program, reported) = ResolveFromHost(path);

        // The answer, not "it returned something": this exact font, able to draw the letter.
        Assert.NotNull(program);
        Assert.True(program!.Covers(HebrewAlef));
        Assert.Equal(0x0000, program.EmbeddingLicense.FsType);
        Assert.Empty(reported);
    }

    // ------------------------------------------------------------------ the refusals

    [Fact]
    public void HostFont_DeclaringRestrictedLicence_IsRefused_NamingTheLicence()
    {
        string path = HostFontFile(FsTypeRestricted);

        var (program, reported) = ResolveFromHost(path);

        Assert.Null(program);

        // Asserted on the REASON, because a null resolution is also what a font that does not cover
        // the text, or one that will not parse, produces — and the control above proves this very
        // fixture resolves when only fsType changes.
        var reject = Assert.Single(reported);
        Assert.Equal(BuilderDiagnosticKind.FontFormatRejected, reject.Kind);
        Assert.Contains("Restricted License embedding", reject.Detail);
        Assert.Contains("0x0002", reject.Detail);
        Assert.Contains(path, reject.Detail);
    }

    [Fact]
    public void HostFont_DeclaringBitmapEmbeddingOnly_IsRefused_NamingTheLicence()
    {
        string path = HostFontFile(FsTypeBitmapOnly);

        var (program, reported) = ResolveFromHost(path);

        Assert.Null(program);
        var reject = Assert.Single(reported);
        Assert.Equal(BuilderDiagnosticKind.FontFormatRejected, reject.Kind);
        Assert.Contains("bitmap embedding only", reject.Detail);
        Assert.Contains("0x0200", reject.Detail);
    }

    // ------------------------------------------------------------------ what the gate must NOT refuse

    [Fact]
    public void HostFont_DeclaringPreviewAndPrint_IsEmbedded_BecauseBits0To3AreOneValue()
    {
        // 0x0006 is Preview & Print — the least restrictive bit set wins. Read as four independent
        // flags it looks like "restricted", which would refuse a font every foundry permits.
        string path = HostFontFile(FsTypePreviewAndPrint);

        var (program, reported) = ResolveFromHost(path);

        Assert.NotNull(program);
        Assert.True(program!.Covers(HebrewAlef));
        Assert.Equal(FsTypePreviewAndPrint, program.EmbeddingLicense.FsType);
        Assert.False(program.EmbeddingLicense.RestrictedLicense);
        Assert.Empty(reported);
    }

    [Fact]
    public void HostFont_ProhibitingSubsettingOnly_IsEmbedded()
    {
        // Bit 8 is about HOW the program ships, not whether it may. Core honours it by declining to
        // subset (see TrueTypeSubsetter / CffSubsetter); refusing the font here would lose a face
        // its own licence permits. Same rule as SystemFontResolver.Embeddable, which gates on
        // PermitsOutlineEmbedding and not on SubsettingProhibited.
        string path = HostFontFile(FsTypeNoSubsetting);

        var (program, reported) = ResolveFromHost(path);

        Assert.NotNull(program);
        Assert.True(program!.EmbeddingLicense.SubsettingProhibited);
        Assert.True(program.EmbeddingLicense.PermitsOutlineEmbedding);
        Assert.Empty(reported);
    }

    // ------------------------------------------------------------------ the deliberate asymmetry

    [Fact]
    public void ProviderSuppliedFont_IsUsed_EvenWhenItsLicenceForbidsEmbedding()
    {
        // The mirror of the refusals above, and it is a design decision rather than an oversight: a
        // caller who hands over a font program has made a licensing decision this code cannot see
        // and may hold a licence the bits know nothing about. Core states the same asymmetry on
        // FontResolutionRequest.AllowRestrictedSystemFonts. Deleting the "host only" scoping of the
        // gate — applying it in FromProvider too — reddens this.
        byte[] restricted = MinimalTrueType.BuildWithFsType(Hebrew, FsTypeRestricted);
        var reported = new List<BuilderDiagnostic>();
        var options = new PdfExportOptions
        {
            AllowSystemFontSubstitution = false,     // no host search at all: the provider or nothing
            FontProvider = _ => restricted,
            OnDiagnostic = reported.Add,
        };

        var program = new BuilderFontResolver(options)
            .Resolve(familyName: null, bold: false, italic: false,
                     requiredCodePoints: new[] { HebrewAlef });

        Assert.NotNull(program);
        Assert.True(program!.EmbeddingLicense.RestrictedLicense);
        Assert.True(program.Covers(HebrewAlef));
        Assert.Empty(reported);
    }

    // ------------------------------------------------------------------ the differential

    [Fact]
    public void TwoHostFontsDifferingOnlyInFsType_ResolveOppositely()
    {
        // The two halves in one test, so the difference cannot be attributed to anything else: same
        // builder, same glyphs, same filename shape, two bytes apart.
        string path = HostFontFile(FsTypeRestricted);
        var reported = new List<BuilderDiagnostic>();
        var options = new PdfExportOptions
        {
            AllowSystemFontSubstitution = true,
            OnDiagnostic = reported.Add,
        };

        var resolver = new BuilderFontResolver(options) { HostFontFilesForTest = new[] { path } };
        var permitted = new BuilderFontResolver(new PdfExportOptions { AllowSystemFontSubstitution = true })
        {
            HostFontFilesForTest = new[] { HostFontFile(0x0000) },
        };

        // Both resolvers see exactly one candidate; only the licence differs.
        Assert.Null(resolver.Resolve(null, false, false, new[] { HebrewAlef }));
        Assert.NotNull(permitted.Resolve(null, false, false, new[] { HebrewAlef }));
        Assert.Contains("Restricted License embedding", Details(reported));
    }
}
