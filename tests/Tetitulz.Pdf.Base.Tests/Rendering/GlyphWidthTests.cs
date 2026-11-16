// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// §9.4.4's <c>w₀</c>: a declared width of zero is a real width, not missing data.
/// </summary>
/// <remarks>
/// <para>Four sites across the two renderers computed this, and three agreed. The fourth — the
/// raster renderer's <c>DrawInvisibleRun</c>, render mode 3, the OCR-overlay mode — coerced any
/// non-positive width to the 500-unit default with <b>no</b> <c>HasExplicitWidth</c> escape. A
/// Type 0 font may carry an explicit <c>0</c> in <c>/W</c> (the Hebrew-nikud and combining-mark
/// case), so that glyph advanced 6 pt at 12 pt type in the raster invisible path and 0 pt in the
/// SVG export — and the raster renderer disagreed <i>with itself</i>, since the same glyph under
/// render mode 0 took the guarded path. The searchable overlay drifted from the glyphs it exists
/// to overlay, by one default advance per combining mark.</para>
///
/// <para><b>A defect in an advance is invisible to a pixel-comparing parity suite</b> — every glyph
/// stays internally correct and only what follows moves — which is why this survived a sweep that
/// compared the two renderers' output directly.</para>
/// </remarks>
public sealed class GlyphWidthTests
{
    /// <summary>An explicitly declared zero is the advance, and is not replaced.</summary>
    [Fact]
    public void AnExplicitZeroWidthIsKept()
    {
        Assert.Equal(0d, GlyphWidth.Units(new StubFont(width: 0, explicitly: true), 500));
    }

    /// <summary>A zero the font does <b>not</b> declare is missing data, and takes the default.</summary>
    [Fact]
    public void AnUndeclaredZeroWidthFallsBackToTheDefault()
    {
        Assert.Equal(GlyphWidth.DefaultUnits, GlyphWidth.Units(new StubFont(width: 0, explicitly: false), 500));
    }

    /// <summary>The ordinary case is unchanged — without this the two above could pass on a rule
    /// that ignored the font entirely.</summary>
    [Fact]
    public void AnOrdinaryWidthIsReturnedUnchanged()
    {
        Assert.Equal(650d, GlyphWidth.Units(new StubFont(width: 650, explicitly: true), 500));
    }

    /// <summary>No font at all is missing data.</summary>
    [Fact]
    public void ANullFontFallsBackToTheDefault()
    {
        Assert.Equal(GlyphWidth.DefaultUnits, GlyphWidth.Units(null, 500));
    }

    private sealed class StubFont : PdfFont
    {
        private readonly double _width;
        private readonly bool _explicit;

        internal StubFont(double width, bool explicitly) { _width = width; _explicit = explicitly; }

        public override string Decode(byte[] bytes) => string.Empty;
        public override double GetWidth(int charCode) => _width;
        public override bool HasExplicitWidth(int charCode) => _explicit;
    }
}
