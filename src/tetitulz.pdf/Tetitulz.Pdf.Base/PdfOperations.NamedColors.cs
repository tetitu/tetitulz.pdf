// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Base.Layout;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Scripting;
using Tetitulz.Pdf.Base.Signing;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base
{
    public static partial class PdfOperations
    {
        /// <summary>
        /// Resolves <paramref name="name"/> through an ICC named-colour profile (class <c>nmcl</c>) — the
        /// colour's full name (prefix, root, suffix) or its root, exactly or ignoring case. The colour is the profile's own: its Lab, its device coordinates and the
        /// sRGB they convert to.
        /// </summary>
        /// <returns>False, with <paramref name="color"/> null, when the profile names no such colour.</returns>
        /// <exception cref="ArgumentException">The bytes are not a readable named-colour profile; the message says why.</exception>
        public static bool TryResolveNamedColor(string name, byte[] namedColorProfile, out IccNamedColor color)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            color = ParseNamedColors(namedColorProfile).Find(name);
            return color != null;
        }

        /// <summary>The colours an ICC named-colour profile (class <c>nmcl</c>) defines, in its order.</summary>
        /// <exception cref="ArgumentException">The bytes are not a readable named-colour profile; the message says why.</exception>
        public static IReadOnlyList<IccNamedColor> ReadNamedColors(byte[] namedColorProfile)
            => ParseNamedColors(namedColorProfile).Colours;

        private static Rendering.IccNamedColors ParseNamedColors(byte[] profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            return Rendering.IccNamedColors.TryParse(profile, out string reason)
                ?? throw new ArgumentException("Not a readable ICC named-colour profile: " + reason, nameof(profile));
        }
    }
}
