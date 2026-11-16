// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.DocxToPdf.Rendering;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// <see cref="FontResolver"/> prunes a CFF/OpenType program's dead tables at load, the way
    /// Builder's resolver has since plan 11 §5e slice 1.
    ///
    /// <para><b>Why this asymmetry existed and why it mattered.</b> Slice 1 wired
    /// <c>Core.Fonts.SfntTablePruner</c> — which had no caller anywhere in <c>src/</c> — into
    /// <c>BuilderFontResolver</c> and the accessibility remediator, and stopped there.
    /// <c>DocxToPdf</c> resolves fonts through its own independent resolver, so every DOCX
    /// conversion kept shipping the <c>GSUB</c>/<c>GPOS</c> tables a PDF reader never consults, in a
    /// converter whose whole job is producing PDFs. Nothing failed; the files were just bigger, which
    /// is the kind of gap no test notices.</para>
    ///
    /// <para><b>It composes with charstring blanking rather than duplicating it.</b> The pruner drops
    /// whole tables; <c>Type0FontBuilder</c>'s blanker empties charstrings at embed time. Neither can
    /// undo the other, and this is measured on the <i>loaded program</i> so it stays a claim about
    /// pruning alone.</para>
    /// </summary>
    public class CffFontPruningTests
    {
        /// <summary>
        /// A CFF font carrying a large layout table must come back from the loader without it — and
        /// still parse, still be CFF, and still have every glyph.
        /// </summary>
        [Fact]
        public void LoadingACffFontDropsItsLayoutTables()
        {
            // 64 KB of GSUB: far more than the rest of the font, so a prune is unmistakable and a
            // no-op is equally so.
            byte[] font = MinimalOpenTypeCff.Build("ABCDEFGH", padTo: 60, layoutTableBytes: 64 * 1024);
            string path = WriteTempFont(font);

            try
            {
                var loaded = FontResolver.LoadFontFileForTest(path);

                Assert.NotNull(loaded);
                Assert.True(loaded.HasCff, "the loader turned a CFF program into something else");
                Assert.True(loaded.FontData.Length < font.Length - 60_000,
                    $"the loaded program is {loaded.FontData.Length} bytes against {font.Length} on " +
                    "disk; the 64 KB GSUB table was supposed to go");

                // Pruning must not touch the outlines or the cmap a reader looks glyphs up through.
                Assert.Equal(
                    MinimalOpenTypeCff.ReadCharstrings(font).Count,
                    MinimalOpenTypeCff.ReadCharstrings(loaded.FontData).Count);
                foreach (char c in "ABCDEFGH")
                    Assert.True(loaded.Covers(c), $"pruning lost coverage of '{c}'");
            }
            finally { TryDelete(path); }
        }

        /// <summary>
        /// A font with nothing droppable must come back byte-identical. The pruner returns null rather
        /// than the input when it cannot help, and the loader has to read that as "use the original" —
        /// a resolver that substituted an empty result here would lose the font entirely.
        /// </summary>
        [Fact]
        public void AFontWithNothingToPruneIsReturnedUnchanged()
        {
            byte[] font = MinimalOpenTypeCff.Build("ABC", padTo: 60);   // no GSUB
            string path = WriteTempFont(font);

            try
            {
                var loaded = FontResolver.LoadFontFileForTest(path);

                Assert.NotNull(loaded);
                Assert.Equal(font.Length, loaded.FontData.Length);
                Assert.Equal(font, loaded.FontData);
            }
            finally { TryDelete(path); }
        }

        /// <summary>
        /// A glyf-outlined font is left alone here: the pruning call is gated on
        /// <c>HasCff</c>, matching Builder, because a TrueType program reaches the real subsetter at
        /// embed time and that one rebuilds the tables anyway.
        /// </summary>
        [Fact]
        public void ATrueTypeFontIsNotPrunedAtLoad()
        {
            byte[] font = MinimalTrueType.Build("ABC");
            string path = WriteTempFont(font);

            try
            {
                var loaded = FontResolver.LoadFontFileForTest(path);

                Assert.NotNull(loaded);
                Assert.False(loaded.HasCff);
                Assert.Equal(font, loaded.FontData);
            }
            finally { TryDelete(path); }
        }

        /// <summary>
        /// A distinct temp path per call. The resolver's file cache is process-wide and never
        /// cleared — clearing one is what made <c>FontFileLocatorTests</c> break
        /// <c>SystemFontResolverTests</c> (GAPS §14) — so two tests reusing a path would serve each
        /// other's font.
        /// </summary>
        private static string WriteTempFont(byte[] bytes)
        {
            string path = Path.Combine(Path.GetTempPath(),
                "tetitulz-cffprune-" + Guid.NewGuid().ToString("N") + ".otf");
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { }
        }
    }
}
