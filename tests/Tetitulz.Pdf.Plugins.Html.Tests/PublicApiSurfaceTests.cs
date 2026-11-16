// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Html.Tests
{
    /// <summary>
    /// Pins <c>Tetitulz.Pdf.Plugins.Html</c>'s exported surface against
    /// <c>tests/ApiSnapshots/Tetitulz.Pdf.Plugins.Html.txt</c>, so a breaking change to a shipping
    /// package's public API fails a test instead of reaching a consumer.
    ///
    /// <para>The renderer is the shared <see cref="PublicApiSnapshot"/> used by every other
    /// baseline in the repo, so all the baselines stay directly comparable and cannot drift in
    /// format.</para>
    /// </summary>
    public class PublicApiSurfaceTests
    {
        private static Assembly PluginAssembly => typeof(HtmlExportPlugin).Assembly;

        [ApiBaselineFact]
        public void Html_plugin_exported_surface_matches_its_baseline()
        {
            string? report = PublicApiSnapshot.Compare(PluginAssembly);
            Assert.True(report == null, report);
        }

        /// <summary>
        /// A floor for the guard itself: a baseline that rendered as nothing would compare equal to
        /// an empty file and pass forever after.
        ///
        /// <para>The floor names all four exported types and the two conversion entry points that
        /// differ in kind — <c>ConvertToHtml</c> rasterises each page, <c>ConvertToLayoutHtml</c>
        /// emits positioned text — because losing either is a break the line count alone would not
        /// localise. The count is what the package exports today (two ten-member adapters plus the
        /// options class and the converter), not a round number.</para>
        /// </summary>
        [ApiBaselineFact]
        public void The_rendered_surface_covers_both_adapters_and_both_conversion_modes()
        {
            string rendered = PublicApiSnapshot.Render(PluginAssembly);

            Assert.Contains("class Tetitulz.Pdf.Plugins.Html.HtmlExportPlugin", rendered);
            Assert.Contains("class Tetitulz.Pdf.Plugins.Html.HtmlImportPlugin", rendered);
            Assert.Contains("class Tetitulz.Pdf.Plugins.Html.HtmlConversionOptions", rendered);
            Assert.Contains("class Tetitulz.Pdf.Plugins.Html.PdfHtmlConverter", rendered);

            Assert.Contains("HtmlExportPlugin::Export(", rendered);
            Assert.Contains("HtmlImportPlugin::Import(", rendered);
            Assert.Contains("PdfHtmlConverter::ConvertToHtml(", rendered);
            Assert.Contains("PdfHtmlConverter::ConvertToLayoutHtml(", rendered);

            int lines = rendered.Split('\n', System.StringSplitOptions.RemoveEmptyEntries).Length;
            Assert.True(lines >= 28,
                $"Only {lines} lines rendered for Tetitulz.Pdf.Plugins.Html; the renderer is " +
                "filtering the surface out, so the baseline comparison would be vacuous.");
        }
    }
}
