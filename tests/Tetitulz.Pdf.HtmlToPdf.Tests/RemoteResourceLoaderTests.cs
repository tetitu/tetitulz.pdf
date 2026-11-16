// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// Remote <c>http(s)</c> subresources in the managed engine, fetched only through
    /// <see cref="HtmlToPdfOptions.RemoteResourceLoader"/>.
    ///
    /// <para><b>No test here touches the network.</b> A scripted loader stands in for it, and the
    /// one suite that drives <see cref="GuardedRemoteResourceLoader"/> does so through a scripted
    /// handler with IP-literal hosts, which the guard classifies without a DNS lookup.</para>
    ///
    /// <para>The no-loader default — every remote resource declined and reported — is pinned where
    /// it always was, by <c>AssemblyScanMediumFixesTests.RemoteSubresources_AreEachReportedAsDeclined</c>
    /// and the per-kind "never fetched" tests; each positive test here has that as its control.</para>
    /// </summary>
    public class RemoteResourceLoaderTests
    {
        private const string Shalom = "שלום";

        private static readonly Regex XObjectInvocation = new(@"/\w+\s+Do\b", RegexOptions.Compiled);

        private static int Draws(byte[] pdf) => XObjectInvocation.Matches(DecodeStreams(pdf)).Count;

        /// <summary>A loader that answers from a script and records what it was asked for.</summary>
        private sealed class ScriptedLoader : IRemoteResourceLoader
        {
            private readonly Func<Uri, (byte[]? Body, string? Failure)> _answer;
            public List<Uri> Requested { get; } = new();

            public ScriptedLoader(Func<Uri, (byte[]? Body, string? Failure)> answer) => _answer = answer;

            public static ScriptedLoader Serving(Func<Uri, byte[]?> body) => new(u => (body(u), null));

            public byte[]? Load(Uri url, out string? failure)
            {
                Requested.Add(url);
                var (body, why) = _answer(url);
                failure = why;
                return body;
            }
        }

        private static (byte[] Pdf, List<BuilderDiagnostic> Seen) Convert(
            string html, IRemoteResourceLoader? loader, Action<HtmlToPdfOptions>? configure = null)
        {
            var seen = new List<BuilderDiagnostic>();
            var o = Managed();
            o.OnDiagnostic = seen.Add;
            o.RemoteResourceLoader = loader;
            configure?.Invoke(o);
            return (ToPdf(html, o), seen);
        }

        // ------------------------------------------------------------------ the four kinds

        [Fact]
        public void ARemoteImage_IsFetchedThroughTheLoader_AndDrawn()
        {
            var loader = ScriptedLoader.Serving(_ => MakeRgbPng(8, 8));

            var (pdf, _) = Convert("<p>Logo:</p><img src=\"https://cdn.example/logo.png\" width=\"40\" height=\"40\">", loader);

            Assert.Equal(new[] { new Uri("https://cdn.example/logo.png") }, loader.Requested);
            Assert.Equal(1, Draws(pdf));
        }

        [Fact]
        public void ARemoteBackgroundImage_IsFetchedThroughTheLoader_AndPainted()
        {
            var loader = ScriptedLoader.Serving(_ => MakeRgbPng(8, 8));

            var (pdf, _) = Convert(
                "<div style=\"background-image:url(https://cdn.example/bg.png);background-repeat:no-repeat\">content</div>",
                loader);

            Assert.Equal(new[] { new Uri("https://cdn.example/bg.png") }, loader.Requested);
            Assert.Equal(1, Draws(pdf));
        }

        [Fact]
        public void ARemoteStylesheet_IsFetchedThroughTheLoader_AndApplied()
        {
            var loader = ScriptedLoader.Serving(_ => Encoding.UTF8.GetBytes(".x{color:#00ff00}"));

            var (pdf, _) = Convert(
                "<link rel=\"stylesheet\" href=\"https://cdn.example/css/site.css\"><p class=\"x\">Hello</p>", loader);

            Assert.Equal(new[] { new Uri("https://cdn.example/css/site.css") }, loader.Requested);
            Assert.Contains("0 1 0 rg", DecodeStreams(pdf));
        }

        /// <summary>The control for the test above: the colour comes from the fetched sheet, not from anywhere else.</summary>
        [Fact]
        public void WithoutALoader_TheSameStylesheetIsNotApplied()
        {
            var (pdf, seen) = Convert(
                "<link rel=\"stylesheet\" href=\"https://cdn.example/css/site.css\"><p class=\"x\">Hello</p>", null);

            Assert.DoesNotContain("0 1 0 rg", DecodeStreams(pdf));
            Assert.Contains(seen, d => d.Detail.Contains("RemoteResourceLoader", StringComparison.Ordinal));
        }

        [Fact]
        public void ARemoteFontFace_IsFetchedThroughTheLoader_AndEmbedded()
        {
            var loader = ScriptedLoader.Serving(_ => MinimalTrueType.Build(Shalom));

            var (pdf, _) = Convert(
                "<style>@font-face{font-family:'MyHebrew';src:url(https://fonts.example/h.ttf)}" +
                $"p{{font-family:'MyHebrew'}}</style><p>{Shalom}</p>",
                loader, o => o.AllowSystemFontSubstitution = false);

            Assert.Equal(new[] { new Uri("https://fonts.example/h.ttf") }, loader.Requested);
            Assert.Contains("/FontFile2", Raw(pdf));
        }

        /// <summary>
        /// A fetched <c>@font-face</c> source that is not a font this engine reads (an EOT) is passed
        /// over for the next one — the same test the local and <c>data:</c> sources get, because a
        /// remote list is the same list.
        /// </summary>
        [Fact]
        public void ARemoteFontFaceThatIsNotAFont_IsPassedOverForTheNextSource()
        {
            var loader = ScriptedLoader.Serving(u => u.AbsolutePath.EndsWith(".eot", StringComparison.Ordinal)
                ? Encoding.ASCII.GetBytes("this is an EOT, which is not an sfnt")
                : MinimalTrueType.Build(Shalom));

            var (pdf, seen) = Convert(
                "<style>@font-face{font-family:'MyHebrew';" +
                "src:url(https://fonts.example/h.eot), url(https://fonts.example/h.ttf)}" +
                $"p{{font-family:'MyHebrew'}}</style><p>{Shalom}</p>",
                loader, o => o.AllowSystemFontSubstitution = false);

            Assert.Equal(new[] { new Uri("https://fonts.example/h.eot"), new Uri("https://fonts.example/h.ttf") },
                         loader.Requested);
            Assert.Contains("/FontFile2", Raw(pdf));
            Assert.DoesNotContain(seen, d => d.Kind == BuilderDiagnosticKind.FontFormatRejected);
        }

        // ------------------------------------------------------------------ URLs

        [Fact]
        public void ASchemeRelativeUrl_IsFetchedAsHttps()
        {
            var loader = ScriptedLoader.Serving(_ => MakeRgbPng(4, 4));

            Convert("<img src=\"//cdn.example/a.png\">", loader);

            Assert.Equal(new[] { new Uri("https://cdn.example/a.png") }, loader.Requested);
        }

        /// <summary>
        /// <b>A relative url inside a fetched stylesheet resolves against the stylesheet</b> — never
        /// against the local base directory. The base directory here holds a file at exactly the
        /// path the url names, so a sheet whose urls were resolved locally would paint it: that is
        /// both the browser-incompatible reading and the one that lets another origin's stylesheet
        /// name the caller's files.
        /// </summary>
        [Fact]
        public void ARelativeUrlInARemoteStylesheet_ResolvesAgainstTheSheet_NotTheLocalBase()
        {
            using var dir = NewScratchDir();
            dir.Write("img/bg.png", MakeRgbPng(8, 8));

            var loader = new ScriptedLoader(u => u.AbsolutePath.EndsWith(".css", StringComparison.Ordinal)
                ? (Encoding.UTF8.GetBytes(".x{background-image:url(img/bg.png);background-repeat:no-repeat}"), null)
                : (null, "not served"));

            var (pdf, _) = Convert(
                "<link rel=\"stylesheet\" href=\"https://cdn.example/site.css\"><div class=\"x\">content</div>",
                loader, o => o.BasePath = dir.Path);

            Assert.Equal(new[] { new Uri("https://cdn.example/site.css"), new Uri("https://cdn.example/img/bg.png") },
                         loader.Requested);
            Assert.Equal(0, Draws(pdf));
        }

        /// <summary>
        /// <b>A fetched stylesheet names no local file, by any spelling.</b> The base directory holds
        /// the file each reference names: a <c>file:</c> URL in a <c>url()</c>, and a local sheet a
        /// string <c>@import</c> names. Neither is read; the control is the same rule in the document's
        /// own <c>&lt;style&gt;</c>, which paints the file.
        /// </summary>
        [Theory]
        [InlineData(".x{background-image:url(FILEURL);background-repeat:no-repeat}")]
        [InlineData("@import \"FILECSS\";")]
        [InlineData(".x{background-image:url(\"FILEURL \");background-repeat:no-repeat}")]
        [InlineData(".x{background-image:url(\"FILEURL\"/**/);background-repeat:no-repeat}")]
        [InlineData(".x{--p:FILEURL;background-image:url(var(--p));background-repeat:no-repeat}")]
        [InlineData(".x{--p:url(FILEURL);background-image:var(--p);background-repeat:no-repeat}")]
        [InlineData(".x{background-image:url(\"x.png \");background-repeat:no-repeat}")]
        [InlineData(".x{background-image:url( ' FILEURL ' );background-repeat:no-repeat}")]
        public void AdversarialRemoteStylesheet_NamingALocalFile_ReadsNothing(string sheet)
        {
            using var dir = NewScratchDir();
            dir.Write("x.png", MakeRgbPng(8, 8));
            string fileUrl = new Uri(Path.Combine(dir.Path, "x.png")).AbsoluteUri;
            dir.Write("local.css", Encoding.UTF8.GetBytes(".x{background-image:url(" + fileUrl + ");background-repeat:no-repeat}"));
            string served = sheet.Replace("FILEURL", fileUrl).Replace("FILECSS", new Uri(Path.Combine(dir.Path, "local.css")).AbsoluteUri);

            var loader = new ScriptedLoader(u => u.AbsolutePath.EndsWith(".css", StringComparison.Ordinal)
                ? (Encoding.UTF8.GetBytes(served), null)
                : (null, "not served"));
            var (pdf, _) = Convert(
                "<link rel=\"stylesheet\" href=\"https://cdn.example/site.css\"><div class=\"x\">content</div>",
                loader, o => o.BasePath = dir.Path);
            Assert.Equal(0, Draws(pdf));

            var (control, _) = Convert(
                "<style>.x{background-image:url(" + fileUrl + ");background-repeat:no-repeat}</style><div class=\"x\">content</div>",
                ScriptedLoader.Serving(_ => null), o => o.BasePath = dir.Path);
            Assert.Equal(1, Draws(control));
        }

        /// <summary>A local sheet's <c>file:</c> reference is its own and stays as written.</summary>
        [Fact]
        public void RebasingALocalStylesheet_KeepsAFileUrl()
        {
            string css = ".a{background:url(file:///C:/base/x.png)}";

            Assert.Equal(css, HtmlToBuilderMapper.RebaseCssUrls(css, new Uri("file:///C:/base/site.css")));
        }

        [Theory]
        [InlineData("url(file:///C:/base/x.png)", "url(\"#\")")]
        [InlineData("url(C:\\base\\x.png)", "url(\"#\")")]
        [InlineData("url(ftp://other.example/x.png)", "url(\"#\")")]
        [InlineData("url(img/a.png)", "url(\"https://cdn.example/css/img/a.png\")")]
        [InlineData("url('../r.png')", "url(\"https://cdn.example/r.png\")")]
        [InlineData("url(\"/rooted.png\")", "url(\"https://cdn.example/rooted.png\")")]
        [InlineData("url(//other.example/x.png)", "url(\"https://other.example/x.png\")")]
        [InlineData("url(https://other.example/x.png)", "url(https://other.example/x.png)")]
        [InlineData("url(data:image/png;base64,AAAA)", "url(data:image/png;base64,AAAA)")]
        [InlineData("url(#clip)", "url(#clip)")]
        public void RebasingAStylesheet_MakesOnlyRelativeUrlsAbsolute(string written, string expected)
        {
            string css = ".a{background:" + written + "}";

            string rebased = HtmlToBuilderMapper.RebaseCssUrls(css, new Uri("https://cdn.example/css/site.css"));

            Assert.Equal(".a{background:" + expected + "}", rebased);
        }

        // ------------------------------------------------------------------ once, and bounded

        [Fact]
        public void ARepeatedUrl_IsFetchedOnce_AndDrawnEachTime()
        {
            var loader = ScriptedLoader.Serving(_ => MakeRgbPng(4, 4));

            var (pdf, _) = Convert(
                "<img src=\"https://cdn.example/dot.png\"><img src=\"https://cdn.example/dot.png\">", loader);

            Assert.Single(loader.Requested);
            Assert.Equal(2, Draws(pdf));
        }

        /// <summary>
        /// The per-conversion budget: each fetch can take the loader's whole timeout, so a document
        /// listing thousands of URLs would otherwise hold a conversion for as long as it liked. The
        /// URLs past the budget are not asked for, and the budget is reported once, not per URL.
        /// </summary>
        [Fact]
        public void PastMaxRemoteResources_NothingMoreIsFetched_AndTheBudgetIsReportedOnce()
        {
            var loader = ScriptedLoader.Serving(_ => MakeRgbPng(4, 4));
            string html = string.Concat(Enumerable.Range(0, 5).Select(i => $"<img src=\"https://cdn.example/{i}.png\">"));

            var (pdf, seen) = Convert(html, loader, o => o.MaxRemoteResources = 2);

            Assert.Equal(2, loader.Requested.Count);
            Assert.Equal(2, Draws(pdf));
            Assert.Single(seen, d => d.Detail.Contains("MaxRemoteResources", StringComparison.Ordinal));
        }

        // ------------------------------------------------------------------ failures

        [Fact]
        public void ALoaderFailure_IsReportedWithTheLoadersReason()
        {
            var loader = new ScriptedLoader(_ => (null, "refused by host policy"));

            var (pdf, seen) = Convert("<p>Text</p><img src=\"https://cdn.example/a.png\">", loader);

            Assert.Equal(0, Draws(pdf));
            Assert.Contains(seen, d => d.Detail.Contains("The image", StringComparison.Ordinal)
                                    && d.Detail.Contains("refused by host policy", StringComparison.Ordinal));
            // Once: the remote path says why, and the local "outside BasePath" report must not
            // also fire for a URL that was never a local path.
            Assert.Single(seen, d => d.Detail.Contains("cdn.example/a.png", StringComparison.Ordinal));
        }

        /// <summary>
        /// A failed remote resource is reported <b>once</b>, at every site. Each site keeps a local
        /// branch whose own "outside BasePath" report sits right below the remote one; letting it
        /// fire for a remote URL tells the caller the same thing twice, the second time wrongly.
        /// The image row alone was pinned at first, and the sweep found the other two unasserted.
        /// </summary>
        [Theory]
        [InlineData("<img src=\"https://cdn.example/x.png\">", "The image")]
        [InlineData("<div style=\"background-image:url(https://cdn.example/x.png)\">content</div>", "The background image")]
        [InlineData("<link rel=\"stylesheet\" href=\"https://cdn.example/x.png\"><p>content</p>", "The stylesheet")]
        public void AFailedRemoteResource_IsReportedExactlyOnce(string html, string what)
        {
            var loader = new ScriptedLoader(_ => (null, "refused by host policy"));

            var (_, seen) = Convert(html, loader);

            Assert.Contains(seen, d => d.Detail.Contains(what, StringComparison.Ordinal)
                                    && d.Detail.Contains("refused by host policy", StringComparison.Ordinal));
            Assert.Single(seen, d => d.Detail.Contains("cdn.example/x.png", StringComparison.Ordinal));
        }

        /// <summary>
        /// A throwing loader is reported and the conversion goes on. The potency half — the
        /// paragraph beside the image is still drawn — is what tells this apart from a conversion
        /// that failed outright and left nothing to find.
        /// </summary>
        [Fact]
        public void ALoaderThatThrows_IsReported_AndTheRestOfTheDocumentConverts()
        {
            var loader = new ScriptedLoader(_ => throw new InvalidOperationException("boom"));

            var (pdf, seen) = Convert("<p>Survivor</p><img src=\"https://cdn.example/a.png\">", loader);

            Assert.Contains(seen, d => d.Detail.Contains("boom", StringComparison.Ordinal));
            Assert.Contains(DrawnTexts(DecodeStreams(pdf)), t => t.Text.Contains("Survivor", StringComparison.Ordinal));
        }

        // ------------------------------------------------------------------ the guarded loader

        /// <summary>A handler that answers from a script and records every request it was asked.</summary>
        private sealed class ScriptedHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;
            public List<Uri> Requests { get; } = new();

            public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) => _answer = answer;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Requests.Add(request.RequestUri!);
                HttpResponseMessage response = _answer(request);
                response.RequestMessage ??= request;
                return Task.FromResult(response);
            }
        }

        private static GuardedRemoteResourceLoader Guarded(ScriptedHandler handler)
            => new(handler, GuardedRemoteResourceLoader.DefaultMaxBytes, null, 5);

        /// <summary>
        /// The guarded loader reaches the network only for a public host: a document naming the
        /// cloud metadata service gets a diagnostic, and the handler is never asked.
        /// </summary>
        [Fact]
        public void TheGuardedLoader_NeverRequestsAPrivateAddressTheDocumentNames()
        {
            var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(MakeRgbPng(4, 4)),
            });
            using var loader = Guarded(handler);

            var (pdf, seen) = Convert("<img src=\"http://169.254.169.254/latest/meta-data/x.png\">", loader);

            Assert.Empty(handler.Requests);
            Assert.Equal(0, Draws(pdf));
            Assert.Contains(seen, d => d.Detail.Contains("public host", StringComparison.Ordinal));
        }

        /// <summary>The acceptance control: the same loader fetches and draws a public host's image.</summary>
        [Fact]
        public void TheGuardedLoader_FetchesAPublicHostsImage()
        {
            var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(MakeRgbPng(4, 4)),
            });
            using var loader = Guarded(handler);

            var (pdf, _) = Convert("<img src=\"http://93.184.216.34/a.png\">", loader);

            Assert.Single(handler.Requests);
            Assert.Equal(1, Draws(pdf));
        }

        // Each limit the loader takes is forwarded to the fetch, and each forwarding is pinned by a
        // fixture only that limit can refuse: the defaults (16 MiB, five hops, fifteen seconds) are
        // far past anything these fixtures reach, so a loader that dropped its own value for the
        // default would pass every other test here.

        [Fact]
        public void TheGuardedLoader_AppliesItsOwnSizeCeiling()
        {
            var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(MakeRgbPng(16, 16)),
            });
            using var loader = new GuardedRemoteResourceLoader(handler, maxBytes: 16, null, 5);

            var (pdf, seen) = Convert("<img src=\"http://93.184.216.34/a.png\">", loader);

            Assert.Single(handler.Requests);
            Assert.Equal(0, Draws(pdf));
            Assert.Contains(seen, d => d.Detail.Contains("larger than 16 bytes", StringComparison.Ordinal));
        }

        [Fact]
        public void TheGuardedLoader_AppliesItsOwnRedirectLimit()
        {
            var handler = new ScriptedHandler(r => r.RequestUri!.AbsolutePath == "/a.png"
                ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("/b.png", UriKind.Relative) } }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(MakeRgbPng(4, 4)) });
            using var loader = new GuardedRemoteResourceLoader(handler, GuardedRemoteResourceLoader.DefaultMaxBytes, null, maxRedirects: 0);

            var (pdf, seen) = Convert("<img src=\"http://93.184.216.34/a.png\">", loader);

            Assert.Single(handler.Requests);
            Assert.Equal(0, Draws(pdf));
            Assert.Contains(seen, d => d.Detail.Contains("Exceeded 0 redirects", StringComparison.Ordinal));
        }

        /// <summary>
        /// Run on a task with a generous wait: the failure this guards against is a loader whose
        /// budget never arrives, which is a conversion that never returns.
        /// </summary>
        [Fact]
        public void TheGuardedLoader_AppliesItsOwnTimeBudget()
        {
            var handler = new StallingHandler();
            using var loader = new GuardedRemoteResourceLoader(
                handler, GuardedRemoteResourceLoader.DefaultMaxBytes, TimeSpan.FromMilliseconds(200), 5);

            var convert = Task.Run(() => Convert("<img src=\"http://93.184.216.34/a.png\">", loader));

            Assert.True(convert.Wait(TimeSpan.FromSeconds(30)), "the loader's own budget was not applied");
            Assert.Contains(convert.Result.Seen, d => d.Detail.Contains("did not finish within", StringComparison.Ordinal));
        }

        /// <summary>A handler that never answers until cancelled.</summary>
        private sealed class StallingHandler : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        }

        [Fact]
        public void TheGuardedLoader_RefusesNonsenseLimits()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new GuardedRemoteResourceLoader(maxBytes: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new GuardedRemoteResourceLoader(timeout: TimeSpan.Zero));
            Assert.Throws<ArgumentOutOfRangeException>(() => new GuardedRemoteResourceLoader(maxRedirects: -1));
        }

        [Fact]
        public void TheGuardedLoader_KeepsTheLimitsItWasGiven()
        {
            using var loader = new GuardedRemoteResourceLoader(1234, TimeSpan.FromSeconds(3), 2);

            Assert.Equal(1234, loader.MaxBytes);
            Assert.Equal(TimeSpan.FromSeconds(3), loader.Timeout);
            Assert.Equal(2, loader.MaxRedirects);
        }
    }
}
