// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Util;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Util;

/// <summary>
/// One subscriber throwing must cost <b>only its own</b> notification.
///
/// <para>This is the rule five diagnostic sinks in this repo each implemented separately, and four
/// of them got wrong: <c>handler(message)</c> invokes the whole multicast list in a single call, so
/// the first subscriber to throw aborts the walk and every later subscriber is skipped. A
/// <c>try</c>/<c>catch</c> around that call — which all four had — contains the exception without
/// fixing the delivery, which is why it survived review.</para>
///
/// <para>It is not a theoretical cost: it produced an intermittent test failure in which a collector
/// that was correct in every respect never received a diagnostic it had asked for, passed when run
/// alone, and passed on two further full runs.</para>
/// </summary>
public sealed class DiagnosticFanOutTests
{
    /// <summary>
    /// <b>The test the four broken copies would have failed.</b> The throwing subscriber is first,
    /// so a single-call invoke never reaches the second.
    /// </summary>
    [Fact]
    public void AThrowingSubscriber_DoesNotBlindTheOnesAfterIt()
    {
        var received = new List<string>();

        Action<string> handler = _ => throw new InvalidOperationException("bad host handler");
        handler += received.Add;

        DiagnosticFanOut.Invoke(handler, "hello");

        Assert.Equal(new[] { "hello" }, received);
    }

    /// <summary>Every subscriber gets it, in subscription order, when none throws.</summary>
    [Fact]
    public void EverySubscriberReceivesTheMessageInOrder()
    {
        var order = new List<string>();

        Action<string> handler = m => order.Add("first:" + m);
        handler += m => order.Add("second:" + m);
        handler += m => order.Add("third:" + m);

        DiagnosticFanOut.Invoke(handler, "x");

        Assert.Equal(new[] { "first:x", "second:x", "third:x" }, order);
    }

    /// <summary>
    /// Several throwing subscribers interleaved with good ones: every good one still hears it.
    /// </summary>
    [Fact]
    public void MultipleThrowingSubscribers_CostOnlyTheirOwnNotifications()
    {
        var received = new List<int>();

        Action<string> handler = _ => throw new Exception("1");
        handler += _ => received.Add(2);
        handler += _ => throw new Exception("3");
        handler += _ => received.Add(4);

        DiagnosticFanOut.Invoke(handler, "x");

        Assert.Equal(new[] { 2, 4 }, received);
    }

    /// <summary>
    /// The exception never escapes. Every caller reports from inside a recovery path whose purpose
    /// is to keep a broken document from costing the rest of the work, so letting a host's logging
    /// callback throw out of there would turn every tolerated failure into an escaping one.
    ///
    /// <para>Asserted by the reporter <b>continuing</b> rather than by the absence of a throw: a
    /// body that only calls the method and asserts nothing cannot fail for the reason its name
    /// gives — it would also pass if the call did nothing at all.</para>
    /// </summary>
    [Fact]
    public void AThrowingSubscriber_DoesNotEscapeToTheReporter()
    {
        Action<string> handler = _ => throw new InvalidOperationException("bad host handler");

        bool reporterContinued = false;
        DiagnosticFanOut.Invoke(handler, "x");
        reporterContinued = true;             // only reached if nothing escaped

        Assert.True(reporterContinued);
    }

    /// <summary>
    /// A null handler is a no-op, so callers need not null-check first — and it must not disturb a
    /// subsequent delivery, which is what this asserts rather than merely "it did not throw".
    /// </summary>
    [Fact]
    public void ANullHandler_IsANoOp()
    {
        var received = new List<string>();

        DiagnosticFanOut.Invoke(null, "dropped");
        DiagnosticFanOut.Invoke(received.Add, "delivered");

        Assert.Equal(new[] { "delivered" }, received);
    }

    // ------------------------------------------------------------------
    //  The two overloads added for the sinks whose events are not Action<string>
    // ------------------------------------------------------------------

    /// <summary>
    /// The single-argument generic overload obeys the same rule. It serves the sinks whose event
    /// carries a payload type of its own — <c>BuilderDiagnostics</c> and the HtmlToPdf mapper's
    /// <c>OnDiagnostic</c>.
    /// </summary>
    [Fact]
    public void AThrowingSubscriber_DoesNotBlindTheOnesAfterIt_PayloadOverload()
    {
        var received = new List<int>();

        Action<int> handler = _ => throw new InvalidOperationException("bad host handler");
        handler += received.Add;
        handler += _ => throw new Exception("also bad");
        handler += v => received.Add(v * 10);

        DiagnosticFanOut.Invoke(handler, 7);

        Assert.Equal(new[] { 7, 70 }, received);
    }

    /// <summary>
    /// The three-argument overload obeys it too, and all three arguments reach every surviving
    /// subscriber unchanged — a fan-out that delivered the message but dropped an argument would
    /// satisfy a subscriber count on its own.
    /// </summary>
    [Fact]
    public void AThrowingSubscriber_DoesNotBlindTheOnesAfterIt_ThreeArgumentOverload()
    {
        var received = new List<string>();
        var boom = new InvalidOperationException("the reported exception");

        Action<int, string, Exception> handler =
            (_, _, _) => throw new InvalidOperationException("bad host handler");
        handler += (k, d, e) => received.Add($"first:{k}:{d}:{e?.Message}");
        handler += (_, _, _) => throw new Exception("also bad");
        handler += (k, d, e) => received.Add($"second:{k}:{d}:{e?.Message}");

        DiagnosticFanOut.Invoke(handler, 3, "detail", boom);

        Assert.Equal(
            new[] { "first:3:detail:the reported exception", "second:3:detail:the reported exception" },
            received);
    }

    /// <summary>
    /// Neither generic overload lets a subscriber's exception escape to the reporter. Asserted by
    /// the reporter <b>continuing</b> — a body that only makes the call would also pass if the call
    /// did nothing at all.
    /// </summary>
    [Fact]
    public void AThrowingSubscriber_DoesNotEscapeToTheReporter_GenericOverloads()
    {
        Action<int> one = _ => throw new InvalidOperationException("bad");
        Action<int, string, Exception> three = (_, _, _) => throw new InvalidOperationException("bad");

        int reachedAfter = 0;
        DiagnosticFanOut.Invoke(one, 1);
        reachedAfter++;
        DiagnosticFanOut.Invoke(three, 1, "d", null);
        reachedAfter++;

        Assert.Equal(2, reachedAfter);
    }

    /// <summary>A null handler is a no-op in both generic overloads as well.</summary>
    [Fact]
    public void ANullHandler_IsANoOp_GenericOverloads()
    {
        var received = new List<string>();

        DiagnosticFanOut.Invoke((Action<int>)null, 1);
        DiagnosticFanOut.Invoke((Action<int, string, Exception>)null, 1, "d", null);
        DiagnosticFanOut.Invoke<int, string, Exception>((k, d, _) => received.Add(k + d), 2, "x", null);

        Assert.Equal(new[] { "2x" }, received);
    }
}
