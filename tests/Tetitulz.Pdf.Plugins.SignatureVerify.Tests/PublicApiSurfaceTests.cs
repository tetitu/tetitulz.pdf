// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Reflection;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.SignatureVerify.Tests
{
    /// <summary>
    /// Pins <c>Tetitulz.Pdf.Plugins.SignatureVerify</c>'s exported surface against
    /// <c>tests/ApiSnapshots/Tetitulz.Pdf.Plugins.SignatureVerify.txt</c>, so a breaking change to a
    /// shipping package's public API fails a test instead of reaching a consumer.
    ///
    /// <para>The renderer is the shared <see cref="PublicApiSnapshot"/> used by every other
    /// baseline in the repo, so all the baselines stay directly comparable and cannot drift in
    /// format.</para>
    /// </summary>
    public class PublicApiSurfaceTests
    {
        private static Assembly PluginAssembly => typeof(SignatureVerifyPlugin).Assembly;

        [ApiBaselineFact]
        public void SignatureVerify_plugin_exported_surface_matches_its_baseline()
        {
            var report = PublicApiSnapshot.Compare(PluginAssembly);
            Assert.True(report == null, report);
        }

        /// <summary>
        /// A floor for the guard itself: a baseline that rendered as nothing would compare equal to
        /// an empty file and pass forever after.
        ///
        /// <para>This package exports one type, so the floor names the contract member by member
        /// rather than trusting a line count alone. <c>VerifyAll</c> is named with its return type
        /// because that type is what a caller reads a verdict out of — a change there is a silent
        /// break for every consumer, which is exactly the case the baseline exists for. Eight lines
        /// is what the package exports today (the class, its constructor, three metadata properties
        /// and three methods), not a round number.</para>
        /// </summary>
        [ApiBaselineFact]
        public void The_rendered_surface_covers_the_whole_plugin_contract()
        {
            string rendered = PublicApiSnapshot.Render(PluginAssembly);

            Assert.Contains("class Tetitulz.Pdf.Plugins.SignatureVerify.SignatureVerifyPlugin", rendered);
            Assert.Contains("SignatureVerifyPlugin::VerifyAll(", rendered);
            Assert.Contains("Tetitulz.Pdf.Signing.Models.SignatureVerificationResult", rendered);
            Assert.Contains("SignatureVerifyPlugin::Initialize()", rendered);
            Assert.Contains("SignatureVerifyPlugin::Shutdown()", rendered);

            int lines = rendered.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
            Assert.True(lines >= 8,
                $"Only {lines} lines rendered for Tetitulz.Pdf.Plugins.SignatureVerify; the " +
                "renderer is filtering the surface out, so the baseline comparison would be vacuous.");
        }
    }
}
