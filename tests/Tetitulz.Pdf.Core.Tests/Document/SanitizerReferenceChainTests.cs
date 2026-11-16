// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// Every object the sanitizer writes has been cleaned, including one behind a chain of references
/// (an object whose whole value is another reference, §7.3.10). The cleaning walk resolves through
/// the parser, which stops after <see cref="PdfLimits.MaxIndirectReferenceChain"/> hops; the walk
/// that decides what is written follows object numbers to the end. An action one hop past the limit
/// was therefore written with its script intact, for any reader that follows the chain further, and
/// an <c>/Info</c> named through a reference to a reference was never cleaned at all.
/// </summary>
public sealed class SanitizerReferenceChainTests
{
    // Asserted without its parentheses: the writer escapes them, so a script that survived would read
    // app.alert\(1\) in the output and an assertion on "app.alert(1)" could never fail.
    private const string Script = "app.alert";
    private const string Action = "<< /S /JavaScript /JS (" + Script + "(1)) >>";

    [Fact]
    public void Sanitize_AnActionOneHopPastTheChainLimit_IsCleaned()
    {
        int objects = PdfLimits.MaxIndirectReferenceChain + 1;   // one hop more than the parser follows
        byte[] input = Build(PageOpenActionBehindAChain(objects), info: null);
        Assert.Contains(Script, Latin1(input), StringComparison.Ordinal);

        byte[] output = Sanitize(input);

        Assert.DoesNotContain(Script, Latin1(output), StringComparison.Ordinal);
        Assert.DoesNotContain("/JavaScript", Latin1(output), StringComparison.Ordinal);
        using var doc = PdfDocument.Open(new MemoryStream(output));
        Assert.Single(doc.Pages);
    }

    [Fact]
    public void Sanitize_AnActionAtTheChainLimit_IsCleaned()
    {
        // The control: a chain the parser follows to its end, cleaned by the first walk.
        byte[] output = Sanitize(Build(PageOpenActionBehindAChain(PdfLimits.MaxIndirectReferenceChain), info: null));

        Assert.DoesNotContain(Script, Latin1(output), StringComparison.Ordinal);
        Assert.DoesNotContain("/JavaScript", Latin1(output), StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_AnInfoNamedThroughAReferenceToAReference_IsCleaned()
    {
        var extra = new List<(int, string)> { (10, "11 0 R"), (11, "<< /Title (t) /Hidden " + Action + " >>") };
        byte[] input = Build(extra, info: "10 0 R");
        Assert.Contains(Script, Latin1(input), StringComparison.Ordinal);

        byte[] output = Sanitize(input);

        Assert.DoesNotContain(Script, Latin1(output), StringComparison.Ordinal);
        Assert.DoesNotContain("/JavaScript", Latin1(output), StringComparison.Ordinal);
        Assert.Contains("(t)", Latin1(output), StringComparison.Ordinal);   // the dictionary is kept, cleaned
    }

    [Fact]
    public void Sanitize_AnOrdinaryFile_CleansEachDictionaryOnce()
    {
        // The extra pass adds nothing when the walks agree: the catalog, the pages and the page.
        Sanitize(Build(new List<(int, string)>(), info: null));

        Assert.Equal(3, PdfSanitizer.LastDictionariesCleaned);
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>
    /// Object 3, the page, opens with <c>/AA /O 4 0 R</c>; objects 4 onward are <paramref name="objects"/>
    /// objects, each naming the next, the last of them the action. Resolving <c>4 0 R</c> fetches each one.
    /// </summary>
    private static List<(int, string)> PageOpenActionBehindAChain(int objects)
    {
        var chain = new List<(int, string)>
        {
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /AA << /O 4 0 R >> >>"),
        };
        for (int i = 0; i < objects - 1; i++)
            chain.Add((4 + i, (5 + i) + " 0 R"));
        chain.Add((3 + objects, Action));
        return chain;
    }

    private static byte[] Sanitize(byte[] input)
    {
        using var output = new MemoryStream();
        PdfSanitizer.Sanitize(new MemoryStream(input), output);
        return output.ToArray();
    }

    /// <summary>Objects 1 (catalog) and 2 (pages, kid 3), plus <paramref name="extra"/>, which supplies object 3.</summary>
    private static byte[] Build(List<(int Number, string Body)> extra, string info)
    {
        var objects = new List<(int Number, string Body)>
        {
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        };
        objects.AddRange(extra);
        if (!objects.Any(o => o.Number == 3))
            objects.Add((3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>"));

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new Dictionary<int, int>();
        foreach (var (number, body) in objects.OrderBy(o => o.Number))
        {
            offsets[number] = sb.Length;
            sb.Append(number).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }
        int max = objects.Max(o => o.Number);
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(max + 1).Append('\n').Append("0000000000 65535 f \n");
        for (int n = 1; n <= max; n++)
            sb.Append(offsets.TryGetValue(n, out int at) ? at.ToString("D10") + " 00000 n \n" : "0000000000 00000 f \n");
        sb.Append("trailer\n<< /Size ").Append(max + 1).Append(" /Root 1 0 R");
        if (info != null) sb.Append(" /Info ").Append(info);
        sb.Append(" >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);
}
