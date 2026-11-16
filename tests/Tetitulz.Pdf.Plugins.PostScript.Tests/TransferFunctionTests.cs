// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// <c>settransfer</c> and <c>setcolortransfer</c> (PLRM §7.3) as an <c>/ExtGState</c>'s <c>/TR</c>:
/// each procedure sampled into a FunctionType 0, set with <c>gs</c> as the program sets it, rolled
/// back by <c>grestore</c>. They were arity-table entries, so a page printed as if it had none.
/// </summary>
public sealed class TransferFunctionTests
{
    private const string Marker = " newpath 0 0 moveto 1 0 lineto 1 1 lineto closepath fill";

    private static PostScriptConversionResult Convert(string program)
        => PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(program));

    private static string[] Ops(PostScriptConversionResult r)
        => ContentStreamAssert.Parse(r.Content).Select(o => o.ToString()).ToArray();

    private static PdfDictionary State(PostScriptConversionResult r, string name)
        => (PdfDictionary)((PdfDictionary)r.Resources[PdfName.ExtGState])[new PdfName(name)];

    /// <summary>An inverting transfer is a sampled function, 255 at 0 and 0 at 1, set before the paint.</summary>
    [Fact]
    public void Settransfer_IsASampledTr_SetBeforeThePaint()
    {
        var r = Convert("{1 exch sub} settransfer" + Marker);
        var ops = Ops(r);
        int gs = System.Array.FindIndex(ops, o => o.EndsWith(" gs", System.StringComparison.Ordinal));
        Assert.True(gs >= 0 && gs < System.Array.IndexOf(ops, "f"));
        var function = Assert.IsType<PdfStream>(State(r, ops[gs].Split(' ')[0].TrimStart('/'))[PdfName.TR]);
        Assert.Equal(0, ((PdfNumber)function[PdfName.FunctionType]).IntValue);
        Assert.Equal(255, function.RawData[0]);
        Assert.Equal(0, function.RawData[255]);
        Assert.Equal(128, function.RawData[127]);
    }

    /// <summary><c>setcolortransfer</c> writes four functions, red, green, blue and grey in that order.</summary>
    [Fact]
    public void Setcolortransfer_IsAnArrayOfFour()
    {
        var r = Convert("{1 exch sub} {} {0.5 mul} {} setcolortransfer" + Marker);
        string name = Ops(r).First(o => o.EndsWith(" gs", System.StringComparison.Ordinal)).Split(' ')[0].TrimStart('/');
        var functions = Assert.IsType<PdfArray>(State(r, name)[PdfName.TR]);
        Assert.Equal(4, functions.Count);
        Assert.Equal(255, ((PdfStream)functions[0]).RawData[0]);
        Assert.Equal(255, ((PdfStream)functions[1]).RawData[255]);   // the identity, sampled
        Assert.Equal(128, ((PdfStream)functions[2]).RawData[255]);
    }

    /// <summary>The identity after another function is written as <c>/Identity</c>; the identity first writes nothing.</summary>
    [Fact]
    public void TheIdentity_IsWrittenOnlyToUndoAnother()
    {
        var undone = Convert("{1 exch sub} settransfer" + Marker + " {} settransfer" + Marker);
        var names = Ops(undone).Where(o => o.EndsWith(" gs", System.StringComparison.Ordinal)).Select(o => o.Split(' ')[0].TrimStart('/')).ToArray();
        Assert.Equal(2, names.Length);
        Assert.Equal("Identity", ((PdfName)State(undone, names[1])[PdfName.TR]).Value);
        Assert.DoesNotContain(Ops(Convert("{} settransfer" + Marker)), o => o.EndsWith(" gs", System.StringComparison.Ordinal));
    }

    /// <summary><c>grestore</c> puts the transfer back with the rest of the state: no second <c>gs</c> is needed.</summary>
    [Fact]
    public void Grestore_PutsTheTransferBack()
    {
        var ops = Ops(Convert("gsave {1 exch sub} settransfer" + Marker + " grestore" + Marker));
        Assert.Single(ops, o => o.EndsWith(" gs", System.StringComparison.Ordinal));
        int q = System.Array.LastIndexOf(ops, "Q");
        Assert.True(q > System.Array.FindIndex(ops, o => o.EndsWith(" gs", System.StringComparison.Ordinal)) && q < System.Array.LastIndexOf(ops, "f"));
    }

    /// <summary>A widening that closes the level a transfer was set under writes it again.</summary>
    [Fact]
    public void AWidening_WritesTheTransferAgain()
    {
        var ops = Ops(Convert("0 0 10 10 rectclip {1 exch sub} settransfer initclip" + Marker));
        Assert.Equal(2, ops.Count(o => o.EndsWith(" gs", System.StringComparison.Ordinal)));
    }

    /// <summary><c>currenttransfer</c> answers the procedure set; <c>currentcolortransfer</c> the four.</summary>
    [Fact]
    public void CurrentTransfer_AnswersTheProcedures()
    {
        Assert.Contains("f", Ops(Convert("/p {1 exch sub} def /p load settransfer currenttransfer /p load eq {" + Marker + "} if")));
        Assert.Contains("f", Ops(Convert("/p {0.5 mul} def {} {} /p load {} setcolortransfer currentcolortransfer pop /p load eq {" + Marker + "} if")));
        Assert.Contains("f", Ops(Convert("currenttransfer length 0 eq {" + Marker + "} if")));
    }

    /// <summary>Four identities are the identity: nothing written where nothing was set.</summary>
    [Fact]
    public void FourIdentities_WriteNothing()
        => Assert.DoesNotContain(Ops(Convert("{} {} {} {} setcolortransfer" + Marker)), o => o.EndsWith(" gs", System.StringComparison.Ordinal));

    /// <summary><c>currenttransfer</c> after <c>setcolortransfer</c> answers the grey procedure, the fourth.</summary>
    [Fact]
    public void CurrentTransfer_AfterSetcolortransfer_IsTheGreyProcedure()
        => Assert.Contains("f", Ops(Convert("/g {0.5 mul} def {} {1 exch sub} {} /g load setcolortransfer currenttransfer /g load eq {" + Marker + "} if")));

    /// <summary>The same procedure set again is the same function: one resource.</summary>
    [Fact]
    public void TheSameProcedure_IsOneFunction()
    {
        var r = Convert("/p {1 exch sub} def /p load settransfer" + Marker + " {} settransfer /p load settransfer" + Marker);
        Assert.Equal(2, ((PdfDictionary)r.Resources[PdfName.ExtGState]).Count);   // the function and the identity
    }

    /// <summary>A procedure that is not one is a <c>typecheck</c>; one that answers no number leaves the transfer as it was, said.</summary>
    [Fact]
    public void AnUnusableTransfer_IsRefused()
    {
        Assert.Contains("f", Ops(Convert("{ 5 settransfer } stopped { $error /errorname get /typecheck eq {" + Marker + "} if } if")));
        var r = Convert("{pop (x)} settransfer" + Marker);
        Assert.DoesNotContain(Ops(r), o => o.EndsWith(" gs", System.StringComparison.Ordinal));
        Assert.Contains("gave no number", r.LastError ?? "", System.StringComparison.Ordinal);
    }

    /// <summary>A transfer procedure that loops spends the program's budget: it is run once a sample.</summary>
    [Fact]
    public void AHostileTransferProcedure_SpendsTheBudget()
    {
        var options = new PostScriptConversionOptions { MaxTotalOps = 100_000 };
        var r = PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes("{ 1000 { 1 pop } repeat } settransfer" + Marker), options);
        Assert.True(r.BudgetExhausted);
    }
}

/// <summary>The transfer function cap, in the collection every test writing a limit joins.</summary>
[Collection(PsLimitsCollection.Name)]
public sealed class TransferFunctionCapTests
{
    /// <summary>Past <see cref="PsLimits.MaxTransferFunctions"/> a new procedure is ignored, said once; one already sampled is set still.</summary>
    [Fact]
    public void PastTheCap_ANewTransferIsIgnored()
    {
        try
        {
            PsLimits.MaxTransferFunctions = 1;
            var r = PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(
                "/a {1 exch sub} def /b {0.5 mul} def /a load settransfer 0 0 1 1 rectfill /b load settransfer 0 0 1 1 rectfill /a load settransfer 0 0 1 1 rectfill"));
            var gs = ContentStreamAssert.Parse(r.Content).Count(o => o.Operator == "gs");
            Assert.Equal(1, gs);
            Assert.Contains("cap was reached", r.LastError ?? "", System.StringComparison.Ordinal);
        }
        finally
        {
            PsLimits.ResetToDefaults();
        }
    }
}
