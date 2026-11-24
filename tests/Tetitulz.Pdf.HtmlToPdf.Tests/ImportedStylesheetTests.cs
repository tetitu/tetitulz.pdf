// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Tetitulz.Pdf.Builder.Export;
using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// A stylesheet's <c>@import</c> (CSS Cascade 4 §5, batch 96): the named sheet's rules stand at the
/// import's place — a local sheet's import beside it, a remote sheet's through the loader against
/// the sheet's URL, a <c>&lt;style&gt;</c> block's against the base directory — when its media list
/// applies; each sheet once per conversion, so a cycle ends; outside the base directory declined and
/// reported; a chain deeper than the nesting cap reported. Before batch 96 no <c>@import</c> was
/// followed at all.
/// </summary>
/// <remarks>
/// Every positive case reads the imported rule's colour (<c>#00ff00</c>, <c>0 1 0 rg</c>) out of the
/// content stream, with the same document minus the import as its control, so "applied" is the
/// imported sheet's doing and nothing else's.
/// </remarks>
public class ImportedStylesheetTests
{
    private const string Green = "0 1 0 rg";
    private const string GreenRule = ".x{color:#00ff00}";
    private const string Page = "<link rel='stylesheet' href='css/site.css'><p class='x'>Hello</p>";

    private sealed class ScriptedLoader : IRemoteResourceLoader
    {
        private readonly Func<Uri, byte[]?> _body;
        public List<Uri> Requested { get; } = new();
        public ScriptedLoader(Func<Uri, byte[]?> body) => _body = body;
        public byte[]? Load(Uri url, out string? failure) { Requested.Add(url); failure = null; return _body(url); }
    }

    private static (string Content, List<string> Reports) Convert(string html, Action<HtmlToPdfOptions> configure)
    {
        var reports = new List<string>();
        var o = Managed();
        o.OnDiagnostic = d => { lock (reports) reports.Add(d.Detail ?? ""); };
        configure(o);
        return (DecodeStreams(ToPdf(html, o)), reports);
    }

    [Theory]
    [InlineData("@import url(theme.css);")]
    [InlineData("@import url(\"theme.css\");")]
    [InlineData("@import \"theme.css\";")]
    [InlineData("@import 'theme.css' print;")]
    [InlineData("@import url(theme.css) layer(theme) supports(display: grid) print and (min-width: 1px);")]
    public void ALocalSheetsImport_ResolvesBesideIt_AndApplies(string import)
    {
        using var dir = NewScratchDir();
        dir.Write("css/site.css", Encoding.UTF8.GetBytes(import));
        dir.Write("css/theme.css", Encoding.UTF8.GetBytes(GreenRule));

        var (content, reports) = Convert(Page, o => o.BasePath = dir.Path);

        Assert.Contains(Green, content, StringComparison.Ordinal);
        Assert.Empty(reports);
    }

    /// <summary>The control: the same sheet without the import line paints nothing green.</summary>
    [Fact]
    public void WithoutTheImport_TheThemeIsNotApplied()
    {
        using var dir = NewScratchDir();
        dir.Write("css/site.css", Encoding.UTF8.GetBytes("p{margin:0}"));
        dir.Write("css/theme.css", Encoding.UTF8.GetBytes(GreenRule));

        var (content, _) = Convert(Page, o => o.BasePath = dir.Path);

        Assert.DoesNotContain(Green, content, StringComparison.Ordinal);
    }

    /// <summary>An import for another medium is not loaded: <c>screen</c> under the print default, <c>print</c> under screen.</summary>
    [Theory]
    [InlineData("@import url(theme.css) screen;", HtmlMediaType.Print)]
    [InlineData("@import url(theme.css) print;", HtmlMediaType.Screen)]
    [InlineData("@import url(theme.css) (min-width: 5000px);", HtmlMediaType.Print)]
    public void AnImportForAnotherMedium_IsNotApplied(string import, HtmlMediaType medium)
    {
        using var dir = NewScratchDir();
        dir.Write("css/site.css", Encoding.UTF8.GetBytes(import));
        dir.Write("css/theme.css", Encoding.UTF8.GetBytes(GreenRule));

        var (content, _) = Convert(Page, o => { o.BasePath = dir.Path; o.MediaType = medium; });

        Assert.DoesNotContain(Green, content, StringComparison.Ordinal);
    }

