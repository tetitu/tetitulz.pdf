// Acrobat behaviour probe, round 2 (GAPS.md §25a): every answer is written, one per line, into the
// field "results". Run as the open action of AcrobatProbe2.pdf; wait three seconds, then save.
var out = [];
var doc = this;
function rec(id, f) {
  var v;
  try { v = f(); v = (typeof v) + ":" + String(v); }
  catch (e) { v = "throw:" + (e && e.name) + ":" + (e && e.message); }
  out.push(id + "\t" + v);
}
function q(s) { return s === null ? "null" : s === undefined ? "undefined" : "'" + s + "'"; }
function keys(o) { var k = []; for (var p in o) k.push(p); return k.sort().join(","); }

// ---- M. Matrix2D, Acrobat's built-in transform object
rec("M.typeof", function () { return typeof Matrix2D; });
rec("M.keys(new)", function () { return keys(new Matrix2D()); });
rec("M.keys(proto)", function () { return keys(Matrix2D.prototype); });
rec("M.new()", function () { var m = new Matrix2D(); return [m.a, m.b, m.c, m.d, m.tx, m.ty, m.h, m.v, m.e, m.f].join(","); });
rec("M.new(1..6)", function () { var m = new Matrix2D(1, 2, 3, 4, 5, 6); return [m.a, m.b, m.c, m.d, m.tx, m.ty, m.h, m.v, m.e, m.f].join(","); });
rec("M.toString", function () { return String(new Matrix2D(1, 2, 3, 4, 5, 6)); });
rec("M.concat", function () { var m = new Matrix2D(1, 2, 3, 4, 5, 6).concat(new Matrix2D(7, 8, 9, 10, 11, 12)); return [m.a, m.b, m.c, m.d, m.tx, m.ty, m.h, m.v].join(","); });
rec("M.concat.mutates", function () { var m = new Matrix2D(1, 2, 3, 4, 5, 6); var r = m.concat(new Matrix2D(2, 0, 0, 2, 0, 0)); return (r === m) + "|" + [m.a, m.b, m.c, m.d].join(","); });
rec("M.invert", function () { var m = new Matrix2D(2, 0, 0, 4, 10, 20).invert(); return [m.a, m.b, m.c, m.d, m.tx, m.ty, m.h, m.v].join(","); });
rec("M.invert.mutates", function () { var m = new Matrix2D(2, 0, 0, 4, 10, 20); var r = m.invert(); return (r === m) + "|" + [m.a, m.d].join(","); });
rec("M.invert.singular", function () { var m = new Matrix2D(0, 0, 0, 0, 1, 1).invert(); return [m.a, m.b, m.c, m.d].join(","); });
rec("M.transform([x,y])", function () { var r = new Matrix2D(1, 2, 3, 4, 5, 6).transform([10, 20]); return typeof r + "|" + String(r); });
rec("M.transform([[x,y],[x,y]])", function () { var r = new Matrix2D(1, 2, 3, 4, 5, 6).transform([[10, 20], [1, 1]]); return String(r); });
rec("M.transform([x1,y1,x2,y2])", function () { var r = new Matrix2D(1, 2, 3, 4, 5, 6).transform([10, 20, 1, 1]); return String(r); });
rec("M.fromRotated(this,0)", function () { var m = (new Matrix2D()).fromRotated(doc, 0); return [m.a, m.b, m.c, m.d, m.tx, m.ty, m.h, m.v].join(","); });
rec("M.fromRotated(this,1)", function () { var m = (new Matrix2D()).fromRotated(doc, 1); return [m.a, m.b, m.c, m.d, m.tx, m.ty, m.h, m.v].join(","); });
rec("M.fromRotated(this,2)", function () { var m = (new Matrix2D()).fromRotated(doc, 2); return [m.a, m.b, m.c, m.d, m.tx, m.ty, m.h, m.v].join(","); });
rec("M.fromRotated.returns", function () { var m = new Matrix2D(); return m.fromRotated(doc, 0) === m; });
rec("M.methods", function () { var n = []; var m = new Matrix2D(); for (var p in m) if (typeof m[p] === "function") n.push(p); return n.sort().join(","); });

