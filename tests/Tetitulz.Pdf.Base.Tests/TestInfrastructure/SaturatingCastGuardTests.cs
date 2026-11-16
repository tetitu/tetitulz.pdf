// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Tetitulz.Pdf.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// Every explicit conversion from <c>float</c> or <c>double</c> to an integer type in <c>src/</c>
/// goes through <c>Core.Util.SaturatingCast</c>. A cast out of range is the type's minimum on
/// .NET Framework, which the netstandard2.0 leg runs on, and saturated on .NET 10 — so a clamp after
/// one, <c>Math.Min(width, (int)Math.Ceiling(x))</c>, held on one runtime and let a negative
/// through on the other — and a cast to <c>byte</c> or <c>ushort</c> wraps on both.
/// </summary>
/// <remarks>
/// <para><b>By the compiler's types, not the text.</b> <c>(int)(a * b)</c> does not say what
/// <c>a * b</c> is. All of <c>src/</c> is compiled as one compilation, under the net10.0 symbols and
/// again under netstandard2.0's (an <c>#if</c> hides code from either), against the runtime's
/// assemblies, this test's own output and each project's NuGet compile assets; every cast whose
/// target is an integer type and whose operand is a <c>float</c> or <c>double</c> is a finding.
/// A constant the compiler folds (<c>(int)1.5</c>) is not: it can sit where only a constant may.</para>
/// <para><b>A cast whose operand's type the compilation cannot name is counted too</b> and held to
/// none, on every platform: an unresolved reference hides a float cast as an unknown one. Nothing
/// under <c>src/</c> needs the Windows desktop framework, so none is referenced.</para>
/// <para><b>A project whose packages are not restored here is not judged here.</b> Running this
/// project's tests alone restores only its own graph, so a plugin's package types (Nethereum's) have
/// no compile assets; that project's untypeable casts are named in the output, not counted. The
/// solution-wide restore CI makes leaves none unrestored.</para>
/// </remarks>
public sealed class SaturatingCastGuardTests
{
    private readonly ITestOutputHelper _output;

    public SaturatingCastGuardTests(ITestOutputHelper output) => _output = output;

    private const string Helper = "src/tetitulz.pdf/Tetitulz.Pdf.Core/Util/SaturatingCast.cs";

    private static readonly HashSet<SpecialType> Integral = new()
    {
        SpecialType.System_SByte, SpecialType.System_Byte, SpecialType.System_Int16, SpecialType.System_UInt16,
        SpecialType.System_Int32, SpecialType.System_UInt32, SpecialType.System_Int64, SpecialType.System_UInt64,
        SpecialType.System_Char,
    };

    private static readonly HashSet<SpecialType> Floating = new() { SpecialType.System_Single, SpecialType.System_Double };

    private static readonly string[][] SymbolSets =
    {
        new[] { "NET", "NETCOREAPP", "NET10_0", "NET5_0_OR_GREATER", "NET6_0_OR_GREATER", "NET7_0_OR_GREATER",
                "NET8_0_OR_GREATER", "NET9_0_OR_GREATER", "NET10_0_OR_GREATER", "NETCOREAPP3_0_OR_GREATER" },
        new[] { "NETSTANDARD", "NETSTANDARD2_0" },
    };

    private sealed record Scan(List<string> Findings, int HelperCasts, int CastsToIntegers, List<string> Unresolved,
                               List<string> NotRestored);

    private static readonly Lazy<Scan> Result = new(Run);

    [RepoSourceFact]
    public void NoFloatToIntegerCast_OutsideSaturatingCast()
    {
        Scan scan = Result.Value;
        // Potency: the helper's own casts are float-to-integer casts, and the scan saw them.
        Assert.True(scan.HelperCasts >= 5, $"the scan found {scan.HelperCasts} casts in {Helper}; it is not seeing types");
        Assert.True(scan.Findings.Count == 0,
            "Route these through Core.Util.SaturatingCast:\n" + string.Join("\n", scan.Findings.Take(40)));
    }

    [RepoSourceFact]
    public void TheScanCanNameTheOperandTypeOfAlmostEveryCast()
    {
        Scan scan = Result.Value;
        foreach (string line in scan.NotRestored) _output.WriteLine("not judged here (packages not restored): " + line);
        Assert.True(scan.CastsToIntegers > 1000, $"only {scan.CastsToIntegers} casts to an integer type were seen");
        Assert.True(scan.Unresolved.Count == 0,
            $"{scan.Unresolved.Count} casts to an integer type have an operand the compilation cannot type " +
            "(a reference is missing, so a float cast could hide among them):\n" + string.Join("\n", scan.Unresolved.Take(40)));
    }

