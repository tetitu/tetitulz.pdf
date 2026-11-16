// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// Reaches the plugin's internal <c>PsCidFonts</c> code-counting rule.
///
/// <para>The rule decides where the pen lands after a CJK <c>show</c>, and it differs between fixed
/// two-byte CMaps and the mixed-width legacy ones. Testing it through a full conversion would only
/// observe it indirectly, through a coordinate that several other things also influence; the plugin
/// grants <c>InternalsVisibleTo</c> to this test project precisely so a rule like this can be
/// asserted on its own.</para>
/// </summary>
internal static class PsCidFontsProbe
{
    public static int CountCodes(string cmapName, byte[] bytes)
        => Emit.PsCidFonts.CountCodes(cmapName, bytes);
}
