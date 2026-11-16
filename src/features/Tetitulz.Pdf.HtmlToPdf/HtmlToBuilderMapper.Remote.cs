// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Export;

namespace Tetitulz.Pdf.HtmlToPdf
{
    internal sealed partial class HtmlToBuilderMapper
    {
        // ============================================================= remote subresources

        /// <summary>
        /// Every remote URL this conversion has asked for, by absolute form, with what came back —
        /// <c>null</c> for a failure, so a URL that failed once is neither fetched nor reported again.
        /// </summary>
        private readonly Dictionary<string, byte[]?> _remoteResources = new(StringComparer.Ordinal);

        /// <summary>Set once <see cref="HtmlToPdfOptions.MaxRemoteResources"/> has refused a URL.</summary>
        private bool _remoteBudgetReported;

        /// <summary>
        /// The one path from a remote URL in the document to its bytes, for all four kinds of
        /// subresource. Reports every outcome that leaves the resource out, and returns <c>null</c>
        /// for each of them.
        /// </summary>
        /// <remarks>
        /// <para><b>One path, not four.</b> The image, background, stylesheet and font sites each
        /// had their own remote test and their own "declined" message; a second copy of the budget,
        /// the cache or the reason text at any one of them is how one kind of subresource would end
        /// up fetched twice, uncounted, or dropped in silence while the others reported.</para>
        ///
        /// <para><b>No loader is not a failure to fetch</b> — it is the engine's documented default,
        /// and it reports with the long-standing wording so a caller can see which opt-in to set.</para>
        /// </remarks>
        /// <param name="url">The URL as the document wrote it.</param>
        /// <param name="what">What it is, for the diagnostic ("image", "stylesheet", …).</param>
        /// <param name="kind">The diagnostic kind the caller's site reports under.</param>
        private byte[]? LoadRemote(string url, string what, BuilderDiagnosticKind kind)
        {
            IRemoteResourceLoader? loader = _options.RemoteResourceLoader;
            if (loader == null)
            {
                ReportDiagnostic(kind,
                    "The " + what + " '" + Excerpt(url) + "' was not loaded: it is remote, and no " +
                    "HtmlToPdfOptions.RemoteResourceLoader is set, so this engine performs no network " +
                    "I/O. It is absent from the output.");
                return null;
            }

            Uri? uri = RemoteUri(url);
            if (uri == null)
            {
                ReportDiagnostic(kind, "The " + what + " '" + Excerpt(url) + "' was not loaded: it is not a " +
                                       "well-formed http(s) URL. It is absent from the output.");
                return null;
            }

            string key = uri.AbsoluteUri;
            if (_remoteResources.TryGetValue(key, out byte[]? known)) return known;

            if (_remoteResources.Count >= Math.Max(0, _options.MaxRemoteResources))
            {
                // Remembered as a failure, so the same URL appearing again neither re-checks the
                // budget nor re-reports; the latch keeps the budget message to one per conversion.
                _remoteResources[key] = null;
                if (!_remoteBudgetReported)
                {
                    _remoteBudgetReported = true;
                    ReportDiagnostic(kind,
                        "The " + what + " '" + Excerpt(url) + "' and every later remote URL were not " +
                        "loaded: this conversion already asked for " + _options.MaxRemoteResources +
                        " (HtmlToPdfOptions.MaxRemoteResources). They are absent from the output.");
                }
                return null;
            }

            byte[]? data;
            string? failure;
            try
            {
                data = loader.Load(uri, out failure);
            }
            catch (Exception ex)
            {
                data = null;
                failure = "the loader threw " + ex.GetType().Name + ": " + ex.Message;
            }

            if (data == null || data.Length == 0)
            {
                data = null;
                ReportDiagnostic(kind, "The " + what + " '" + Excerpt(url) + "' was not loaded: " +
                                       (failure ?? "the loader returned nothing") + ". It is absent from the output.");
            }
            _remoteResources[key] = data;
            return data;
        }

