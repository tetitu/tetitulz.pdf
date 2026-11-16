// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Runtime.CompilerServices;
using System.Text;
using Tetitulz.Pdf.Plugins.PostScript.Emit;
using Tetitulz.Pdf.Plugins.PostScript.Graphics;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;
using Tetitulz.Pdf.Plugins.PostScript.Operators;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// The Type 3 registry — <c>PsTextOps</c>'s per-thread cache of recognised Type 3 fonts, keyed by
/// the font dictionary — is per conversion: <c>ResetPerConversionState</c> drops it. It was the one
/// slot the reset missed, and because it is keyed by dictionary instance it could never answer a
/// later conversion wrongly, so nothing observable said it held every Type 3 font of every
/// conversion the thread had run, built CharProcs included, for ever. A weak reference to the font
/// dictionary is the observation: after the reset, nothing holds it.
/// </summary>
public sealed class Type3RegistryLifetimeTests
{
    /// <summary>
    /// A Type 3 font set and shown <b>without</b> <c>definefont</c>, so that the font directory and
    /// the defined-font registry never see it: the Type 3 registry is the only per-thread holder of
    /// the dictionary, and the test is about that slot alone.
    /// </summary>
    private const string Program =
        "8 dict dup begin /FontType 3 def /FontMatrix [.001 0 0 .001 0 0] def " +
        "/FontBBox [0 0 1000 1000] def /Encoding 256 array def Encoding 65 /square put " +
        "/CharProcs 1 dict def " +
        "CharProcs /square { 750 0 0 0 750 750 setcachedevice 0 0 750 750 rectfill } put " +
        "/BuildGlyph { exch begin CharProcs exch get exec end } def end " +
        "12 scalefont setfont 10 10 moveto (A) show";

    [Fact]
    public void AfterTheReset_NothingHoldsTheFont()
    {
        WeakReference font = ShowAType3Glyph();

        PsTextOps.ResetPerConversionState();
        PsType3GlyphBuilder.ResetPerConversionState();
        Collect();

        Assert.False(font.IsAlive, "the reset should have dropped the Type 3 registry, the last holder of the dictionary");
    }

    /// <summary>
    /// The control: without the reset the dictionary is held, and by per-thread state alone, since
    /// nothing of the interpreter outlives the method that ran it. Without this, the test above
    /// would also pass for a font the registry never saw.
    /// </summary>
    [Fact]
    public void WithoutTheReset_TheRegistryHoldsTheFont()
    {
        WeakReference font = ShowAType3Glyph();

        Collect();

        try
        {
            Assert.True(font.IsAlive, "the Type 3 registry should still hold the dictionary it recognised");
        }
        finally
        {
            PsTextOps.ResetPerConversionState();
            PsType3GlyphBuilder.ResetPerConversionState();
        }
    }

    /// <summary>
    /// Runs the program on a bare interpreter and returns a weak reference to the font it showed.
    /// Not inlined, so that the interpreter and everything it owns are unreachable once it returns.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ShowAType3Glyph()
    {
        PostScriptDiagnostics.Clear();
        PsTextOps.ResetPerConversionState();
        PsType3GlyphBuilder.ResetPerConversionState();

        var interpreter = new PsInterpreter(null)
        {
            Graphics = new PsGraphicsEngine(new ContentStreamEmitter(), PsMatrix.Identity)
        };
        interpreter.Run(new PsParser(new PsScanner(Encoding.ASCII.GetBytes(Program))).ReadAll());

        Assert.True(interpreter.CurrentFontIsType3,
            "fixture: the shown font must be recognised as Type 3, or the registry never saw it");
        return new WeakReference(interpreter.CurrentFontDict);
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