// ---- D. dates
var yy = ["1/2/00", "1/2/29", "1/2/30", "1/2/49", "1/2/50", "1/2/69", "1/2/70", "1/2/99"];
for (var i = 0; i < yy.length; i++) (function (s) {
  rec("D.AFParseDateEx(" + q(s) + ",m/d/yy)", function () { var d = AFParseDateEx(s, "m/d/yy"); return d ? d.getFullYear() + "-" + (d.getMonth() + 1) + "-" + d.getDate() : d; });
})(yy[i]);
var d0 = new Date(2026, 2, 4, 13, 5, 9);
var pd = ["'d' d", "'day' d", "\\d d", "\\\\ d", "d 'mmm' m", "''", "'"];
for (var j = 0; j < pd.length; j++) (function (f) { rec("D.printd(" + q(f) + ")", function () { return util.printd(f, d0); }); })(pd[j]);
var ps = [["mmm d, yyyy", "March 4 2026"], ["mmm d, yyyy", "Mar 4, 2026"], ["m/d/yyyy", "2026-03-04"], ["yyyy-mm-dd", "2026/3/4"],
          ["m/d/yyyy", "13/4/2026"], ["m/d/yyyy", "2/30/2026"], ["m/d", "3/4"], ["HH:MM", "13:05"], ["h:MM tt", "1:05 pm"],
          ["m/d/yyyy", "3/4"], ["d/m/yyyy x", "3/4/2026"], ["m/d/yyyy", "3.4.2026 extra 99"]];
for (var k = 0; k < ps.length; k++) (function (f, s) {
  rec("D.scand(" + q(f) + "," + q(s) + ")", function () { var d = util.scand(f, s); return d ? d.getFullYear() + "-" + (d.getMonth() + 1) + "-" + d.getDate() + " " + d.getHours() + ":" + d.getMinutes() : d; });
})(ps[k][0], ps[k][1]);

// ---- P. printf
rec("P.tooFew", function () { return util.printf("%d %d", 1); });
rec("P.%d 1234567", function () { return util.printf("%d", 1234567); });
rec("P.%f 1234.5", function () { return util.printf("%f", 1234.5); });
rec("P.%.2f 1234.5", function () { return util.printf("%.2f", 1234.5); });
rec("P.%,0d 1234567", function () { return util.printf("%,0d", 1234567); });
rec("P.%,1d 1234567", function () { return util.printf("%,1d", 1234567); });
rec("P.%s 12", function () { return util.printf("%s", 12); });
rec("P.%5s", function () { return util.printf("[%5s]", "ab"); });
rec("P.%%", function () { return util.printf("100%%"); });
rec("P.% d", function () { return util.printf("[% d]", 5); });
rec("P.%x -255", function () { return util.printf("%x", -255); });
rec("P.%d '12abc'", function () { return util.printf("%d", "12abc"); });
rec("P.%f 'x'", function () { return util.printf("%f", "x"); });
rec("P.%.3f 0.0005", function () { return util.printf("%.3f", 0.0005); });
rec("P.%.0f 0.5", function () { return util.printf("%.0f", 0.5); });
rec("P.%.0f -0.5", function () { return util.printf("%.0f", -0.5); });
rec("P.%.0f -1.5", function () { return util.printf("%.0f", -1.5); });
rec("P.%e", function () { return util.printf("%e", 1234.5); });

// ---- S. AFSimple_Calculate over fields that are empty and unreadable
rec("S.AVG(p1..p3)", function () { event.value = "x"; AFSimple_Calculate("AVG", ["p1", "p2", "p3"]); return typeof event.value + "/" + event.value; });
rec("S.SUM(p1..p3)", function () { event.value = "x"; AFSimple_Calculate("SUM", "p1, p2, p3"); return typeof event.value + "/" + event.value; });
rec("S.MIN(p1..p3)", function () { event.value = "x"; AFSimple_Calculate("MIN", ["p1", "p2", "p3"]); return event.value; });
rec("S.PRD(p1..p3)", function () { event.value = "x"; AFSimple_Calculate("PRD", ["p1", "p2", "p3"]); return event.value; });
rec("S.SUM(grp)", function () { event.value = "x"; AFSimple_Calculate("SUM", ["grp"]); return event.value; });
rec("S.SUM(missing)", function () { event.value = "x"; AFSimple_Calculate("SUM", ["noSuchField"]); return event.value; });

