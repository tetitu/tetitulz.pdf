// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.DocxToPdf.Rendering;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// Two independent defences against <see cref="FontResolver"/>'s process-wide file cache growing
    /// without limit (GAPS §16 item 4b), each tested for what it actually contributes.
    ///
    /// <para><b>What went wrong.</b> <c>CandidateFiles</c> falls back to matching every installed
    /// font's family name whenever the requested family is outside its 17-entry filename map, and
    /// <c>FindAnyCovering</c> walks every font when nothing covers the run's text. Both used to
    /// <c>LoadFile</c> each candidate — a full read whose <c>FontData</c> the cache then held for the
    /// process lifetime, since <c>Parse</c> keeps the whole file for <c>/FontFile2</c> embedding.
    /// Measured at <b>210 MiB</b> after one one-paragraph conversion.</para>
    ///
    /// <para><b>Why two tests.</b> The ceiling and the probe fix different halves, and one test would
    /// let either rot unnoticed. <see cref="OneParagraph_RejectsFontsWithoutReadingThem"/> is about the
    /// probe and fails if it stops being consulted; <see cref="LoadingManyFonts_StopsAtTheCeiling"/> is
    /// about the bound, loads fonts by path so the probe cannot help, and fails if the ceiling stops
    /// clamping. Before the probe existed one test covered both — and once the probe landed that test
    /// passed with the bound deleted, its name still fitting while half its evidence had moved
    /// elsewhere. Both mutations are verified separately now.</para>
    ///
    /// <para><b>Host-dependent, stated rather than papered over.</b> The ceiling can only be
    /// <i>exceeded</i> where more than <see cref="FontResolver.MaxCachedFontBytes"/> of fonts are
    /// installed, so on a bare container the second test is vacuously true. Both assertions are
    /// invariants, so a small host cannot turn them green by accident. No cache reset appears here
    /// either: clearing a process-wide font cache is what made <c>FontFileLocatorTests</c> break
    /// <c>SystemFontResolverTests</c> (GAPS §14).</para>
    /// </summary>
    public class FontCacheBoundTests
    {
        private static byte[] OneParagraph(string text)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var body = new Body();
                body.Append(new Paragraph(new Run(new Text(text))));
                main.Document = new Document(body);
                main.Document.Save();
            }
            return ms.ToArray();
        }

        /// <summary>
        /// The probe's contribution: a conversion that has to consult every installed font must reject
        /// them cheaply rather than read them.
        ///
        /// <para><b>Why this counts rejections instead of bytes.</b> The first draft asserted that the
        /// conversion added little to <see cref="FontResolver.ChargedBytes"/>, and it was vacuous: its
        /// sibling <see cref="LoadingManyFonts_StopsAtTheCeiling"/> deliberately fills the same
        /// process-wide cache to the ceiling, after which no conversion can charge anything and the
        /// assertion passes with the probe deleted. Mutation is what exposed it. A rejection is counted
        /// whether or not the path was already cached, so this does not depend on test order — the trap
        /// GAPS §14 records, met here between two tests in one file.</para>
        /// </summary>
        /// <remarks>
        /// The fonts are <see cref="HostFonts"/>' generated set rather than the machine's, so the
        /// figure is exact on every host: a Linux runner carries a fraction of a Windows machine's
        /// fonts, and a bound sized for one says nothing on the other.
        /// </remarks>
        [Fact]
        public void OneParagraph_RejectsFontsWithoutReadingThem()
        {
            // U+12000 CUNEIFORM SIGN A: none of the generated fonts maps it, so neither the
            // family-name match nor the coverage walk can stop early — the walk reaches every font,
            // and every one of them must be rejected cheaply.
            byte[] docx = OneParagraph("𒀀𒀁𒀂");

            using var scratch = new ScratchDirectory();
            string[] fonts = HostFonts(scratch.Path);
            long rejected;
            using (FontResolver.UseHostFontFilesForTest(fonts))
            {
                long before = FontResolver.ProbeRejections;
                byte[] pdf = DocxToPdfConverter.ConvertToBytes(docx, new DocxToPdfOptions());
                Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(pdf, 0, 5));
                rejected = FontResolver.ProbeRejections - before;
            }

            Assert.True(rejected >= fonts.Length,
                $"the scan rejected only {rejected} of {fonts.Length} fonts through FontCoverageProbe. " +
                "None covers cuneiform, so the walk must reach every font and reject each from the sfnt " +
                "directory plus one table. A figure below the count means the probe is not consulted and " +
                "the scan is back to a full read per candidate (GAPS §16 item 4a).");
        }

        /// <summary>
        /// Forty fonts in <paramref name="directory"/>, each mapping only <c>A</c> and <c>B</c>, each
        /// named its own family; their file names match none of the resolver's preferred stems.
        /// </summary>
        private static string[] HostFonts(string directory)
        {
            var paths = new string[40];
            for (int i = 0; i < paths.Length; i++)
            {
                paths[i] = Path.Combine(directory, "probe-face-" + i + ".ttf");
                File.WriteAllBytes(paths[i], Tetitulz.Pdf.TestHelpers.MinimalTrueType.BuildNamed("AB", "Probe Face " + i));
            }
            return paths;
        }

        /// <summary>
        /// The family-name half of the probe, which is a different loop from the coverage walk and so
        /// needs its own evidence.
        ///
        /// <para>GAPS §16 item 4a shipped with a note saying this wiring was covered by nothing — the
        /// coverage counter does not move for it, so a regression that stopped consulting
        /// <c>FamilyName</c> would have been silent. This closes that.</para>
        ///
        /// <para><b>The fixture took two attempts and the first one is the lesson.</b> A family outside
        /// <c>KnownFiles</c> is necessary but not sufficient: with <b>Latin</b> text the resolver is
        /// never consulted at all, because DocxToPdf keeps the standard-14 WinAnsi path and only asks
        /// for a font program when the text cannot be encoded that way. So the first version measured
        /// zero and would have "passed" as soon as the threshold was lowered to match — a fixture that
        /// never reaches the feature, which is banned pattern 3 wearing a different hat. Hebrew forces
        /// the resolution, and a family this host cannot have forces the walk to run to completion
        /// instead of stopping at a match.</para>
        ///
        /// <para><b>The host's own fonts, counted.</b> The requested-family walk is the step a supplied
        /// font list skips, so this one runs over the machine's fonts — and asks the probe itself how
        /// many of them it can name, so the bound is exact wherever it runs: a Linux runner carries a
        /// fraction of a Windows machine's fonts, and a fixed figure sized for one says nothing on the
        /// other.</para>
        /// </summary>
        [Fact]
        public void AFamilyOutsideTheFilenameMap_IsMatchedFromTheNameTableAlone()
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var body = new Body();
                // Hebrew: outside WinAnsi, so a real font program has to be resolved. The family is
                // deliberately one no host installs, so the family-name walk cannot stop early on a
                // match -- it has to name every installed font, which is exactly the cost under test.
                var run = new Run(new Text("שלום עולם"));
                run.RunProperties = new RunProperties(
                    new RunFonts { Ascii = "Zzz No Such Family", HighAnsi = "Zzz No Such Family",
                                   ComplexScript = "Zzz No Such Family" });
                body.Append(new Paragraph(run));
                main.Document = new Document(body);
                main.Document.Save();
            }

            // What the walk visits — the resolver's own enumeration — and how many of those the probe
            // names from the `name` table alone.
            int nameable = Tetitulz.Pdf.Core.Fonts.FontFileLocator.EnumerateFontFiles(includeOpenType: true)
                .Count(p => Tetitulz.Pdf.Core.Fonts.FontCoverageProbe.FamilyName(p) != null);
            Assert.True(nameable > 0, "this host has no font the probe can name, so the walk has nothing to show");

            long before = FontResolver.ProbeFamilyNames;
            byte[] pdf = DocxToPdfConverter.ConvertToBytes(ms.ToArray(), new DocxToPdfOptions());
            Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(pdf, 0, 5));

            long named = FontResolver.ProbeFamilyNames - before;
            Assert.True(named >= nameable,
                $"only {named} of the {nameable} fonts the probe can name were named from the `name` table. " +
                "A family outside the 17-entry KnownFiles map has to be found by walking every installed " +
                "font's family name, and FontCoverageProbe.FamilyName is what makes that cheap — a figure " +
                "below the count means the walk is back to a full read per candidate (GAPS §16 item 4a).");
        }

        /// <summary>
        /// The ceiling's contribution, measured where the probe cannot help: loading fonts by path is
        /// a caller saying "I want this file", so there is nothing to reject and the bound is the only
        /// thing standing between the cache and the whole collection.
        /// </summary>
        [Fact]
        public void LoadingManyFonts_StopsAtTheCeiling()
        {
            string[] installed = InstalledFontFiles();

            // Enough bytes to matter, or the assertion below cannot be exceeded and says nothing.
            // Not a skip: the invariant is still worth asserting, and a small host cannot fake it.
            foreach (string path in installed)
                FontResolver.LoadFontFileForTest(path);

            Assert.True(FontResolver.ChargedBytes <= FontResolver.MaxCachedFontBytes,
                $"after loading {installed.Length} fonts the cache holds " +
                $"{FontResolver.ChargedBytes / 1024 / 1024} MiB, over the " +
                $"{FontResolver.MaxCachedFontBytes / 1024 / 1024} MiB ceiling — it is retaining every " +
                "font it was handed, which is the leak-shaped footprint of GAPS §16 item 4b");
        }

        /// <summary>
        /// The mistake that would be worse than the leak: a cache that stops accepting entries and
        /// stops <i>returning</i> them, so a font resolved after the ceiling loses its glyphs.
        /// </summary>
        [Fact]
        public void PastTheCeiling_TheProgramIsStillReturned()
        {
            string path = Path.Combine(Path.GetTempPath(),
                "tetitulz-cachebound-" + Guid.NewGuid().ToString("N") + ".ttf");
            try
            {
                File.WriteAllBytes(path, Tetitulz.Pdf.TestHelpers.MinimalTrueType.Build("AB"));

                // Two calls: the first may or may not be cached depending on how full the process's
                // cache already is, and the second must agree with it either way. "Cached" and
                // "returned" are different questions and only the second one is a contract.
                var first = FontResolver.LoadFontFileForTest(path);
                var second = FontResolver.LoadFontFileForTest(path);

                Assert.NotNull(first);
                Assert.NotNull(second);
                Assert.Equal(first!.UnitsPerEm, second!.UnitsPerEm);
                Assert.True(first.FontData.Length > 0,
                    "the loader returned a program with no bytes, so it could never be embedded");
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        /// <summary>A fresh directory under the temp path, deleted with what it holds.</summary>
        private sealed class ScratchDirectory : IDisposable
        {
            public string Path { get; } = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "tetitulz-fontprobe-" + Guid.NewGuid().ToString("N"));

            public ScratchDirectory() => Directory.CreateDirectory(Path);

            public void Dispose()
            {
                try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private static string[] InstalledFontFiles()
        {
            var dirs = new System.Collections.Generic.List<string>();
            string shared = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            if (Directory.Exists(shared)) dirs.Add(shared);
            string perUser = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Windows", "Fonts");
            if (Directory.Exists(perUser)) dirs.Add(perUser);
            foreach (string linux in new[] { "/usr/share/fonts", "/usr/local/share/fonts" })
                if (Directory.Exists(linux)) dirs.Add(linux);

            var extensions = new[] { ".ttf", ".ttc", ".otf" };
            var found = new System.Collections.Generic.List<string>();
            foreach (string dir in dirs)
            {
                try
                {
                    found.AddRange(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                        .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant())));
                }
                catch { /* an unreadable font directory is not a test failure */ }
            }
            return found.ToArray();
        }
    }
}
