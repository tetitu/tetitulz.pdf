// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Objects;

/// <summary>
/// Pins <see cref="PdfReferenceEquality"/>'s one promise: value-equal but distinct objects stay
/// distinct keys.
/// </summary>
/// <remarks>
/// <para><b>These tests exist because a mutation survived.</b> Five call sites across Core and two
/// plugins were consolidated onto this comparer, and replacing its body with
/// <c>object.Equals</c> + <c>obj.GetHashCode()</c> left all 26 test assemblies green. That was not a
/// coverage gap in the usual sense — it was an <i>accidentally equivalent</i> mutation:
/// <c>PdfDictionary</c>, <c>PdfObject</c>, <c>byte[]</c> and <c>IImageDecodePlugin</c> do not
/// override <c>Equals</c>, so for every key type in use today the default comparer already does
/// reference identity.</para>
/// <para>Which is exactly why the comparer is there — every one of those call sites documents that
/// it guards against a value-equality override being added <i>later</i>. A guard against a future
/// change is unobservable through the present callers, so it has to be pinned directly.</para>
/// <para><see cref="PdfName"/> is the fixture because it is a real Core type that <b>does</b>
/// override <c>Equals</c> and <c>GetHashCode</c> — so it behaves the way a future
/// <c>PdfDictionary</c> would, rather than the way a synthetic test double invented for the purpose
/// would.</para>
/// </remarks>
public class PdfReferenceEqualityTests
{
    private static readonly IEqualityComparer<PdfName> Comparer = PdfReferenceEquality.Instance;

    [Fact]
    public void Value_equal_but_distinct_objects_are_not_equal()
    {
        var a = new PdfName("Widget");
        var b = new PdfName("Widget");

        // The premise: without an override this test would prove nothing, because every object is
        // already reference-equal-only. PdfName IS value-equal here.
        Assert.True(a.Equals(b));
        Assert.NotSame(a, b);

        Assert.False(Comparer.Equals(a, b));
    }

    [Fact]
    public void The_same_object_is_equal_to_itself()
    {
        var a = new PdfName("Widget");
        Assert.True(Comparer.Equals(a, a));
    }

    /// <summary>
    /// The acceptance control. Without it every assertion above would still pass if the collections
    /// had simply been left on the <b>default</b> comparer — the fixture has to be shown to
    /// discriminate between the two.
    /// </summary>
    [Fact]
    public void The_default_comparer_would_merge_them_which_is_what_this_prevents()
    {
        var a = new PdfName("Widget");
        var b = new PdfName("Widget");

        var withDefault = new HashSet<PdfName> { a, b };
        var withIdentity = new HashSet<PdfName>(PdfReferenceEquality.Instance) { a, b };

        Assert.Single(withDefault);       // collapsed — the failure the comparer exists to stop
        Assert.Equal(2, withIdentity.Count);
    }

    [Fact]
    public void A_dictionary_keyed_by_identity_keeps_one_entry_per_object()
    {
        var a = new PdfName("Widget");
        var b = new PdfName("Widget");

        var map = new Dictionary<PdfName, int>(PdfReferenceEquality.Instance)
        {
            [a] = 1,
            [b] = 2,
        };

        Assert.Equal(2, map.Count);
        Assert.Equal(1, map[a]);
        Assert.Equal(2, map[b]);
    }

    /// <summary>
    /// Hashing must not consult the key's own <c>GetHashCode</c>: two value-equal keys landing in
    /// one bucket is how the merge this prevents actually happens.
    /// </summary>
    [Fact]
    public void The_hash_is_the_identity_hash_not_the_objects_own()
    {
        var a = new PdfName("Widget");
        var b = new PdfName("Widget");

        Assert.Equal(a.GetHashCode(), b.GetHashCode());              // the type's own hash agrees…
        Assert.NotEqual(Comparer.GetHashCode(a), Comparer.GetHashCode(b)); // …the comparer's does not

        Assert.Equal(Comparer.GetHashCode(a), Comparer.GetHashCode(a)); // and it is stable
    }

    /// <summary>
    /// Contravariance is why one non-generic instance can serve every call site; if it stopped
    /// compiling, five of them would need a type argument spelled out.
    /// </summary>
    [Fact]
    public void One_instance_serves_any_reference_key_type()
    {
        IEqualityComparer<PdfName> asNames = PdfReferenceEquality.Instance;
        IEqualityComparer<PdfDictionary> asDictionaries = PdfReferenceEquality.Instance;
        IEqualityComparer<object> asObjects = PdfReferenceEquality.Instance;

        var dictionary = new PdfDictionary();

        Assert.True(asNames.Equals(null, null));
        Assert.True(asDictionaries.Equals(dictionary, dictionary));
        Assert.False(asObjects.Equals(new PdfName("A"), new PdfName("A")));
    }

    [Fact]
    public void Nulls_are_handled_rather_than_thrown_on()
    {
        Assert.True(Comparer.Equals(null, null));
        Assert.False(Comparer.Equals(new PdfName("A"), null));
        Assert.False(Comparer.Equals(null, new PdfName("A")));

        // RuntimeHelpers.GetHashCode(null) is defined to be 0; a dictionary probing a null key
        // must not blow up inside the comparer.
        Assert.Equal(0, PdfReferenceEquality.Instance.GetHashCode(null));
    }
}
