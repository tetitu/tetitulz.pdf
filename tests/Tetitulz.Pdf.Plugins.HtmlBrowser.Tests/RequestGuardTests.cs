// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Plugins.HtmlBrowser;

namespace Tetitulz.Pdf.Plugins.HtmlBrowser.Tests;

/// <summary>
/// The request guard's own decisions, asked without a browser: which local files a page may load
/// and which hosts a page's WebSocket may reach. What Chromium then does with an aborted request is
/// Playwright's; which requests are aborted is decided here.
/// </summary>
public class RequestGuardTests
{
    private static readonly string Base = Path.Combine(Path.GetTempPath(), "tetitulz-guard-base");

    private static bool Allowed(string path)
        => PlaywrightRenderer.IsFileUriWithinBase(new Uri(path), PlaywrightRenderer.NormalizeBaseDirectory(Base));

    /// <summary>
    /// A URL <see cref="Uri"/> cannot parse is checked by nothing, so it is aborted — .NET Framework
    /// refuses one past about 65,519 characters, which Chromium accepts and reads a file: URL's file
    /// from whatever its query holds. A data: URL that long (an inline image) goes on; and the same
    /// file inside the base directory, parsed, is allowed (the controls).
    /// </summary>
    [Fact]
    public void AdversarialUnparsableUrl_IsAborted_ButADataUrlGoesOn()
    {
        string baseDir = PlaywrightRenderer.NormalizeBaseDirectory(Base);
        Assert.False(Uri.TryCreate("http://[::1/x", UriKind.Absolute, out _));
        Assert.False(PlaywrightRenderer.IsRequestAllowed("http://[::1/x", true, baseDir));
        Assert.False(PlaywrightRenderer.IsRequestAllowed("", true, baseDir));
        Assert.True(PlaywrightRenderer.IsRequestAllowed("data:image/png;base64,%%%" + new string('A', 10), true, baseDir));

        Assert.True(PlaywrightRenderer.IsRequestAllowed(new Uri(Path.Combine(Base, "page.html")).AbsoluteUri, true, baseDir));
        Assert.False(PlaywrightRenderer.IsRequestAllowed(new Uri(Path.Combine(Base, "page.html")).AbsoluteUri, false, baseDir));
        Assert.False(PlaywrightRenderer.IsRequestAllowed("ftp://example.com/x", true, baseDir));
        Assert.True(PlaywrightRenderer.IsRequestAllowed("about:blank", true, baseDir));
    }

    [Fact]
    public void AFileInTheBaseDirectoryOrBelowIt_IsAllowed()
    {
        Assert.True(Allowed(Path.Combine(Base, "page.html")));
        Assert.True(Allowed(Path.Combine(Base, "img", "logo.png")));
    }

    /// <summary>
    /// Outside the directory by each route a path has: a parent, a sibling whose name begins with
    /// the directory's, a climb back out, and another root altogether.
    /// </summary>
    [Fact]
    public void AFileOutsideTheBaseDirectory_IsRefused()
    {
        Assert.False(Allowed(Path.Combine(Path.GetTempPath(), "secret.txt")));
        Assert.False(Allowed(Base + "-evil" + Path.DirectorySeparatorChar + "x.png"));
        Assert.False(Allowed(Path.Combine(Base, "..", "secret.txt")));
        Assert.False(Allowed(OperatingSystem.IsWindows() ? @"C:\Windows\win.ini" : "/etc/passwd"));
    }

    /// <summary>
    /// A name that differs only in case is the same directory where the file system says so
    /// (Windows) and another directory where it does not — there a sibling <c>…-BASE</c> is not
    /// inside <c>…-base</c>, however alike they read.
    /// </summary>
    [Fact]
    public void CaseCounts_WhereTheFileSystemsDoes()
    {
        string otherCase = Path.Combine(Path.GetTempPath(), "TETITULZ-GUARD-BASE", "page.html");

        Assert.Equal(OperatingSystem.IsWindows(), Allowed(otherCase));
    }

    /// <summary>With no base directory there is no local file a page may load.</summary>
    [Fact]
    public void WithNoBaseDirectory_NoFileIsAllowed()
    {
        Assert.Null(PlaywrightRenderer.NormalizeBaseDirectory(null));
        Assert.False(PlaywrightRenderer.IsFileUriWithinBase(new Uri(Path.Combine(Base, "page.html")), null));
    }

