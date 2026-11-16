// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Fonts.Tests
{
    /// <summary>
    /// Pins <c>Tetitulz.Pdf.Plugins.Fonts</c>'s exported surface against
    /// <c>tests/ApiSnapshots/Tetitulz.Pdf.Plugins.Fonts.txt</c>, so a breaking change to a shipping
    /// package's public API fails a test instead of reaching a consumer. The renderer is the shared
    /// <see cref="PublicApiSnapshot"/> every other baseline uses.
    /// </summary>
    public class PublicApiSurfaceTests
    {
        private static Assembly PluginAssembly => typeof(BundledFontsPlugin).Assembly;

        [ApiBaselineFact]
        public void Fonts_plugin_exported_surface_matches_its_baseline()
        {
            string? report = PublicApiSnapshot.Compare(PluginAssembly);
            Assert.True(report == null, report);
        }

        /// <summary>A floor for the guard: the two public types, member by member, so an empty render cannot pass.</summary>
        [ApiBaselineFact]
        public void The_rendered_surface_covers_the_plugin_and_the_fonts()
        {
            string rendered = PublicApiSnapshot.Render(PluginAssembly);

            Assert.Contains("class Tetitulz.Pdf.Plugins.Fonts.BundledFontsPlugin", rendered);
            Assert.Contains("BundledFontsPlugin::Initialize()", rendered);
            Assert.Contains("BundledFontsPlugin::Shutdown()", rendered);
            Assert.Contains("class Tetitulz.Pdf.Plugins.Fonts.BundledFonts", rendered);
            Assert.Contains("BundledFonts::Register()", rendered);
            Assert.Contains("BundledFonts::Register(System.String)", rendered);
            Assert.Contains("BundledFonts::Extract(System.String)", rendered);
            Assert.Contains("BundledFonts::Unregister()", rendered);
            Assert.Contains("BundledFonts::FileNames", rendered);
        }
    }
}
