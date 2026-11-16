// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// Mixed licences: this package (Apache-2.0 AND OFL-1.1) also carries the Liberation and Noto fonts,
// which keep their own licence. See NOTICE.md at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.Fonts
{
    /// <summary>
    /// The plugin face of <see cref="BundledFonts"/>: <see cref="Initialize"/> writes the fonts to the
    /// default directory and registers it; <see cref="Shutdown"/> removes the directory from the
    /// search path (the files stay, for the next start). Registration:
    /// <code>
    ///   PdfPluginHost.Instance.Register(new BundledFontsPlugin());
    /// </code>
    /// or, without the plugin host, <c>BundledFonts.Register()</c>.
    /// </summary>
    public sealed class BundledFontsPlugin : IPdfPlugin
    {
        public string Name        => "Tetitulz.Plugins.Fonts";
        public string Description => "Registers the bundled Liberation and Noto Sans Hebrew and Arabic fonts (SIL Open Font License) first in the font search path.";
        public Version Version    => new Version(1, 0, 0);

        public void Initialize() => BundledFonts.Register();

        public void Shutdown() => BundledFonts.Unregister();
    }
}
