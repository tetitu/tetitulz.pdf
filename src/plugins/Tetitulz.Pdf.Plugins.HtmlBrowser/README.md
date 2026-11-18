# Tetitulz.Pdf.Plugins.HtmlBrowser

Browser-backed HTML to PDF for Tetitulz.Pdf, for pages that need a **real layout engine and script
execution** - modern CSS, web fonts, charts drawn by JavaScript.

Isolates Microsoft.Playwright from the core package graph. The in-process converter in
`Tetitulz.Pdf.HtmlToPdf` needs no browser and is the right choice for straightforward documents;
this is the one to reach for when fidelity to a real browser is the requirement.

## Installing the browser

Playwright needs its browser binaries on the machine. That is a deployment step, not a NuGet one:

```csharp
PlaywrightHtmlBrowserPlugin.InstallBrowsers();          // once, or as part of provisioning
```

## Rendering

```csharp
var plugin = new PlaywrightHtmlBrowserPlugin();
plugin.Initialize();
PdfPluginHost.Instance.Register(plugin);

// ForHtml: an HTML string, with no directory a page may read local files from.
byte[] pdf = await plugin.RenderAsync(HtmlBrowserRequest.ForHtml(html, new HtmlToPdfOptions()));
```

## How it renders

Each conversion starts its own Playwright driver and headless browser, opens one context (JavaScript on or off
by `HtmlToPdfOptions.JavaScriptEnabled`, every request through the SSRF guard) and one page, navigates or sets
the content, prints, and tears down in reverse — the context before the browser, the browser before the driver —
so a failure part-way does not leak a browser process, and no two conversions share state.

The browser is headless Chromium (`BrowserEngine.Chromium`, the enum's one value): Playwright prints
a PDF from no other ("PDF generation is only supported for Headless Chromium"), so Firefox and
WebKit are not offered.

The page size, margins, landscape, background, header and footer templates and scale go to `page.pdf()` as they
are set; the scale is clamped to Chromium's `[0.1, 2.0]` by the managed engine's own clamp
(`PdfExportOptions.ClampScale`), so the two engines mean the same thing by it.

## What you are taking on

A browser is a process, a large dependency and an execution environment. If the HTML comes from
somewhere you do not control, everything in it runs - so treat this as a network-and-execution
decision, not merely a rendering one. The managed converter has no such surface.

With `HtmlToPdfOptions.BlockPrivateNetworkAccess` on (the default), a page's network access is
decided in two places:

- **The browser is launched behind a proxy of the plugin's own** — a SOCKS5 listener on the
  loopback interface, alive for the one conversion. Every TCP connection the browser makes goes to
  it, loopback included: requests, redirects, beacons, WebSockets, a worker's fetches and sockets.
  The proxy is handed the host name, resolves it once, and connects to an address only if every
  address the name has is public. The address checked is the address connected to, so a name that
  answers differently a second time gains nothing.
- **The context's request route** decides what a proxy cannot: `file:` only inside the request's
  base directory (none for `ForHtml`), by the containment rule the managed converter applies, so
  case counts where the file system's does; `data:`, `blob:` and `about:` allowed; every other
  scheme aborted. It also refuses a private host early, with an error that names it.

And around them: WebSockets are routed and held to the same host rule, service workers are
blocked, downloads are not accepted, and WebRTC is kept from sending UDP outside the proxy.

**Chromium runs in its operating-system sandbox** (`HtmlToPdfOptions.ChromiumSandbox`, on by
default): the proxy and the route bound what an intact renderer does, and a page that compromised
the renderer would otherwise act with this process's own file and network access. Turn it off only
where Chromium cannot start sandboxed — a Linux host running as root, or a container without user
namespaces.

**The guard's proxy connects directly.** It replaces the browser's proxy setting, the system's
included, so on a machine that reaches the web only through a proxy of its own a guarded page
loads no remote resource; such a host fetches the page itself and renders it with `ForHtml`, or
turns the guard off for input it trusts.

What that leaves: the proxy asks for no credentials, since a browser offers a SOCKS proxy none, so
during a conversion another process on the machine can connect through it — to public addresses
only. This was run against Chromium, channel by channel, against a loopback listener. `Microsoft.Playwright` 1.64.1 is the floor: earlier clients fail on a routed
WebSocket. With `BlockPrivateNetworkAccess` off there is no proxy and no route, and a page reaches
whatever the machine does, its local files included.

## Targets

`net10.0` and `netstandard2.0`.

## Licence and provenance

Apache License 2.0. Copyright (c) 2026 Tamir Mordo. Credit it as "Uses Tetitulz.Pdf by Tamir Mordo" (the package's NOTICE file).

See the repository for the full guide, the open-gap register and the third-party notices, including
Playwright's.
