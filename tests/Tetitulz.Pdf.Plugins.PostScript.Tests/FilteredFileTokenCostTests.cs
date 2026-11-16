// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Linq;
using System.Text;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// <c>token</c> on a filtered <c>currentfile</c> scans the decoded block in place. It copied the
/// whole rest of the block into a fresh array for every token, at one tick, so a block read a token
/// at a time cost the square of its length in copying.
/// </summary>
public sealed class FilteredFileTokenCostTests
{
    private const int Tokens = 20_000;

    private static byte[] Program()
    {
        string data = string.Concat(Enumerable.Repeat("1 ", Tokens));
        string hex = string.Concat(Encoding.ASCII.GetBytes(data).Select(b => b.ToString("X2")));
        return Encoding.ASCII.GetBytes(
            "/R { /n 0 def { dup token { pop /n n 1 add def } { exit } ifelse } loop pop n 0 1 1 rectfill } def\n" +
            "currentfile /ASCIIHexDecode filter R\n" + hex + ">\n");
    }

    [Fact]
    public void AdversarialTokenLoopOverAFilteredFile_DoesNotCopyTheBlockPerToken()
    {
        byte[] program = Program();
        PostScriptConversion.ConvertFragment(program);   // warm

        long before = GC.GetAllocatedBytesForCurrentThread();
        var r = PostScriptConversion.ConvertFragment(program);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Every token read: the potency — the loop ran over the whole block.
        Assert.Contains($"\n{Tokens} 0 m\n", Encoding.ASCII.GetString(r.Content), StringComparison.Ordinal);
        // Copying the rest per token was 20 000 × 20 000 bytes on average: 400 MB.
        Assert.InRange(allocated, 0, 40L * 1024 * 1024);
    }

    /// <summary>
    /// The scan is charged by the bytes it read, as the string form is: one 100 000-byte token read
    /// from a filtered file was one tick.
    /// </summary>
    [Theory]
    [InlineData(100_000, true)]
    [InlineData(1, false)]
    public void AdversarialLargeTokenFromAFilteredFile_IsChargedByItsLength(int length, bool exhausted)
    {
        string data = "(" + new string('A', length) + ")";
        string hex = string.Concat(Encoding.ASCII.GetBytes(data).Select(b => b.ToString("X2")));
        var r = PostScriptConversion.ConvertFragment(
            Encoding.ASCII.GetBytes("currentfile /ASCIIHexDecode filter token pop pop\n" + hex + ">\n"),
            new PostScriptConversionOptions { MaxTotalOps = 20_000 });

        Assert.Equal(exhausted, r.BudgetExhausted);
    }
}
