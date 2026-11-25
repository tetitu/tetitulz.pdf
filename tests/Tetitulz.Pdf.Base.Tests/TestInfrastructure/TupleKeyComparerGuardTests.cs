// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using Xunit.Abstractions;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// Every hash table and hashing LINQ operator in <c>src/</c> keyed by a tuple that holds a string
/// takes an <c>IEqualityComparer</c>: <c>Core.Util.KeyedHash.Comparer</c>, which hashes the string
/// under a key a file cannot see. A tuple's own hash is the runtime's string hash, which .NET
/// Framework does not key: 16 384 names chosen to share it take 5.2 s to insert as a
/// <c>(string, string)</c> key there, against 4 ms for random names.
/// </summary>
/// <remarks>
/// <para><b>By the compiler's types, not the text.</b> <c>Select(r =&gt; (r.Value, r.Box)).Distinct()</c>
/// does not say that it hashes a string. Every object creation of <c>Dictionary</c>, <c>HashSet</c>
/// or <c>ConcurrentDictionary</c>, and every call of a LINQ operator that hashes its keys, whose key
/// type is a tuple (or an anonymous type) holding a string at any depth, must call an overload with
/// an <c>IEqualityComparer</c> parameter. The compilation is <see cref="SourceCompilations"/>'.</para>
/// <para><b>A bare string key is not judged.</b> A <c>Dictionary</c> or <c>HashSet</c> of
/// <c>string</c> switches itself to a randomized comparer on .NET Framework; whether a LINQ
/// operator's strings are a file's is a question for its reader (<c>KeyedHash.Strings</c>).</para>
/// </remarks>
public sealed class TupleKeyComparerGuardTests
{
    private readonly ITestOutputHelper _output;

    public TupleKeyComparerGuardTests(ITestOutputHelper output) => _output = output;

    private static readonly HashSet<string> HashTableNames = new(StringComparer.Ordinal)
    {
        "Dictionary", "HashSet", "ConcurrentDictionary",
    };

    private static readonly HashSet<string> HashingOperators = new(StringComparer.Ordinal)
    {
        "Distinct", "DistinctBy", "GroupBy", "ToLookup", "ToDictionary", "ToHashSet", "Union", "UnionBy",
        "Intersect", "IntersectBy", "Except", "ExceptBy", "Join", "GroupJoin", "CountBy", "AggregateBy",
    };

    private sealed record Scan(List<string> Findings, List<string> Keyed, List<string> NotRestored);

    private static readonly Lazy<Scan> Result = new(Run);

    [RepoSourceFact]
    public void EveryTupleKeyHoldingAString_TakesAComparer()
    {
        Scan scan = Result.Value;
        foreach (string line in scan.NotRestored) _output.WriteLine("not judged here (packages not restored): " + line);
        Assert.True(scan.Findings.Count == 0,
            "Give these a Core.Util.KeyedHash.Comparer:\n" + string.Join("\n", scan.Findings.Take(40)));
        // Potency: the keys that take a comparer are seen, so a scan that sees no tuple key fails here.
        Assert.True(scan.Keyed.Count >= 8,
            $"the scan found {scan.Keyed.Count} tuple keys holding a string that take a comparer:\n" + string.Join("\n", scan.Keyed));
    }

    /// <summary>
    /// The check tells a keyed tuple from an unkeyed one, in each form it reads: a field's
    /// target-typed <c>new()</c>, an explicit creation, a nested tuple, a LINQ operator over a
    /// projected tuple and one keyed by one; a bare string and a tuple of no string are not judged.
    /// </summary>
    [Fact]
    public void TheCheck_TellsAKeyedTupleFromAnUnkeyedOne()
    {
        string[] lines =
        {
            "using System.Collections.Generic;",                                                         // 1
            "using System.Linq;",                                                                        // 2
            "class C",                                                                                   // 3
            "{",                                                                                         // 4
            "    static readonly IEqualityComparer<(string, int)> Keys = EqualityComparer<(string, int)>.Default;", // 5
            "    Dictionary<(string, int), int> unkeyedField = new();",                                  // 6
            "    Dictionary<(string, int), int> keyedField = new(Keys);",                                // 7
            "    void M(List<(string A, int B)> rows, List<string> names, List<(int, int)> pairs)",     // 8
            "    {",                                                                                     // 9
            "        var unkeyed = new HashSet<(int, (string, float))>();",                              // 10
            "        var keyed = new HashSet<(string, int)>(Keys);",                                     // 11
            "        int a = rows.Distinct().Count();",                                                  // 12
            "        int b = rows.Distinct(Keys).Count();",                                              // 13
            "        int c = rows.GroupBy(r => (r.A, r.B)).Count();",                                    // 14
            "        int d = names.Distinct().Count();",                                                 // 15
            "        int e = pairs.Distinct().Count();",                                                 // 16
            "        var f = new Dictionary<string, int>();",                                            // 17
            "        var g = rows.ToDictionary(r => r, r => r.B, Keys);",                                // 18
            "        var h = names.GroupBy(n => new { n, n.Length }).Count();",                          // 19
            "    }",                                                                                     // 20
            "}",                                                                                         // 21
        };
        var tree = CSharpSyntaxTree.ParseText(string.Join("\n", lines), path: "Snippet.cs");
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("snippet", new[] { tree }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var keyed = new SortedSet<string>(StringComparer.Ordinal);
        Check(compilation, _ => "Snippet.cs", findings, keyed);

        Assert.Equal(new[] { 6, 10, 12, 14, 19 }, LineNumbers(findings));
        Assert.Equal(new[] { 7, 11, 13, 18 }, LineNumbers(keyed));
    }

    private static int[] LineNumbers(IEnumerable<string> sites)
        => sites.Select(s => int.Parse(s.Split(':', ' ')[1])).OrderBy(n => n).ToArray();

    private static Scan Run()
    {
        var sources = new SourceCompilations();
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var keyed = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var compilation in sources.Each())
            Check(compilation, sources.Relative, findings, keyed);
        var notRestored = findings.Where(f => sources.IsUnrestored(f)).ToList();
        return new Scan(findings.Where(f => !sources.IsUnrestored(f)).ToList(), keyed.ToList(), notRestored);
    }

