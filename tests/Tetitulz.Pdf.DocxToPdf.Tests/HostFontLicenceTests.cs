// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.DocxToPdf.Rendering;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// The <c>OS/2</c> <c>fsType</c> gate on fonts <see cref="FontResolver"/> picks up off the host.
    /// </summary>
    /// <remarks>
    /// <para><b>What this closes.</b> Embedding a font redistributes it, and <c>fsType</c> is where
    /// the foundry records what it permits. This resolver never read the field, while
    /// <c>Builder.Export.BuilderFontResolver</c> gated the identical decision — so the same font on
    /// the same machine was refused by one converter and shipped inside the PDF by the other. GAPS.md
    /// §3 carried it as open because a gate alone is not the fix: refusing changes layout (the run
    /// falls back to standard-14), so a deployment that <i>does</i> hold the licence needed a way
    /// through. <c>DocxToPdfOptions.AllowRestrictedSystemFonts</c> is that way.</para>
    ///
    /// <para><b>Why a refusal file needs its acceptance control.</b> A resolution can return
    /// <c>null</c> for want of coverage, for an unparseable file, or for the licence. Every refusal
    /// here therefore has <see cref="AHostFont_DeclaringNoRestriction_IsResolved"/> beside it,
    /// pinning the same fixture succeeding with only the two <c>fsType</c> bytes changed — without
    /// it the whole file would pass with the fixture never reaching the gate at all.</para>
    ///
    /// <para><b>Host-independent.</b> The font list is replaced for the duration of each test, so
    /// the search sees exactly the synthetic fonts written below and nothing the runner happens to
    /// have installed — and the resolver's static caches are cleared on the way in and out.</para>
    ///
    /// <para><b>Serialised with the other diagnostics classes</b>, because the isolation above is
    /// only as good as what runs beside it: the replaced list is <c>[ThreadStatic]</c> but the
    /// resolution caches it clears are <b>static</b>, so a class resolving fonts in parallel can
    /// refill them from the real machine between the clear and the resolve. These tests also
    /// collect from the process-wide <c>DocxDiagnostics.OnEvent</c>, which is the collection's own
    /// reason for existing.</para>
    /// </remarks>
    [Collection(DiagnosticsCollection.Name)]
    public sealed class HostFontLicenceTests : IDisposable
    {
        private const string Hebrew = "א";        // nothing standard-14 can encode
        private const int HebrewAlef = 0x05D0;

        /// <summary>Bits 0–3 == 2: "must not be modified, embedded or exchanged".</summary>
        private const int FsTypeRestricted = 0x0002;

        /// <summary>Bit 9: bitmaps only, so the outlines may not be embedded.</summary>
        private const int FsTypeBitmapOnly = 0x0200;

        /// <summary>Installable, i.e. no restriction at all.</summary>
        private const int FsTypeInstallable = 0x0000;

        /// <summary>Bits 0–3 == 6: Preview &amp; Print — the classic misread as "restricted | 4".</summary>
        private const int FsTypePreviewAndPrint = 0x0006;

        private readonly List<string> _written = new List<string>();

        public void Dispose()
        {
            foreach (string path in _written)
                try { File.Delete(path); } catch { /* a leftover temp file is not a test failure */ }
        }

        private string HostFontFile(int fsType)
        {
            string path = Path.Combine(Path.GetTempPath(),
                "tetitulz-docx-licence-" + Guid.NewGuid().ToString("N") + ".ttf");
            File.WriteAllBytes(path, MinimalTrueType.BuildWithFsType(Hebrew, fsType, "DocxLicenceProbe"));
            _written.Add(path);
            return path;
        }

        /// <summary>
        /// Resolves through the replaced font list, returning the program <b>and what was
        /// reported</b> — because a refusal has to be checked for its reason, not merely for being
        /// a refusal. A null here could equally mean the fixture was unparseable or covered
        /// nothing.
        /// </summary>
        private (TrueTypeFontProgram Program, string Reported) ResolveFromHost(
            int fsType, bool allowRestricted)
        {
            string path = HostFontFile(fsType);
            var reported = new List<string>();
            Action<string> collect = reported.Add;

            DocxDiagnostics.OnEvent += collect;
            try
            {
                using (FontResolver.UseHostFontFilesForTest(new[] { path }))
                {
                    // A family no candidate filename matches, so the search reaches the
                    // coverage pass — which is the one that reads the replaced font list.
                    var program = FontResolver.Resolve(
                        "NoSuchFamilyOnThisMachine", bold: false, italic: false,
                        requiredCodePoints: new[] { HebrewAlef }, allowRestricted: allowRestricted);
                    return (program, string.Join("\n", reported));
                }
            }
            finally { DocxDiagnostics.OnEvent -= collect; }
        }

        // ── refused, each for the reason in its name ────────────────────────────────

        [Fact]
        public void AHostFont_DeclaringRestrictedLicence_IsNotEmbedded_NamingTheLicence()
        {
            var (program, reported) = ResolveFromHost(FsTypeRestricted, allowRestricted: false);

            Assert.Null(program);
            // The reason, not just the refusal: a null program is also what "no covering font"
            // and "unparseable file" produce, and those are different defects.
            Assert.Contains("was not embedded", reported, StringComparison.Ordinal);
            Assert.Contains("AllowRestrictedSystemFonts", reported, StringComparison.Ordinal);
        }

        [Fact]
        public void AHostFont_DeclaringBitmapEmbeddingOnly_IsNotEmbedded_NamingTheLicence()
        {
            var (program, reported) = ResolveFromHost(FsTypeBitmapOnly, allowRestricted: false);

            Assert.Null(program);
            Assert.Contains("was not embedded", reported, StringComparison.Ordinal);
        }

        // ── accepted ───────────────────────────────────────────────────────────────

        /// <summary>
        /// The acceptance control: the same synthetic font with only the two <c>fsType</c> bytes
        /// changed resolves, and nothing is reported. Without this the refusals above would pass on
        /// a fixture that never reached the gate.
        /// </summary>
        [Fact]
        public void AHostFont_DeclaringNoRestriction_IsResolvedAndReportsNothing()
        {
            var (program, reported) = ResolveFromHost(FsTypeInstallable, allowRestricted: false);

            Assert.NotNull(program);
            Assert.True(program.Covers(HebrewAlef), "the resolved face must cover the text asked for");
            Assert.DoesNotContain("was not embedded", reported, StringComparison.Ordinal);
        }

        /// <summary>
        /// <b>Bits 0–3 are one mutually-exclusive value, not four flags.</b> <c>0x0006</c> is
        /// Preview &amp; Print — the least restrictive bit set wins — and reading it as
        /// "restricted <i>and</i> preview" is the classic way to get this wrong.
        /// </summary>
        [Fact]
        public void AHostFont_DeclaringPreviewAndPrint_IsResolved()
        {
            var (program, reported) = ResolveFromHost(FsTypePreviewAndPrint, allowRestricted: false);

            Assert.NotNull(program);
            Assert.True(program.Covers(HebrewAlef), "the resolved face must cover the text asked for");
            Assert.DoesNotContain("was not embedded", reported, StringComparison.Ordinal);
        }

        // ── the escape hatch ───────────────────────────────────────────────────────

        /// <summary>
        /// <c>AllowRestrictedSystemFonts</c> lets a deployment that holds the licence through, and
        /// says nothing while doing it — the caller has already made the decision. This is what
        /// makes the gate shippable: without it, refusing would change layout with no recourse,
        /// which is why GAPS.md kept the item open rather than adding the check alone.
        /// </summary>
        [Theory]
        [InlineData(FsTypeRestricted)]
        [InlineData(FsTypeBitmapOnly)]
        public void WithTheOptionSet_ARestrictedHostFontIsEmbedded(int fsType)
        {
            var (program, reported) = ResolveFromHost(fsType, allowRestricted: true);

            Assert.NotNull(program);
            Assert.True(program.Covers(HebrewAlef), "the resolved face must cover the text asked for");
            Assert.DoesNotContain("was not embedded", reported, StringComparison.Ordinal);
        }

        /// <summary>
        /// <b>The flag is part of the cache key.</b> The resolver is a static class with static
        /// caches, so a permissive conversion must not hand its answer to a strict one that follows
        /// — which, for a licence gate, would mean one caller's setting silently governing another's
        /// output.
        /// </summary>
        [Fact]
        public void APermissiveResolutionDoesNotAnswerAStrictOne()
        {
            string path = HostFontFile(FsTypeRestricted);
            using (FontResolver.UseHostFontFilesForTest(new[] { path }))
            {
                var permissive = FontResolver.Resolve("NoSuchFamilyOnThisMachine", false, false,
                    new[] { HebrewAlef }, allowRestricted: true);
                var strict = FontResolver.Resolve("NoSuchFamilyOnThisMachine", false, false,
                    new[] { HebrewAlef }, allowRestricted: false);

                Assert.True(permissive != null && permissive.Covers(HebrewAlef),
                    "the permissive call must resolve the restricted face");
                Assert.True(strict == null,
                    "the strict call must refuse it, not inherit the permissive call's cached answer");
            }
        }
    }
}
