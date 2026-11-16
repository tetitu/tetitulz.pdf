// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Fonts.Tests;

/// <summary>
/// The payload and its door: sixteen TrueType files the library parses, with the glyphs each family
/// is bundled for; written once and whole; registered first in the search path and taken out again;
/// the three licence texts beside them. The search path's own behaviour is Core's
/// (<c>FontSearchPathTests</c>); this suite asks whether this package feeds it the right things.
/// </summary>
[Collection("BundledFonts")]   // the search path is process-global
public class BundledFontsTests
{
    private static readonly string[] ExpectedFiles =
    {
        "LiberationMono-Bold.ttf", "LiberationMono-BoldItalic.ttf", "LiberationMono-Italic.ttf", "LiberationMono-Regular.ttf",
        "LiberationSans-Bold.ttf", "LiberationSans-BoldItalic.ttf", "LiberationSans-Italic.ttf", "LiberationSans-Regular.ttf",
        "LiberationSerif-Bold.ttf", "LiberationSerif-BoldItalic.ttf", "LiberationSerif-Italic.ttf", "LiberationSerif-Regular.ttf",
        "NotoSansArabic-Bold.ttf", "NotoSansArabic-Regular.ttf",
        "NotoSansHebrew-Bold.ttf", "NotoSansHebrew-Regular.ttf",
    };

    [Fact]
    public void ThePackageCarries_TheSixteenFonts_AndThreeLicences()
    {
        Assert.Equal(ExpectedFiles, BundledFonts.FileNames);
        Assert.Equal(new[] { "LICENSE-Liberation-OFL.txt", "LICENSE-NotoSansArabic-OFL.txt", "LICENSE-NotoSansHebrew-OFL.txt" },
            BundledFonts.LicenceFileNames);
    }

    [Fact]
    public void Extract_WritesEveryFontAndLicence_OnceAndWhole_AndRepairsAChangedFile()
    {
        string dir = TempDir();
        try
        {
            string full = BundledFonts.Extract(dir);
            Assert.Equal(Path.GetFullPath(dir), full);
            foreach (string name in ExpectedFiles) Assert.True(File.Exists(Path.Combine(full, name)), name);
            foreach (string licence in BundledFonts.LicenceFileNames)
                Assert.Contains("SIL OPEN FONT LICENSE Version 1.1", File.ReadAllText(Path.Combine(full, licence)), StringComparison.Ordinal);

            // Every file is a TrueType program the library parses, with the glyphs its family is here for.
            var sans = TrueTypeFontProgram.Parse(File.ReadAllBytes(Path.Combine(full, "LiberationSans-Regular.ttf")));
            Assert.True(sans.Covers('A') && sans.Covers('é'));
            var hebrew = TrueTypeFontProgram.Parse(File.ReadAllBytes(Path.Combine(full, "NotoSansHebrew-Regular.ttf")));
            Assert.True(hebrew.Covers('א') && hebrew.Covers('ְ'));      // alef, sheva
            var arabic = TrueTypeFontProgram.Parse(File.ReadAllBytes(Path.Combine(full, "NotoSansArabic-Regular.ttf")));
            Assert.True(arabic.Covers('ا') && arabic.CoversShaped('ب'));  // alef; beh through its joining forms

            // Idempotent: a second extract rewrites nothing.
            var stamps = ExpectedFiles.ToDictionary(n => n, n => File.GetLastWriteTimeUtc(Path.Combine(full, n)));
            BundledFonts.Extract(dir);
            foreach (string name in ExpectedFiles)
                Assert.Equal(stamps[name], File.GetLastWriteTimeUtc(Path.Combine(full, name)));

            // A damaged file is written again.
            string damaged = Path.Combine(full, "LiberationMono-Italic.ttf");
            long length = new FileInfo(damaged).Length;
            File.WriteAllBytes(damaged, new byte[16]);
            BundledFonts.Extract(dir);
            Assert.Equal(length, new FileInfo(damaged).Length);
            Assert.Equal(16, ExpectedFiles.Length);

            // A file of the bundled font's length and other bytes — what another account could plant in a
            // shared temporary directory — is written again too: the bytes are compared, not the length.
            byte[] bundled = File.ReadAllBytes(damaged);
            byte[] planted = (byte[])bundled.Clone();
            planted[planted.Length / 2] ^= 0xFF;
            File.WriteAllBytes(damaged, planted);
            BundledFonts.Extract(dir);
            Assert.Equal(bundled, File.ReadAllBytes(damaged));
        }
        finally { Delete(dir); }
    }

    [Fact]
    public void Register_PutsTheDirectoryFirstInTheSearchPath_AndUnregisterTakesItOut()
    {
        string dir = TempDir();
        try
        {
            string full = BundledFonts.Register(dir);

            Assert.Equal(full, BundledFonts.Directory);
            Assert.Equal(full, FontSearchPath.Directories[0]);

            BundledFonts.Unregister();
            Assert.Null(BundledFonts.Directory);
            Assert.DoesNotContain(FontSearchPath.Directories, d => d == full);
            Assert.True(File.Exists(Path.Combine(full, "LiberationSans-Regular.ttf")), "the files stay for the next start");
        }
        finally { BundledFonts.Unregister(); Delete(dir); }
    }

