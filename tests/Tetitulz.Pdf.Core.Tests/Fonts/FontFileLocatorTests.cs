// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts
{
    /// <summary>
    /// Covers the cross-platform font-file resolution that backs standard-14 substitution when the
    /// platform font manager has nothing to offer (a bare Linux container has no Arial, Times New
    /// Roman or Courier New — the three names the renderer's tables ask for).
    ///
    /// <para>These tests drive the real matcher against a controlled directory supplied through
    /// <c>TETITULZ_PDF_FONTDIR</c>. The matcher works purely on filenames — it never opens a font —
    /// so empty fixture files exercise every branch honestly, and the outcome does not depend on
    /// what fonts the machine running the suite happens to have installed.</para>
    ///
    /// <para>The locator's caches and the environment variable are process-global, so every test
    /// here re-points the variable and resets the caches. xunit runs the methods of one class
    /// sequentially, which is what makes that safe.</para>
    /// </summary>
    [Collection(SystemFontCacheCollection.Name)]
    public sealed class FontFileLocatorTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _previousEnv;

        public FontFileLocatorTests()
        {
            _previousEnv = Environment.GetEnvironmentVariable(FontFileLocator.FontDirEnvVar);
            _dir = Path.Combine(Path.GetTempPath(),
                "tetitulz-fontlocator-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(FontFileLocator.FontDirEnvVar, _previousEnv);
            FontFileLocator.ResetCache();
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }

        /// <summary>
        /// The one family match: separators and case folded, so the family a PostScript name derives
        /// to matches the one the platform reports — and the platform's substitute for an unknown
        /// family, or a different family sharing a prefix, does not. <c>FontCache</c>,
        /// <c>SystemFontResolver</c> and the Accessibility font remediator all call it, so no copy keeps a
        /// separator set of its own.
        /// </summary>
        [Theory]
        [InlineData("Times New Roman", "TimesNewRoman", true)]
        [InlineData("Noto-Sans_CJK JP", "notosanscjkjp", true)]
        [InlineData("Liberation.Sans", "Liberation Sans", true)]
        [InlineData("Arial", "Arial", true)]
        [InlineData("Segoe UI", "Arial", false)]
        [InlineData("Arial Narrow", "Arial", false)]
        [InlineData("", "Arial", false)]
        [InlineData("Arial", null, false)]
        public void IsFamilyMatch_FoldsSeparatorsAndCaseAndRefusesASubstitute(
            string resolved, string requested, bool expected)
        {
            Assert.Equal(expected, FontFileLocator.IsFamilyMatch(resolved, requested));
        }

        /// <summary>
        /// Creates the named empty files and points the locator at only this directory.
        /// Pointing it exclusively here is what keeps the assertions independent of the host: the
        /// custom root is searched first, and every assertion below is about which of THESE files
        /// wins, never about whether some real system font exists.
        /// </summary>
        private void GivenFonts(params string[] fileNames)
        {
            foreach (string name in fileNames)
                File.WriteAllBytes(Path.Combine(_dir, name), Array.Empty<byte>());

            Environment.SetEnvironmentVariable(FontFileLocator.FontDirEnvVar, _dir);
            FontFileLocator.ResetCache();
        }

        /// <summary>Resolved path, reduced to its filename, or null.</summary>
        private static string NameOf(string path) => path == null ? null : Path.GetFileName(path);

        // ── standard-14 substitution ────────────────────────────────────────────────────────

        [Fact]
        public void FindFamilyFile_Arial_PrefersMetricCompatibleLiberation_OverDejaVu()
        {
            // Both cover Latin; only Liberation Sans is metric-compatible with Arial, so
            // substituting it leaves the PDF /Widths array meaningful. Order matters, and this is
            // the assertion that pins it.
            GivenFonts("DejaVuSans.ttf", "LiberationSans-Regular.ttf");

            string resolved = FontFileLocator.FindFamilyFile("Arial", bold: false, italic: false);

            Assert.Equal("LiberationSans-Regular.ttf", NameOf(resolved));
        }

        [Fact]
        public void FindFamilyFile_TimesNewRoman_ResolvesToLiberationSerif()
        {
            GivenFonts("LiberationSerif-Regular.ttf", "LiberationSans-Regular.ttf");

            string resolved = FontFileLocator.FindFamilyFile("Times New Roman", bold: false, italic: false);

            Assert.Equal("LiberationSerif-Regular.ttf", NameOf(resolved));
        }

        [Fact]
        public void FindFamilyFile_CourierNew_ResolvesToLiberationMono_NotLiberationSans()
        {
            GivenFonts("LiberationSans-Regular.ttf", "LiberationMono-Regular.ttf");

            string resolved = FontFileLocator.FindFamilyFile("Courier New", bold: false, italic: false);

            Assert.Equal("LiberationMono-Regular.ttf", NameOf(resolved));
        }

        [Fact]
        public void FindFamilyFile_Helvetica_MapsToTheSameCandidatesAsArial()
        {
            GivenFonts("LiberationSans-Regular.ttf");

            string helvetica = FontFileLocator.FindFamilyFile("Helvetica", bold: false, italic: false);
            string arial = FontFileLocator.FindFamilyFile("Arial", bold: false, italic: false);

            Assert.Equal("LiberationSans-Regular.ttf", NameOf(helvetica));
            Assert.Equal(arial, helvetica);
        }

        [Fact]
        public void FindFamilyFile_PrefersWindowsStem_WhenBothArePresent()
        {
            // "arial" leads SansStems, so a real Arial beats the substitute where it exists.
            GivenFonts("LiberationSans-Regular.ttf", "arial.ttf");

            string resolved = FontFileLocator.FindFamilyFile("Arial", bold: false, italic: false);

            Assert.Equal("arial.ttf", NameOf(resolved));
        }

        [Fact]
        public void FindFamilyFile_LiberationAskedForByName_IsAnsweredWithLiberation_NotArial()
        {
            // "arial" leads SansStems; the family asked for by its own name is promoted ahead of it.
            GivenFonts("arial.ttf", "LiberationSans-Regular.ttf");

            Assert.Equal("LiberationSans-Regular.ttf", NameOf(FontFileLocator.FindFamilyFile("Liberation Sans", bold: false, italic: false)));
            // Controls: Arial's own list still leads with Arial, and Helvetica — no file of its own
            // here — still takes the list's head.
            Assert.Equal("arial.ttf", NameOf(FontFileLocator.FindFamilyFile("Arial", bold: false, italic: false)));
            Assert.Equal("arial.ttf", NameOf(FontFileLocator.FindFamilyFile("Helvetica", bold: false, italic: false)));
        }

        [Fact]
        public void FindFamilyFile_ARegisteredDirectorysFile_WinsATieWithTheHosts_WhateverThePathsSort()
        {
            // The registered directory's name sorts AFTER the host's ("z-" against a hex guid), so
            // the ordinal tie-break alone would choose the host's file: the control below shows it does.
            GivenFonts("LiberationSans-Regular.ttf");
            string registered = Path.Combine(Path.GetTempPath(), "tetitulz-fontlocator-z-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(registered);
            string copy = Path.Combine(registered, "LiberationSans-Regular.ttf");
            File.WriteAllBytes(copy, Array.Empty<byte>());
            try
            {
                FontSearchPath.Add(registered);
                Assert.Equal(copy, FontFileLocator.FindFamilyFile("Liberation Sans", bold: false, italic: false), StringComparer.OrdinalIgnoreCase);

                FontSearchPath.Remove(registered);
                Assert.Equal(Path.Combine(_dir, "LiberationSans-Regular.ttf"),
                    FontFileLocator.FindFamilyFile("Liberation Sans", bold: false, italic: false), StringComparer.OrdinalIgnoreCase);
            }
            finally
            {
                FontSearchPath.Remove(registered);
                try { Directory.Delete(registered, recursive: true); } catch (IOException) { }
            }
        }

        // ── style selection ─────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(true, false, "LiberationSans-Bold.ttf")]
        [InlineData(false, true, "LiberationSans-Italic.ttf")]
        [InlineData(true, true, "LiberationSans-BoldItalic.ttf")]
        [InlineData(false, false, "LiberationSans-Regular.ttf")]
        public void FindFamilyFile_SelectsTheRequestedStyle(bool bold, bool italic, string expected)
        {
            GivenFonts(
                "LiberationSans-Regular.ttf",
                "LiberationSans-Bold.ttf",
                "LiberationSans-Italic.ttf",
                "LiberationSans-BoldItalic.ttf");

            string resolved = FontFileLocator.FindFamilyFile("Arial", bold, italic);

            Assert.Equal(expected, NameOf(resolved));
        }

        [Theory]
        [InlineData(true, false, "arialbd.ttf")]
        [InlineData(false, true, "ariali.ttf")]
        [InlineData(true, true, "arialbi.ttf")]
        [InlineData(false, false, "arial.ttf")]
        public void FindFamilyFile_UnderstandsWindowsStyleSuffixes(bool bold, bool italic, string expected)
        {
            // Windows abbreviates: arialbd / ariali / arialbi. A matcher that only knew the
            // "-Bold" spelling would silently return regular Arial for every bold run.
            GivenFonts("arial.ttf", "arialbd.ttf", "ariali.ttf", "arialbi.ttf");

            string resolved = FontFileLocator.FindFamilyFile("Arial", bold, italic);

            Assert.Equal(expected, NameOf(resolved));
        }

        [Fact]
        public void FindFamilyFile_UnderstandsObliqueAsItalic()
        {
            // DejaVu names its slanted faces "Oblique", not "Italic".
            GivenFonts("DejaVuSans.ttf", "DejaVuSans-Oblique.ttf", "DejaVuSans-BoldOblique.ttf");

            string italic = FontFileLocator.FindFamilyFile("DejaVu Sans", bold: false, italic: true);
            string boldItalic = FontFileLocator.FindFamilyFile("DejaVu Sans", bold: true, italic: true);

            Assert.Equal("DejaVuSans-Oblique.ttf", NameOf(italic));
            Assert.Equal("DejaVuSans-BoldOblique.ttf", NameOf(boldItalic));
        }

        [Fact]
        public void FindFamilyFile_FallsBackToRegular_WhenTheStyledFaceIsMissing()
        {
            GivenFonts("LiberationSans-Regular.ttf");

            string resolved = FontFileLocator.FindFamilyFile("Arial", bold: true, italic: true);

            Assert.Equal("LiberationSans-Regular.ttf", NameOf(resolved));
        }

        [Fact]
        public void FindFamilyFile_NeverReturnsAStyledFace_ForARegularRequest()
        {
            // Substituting bold for regular would be visibly wrong on every page; returning
            // nothing lets the caller fall through to its next candidate instead.
            GivenFonts("LiberationSans-Bold.ttf");

            string resolved = FontFileLocator.FindFamilyFile("Arial", bold: false, italic: false);

            Assert.Null(resolved);
        }

        // ── family-boundary rejection ───────────────────────────────────────────────────────

        [Fact]
        public void FindFamilyFile_DoesNotMatchADifferentFamilyBySharedPrefix()
        {
            // "DejaVuSansMono" starts with "DejaVuSans". Accepting it would render body text in a
            // monospace face — the exact failure a naive StartsWith match produces.
            GivenFonts("DejaVuSansMono.ttf");

            string resolved = FontFileLocator.FindFamilyFile("DejaVu Sans", bold: false, italic: false);

            Assert.Null(resolved);
        }

        [Fact]
        public void FindFamilyFile_CourierNew_DoesNotClaimLiberationSansCondensed()
        {
            GivenFonts("LiberationSansNarrow-Regular.ttf");

            string resolved = FontFileLocator.FindFamilyFile("Courier New", bold: false, italic: false);

            Assert.Null(resolved);
        }

        [Fact]
        public void FindFamilyFile_ReturnsNull_WhenNothingOnTheHostMatches()
        {
            GivenFonts("SomeUnrelatedFont.ttf");

            Assert.Null(FontFileLocator.FindFamilyFile("Arial", bold: false, italic: false));
            Assert.Null(FontFileLocator.FindFamilyFile("Wingdings 3", bold: false, italic: false));
        }

        [Fact]
        public void FindFamilyFile_ReturnsNull_ForNullOrEmptyFamily()
        {
            GivenFonts("LiberationSans-Regular.ttf");

            Assert.Null(FontFileLocator.FindFamilyFile(null, bold: false, italic: false));
            Assert.Null(FontFileLocator.FindFamilyFile("", bold: false, italic: false));
        }

        // ── arbitrary (non standard-14) families ────────────────────────────────────────────

        [Fact]
        public void FindFamilyFile_MatchesAnUnmappedFamilyByFilename_IgnoringSpaces()
        {
            // The renderer's CJK and symbol candidate lists pass family names that are not in the
            // standard-14 table; they have to resolve by filename or they do nothing off Windows.
            GivenFonts("NotoSansCJKjp-Regular.otf");

            string resolved = FontFileLocator.FindFamilyFile("Noto Sans CJK jp", bold: false, italic: false);

            Assert.Equal("NotoSansCJKjp-Regular.otf", NameOf(resolved));
        }

        [Fact]
        public void FindFamilyFile_FindsFontsInNestedDirectories()
        {
            // Linux distributions nest by foundry: /usr/share/fonts/truetype/liberation/...
            string nested = Path.Combine(_dir, "truetype", "liberation");
            Directory.CreateDirectory(nested);
            File.WriteAllBytes(Path.Combine(nested, "LiberationSans-Regular.ttf"), Array.Empty<byte>());
            Environment.SetEnvironmentVariable(FontFileLocator.FontDirEnvVar, _dir);
            FontFileLocator.ResetCache();

            string resolved = FontFileLocator.FindFamilyFile("Arial", bold: false, italic: false);

            Assert.Equal("LiberationSans-Regular.ttf", NameOf(resolved));
        }

        // ── directory + enumeration plumbing ────────────────────────────────────────────────

        [Fact]
        public void Directories_IncludeTheEnvironmentOverride_First()
        {
            GivenFonts("LiberationSans-Regular.ttf");

            IReadOnlyList<string> dirs = FontFileLocator.Directories;

            Assert.NotEmpty(dirs);
            Assert.Equal(_dir, dirs[0]);
        }

        [Fact]
        public void Directories_DropAnOverrideThatDoesNotExist()
        {
            string missing = Path.Combine(_dir, "no-such-directory");
            Environment.SetEnvironmentVariable(FontFileLocator.FontDirEnvVar, missing);
            FontFileLocator.ResetCache();

            Assert.DoesNotContain(missing, FontFileLocator.Directories);
        }

        [Fact]
        public void EnumerateFontFiles_ExcludesOpenType_WhenTheCallerCannotConsumeCff()
        {
            GivenFonts("LiberationSans-Regular.ttf", "SomeFont.otf", "Collection.ttc");

            var withOtf = FontFileLocator.EnumerateFontFiles(includeOpenType: true)
                .Select(Path.GetFileName).ToList();
            var withoutOtf = FontFileLocator.EnumerateFontFiles(includeOpenType: false)
                .Select(Path.GetFileName).ToList();

            Assert.Contains("SomeFont.otf", withOtf);
            Assert.DoesNotContain("SomeFont.otf", withoutOtf);

            // Both lists still carry the TrueType formats.
            Assert.Contains("LiberationSans-Regular.ttf", withoutOtf);
            Assert.Contains("Collection.ttc", withoutOtf);
        }

        // ── the two matchers answer with one scoring rule ───────────────────────────────────

        /// <summary>
        /// Points the locator at two roots, in the order given, each holding the named files.
        /// Two roots rather than one directory because the enumeration order <b>between</b> roots
        /// is the search path's order and therefore deterministic, while the order of entries
        /// <i>inside</i> one directory is the filesystem's business.
        /// </summary>
        private void GivenFontsInTwoRoots(string firstRoot, string secondRoot, params string[] fileNames)
        {
            string a = Path.Combine(_dir, firstRoot);
            string b = Path.Combine(_dir, secondRoot);
            Directory.CreateDirectory(a);
            Directory.CreateDirectory(b);
            foreach (string name in fileNames)
            {
                File.WriteAllBytes(Path.Combine(a, name), Array.Empty<byte>());
                File.WriteAllBytes(Path.Combine(b, name), Array.Empty<byte>());
            }

            Environment.SetEnvironmentVariable(
                FontFileLocator.FontDirEnvVar, a + Path.PathSeparator + b);
            FontFileLocator.ResetCache();
        }

        /// <summary>
        /// <b>The invariant <c>MatchStems</c>' doc comment claims</b>: the best file
        /// <see cref="FontFileLocator.FindFamilyFile"/> answers with is the first element of
        /// <see cref="FontFileLocator.FindFamilyFiles"/> for the same family and style.
        ///
        /// <para>Two files can tie on score — the same filename under two roots is the everyday
        /// case, and a per-user font directory shadowing a system one is exactly that. The two
        /// matchers then had to break the tie the same way, and they did not: one kept the
        /// first file the search path yielded, the other sorted the ties by ordinal path
        /// "stable across hosts". So the ranked list's head was not the single answer, and which
        /// face a page rendered in depended on the order the font directories were listed in.</para>
        /// </summary>
        [Fact]
        public void FindFamilyFile_AnswersTheHeadOfFindFamilyFiles_WhenTwoFilesTie()
        {
            // "zzz" first in the search path, so enumeration order and ordinal path order disagree.
            GivenFontsInTwoRoots("zzz", "aaa", "arial.ttf");

            string single = FontFileLocator.FindFamilyFile("Arial", bold: false, italic: false);
            IReadOnlyList<string> ranked =
                FontFileLocator.FindFamilyFiles("Arial", bold: false, italic: false, max: 8);

            Assert.Equal(2, ranked.Count);
            Assert.Equal(ranked[0], single);
        }

        /// <summary>
        /// <b>The direction, not only the agreement.</b> Asserting equality alone would
        /// be satisfied by both matchers moving to search-path order, which is the host-dependent
        /// answer the ranked list's tie-break was written to avoid. The winner is the ordinally
        /// first path.
        /// </summary>
        [Fact]
        public void FindFamilyFile_BreaksATieDeterministically_NotBySearchPathOrder()
        {
            GivenFontsInTwoRoots("zzz", "aaa", "arial.ttf");

            string resolved = FontFileLocator.FindFamilyFile("Arial", bold: false, italic: false);

            Assert.Equal(Path.Combine(_dir, "aaa", "arial.ttf"), resolved);
        }

        /// <summary>
        /// The scoring itself still ranks: a tie is broken by path, but a better score always wins
        /// whatever order the roots are listed in. Without this the two tests above would pass on a
        /// matcher that had forgotten how to rank at all and just sorted paths.
        /// </summary>
        [Fact]
        public void FindFamilyFile_PrefersTheBetterScore_OverTheOrdinallyFirstPath()
        {
            // Both roots hold the regular face; only "zzz" holds the bold one, and a bold request
            // must take it even though every "aaa" path sorts first.
            string a = Path.Combine(_dir, "aaa");
            string z = Path.Combine(_dir, "zzz");
            Directory.CreateDirectory(a);
            Directory.CreateDirectory(z);
            File.WriteAllBytes(Path.Combine(a, "LiberationSans-Regular.ttf"), Array.Empty<byte>());
            File.WriteAllBytes(Path.Combine(z, "LiberationSans-Regular.ttf"), Array.Empty<byte>());
            File.WriteAllBytes(Path.Combine(z, "LiberationSans-Bold.ttf"), Array.Empty<byte>());
            Environment.SetEnvironmentVariable(
                FontFileLocator.FontDirEnvVar, a + Path.PathSeparator + z);
            FontFileLocator.ResetCache();

            string bold = FontFileLocator.FindFamilyFile("Arial", bold: true, italic: false);

            Assert.Equal(Path.Combine(z, "LiberationSans-Bold.ttf"), bold);
        }

        /// <summary>
        /// Within one rank the shorter style suffix wins — the second half of the score, and the
        /// half nothing reached until a mutation deleting <c>+ rest.Length</c> survived the whole
        /// suite. The example the code gave for the rule ("arial" beats "arialunicodems") is
        /// decided a line earlier, by the style-token test that rejects "unicodems" outright.
        ///
        /// <para>"arial-Regular" normalises to a recognised <c>regular</c> suffix, so it does
        /// compete, and it sorts <i>before</i> "arial.ttf" by ordinal path ('-' &lt; '.') — so a
        /// scorer that had forgotten the suffix length would answer with it.</para>
        /// </summary>
        [Fact]
        public void FindFamilyFile_PrefersTheShorterStyleSuffix_WithinARank()
        {
            GivenFonts("arial.ttf", "arial-Regular.ttf");

            string resolved = FontFileLocator.FindFamilyFile("Arial", bold: false, italic: false);

            Assert.Equal("arial.ttf", NameOf(resolved));
        }

        [Fact]
        public void ResetCache_PicksUpFontsInstalledAfterTheFirstScan()
        {
            GivenFonts("SomeUnrelatedFont.ttf");
            Assert.Null(FontFileLocator.FindFamilyFile("Arial", bold: false, italic: false));

            File.WriteAllBytes(Path.Combine(_dir, "LiberationSans-Regular.ttf"), Array.Empty<byte>());

            // Still the cached "not found" — the negative answer is deliberately cached.
            Assert.Null(FontFileLocator.FindFamilyFile("Arial", bold: false, italic: false));

            FontFileLocator.ResetCache();

            Assert.Equal("LiberationSans-Regular.ttf",
                NameOf(FontFileLocator.FindFamilyFile("Arial", bold: false, italic: false)));
        }
    }
}