        /// <summary>
        /// A remote stylesheet's text with every relative <c>url()</c> made absolute against the
        /// stylesheet's own URL, or <c>null</c> when it could not be loaded.
        /// </summary>
        /// <remarks>
        /// <para><b>Rebased here, before the rules are extracted</b>, because nothing downstream
        /// knows which sheet a declaration came from: a relative url that reaches the walk is
        /// resolved against <see cref="HtmlToPdfOptions.BasePath"/>, which is right only for the
        /// document's own <c>&lt;style&gt;</c> (a local sheet is rebased too, in
        /// <see cref="LoadLocalStylesheet"/>). For a remote sheet that is wrong for the same reason
        /// a browser does not do it — <c>url(img/bg.png)</c> in
        /// <c>https://cdn.example/css/site.css</c> means <c>https://cdn.example/css/img/bg.png</c> —
        /// and it is a trust leak besides: a stylesheet from another origin would be naming files in
        /// the caller's local base directory.</para>
        /// </remarks>
        /// <param name="href">The URL as the document wrote it.</param>
        /// <param name="location">The sheet's absolute URL, for its own <c>@import</c>s to resolve against; null when not loaded.</param>
        private string? LoadRemoteStylesheet(string href, out string? location)
        {
            location = null;
            Uri? sheet = RemoteUri(href);
            location = sheet?.AbsoluteUri;
            // One read per location a conversion: a document that names the same sheet a hundred thousand
            // times (<link> or @import) would otherwise re-decode and re-scan it each time. An empty
            // result is a sheet already loaded, not a failure, so the caller reports no decline.
            if (location != null && !_importedSheets.Add(location)) return "";

            byte[]? bytes = LoadRemote(href, "stylesheet", BuilderDiagnosticKind.ContentApproximated);
            if (bytes == null) return null;
            CountStylesheetRead();

            string css;
            using (var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8,
                                                 detectEncodingFromByteOrderMarks: true))
                css = reader.ReadToEnd();
            if (TooLargeStylesheet(css.Length, href)) return "";

            return sheet == null ? css : RebaseCssUrls(css, sheet);
        }

        /// <summary>
        /// Whether a stylesheet of <paramref name="chars"/> characters is past
        /// <c>MaxStylesheetChars</c>; reports it once (named by <paramref name="href"/>) and is skipped.
        /// </summary>
        private bool TooLargeStylesheet(int chars, string href)
        {
            if (chars <= MaxStylesheetChars) return false;
            if (!_stylesheetSizeReported)
            {
                _stylesheetSizeReported = true;
                ReportDiagnostic(BuilderDiagnosticKind.ContentApproximated,
                    "A stylesheet (" + href + ") is larger than this engine reads; its rules were not applied.");
            }
            return true;
        }

        /// <summary>
        /// A local stylesheet's text with every relative <c>url()</c> made absolute against the
        /// stylesheet's own location, or <c>null</c> when it is outside
        /// <see cref="HtmlToPdfOptions.BasePath"/> or cannot be read.
        /// </summary>
        /// <remarks>
        /// <para><b>The same rule as <see cref="LoadRemoteStylesheet"/>, for the same reason</b>:
        /// <c>url(../fonts/x.ttf)</c> in <c>css/site.css</c> means <c>fonts/x.ttf</c>, not a
        /// <c>fonts</c> folder beside the base directory. Only remote sheets were rebased, so every
        /// local sheet kept in its own folder — Bootstrap's <c>css/</c> + <c>fonts/</c> layout —
        /// lost its fonts and background images, each reported as outside the base directory.</para>
        /// <para><b>The trust boundary does not move.</b> The rebased URL is a <c>file:</c> URI,
        /// which still reaches the file system only through <see cref="ResolveLocalPath"/> and its
        /// containment check, so a sheet can name nothing outside the base directory that it could
        /// not name before.</para>
        /// </remarks>
        /// <param name="href">The reference as the document or an importing sheet wrote it.</param>
        /// <param name="location">The sheet's full path, for its own <c>@import</c>s to resolve against; null when not loaded.</param>
        private string? LoadLocalStylesheet(string href, out string? location)
        {
            location = null;
            string? full = ResolveLocalPath(href);
            if (full == null) return null;

            location = full;
            // Resolve and dedup before the read: a document naming the same local sheet a hundred
            // thousand times would otherwise read, rebase and scan the whole file each time.
            if (!_importedSheets.Add(full)) return "";

            string css;
            try
            {
                var info = new FileInfo(full);
                if (info.Exists && info.Length > MaxStylesheetChars && TooLargeStylesheet(int.MaxValue, href)) return "";
                css = File.ReadAllText(full);
            }
            catch { return null; }
            CountStylesheetRead();
            if (TooLargeStylesheet(css.Length, href)) return "";

            return RebaseCssUrls(css, new Uri(full));
        }

