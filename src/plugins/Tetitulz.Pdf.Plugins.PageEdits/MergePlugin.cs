// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.PageEdits
{
    /// <summary>
    /// <see cref="IMergePlugin"/> that merges multiple PDF streams into one
    /// using the built-in <see cref="PdfMerger"/>.
    ///
    /// Registration:
    /// <code>
    ///   PdfPluginHost.Instance.Register(new MergePlugin());
    /// </code>
    /// </summary>
    public sealed class MergePlugin : IMergePlugin
    {
        public string Name        => "Tetitulz.Plugins.Merge";
        public string Description => "Merges multiple PDF documents into one.";
        public Version Version    => new Version(1, 0, 0);

        public void Initialize() { }
        public void Shutdown()   { }

        public void Merge(Stream output, IReadOnlyList<Stream> inputs)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (inputs == null) throw new ArgumentNullException(nameof(inputs));
            PdfMerger.Merge(output, inputs.ToArray());
        }
    }
}
