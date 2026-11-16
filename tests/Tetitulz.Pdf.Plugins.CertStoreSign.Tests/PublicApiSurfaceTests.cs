// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.CertStoreSign.Tests
{
    /// <summary>
    /// Pins <c>Tetitulz.Pdf.Plugins.CertStoreSign</c>'s exported surface against
    /// <c>tests/ApiSnapshots/Tetitulz.Pdf.Plugins.CertStoreSign.txt</c>.
    /// </summary>
    public class PublicApiSurfaceTests
    {
        private static Assembly PluginAssembly => typeof(CertStoreExternalSigner).Assembly;

        [ApiBaselineFact]
        public void CertStoreSign_exported_surface_matches_its_baseline()
        {
            string? report = PublicApiSnapshot.Compare(PluginAssembly);
            Assert.True(report == null, report);
        }

        /// <summary>
        /// A floor for the guard itself: a baseline that rendered as nothing would compare equal to
        /// an empty file and pass forever after. The contract is named member by member rather than
        /// trusted to a line count — this package exists to implement <c>IExternalSigner</c>, so
        /// every method of that interface must be visible in the rendered surface.
        /// </summary>
        [ApiBaselineFact]
        public void The_rendered_surface_covers_the_whole_signer_contract()
        {
            string rendered = PublicApiSnapshot.Render(PluginAssembly);

            Assert.Contains("class Tetitulz.Pdf.Plugins.CertStoreSign.CertStoreExternalSigner", rendered);
            Assert.Contains("CertStoreExternalSigner::SignData(", rendered);
            Assert.Contains("CertStoreExternalSigner::SignDigest(", rendered);
            Assert.Contains("CertStoreExternalSigner::GetCertificateChain()", rendered);
            Assert.Contains("CertStoreExternalSigner::EstimatedSignatureSize", rendered);
            Assert.Contains("CertStoreLookup::FindByThumbprint(", rendered);
        }
    }
}