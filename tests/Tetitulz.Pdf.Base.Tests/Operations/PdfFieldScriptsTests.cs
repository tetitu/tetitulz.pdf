// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Base.Scripting;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Plugins;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A committed field value through the field's Keystroke and Validate scripts and the document's
/// <c>/CO</c> calculations.
/// </summary>
/// <remarks>
/// The engine is a stub that reads a few instructions from the <c>/JS</c> text, so what each
/// event does is written in the fixture rather than in Jint; like the real engine it writes
/// <c>event.value</c> into <c>ModifiedFieldValues</c> under the event's own field. In
/// <c>PluginHostCollection</c> because it registers on the process-wide host.
/// </remarks>
[Collection(TestInfrastructure.PluginHostCollection.Name)]
public class PdfFieldScriptsTests
{
    /// <summary>
    /// <c>upper</c>; <c>value=X</c>; <c>rc=false</c>; <c>msg=X</c>; <c>sum=A,B</c> (event.value,
    /// from the context's values); <c>set=Name:X</c> (getField(Name).value = X); <c>alert=X</c>;
    /// <c>throw</c> (fails closed, as the engine does). Instructions are separated by <c>;</c>.
    /// </summary>
    private sealed class InstructionJsPlugin : IJavaScriptPlugin
    {
        internal readonly List<(string Field, string Script, bool WillCommit)> Ran = new();

        public string Name => "instruction-js-stub";
        public string Description => "runs the fixture's instructions";
        public Version Version => new(1, 0, 0);
        public void Initialize() { }
        public void Shutdown() { }

        /// <summary>The last context's keystroke: its value, change and selection.</summary>
        internal (string Value, string Change, int Start, int End) LastKeystroke;

        public bool TryExecute(string script, PdfJsExecutionContext ctx)
        {
            Ran.Add((ctx.EventFieldName, script, ctx.EventWillCommit));
            LastKeystroke = (ctx.EventValue, ctx.EventChange, ctx.EventSelStart, ctx.EventSelEnd);
            foreach (string part in script.Split(';'))
            {
                string[] kv = part.Split(new[] { '=' }, 2);
                string arg = kv.Length > 1 ? kv[1] : "";
                switch (kv[0])
                {
                    case "upper": ctx.EventValue = ctx.EventValue.ToUpperInvariant(); break;
                    case "value": ctx.EventValue = arg; break;
                    case "change": ctx.EventChange = arg; break;
                    case "rc": ctx.ValidationPassed = arg != "false"; break;
                    case "msg": ctx.ValidationMessage = arg; break;
                    case "alert": ctx.AlertMessages.Add(arg); break;
                    case "set":
                        string[] nv = arg.Split(':');
                        ctx.ModifiedFieldValues[nv[0]] = nv[1];
                        break;
                    case "sum":
                        double total = 0;
                        foreach (string f in arg.Split(','))
                            if (ctx.FieldValues.TryGetValue(f, out string? v)
                                && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                                total += d;
                        ctx.EventValue = total.ToString(CultureInfo.InvariantCulture);
                        break;
                    case "throw":
                        ctx.Error = new InvalidOperationException("script error");
                        ctx.ValidationPassed = false;
                        return true;
                }
            }
            // As the real engine: event.value lands under the event's own field.
            if (!string.IsNullOrEmpty(ctx.EventFieldName))
                ctx.ModifiedFieldValues[ctx.EventFieldName] = ctx.EventValue;
            return true;
        }
    }

    private sealed class RegisteredStub : IDisposable
    {
        internal readonly InstructionJsPlugin Plugin = new();
        internal RegisteredStub() => PdfPluginHost.Instance.Register(Plugin);
        public void Dispose() => PdfPluginHost.Instance.Unregister(Plugin.Name);
    }

    /// <summary>
    /// A form of text fields <c>name → (keystroke, validate, calculate)</c> scripts, a null script
    /// absent; <paramref name="order"/> is <c>/CO</c>.
    /// </summary>
    private static PdfDocument Form(IEnumerable<(string Name, string? K, string? V, string? C)> fields, params string[] order)
        => PdfDocument.Open(new MemoryStream(FormBytes(fields, order)));

    internal static byte[] FormBytes(IEnumerable<(string Name, string? K, string? V, string? C)> fields, params string[] order)
        => FormBytes(fields, formats: null, order);

