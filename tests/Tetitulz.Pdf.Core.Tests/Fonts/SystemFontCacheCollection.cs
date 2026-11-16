// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// Serialises the test classes that share <c>FontFileLocator</c>'s process-wide font cache.
///
/// <para><b>The fifth cause of a flaky whole-solution run</b> (GAPS.md §8a), and the same shape as
/// the four before it: shared process state, and a test that assumes exclusive access to it.
/// <c>FontFileLocatorTests</c> calls <c>FontFileLocator.ResetCache()</c> five times to test cache
/// behaviour. xUnit runs different test classes in parallel, so those resets landed in the middle
/// of <c>SystemFontResolverTests</c> resolving a real system font — the cache it had just
/// populated vanished, and a resolution that should succeed returned a failure. Roughly one run in
/// three, never reproducibly, and always green under <c>dotnet test --blame</c>, which runs
/// sequentially.</para>
///
/// <para><b>Why a collection and not a lock.</b> The locator is already internally thread-safe;
/// every cache access is inside its <c>Gate</c>. The problem is not a torn read, it is a
/// <i>semantic</i> one — one test deliberately destroying state another is relying on — and no
/// amount of locking inside the locator fixes that. Nor is the answer to stop
/// <c>FontFileLocatorTests</c> resetting: clearing the cache is precisely what it is testing.
/// Disabling parallelism between exactly these two classes is the narrowest fix that keeps both
/// tests doing what they were written to do, and it is the remedy this repository already uses for
/// <c>FilterPipelineStatics</c> and <c>PluginHostCollection</c>.</para>
///
/// <para>Membership is deliberately narrow: add a class here only if it reads or resets
/// <c>FontFileLocator</c>'s cache. Widening it to "font tests" would serialise work that has no
/// reason to be serial, and would slowly turn a targeted fix into a blanket one.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SystemFontCacheCollection
{
    public const string Name = "SystemFontCache";
}