    /// <summary>
    /// The imported rules stand at the import's place in the cascade: a rule after the import in the
    /// importing sheet, of the same specificity, wins over the imported one — and one before it loses.
    /// </summary>
    [Fact]
    public void ImportedRules_TakeTheImportsPlaceInSourceOrder()
    {
        using var dir = NewScratchDir();
        dir.Write("css/theme.css", Encoding.UTF8.GetBytes(GreenRule));
        dir.Write("css/after.css", Encoding.UTF8.GetBytes("@import url(theme.css); .x{color:#ff0000}"));
        dir.Write("css/before.css", Encoding.UTF8.GetBytes(".x{color:#ff0000} @import url(theme.css);"));

        var (after, _) = Convert("<link rel='stylesheet' href='css/after.css'><p class='x'>Hello</p>", o => o.BasePath = dir.Path);
        var (before, _) = Convert("<link rel='stylesheet' href='css/before.css'><p class='x'>Hello</p>", o => o.BasePath = dir.Path);

        Assert.Contains("1 0 0 rg", after, StringComparison.Ordinal);
        Assert.DoesNotContain(Green, after, StringComparison.Ordinal);
        Assert.Contains(Green, before, StringComparison.Ordinal);
    }

    /// <summary>A <c>&lt;style&gt;</c> block's import resolves against the base directory, as its <c>url()</c>s do.</summary>
    [Fact]
    public void AStyleBlocksImport_ResolvesAgainstTheBaseDirectory()
    {
        using var dir = NewScratchDir();
        dir.Write("css/theme.css", Encoding.UTF8.GetBytes(GreenRule));

        var (content, reports) = Convert("<style>@import url(css/theme.css);</style><p class='x'>Hello</p>", o => o.BasePath = dir.Path);

        Assert.Contains(Green, content, StringComparison.Ordinal);
        Assert.Empty(reports);
    }

    /// <summary>
    /// An imported sheet's own string-form import resolves beside <i>that</i> sheet: the loader
    /// rebases a <c>url()</c> import to an absolute path before the rules are read, so only the
    /// string form can tell whether the imported sheet's location was carried down with its text.
    /// </summary>
    [Fact]
    public void ANestedStringFormImport_ResolvesBesideTheImportedSheet()
    {
        using var dir = NewScratchDir();
        dir.Write("css/site.css", Encoding.UTF8.GetBytes("@import url(a.css);"));
        dir.Write("css/a.css", Encoding.UTF8.GetBytes("@import \"theme.css\";"));
        dir.Write("css/theme.css", Encoding.UTF8.GetBytes(GreenRule));

        var (content, reports) = Convert(Page, o => o.BasePath = dir.Path);

        Assert.Contains(Green, content, StringComparison.Ordinal);
        Assert.Empty(reports);
    }

    /// <summary>A sheet that imports its importer, and one imported twice, is applied once: the conversion ends and the colour is there.</summary>
    [Fact]
    public void ACycle_EndsWhereItBegan_AndASheetIsImportedOnce()
    {
        using var dir = NewScratchDir();
        dir.Write("css/site.css", Encoding.UTF8.GetBytes("@import url(theme.css); @import url(theme.css);"));
        dir.Write("css/theme.css", Encoding.UTF8.GetBytes("@import url(site.css); " + GreenRule));

        var (content, reports) = Convert(Page, o => o.BasePath = dir.Path);

        Assert.Contains(Green, content, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(content, "0 1 0 rg").Cast<System.Text.RegularExpressions.Match>());
        Assert.Empty(reports);
    }