    /// <summary>As <see cref="FormBytes(IEnumerable{ValueTuple{string, string, string, string}}, string[])"/>, with Format scripts (<c>/F</c>) by field name.</summary>
    internal static byte[] FormBytes(IEnumerable<(string Name, string? K, string? V, string? C)> fields,
        IReadOnlyDictionary<string, string>? formats, params string[] order)
    {
        var list = fields.ToList();
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields ["
                + string.Join(" ", list.Select((_, i) => (4 + i) + " 0 R")) + "] /CO ["
                + string.Join(" ", order.Select(o => (4 + list.FindIndex(f => f.Name == o)) + " 0 R")) + "] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>",
        };
        foreach (var f in list)
        {
            string aa = Js("K", f.K) + Js("V", f.V) + Js("C", f.C)
                + Js("F", formats != null && formats.TryGetValue(f.Name, out string? format) ? format : null);
            objects.Add("<< /T (" + f.Name + ") /FT /Tx" + (aa.Length > 0 ? " /AA << " + aa + ">>" : "") + " >>");
        }
        return ActionTargetAgreementTests.Build(objects.ToArray());

        static string Js(string key, string? script)
            => script == null ? "" : "/" + key + " << /S /JavaScript /JS (" + script + ") >> ";
    }

    private static Dictionary<string, string> Values(params (string, string)[] pairs)
        => pairs.ToDictionary(p => p.Item1, p => p.Item2, StringComparer.Ordinal);

    /// <summary>
    /// A keystroke runs the field's Keystroke script with the change and the selection, not
    /// committing; the script may refuse it or rewrite the change.
    /// </summary>
    [Fact]
    public void AKeystroke_IsOfferedToTheKeystrokeScript_WithItsSelection()
    {
        using var stub = new RegisteredStub();
        var budget = TimeSpan.FromSeconds(5);

        using (var rewriting = Form(new[] { ("A", (string?)"change=X", (string?)null, (string?)null) }))
        {
            KeystrokeResult r = PdfFieldScripts.Keystroke(rewriting, "A", "abcd", 1, 3, "z", Values(), budget);
            Assert.True(r.Accepted);
            Assert.True(r.ScriptRan);
            Assert.Equal("X", r.Change);
            Assert.Equal(("abcd", "z", 1, 3), stub.Plugin.LastKeystroke);
            Assert.Equal(("A", "change=X", false), stub.Plugin.Ran.Last());     // not committing
        }

        using (var refusing = Form(new[] { ("A", (string?)"rc=false", (string?)null, (string?)null) }))
        {
            KeystrokeResult r = PdfFieldScripts.Keystroke(refusing, "A", "", 0, 0, "q", Values(), budget);
            Assert.False(r.Accepted);
            Assert.Equal("q", r.Change);
        }

        // No Keystroke script: the keystroke goes in as typed.
        using (var plain = Form(new[] { ("A", (string?)null, (string?)null, (string?)null) }))
        {
            KeystrokeResult r = PdfFieldScripts.Keystroke(plain, "A", "ab", 2, 2, "c", Values(), budget);
            Assert.True(r.Accepted);
            Assert.False(r.ScriptRan);
            Assert.Equal("c", r.Change);

            Assert.Throws<ArgumentOutOfRangeException>(() => PdfFieldScripts.Keystroke(plain, "A", "ab", 3, 3, "c", Values(), budget));
            Assert.Throws<ArgumentOutOfRangeException>(() => PdfFieldScripts.Keystroke(plain, "A", "ab", 2, 1, "c", Values(), budget));
        }
    }

    /// <summary>With the budget spent a Keystroke script is a gate that did not run; with no engine there is no gate.</summary>
    [Fact]
    public void AKeystroke_IsRefusedWhenItsScriptCannotRun_AndTakenWithNoEngine()
    {
        using (var doc = Form(new[] { ("A", (string?)"change=X", (string?)null, (string?)null) }))
        {
            KeystrokeResult noEngine = PdfFieldScripts.Keystroke(doc, "A", "", 0, 0, "q", Values(), TimeSpan.FromSeconds(5));
            Assert.True(noEngine.Accepted);
            Assert.False(noEngine.ScriptRan);

            using var stub = new RegisteredStub();
            Assert.False(PdfFieldScripts.Keystroke(doc, "A", "", 0, 0, "q", Values(), TimeSpan.Zero).Accepted);
            Assert.True(PdfFieldScripts.Keystroke(doc, "A", "", 0, 0, "q", Values(), TimeSpan.FromSeconds(5)).Accepted);   // the control
        }
    }

