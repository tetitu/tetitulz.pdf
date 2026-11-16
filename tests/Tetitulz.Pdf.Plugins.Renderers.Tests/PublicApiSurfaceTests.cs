// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Renderers.Tests
{
    /// <summary>
    /// Pins <c>Tetitulz.Pdf.Plugins.Renderers</c>'s exported surface against
    /// <c>tests/ApiSnapshots/Tetitulz.Pdf.Plugins.Renderers.txt</c>, so a breaking change to a
    /// shipping package's public API fails a test instead of reaching a consumer.
    ///
    /// <para>The renderer is the shared <see cref="PublicApiSnapshot"/> used by every other
    /// baseline in the repo, so all the baselines stay directly comparable and cannot drift in
    /// format.</para>
    /// </summary>
    public class PublicApiSurfaceTests
    {
        private static Assembly PluginAssembly => typeof(SkiaSharpRendererPlugin).Assembly;

        [ApiBaselineFact]
        public void Renderers_plugin_exported_surface_matches_its_baseline()
        {
            string? report = PublicApiSnapshot.Compare(PluginAssembly);
            Assert.True(report == null, report);
        }

        /// <summary>
        /// A floor for the guard itself: a baseline that rendered as nothing would compare equal to
        /// an empty file and pass forever after.
        ///
        /// <para>This package exports one type, so the floor names the <c>IRendererPlugin</c>
        /// contract member by member rather than trusting a line count alone. <c>Priority</c> is
        /// named explicitly because it is the member that decides whether this renderer is
        /// <i>selected</i> at all — dropping it changes dispatch without changing any signature a
        /// caller writes. Ten lines is what the package exports today (the class, its constructor,
        /// four properties and four methods), not a round number.</para>
        /// </summary>
        [ApiBaselineFact]
        public void The_rendered_surface_covers_the_whole_renderer_contract()
        {
            string rendered = PublicApiSnapshot.Render(PluginAssembly);

            Assert.Contains("class Tetitulz.Pdf.Plugins.Renderers.SkiaSharpRendererPlugin", rendered);
            Assert.Contains("SkiaSharpRendererPlugin::RenderPage(", rendered);
            Assert.Contains("SkiaSharpRendererPlugin::CanRender(", rendered);
            Assert.Contains("SkiaSharpRendererPlugin::Priority", rendered);
            Assert.Contains("SkiaSharpRendererPlugin::Initialize()", rendered);
            Assert.Contains("SkiaSharpRendererPlugin::Shutdown()", rendered);

            int lines = rendered.Split('\n', System.StringSplitOptions.RemoveEmptyEntries).Length;
            Assert.True(lines >= 10,
                $"Only {lines} lines rendered for Tetitulz.Pdf.Plugins.Renderers; the renderer is " +
                "filtering the surface out, so the baseline comparison would be vacuous.");
        }
    }
}
