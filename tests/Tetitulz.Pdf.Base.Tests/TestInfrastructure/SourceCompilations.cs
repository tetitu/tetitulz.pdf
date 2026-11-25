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
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// All of <c>src/</c> as one compilation, for a guard that judges by the compiler's types: under the
/// net10.0 symbols and again under netstandard2.0's (an <c>#if</c> hides code from either), against
/// the runtime's assemblies, this test's own output and each project's NuGet compile assets.
/// </summary>
/// <remarks>
/// A project whose packages are not restored here has no compile assets for its package types:
/// running one test project alone restores only its own graph. <see cref="UnrestoredProjects"/> names
/// such projects, so a guard can leave what it cannot type there unjudged rather than count it. The
/// solution-wide restore CI makes leaves none.
/// </remarks>
internal sealed class SourceCompilations
{
    private static readonly string[][] SymbolSets =
    {
        new[] { "NET", "NETCOREAPP", "NET10_0", "NET5_0_OR_GREATER", "NET6_0_OR_GREATER", "NET7_0_OR_GREATER",
                "NET8_0_OR_GREATER", "NET9_0_OR_GREATER", "NET10_0_OR_GREATER", "NETCOREAPP3_0_OR_GREATER" },
        new[] { "NETSTANDARD", "NETSTANDARD2_0" },
    };

    private readonly List<string> _files;
    private readonly List<MetadataReference> _metadata;

    /// <summary>The repository root.</summary>
    public string Root { get; }

    /// <summary>
    /// Each project directory under <c>src/</c> whose packages are not restored here, relative to the
    /// root with forward slashes and a trailing slash.
    /// </summary>
    public IReadOnlyList<string> UnrestoredProjects { get; }

    public SourceCompilations()
    {
        Root = TestCorpus.RepoRoot!;
        string src = Path.Combine(Root, "src");
        _files = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
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
        UnrestoredProjects = projectDirs.Where(d => !File.Exists(Path.Combine(d!, "obj", "project.assets.json")))
            .Select(d => Path.GetRelativePath(Root, d!).Replace('\\', '/') + "/").ToList();
        foreach (var dir in projectDirs)
        {
            string assets = Path.Combine(dir!, "obj", "project.assets.json");
            if (!File.Exists(assets)) continue;
            foreach (var dll in CompileAssets(assets))
                references.TryAdd(Path.GetFileName(dll), dll);
        }
        _metadata = references.Values.Where(File.Exists).Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
    }

    /// <summary>The compilation under each set of symbols, built one at a time, so one is held at once.</summary>
    public IEnumerable<CSharpCompilation> Each()
    {
        foreach (var symbols in SymbolSets)
        {
            var options = new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: symbols);
            var trees = _files.Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), options, f)).ToList();
            yield return CSharpCompilation.Create("guard", trees, _metadata,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        }
    }

    /// <summary>A tree's path relative to the root, with forward slashes.</summary>
    public string Relative(SyntaxTree tree) => Path.GetRelativePath(Root, tree.FilePath).Replace('\\', '/');

    /// <summary>Whether a path relative to the root lies in a project whose packages are not restored here.</summary>
    public bool IsUnrestored(string relative) => UnrestoredProjects.Any(u => relative.StartsWith(u, StringComparison.Ordinal));

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
