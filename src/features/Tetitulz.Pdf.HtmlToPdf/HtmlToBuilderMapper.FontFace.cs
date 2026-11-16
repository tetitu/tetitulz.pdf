// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Imaging;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Util;
using M = Tetitulz.Pdf.Builder.Model;

namespace Tetitulz.Pdf.HtmlToPdf
{
    internal sealed partial class HtmlToBuilderMapper
    {
        // ============================================================= @font-face

        /// <summary>One <c>@font-face</c> rule that yielded readable font bytes.</summary>
        private sealed class FontFaceRule
        {
            public string Family = "";
            public bool Bold;
            public bool Italic;
            public byte[] Program = Array.Empty<byte>();
        }

        private readonly List<FontFaceRule> _fontFaces = new();

        /// <summary>
        /// Cap on <c>@font-face</c> rules <b>honoured</b> (parsed to a usable program). A block or
        /// <c>src</c> entry that fails never reaches <see cref="_fontFaces"/>, so failures are bounded
        /// separately by <see cref="_fontFaceWork"/> against <see cref="MaxFontFaceWork"/> — otherwise a
        /// stylesheet of 100 000 failing blocks, or one block with 100 000 <c>url()</c> entries, each
        /// naming a large file, re-read it every time.
        /// </summary>
        private const int MaxFontFaces = 32;

        /// <summary>Blocks and <c>src</c> entries examined this conversion (not only the ones honoured).</summary>
        private int _fontFaceWork;
        private const int MaxFontFaceWork = 4 * MaxFontFaces;

        /// <summary>A font file read once per resolved path this conversion; <c>null</c> for one that failed.</summary>
        private readonly Dictionary<string, byte[]?> _fontFaceFileBytes = new(StringComparer.Ordinal);

        /// <summary>A <c>src</c> file past the font decoders' own output bound (WOFF 2.0's 64 MiB) is refused unread.</summary>
        private const long MaxFontFileBytes = 64L * 1024 * 1024;

        /// <summary>
        /// Parses one <c>@font-face</c> block and, if its <c>src</c> resolves to readable font
        /// bytes, records it for <see cref="BuildFontProvider"/>.
        ///
        /// <para><c>url()</c> is resolved through <see cref="ResolveLocalPath"/> — the same single
        /// trust boundary every other local read uses, so a stylesheet cannot reach outside
        /// <see cref="HtmlToPdfOptions.BasePath"/> and remote URLs are never fetched. A second
        /// path guard here would be a second thing to get wrong.</para>
        /// </summary>
        private void CollectFontFace(string block)
        {
            if (_fontFaces.Count >= MaxFontFaces || _fontFaceWork >= MaxFontFaceWork) return;
            _fontFaceWork++;

            string family = "";
            bool bold = false, italic = false;
            string? src = null;

            foreach (string decl in SplitDeclarations(block))
            {
                if (!TrySplitDeclaration(decl, out string prop, out string value, out _)) continue;
                switch (DeclarationKey(prop))
                {
                    case "font-family": family = UnquoteFamily(value); break;
                    case "font-weight":
                        bold = Eq(value, "bold") || Eq(value, "bolder")
                               || (int.TryParse(value.Split(' ')[0], out int w) && w >= 600);
                        break;
                    case "font-style": italic = Eq(value, "italic") || Eq(value, "oblique"); break;
                    case "src": src = value; break;
                }
            }

            if (family.Length == 0 || src == null) return;
            byte[]? program = LoadFontFaceSource(src, family);
            if (program == null) return;

            _fontFaces.Add(new FontFaceRule
            {
                Family = family, Bold = bold, Italic = italic, Program = program,
            });
        }

        /// <summary>
        /// Walks a <c>src:</c> list and returns the bytes of the first entry this engine can
        /// actually read — a local <c>url()</c> inside <c>BasePath</c>, a remote one through the
        /// loader, or a <c>data:</c> payload — whose bytes are a font program it can parse.
        ///
        /// <para><b>Read is not enough; the bytes must parse.</b> A <c>src</c> list is a list of
        /// alternatives in different formats, and a browser passes over the ones it does not
        /// support. Bootstrap 3's list — like most generated ones — starts with an <c>.eot</c>, so
        /// returning the first file that can merely be <i>read</i> would never reach the WOFF and
        /// TTF after it, and the icons would fall back to whatever font the host had.
        /// <c>TrueTypeFontProgram.Parse</c> is the test: it unwraps WOFF 1.0 and WOFF 2.0 and
        /// answers <c>null</c> for EOT and SVG fonts. A list with nothing parseable in it is
        /// reported once, naming the last entry that was read.</para>
        /// </summary>
        private byte[]? LoadFontFaceSource(string src, string family)
        {
            // The last entry that was read but is not a font this engine parses, for the report.
            string? unparsed = null;

            byte[]? Accept(byte[] bytes, string source)
            {
                if (Tetitulz.Pdf.Core.Fonts.TrueTypeFontProgram.Parse(bytes) != null) return bytes;
                unparsed = source;
                return null;
            }

            foreach (string entry in SplitSrcList(src))
            {
                string e = entry.Trim();
                if (e.Length == 0) continue;
                if (_fontFaceWork >= MaxFontFaceWork) break;   // every entry tried counts, parsed or not
                _fontFaceWork++;

                // format() is read for nothing now: WOFF 1.0, WOFF 2.0 and a bare sfnt all reach
                // Core.Fonts.TrueTypeFontProgram.Parse, which recognises the container from its own
                // signature. Deciding on the DECLARED format was always the weaker test anyway —
                // format() is optional in CSS and stylesheets routinely omit it.
                string? url = MatchFunction(e, "url");
                if (url == null) continue;                    // local(…) → AllowSystemFontSubstitution's job
                url = UnquoteFamily(url);

                if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    byte[]? inline = DecodeDataUri(url);
                    if (inline != null && Accept(inline, url) is { } font) return font;
                    continue;
                }

                // A remote source goes through the one remote path, and only when the caller has
                // supplied a loader. Without one it falls through to the long-standing report below,
                // whose wording tests and callers already match on.
                if (IsRemoteUrl(url) && _options.RemoteResourceLoader != null)
                {
                    byte[]? fetched = LoadRemote(url, "@font-face source for '" + family + "'",
                                                 BuilderDiagnosticKind.FontFormatRejected);
                    if (fetched != null && Accept(fetched, url) is { } font) return font;
                    continue;
                }

                string? path = ResolveLocalPath(url);
                if (path == null)
                {
                    ReportDiagnostic(BuilderDiagnosticKind.FontFormatRejected,
                        $"@font-face for '{family}' references '{url}', which is remote or outside " +
                        "HtmlToPdfOptions.BasePath; the managed engine never fetches fonts over the network " +
                        "unless HtmlToPdfOptions.RemoteResourceLoader is set.");
                    continue;
                }
                if (!_fontFaceFileBytes.TryGetValue(path, out byte[]? bytes))
                {
                    try
                    {
                        long length = new FileInfo(path).Length;
                        if (length > MaxFontFileBytes)
                        {
                            ReportDiagnostic(BuilderDiagnosticKind.FontFormatRejected,
                                $"@font-face for '{family}': '{url}' is {length:N0} bytes, past the " +
                                $"{MaxFontFileBytes:N0}-byte font limit; it was not read.");
                            bytes = null;
                        }
                        else
                        {
                            bytes = File.ReadAllBytes(path);
                        }
                    }
                    catch (Exception ex)
                    {
                        ReportDiagnostic(BuilderDiagnosticKind.FontFormatRejected,
                            $"@font-face for '{family}': '{url}' could not be read ({ex.Message}).");
                        bytes = null;
                    }
                    _fontFaceFileBytes[path] = bytes;   // read once per path, success or failure
                }
                if (bytes != null && Accept(bytes, url) is { } local) return local;
            }

