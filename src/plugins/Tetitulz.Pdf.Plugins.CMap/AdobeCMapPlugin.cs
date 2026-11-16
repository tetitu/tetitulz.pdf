// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// Mixed licences: this package (Apache-2.0 AND BSD-3-Clause) also carries the Adobe CMap resources,
// which keep their own licence. See NOTICE.md at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Reflection;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.CMap
{
    /// <summary>
    /// Plugin that registers all bundled Adobe predefined CMaps (Japan1, GB1, CNS1, Korea1)
    /// with <see cref="PredefinedCMapRegistry.Register"/> at startup.
    ///
    /// The CMap files are embedded as assembly resources (BSD-3-Clause, copyright 1990-2023 Adobe).
    /// Source: https://github.com/adobe-type-tools/cmap-resources
    ///
    /// Registration:
    /// <code>
    ///   PdfPluginHost.Instance.Register(new AdobeCMapPlugin());
    /// </code>
    /// After that, CID fonts with Japan1/GB1/CNS1/Korea1 orderings will decode CJK text correctly.
    ///
    /// <para><b>Registration is eager, and costs about 6.5 MB permanently.</b>
    /// <see cref="Initialize"/> reads all 167 embedded CMap files into process-wide
    /// <c>byte[]</c>s whether or not the host ever opens a CJK document — so a service that only
    /// ever processes Latin invoices pays the full cost simply for <i>referencing</i> this
    /// package, since <c>PdfPluginHost.RegisterFromLoadedAssemblies()</c> makes referenced mean
    /// enabled. A host that cannot afford that should not register this plugin.</para>
    ///
    /// <para><b><see cref="Shutdown"/> does not undo <see cref="Initialize"/>.</b>
    /// <c>PdfPluginHost.Unregister("Tetitulz.Plugins.AdobeCMap")</c> returns <c>true</c> and runs
    /// <c>Shutdown</c>, but every CMap stays in
    /// <see cref="PredefinedCMapRegistry"/> and every byte stays alive: CID fonts keep resolving
    /// through a plugin the host believes it removed, and the memory is unreclaimable for the
    /// process lifetime. Undoing it needs a <c>PredefinedCMapRegistry.Unregister</c>, and
    /// materialising a CMap only when a document asks for it needs the registry to accept a
    /// <c>name → bytes</c> resolver instead of 167 eager blobs — both of which belong to
    /// <c>Tetitulz.Pdf.Core.Fonts</c>, not here. This plugin deliberately owns <b>data only</b>:
    /// the mechanism lives in Core and nothing in Core depends on this assembly, which is the
    /// shape CLAUDE.md holds this plugin up as the model of.</para>
    /// </summary>
    public sealed class AdobeCMapPlugin : IPdfPlugin
    {
        public string Name        => "Tetitulz.Plugins.AdobeCMap";
        public string Description => "Registers bundled Adobe predefined CMaps for CJK CID font rendering.";
        public Version Version    => new Version(1, 0, 0);

        // Embedded resource names are: {DefaultNamespace}.CMapResources.{collection}.{cmapName}
        private const string ResourcePrefix = "Tetitulz.Pdf.Plugins.CMap.CMapResources.";

        public void Initialize()
        {
            var asm = typeof(AdobeCMapPlugin).Assembly;
            foreach (var resourceName in asm.GetManifestResourceNames())
            {
                if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                    continue;

                // Derive the CMap name from the last path segment.
                // Path separators become '.'; CMap file names contain '-' but not '.'.
                // E.g. "...CMapResources.Adobe-Japan1-7.UniJIS-UTF16-H" → "UniJIS-UTF16-H"
                string remainder = resourceName.Substring(ResourcePrefix.Length);
                int lastDot = remainder.LastIndexOf('.');
                string cmapName = lastDot >= 0 ? remainder.Substring(lastDot + 1) : remainder;
                if (string.IsNullOrEmpty(cmapName)) continue;

                if (PredefinedCMapRegistry.IsRegistered(cmapName)) continue;

                using var stream = asm.GetManifestResourceStream(resourceName);
                if (stream == null) continue;

                var bytes = new byte[stream.Length];
                Tetitulz.Pdf.Core.IO.StreamBytes.Fill(stream, bytes, 0, bytes.Length);

                PredefinedCMapRegistry.Register(cmapName, bytes);
            }
        }

        /// <summary>
        /// Deliberately a no-op: the CMaps registered by <see cref="Initialize"/> stay registered.
        /// See the type's remarks — unregistering needs a facility
        /// <see cref="PredefinedCMapRegistry"/> does not expose.
        /// </summary>
        public void Shutdown() { }
    }
}
