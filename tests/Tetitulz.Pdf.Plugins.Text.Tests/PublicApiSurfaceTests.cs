// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Text.Tests
{
    /// <summary>
    /// Pins <c>Tetitulz.Pdf.Plugins.Text</c>'s exported surface against
    /// <c>tests/ApiSnapshots/Tetitulz.Pdf.Plugins.Text.txt</c>, so a breaking change to a shipping
    /// package's public API fails a test instead of reaching a consumer.
    ///
    /// <para>The renderer is the shared <see cref="PublicApiSnapshot"/> used by every other
    /// baseline in the repo, so all the baselines stay directly comparable and cannot drift in
    /// format.</para>
    /// </summary>
    public class PublicApiSurfaceTests
    {
        private static Assembly PluginAssembly => typeof(TextExportPlugin).Assembly;

        [ApiBaselineFact]
        public void Text_plugin_exported_surface_matches_its_baseline()
        {
            string? report = PublicApiSnapshot.Compare(PluginAssembly);
            Assert.True(report == null, report);
        }

        /// <summary>
        /// A floor for the guard itself: a baseline that rendered as nothing would compare equal to
        /// an empty file and pass forever after.
        ///
        /// <para>This package exports one type, so the floor names the <c>IExportPlugin</c> contract
        /// member by member rather than trusting a line count alone. <c>FormatKey</c> and
        /// <c>TargetMimeType</c> are named explicitly because they are how a host <i>selects</i>
        /// this exporter by string key — losing one breaks dispatch without changing any signature
        /// a caller writes. Ten lines is what the package exports today (the class, its
        /// constructor, five properties and three methods), not a round number.</para>
        /// </summary>
        [ApiBaselineFact]
        public void The_rendered_surface_covers_the_whole_export_contract()
        {
            string rendered = PublicApiSnapshot.Render(PluginAssembly);

            Assert.Contains("class Tetitulz.Pdf.Plugins.Text.TextExportPlugin", rendered);
            Assert.Contains("TextExportPlugin::Export(", rendered);
            Assert.Contains("TextExportPlugin::FormatKey", rendered);
            Assert.Contains("TextExportPlugin::TargetMimeType", rendered);
            Assert.Contains("TextExportPlugin::Initialize()", rendered);
            Assert.Contains("TextExportPlugin::Shutdown()", rendered);

            int lines = rendered.Split('\n', System.StringSplitOptions.RemoveEmptyEntries).Length;
            Assert.True(lines >= 10,
                $"Only {lines} lines rendered for Tetitulz.Pdf.Plugins.Text; the renderer is " +
                "filtering the surface out, so the baseline comparison would be vacuous.");
        }
    }
}