            // Only when NOTHING parsed: an unsupported format followed by one that loads is the
            // list working as intended, and reporting it would fire on every Bootstrap page.
            if (unparsed != null)
                ReportDiagnostic(BuilderDiagnosticKind.FontFormatRejected,
                    $"@font-face for '{family}': no src entry is a font this engine reads. " +
                    $"'{Excerpt(unparsed)}' was loaded but is not TrueType, OpenType, WOFF or WOFF2 " +
                    "(EOT and SVG fonts are not supported), so the family falls back.");
            return null;
        }

        // Splits a src: list on top-level commas, keeping url(...) and quoted strings whole.
        private static IEnumerable<string> SplitSrcList(string src)
            => SplitTopLevel(src, ch => ch == ',', keepEmpty: false);

        /// <summary>The argument of <c>name(...)</c> inside <paramref name="entry"/>, or null.</summary>
        private static string? MatchFunction(string entry, string name)
        {
            int i = entry.IndexOf(name + "(", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            int open = i + name.Length;
            int close = ClosingParen(entry, open);
            return close < 0 ? null : entry.Substring(open + 1, close - open - 1).Trim();
        }

        private static string UnquoteFamily(string v)
        {
            v = v.Trim();
            // Not v[^1]: System.Index does not exist on the netstandard2.0 leg this assembly builds.
            if (v.Length >= 2 && (v[0] == '"' || v[0] == '\'') && v[v.Length - 1] == v[0])
                v = v.Substring(1, v.Length - 2);
            return v.Trim();
        }

        /// <summary>
        /// The font source the exporter will consult: <c>@font-face</c> first, then the caller's
        /// <see cref="HtmlToPdfOptions.FontProvider"/>. Host fonts come after both, inside the
        /// Builder's resolver.
        /// </summary>
        private Func<BuilderFontRequest, byte[]?>? BuildFontProvider()
        {
            var faces = _fontFaces;
            var callerProvider = _options.FontProvider;
            if (faces.Count == 0 && callerProvider == null) return null;

            return request =>
            {
                foreach (var face in faces)
                {
                    if (!string.Equals(face.Family, request.FamilyName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    // Style is a preference, not a filter: a family that ships only a regular
                    // face should still be used for its bold text rather than dropping to a
                    // fallback that does not look like the requested family at all.
                    if (face.Bold == request.Bold && face.Italic == request.Italic) return face.Program;
                }
                foreach (var face in faces)
                    if (string.Equals(face.Family, request.FamilyName, StringComparison.OrdinalIgnoreCase))
                        return face.Program;

                return callerProvider?.Invoke(request);
            };
        }

        /// <summary>A short, single-line excerpt of a value, for a diagnostic message.</summary>
        private static string Excerpt(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            string s = value.Replace('\n', ' ').Replace('\r', ' ');
            return s.Length <= 60 ? s : s.Substring(0, 60) + "…";
        }

        /// <summary>
        /// Delivers one tolerated failure to the caller's <c>OnDiagnostic</c>.
        /// </summary>
        /// <remarks>
        /// Through <see cref="DiagnosticFanOut"/>, the one implementation of this rule. A
        /// <c>try</c>/<c>catch</c> round a single multicast invoke is the half that is easy to see:
        /// it keeps a throwing handler from breaking the conversion, but the invoke skips every
        /// handler after the one that threw. A host
        /// aggregating telemetry attaches more than one.
        /// </remarks>
        private void ReportDiagnostic(BuilderDiagnosticKind kind, string detail)
        {
            var d = new BuilderDiagnostic(kind, detail);
            DiagnosticFanOut.Invoke(_options.OnDiagnostic, d);
        }
    }
}
