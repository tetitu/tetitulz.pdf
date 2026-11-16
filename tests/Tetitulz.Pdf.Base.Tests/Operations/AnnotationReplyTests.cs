// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A reply (<c>/IRT</c>, Table 170) names the comment it answers by that comment's <c>/NM</c>, with
/// its relationship (<c>/RT</c>); a comment that replies to none, or to one that has no name, names
/// nothing.
/// </summary>
public class AnnotationReplyTests
{
    [Fact]
    public void AReply_NamesTheCommentItAnswers_AndItsRelationship()
    {
        byte[] pdf = FormPdfFixtures.Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R 5 0 R 6 0 R 7 0 R 8 0 R] >>",
            "<< /Type /Annot /Subtype /Text /NM (note-1) /Rect [10 10 30 30] /Contents (Original) >>",
            "<< /Type /Annot /Subtype /Text /NM (reply-1) /Rect [10 40 30 60] /IRT 4 0 R /Contents (Agreed) >>",
            "<< /Type /Annot /Subtype /Text /NM (member) /Rect [10 70 30 90] /IRT 4 0 R /RT /Group >>",
            "<< /Type /Annot /Subtype /Text /Rect [10 100 30 120] /Contents (Unnamed) >>",
            "<< /Type /Annot /Subtype /Text /NM (orphan) /Rect [10 130 30 150] /IRT 7 0 R >>");

        var annots = PdfAnnotationExtractor.ListAnnotations(new MemoryStream(pdf));

        Assert.Equal(5, annots.Count);
        Assert.Null(annots[0].InReplyToName);
        Assert.Null(annots[0].ReplyType);
        Assert.Equal("note-1", annots[1].InReplyToName);
        Assert.Equal("R", annots[1].ReplyType);                      // the default relationship
        Assert.Equal("note-1", annots[2].InReplyToName);
        Assert.Equal("Group", annots[2].ReplyType);
        Assert.Null(annots[4].InReplyToName);                        // a reply to an unnamed comment
        Assert.Equal("R", annots[4].ReplyType);                      // but a reply all the same
        Assert.Equal(new[] { "note-1", "reply-1", "member", null, "orphan" }, annots.Select(a => a.Name));
    }
}
