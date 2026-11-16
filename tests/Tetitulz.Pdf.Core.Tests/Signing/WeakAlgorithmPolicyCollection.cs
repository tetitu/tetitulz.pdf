// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Signing;

/// <summary>
/// Serialises every test that reads or writes
/// <see cref="Tetitulz.Pdf.Signing.Cms.CmsSignatureVerifier.RejectWeakAlgorithms"/>.
///
/// <para>
/// <b>Why a collection and not a thread filter.</b> CLAUDE.md's rule for shared state is to scope
/// the state per-thread rather than to serialise the tests, because serialising hides the coupling.
/// That rule assumes the state <i>can</i> be scoped. This one cannot: it is a deliberately
/// process-wide legacy switch whose whole documented purpose is to govern calls that pass no
/// per-call policy, and <c>[ThreadStatic]</c> would change its published contract rather than the
/// test's. The per-call escape already exists — <c>SignatureVerificationOptions</c> — and the tests
/// here exist precisely to pin the process-wide path.
/// </para>
/// <para>
/// It became load-bearing when the static stopped being inert. Until the reachability fix,
/// <c>PdfSignatureVerifier.VerifyAll</c> materialised <c>SignatureVerificationOptions.Default</c>
/// before the CMS call, so a neighbour that set the static to <c>true</c> inside a try/finally
/// could not disturb a SHA-1 document verified through the public API. Now it can: xunit runs each
/// class as its own collection in parallel by default, and the write window covers a whole
/// key-generation-plus-sign, which is tens of milliseconds. Sharing one collection is what keeps
/// <c>WeakAlgorithmReachesPublicResultTests</c> from failing for a neighbour's policy.
/// </para>
/// </summary>
[CollectionDefinition(WeakAlgorithmPolicyCollection.Name)]
public sealed class WeakAlgorithmPolicyCollection
{
    public const string Name = "CmsSignatureVerifier.RejectWeakAlgorithms";
}