    /// <summary>
    /// A page's WebSocket is held to the rule its requests are: a public host passes, loopback, a
    /// private range and the metadata address do not, and neither does a scheme that is not a
    /// WebSocket's.
    /// </summary>
    [Theory]
    [InlineData("ws://93.184.216.34/feed", true)]
    [InlineData("wss://93.184.216.34:8443/feed", true)]
    [InlineData("ws://127.0.0.1:9222/devtools", false)]
    [InlineData("ws://[::1]/x", false)]
    [InlineData("wss://10.0.0.5/x", false)]
    [InlineData("ws://169.254.169.254/latest", false)]
    [InlineData("http://93.184.216.34/", false)]
    [InlineData("not a url", false)]
    public void AWebSocket_IsHeldToTheRequestGuardsRule(string url, bool allowed)
        => Assert.Equal(allowed, PlaywrightRenderer.IsWebSocketAllowed(url));

    /// <summary>
    /// Behind the guard's proxy every engine is told the proxy and that loopback goes to it too —
    /// a browser otherwise connects to <c>127.0.0.1</c> around its proxy, which is the address the
    /// guard exists for.
    /// </summary>
    [Theory]
    [InlineData(Tetitulz.Pdf.HtmlToPdf.BrowserEngine.Chromium)]
    [InlineData(Tetitulz.Pdf.HtmlToPdf.BrowserEngine.Firefox)]
    [InlineData(Tetitulz.Pdf.HtmlToPdf.BrowserEngine.WebKit)]
    public void BehindTheProxy_EveryEngineIsSentThroughIt_LoopbackIncluded(Tetitulz.Pdf.HtmlToPdf.BrowserEngine engine)
    {
        var launch = PlaywrightRenderer.LaunchOptions(engine, "socks5://127.0.0.1:5555");

        Assert.True(launch.Headless);
        Assert.Equal("socks5://127.0.0.1:5555", launch.Proxy.Server);
        Assert.Equal("<-loopback>", launch.Proxy.Bypass);
    }

    /// <summary>
    /// WebRTC's own UDP is kept inside the proxy where the engine has a switch for it: Chromium's
    /// handling policy, Firefox's peer-connection preference.
    /// </summary>
    [Fact]
    public void BehindTheProxy_WebRtcIsKeptFromSendingUdpAroundIt()
    {
        var chromium = PlaywrightRenderer.LaunchOptions(Tetitulz.Pdf.HtmlToPdf.BrowserEngine.Chromium, "socks5://127.0.0.1:5555");
        var firefox = PlaywrightRenderer.LaunchOptions(Tetitulz.Pdf.HtmlToPdf.BrowserEngine.Firefox, "socks5://127.0.0.1:5555");

        Assert.Equal(new[] { "--force-webrtc-ip-handling-policy=disable_non_proxied_udp" }, chromium.Args);
        Assert.Null(chromium.FirefoxUserPrefs);
        var preferences = System.Linq.Enumerable.ToDictionary(firefox.FirefoxUserPrefs, p => p.Key, p => p.Value);
        Assert.Equal(false, preferences["media.peerconnection.enabled"]);
        Assert.Null(firefox.Args);
    }

    /// <summary>With the guard off there is no proxy, and nothing that belongs to it is set.</summary>
    [Fact]
    public void WithoutAProxy_TheBrowserIsLaunchedPlain()
    {
        var launch = PlaywrightRenderer.LaunchOptions(Tetitulz.Pdf.HtmlToPdf.BrowserEngine.Chromium, null);

        Assert.True(launch.Headless);
        Assert.Null(launch.Proxy);
        Assert.Null(launch.Args);
        Assert.Null(launch.FirefoxUserPrefs);
    }

    /// <summary>
    /// Chromium is launched in its sandbox: the driver passes <c>--no-sandbox</c> unless asked for one,
    /// and a page that compromised the renderer would then hold this process's own access, proxy or
    /// not. The options' default asks for it, with or without the guard; turned off, it is not asked for.
    /// </summary>
    [Fact]
    public void Chromium_IsLaunchedInItsSandbox_UnlessTurnedOff()
    {
        var options = new Tetitulz.Pdf.HtmlToPdf.HtmlToPdfOptions();
        Assert.True(options.ChromiumSandbox);

        Assert.True(PlaywrightRenderer.LaunchOptions(Tetitulz.Pdf.HtmlToPdf.BrowserEngine.Chromium, "socks5://127.0.0.1:5555", options.ChromiumSandbox).ChromiumSandbox);
        Assert.True(PlaywrightRenderer.LaunchOptions(Tetitulz.Pdf.HtmlToPdf.BrowserEngine.Chromium, null).ChromiumSandbox);
        Assert.False(PlaywrightRenderer.LaunchOptions(Tetitulz.Pdf.HtmlToPdf.BrowserEngine.Chromium, null, chromiumSandbox: false).ChromiumSandbox);
    }
}