    /// <summary>An import that leaves the base directory is declined and reported, as a <c>&lt;link&gt;</c> there is.</summary>
    [Fact]
    public void AnImportOutsideTheBaseDirectory_IsDeclinedAndReported()
    {
        using var dir = NewScratchDir();
        dir.Write("css/site.css", Encoding.UTF8.GetBytes("@import url(../../outside.css);"));

        var (content, reports) = Convert(Page, o => o.BasePath = dir.Path);

        Assert.DoesNotContain(Green, content, StringComparison.Ordinal);
        string report = Assert.Single(reports);
        Assert.Contains("stylesheet", report, StringComparison.Ordinal);
        Assert.Contains("outside.css", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// Under a long base directory the report still names the file: the location is shortened in its
    /// middle, so a resolved URL some hundred characters long keeps <c>outside.css</c> at its end (a
    /// macOS temp folder alone is fifty characters).
    /// </summary>
    [Fact]
    public void AnImportOutsideALongBaseDirectory_IsReportedByItsFileName()
    {
        using var dir = NewScratchDir();
        string deep = new string('d', 70);
        dir.Write(deep + "/css/site.css", Encoding.UTF8.GetBytes("@import url(../../outside.css);"));

        var (content, reports) = Convert(Page, o => o.BasePath = System.IO.Path.Combine(dir.Path, deep));

        Assert.DoesNotContain(Green, content, StringComparison.Ordinal);
        string report = Assert.Single(reports);
        Assert.Contains("outside.css", report, StringComparison.Ordinal);
    }

    /// <summary>A chain of imports deeper than the nesting cap stops there, reported; the rules above the cap still apply.</summary>
    [Fact]
    public void AChainDeeperThanTheNestingCap_IsReported()
    {
        using var dir = NewScratchDir();
        for (int i = 0; i < 20; i++)
            dir.Write($"css/s{i}.css", Encoding.UTF8.GetBytes($"@import url(s{i + 1}.css); .y{i}{{color:#ff0000}}"));
        dir.Write("css/s20.css", Encoding.UTF8.GetBytes(GreenRule));

        var (content, reports) = Convert("<link rel='stylesheet' href='css/s0.css'><p class='x y0'>Hello</p>", o => o.BasePath = dir.Path);

        Assert.DoesNotContain(Green, content, StringComparison.Ordinal);
        Assert.Contains("1 0 0 rg", content, StringComparison.Ordinal);
        Assert.Contains(reports, r => r.Contains("nesting deeper than", StringComparison.Ordinal) && r.Contains("@import", StringComparison.Ordinal));
    }

    /// <summary>More sheets than the cap, flat: the ones past it are not loaded, reported once; the ones before it apply.</summary>
    [Fact]
    public void MoreSheetsThanTheCap_AreNotFollowed_AndReportedOnce()
    {
        using var dir = NewScratchDir();
        var site = new StringBuilder();
        for (int i = 0; i < 70; i++)
        {
            site.Append($"@import url(t{i}.css);");
            dir.Write($"css/t{i}.css", Encoding.UTF8.GetBytes(i == 69 ? GreenRule : $".y{i}{{color:#ff0000}}"));
        }
        dir.Write("css/site.css", Encoding.UTF8.GetBytes(site.ToString()));

        var (content, reports) = Convert("<link rel='stylesheet' href='css/site.css'><p class='x y0'>Hello</p>", o => o.BasePath = dir.Path);

        Assert.Contains("1 0 0 rg", content, StringComparison.Ordinal);
        Assert.DoesNotContain(Green, content, StringComparison.Ordinal);
        Assert.Single(reports, r => r.Contains("stylesheets are reached through @import", StringComparison.Ordinal));
    }

    /// <summary>
    /// A remote sheet's import — <c>url()</c> rebased to an absolute URL by the loader path, and the
    /// string form resolved here against the sheet's URL — is fetched through the loader and applied.
    /// </summary>
    [Theory]
    [InlineData("@import url(theme.css);")]
    [InlineData("@import \"theme.css\";")]
    [InlineData("@import url(/css/theme.css);")]
    public void ARemoteSheetsImport_ResolvesAgainstTheSheet_AndIsFetched(string import)
    {
        var loader = new ScriptedLoader(u => Encoding.UTF8.GetBytes(u.AbsolutePath.EndsWith("site.css", StringComparison.Ordinal) ? import : GreenRule));

        var (content, _) = Convert("<link rel='stylesheet' href='https://cdn.example/css/site.css'><p class='x'>Hello</p>", o => o.RemoteResourceLoader = loader);

        Assert.Equal(new[] { new Uri("https://cdn.example/css/site.css"), new Uri("https://cdn.example/css/theme.css") }, loader.Requested);
        Assert.Contains(Green, content, StringComparison.Ordinal);
    }

    /// <summary>A local sheet may import a remote one; with no loader it is declined with the loader's own report.</summary>
    [Fact]
    public void ALocalSheetsRemoteImport_GoesThroughTheLoader_OrIsDeclined()
    {
        using var dir = NewScratchDir();
        dir.Write("css/site.css", Encoding.UTF8.GetBytes("@import url(https://cdn.example/theme.css);"));
        var loader = new ScriptedLoader(_ => Encoding.UTF8.GetBytes(GreenRule));

        var (fetched, _) = Convert(Page, o => { o.BasePath = dir.Path; o.RemoteResourceLoader = loader; });
        var (declined, reports) = Convert(Page, o => o.BasePath = dir.Path);

        Assert.Equal(new[] { new Uri("https://cdn.example/theme.css") }, loader.Requested);
        Assert.Contains(Green, fetched, StringComparison.Ordinal);
        Assert.DoesNotContain(Green, declined, StringComparison.Ordinal);
        Assert.Contains(reports, r => r.Contains("RemoteResourceLoader", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ DoS bounds

    /// <summary>
    /// The same local sheet named a hundred times is read once: the cascade resolves each reference to
    /// its location and skips one already loaded. Without the dedup it read, rebased and scanned the file
    /// every time — a document of a hundred thousand links to one file read it a hundred thousand times.
    /// The imported rule still applies, so the sheet was read at least once.
    /// </summary>
    [Fact]
    public void TheSameLocalSheetNamedManyTimes_IsReadOnce()
    {
        using var dir = NewScratchDir();
        dir.Write("theme.css", Encoding.UTF8.GetBytes(GreenRule));
        var links = new StringBuilder();
        for (int i = 0; i < 100; i++) links.Append("<link rel='stylesheet' href='theme.css'>");
        links.Append("<p class='x'>Hello</p>");

        var (content, _) = Convert(links.ToString(), o => o.BasePath = dir.Path);

        Assert.Equal(1, HtmlToBuilderMapper.StylesheetReadCount);
        Assert.Contains(Green, content, StringComparison.Ordinal);
    }

    /// <summary>The control: two different sheets are each read, so the count is their number, not always one.</summary>
    [Fact]
    public void TwoDifferentSheets_AreEachRead()
    {
        using var dir = NewScratchDir();
        dir.Write("a.css", Encoding.UTF8.GetBytes(".x{color:#00ff00}"));
        dir.Write("b.css", Encoding.UTF8.GetBytes("p{margin:0}"));

        Convert("<link rel='stylesheet' href='a.css'><link rel='stylesheet' href='b.css'><p class='x'>Hi</p>",
            o => o.BasePath = dir.Path);

        Assert.Equal(2, HtmlToBuilderMapper.StylesheetReadCount);
    }

    /// <summary>
    /// A stylesheet larger than the engine reads is skipped and reported once; the rule after the budget
    /// is not applied. The budget is lowered so the fixture stays small.
    /// </summary>
    [Fact]
    public void AnOversizedStylesheet_IsSkippedAndReported()
    {
        int saved = HtmlToBuilderMapper.MaxStylesheetChars;
        try
        {
            HtmlToBuilderMapper.MaxStylesheetChars = 256;
            using var dir = NewScratchDir();
            dir.Write("big.css", Encoding.UTF8.GetBytes(new string(' ', 300) + GreenRule));

            var (content, reports) = Convert(
                "<link rel='stylesheet' href='big.css'><p class='x'>Hi</p>", o => o.BasePath = dir.Path);

            Assert.DoesNotContain(Green, content, StringComparison.Ordinal);
            Assert.Contains(reports, r => r.Contains("larger than this engine reads", StringComparison.Ordinal));
        }
        finally { HtmlToBuilderMapper.MaxStylesheetChars = saved; }
    }

    /// <summary>An <c>@import</c> prelude longer than the engine reads names nothing: the keyword-skipping loop is bounded.</summary>
    [Fact]
    public void AnEnormousImportPrelude_NamesNothing()
    {
        var prelude = new StringBuilder(" url(a.css) ");
        for (int i = 0; i < 2000; i++) prelude.Append("layer ");   // ~12 000 chars, past the cap

        string? got = HtmlToBuilderMapper.ParseImportTarget(prelude.ToString(), out _);

        Assert.Null(got);
    }

    /// <summary>The prelude parser, directly: the target and the media list for each written form.</summary>
    [Theory]
    [InlineData(" url(a.css)", "a.css", "")]
    [InlineData(" url( 'a.css' ) print", "a.css", "print")]
    [InlineData(" \"a.css\" screen and (min-width: 1px)", "a.css", "screen and (min-width: 1px)")]
    [InlineData(" url(a.css) layer(x) supports((display: grid) and (not (display: inline-grid))) print", "a.css", "print")]
    [InlineData(" url(a.css) layer print", "a.css", "print")]
    [InlineData(" a.css", null, "")]
    [InlineData(" url()", null, "")]
    public void TheImportPrelude_YieldsItsTargetAndMedia(string prelude, string? target, string media)
    {
        string? got = HtmlToBuilderMapper.ParseImportTarget(prelude, out string gotMedia);

        Assert.Equal(target, got);
        Assert.Equal(media, gotMedia);
    }
}