    /// <summary>
    /// Format runs last, for the committed field and each field the commit changed; its text is a
    /// display value, never a value — and a Format script that fails leaves none.
    /// </summary>
    [Fact]
    public void FormatScripts_GiveDisplayValues_ForTheFieldsTheCommitChanged()
    {
        using var stub = new RegisteredStub();
        var fields = new[]
        {
            ("A", (string?)null, (string?)null, (string?)null),
            ("B", null, null, null),
            ("Total", null, null, "sum=A,B"),
            ("Bad", null, null, null),
        };
        var formats = new Dictionary<string, string>
        {
            ["A"] = "value=A-shown",
            ["Total"] = "value=$7.00",
            ["B"] = "value=B-shown",          // B is not changed by this commit
        };
        using var doc = PdfDocument.Open(new MemoryStream(PdfFieldScriptsTests.FormBytes(fields, formats, "Total")));

        FieldCommitResult r = PdfFieldScripts.Commit(doc, "A", "3", Values(("A", "1"), ("B", "4"), ("Total", "5")));

        Assert.True(r.Accepted);
        Assert.Equal("3", r.Value);                                       // the value stays the value
        Assert.Equal("7", r.OtherChanges["Total"]);
        Assert.Equal("A-shown", r.DisplayValues["A"]);
        Assert.Equal("$7.00", r.DisplayValues["Total"]);
        Assert.False(r.DisplayValues.ContainsKey("B"));                   // unchanged, not formatted
        Assert.Equal(2, r.DisplayValues.Count);

        // A Format script that fails gives no display value; the value is drawn as it is.
        using var failing = PdfDocument.Open(new MemoryStream(PdfFieldScriptsTests.FormBytes(
            new[] { ("A", (string?)null, (string?)null, (string?)null) },
            new Dictionary<string, string> { ["A"] = "value=x;throw" })));
        FieldCommitResult f = PdfFieldScripts.Commit(failing, "A", "3", Values());
        Assert.True(f.Accepted);
        Assert.Empty(f.DisplayValues);
    }

    [Fact]
    public void AnAcceptedValue_IsTheScriptsValue_AndTheCalculationsFollow()
    {
        using var stub = new RegisteredStub();
        using var doc = Form(new[]
        {
            ("A", "value=3", "alert=checked", (string?)null),
            ("B", null, null, null),
            ("Total", null, null, "sum=A,B"),
        }, "Total");

        FieldCommitResult r = PdfFieldScripts.Commit(doc, "A", "2", Values(("A", "1"), ("B", "4"), ("Total", "5")));

        Assert.True(r.Accepted);
        Assert.True(r.ScriptsRan);
        Assert.Equal("3", r.Value);                                  // the keystroke script's value…
        Assert.Equal("7", r.OtherChanges["Total"]);                  // …is what the calculation saw
        Assert.Single(r.OtherChanges);
        Assert.Equal(new[] { "checked" }, r.Alerts);
        Assert.Null(r.RejectionMessage);

        // Keystroke at commit, then Validate, then the calculation, each on its own field.
        Assert.Equal(new[] { ("A", "value=3", true), ("A", "alert=checked", true), ("Total", "sum=A,B", false) },
            stub.Plugin.Ran);
    }

    [Theory]
    [InlineData("rc=false;msg=Too big", "Too big")]
    [InlineData("throw", null)]
    public void AValidateScriptThatRefusesOrFails_KeepsNothing_AndRunsNoCalculation(string validate, string? message)
    {
        using var stub = new RegisteredStub();
        using var doc = Form(new[]
        {
            ("A", "set=B:9", validate, (string?)null),
            ("B", null, null, null),
            ("Total", null, null, "sum=A,B"),
        }, "Total");

        FieldCommitResult r = PdfFieldScripts.Commit(doc, "A", "50", Values(("A", "1"), ("B", "4")));

        Assert.False(r.Accepted);
        Assert.Empty(r.OtherChanges);                                // the keystroke's write to B is dropped
        if (message != null) Assert.Equal(message, r.RejectionMessage);
        Assert.DoesNotContain(stub.Plugin.Ran, run => run.Field == "Total");

        // The control: the same form with a validate script that accepts.
        using var accepting = Form(new[]
        {
            ("A", "set=B:9", "alert=ok", (string?)null),
            ("B", null, null, null),
            ("Total", null, null, "sum=A,B"),
        }, "Total");
        FieldCommitResult ok = PdfFieldScripts.Commit(accepting, "A", "50", Values(("A", "1"), ("B", "4")));
        Assert.True(ok.Accepted);
        Assert.Equal("9", ok.OtherChanges["B"]);
        Assert.Equal("59", ok.OtherChanges["Total"]);               // the calculation saw B's new value
    }

    [Fact]
    public void ARefusingKeystrokeScript_StopsBeforeValidate()
    {
        using var stub = new RegisteredStub();
        using var doc = Form(new[] { ("A", (string?)"rc=false", (string?)"alert=validated", (string?)null) });

        FieldCommitResult r = PdfFieldScripts.Commit(doc, "A", "x", Values());

        Assert.False(r.Accepted);
        Assert.Single(stub.Plugin.Ran);
        Assert.Empty(r.Alerts);
    }

