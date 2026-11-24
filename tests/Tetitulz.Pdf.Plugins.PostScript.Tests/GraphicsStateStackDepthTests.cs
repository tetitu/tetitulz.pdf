// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Plugins.PostScript.Emit;
using Tetitulz.Pdf.Plugins.PostScript.Graphics;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// The graphics-state stack is a composite an untrusted program grows, so it needs the same two
/// guards every other one in this interpreter has: a hard bound on what is retained, and a charge
/// so the traffic is visible to <c>MaxCompositeAllocationBytes</c>.
///
/// <para><c>ContentStreamEmitter.Save</c> capped the emitted <c>q</c> at
/// <see cref="PdfLimits.MaxGraphicsStateStackDepth"/> and returned quietly, which bounded the
/// <i>output</i> only: <c>PsGraphicsEngine.GSave</c> went on cloning a state per iteration until
/// <c>MaxTotalOps</c> ran out. Each assertion below is on the guard's own evidence — a typed
/// <c>limitcheck</c>, a bounded <c>q</c> count, a bounded diagnostic count — never on a wall
/// clock.</para>
/// </summary>
public sealed class GraphicsStateStackDepthTests
{
    /// <summary>The base state occupies the first level, so this many <c>gsave</c> fit under it.</summary>
    private static int MaxGSaveLevels => PdfLimits.MaxGraphicsStateStackDepth - 1;

    // ==================================================================
    //  The engine itself
    // ==================================================================

    /// <summary>
    /// At the cap <c>gsave</c> raises <c>limitcheck</c> — the same shape <c>PsPath.Append</c> uses,
    /// so a program that brackets its work in <c>stopped</c> can catch it — and the refused push
    /// emits no <c>q</c> either, which is what keeps the stream balanced.
    /// </summary>
    [Fact]
    public void GSave_AtTheDepthCap_RaisesLimitcheckAndEmitsNothing()
    {
        var emitter = new ContentStreamEmitter();
        var engine  = new PsGraphicsEngine(emitter, PsMatrix.Identity);

        for (int i = 0; i < MaxGSaveLevels; i++) engine.GSave();
        Assert.Equal(MaxGSaveLevels, emitter.SaveDepth);

        var ex = Assert.Throws<PsErrorException>(() => engine.GSave());
        Assert.Equal(PsErrorKind.LimitCheck, ex.Kind);
        Assert.Equal("limitcheck", ex.ErrorName);
        Assert.Contains("graphics-state stack", ex.Message);

        // The refusal is complete: nothing was pushed and nothing was written, so the emitted
        // depth is exactly where it was before the refused operator.
        Assert.Equal(MaxGSaveLevels, emitter.SaveDepth);
    }

    /// <summary>
    /// A refused <c>gsave</c> must not consume a level: after one <c>grestore</c> there is room for
    /// exactly one more, and the stream that comes out is still balanced.
    /// </summary>
    [Fact]
    public void GSave_RefusedAtTheCap_LeavesRoomAfterAGRestore()
    {
        var emitter = new ContentStreamEmitter();
        var engine  = new PsGraphicsEngine(emitter, PsMatrix.Identity);

        for (int i = 0; i < MaxGSaveLevels; i++) engine.GSave();
        Assert.Throws<PsErrorException>(() => engine.GSave());

        engine.GRestore();
        Assert.Equal(MaxGSaveLevels - 1, emitter.SaveDepth);

        engine.GSave();
        Assert.Equal(MaxGSaveLevels, emitter.SaveDepth);

        ContentStreamAssert.AssertBalancedSaveRestore(emitter.ToBytes());
    }

    /// <summary>
    /// <c>save</c> pushes a graphics level too, so it shares the cap — and it must leave no marker
    /// behind for a save that did not happen, or a later <c>restore</c> would unwind to a depth
    /// that never existed.
    /// </summary>
    [Fact]
    public void Save_AtTheDepthCap_RaisesLimitcheckAndRecordsNoMarker()
    {
        var emitter = new ContentStreamEmitter();
        var engine  = new PsGraphicsEngine(emitter, PsMatrix.Identity);

        int outer = engine.Save();
        for (int i = 0; i < MaxGSaveLevels - 1; i++) engine.GSave();

        var ex = Assert.Throws<PsErrorException>(() => engine.Save());
        Assert.Equal(PsErrorKind.LimitCheck, ex.Kind);

        // The one save that did succeed still unwinds to its own depth.
        Assert.True(engine.Restore(outer), "the outer save's id should still name an open save.");
        Assert.Equal(0, emitter.SaveDepth);
    }

    // ==================================================================
    //  Through the public entry point
    // ==================================================================

