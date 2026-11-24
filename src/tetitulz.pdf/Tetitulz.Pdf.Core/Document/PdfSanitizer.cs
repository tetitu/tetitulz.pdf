// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Core.Document
{
    /// <summary>
    /// Sanitizes PDF files by stripping potentially dangerous content such as
    /// JavaScript, launch actions, embedded files, form submission actions,
    /// URI actions, and automatic open actions.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this lives in Core.</b> Two callers decide it. A host that signs references
    /// <c>Core</c> and <c>Signing</c> alone, and sanitizing before signing must not cost it Base's
    /// SkiaSharp native assets — the sanitizer reads, cleans and writes objects and draws nothing.
    /// And the PDF/A and PDF/X converters in <c>Plugins.Conformance</c> sanitize before converting:
    /// a home in <c>Plugins.Maintenance</c> would mean a <c>Plugins.Conformance</c> →
    /// <c>Plugins.Maintenance</c> project reference, the plugin → plugin edge the architecture
    /// forbids (CLAUDE.md, "The reference invariant is acyclicity, not direction"), and would drag
    /// the optimizer, linearizer and repair into the accessibility package graph for one call. The
    /// code lives in the ring every caller already references (as
    /// <c>Core.Writing.IncrementalUpdateWriter</c> does); passing sanitization in as a
    /// caller-supplied delegate would cost "the converter cannot sanitize <i>standalone</i>", a
    /// silent gap.</para>
    /// <para><c>Plugins.Maintenance.SanitizerPlugin</c> is the <c>ISanitizePlugin</c> discovery
    /// wrapper over this type.</para>
    /// <para><b>The output is a reachability rewrite, not an object-for-object copy.</b> Every object
    /// the cleaned catalog and <c>/Info</c> reach is written, renumbered, once; every object they no
    /// longer reach is dropped. This is load-bearing for the sweeps' own guarantee: an action stripped
    /// from a dictionary, an embedded file whose name tree was removed, a script stream whose <c>/JS</c>
    /// reference is gone — each becomes unreachable and so leaves the file, rather than being emitted
    /// as an orphan with its bytes intact. The object-stream containers (<c>/Type /ObjStm</c>) and the
    /// cross-reference streams (<c>/Type /XRef</c>) a compressed input is built from are themselves
    /// unreachable from the catalog, so a file that carried its actions inside an object stream
    /// does not ship that stream's untouched copy.</para>
    /// </remarks>
    public static class PdfSanitizer
    {
        [ThreadStatic] private static string _lastXfaWarning;
        [ThreadStatic] private static string _lastObjectWarning;

        /// <summary>
        /// How many dictionaries the last clean walk processed on this thread. A probe, not a
        /// diagnostic message: the cleaning walk keeps one visited set for the whole document and
        /// admits each dictionary once, so this equals the count of distinct reachable dictionaries —
        /// not a multiple of it. It is the falsifiable evidence that the walk is linear in the graph
        /// rather than quadratic (a per-object visited reset) or cubic (arrays re-walked), which no
        /// assertion about the sanitized <i>output</i> could see. Reset to 0 at the start of each
        /// sanitize. Read on the calling thread.
        /// </summary>
        [ThreadStatic] internal static int LastDictionariesCleaned;

        /// <summary>
        /// Non-null when the last sanitize on this thread dropped an <c>/AcroForm /XFA</c> packet;
        /// null when the source carried none, or when <see cref="SanitizeOptions.RemoveXfa"/> was
        /// off.
        /// </summary>
        /// <remarks>
        /// <para><b>The removal is destructive and otherwise silent.</b> XFA (§12.7.8) holds the whole
        /// form — its layout, its field definitions and, in a static form, its values — as XML beside
        /// the AcroForm. Dropping it is the right thing for a sanitizer to do, because that XML is
        /// where a script lives, but afterwards nothing in the output distinguishes "this was never
        /// an XFA form" from "this was one and its form definition was discarded". A caller that
        /// hands the result back to a filling workflow needs to know which.</para>
        ///
        /// <para><b>Read it on the calling thread</b>, like every other slot here, and read it after
        /// the call — it is cleared at the start of each <see cref="Sanitize(Stream, Stream, SanitizeOptions)"/>.</para>
        /// </remarks>
        public static string LastXfaWarning => _lastXfaWarning;

        /// <summary>
        /// Non-null when the last sanitize on this thread could not read one or more objects the
        /// cross-reference table listed, or dropped unreachable objects it could not account for;
        /// null when every listed object parsed. Read it on the calling thread, after the call.
        /// </summary>
        /// <remarks>
        /// CLAUDE.md: "an unreadable object is not an absent one" — a writer that leaves an object
        /// out says so. The sanitizer skips an object it cannot parse rather than aborting the whole
        /// file, so the count is how many the output could not carry through.
        /// </remarks>
        public static string LastObjectWarning => _lastObjectWarning;

        /// <summary>
        /// Sanitizes a PDF file and writes the clean result to a new file.
        /// </summary>
        public static void Sanitize(string inputPath, string outputPath, SanitizeOptions options = null)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            if (outputPath == null) throw new ArgumentNullException(nameof(outputPath));

            using (var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                Sanitize(input, output, options);
            }
        }

        /// <summary>
        /// Sanitizes a PDF from an input stream and writes the clean result to an output stream.
        /// </summary>
        public static void Sanitize(Stream input, Stream output, SanitizeOptions options = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));

            _lastXfaWarning = null;
            _lastObjectWarning = null;
            LastDictionariesCleaned = 0;

            options = options ?? SanitizeOptions.Default;

            using (var doc = PdfDocument.Open(input))
            {
                // Fail closed on encrypted input. The rewrite below renumbers every object, but the
                // trailer (including /Encrypt) is copied — and per-object keys are derived from the
                // object number, so the output would be silently undecryptable; and for AES-256
                // only *names* would be sanitized while encrypted /JS bodies and /EF streams passed
                // through untouched, handing the caller a file they believe is clean. Matches
                // PdfRedactionEngine's guard.
                if (doc.IsEncrypted)
                    throw new PdfEncryptedDocumentException(
                        "Encrypted PDFs are not supported by PdfSanitizer; decrypt the document first " +
                        "(Tetitulz.Pdf.Encryption.PdfDecryptor), sanitize, then re-encrypt if required.");

                var parser = doc.Parser;
                var version = doc.Version;

                // Step 1: Collect every object the cross-reference table lists. An object that cannot
                // be parsed is not an absent one (CLAUDE.md): count it so the caller hears that the
                // output could not carry it through, rather than dropping it in silence.
                int unreadable = 0;
                var allObjects = new Dictionary<int, PdfObject>();
                foreach (var kvp in parser.EnumerateAllObjects((_, __) => unreadable++))
                {
                    allObjects[kvp.Key] = kvp.Value;
                }
                if (unreadable > 0)
                    _lastObjectWarning =
                        unreadable.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                        " object(s) the cross-reference table listed could not be read and were left " +
                        "out of the sanitized output.";

                // The catalog every step below reads is the one a reader uses, named by the trailer as a
                // reference to a dictionary: an inline /Root, a reference to a reference, or a catalog
                // the parser recovered by scanning would otherwise leave every catalog-level removal
                // undone and the inline catalog copied through whole.
                AnchorTrailerEntry(doc, allObjects, PdfName.Root, doc.Catalog?.Dictionary);
                AnchorTrailerEntry(doc, allObjects, PdfName.Info, null);

                // The rewrite renumbers every object, so a signature's /ByteRange would name offsets
                // that no longer exist: the output looks signed and verifies as corrupt. This is one
                // of the rewriters PdfSignatureDetection was written for.
                if (!options.AllowSignatureInvalidation &&
                    PdfSignatureDetection.HasSignature(doc.Trailer, n => allObjects.TryGetValue(n, out var o) ? o : null))
                {
                    throw new InvalidOperationException(
                        "The document carries a digital signature. Sanitizing rewrites every object, so " +
                        "the signature's /ByteRange would name offsets that no longer exist and the output " +
                        "would verify as corrupt rather than merely modified. Set " +
                        "SanitizeOptions.AllowSignatureInvalidation to proceed anyway.");
                }

                var catalog = FindCatalogDictionary(allObjects, doc.Trailer);

                // Step 2: Clean every dictionary reachable from the catalog and /Info — actions,
                // inline JavaScript, dangerous annotations. One traversal, one visited set, iterative
                // (no recursion, so a chain of a hundred thousand distinct dictionaries neither
                // overflows the stack nor is silently clipped at a depth cap), and arrays are walked
                // too so a value's cost is paid once.
                var cleanRoots = new List<PdfDictionary>();
                if (catalog != null) cleanRoots.Add(catalog);
                var infoDict = ResolveTrailerDictionary(allObjects, doc.Trailer, PdfName.Info);
                if (infoDict != null) cleanRoots.Add(infoDict);
                // A catalog this build cannot find still has to be cleaned: fall back to every object.
                if (catalog == null)
                    foreach (var o in allObjects.Values)
                        if (o is PdfDictionary d) cleanRoots.Add(d);

                var cleaned = new HashSet<object>(PdfReferenceEquality.Instance);
                CleanReachable(cleanRoots, options, parser, cleaned);

                // Step 3: Catalog-level removals, which drop whole subgraphs by name.
                if (catalog != null)
                {
                    if (options.RemoveOpenAction)
                        catalog.Remove(PdfName.OpenAction);
                    if (options.RemoveEmbeddedFiles)
                    {
                        RemoveEmbeddedFilesFromCatalog(catalog, doc, parser);
                        catalog.Remove(PdfName.Collection);   // a portfolio's presentation, with nothing left to present
                    }
                    if (options.RemoveXfa)
                        RemoveXfaFromCatalog(catalog, doc, parser);
                    if (options.RemoveURIActions || options.RemoveNonWebUriActions)
                        RemoveNonWebUriBase(catalog, options, parser);
                }

                // A file can be associated with a page, a form, an annotation or a structure element as
                // well as the catalog (§7.11.4.2), and any object can hold a file specification. So every
                // specification left gives up its payload, and any stream still declaring itself an
                // embedded file is blanked: /Type is optional, so that alone would miss one that does not.
                if (options.RemoveEmbeddedFiles)
                {
                    RemoveEveryFileSpecificationsPayload(allObjects.Values, parser);
                    foreach (var o in allObjects.Values)
                        if (EmbeddedFileStreams.NamesItself(o) && o is PdfStream efs)
                        {
                            foreach (var entry in new List<PdfName>(efs.Keys))
                                efs.Remove(entry);
                            efs.RawData = Array.Empty<byte>();
                        }
                }

                if (options.RemoveRemoteGoToActions) RemoveRoutesOut(allObjects.Values);

                // Step 4: The objects the cleaned graph still reaches. An object stripped of its last
                // referrer — a script stream, an embedded file, the object-stream container and the
                // cross-reference stream a compressed file is built from — is not among them.
                HashSet<int> keep;
                if (catalog != null)
                {
                    var roots = new List<int>();
                    AddRootNumber(doc.Trailer, PdfName.Root, roots);
                    AddRootNumber(doc.Trailer, PdfName.Info, roots);
                    keep = ReachableObjectNumbers(roots, allObjects);

                    // Every object written has been cleaned. The two walks read a reference
                    // differently: the cleaning walk resolves it through the parser, which gives up
                    // on a chain of references longer than PdfLimits.MaxIndirectReferenceChain, and
                    // this one follows object numbers to the end of any chain. So an action behind
                    // such a chain (or under an /Info reached through one) was kept uncleaned, for
                    // any reader that follows the chain further. What the cleaning walk missed is
                    // cleaned now and the kept set recomputed; cleaning only removes edges, so every
                    // object still kept afterwards has been cleaned.
                    var missed = new List<PdfObject>();
                    foreach (int number in keep)
                        if (allObjects.TryGetValue(number, out var kept)
                            && (kept is PdfDictionary || kept is PdfArray) && !cleaned.Contains(kept))
                            missed.Add(kept);
                    if (missed.Count > 0)
                    {
                        CleanReachable(missed, options, parser, cleaned);
                        keep = ReachableObjectNumbers(roots, allObjects);
                    }
                }
                else
                {
                    // No catalog to anchor a reachability walk: keep every object, as the pre-rewrite
                    // sanitizer did, so a structurally broken file still comes back whole.
                    keep = new HashSet<int>(allObjects.Keys);
                }

                // Step 5: Remap the kept objects to sequential numbers and write.
                var remapTable = new Dictionary<int, int>();
                int nextObjNum = 1;
                foreach (int oldNum in allObjects.Keys.Where(keep.Contains).OrderBy(k => k))
                    remapTable[oldNum] = nextObjNum++;

                var finalObjects = new List<(int newNum, PdfObject obj)>();
                foreach (var kvp in allObjects.Where(x => keep.Contains(x.Key)).OrderBy(x => x.Key))
                {
                    int newNum = remapTable[kvp.Key];
                    var rewritten = RemapReferences(kvp.Value, remapTable);
                    finalObjects.Add((newNum, rewritten));
                }

                // Build the output trailer from the structural keys only. The input's /Prev, /XRefStm
                // and cross-reference-stream keys (/W, /Index, /Filter, /Length) named offsets in a
                // file that no longer exists; carrying them forward described this output falsely.
                var trailerDict = new PdfDictionary();
                foreach (var key in new[] { PdfName.Root, PdfName.Info })
                    if (doc.Trailer[key] != null)
                        trailerDict.Set(key, RemapReferences(doc.Trailer[key], remapTable));
                if (doc.Trailer[PdfName.ID] is PdfObject id)
                    trailerDict.Set(PdfName.ID, RemapReferences(id, remapTable));
                trailerDict.Set(PdfName.Size, new PdfNumber(nextObjNum));

                // Write the sanitized PDF.
                var writer = new PdfWriter(output);
                writer.WriteHeader(version);

                var xrefEntries = new List<(int objNum, long offset)>();
                xrefEntries.Add((0, 0));

                foreach (var (num, obj) in finalObjects)
                {
                    long offset = writer.WriteIndirectObject(num, 0, obj);
                    xrefEntries.Add((num, offset));
                }

                long xrefOffset = writer.WriteXrefTable(xrefEntries);
                writer.WriteTrailer(trailerDict, xrefOffset);
            }
        }

        // -------------------------------------------------------------------
        //  Cleaning walk
        // -------------------------------------------------------------------

        /// <summary>
        /// Cleans every dictionary reachable from <paramref name="roots"/>, iteratively. The visited
        /// set admits each dictionary and each array once (by identity, so a resolved indirect object
        /// and the inline values inside it are one each), which both breaks cycles and bounds the
        /// total work — the reason this is not recursive and carries no depth cap: a depth cap on a
        /// walk of a hundred thousand <i>distinct</i> nodes would have to clip silently, which for a
        /// sanitizer is the harmful direction.
        /// </summary>
        /// <remarks>
        /// An array is a node of the walk whether it is written inline or as its own object: a page's
        /// <c>/Annots</c> and the form's <c>/Fields</c> are commonly indirect arrays, and every widget
        /// and every field script lies beyond one.
        /// <para><c>visited</c> is what has been cleaned, shared across calls, so a later walk from
        /// objects the first one missed cleans nothing twice.</para>
        /// </remarks>
        private static void CleanReachable(IEnumerable<PdfObject> roots, SanitizeOptions options, PdfParser parser,
            HashSet<object> visited)
        {
            var work = new Stack<PdfObject>();
            foreach (var r in roots)
                if (r != null) work.Push(r);

            while (work.Count > 0)
            {
                var node = work.Pop();
                if (!visited.Add(node)) continue;

                if (node is PdfArray array)
                {
                    for (int i = 0; i < array.Count; i++)
                        EnqueueChild(array[i], parser, work);
                    continue;
                }

                var dict = (PdfDictionary)node;
                LastDictionariesCleaned++;
                CleanDictionary(dict, options, parser);

                // Enqueue the survivors: every value still present after cleaning, through inline
                // dictionaries and arrays and across indirect references. A dangerous action removed
                // above is not enqueued, because it is no longer a value of this dictionary.
                foreach (var kvp in dict)
                    EnqueueChild(kvp.Value, parser, work);
            }
        }

        /// <summary>Pushes <paramref name="value"/> when it is, or refers to, a dictionary or an array.</summary>
        private static void EnqueueChild(PdfObject value, PdfParser parser, Stack<PdfObject> work)
        {
            if (value is PdfReference reference)
                value = parser?.ResolveObject(reference);
            if (value is PdfDictionary || value is PdfArray)
                work.Push(value);
        }

        // Not in Core's PdfName well-known set. These are /Subtype values matched during the
        // annotation sweep; /FS — the file specification whose embedded payload is neutralized before
        // a FileAttachment annotation is dropped — is PdfName.FS.
        private static readonly PdfName FileAttachment = new PdfName(PdfAnnotationSubtypes.FileAttachment);
        private static readonly PdfName ThreeD = new PdfName(PdfAnnotationSubtypes.ThreeD);

        private static void CleanDictionary(PdfDictionary dict, SanitizeOptions options, PdfParser parser)
        {
            if (dict == null) return;

            // Inline JavaScript (/JS), whether a string or a text stream. Blank the stream's bytes
            // too: the reachability write drops it once the reference is gone, but a stream shared on
            // another path is emptied here as well.
            if (options.RemoveJavaScript && dict.ContainsKey(PdfName.JS))
            {
                BlankStreamPayload(dict[PdfName.JS], parser);
                dict.Remove(PdfName.JS);
            }

            // If this dictionary is itself a dangerous action, neutralize it in place by dropping its
            // /S type (a typeless action is ignored, §12.6.4) along with its payload. This reaches an
            // action written as its own indirect object, or inline under a key the walk does not treat
            // specially (/OpenAction kept by the caller, a link annotation's /PA, a /Next array entry).
            if (dict.ContainsKey(PdfName.S) && IsRemovableAction(dict, options, parser))
            {
                dict.Remove(PdfName.S);
                dict.Remove(PdfName.JS);
                dict.Remove(PdfName.F); // the remote target of a GoToR/GoToE/ImportData/Thread/Launch
            }

            // /A action entry.
            if (dict.ContainsKey(PdfName.A))
            {
                var actionDict = ResolveAsDictionary(dict[PdfName.A], parser);
                if (actionDict != null && IsRemovableAction(actionDict, options, parser))
                    dict.Remove(PdfName.A);
            }

            // Additional actions (/AA).
            if (dict.ContainsKey(PdfName.AA))
            {
                if (options.RemoveAllAdditionalActions)
                {
                    dict.Remove(PdfName.AA);
                }
                else
                {
                var aaDict = ResolveAsDictionary(dict[PdfName.AA], parser);
                if (aaDict != null)
                {
                    foreach (var key in new List<PdfName>(aaDict.Keys))
                    {
                        var actionDict = ResolveAsDictionary(aaDict[key], parser);
                        if (actionDict != null && IsRemovableAction(actionDict, options, parser))
                            aaDict.Remove(key);
                    }
                    if (aaDict.Count == 0)
                        dict.Remove(PdfName.AA);
                }
                }
            }

            // Sever a /Next link that leads to a dangerous action. A surviving /Next is walked as an
            // ordinary child edge, so the whole chain is cleaned however long it is.
            if (dict.ContainsKey(PdfName.Next))
            {
                var nextDict = ResolveAsDictionary(dict[PdfName.Next], parser);
                if (nextDict != null && IsRemovableAction(nextDict, options, parser))
                    dict.Remove(PdfName.Next);
            }

            // Prune dangerous annotations from an /Annots array.
            PruneAnnotations(dict, options, parser);
        }

        private static void PruneAnnotations(PdfDictionary dict, SanitizeOptions options, PdfParser parser)
        {
            if (!dict.ContainsKey(PdfName.Annots)) return;

            var annotsArray = dict[PdfName.Annots] as PdfArray;
            if (annotsArray == null && dict[PdfName.Annots] is PdfReference annotsRef)
                annotsArray = parser?.ResolveObject(annotsRef) as PdfArray;
            if (annotsArray == null) return;

            // Counts DOWN because the body removes: a forward loop skips whatever slides into the
            // index just vacated, so a run of adjacent removable annotations comes out half-swept.
            // This sweep must be correct in ONE pass — the page tree sweeps a page twice (once as its
            // own object, once through /Pages /Kids), which would paper over a forward-loop defect on
            // runs of two or three but not four.
            for (int i = annotsArray.Count - 1; i >= 0; i--)
            {
                var annotDict = ResolveAsDictionary(annotsArray[i], parser);
                if (annotDict == null) continue;

                var subtype = ResolveAsName(annotDict[PdfName.Subtype], parser);
                if (subtype == null) continue;

                if (options.RemoveRichMediaAnnotations
                    && (subtype == PdfName.RichMedia || MediaAnnotations.Contains(subtype.Value)))
                {
                    annotsArray.RemoveAt(i);
                    continue;
                }

                if (options.Remove3DAnnotations && subtype == ThreeD)
                {
                    annotsArray.RemoveAt(i);
                    continue;
                }

                // A /Subtype /FileAttachment annotation carries an embedded payload via /FS → /EF.
                // The catalog-level /Names /EmbeddedFiles sweep does not reach it, so drop the whole
                // annotation here and neutralize the streams it points at.
                if (options.RemoveEmbeddedFiles && subtype == FileAttachment)
                {
                    NeutralizeEmbeddedFiles(annotDict[PdfName.FS], parser);
                    annotsArray.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Removes the catalog's <c>/URI /Base</c> (§12.3.3 Table 29) when it is not a web address: every
        /// relative link kept as harmless resolves against it, so a base that is a UNC path or a
        /// <c>file:</c> URL is every one of those links going there.
        /// </summary>
        private static void RemoveNonWebUriBase(PdfDictionary catalog, SanitizeOptions options, PdfParser parser)
        {
            if (!(ResolveAsDictionary(catalog[PdfName.URI], parser) is PdfDictionary uriDict)) return;
            if (options.RemoveURIActions) { catalog.Remove(PdfName.URI); return; }

            var holder = new PdfDictionary();
            holder.Set(PdfName.URI, uriDict[Base]);
            if (uriDict[Base] != null && !IsWebUri(holder, parser)) catalog.Remove(PdfName.URI);
        }

        private static readonly PdfName Base = new PdfName("Base");

        /// <summary>
        /// Whether <paramref name="actionDict"/> is an action the options ask to remove. One source
        /// for both the in-place neutralization and the <c>/A</c>, <c>/AA</c> and <c>/Next</c> sites.
        /// </summary>
        /// <remarks>
        /// <b><c>/S</c> is resolved, and in this class that is a security property.</b> A legal
        /// <c>/S 12 0 R</c> (§7.3.10), trivially writable by whoever supplied the file, read as "no
        /// type" through the raw accessor — and the null branch keeps the action. So a JavaScript or
        /// Launch action wrote its type indirectly and survived, on a function whose whole purpose is
        /// removing active content. A sanitizer that fails open on attacker-controlled input is worse
        /// than one that is absent.
        /// </remarks>
        private static bool IsRemovableAction(PdfDictionary actionDict, SanitizeOptions options, PdfParser parser)
        {
            var typeName = ResolveAsName(actionDict?[PdfName.S], parser);
            if (typeName == null)
                return false;

            switch (typeName.Value)
            {
                case "JavaScript":
                    return options.RemoveJavaScript;
                case "Launch":
                    return options.RemoveLaunchActions;
                case "SubmitForm":
                    return options.RemoveFormActions;
                case "URI":
                    if (options.RemoveURIActions) return true;
                    return options.RemoveNonWebUriActions && !IsWebUri(actionDict, parser);
                // Actions whose target is another document: a server-side-request and
                // credential-leak route (a UNC path makes the viewer authenticate to the host).
                case "GoToR":
                case "GoToE":
                case "ImportData":
                    return options.RemoveRemoteGoToActions;
                case "Thread":
                    // An external thread names a file in /F; a within-document thread does not.
                    return options.RemoveRemoteGoToActions && actionDict.ContainsKey(PdfName.F);
                // What plays media or drives it: a clip may name a remote file (§13.2), and with the
                // annotations gone these have nothing else to do.
                case "Movie":
                case "Sound":
                case "Rendition":
                case "RichMediaExecute":
                case "GoTo3DView":
                    return options.RemoveRichMediaAnnotations;
                default:
                    return false;
            }
        }

        /// <summary>The annotations that play media besides <c>/RichMedia</c>: each names a clip, embedded or at a remote address.</summary>
        private static readonly HashSet<string> MediaAnnotations =
            new HashSet<string>(StringComparer.Ordinal) { "Movie", "Sound", "Screen" };

        private static readonly HashSet<string> WebUriSchemes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "http", "https", "mailto" };

        /// <summary>
        /// Whether a URI with no scheme still names another host: a UNC path, which makes the viewer
        /// authenticate to it, and a network-path reference, which takes its scheme from the document.
        /// </summary>
        private static bool NamesAHostWithoutAScheme(string uri)
        {
            string t = uri.TrimStart();
            return t.StartsWith("\\", StringComparison.Ordinal) || t.StartsWith("//", StringComparison.Ordinal);
        }

        private static bool IsWebUri(PdfDictionary actionDict, PdfParser parser)
        {
            var uriObj = actionDict[PdfName.URI];
            if (uriObj is PdfReference reference)
                uriObj = parser?.ResolveObject(reference);
            if (!(uriObj is PdfString uriString))
                return true; // no readable URI target: the action does nothing, so it is not the
                             // leak this option targets — only a URI we can read as non-web is dropped

            string uri = uriString.GetText();
            if (string.IsNullOrEmpty(uri)) return true;
            if (NamesAHostWithoutAScheme(uri)) return false;
            int colon = uri.IndexOf(':');
            if (colon <= 0) return true; // a relative URI resolves against the catalog's base, which RemoveNonWebUriBase answers for
            return WebUriSchemes.Contains(uri.Substring(0, colon));
        }

        // -------------------------------------------------------------------
        //  /Next chain severing (standalone utility; exercised directly by the DoS guard tests)
        // -------------------------------------------------------------------

        /// <summary>
        /// Walks a <c>/Next</c> action chain iteratively, severing the link ahead of the first action
        /// the options forbid. The whole chain is followed, however long; a cycle (A→B→A) stops at the
        /// repeat. Iterative, so it consumes no stack per link.
        /// </summary>
        /// <remarks>
        /// The production clean walk severs a dangerous <c>/Next</c> as an ordinary edge in
        /// <see cref="CleanDictionary"/> and then follows a surviving one as a child; this method is
        /// the same severing rule in a standalone loop, kept because the DoS guard tests drive it
        /// directly on a synthetic graph without building a whole file.
        /// </remarks>
        private static void SanitizeNextChain(PdfDictionary actionDict, SanitizeOptions options, PdfParser parser)
        {
            if (actionDict == null) return;

            var visited = new HashSet<PdfDictionary>(PdfReferenceEquality.Instance);
            var current = actionDict;
            while (current != null && visited.Add(current))
            {
                var nextDict = ResolveAsDictionary(current[PdfName.Next], parser);
                if (nextDict == null) return;

                if (IsRemovableAction(nextDict, options, parser))
                {
                    current.Remove(PdfName.Next);
                    return;
                }
                current = nextDict;
            }
        }

        // -------------------------------------------------------------------
        //  Reachability
        // -------------------------------------------------------------------

        private static HashSet<int> ReachableObjectNumbers(List<int> roots, Dictionary<int, PdfObject> allObjects)
        {
            var reachable = new HashSet<int>();
            var work = new Stack<int>(roots);
            while (work.Count > 0)
            {
                int num = work.Pop();
                if (!reachable.Add(num)) continue;
                if (allObjects.TryGetValue(num, out var obj))
                    CollectReferenceNumbers(obj, 0, work);
            }
            return reachable;
        }

        private static void CollectReferenceNumbers(PdfObject obj, int depth, Stack<int> work)
        {
            if (depth > PdfLimits.MaxObjectGraphDepth) return;
            switch (obj)
            {
                case PdfReference reference:
                    work.Push(reference.ReferenceObjectNumber);
                    break;
                case PdfStream stream:
                    foreach (var kvp in stream)
                        CollectReferenceNumbers(kvp.Value, depth + 1, work);
                    break;
                case PdfDictionary dict:
                    foreach (var kvp in dict)
                        CollectReferenceNumbers(kvp.Value, depth + 1, work);
                    break;
                case PdfArray array:
                    for (int i = 0; i < array.Count; i++)
                        CollectReferenceNumbers(array[i], depth + 1, work);
                    break;
            }
        }

        private static void AddRootNumber(PdfDictionary trailer, PdfName key, List<int> roots)
        {
            if (trailer[key] is PdfReference reference)
                roots.Add(reference.ReferenceObjectNumber);
        }

        // -------------------------------------------------------------------
        //  Catalog-level removal
        // -------------------------------------------------------------------

        /// <summary>
        /// Drops the catalog's <c>/AcroForm /XFA</c> entry — <b>payload first, key second</b>.
        /// </summary>
        /// <remarks>
        /// <para>The array form is the common one: §12.7.8 allows a single stream holding a whole XDP
        /// or an array of alternating <c>(name) stream</c> pairs, and common producers write the array.
        /// Resolving it is <see cref="XfaPacket"/>'s job, shared with the reader and the filler.</para>
        /// <para>The streams are blanked rather than dropped from the object table: the reachability
        /// write drops the now-unreferenced streams anyway, and blanking the shared instances keeps
        /// their bytes out even if one is reached another way.</para>
        /// </remarks>
        private static void RemoveXfaFromCatalog(PdfDictionary catalogDict, PdfDocument doc, PdfParser parser)
        {
            var acroFormDict = ResolveAsDictionary(catalogDict[PdfName.AcroForm], parser);
            if (acroFormDict == null) return;

            // Presence is asked before resolution: a packet this build cannot parse is still a packet
            // that was dropped, and a caller must hear about that one too.
            if (!XfaPacket.IsPresent(acroFormDict)) return;

            var packet = XfaPacket.Read(acroFormDict, doc);
            if (packet != null)
                foreach (var stream in packet.Streams)
                    stream.RawData = Array.Empty<byte>();

            acroFormDict.Remove(PdfName.XFA);

            _lastXfaWarning =
                "The /AcroForm carried an /XFA packet, which was removed along with its packet " +
                "streams: XFA holds the form's layout, field definitions and — in a static form — " +
                "its values as XML, and that XML is where a script would live. The AcroForm fields " +
                "themselves are untouched, so the document still renders and still fills; what is " +
                "gone is the XFA view of it, which cannot be recovered from the output.";
        }

        /// <summary>
        /// Drops the catalog's <c>/Names /EmbeddedFiles</c> name tree and its <c>/AF</c> associated
        /// files — <b>payload first, key second</b>.
        /// </summary>
        /// <remarks>
        /// Removing the key alone is not removal while the write is object-for-object: the name-tree
        /// nodes, the <c>/Filespec</c> dictionaries and the <c>/Type /EmbeddedFile</c> streams are
        /// still emitted with every byte intact — a mail gateway that sanitized and then trusted the
        /// result passed the executable through. The <c>/AF</c> array (ISO 32000-2, PDF/A-3) is the
        /// second door to an attachment and the one the catalog-level name tree does not cover.
        /// </remarks>
        private static void RemoveEmbeddedFilesFromCatalog(PdfDictionary catalogDict, PdfDocument doc, PdfParser parser)
        {
            var namesDict = ResolveAsDictionary(catalogDict[PdfName.Names], parser);
            if (namesDict != null)
            {
                var leaves = new List<KeyValuePair<PdfObject, PdfObject>>();
                int budget = PdfLimits.MaxNameTreeEntries;
                PdfNameTree.TryFlatten(namesDict[PdfName.EmbeddedFiles], doc, leaves, ref budget);
                foreach (var leaf in leaves)
                    NeutralizeEmbeddedFiles(leaf.Value, parser);

                namesDict.Remove(PdfName.EmbeddedFiles);
            }

            // Associated files on the catalog (ISO 32000-2 §7.11.4.2). Each /AF entry is a file
            // specification with the same /EF payload a FileAttachment annotation carries.
            var afArray = catalogDict[PdfName.AF] as PdfArray;
            if (afArray == null && catalogDict[PdfName.AF] is PdfReference afRef)
                afArray = parser?.ResolveObject(afRef) as PdfArray;
            if (afArray != null)
            {
                for (int i = 0; i < afArray.Count; i++)
                    NeutralizeEmbeddedFiles(afArray[i], parser);
                catalogDict.Remove(PdfName.AF);
            }
        }

        /// <summary>
        /// Points the trailer's <paramref name="key"/> straight at the dictionary a reader takes for it,
        /// as an object of <paramref name="allObjects"/>: an inline dictionary becomes an object of its
        /// own, numbered past every other; and an entry that names no dictionary — a reference to a
        /// reference, one to nothing — is pointed at <paramref name="recovered"/>, the catalog the
        /// parser settled on (following the chain, or scanning), when that is one of the objects.
        /// Left as it is otherwise.
        /// </summary>
        private static void AnchorTrailerEntry(PdfDocument doc, Dictionary<int, PdfObject> allObjects, PdfName key,
            PdfDictionary recovered)
        {
            PdfDictionary trailer = doc.Trailer;
            if (trailer == null) return;
            PdfObject entry = trailer[key];

            if (entry is PdfDictionary inline)
            {
                int number = allObjects.Count == 0 ? 1 : allObjects.Keys.Max() + 1;
                allObjects[number] = inline;
                trailer.Set(key, new PdfReference(number, 0));
                return;
            }

            if (entry is PdfReference reference
                && allObjects.TryGetValue(reference.ReferenceObjectNumber, out PdfObject target) && target is PdfDictionary)
                return;

            if (recovered == null) return;
            foreach (var kvp in allObjects)
                if (ReferenceEquals(kvp.Value, recovered))
                {
                    trailer.Set(key, new PdfReference(kvp.Key, 0));
                    return;
                }
        }

        private static PdfDictionary FindCatalogDictionary(Dictionary<int, PdfObject> allObjects, PdfDictionary trailer)
            => ResolveTrailerDictionary(allObjects, trailer, PdfName.Root);

        private static PdfDictionary ResolveTrailerDictionary(
            Dictionary<int, PdfObject> allObjects, PdfDictionary trailer, PdfName key)
        {
            if (!(trailer[key] is PdfReference reference))
                return null;
            if (allObjects.TryGetValue(reference.ReferenceObjectNumber, out var obj))
                return obj as PdfDictionary;
            return null;
        }

        // -------------------------------------------------------------------
        //  Reference remapping (for the sequential rewrite)
        // -------------------------------------------------------------------

        private static PdfObject RemapReferences(PdfObject obj, Dictionary<int, int> remapTable)
        {
            if (obj is PdfReference reference)
            {
                int target = reference.ReferenceObjectNumber;
                if (remapTable.TryGetValue(target, out int newNum))
                    return new PdfReference(newNum, 0);
                return PdfNull.Instance;
            }

            if (obj is PdfStream stream)
            {
                var newStream = new PdfStream();
                foreach (var kvp in stream)
                    newStream.Set(kvp.Key, RemapReferences(kvp.Value, remapTable));
                var raw = stream.RawData ?? Array.Empty<byte>();
                newStream.RawData = raw;
                // Force /Length to match the actual raw bytes rather than carrying the
                // possibly-attacker-controlled value through. PdfWriter.WriteIndirectObject recomputes
                // /Length from RawData before serialising, so this keeps the in-memory dictionary
                // self-consistent.
                newStream.Set(PdfName.Length, new PdfNumber(raw.Length));
                return newStream;
            }

            if (obj is PdfDictionary dict)
            {
                var newDict = new PdfDictionary();
                foreach (var kvp in dict)
                    newDict.Set(kvp.Key, RemapReferences(kvp.Value, remapTable));
                return newDict;
            }

            if (obj is PdfArray array)
            {
                var newArray = new PdfArray();
                for (int i = 0; i < array.Count; i++)
                    newArray.Add(RemapReferences(array[i], remapTable));
                return newArray;
            }

            return obj;
        }

        // -------------------------------------------------------------------
        //  Helpers
        // -------------------------------------------------------------------

        private static PdfName ResolveAsName(PdfObject obj, PdfParser parser)
        {
            if (obj is PdfName direct) return direct;
            if (obj is PdfReference reference && parser != null)
                return parser.ResolveObject(reference) as PdfName;
            return null;
        }

        private static PdfDictionary ResolveAsDictionary(PdfObject obj, PdfParser parser)
        {
            if (obj is PdfDictionary dict)
                return dict;
            if (obj is PdfReference reference)
                return parser?.ResolveObject(reference) as PdfDictionary;
            return null;
        }

        // Blanks the bytes of a stream reached directly or by reference. A /JS whose value is a text
        // stream (§12.6.4.16) keeps its script in the stream, not the dictionary; emptying the key
        // without emptying the stream leaves the script in the file.
        private static void BlankStreamPayload(PdfObject obj, PdfParser parser)
        {
            if (obj is PdfReference reference)
                obj = parser?.ResolveObject(reference);
            if (obj is PdfStream stream)
                stream.RawData = Array.Empty<byte>();
        }

        // Given a file specification (the /FS value of a FileAttachment annotation, or an /AF entry),
        // strips its /EF entry and blanks the referenced embedded-file streams in place. The parser
        // caches objects, so the resolved stream is the instance the rewrite sees.
        /// <summary>
        /// What makes a reader fetch something by opening the file, in a kept object: a stream whose data
        /// is elsewhere (<c>/F</c>, §7.3.8.2), a reference XObject (<c>/Ref</c>, §8.10.4), a URL file
        /// specification wherever it stands (§7.11.5). A form submission's address is what
        /// <see cref="SanitizeOptions.RemoveFormActions"/> decides, and is passed over. Iterative, as a
        /// file chooses how deep a direct object nests.
        /// </summary>
        private static void RemoveRoutesOut(IEnumerable<PdfObject> objects)
        {
            var pending = new Stack<PdfObject>(objects);
            while (pending.Count > 0)
            {
                PdfObject o = pending.Pop();
                if (o is PdfArray a)
                {
                    foreach (PdfObject item in a)
                        if (!(item is PdfReference)) pending.Push(item);
                    continue;
                }
                if (!(o is PdfDictionary d) || d[PdfName.S] is PdfName kind && kind.Value == "SubmitForm") continue;

                if (d is PdfStream)
                {
                    d.Remove(PdfName.F);
                    d.Remove(PdfName.FFilter);
                    d.Remove(PdfName.FDecodeParms);
                    d.Remove(PdfName.Ref);
                }
                if (d[PdfName.FS] is PdfName system && system.Value == "URL")
                {
                    d.Remove(PdfName.F);
                    d.Remove(PdfName.UF);
                }

                foreach (var entry in d)
                    if (!(entry.Value is PdfReference)) pending.Push(entry.Value);
            }
        }


        /// <summary>
        /// Every dictionary among <paramref name="objects"/> and the direct objects inside them holding an
        /// <c>/EF</c> or <c>/RF</c>, which only a file specification does, emptied of it. Iterative: a
        /// file chooses how deep a direct object nests.
        /// </summary>
        private static void RemoveEveryFileSpecificationsPayload(IEnumerable<PdfObject> objects, PdfParser parser)
        {
            var pending = new Stack<PdfObject>(objects);
            while (pending.Count > 0)
            {
                PdfObject o = pending.Pop();
                if (o is PdfDictionary d)
                {
                    if (d.ContainsKey(PdfName.EF) || d.ContainsKey(RelatedFiles)) NeutralizeEmbeddedFiles(d, parser);
                    foreach (var entry in d)
                        if (!(entry.Value is PdfReference)) pending.Push(entry.Value);
                }
                else if (o is PdfArray a)
                {
                    foreach (PdfObject item in a)
                        if (!(item is PdfReference)) pending.Push(item);
                }
            }
        }

        /// <summary>A file specification's related files (Table 44): more embedded streams, keyed as <c>/EF</c> is.</summary>
        private static readonly PdfName RelatedFiles = new PdfName("RF");

        private static void NeutralizeEmbeddedFiles(PdfObject fsObj, PdfParser parser)
        {
            var fsDict = ResolveAsDictionary(fsObj, parser);
            if (fsDict == null) return;

            NeutralizeRelatedFiles(fsDict, parser);

            var efDict = ResolveAsDictionary(fsDict[PdfName.EF], parser);
            if (efDict != null)
            {
                // /EF maps keys (/F, /UF, /DOS, /Mac, /Unix) to embedded-file stream refs.
                foreach (var key in new List<PdfName>(efDict.Keys))
                {
                    if (parser?.ResolveObject(efDict[key]) is PdfStream efStream)
                    {
                        // The bytes and what describes them. /Params carries the file's size, checksum
                        // and dates, /Subtype its media type, so emptying the data alone left them
                        // behind; /Type /EmbeddedFile kept the empty stream an embedded file to a
                        // validator (PDF/X's X16).
                        foreach (var entry in new List<PdfName>(efStream.Keys))
                            efStream.Remove(entry);
                        efStream.RawData = Array.Empty<byte>();
                    }
                }
            }

            fsDict.Remove(PdfName.EF);
        }

        /// <summary><c>/RF</c>'s streams, each named in an array after its file's name, emptied and the entry gone.</summary>
        private static void NeutralizeRelatedFiles(PdfDictionary fsDict, PdfParser parser)
        {
            var related = ResolveAsDictionary(fsDict[RelatedFiles], parser);
            if (related != null)
                foreach (var key in new List<PdfName>(related.Keys))
                    if (parser?.ResolveObject(related[key]) is PdfArray pairs)
                        foreach (PdfObject item in pairs)
                            if (parser.ResolveObject(item) is PdfStream stream)
                            {
                                foreach (var entry in new List<PdfName>(stream.Keys))
                                    stream.Remove(entry);
                                stream.RawData = Array.Empty<byte>();
                            }
            fsDict.Remove(RelatedFiles);
        }
    }
}
