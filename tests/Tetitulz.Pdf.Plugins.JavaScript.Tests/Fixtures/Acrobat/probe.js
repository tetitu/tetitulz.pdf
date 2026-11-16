// Acrobat behaviour probe: every answer is written, one per line, into the field "results".
var out = [];
var doc = this;
function rec(id, f) {
  var v;
  try { v = f(); v = (typeof v) + ":" + String(v); }
  catch (e) { v = "throw:" + (e && e.name) + ":" + (e && e.message); }
  out.push(id + "\t" + v);
}
function q(s) { return s === null ? "null" : s === undefined ? "undefined" : "'" + s + "'"; }

// ---- A. surface census: typeof each member the reference lists
var surface = {
  "app": "activeDocs calculate constants focusRect formsVersion fromPDFConverters fs fullscreen language media monitors numPlugIns openInPlace platform plugIns printColorProfiles printerNames runtimeHighlight runtimeHighlightColor thermometer toolbar toolbarHorizontal toolbarVertical viewerType viewerVariation viewerVersion addMenuItem addSubMenu addToolButton alert beep beginPriv browseForDoc clearInterval clearTimeOut endPriv execDialog execMenuItem getNthPlugInName getPath goBack goForward hideMenuItem hideToolbarButton launchURL listMenuItems listToolbarButtons loadPolicyFile mailGetAddrs mailMsg newDoc newCollection newFDF openDoc openFDF popUpMenu popUpMenuEx removeToolButton response setInterval setTimeOut trustedFunction trustPropagatorFunction",
  "doc": "author baseURL bookmarkRoot calculate creationDate creator dataObjects delay dirty disclosed docID documentFileName dynamicXFAForm external filesize hidden hostContainer icons info keywords layout media metadata modDate mouseX mouseY noautocomplete numFields numPages numTemplates path pageNum pageWindowRect permStatusReady producer requiresFullSave securityHandler selectedAnnots sounds subject templates title URL viewState xfa zoom zoomType addAnnot addField addIcon addLink addScript bringToFront calculateNow closeDoc createDataObject createTemplate deletePages exportAsFDF exportAsText exportAsXFDF exportDataObject extractPages flattenPages getAnnot getAnnots getDataObject getField getIcon getLinks getNthFieldName getNthTemplate getOCGs getPageBox getPageLabel getPageNthWord getPageNumWords getPageRotation getPrintParams getTemplate getURL gotoNamedDest importAnFDF importAnXFDF importDataObject importIcon importTextData insertPages mailDoc mailForm movePage newPage print removeDataObject removeField removeIcon removeScript replacePages resetForm saveAs scroll selectPageNthWord setAction setPageAction setPageBoxes setPageLabels setPageRotations spawnPageFromTemplate submitForm syncAnnotScan getFieldNames",
  "field": "alignment borderStyle buttonAlignX buttonAlignY buttonFitBounds buttonPosition buttonScaleHow buttonScaleWhen calcOrderIndex charLimit comb commitOnSelChange currentValueIndices defaultStyle defaultValue doNotScroll doNotSpellCheck delay display doc editable exportValues fileSelect fillColor hidden highlight lineWidth multiline multipleSelection name numItems page password print radiosInUnison readonly readOnly rect required richText richValue rotation strokeColor style submitName textColor textFont textSize type userName value valueAsString browseForFileToSubmit buttonGetCaption buttonGetIcon buttonImportIcon buttonSetCaption buttonSetIcon checkThisBox clearItems defaultIsChecked deleteItemAt getArray getItemAt getLock insertItemAt isBoxChecked isDefaultChecked setAction setFocus setItems setLock signatureGetModifications signatureGetSeedValue signatureInfo signatureSetSeedValue signatureSign signatureValidate",
  "event": "change changeEx commitKey fieldFull keyDown modifier name rc richChange richChangeEx richValue selEnd selStart shift source target targetName type value willCommit",
  "util": "crackURL iconStreamFromIcon printd printf printx readFileIntoStream scand spansToXML streamFromString stringFromStream xmlToSpans byteToChar",
  "color": "transparent black white red green blue cyan magenta yellow dkGray gray ltGray convert equal",
  "console": "clear hide println show",
  "global": "setPersistent subscribe"
};
var holders = { "app": app, "doc": this, "field": this.getField("results"), "event": event, "util": util, "color": color, "console": console, "global": global };
for (var o in surface) {
  var names = surface[o].split(" ");
  for (var i = 0; i < names.length; i++) {
    (function (h, n) { rec("A." + o + "." + n, function () { return typeof h[n]; }); })(holders[o], names[i]);
  }
}
var globals = "border display position scaleHow scaleWhen style highlight font zoomtype cursor trans ADBE AFMakeNumber AFExtractNums AFParseDateEx AFMergeChange AFMakeArrayFromList AFNumber_Format AFNumber_Keystroke AFPercent_Format AFPercent_Keystroke AFDate_Format AFDate_FormatEx AFDate_Keystroke AFDate_KeystrokeEx AFTime_Format AFTime_FormatEx AFTime_Keystroke AFTime_KeystrokeEx AFSpecial_Format AFSpecial_Keystroke AFSpecial_KeystrokeEx AFRange_Validate AFSimple AFSimple_Calculate AFExactMatch eMailValidate getField calculateNow numFields probeDocFn".split(" ");
for (var g = 0; g < globals.length; g++) {
  (function (n) { rec("A.global." + n, function () { return eval("typeof " + n); }); })(globals[g]);
}