    private static Scan Run()
    {
        string root = TestCorpus.RepoRoot!;
        string src = Path.Combine(root, "src");
        var files = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Replace('\\', '/').Contains("/obj/") && !f.Replace('\\', '/').Contains("/bin/"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var references = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dll in ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator))
            references.TryAdd(Path.GetFileName(dll), dll);
        foreach (var dll in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"))
            if (!Path.GetFileName(dll).StartsWith("Tetitulz.Pdf.", StringComparison.Ordinal))   // compiled from source
                references.TryAdd(Path.GetFileName(dll), dll);
        // A project's directory, and whether its packages are restored here (its project.assets.json).
        var projectDirs = Directory.EnumerateFiles(src, "*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName).ToList();
        var unrestored = projectDirs.Where(d => !File.Exists(Path.Combine(d!, "obj", "project.assets.json")))
            .Select(d => Path.GetRelativePath(root, d!).Replace('\\', '/') + "/").ToList();
        foreach (var dir in projectDirs)
        {
            string assets = Path.Combine(dir!, "obj", "project.assets.json");
            if (!File.Exists(assets)) continue;
            foreach (var dll in CompileAssets(assets))
                references.TryAdd(Path.GetFileName(dll), dll);
        }
        var metadata = references.Values.Where(File.Exists).Select(p => MetadataReference.CreateFromFile(p)).ToList();

        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var unresolved = new SortedSet<string>(StringComparer.Ordinal);
        var notRestored = new SortedSet<string>(StringComparer.Ordinal);
        int helperCasts = 0, castsToIntegers = 0;
        foreach (var symbols in SymbolSets)
        {
            var options = new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: symbols);
            var trees = files.Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), options, f)).ToList();
            var compilation = CSharpCompilation.Create("guard", trees, metadata,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
            foreach (var tree in trees)
            {
                string rel = Path.GetRelativePath(root, tree.FilePath).Replace('\\', '/');
                var model = compilation.GetSemanticModel(tree);
                foreach (var cast in tree.GetRoot().DescendantNodes().OfType<CastExpressionSyntax>())
                {
                    var target = model.GetTypeInfo(cast.Type).Type;
                    if (target == null || !Integral.Contains(target.SpecialType)) continue;
                    castsToIntegers++;
                    var source = model.GetTypeInfo(cast.Expression).Type;
                    string where = $"{rel}:{tree.GetLineSpan(cast.Span).StartLinePosition.Line + 1}  {cast}";
                    if (source == null || source.TypeKind == TypeKind.Error)
                    {
                        (unrestored.Any(u => rel.StartsWith(u, StringComparison.Ordinal)) ? notRestored : unresolved).Add(where);
                        continue;
                    }
                    if (!Floating.Contains(source.SpecialType) || model.GetConstantValue(cast).HasValue) continue;
                    if (rel == Helper) helperCasts++;
                    else findings.Add(where);
                }
            }
        }
        return new Scan(findings.ToList(), helperCasts, castsToIntegers, unresolved.ToList(), notRestored.ToList());
    }

    /// <summary>Every compile-time assembly a restored project's packages supply, from its <c>project.assets.json</c>.</summary>
    private static IEnumerable<string> CompileAssets(string assetsPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(assetsPath));
        var rootEl = doc.RootElement;
        var folders = rootEl.GetProperty("packageFolders").EnumerateObject().Select(p => p.Name).ToList();
        var libraries = rootEl.GetProperty("libraries");
        foreach (var target in rootEl.GetProperty("targets").EnumerateObject())
            foreach (var lib in target.Value.EnumerateObject())
            {
                if (!lib.Value.TryGetProperty("compile", out var compile)) continue;
                if (!libraries.TryGetProperty(lib.Name, out var library) || !library.TryGetProperty("path", out var path)) continue;
                foreach (var file in compile.EnumerateObject())
                {
                    if (!file.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var folder in folders)
                    {
                        string full = Path.Combine(folder, path.GetString()!, file.Name);
                        if (File.Exists(full)) { yield return full; break; }
                    }
                }
            }
    }
}
