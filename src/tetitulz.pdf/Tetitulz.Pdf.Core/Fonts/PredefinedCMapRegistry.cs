// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// Public entry point for registering Adobe predefined CMaps so CID fonts (CJK, etc.)
    /// decode text correctly.  Delegates to the internal <see cref="CMapParser"/> store.
    ///
    /// Typical use — load via <c>Tetitulz.Pdf.Plugins.CMap.AdobeCMapPlugin</c>:
    /// <code>
    ///   PdfPluginHost.Instance.Register(new AdobeCMapPlugin());
    /// </code>
    /// Or register individual files manually:
    /// <code>
    ///   PredefinedCMapRegistry.Register("UniJIS-UTF16-H", File.ReadAllBytes("UniJIS-UTF16-H"));
    /// </code>
    /// </summary>
    public static class PredefinedCMapRegistry
    {
        /// <summary>
        /// Registers the raw bytes of a predefined CMap file under the given <paramref name="name"/>.
        /// </summary>
        public static void Register(string name, byte[] cmapBytes)
            => CMapParser.RegisterPredefinedCMap(name, cmapBytes);

        /// <summary>
        /// Returns true when a CMap with the given name has been registered.
        /// </summary>
        public static bool IsRegistered(string name)
            => CMapParser.IsPredefinedCMapRegistered(name);
    }
}
