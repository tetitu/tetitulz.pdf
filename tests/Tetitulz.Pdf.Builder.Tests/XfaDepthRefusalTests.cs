// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Tetitulz.Pdf.Builder.Import;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// A template packet nested past <see cref="XfaTemplate.MaxXmlDepth"/> is refused at load, before the
/// per-level recursions below it run. <c>XDocument.Load</c> builds the tree iteratively, but reading a
/// value's text, cloning a prototype and walking the subform tree each recurse once a level, so a packet
/// a few thousand elements deep — well under the element budget — would overflow the thread stack
/// uncatchably and take the host process down.
/// </summary>
public class XfaDepthRefusalTests
{
    private const string Ns = "http://www.xfa.org/schema/xfa-template/3.3/";

    private static byte[] Packet(string body) =>
        Encoding.UTF8.GetBytes("<template xmlns=\"" + Ns + "\"><subform name=\"f\">" + body + "</subform></template>");

    private static string Nest(string open, string inner, string close, int depth)
    {
        var sb = new StringBuilder(open.Length * depth + inner.Length);
        for (int i = 0; i < depth; i++) sb.Append(open);
        sb.Append(inner);
        for (int i = 0; i < depth; i++) sb.Append(close);
        return sb.ToString();
    }

    /// <summary>A value's text nested thousands of elements deep is refused, where <c>ReadValue</c> would recurse per level.</summary>
    [Fact]
    public void ADeeplyNestedValueText_IsRefused()
    {
        byte[] packet = Packet("<draw><value><text>" + Nest("<b>", "x", "</b>", 5000) + "</text></value></draw>");

        Assert.Null(XfaTemplate.Parse(packet));
    }

    /// <summary>A subform (or prototype) nested thousands deep is refused, where the clone's copy constructor would recurse per level.</summary>
    [Fact]
    public void ADeeplyNestedSubformTree_IsRefused()
    {
        byte[] packet = Packet(Nest("<subform>", "<draw/>", "</subform>", 5000));

        Assert.Null(XfaTemplate.Parse(packet));
    }

    /// <summary>The control: an ordinary template parses to its subform tree — the depth guard refuses only the hostile depth.</summary>
    [Fact]
    public void AnOrdinaryTemplate_StillParses()
    {
        byte[] packet = Packet(
            "<pageSet><pageArea name=\"P\"><contentArea x=\"0.25in\" y=\"0.25in\" w=\"576pt\" h=\"756pt\"/>" +
            "<medium stock=\"letter\" short=\"612pt\" long=\"792pt\"/></pageArea></pageSet>" +
            "<subform w=\"576pt\" h=\"756pt\"><draw><value><text>Hello</text></value></draw></subform>");

        XfaTemplate? t = XfaTemplate.Parse(packet);
        Assert.NotNull(t);
        Assert.Equal("f", t!.Root.Name);   // the root subform was read, not refused
    }
}
