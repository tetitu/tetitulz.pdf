// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.IO;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.Maintenance
{
    /// <summary>
    /// <see cref="ILinearizePlugin"/> that web-optimises a PDF for fast first-page display
    /// using the built-in <see cref="PdfLinearizer"/>.
    ///
    /// Registration:
    /// <code>
    ///   PdfPluginHost.Instance.Register(new LinearizerPlugin());
    /// </code>
    /// </summary>
    public sealed class LinearizerPlugin : ILinearizePlugin
    {
        public string Name        => "Tetitulz.Plugins.Linearizer";
        public string Description => "Linearises PDFs for fast first-page web display.";
        public Version Version    => new Version(1, 0, 0);

        public void Initialize() { }
        public void Shutdown()   { }

        public void Linearize(Stream input, Stream output)
        {
            if (input == null)  throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            PdfLinearizer.Linearize(input, output);
        }
    }
}
