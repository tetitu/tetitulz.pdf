// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Plugins.HtmlScript.Tests;

/// <summary>
/// <c>el.dataset</c>, a string indexer with a getter and a setter. A name with no <c>data-*</c>
/// attribute was still an own property — <c>'x' in el.dataset</c> true for every x — because the
/// setter existed (Wave 22, HtmlScript-2:0); a symbol-keyed write reached the indexer as
/// "Symbol(k)" and its read never found it (2:1).
/// </summary>
[Collection(PluginHostCollection.Name)]
public sealed class DatasetTests
{
    private static string Run(string script) =>
        PageScriptTests.Convert("<html><body><p id='a' data-tip='T'>BEFORE</p><script>var p = document.getElementById('a');" +
                                script + "</script></body></html>").Text;

    /// <summary>A name with no attribute is not in the map; the control, one with an attribute, is.</summary>
    [Fact]
    public void ANameWithNoAttribute_IsNotInTheMap()
    {
        string text = Run("p.textContent = ('src' in p.dataset) + '/' + p.dataset.hasOwnProperty('src') + '/' + ('tip' in p.dataset);");
        Assert.Contains("false/false/true", text, StringComparison.Ordinal);
    }

    /// <summary>A write to a name not yet in the map still reaches the attribute, and reads back.</summary>
    [Fact]
    public void AWriteToANewName_ReachesTheAttribute()
    {
        string text = Run("p.dataset.fresh = 'F'; p.textContent = p.getAttribute('data-fresh') + '/' + p.dataset.fresh + '/' + ('fresh' in p.dataset);");
        Assert.Contains("F/F/true", text, StringComparison.Ordinal);
    }

    /// <summary><c>Object.defineProperty</c> with a value, on a name not in the map, sets the attribute.</summary>
    [Fact]
    public void DefinePropertyOnANewName_SetsTheAttribute()
    {
        string text = Run("Object.defineProperty(p.dataset, 'x', { value: '1' }); p.textContent = 'X' + p.getAttribute('data-x');");
        Assert.Contains("X1", text, StringComparison.Ordinal);
    }

    /// <summary>A symbol-keyed write is an ordinary expando: it reads back, and adds no attribute.</summary>
    [Fact]
    public void ASymbolKeyedWrite_ReadsBack_AndAddsNoAttribute()
    {
        string text = Run("var k = Symbol('k'); p.dataset[k] = 5; p.textContent = 'V' + String(p.dataset[k]) + '/' + p.attributes.length;");
        // id and data-tip: two attributes, as before the write.
        Assert.Contains("V5/2", text, StringComparison.Ordinal);
    }
}