// ---- A. the alerts the AF functions write (app.alert replaced, if Acrobat lets a script replace it)
var alerts = [];
var origAlert = app.alert;
rec("A.replaceAlert", function () { app.alert = function (m) { alerts.push(typeof m === "object" ? m.cMsg : m); return 1; }; return app.alert === origAlert ? "kept" : "replaced"; });
function lastAlert(f) { return function () { alerts.length = 0; try { f(); } catch (e) { return "throw:" + e.name; } return alerts.length ? alerts[alerts.length - 1] : "(none)"; }; }
rec("A.range(both)", lastAlert(function () { event.value = "50"; AFRange_Validate(true, 0, true, 10); }));
rec("A.range(gt)", lastAlert(function () { event.value = "-5"; AFRange_Validate(true, 0, false, 0); }));
rec("A.range(lt)", lastAlert(function () { event.value = "50"; AFRange_Validate(false, 0, true, 10); }));
rec("A.range.rc", function () { event.value = "50"; event.rc = true; AFRange_Validate(true, 0, true, 10); return event.rc; });
rec("A.dateKeystroke", lastAlert(function () { event.value = "99/99/9999"; event.willCommit = true; AFDate_KeystrokeEx("mm/dd/yyyy"); }));
rec("A.numberKeystroke", lastAlert(function () { event.value = "12x"; event.change = ""; event.willCommit = true; AFNumber_Keystroke(2, 0, 0, 0, "", true); }));
rec("A.numberKeystroke.rc", function () { event.value = "12x"; event.rc = true; event.willCommit = true; AFNumber_Keystroke(2, 0, 0, 0, "", true); return event.rc; });
rec("A.specialKeystroke", lastAlert(function () { event.value = "12"; event.willCommit = true; AFSpecial_Keystroke(0); }));
rec("A.event.willCommit settable", function () { event.willCommit = true; return event.willCommit; });
app.alert = origAlert;

// ---- E. eMailValidate
var em = ["a@b.com", "a.b+c@d-e.co.uk", "a@b", "@b.com", "a b@c.com", "a@b..com", "A@B.COM", "a@b.c"];
for (var e = 0; e < em.length; e++) (function (s) { rec("E.eMailValidate(" + q(s) + ")", function () { return eMailValidate(s); }); })(em[e]);