// ---- B. constant values
var consts = {
  "border": "s d b i u", "display": "visible hidden noPrint noView",
  "position": "textOnly iconOnly iconTextV textIconV iconTextH textIconH overlay",
  "scaleHow": "proportional anamorphic", "scaleWhen": "always never tooBig tooSmall",
  "style": "ch cr di ci st sq", "highlight": "n i p o",
  "font": "Times TimesB TimesI TimesBI Helv HelvB HelvI HelvBI Cour CourB CourI CourBI Symbol ZapfD KaGo KaMi",
  "zoomtype": "none fitP fitW fitH fitV pref refW", "cursor": "visible hidden delay"
};
for (var c in consts) {
  var ks = consts[c].split(" ");
  for (var k = 0; k < ks.length; k++) {
    (function (cn, kn) { rec("B." + cn + "." + kn, function () { return eval(cn)[kn]; }); })(c, ks[k]);
  }
}
var colours = "transparent black white red green blue cyan magenta yellow dkGray gray ltGray".split(" ");
for (var ci = 0; ci < colours.length; ci++) {
  (function (n) { rec("B.color." + n, function () { return color[n].join(","); }); })(colours[ci]);
}

// ---- C. AF helpers on values
var mk = ["abc", "1,2,3", " 12,5 ", "1.5", "-3", "12-", "", " ", "1,234.56", "1.234,56", ".5", ",5", "5.", "1e3", "0x10", "(5)", "$12", "12%", "--4", "+7"];
for (var m = 0; m < mk.length; m++) {
  (function (s) {
    rec("C.AFMakeNumber(" + q(s) + ")", function () { return AFMakeNumber(s); });
    rec("C.AFExtractNums(" + q(s) + ")", function () { var r = AFExtractNums(s); return r === null ? null : r.join("|"); });
  })(mk[m]);
}
rec("C.AFMakeNumber(7)", function () { return AFMakeNumber(7); });
rec("C.AFMakeNumber(null)", function () { return AFMakeNumber(null); });
rec("C.AFMakeNumber(true)", function () { return AFMakeNumber(true); });
rec("C.AFSimple(SUM,2,3)", function () { return AFSimple("SUM", 2, 3); });
rec("C.AFSimple(AVG,'2','x')", function () { return AFSimple("AVG", "2", "x"); });
rec("C.AFMakeArrayFromList('a, b,c')", function () { return AFMakeArrayFromList("a, b,c").join("|"); });
rec("C.AFParseDateEx('3/4/2026','m/d/yyyy')", function () { var d = AFParseDateEx("3/4/2026", "m/d/yyyy"); return d ? d.getFullYear() + "-" + (d.getMonth() + 1) + "-" + d.getDate() : d; });

// ---- D. formatting through event.value (the Doc/Open event)
var fmts = [
  ["AFNumber_Format(2,0,0,0,'',true)", "1234.5"], ["AFNumber_Format(2,0,0,0,'',true)", "-1234.5"],
  ["AFNumber_Format(2,0,1,0,'',true)", "-1234.5"], ["AFNumber_Format(2,0,2,0,'',true)", "-1234.5"],
  ["AFNumber_Format(2,0,3,0,'',true)", "-1234.5"], ["AFNumber_Format(2,2,0,0,'',true)", "1234.5"],
  ["AFNumber_Format(2,4,0,0,'',true)", "1234567.891"], ["AFNumber_Format(0,0,0,0,'$',true)", "-0.5"],
  ["AFNumber_Format(2,0,0,0,'EUR',false)", "-3"], ["AFNumber_Format(2,0,0,0,'',true)", "abc"],
  ["AFNumber_Format(2,0,0,0,'',true)", ""], ["AFPercent_Format(1,0)", "0.1234"],
  ["AFPercent_Format(1,0,true)", "0.1234"], ["AFPercent_Format(2,2)", "12.5"],
  ["AFPercent_Format(1,0)", "abc"], ["AFSpecial_Format(0)", "1234567"],
  ["AFSpecial_Format(1)", "123456789"], ["AFSpecial_Format(2)", "5551234"],
  ["AFSpecial_Format(2)", "4085551234"], ["AFSpecial_Format(3)", "123456789"],
  ["AFDate_FormatEx('yyyy-mm-dd')", "3/4/2026"], ["AFDate_Format(1)", "2026-03-04"],
  ["AFTime_Format(1)", "13:05"], ["AFTime_FormatEx('HH:MM:ss')", "1:05 pm"]
];
for (var f = 0; f < fmts.length; f++) {
  (function (call, v) {
    rec("D." + call + "<-" + q(v), function () { event.value = v; eval(call); return event.value; });
  })(fmts[f][0], fmts[f][1]);
}

