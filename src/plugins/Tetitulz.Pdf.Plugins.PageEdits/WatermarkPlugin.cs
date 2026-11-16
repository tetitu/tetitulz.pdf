// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.IO;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.PageEdits
{
    /// <summary>
    /// <see cref="IWatermarkPlugin"/> that stamps a diagonal text watermark on all PDF pages
    /// using the built-in <see cref="PdfWatermarker"/>.
    ///
    /// Registration:
    /// <code>
    ///   PdfPluginHost.Instance.Register(new WatermarkPlugin());
    /// </code>
    /// </summary>
    public sealed class WatermarkPlugin : IWatermarkPlugin
    {
        public string Name        => "Tetitulz.Plugins.Watermark";
        public string Description => "Stamps a diagonal text watermark on all PDF pages.";
        public Version Version    => new Version(1, 0, 0);

        public void Initialize() { }
        public void Shutdown()   { }

        public void AddWatermark(Stream input, Stream output, PluginWatermarkOptions options)
        {
            if (input == null)   throw new ArgumentNullException(nameof(input));
            if (output == null)  throw new ArgumentNullException(nameof(output));
            if (options == null) throw new ArgumentNullException(nameof(options));

            var opts = new WatermarkOptions
            {
                Text         = options.Text,
                FontName     = options.FontName,
                FontSize     = options.FontSize,
                AngleDegrees = options.AngleDegrees,
                Opacity      = options.Opacity,
                ColorR       = options.ColorR,
                ColorG       = options.ColorG,
                ColorB       = options.ColorB,
            };
            PdfWatermarker.AddWatermark(input, output, opts);
        }
    }
}
