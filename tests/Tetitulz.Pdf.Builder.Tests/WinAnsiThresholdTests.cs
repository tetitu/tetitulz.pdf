// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// The threshold at which a run stops being drawable by a Standard-14 face and needs an embedded
/// composite font — and the fact that <b>two</b> pieces of code have to agree on it.
/// </summary>
/// <remarks>
/// <para><b>Why this file exists.</b> <c>RtlText.NeedsUnicode</c> decides whether a run enters the
/// embedding path; <c>RtlText.CollectCodePoints</c> decides which code points that path must
/// cover. Its own remarks say the two "must agree, or a run enters the embedding path with an
/// empty required-code-point set, resolution declines for want of anything to cover, and the
/// exporter reports unrenderable text for a character WinAnsi can draw". A mutation sweep moved
/// <c>NeedsUnicode</c>'s threshold from <c>&gt; 0xFF</c> to <c>&gt; 0x7F</c> — creating exactly
/// that disagreement — and <b>all 458 Builder tests stayed green</b>.</para>
///
/// <para><b>What it costs.</b> Every character in U+0080–U+00FF is a character WinAnsi encodes and
/// the Standard-14 AFM tables carry: accented Latin, the currency and punctuation a European
/// document is full of. Under the disagreement each one is reported unrenderable and drawn as a
/// substitute, so a French or Spanish document degrades while an English one is perfect — and
/// under <c>UnrenderableTextPolicy.Throw</c> it stops being exportable at all.</para>
///
/// <para><b>The pairing is the test.</b> The accepting arm alone passes on an exporter that never
/// reports anything; the refusing arm alone passes on the mutation. Only together do they say the
/// threshold sits between Latin-1 supplement and everything above it.</para>
/// </remarks>
public class WinAnsiThresholdTests
{
    /// <summary>Nothing on the host may be read, so the standard-14 path is the only one left.</summary>
    private static PdfExportOptions NoFonts() => new PdfExportOptions
    {
        AllowSystemFontSubstitution = false,
        FontProvider = null,
    };

    private static void Export(string text)
    {
        var doc = new Document();
        var section = new Section();
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = text });
        section.Children.Add(p);
        doc.Sections.Add(section);
        doc.ToPdf(NoFonts());
    }

    // ── accepted: WinAnsi can draw these, so no font has to be found ──────────────────

    /// <summary>
    /// A Latin-1 supplement character is drawn by the Standard-14 face and raises no
    /// font-resolution diagnostic — there is nothing to resolve.
    /// </summary>
    [Theory]
    [InlineData("café")]        // é — the commonest case there is
    [InlineData("über")]        // ü
    [InlineData("España")]      // ñ
    [InlineData("£10.00")]      // £ — Latin-1 punctuation, not a letter
    public void ALatin1SupplementCharacter_NeedsNoEmbeddedFont(string text)
    {
        Export(text);

        var d = BuilderDiagnostics.LastError;
        Assert.False(d != null && d.Kind == BuilderDiagnosticKind.FontNotResolved,
            $"\"{text}\" is WinAnsi-encodable and the Standard-14 tables carry it, so it must not " +
            $"be reported unresolvable. Reported: {d?.Kind} — {d?.Detail}");
    }

    /// <summary>Plain ASCII is the same answer, and the floor of the range above.</summary>
    [Fact]
    public void AsciiNeedsNoEmbeddedFont()
    {
        Export("plain ascii");

        var d = BuilderDiagnostics.LastError;
        Assert.False(d != null && d.Kind == BuilderDiagnosticKind.FontNotResolved,
            $"ASCII must never need an embedded font. Reported: {d?.Kind} — {d?.Detail}");
    }

    // ── refused: above the threshold there genuinely is nothing to draw with ──────────

    /// <summary>
    /// The acceptance control, in the other direction: a character WinAnsi cannot encode does need
    /// a font, and with none available the exporter says so rather than drawing a silent
    /// substitute. Without this arm the tests above pass on an exporter that reports nothing at all.
    /// </summary>
    [Theory]
    [InlineData("שלום")]   // Hebrew
    [InlineData("你好")]               // CJK
    [InlineData("€5")]                    // € — above the Latin-1 block, unlike £
    public void ACharacterAboveTheThreshold_IsReportedUnresolvable(string text)
    {
        Export(text);

        var d = BuilderDiagnostics.LastError;
        Assert.NotNull(d);
        Assert.Equal(BuilderDiagnosticKind.FontNotResolved, d!.Kind);
    }
}
