// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Globalization;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// <c>save</c> levels are bounded and charged in the VM itself, whatever interpreter opens them, and
/// <c>restore</c> finds its level by binary search; <c>showpage</c> resets the current graphics state
/// and leaves the <c>gsave</c> stack alone (PLRM: <c>initgraphics</c> touches no stack).
/// </summary>
[Collection(PsLimitsCollection.Name)]
public sealed class SaveLevelBoundsTests
{
    [Fact]
    public void Save_IsRefusedPastTheGraphicsStackDepth_WithNoEngineAttached()
    {
        var vm = new PsVm(new PsAllocationBudget(1L << 40));
        for (int k = 0; k < PdfLimits.MaxGraphicsStateStackDepth; k++) vm.Save(0);

        var ex = Assert.Throws<PsErrorException>(() => vm.Save(0));
        Assert.Equal(PsErrorKind.LimitCheck, ex.Kind);
    }

    [Fact]
    public void Save_IsChargedToTheAllocationBudget()
    {
        var budget = new PsAllocationBudget(2L * PsAllocationBudget.BytesPerSaveLevel);
        var vm = new PsVm(budget);
        vm.Save(0);
        vm.Save(0);
        Assert.Equal(2L * PsAllocationBudget.BytesPerSaveLevel, budget.Charged);

        var ex = Assert.Throws<PsErrorException>(() => vm.Save(0));
        Assert.Equal(PsErrorKind.VmError, ex.Kind);
    }

    [Fact]
    public void Restore_FindsItsLevelInLogarithmicComparisons()
    {
        var vm = new PsVm(new PsAllocationBudget(1L << 30));
        int first = vm.Save(7);
        for (int k = 1; k < 1000; k++) vm.Save(k);

        Assert.False(vm.Restore(5000, out _));
        Assert.InRange(vm.LastRestoreComparisons, 1, 11);

        Assert.True(vm.Restore(first, out int graphics));
        Assert.Equal(7, graphics);
        Assert.InRange(vm.LastRestoreComparisons, 1, 11);
        Assert.Equal(0, PsVmLevelCount(vm));
    }

    private static int PsVmLevelCount(PsVm vm)
    {
        // A fresh save after the restore of the first level is the only level open.
        int id = vm.Save(0);
        Assert.True(vm.Restore(id, out _));
        return vm.LastRestoreComparisons - 1;
    }

    [Theory]
    [InlineData("3000 {save pop} repeat 0", true)]
    [InlineData("1 exch sub", false)]
    public void TheTintTransformsChild_IsHeldToTheSaveCap(string tintTransform, bool capped)
    {
        // The child interpreter a tint transform runs in has no graphics engine, so its saves were
        // capped and charged nowhere; now the VM refuses them past the graphics-stack depth, and the
        // child's interpreter reports the limitcheck. Every such save also reports that it drew
        // nothing, so the depth is kept under the report cap, or the refusal's own report would be
        // dropped with the rest.
        try
        {
            PdfLimits.MaxGraphicsStateStackDepth = 64;
            using var collector = DiagnosticCollector.Start();
            string program = "[/Separation /X /DeviceGray {" + tintTransform + "}] setcolorspace 0.5 setcolor 0 0 10 10 rectfill";
            PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(program));
            Assert.Equal(capped, collector.Any("'save': more than 64 saves are open"));
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    [Theory]
    [InlineData("2 2 scale gsave showpage grestore 0 0 moveto 10 0 lineto stroke showpage", 20.0)]
    [InlineData("2 2 scale showpage 0 0 moveto 10 0 lineto stroke showpage", 10.0)]
    public void ShowPage_LeavesTheGsaveStack_SoGrestoreRestoresTheSavedTransformOnTheNextPage(string body, double deviceLength)
    {
        using var source = new MemoryStream(Encoding.ASCII.GetBytes("%!PS\n" + body + "\n"));
        using var output = new MemoryStream();
        var result = PostScriptConversion.ConvertDocument(source, output, null);
        Assert.Equal(2, result.PageCount);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        Assert.NotNull(doc);
        byte[] content = doc!.Pages[1].GetContentStreamData() ?? Array.Empty<byte>();

        // The line's length as a reader draws it: its `m` and `l` operands through every `cm` in
        // force (the page's content is translated by the pen's half width, which a length ignores).
        double[] ctm = { 1, 0, 0, 1, 0, 0 };
        var saved = new Stack<double[]>();
        double? startX = null, endX = null;
        foreach (var op in ContentStreamAssert.Parse(content))
        {
            double[] n = op.Operands.Select(t => double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0).ToArray();
            switch (op.Operator)
            {
                case "q": saved.Push(ctm); break;
                case "Q": ctm = saved.Pop(); break;
                case "cm": ctm = Multiply(n, ctm); break;
                case "m": startX = n[0] * ctm[0] + n[1] * ctm[2] + ctm[4]; break;
                case "l": endX = n[0] * ctm[0] + n[1] * ctm[2] + ctm[4]; break;
            }
        }
        Assert.True(startX.HasValue && endX.HasValue, Encoding.ASCII.GetString(content));
        Assert.Equal(deviceLength, endX!.Value - startX!.Value, 6);
    }

    private static double[] Multiply(double[] a, double[] b) => new[]
    {
        a[0] * b[0] + a[1] * b[2], a[0] * b[1] + a[1] * b[3],
        a[2] * b[0] + a[3] * b[2], a[2] * b[1] + a[3] * b[3],
        a[4] * b[0] + a[5] * b[2] + b[4], a[4] * b[1] + a[5] * b[3] + b[5],
    };
}