// ---- E. util
var d0 = new Date(2026, 2, 4, 13, 5, 9);
var pd = ["mm/dd/yyyy", "m/d/yy", "mmmm d, yyyy", "ddd mmm d", "dddd", "HH:MM:ss", "h:MM tt", "yyyy-mm-dd'T'HH:MM", 0, 1, 2];
for (var p = 0; p < pd.length; p++) {
  (function (fm) { rec("E.printd(" + q(String(fm)) + ")", function () { return util.printd(fm, d0); }); })(pd[p]);
}
var pf = [["%d", 42.7], ["%5d", 42], ["%05d", 42], ["%+d", 42], ["%.2f", 3.14159], ["%,0.2f", 1234567.891],
          ["%,1.2f", 1234567.891], ["%,2.2f", 1234567.891], ["%,3.2f", 1234567.891], ["%,4.2f", 1234567.891],
          ["%x", 255], ["%#x", 255], ["%s|%s", "a"], ["%q", 1], ["%.0f", 2.5], ["%.0f", -0.4], ["%f", 1.5]];
for (var pi = 0; pi < pf.length; pi++) {
  (function (fm, a) { rec("E.printf(" + q(fm) + "," + a + ")", function () { return util.printf(fm, a, "b"); }); })(pf[pi][0], pf[pi][1]);
}
var px = [["(999) 999-9999", "4085551234"], [">AAA<aaa", "abcDEF"], ["9-9", "a1b2c3"], ["\\9?*", "xyz123"], ["X X", "!a?b"]];
for (var xi = 0; xi < px.length; xi++) {
  (function (fm, s) { rec("E.printx(" + q(fm) + "," + q(s) + ")", function () { return util.printx(fm, s); }); })(px[xi][0], px[xi][1]);
}
rec("E.scand", function () { var d = util.scand("mm/dd/yyyy", "03/04/2026"); return d ? d.getFullYear() + "-" + (d.getMonth() + 1) + "-" + d.getDate() : d; });
rec("E.crackURL", function () { var u = util.crackURL("http://user@host.example:8080/p/a?x=1#h"); return [u.cScheme, u.cUser, u.cHost, u.nPort, u.cPath, u.cQuery, u.cFragment].join("|"); });

// ---- F. color
var cv = [[["RGB", 1, 0, 0], "G"], [["RGB", 1, 0, 0], "CMYK"], [["CMYK", 1, 0, 0, 0], "RGB"], [["CMYK", 0.2, 0.3, 0.4, 0.1], "G"],
          [["G", 0.5], "RGB"], [["G", 0.5], "CMYK"], [["T"], "RGB"], [["RGB", 1, 0, 0], "T"], [["RGB", 1, 0, 0], "XYZ"]];
for (var vi = 0; vi < cv.length; vi++) {
  (function (a, sp) { rec("F.convert(" + a.join(",") + "->" + sp + ")", function () { return color.convert(a, sp).join(","); }); })(cv[vi][0], cv[vi][1]);
}
rec("F.equal(red,RGB100)", function () { return color.equal(color.red, ["RGB", 1, 0, 0]); });
rec("F.equal(black,G0)", function () { return color.equal(["RGB", 0, 0, 0], ["G", 0]); });

// ---- G. fields, scope, timers
var num = this.getField("num");
rec("G.num.value='12.5' typeof", function () { num.value = "12.5"; return typeof num.value; });
rec("G.num.value='12,5' typeof", function () { num.value = "12,5"; return typeof num.value + "/" + num.value; });
rec("G.num.value='007' value", function () { num.value = "007"; return typeof num.value + "/" + num.value; });
rec("G.num.value=' ' typeof", function () { num.value = " "; return typeof num.value + "/" + num.value; });
rec("G.num.valueAsString", function () { num.value = 3; return typeof num.valueAsString + "/" + num.valueAsString; });
rec("G.sum", function () { num.value = "2"; return num.value + num.value; });
rec("G.results.type", function () { return doc.getField("results").type; });
rec("G.results.rect", function () { return doc.getField("results").rect.join(","); });
rec("G.results.page", function () { return doc.getField("results").page; });
rec("G.numFields", function () { return doc.numFields; });
rec("G.getNthFieldName(0)", function () { return doc.getNthFieldName(0); });
rec("G.probeDocFn()", function () { return probeDocFn(); });
rec("G.globalThisIsDoc", function () { return (function () { return this; })() === doc; });
rec("G.bareGetField", function () { return typeof getField; });
rec("G.event.type/name", function () { return event.type + "/" + event.name; });
rec("G.event.target", function () { return typeof event.target; });
rec("G.global.x", function () { global.probeX = 5; return global.probeX; });
rec("G.setTimeOut", function () { var t = app.setTimeOut("this.getField('timer').value = 'fired';", 200); return typeof t; });
rec("G.execDialog", function () { return typeof app.execDialog; });

this.getField("results").value = out.join("\n");
app.alert("Probe finished: " + out.length + " answers. Wait one second, then save this file (Ctrl+S) and close it.");
