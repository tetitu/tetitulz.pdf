// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Plugins.CMap;

namespace Tetitulz.Pdf.Plugins.CMap.Tests
{
    public sealed class AdobeCMapPluginTests
    {
        [Fact]
        public void Initialize_RegistersAllFourCjkUnicodeCMaps()
        {
            var plugin = new AdobeCMapPlugin();
            plugin.Initialize();

            // Each CJK ordering has a primary Unicode CMap that FontCache.cs checks.
            Assert.True(PredefinedCMapRegistry.IsRegistered("UniJIS-UTF16-H"),  "Japan1");
            Assert.True(PredefinedCMapRegistry.IsRegistered("UniGB-UTF16-H"),   "GB1 (Simplified Chinese)");
            Assert.True(PredefinedCMapRegistry.IsRegistered("UniCNS-UTF16-H"),  "CNS1 (Traditional Chinese)");
            Assert.True(PredefinedCMapRegistry.IsRegistered("UniKS-UTF16-H"),   "Korea1");
        }

        [Fact]
        public void Initialize_RegistersAllEmbeddedResources()
        {
            var plugin = new AdobeCMapPlugin();
            plugin.Initialize();

            // All 167 CMap files from 4 collections must be registered.
            var asm = typeof(AdobeCMapPlugin).Assembly;
            const string prefix = "Tetitulz.Pdf.Plugins.CMap.CMapResources.";
            int count = 0;
            foreach (var name in asm.GetManifestResourceNames())
            {
                if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                string remainder = name.Substring(prefix.Length);
                int lastDot = remainder.LastIndexOf('.');
                string cmapName = lastDot >= 0 ? remainder.Substring(lastDot + 1) : remainder;
                if (string.IsNullOrEmpty(cmapName)) continue;
                Assert.True(PredefinedCMapRegistry.IsRegistered(cmapName), $"Expected '{cmapName}' to be registered");
                count++;
            }
            Assert.Equal(167, count);
        }

        [Fact]
        public void Initialize_IsIdempotent()
        {
            var plugin = new AdobeCMapPlugin();
            plugin.Initialize();
            plugin.Initialize(); // must not throw
            Assert.True(PredefinedCMapRegistry.IsRegistered("UniJIS-UTF16-H"));
        }

        [Fact]
        public void Plugin_MetadataIsCorrect()
        {
            var plugin = new AdobeCMapPlugin();
            Assert.Equal("Tetitulz.Plugins.AdobeCMap", plugin.Name);
            Assert.Equal(new Version(1, 0, 0), plugin.Version);
        }
    }
}