        internal static readonly Regex CssUrl = new(
            @"url\(\s*(?<q>['""]?)(?<u>[^'""()\s]+)\k<q>\s*\)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        /// <summary>
        /// Rewrites every relative <c>url()</c> in <paramref name="css"/> as an absolute URL under
        /// <paramref name="sheet"/>. Fragments are left as written, and so are URLs that carry a scheme
        /// — in a remote sheet only <c>http(s)</c> and <c>data:</c> ones, every other reference of a
        /// remote sheet (a <c>file:</c> URL, a drive path) naming nothing.
        /// </summary>
        /// <remarks>
        /// <b>A <c>file:</c> sheet keeps a reference's query and fragment out of the rebase.</b>
        /// <c>System.Uri</c> gives a <c>file:</c> URI no query or fragment, so it escapes both into
        /// the path — <c>x.eot?#iefix</c>, the suffix every generated <c>@font-face</c> list carries,
        /// became <c>x.eot%3F%23iefix</c>, a file that does not exist. The suffix is peeled off,
        /// the path rebased, and the suffix put back as written, for
        /// <see cref="ResolveLocalPath"/> to strip exactly as it does from any local reference.
        /// <para>The sheet is the document's, so the pattern carries a match timeout; a sheet it is
        /// exceeded on is returned as written, its relative references resolved against the page.</para>
        /// </remarks>
        internal static string RebaseCssUrls(string css, Uri sheet)
        {
            if (!sheet.IsFile) return RebaseRemoteCssUrls(css, sheet);
            try { return CssUrl.Replace(css, m => Rebased(m, sheet)); }
            catch (RegexMatchTimeoutException) { return css; }
        }

        /// <summary>
        /// A remote sheet's text with every <c>url(…)</c> read as the readers read it — comments
        /// stripped first, the argument closed by <see cref="ClosingParen"/>, unquoted and trimmed —
        /// and rewritten as an http(s) or data: URL, or as one that names nothing.
        /// </summary>
        /// <remarks>
        /// <para><b>A scan, because a pattern's rewrite left what it did not match as written</b>, and
        /// the readers then trimmed and unquoted it into a local reference: a quoted URL with a space
        /// inside its quotes, a comment between the quote and the parenthesis, <c>url(var(--p))</c> —
        /// and a sheet the pattern timed out on came back whole. A remote sheet names no local file
        /// (<see cref="HtmlToPdfOptions.RemoteResourceLoader"/>).</para>
        /// <para><b>A <c>url()</c> holding <c>var()</c>, <c>env()</c> or <c>attr()</c> names nothing</b>:
        /// the value is only known after substitution, and the sheet's own custom property can hold a
        /// local path. An unterminated <c>url(</c> ends the sheet there.</para>
        /// </remarks>
        internal static string RebaseRemoteCssUrls(string css, Uri sheet)
        {
            css = StripCssComments(css);
            var sb = new StringBuilder(css.Length);
            int from = 0;
            for (int at = UrlFunctionAt(css, 0); at >= 0; at = UrlFunctionAt(css, from))
            {
                sb.Append(css, from, at - from);
                int open = at + 3;
                int close = ClosingParen(css, open);
                if (close < 0) { from = css.Length; break; }
                sb.Append(RemoteUrl(UnquoteFamily(css.Substring(open + 1, close - open - 1)), sheet)
                          ?? css.Substring(at, close - at + 1));
                from = close + 1;
            }
            sb.Append(css, from, css.Length - from);
            return sb.ToString();
        }

        /// <summary>The index of the next <c>url(</c>, in any ASCII case, at or after <paramref name="from"/>; −1 when none.</summary>
        private static int UrlFunctionAt(string css, int from)
            => from >= css.Length ? -1 : css.IndexOf("url(", from, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// One remote sheet's reference, unquoted, as the <c>url()</c> to write (see
        /// <see cref="RebaseRemoteCssUrls"/>), or <c>null</c> to keep the <c>url()</c> as written: an
        /// http(s), data: or fragment reference, which names no local file however the readers trim it.
        /// </summary>
        private static string? RemoteUrl(string u, Uri sheet)
        {
            if (u.Length == 0
                || u.IndexOf("var(", StringComparison.OrdinalIgnoreCase) >= 0
                || u.IndexOf("env(", StringComparison.OrdinalIgnoreCase) >= 0
                || u.IndexOf("attr(", StringComparison.OrdinalIgnoreCase) >= 0)
                return NothingUrl;
            string target;
            if (u[0] == '#') return null;
            else if (HasUrlScheme(u)) { if (!IsWebOrData(u)) return NothingUrl; return null; }
            else if (!Uri.TryCreate(sheet, u, out Uri? rebased)
                     || (rebased.Scheme != Uri.UriSchemeHttp && rebased.Scheme != Uri.UriSchemeHttps))
                return NothingUrl;
            else target = rebased.AbsoluteUri;

            if (target.IndexOf('"') < 0) return "url(\"" + target + "\")";
            if (target.IndexOf('\'') < 0) return "url('" + target + "')";
            return NothingUrl;
        }

        private static string Rebased(Match m, Uri sheet)
        {
            string u = m.Groups["u"].Value;
            if (u.StartsWith("#", StringComparison.Ordinal)) return m.Value;
            // A sheet from another origin names no local file (HtmlToPdfOptions.RemoteResourceLoader): a
            // reference of its that is not http(s) or data: — file:, or a drive path Uri reads as file: —
            // becomes one that resolves to nothing.
            if (HasUrlScheme(u))
                return sheet.IsFile || IsWebOrData(u) ? m.Value : NothingUrl;

            string suffix = "";
            if (sheet.IsFile)
            {
                int cut = u.IndexOfAny(QueryOrFragment);
                if (cut >= 0) { suffix = u.Substring(cut); u = u.Substring(0, cut); }
                if (u.Length == 0) return m.Value;
            }

            if (!Uri.TryCreate(sheet, u, out Uri? rebased)) return sheet.IsFile ? m.Value : NothingUrl;
            if (!sheet.IsFile && rebased.Scheme != Uri.UriSchemeHttp && rebased.Scheme != Uri.UriSchemeHttps) return NothingUrl;
            return "url(\"" + rebased.AbsoluteUri + suffix + "\")";
        }

        /// <summary>A <c>url()</c> that names nothing: an empty fragment, which no loader reads.</summary>
        private const string NothingUrl = "url(\"#\")";

        private static bool IsWebOrData(string url)
            => url.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
               || url.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
               || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase);

        private static readonly char[] QueryOrFragment = { '?', '#' };

        /// <summary>
        /// A URL that names its own scheme (<c>data:</c>, <c>https:</c>, …). Decided by pattern, not
        /// by <c>Uri.TryCreate(…, Absolute)</c>: on Linux that accepts <c>/img/x.png</c> as a
        /// <c>file:</c> URI, which would leave a rooted url unrebased and resolved against the local
        /// base; and it accepts <c>C:x</c> as a one-letter scheme. An ASCII letter, at least one more
        /// letter, digit, <c>+</c>, <c>.</c> or <c>-</c>, then <c>:</c> — scanned by hand.
        /// </summary>
        internal static bool HasUrlScheme(string url)
        {
            if (url.Length < 3 || !IsAsciiLetter(url[0])) return false;
            int i = 1;
            while (i < url.Length && (IsAsciiLetter(url[i]) || (url[i] >= '0' && url[i] <= '9')
                                      || url[i] == '+' || url[i] == '.' || url[i] == '-'))
                i++;
            return i >= 2 && i < url.Length && url[i] == ':';

            static bool IsAsciiLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        }

        /// <summary>
        /// The absolute <c>http(s)</c> URL a remote reference names — a scheme-relative one taken as
        /// <c>https</c> — or <c>null</c> when it is not one.
        /// </summary>
        internal static Uri? RemoteUri(string url)
        {
            string s = url.Trim();
            if (s.StartsWith("//", StringComparison.Ordinal)) s = "https:" + s;
            if (!Uri.TryCreate(s, UriKind.Absolute, out Uri? uri)) return null;
            return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps ? uri : null;
        }
    }
}