// ---- F. fields of each type: which properties throw, captions, widgets, groups
var probes = "alignment borderStyle buttonAlignX buttonAlignY buttonFitBounds buttonPosition buttonScaleHow buttonScaleWhen calcOrderIndex charLimit comb commitOnSelChange currentValueIndices defaultStyle defaultValue doNotScroll doNotSpellCheck display editable exportValues fileSelect fillColor hidden highlight lineWidth multiline multipleSelection numItems page password print radiosInUnison readonly rect required richText richValue rotation strokeColor style submitName textColor textFont textSize type userName value valueAsString".split(" ");
var typed = ["pb", "cb", "rb", "co", "li"];
for (var t = 0; t < typed.length; t++) {
  for (var p = 0; p < probes.length; p++) (function (fn, pn) {
    rec("F." + fn + "." + pn, function () { var v = doc.getField(fn)[pn]; return typeof v === "object" && v !== null && typeof v.length === "number" ? "[" + Array.prototype.join.call(v, ",") + "]" : v; });
  })(typed[t], probes[p]);
}
rec("F.pb.buttonGetCaption()", function () { return doc.getField("pb").buttonGetCaption(); });
rec("F.pb.buttonGetCaption(1)", function () { return doc.getField("pb").buttonGetCaption(1); });
rec("F.pb.buttonGetCaption(2)", function () { return doc.getField("pb").buttonGetCaption(2); });
rec("F.cb.isBoxChecked(0)", function () { return doc.getField("cb").isBoxChecked(0); });
rec("F.rb.exportValues", function () { return doc.getField("rb").exportValues.join(","); });
rec("F.rb.0.rect", function () { return doc.getField("rb.0").rect.join(","); });
rec("F.rb.1.rect", function () { return doc.getField("rb.1").rect.join(","); });
rec("F.rb.1.page", function () { return doc.getField("rb.1").page; });
rec("F.rb.2", function () { return doc.getField("rb.2"); });
rec("F.rb.page", function () { var p = doc.getField("rb").page; return typeof p === "object" ? "[" + p.join(",") + "]" : p; });
rec("F.co.getItemAt(1)", function () { return doc.getField("co").getItemAt(1); });
rec("F.co.getItemAt(1,false)", function () { return doc.getField("co").getItemAt(1, false); });
rec("F.co.getItemAt(-1)", function () { return doc.getField("co").getItemAt(-1); });
rec("F.co.getItemAt(9)", function () { return doc.getField("co").getItemAt(9); });
rec("F.co.value", function () { return doc.getField("co").value; });
rec("F.grp", function () { return doc.getField("grp") === null ? "null" : doc.getField("grp").name; });
rec("F.grp.type", function () { return doc.getField("grp").type; });
rec("F.grp.value", function () { return doc.getField("grp").value; });
rec("F.grp.getArray", function () { var a = doc.getField("grp").getArray(); var n = []; for (var i = 0; i < a.length; i++) n.push(a[i].name); return n.join(","); });
rec("F.getField(missing)", function () { return doc.getField("noSuchField"); });
rec("F.numFields", function () { return doc.numFields; });
rec("F.getNthFieldName(all)", function () { var n = []; for (var i = 0; i < doc.numFields; i++) n.push(doc.getNthFieldName(i)); return n.join(","); });

// ---- I. the document's /Info and its attachments
var infoKeys = "author title subject keywords creator producer creationDate modDate".split(" ");
for (var ik = 0; ik < infoKeys.length; ik++) (function (k) {
  rec("I." + k, function () { var v = doc[k]; return v instanceof Date ? "Date:" + v.getFullYear() + "-" + (v.getMonth() + 1) + "-" + v.getDate() + " " + v.getHours() + ":" + v.getMinutes() : v; });
})(infoKeys[ik]);
rec("I.info.keys", function () { return keys(doc.info); });
rec("I.info.Author", function () { return doc.info.Author; });
rec("I.info.author", function () { return doc.info.author; });
rec("I.info.Custom", function () { return doc.info.Custom; });
rec("I.dataObjects", function () { var d = doc.dataObjects; return d === null ? "null" : d.length; });
rec("I.dataObjects[0].keys", function () { return keys(doc.dataObjects[0]); });
var dkeys = "name path size MIMEType description creationDate modDate".split(" ");
for (var dk = 0; dk < dkeys.length; dk++) (function (k) {
  rec("I.dataObjects[0]." + k, function () { var v = doc.dataObjects[0][k]; return v instanceof Date ? "Date:" + v.getFullYear() : v; });
})(dkeys[dk]);
rec("I.getDataObject(name)", function () { var o = doc.getDataObject(doc.dataObjects[0].name); return o ? o.name : o; });
rec("I.getDataObjectContents", function () { var s = doc.getDataObjectContents(doc.dataObjects[0].name); return util.stringFromStream(s); });

// ---- T. timers: they write into "timer" and "ival" if they run before the file is saved
rec("T.setTimeOut", function () { var t = app.setTimeOut("this.getField('timer').value = 'fired';", 300); return typeof t + "|" + keys(t); });
rec("T.setInterval", function () { var n = 0; app.setInterval("var f = this.getField('ival'); f.value = Number(f.value || 0) + 1;", 500); return "started"; });

doc.getField("results").value = out.join("\n");
app.alert("Probe 2 finished: " + out.length + " answers. Wait three seconds, then save this file (Ctrl+S) and close it.");