    /// <summary>
    /// Adds every hash table creation and hashing LINQ call in <paramref name="compilation"/> whose key
    /// is a tuple holding a string to <paramref name="keyed"/> when the overload takes a comparer, else
    /// to <paramref name="findings"/>, as <c>path:line  text</c>.
    /// </summary>
    private static void Check(Compilation compilation, Func<SyntaxTree, string> relative,
                              ISet<string> findings, ISet<string> keyed)
    {
        foreach (var tree in compilation.SyntaxTrees)
        {
            SemanticModel? model = null;
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                ITypeSymbol? key;
                IMethodSymbol? method;
                if (node is BaseObjectCreationExpressionSyntax creation)
                {
                    if (creation is ObjectCreationExpressionSyntax named && !HashTableNames.Contains(RightmostName(named.Type))) continue;
                    model ??= compilation.GetSemanticModel(tree);
                    if (model.GetTypeInfo(creation).Type is not INamedTypeSymbol type || !IsHashTable(type)) continue;
                    key = type.TypeArguments[0];
                    method = model.GetSymbolInfo(creation).Symbol as IMethodSymbol;
                }
                else if (node is InvocationExpressionSyntax call && HashingOperators.Contains(CalledName(call)))
                {
                    model ??= compilation.GetSemanticModel(tree);
                    var called = model.GetSymbolInfo(call).Symbol as IMethodSymbol;
                    method = called?.GetConstructedReducedFrom() ?? called;
                    if (method?.ContainingType is not { Name: "Enumerable" } owner
                        || owner.ContainingNamespace.ToDisplayString() != "System.Linq") continue;
                    key = KeyType(method);
                }
                else continue;

                if (key == null || !HoldsString(key, 0)) continue;
                int line = tree.GetLineSpan(node.Span).StartLinePosition.Line + 1;
                string text = node.ToString().Replace("\r", " ").Replace("\n", " ");
                string where = $"{relative(tree)}:{line}  {(text.Length > 120 ? text.Substring(0, 120) + "…" : text)}";
                (method != null && TakesComparer(method) ? keyed : findings).Add(where);
            }
        }
    }

    private static string RightmostName(TypeSyntax type) => type switch
    {
        SimpleNameSyntax simple => simple.Identifier.Text,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.Text,
        _ => "",
    };

    private static string CalledName(InvocationExpressionSyntax call) => call.Expression switch
    {
        MemberAccessExpressionSyntax access => access.Name.Identifier.Text,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.Text,
        SimpleNameSyntax simple => simple.Identifier.Text,
        _ => "",
    };

    private static bool IsHashTable(INamedTypeSymbol type)
    {
        string ns = type.ContainingNamespace?.ToDisplayString() ?? "";
        return (ns == "System.Collections.Generic" || ns == "System.Collections.Concurrent")
            && HashTableNames.Contains(type.Name) && type.TypeArguments.Length > 0;
    }

    /// <summary>The type an operator hashes: its <c>TKey</c>, or, with none, its <c>TSource</c>.</summary>
    private static ITypeSymbol? KeyType(IMethodSymbol method)
    {
        foreach (string name in new[] { "TKey", "TSource" })
            for (int i = 0; i < method.TypeParameters.Length; i++)
                if (method.TypeParameters[i].Name == name) return method.TypeArguments[i];
        return null;
    }

    private static bool TakesComparer(IMethodSymbol method)
        => method.Parameters.Any(p => p.Type is INamedTypeSymbol t
            && t.Name == "IEqualityComparer" && t.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic");

    /// <summary>Whether a tuple or anonymous type holds a string at any depth; a bare string does not count.</summary>
    private static bool HoldsString(ITypeSymbol type, int depth)
    {
        if (depth > 8) return false;
        if (type.SpecialType == SpecialType.System_String) return depth > 0;
        if (type is not INamedTypeSymbol named) return false;
        if (named.IsTupleType) return named.TupleElements.Any(e => HoldsString(e.Type, depth + 1));
        if (named.IsAnonymousType)
            return named.GetMembers().OfType<IPropertySymbol>().Any(p => HoldsString(p.Type, depth + 1));
        if (named.Name == "Tuple" && named.ContainingNamespace?.ToDisplayString() == "System")
            return named.TypeArguments.Any(t => HoldsString(t, depth + 1));
        return false;
    }
}
