// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Tests.TestInfrastructure;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts
{
    /// <summary>
    /// <see cref="FontCoverageProbe"/> must never disagree with a full
    /// <see cref="TrueTypeFontProgram.Parse(byte[])"/> about coverage.
    ///
    /// <para>This is the test that made the probe shippable. The optimisation was left open in GAPS
    /// §16 item 4a for one reason: a cheap coverage check that parses a <c>cmap</c> itself would be a
    /// second implementation of a rule <see cref="TrueTypeFontProgram.Covers"/> already owns, and two
    /// implementations of one rule silently disagreeing is this repository's most-repeated defect
    /// class. The probe avoids that by construction — it reassembles the <c>cmap</c> bytes and feeds
    /// the <i>same</i> parser — but "by construction" is an argument, and an argument is what these
    /// tests replace.</para>
    ///
    /// <para><b>The failure mode being guarded is one-directional and silent.</b> A probe that
    /// wrongly says <c>false</c> makes a scan skip a font that would have worked, so a document
    /// silently loses its glyphs while every structural assertion still passes. A probe that wrongly
    /// says <c>true</c> costs one needless full read and nothing else. So the assertions below are
    /// about <c>false</c> being earned.</para>
    /// </summary>
    public class FontCoverageProbeParityTests
    {
        /// <summary>
        /// Code points chosen to exercise both cheap and expensive cmap shapes: BMP Latin, a
        /// combining mark, a CJK ideograph, Hebrew, and two astral planes — format 4 cannot express
        /// the last two at all, so a font covering them must have a format 12 subtable.
        /// </summary>
        private static readonly int[] Probes =
        {
            'A', 'z', '0', 0x00E9, 0x0301, 0x05D0, 0x4E2D, 0x20AC, 0x1F600, 0x12000, 0x10FFFF,
        };

        [Fact]
        public void ASyntheticFontsOwnCharacters_ProbeAndFullParseAgree()
        {
            // Host-independent half: a font this test built, so the parity claim is asserted even on
            // a machine with no fonts installed at all.
            string path = Path.Combine(Path.GetTempPath(),
                "tetitulz-probe-" + Guid.NewGuid().ToString("N") + ".ttf");
            try
            {
                File.WriteAllBytes(path, MinimalTrueType.Build("ABC"));

                var full = TrueTypeFontProgram.Parse(File.ReadAllBytes(path));
                Assert.NotNull(full);

                // Covered, so the probe must not reject.
                Assert.NotEqual(false, FontCoverageProbe.Covers(path, new[] { (int)'A' }));
                Assert.NotEqual(false, FontCoverageProbe.Covers(path, new[] { (int)'A', 'B', 'C' }));

                // Not covered, and the full parse agrees — so a rejection here is correct and is the
                // whole point of the probe.
                Assert.False(full!.Covers('Z'));
                Assert.False(FontCoverageProbe.Covers(path, new[] { (int)'Z' }));

                // A set that is only partly covered must be rejected, not accepted: the contract is
                // "covers EVERY one of these".
                Assert.False(FontCoverageProbe.Covers(path, new[] { (int)'A', 'Z' }));
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        [InstalledFontsFact]
        public void EveryInstalledFont_ProbeNeverContradictsAFullParse()
        {
            // The attribute has already established this host has enough fonts to be worth asking.
            string[] files = InstalledFontProbe.Files;

            var disagreements = new List<string>();
            int decided = 0, deferred = 0, namesDecided = 0, namesDeferred = 0;

            foreach (string path in files)
            {
                TrueTypeFontProgram full;
                try { full = TrueTypeFontProgram.Parse(File.ReadAllBytes(path)); }
                catch { continue; }

                // The family-name half. A wrong name is the more dangerous of the two: it makes a
                // document naming that font silently miss its own face and fall back.
                string probedFamily = FontCoverageProbe.FamilyName(path);
                if (probedFamily != null && full != null && full.FamilyName != null &&
                    probedFamily != full.FamilyName)
                {
                    disagreements.Add(
                        $"{Path.GetFileName(path)} family: probe='{probedFamily}', parse='{full.FamilyName}'");
                }
                if (probedFamily != null) namesDecided++; else namesDeferred++;

                foreach (int codePoint in Probes)
                {
                    bool? probed = FontCoverageProbe.Covers(path, new[] { codePoint });
                    if (probed == null) { deferred++; continue; }
                    decided++;

                    // A null program means "unusable", which every caller treats as not covering, so
                    // the probe is allowed to say false and must not say true.
                    bool truth = full != null && full.Covers(codePoint);
                    if (probed.Value != truth)
                        disagreements.Add(
                            $"{Path.GetFileName(path)} U+{codePoint:X4}: probe={probed.Value}, parse={truth}");
                }
            }

            Assert.True(disagreements.Count == 0,
                $"the probe contradicted a full parse {disagreements.Count} time(s) — a wrong 'false' " +
                "silently drops glyphs from documents that had a usable font:" +
                Environment.NewLine + string.Join(Environment.NewLine, disagreements.Take(20)));

            // And the probe must actually be deciding things. If it deferred everything, the loop
            // above proved only that null never contradicts anything — banned test pattern 3 with a
            // font corpus attached.
            Assert.True(decided > deferred,
                $"the probe decided only {decided} of {decided + deferred} coverage questions, so this " +
                "asserts almost nothing about its verdicts");
            Assert.True(namesDecided > namesDeferred,
                $"the probe named only {namesDecided} of {namesDecided + namesDeferred} fonts, so the " +
                "family-name half is mostly deferring and buying nothing");
        }

        /// <summary>
        /// The probe must defer, never guess, on containers and wrappers it cannot reassemble.
        /// </summary>
        [Fact]
        public void MalformedAndWrappedInput_DefersInsteadOfRejecting()
        {
            string dir = Path.Combine(Path.GetTempPath(), "tetitulz-probe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                // A WOFF 1.0 container: Parse decodes these, so the probe must hand it over rather
                // than read 'numTables' out of a WOFF header and reject on nonsense.
                string woff = Path.Combine(dir, "wrapped.woff");
                File.WriteAllBytes(woff, MinimalWoff.Wrap(MinimalTrueType.Build("ABC")));
                Assert.Null(FontCoverageProbe.Covers(woff, new[] { (int)'A' }));

                // Truncated mid-directory, and empty: undecidable, not "does not cover".
                string truncated = Path.Combine(dir, "truncated.ttf");
                byte[] whole = MinimalTrueType.Build("ABC");
                File.WriteAllBytes(truncated, whole.Take(20).ToArray());
                Assert.Null(FontCoverageProbe.Covers(truncated, new[] { (int)'A' }));

                string empty = Path.Combine(dir, "empty.ttf");
                File.WriteAllBytes(empty, Array.Empty<byte>());
                Assert.Null(FontCoverageProbe.Covers(empty, new[] { (int)'A' }));

                // Absent entirely: the caller's File.Exists check is what handles this, and the probe
                // must not throw through it.
                Assert.Null(FontCoverageProbe.Covers(Path.Combine(dir, "absent.ttf"), new[] { (int)'A' }));

                // No code points asked about: nothing to decide.
                Assert.Null(FontCoverageProbe.Covers(woff, Array.Empty<int>()));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

    }
}
