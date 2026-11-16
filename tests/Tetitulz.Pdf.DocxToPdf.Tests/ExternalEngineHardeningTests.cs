// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Tetitulz.Pdf.DocxToPdf.Engines;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests;

/// <summary>
/// What the two external engines are told before they open a document that may be anyone's: Word,
/// that no macro runs; LibreOffice, a profile of the conversion's own that says the same and loads
/// no links. Neither program is run here — these are the settings and the command line handed to it.
/// </summary>
public class ExternalEngineHardeningTests
{
    // ------------------------------------------------------------------ Word

    /// <summary>A stand-in for Word.Application: the two things the converter sets on it.</summary>
    public sealed class WordStandIn
    {
        /// <summary>1 is msoAutomationSecurityLow, where an automated Word starts.</summary>
        public int AutomationSecurity { get; set; } = 1;
        public WordOptions Options { get; } = new();
    }

    public sealed class WordOptions
    {
        public bool UpdateLinksAtOpen { get; set; } = true;
    }

    /// <summary>A Word that takes the assignment and keeps its own value.</summary>
    public sealed class WordThatKeepsItsMacros
    {
        public int AutomationSecurity { get => 1; set { } }
        public WordOptions Options { get; } = new();
    }

    /// <summary>A Word with no link option to set.</summary>
    public sealed class WordWithoutLinkOption
    {
        public int AutomationSecurity { get; set; } = 1;
        public object Options { get; } = new();
    }

    [Fact]
    public void Word_IsSetToRunNoMacro_AndToUpdateNoLink_BeforeADocumentIsOpened()
    {
        var word = new WordStandIn();

        WordComConverter.HardenForUntrustedInput(word);

        Assert.Equal(3, WordComConverter.MsoAutomationSecurityForceDisable);
        Assert.Equal(WordComConverter.MsoAutomationSecurityForceDisable, word.AutomationSecurity);
        Assert.False(word.Options.UpdateLinksAtOpen);
    }

    /// <summary>The macro setting is the boundary: a Word that does not take it stops the conversion before a file is opened.</summary>
    [Fact]
    public void AWordThatKeepsMacrosEnabled_StopsTheConversion()
    {
        var refusal = Assert.Throws<DocxToPdfException>(
            () => WordComConverter.HardenForUntrustedInput(new WordThatKeepsItsMacros()));

        Assert.Contains("disables macros", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>The link setting is a reduction: a Word without it is still used, with its macros off.</summary>
    [Fact]
    public void AWordWithoutTheLinkOption_IsStillHardenedAgainstMacros()
    {
        var word = new WordWithoutLinkOption();

        WordComConverter.HardenForUntrustedInput(word);

        Assert.Equal(WordComConverter.MsoAutomationSecurityForceDisable, word.AutomationSecurity);
    }

    // ------------------------------------------------------------------ LibreOffice

    /// <summary>
    /// The profile is LibreOffice's configuration form, well-formed, and says four things: the
    /// highest macro security level, macros not executed, links from untrusted documents blocked,
    /// a text document's links never updated.
    /// </summary>
    [Fact]
    public void TheLibreOfficeProfile_TurnsMacrosOff_AndLinksOff()
    {
        XNamespace oor = "http://openoffice.org/2001/registry";
        XDocument profile = XDocument.Parse(LibreOfficeConverter.ProfileSettings);

        var settings = profile.Root!.Elements("item").ToDictionary(
            item => (string)item.Attribute(oor + "path")! + "/" + (string)item.Element("prop")!.Attribute(oor + "name")!,
            item => item.Element("prop")!.Element("value")!.Value);

        Assert.Equal(oor + "items", profile.Root.Name);
        Assert.Equal(4, settings.Count);
        Assert.Equal("3", settings["/org.openoffice.Office.Common/Security/Scripting/MacroSecurityLevel"]);
        Assert.Equal("true", settings["/org.openoffice.Office.Common/Security/Scripting/DisableMacrosExecution"]);
        Assert.Equal("true", settings["/org.openoffice.Office.Common/Security/Scripting/BlockUntrustedRefererLinks"]);
        Assert.Equal("0", settings["/org.openoffice.Office.Writer/Content/Update/Link"]);
    }

    /// <summary>The profile lands where a LibreOffice started on that directory reads it, and the command line names that directory first.</summary>
    [Fact]
    public void TheConversion_RunsOnAProfileOfItsOwn_InADirectoryOfItsOwn()
    {
        string dir = LibreOfficeConverter.CreatePrivateDirectory();
        try
        {
            Assert.True(Directory.Exists(dir));
            Assert.Empty(Directory.GetFileSystemEntries(dir));
            string second = LibreOfficeConverter.CreatePrivateDirectory();
            Directory.Delete(second);
            Assert.NotEqual(dir, second);

            string profile = Path.Combine(dir, "profile");
            LibreOfficeConverter.WriteProfile(profile);
            Assert.Equal(LibreOfficeConverter.ProfileSettings,
                File.ReadAllText(Path.Combine(profile, "user", "registrymodifications.xcu")));

            string input = Path.Combine(dir, "in put.docx");
            string[] arguments = LibreOfficeConverter.ConversionArguments(dir, input, profile);

            Assert.Equal("-env:UserInstallation=" + new Uri(profile).AbsoluteUri, arguments[0]);
            Assert.StartsWith("-env:UserInstallation=file:///", arguments[0], StringComparison.Ordinal);
            Assert.Equal(new[] { "--headless", "--norestore", "--nologo", "--nodefault", "--nolockcheck",
                                 "--convert-to", "pdf", "--outdir", dir, input }, arguments.Skip(1));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