    /// <summary>
    /// <c>{ gsave } loop</c> is the bomb. Unbounded, it would retain one cloned state per iteration
    /// until <c>MaxTotalOps</c> ran out — millions of them, none charged to the allocation budget —
    /// while the emitter reported "gsave nesting reached" once per iteration on top, undeduplicated.
    /// </summary>
    [Fact]
    public void GSaveBomb_IsRefusedAtTheDepthCapAndReportedOnce()
    {
        var options = new PostScriptConversionOptions { MaxTotalOps = 2_000_000 };

        using var log = DiagnosticCollector.Start();
        var result = PostScriptConversion.ConvertFragment(
            Encoding.ASCII.GetBytes("{ gsave } loop"), options);

        Assert.True(log.Any("limitcheck"), "the cap should have raised a PostScript limitcheck.");
        Assert.True(log.Any("graphics-state stack"),
            "the diagnostic should name what overflowed. Reported: " + string.Join(" | ", log.Messages));

        // Exactly one `q` per level the cap allows, and not one more: an engine that kept cloning
        // past this would be stopped only by the emitter's own guard, one level deeper.
        Assert.Equal(MaxGSaveLevels, ContentStreamAssert.Count(result.Content, "q"));

        // The diagnostics must not themselves become the attack. One emitter report per iteration
        // would be hundreds of thousands of composed messages and OnEvent invocations from a
        // 14-byte program.
        Assert.InRange(result.DiagnosticCount, 1, 8);
    }

    /// <summary>
    /// <c>save</c> takes the same route, and it also grew the save-marker list, which nothing else
    /// bounds.
    /// </summary>
    [Fact]
    public void SaveBomb_IsRefusedAtTheDepthCap()
    {
        var options = new PostScriptConversionOptions { MaxTotalOps = 2_000_000 };

        var result = PostScriptConversion.ConvertFragment(
            Encoding.ASCII.GetBytes("{ save pop } loop"), options);

        Assert.Contains("limitcheck", result.LastError);
        Assert.Equal(MaxGSaveLevels, ContentStreamAssert.Count(result.Content, "q"));
        Assert.InRange(result.DiagnosticCount, 1, 8);
    }

    /// <summary>
    /// The other half, and the one a depth cap cannot see: <c>{ gsave grestore } loop</c> never
    /// takes the stack past depth two, yet allocates a state every iteration. The charge is what
    /// makes that visible, so it stops against <c>MaxCompositeAllocationBytes</c> instead of
    /// running to <c>MaxTotalOps</c>.
    /// </summary>
    [Fact]
    public void GSaveGrestoreCycling_IsChargedToTheCompositeAllocationBudget()
    {
        const int Allowance = 1_000;
        var options = new PostScriptConversionOptions
        {
            MaxCompositeAllocationBytes = Allowance * PsAllocationBudget.BytesPerGraphicsState,
            MaxTotalOps = 2_000_000
        };

        var result = PostScriptConversion.ConvertFragment(
            Encoding.ASCII.GetBytes("{ gsave grestore } loop"), options);

        // limitcheck, not "the operation budget was exhausted": the allocation budget is what
        // stopped it, which is the whole claim.
        Assert.Contains("limitcheck", result.LastError);
        Assert.True(result.BudgetExhausted);

        int emitted = ContentStreamAssert.Count(result.Content, "q");
        Assert.InRange(emitted, 1, Allowance);
    }

    /// <summary>
    /// <c>save</c>/<c>restore</c> cycling is the same case and needs its own charge: it clones a
    /// state per iteration while the stack never grows, so neither the depth cap nor the
    /// <c>gsave</c> charge covers it.
    /// </summary>
    [Fact]
    public void SaveRestoreCycling_IsChargedToTheCompositeAllocationBudget()
    {
        const int Allowance = 1_000;
        var options = new PostScriptConversionOptions
        {
            MaxCompositeAllocationBytes = Allowance * PsAllocationBudget.BytesPerGraphicsState,
            MaxTotalOps = 2_000_000
        };

        var result = PostScriptConversion.ConvertFragment(
            Encoding.ASCII.GetBytes("{ save restore } loop"), options);

        Assert.Contains("limitcheck", result.LastError);
        Assert.InRange(ContentStreamAssert.Count(result.Content, "q"), 1, Allowance);
    }

    /// <summary>
    /// The charge must not refuse ordinary art. A hundred nested <c>gsave</c>s and their
    /// <c>grestore</c>s convert cleanly under the default budget, and the drawing inside them
    /// still reaches the stream.
    /// </summary>
    [Fact]
    public void OrdinaryNesting_IsUnaffectedByTheChargeAndTheCap()
    {
        var source = new StringBuilder();
        for (int i = 0; i < 100; i++) source.Append("gsave ");
        source.Append("1 0 0 setrgbcolor newpath 0 0 moveto 10 0 lineto 10 10 lineto closepath fill ");
        for (int i = 0; i < 100; i++) source.Append("grestore ");

        var result = PostScriptConversion.ConvertFragment(
            Encoding.ASCII.GetBytes(source.ToString()));

        Assert.Equal(100, ContentStreamAssert.Count(result.Content, "q"));
        Assert.Equal(100, ContentStreamAssert.Count(result.Content, "Q"));
        ContentStreamAssert.Contains(result.Content, "1 0 0 rg");
        ContentStreamAssert.Contains(result.Content, "10 10 l");
        Assert.True(result.IsClean, "an ordinary nesting depth must convert clean: " + result.LastError);
    }
}
