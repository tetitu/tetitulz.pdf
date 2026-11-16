// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.PostScript
{
    /// <summary>
    /// Renders embedded PostScript — <c>/Subtype /PS</c> XObjects (ISO 32000-1 §8.10.7) and the
    /// legacy inline <c>PS</c> operator — by converting it to a PDF content stream that the host's
    /// renderers execute through their form-XObject path.
    ///
    /// <para>Register it explicitly:</para>
    /// <code>
    ///   PdfPluginHost.Instance.Register(new PostScriptXObjectPlugin());
    /// </code>
    /// <para>or let <c>PdfPluginHost.Instance.RegisterFromLoadedAssemblies()</c> find it — this
    /// assembly carries <c>PdfPluginAssemblyAttribute</c> and matches the
    /// <c>Tetitulz.Pdf.Plugins.</c> prefix.</para>
    ///
    /// <para><b>What it draws.</b> Vector artwork (paths, painting, clipping, the graphics-state
    /// stack, transformations, device colour, line style), <b>text</b> — mapped onto the standard 14
    /// and measured with real AFM metrics, or using the document's own Type 1 program when it embeds
    /// one — and <b>images</b> (<c>image</c> / <c>imagemask</c> / <c>colorimage</c> with a string
    /// data source). What it does not do is recorded in
    /// <see cref="PostScriptConversionResult.UnimplementedOperators"/> so a caller can tell a
    /// complete conversion from a partial one. The interpreter runs the whole language regardless,
    /// so a program's control flow and arithmetic are honoured even where its output is not.</para>
    ///
    /// <para><b>It never throws.</b> A conversion failure returns <c>false</c>, which makes the
    /// host fall back to its documented placeholder — losing one XObject rather than the page.</para>
    /// </summary>
    public sealed class PostScriptXObjectPlugin : IPostScriptPlugin
    {
        private readonly PostScriptConversionOptions _options;

        /// <summary>Uses <see cref="PostScriptConversionOptions.Default"/>.</summary>
        public PostScriptXObjectPlugin() : this(null) { }

        /// <summary>Uses <paramref name="options"/>, for a host that needs different bounds.</summary>
        public PostScriptXObjectPlugin(PostScriptConversionOptions options)
            => _options = options ?? PostScriptConversionOptions.Default;

        public string Name        => "Tetitulz.Plugins.PostScript";
        public string Description => "Pure-managed PostScript interpreter for /Subtype /PS XObjects " +
                                     "and the legacy inline PS operator.";
        public Version Version    => new(1, 0, 0);

        public void Initialize() { }
        public void Shutdown() { }

        /// <inheritdoc/>
        public bool TryConvertToPdfContent(PostScriptContentRequest request,
                                           out PostScriptContentResult result)
        {
            result = null;

            // Cleared FIRST, on every path. Two of the early returns below decline without ever
            // reaching ConvertFragment (which does its own Clear), so a host that gets `false`,
            // draws its placeholder and logs LastError to explain why was reading the PREVIOUS
            // XObject's diagnostics — a fact about a different piece of content.
            PostScriptDiagnostics.Clear();

            if (request == null)
            {
                PostScriptDiagnostics.Report("PostScriptXObjectPlugin", "the request was null");
                return false;
            }

            // Prefer /Level1 when present: a producer supplies it precisely because the main stream
            // needs a Level 2/3 interpreter, and this one is closer to Level 1 in coverage.
            byte[] source = request.Level1PostScript is { Length: > 0 }
                ? request.Level1PostScript
                : request.PostScript;

            if (source == null || source.Length == 0)
            {
                PostScriptDiagnostics.Report("PostScriptXObjectPlugin",
                    "the request carried no PostScript program");
                return false;
            }

            PostScriptConversionResult converted;
            try
            {
                converted = PostScriptConversion.ConvertFragment(source, _options);
            }
            catch (Exception ex)
            {
                // ConvertFragment already contains its own failures; this is belt-and-braces, since
                // an escaping exception here would be reported by the host as a plugin fault and
                // cost the XObject anyway.
                PostScriptDiagnostics.Report("PostScriptXObjectPlugin",
                    "conversion threw; declining so the host draws its placeholder", ex);
                return false;
            }

            // Decline rather than hand back an empty stream. The host treats empty output as a
            // plugin failure and reports it, where declining is the honest signal that this
            // PostScript produced no marks.
            if (converted.Content == null || converted.Content.Length == 0) return false;

            result = new PostScriptContentResult
            {
                ContentStream = converted.Content,
                Resources     = converted.Resources
            };
            return true;
        }
    }
}
