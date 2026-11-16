// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// The PDF/UA checkers' and the accessibility fixer's one dereference, and the record of what it
    /// could not read.
    /// </summary>
    /// <remarks>
    /// <para><b>An unreadable object is not an absent one</b> (CLAUDE.md). A resolver answering both
    /// with null would report a structure tree root that will not parse as a missing one, and nothing
    /// in the result would say a read had failed. A check still sees null here,
    /// since each check's own judgement of an object it cannot read is beyond this class. What changes
    /// is that every unreadable object is counted, and the report says so: one notice per run, under
    /// the checker's own "incomplete" code (<see cref="Notice"/>).</para>
    /// <para>"Unreadable" is the parser's own answer: a read that returned the null object with
    /// <see cref="PdfParser.LastObjectParseError"/> set, read immediately because the slot belongs to
    /// the most recent read. A reference to an object the file does not have, or one that holds
    /// <c>null</c>, sets no error and stays absent.</para>
    /// <para>The record is <c>[ThreadStatic]</c> and is reset by <see cref="Begin"/> at the start of
    /// each run, on every path.</para>
    /// </remarks>
    internal static class UnreadableObjects
    {
        /// <summary>Distinct unreadable objects a run names one by one; past it the count still grows.</summary>
        internal const int MaxRemembered = 4096;

        [ThreadStatic] private static HashSet<int> _numbers;
        [ThreadStatic] private static int _count;
        [ThreadStatic] private static string _first;

        /// <summary>Starts a run's record.</summary>
        internal static void Begin()
        {
            _numbers = null;
            _count = 0;
            _first = null;
        }

        /// <summary>How many distinct objects this run could not read.</summary>
        internal static int Count => _count;

        /// <summary>
        /// <paramref name="obj"/> followed when it is a reference: the object, or null when it is absent
        /// or could not be read, the latter recorded. An exception from the read is handed to
        /// <paramref name="onThrow"/> (the caller's diagnostics) and recorded the same way.
        /// </summary>
        internal static PdfObject Resolve(PdfDocument doc, PdfObject obj, Action<Exception> onThrow)
        {
            if (obj == null) return null;
            if (!(obj is PdfReference reference)) return obj;
            try
            {
                var resolved = doc.ResolveObject(obj);
                if (resolved == null || resolved is PdfNull)
                {
                    string why = PdfParser.LastObjectParseError;
                    if (why != null) Note(reference.ReferenceObjectNumber, why);
                    return null;
                }
                return resolved;
            }
            catch (Exception ex)
            {
                onThrow?.Invoke(ex);
                Note(reference.ReferenceObjectNumber, ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// The run's notice, or null when every object read: how many could not be, the first and why,
        /// and what that does to the report.
        /// </summary>
        internal static string Notice()
        {
            if (_count == 0) return null;
            string how = _count >= MaxRemembered
                ? "At least " + MaxRemembered.ToString(CultureInfo.InvariantCulture)
                : _count.ToString(CultureInfo.InvariantCulture);
            return how + " object(s) could not be read (the first: " + _first + "). A check that needed " +
                   "one judged it absent, so a finding may be missing or name the wrong cause. The " +
                   "conformance report is incomplete.";
        }

        private static void Note(int number, string why)
        {
            var numbers = _numbers ??= new HashSet<int>();
            if (numbers.Count >= MaxRemembered || !numbers.Add(number)) return;
            if (_count++ == 0) _first = why;
        }
    }
}