    [Fact]
    public void CalculationsRunInCoOrder_EachSeeingTheOnesBefore()
    {
        using var stub = new RegisteredStub();
        var fields = new[]
        {
            ("A", (string?)null, (string?)null, (string?)null),
            ("Sub", null, null, "sum=A"),
            ("Total", null, null, "sum=Sub"),
        };

        using (var doc = Form(fields, "Sub", "Total"))
        {
            FieldCommitResult r = PdfFieldScripts.Commit(doc, "A", "8", Values(("A", "1"), ("Sub", "1"), ("Total", "1")));
            Assert.Equal("8", r.OtherChanges["Sub"]);
            Assert.Equal("8", r.OtherChanges["Total"]);
        }

        // The other order: Total runs first and still sees Sub's old value.
        using (var doc = Form(fields, "Total", "Sub"))
        {
            FieldCommitResult r = PdfFieldScripts.Commit(doc, "A", "8", Values(("A", "1"), ("Sub", "1"), ("Total", "1")));
            Assert.Equal("8", r.OtherChanges["Sub"]);
            Assert.False(r.OtherChanges.ContainsKey("Total"));       // unchanged at 1
        }
    }

    [Fact]
    public void AFailedCalculation_ChangesNothing()
    {
        using var stub = new RegisteredStub();
        using var doc = Form(new[]
        {
            ("A", (string?)null, (string?)null, (string?)null),
            ("Bad", null, null, "value=99;throw"),
            ("Good", null, null, "sum=A"),
        }, "Bad", "Good");

        FieldCommitResult r = PdfFieldScripts.Commit(doc, "A", "5", Values(("A", "1")));

        Assert.True(r.Accepted);
        Assert.False(r.OtherChanges.ContainsKey("Bad"));
        Assert.Equal("5", r.OtherChanges["Good"]);                   // potency: the next one still runs
    }

    [Fact]
    public void WithNoEngine_TheValueIsAcceptedAsTyped_AndTheWarningSaysWhy()
    {
        using var doc = Form(new[]
        {
            ("A", "rc=false", "rc=false", (string?)null),
            ("Total", null, null, "sum=A"),
        }, "Total");

        FieldCommitResult r = PdfFieldScripts.Commit(doc, "A", "5", Values());

        Assert.True(r.Accepted);
        Assert.False(r.ScriptsRan);
        Assert.Equal("5", r.Value);
        Assert.Empty(r.OtherChanges);
        Assert.Contains(r.Warnings, w => w.Contains("IJavaScriptPlugin is registered"));
    }

    /// <summary>The <see cref="PdfOperations"/> door over a stream answers as the document overload does.</summary>
    [Fact]
    public void TheStreamDoor_CommitsThroughTheSameScripts()
    {
        using var stub = new RegisteredStub();
        var fields = new[]
        {
            ("A", "upper", "rc=false;msg=No", (string?)null),
            ("Total", null, null, "value=done"),
        };
        FieldCommitResult refused = PdfOperations.CommitFieldValue(new MemoryStream(FormBytes(fields, "Total")), "A", "x", Values());
        Assert.False(refused.Accepted);
        Assert.Equal("No", refused.RejectionMessage);

        var accepting = new[]
        {
            ("A", "upper", (string?)null, (string?)null),
            ("Total", null, null, "value=done"),
        };
        FieldCommitResult ok = PdfOperations.CommitFieldValue(new MemoryStream(FormBytes(accepting, "Total")), "A", "x", Values());
        Assert.True(ok.Accepted);
        Assert.Equal("X", ok.Value);
        Assert.Equal("done", ok.OtherChanges["Total"]);
    }

    /// <summary>A gate an engine could have run, and did not, has approved nothing.</summary>
    [Fact]
    public void WithTheBudgetSpent_AFieldWithAGateIsRefused_AndOneWithoutIsAccepted()
    {
        using var stub = new RegisteredStub();
        using var doc = Form(new[]
        {
            ("Gated", (string?)null, "alert=ok", (string?)null),
            ("Free", null, null, null),
        });

        FieldCommitResult gated = PdfFieldScripts.Commit(doc, "Gated", "1", Values(), TimeSpan.Zero);
        Assert.False(gated.Accepted);
        Assert.Contains("budget", gated.RejectionMessage);
        Assert.Empty(stub.Plugin.Ran);

        FieldCommitResult free = PdfFieldScripts.Commit(doc, "Free", "1", Values(), TimeSpan.Zero);
        Assert.True(free.Accepted);

        FieldCommitResult funded = PdfFieldScripts.Commit(doc, "Gated", "1", Values());
        Assert.True(funded.Accepted);                                // the control: a budget lets it run
    }
}
