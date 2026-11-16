// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.IO;
// PdfSanitizer and SanitizeOptions live in Core, where Plugins.Conformance reaches them without a
// plugin -> plugin reference; this plugin is their discovery wrapper.
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.Maintenance
{
    /// <summary>
    /// <see cref="ISanitizePlugin"/> that removes JavaScript, launch actions, embedded files,
    /// and other potentially dangerous content using the built-in <see cref="PdfSanitizer"/>.
    ///
    /// Registration:
    /// <code>
    ///   PdfPluginHost.Instance.Register(new SanitizerPlugin());
    /// </code>
    /// </summary>
    public sealed class SanitizerPlugin : ISanitizePlugin
    {
        public string Name        => "Tetitulz.Plugins.Sanitizer";
        public string Description => "Removes JavaScript, launch actions, and other dangerous PDF content.";
        public Version Version    => new Version(1, 0, 0);

        public void Initialize() { }
        public void Shutdown()   { }

        public void Sanitize(Stream input, Stream output)
        {
            if (input == null)  throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            PdfSanitizer.Sanitize(input, output);
        }
    }
}
