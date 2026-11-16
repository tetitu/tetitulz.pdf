// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Docx.Tests
{
    /// <summary>
    /// Pins <c>Tetitulz.Pdf.Plugins.Docx</c>'s exported surface against
    /// <c>tests/ApiSnapshots/Tetitulz.Pdf.Plugins.Docx.txt</c>, so a breaking change to a shipping
    /// package's public API fails a test instead of reaching a consumer.
    ///
    /// <para>The renderer is the shared <see cref="PublicApiSnapshot"/> used by every other
    /// baseline in the repo, so all the baselines stay directly comparable and cannot drift in
    /// format.</para>
    /// </summary>
    public class PublicApiSurfaceTests
    {
        private static Assembly PluginAssembly => typeof(DocxExportPlugin).Assembly;

        [ApiBaselineFact]
        public void Docx_plugin_exported_surface_matches_its_baseline()
        {
            string? report = PublicApiSnapshot.Compare(PluginAssembly);
            Assert.True(report == null, report);
        }

        /// <summary>
        /// A floor for the guard itself: a baseline that rendered as nothing would compare equal to
        /// an empty file and pass forever after.
        ///
        /// <para>The floor names all five exported types rather than trusting a line count alone,
        /// because this package's contract is the import/export adapter <i>pair</i> plus the three
        /// converters — losing either half is exactly the break the baseline exists to catch. The
        /// count is what the package actually exports today (two plugins of ten members each plus
        /// eight converter lines), not a round number.</para>
        /// </summary>
        [ApiBaselineFact]
        public void The_rendered_surface_covers_both_adapters_and_all_three_converters()
        {
            string rendered = PublicApiSnapshot.Render(PluginAssembly);

            Assert.Contains("class Tetitulz.Pdf.Plugins.Docx.DocxExportPlugin", rendered);
            Assert.Contains("class Tetitulz.Pdf.Plugins.Docx.DocxImportPlugin", rendered);
            Assert.Contains("class Tetitulz.Pdf.Plugins.Docx.PdfToDocxConverter", rendered);
            Assert.Contains("class Tetitulz.Pdf.Plugins.Docx.PdfToExcelConverter", rendered);
            Assert.Contains("class Tetitulz.Pdf.Plugins.Docx.PdfToPptxConverter", rendered);

            // The two operations the adapters exist to provide, not merely their metadata.
            Assert.Contains("DocxExportPlugin::Export(", rendered);
            Assert.Contains("DocxImportPlugin::Import(", rendered);

            int lines = rendered.Split('\n', System.StringSplitOptions.RemoveEmptyEntries).Length;
            Assert.True(lines >= 24,
                $"Only {lines} lines rendered for Tetitulz.Pdf.Plugins.Docx; the renderer is " +
                "filtering the surface out, so the baseline comparison would be vacuous.");
        }
    }
}
