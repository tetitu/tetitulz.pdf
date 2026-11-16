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
    /// <see cref="IOptimizePlugin"/> that compresses streams and removes unused objects
    /// using the built-in <see cref="PdfOptimizer"/>.
    ///
    /// Registration:
    /// <code>
    ///   PdfPluginHost.Instance.Register(new OptimizerPlugin());
    /// </code>
    /// </summary>
    public sealed class OptimizerPlugin : IOptimizePlugin
    {
        public string Name        => "Tetitulz.Plugins.Optimizer";
        public string Description => "Optimises PDF streams and removes unused objects.";
        public Version Version    => new Version(1, 0, 0);

        public void Initialize() { }
        public void Shutdown()   { }

        public void Optimize(Stream input, Stream output)
        {
            if (input == null)  throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            PdfOptimizer.Optimize(input, output);
        }
    }
}
