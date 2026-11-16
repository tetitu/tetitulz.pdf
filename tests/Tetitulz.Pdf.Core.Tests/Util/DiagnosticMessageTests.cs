// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Util;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Util
{
    /// <summary>
    /// The single diagnostic-message composer, which replaced four <c>private static Compose</c>
    /// methods differing only in one literal.
    /// </summary>
    /// <remarks>
    /// <b>Every branch here is a fallback, and fallbacks are what a happy-path fixture never
    /// reaches.</b> A sink's own tests report a real operation with real detail, so the two
    /// substitutions and the exception-less shape can all be removed without reddening anything the
    /// callers own. That is exactly the state that turns a merge into a deletion, so the survivor
    /// gets its own cases.
    /// </remarks>
    public class DiagnosticMessageTests
    {
        [Fact]
        public void TheFullShapeIsTagDetailDashTypeAndMessage()
        {
            string message = DiagnosticMessage.Compose(
                "Sink", "LoadPage", "the page tree was cyclic", new InvalidOperationException("boom"));

            Assert.Equal("[LoadPage] the page tree was cyclic — InvalidOperationException: boom", message);
        }

        [Fact]
        public void WithNoExceptionTheTrailerIsOmittedEntirely()
        {
            Assert.Equal("[LoadPage] nothing to do",
                DiagnosticMessage.Compose("Sink", "LoadPage", "nothing to do", null));
        }

        /// <summary>
        /// An absent operation falls back to the sink's own name — the one thing that differed
        /// between the four copies, and therefore the one thing the parameterisation could get
        /// wrong.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void AnAbsentOperationFallsBackToTheSinkName(string? operation)
        {
            Assert.Equal("[PostScript] detail",
                DiagnosticMessage.Compose("PostScript", operation, "detail", null));
        }

        /// <summary>
        /// An absent detail becomes <c>(no detail)</c>, so a message is never a bare tag with
        /// trailing whitespace — the difference that keeps <c>PdfXValidator.Report</c> out of this
        /// helper rather than folded into it.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void AnAbsentDetailBecomesTheNoDetailPlaceholder(string? detail)
        {
            Assert.Equal("[Op] (no detail)", DiagnosticMessage.Compose("Sink", "Op", detail, null));
        }

        [Fact]
        public void BothFallbacksCanApplyAtOnce()
        {
            Assert.Equal("[Sink] (no detail) — TimeoutException: late",
                DiagnosticMessage.Compose("Sink", null, null, new TimeoutException("late")));
        }
    }
}
