// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.CMap.Tests
{
    /// <summary>
    /// Pins <c>Tetitulz.Pdf.Plugins.CMap</c>'s exported surface against
    /// <c>tests/ApiSnapshots/Tetitulz.Pdf.Plugins.CMap.txt</c>, so a breaking change to a shipping
    /// package's public API fails a test instead of reaching a consumer.
    ///
    /// <para>The renderer is the shared <see cref="PublicApiSnapshot"/> used by every other
    /// baseline in the repo, so all the baselines stay directly comparable and cannot drift in
    /// format.</para>
    /// </summary>
    public class PublicApiSurfaceTests
    {
        private static Assembly PluginAssembly => typeof(AdobeCMapPlugin).Assembly;

        [ApiBaselineFact]
        public void CMap_plugin_exported_surface_matches_its_baseline()
        {
            string? report = PublicApiSnapshot.Compare(PluginAssembly);
            Assert.True(report == null, report);
        }

        /// <summary>
        /// A floor for the guard itself: a baseline that rendered as nothing would compare equal to
        /// an empty file and pass forever after.
        ///
        /// <para>This package exports exactly one type — the <c>IPdfPlugin</c> implementation — so
        /// the floor names that contract member by member rather than trusting a line count alone.
        /// Seven lines is what the package actually exports today (the class, its constructor, the
        /// three metadata properties and the two lifecycle methods), not a round number.</para>
        /// </summary>
        [ApiBaselineFact]
        public void The_rendered_surface_covers_the_whole_plugin_contract()
        {
            string rendered = PublicApiSnapshot.Render(PluginAssembly);

            Assert.Contains("class Tetitulz.Pdf.Plugins.CMap.AdobeCMapPlugin", rendered);
            Assert.Contains("AdobeCMapPlugin::Initialize()", rendered);
            Assert.Contains("AdobeCMapPlugin::Shutdown()", rendered);
            Assert.Contains("AdobeCMapPlugin::Name", rendered);
            Assert.Contains("AdobeCMapPlugin::Description", rendered);
            Assert.Contains("AdobeCMapPlugin::Version", rendered);

            int lines = rendered.Split('\n', System.StringSplitOptions.RemoveEmptyEntries).Length;
            Assert.True(lines >= 7,
                $"Only {lines} lines rendered for Tetitulz.Pdf.Plugins.CMap; the renderer is " +
                "filtering the surface out, so the baseline comparison would be vacuous.");
        }
    }
}