    /// <summary>
    /// The point of the package, through the one resolver every producer uses: with the directory
    /// registered, Liberation Sans Bold resolves to the bundled file and a Hebrew letter to the
    /// bundled Noto Sans Hebrew — files under this directory, whatever the host has installed (a
    /// host's own Liberation would be enumerated after it). The control: once the directory is taken
    /// out, neither resolution comes from it. Helvetica itself is not asked for: where the host has
    /// Arial, the locator's stem ranking prefers it to Liberation by design, so that answer would
    /// describe the machine, not the package.
    /// </summary>
    [Fact]
    public void WithTheDirectoryRegistered_TheResolverFindsTheBundledFaces()
    {
        string dir = TempDir();
        try
        {
            string full = BundledFonts.Register(dir);

            var sans = Resolve("Liberation Sans", bold: true, (int)'A');
            Assert.True(sans.Success, sans.FailureReason);
            Assert.StartsWith(full, sans.SourcePath, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("LiberationSans-Bold.ttf", sans.SourcePath, StringComparison.Ordinal);

            var hebrew = Resolve("Noto Sans Hebrew", bold: false, 0x05D0, 0x05B0);
            Assert.True(hebrew.Success, hebrew.FailureReason);
            Assert.StartsWith(full, hebrew.SourcePath, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("NotoSansHebrew-Regular.ttf", hebrew.SourcePath, StringComparison.Ordinal);

            BundledFonts.Unregister();
            foreach (var after in new[] { Resolve("Liberation Sans", bold: true, (int)'A'), Resolve("Noto Sans Hebrew", bold: false, 0x05D0, 0x05B0) })
                if (after.Success)
                    Assert.False(after.SourcePath.StartsWith(full, StringComparison.OrdinalIgnoreCase), after.SourcePath);
        }
        finally { BundledFonts.Unregister(); Delete(dir); }
    }

    private static FontResolution Resolve(string family, bool bold, params int[] codePoints) =>
        SystemFontResolver.Resolve(new FontResolutionRequest
        {
            FamilyHint = family, Bold = bold, RequiredCodePoints = codePoints, AllowSystemFontSubstitution = true,
        }, fontProvider: null);

    [Fact]
    public void ThePlugin_RegistersTheDefaultDirectory_AndShutdownUnregistersIt()
    {
        var plugin = new BundledFontsPlugin();
        try
        {
            plugin.Initialize();
            Assert.Equal(BundledFonts.DefaultDirectory(), BundledFonts.Directory);
            Assert.Equal(BundledFonts.DefaultDirectory(), FontSearchPath.Directories[0]);
            Assert.Contains("tetitulz-pdf-fonts", BundledFonts.DefaultDirectory(), StringComparison.Ordinal);
            Assert.Equal("Tetitulz.Plugins.Fonts", plugin.Name);
        }
        finally
        {
            plugin.Shutdown();
        }
        Assert.Null(BundledFonts.Directory);
    }

    /// <summary>
    /// The directory is searched first, so it is the account's own: under its application-data
    /// folder, where no other account writes, and not a well-known name in the temporary directory.
    /// </summary>
    [Fact]
    public void TheDefaultDirectory_IsUnderTheAccountsOwnDataFolder()
    {
        string data = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        Assert.False(string.IsNullOrEmpty(data), "The test account has no application-data folder to assert about.");

        Assert.StartsWith(Path.Combine(data, "tetitulz-pdf-fonts") + Path.DirectorySeparatorChar,
            BundledFonts.DefaultDirectory(), StringComparison.Ordinal);
    }

    /// <summary>
    /// An account whose data folder cannot be written — here a path under a file — or that has none
    /// gets a directory this process made: under the temporary directory, by a name no one could
    /// have made first, holding every font, registered first, and the same one however often it is
    /// asked for.
    /// </summary>
    [Fact]
    public void WithNoDataFolderItCanWrite_RegisterUsesADirectoryThisProcessMade()
    {
        string notAFolder = Path.Combine(Path.GetTempPath(), "tetitulz-not-a-folder-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(notAFolder, "a file where a folder is asked for");
        try
        {
            string made = BundledFonts.RegisterUnder(notAFolder);

            Assert.False(made.StartsWith(notAFolder, StringComparison.OrdinalIgnoreCase), made);
            Assert.StartsWith("tetitulz-pdf-fonts-", Path.GetFileName(made), StringComparison.Ordinal);
            Assert.NotEqual("tetitulz-pdf-fonts", Path.GetFileName(Path.GetDirectoryName(made)));
            Assert.Equal(
                Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(Path.GetDirectoryName(made)!).TrimEnd(Path.DirectorySeparatorChar),
                ignoreCase: OperatingSystem.IsWindows());
            foreach (string name in ExpectedFiles) Assert.True(File.Exists(Path.Combine(made, name)), name);
            Assert.Equal(made, FontSearchPath.Directories[0]);

            Assert.Equal(made, BundledFonts.RegisterUnder(null));
        }
        finally
        {
            BundledFonts.Unregister();
            File.Delete(notAFolder);
        }
    }

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "tetitulz-bundled-fonts-" + Guid.NewGuid().ToString("N"));

    private static void Delete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }
}
