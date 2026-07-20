// Standalone, dependency-free test for the config selector's PURE decision
// logic in scripts.js: sbConfigFilterMatches/sbParseConfigList (the selector's
// show/hide decision), plus getExtension/isFile/isSupportedExtension (the
// hash-router's file-classification logic, which the config-variant
// auto-navigate feature depends on -- see the config-variant-suffix cases
// below). This is NOT wired into `dotnet test` -- there is no JS test
// framework anywhere in this repo (no package.json/npm/Node), and pulling in
// one for a handful of small functions would be a heavier dependency than the
// feature warrants. Instead this is a small, manually-run assertion script
// written in the same ES5 dialect as scripts.js, executable with either:
//
//   cscript //nologo //e:jscript configSelectorFilter.tests.js     (Windows, zero install --
//                                                                    Windows Script Host ships with Windows)
//   node configSelectorFilter.tests.js                              (if Node happens to be available)
//
// Run from this file's own directory (src/HtmlGenerator.Tests/ClientScriptTests/)
// so the relative lookup of scripts.js below resolves correctly.
//
// Deliberately kept OUTSIDE src/SourceIndexServer/wwwroot (and outside the
// SourceIndexServer project entirely) -- anything published from that project
// gets wholesale-copied into every generated site by WebsiteFinalizer.Finalize
// (FileUtilities.CopyDirectory copies the whole publish output, unfiltered).
// This is a dev-only test script, not a runtime artifact, so it lives under
// HtmlGenerator.Tests instead, which is never published to a generated site.
//
// It reads scripts.js off disk (relative to this file, via the sibling
// SourceIndexServer project) and extracts ONLY the pure functions/statements
// under test (by matching balanced braces/brackets, starting from each
// signature) rather than loading the whole file -- scripts.js has real
// top-level DOM calls (e.g. `document.addEventListener(...)` at file scope)
// that would throw immediately in a non-browser engine. This keeps the test
// exercising the ACTUAL production function bodies (no hand-duplicated
// copy that could silently drift from what ships), while never touching any
// browser-only global.
//
// IMPORTANT: this only validates pure logic (the filter decision plus the
// hash-router's file classification). It does NOT validate sbApplyConfigFilter,
// sbMountConfigSelectorIntoContentPage, sbTryAutoNavigateToVariant, or any
// other DOM-manipulation code in scripts.js -- those have been separately
// verified live in a real browser (pill toggling, #if-region grey/highlight
// switching, and auto-navigate all confirmed working end-to-end against the
// multi-config demo site), but are not exercised by this dependency-free
// harness. See the prominent comment block in scripts.js immediately above
// sbConfigFilterMatches.

function sbTestReadScriptsJs() {
    var fso = new ActiveXObject("Scripting.FileSystemObject");
    var thisScriptPath = WScript.ScriptFullName;
    var thisDir = fso.GetParentFolderName(thisScriptPath);
    // this file:        src/HtmlGenerator.Tests/ClientScriptTests/configSelectorFilter.tests.js
    // scripts.js:       src/SourceIndexServer/wwwroot/scripts.js
    var scriptsJsPath = fso.BuildPath(thisDir, "..\\..\\SourceIndexServer\\wwwroot\\scripts.js");
    var stream = fso.OpenTextFile(scriptsJsPath, 1);
    var contents = stream.ReadAll();
    stream.Close();
    return contents;
}

// cscript's JScript engine is ES3 and has no Array.prototype.indexOf (added in
// ES5); isSupportedExtension() -- real production code that runs fine in
// every actual browser -- relies on it. Polyfill it here, in the TEST harness
// only, rather than touching scripts.js for an engine limitation that doesn't
// exist in any real browser target.
if (!Array.prototype.indexOf) {
    Array.prototype.indexOf = function (searchElement) {
        for (var i = 0; i < this.length; i++) {
            if (this[i] === searchElement) {
                return i;
            }
        }
        return -1;
    };
}

// Extracts "function <name>(...) { ... }" (with balanced braces) out of
// `source`, starting at the first occurrence of "function <name>(".
function sbTestExtractFunction(source, name) {
    var marker = "function " + name + "(";
    var start = source.indexOf(marker);
    if (start === -1) {
        throw new Error("Could not find function '" + name + "' in scripts.js -- extraction marker out of date?");
    }

    var openBrace = source.indexOf("{", start);
    if (openBrace === -1) {
        throw new Error("Could not find opening brace for function '" + name + "'.");
    }

    var depth = 0;
    var i = openBrace;
    for (; i < source.length; i++) {
        var ch = source.charAt(i);
        if (ch === "{") {
            depth++;
        } else if (ch === "}") {
            depth--;
            if (depth === 0) {
                break;
            }
        }
    }

    if (depth !== 0) {
        throw new Error("Unbalanced braces while extracting function '" + name + "'.");
    }

    return source.substring(start, i + 1);
}

// Extracts "var <name> = [ ... ];" (with balanced brackets), mirroring
// sbTestExtractFunction above but for an array-literal statement instead of a
// function body -- used to pull supportedFileExtensions out of scripts.js
// without loading the whole file (which has real top-level DOM calls).
function sbTestExtractArrayStatement(source, name) {
    var marker = "var " + name + " = [";
    var start = source.indexOf(marker);
    if (start === -1) {
        throw new Error("Could not find array statement '" + name + "' in scripts.js -- extraction marker out of date?");
    }

    var openBracket = source.indexOf("[", start);
    var depth = 0;
    var i = openBracket;
    for (; i < source.length; i++) {
        var ch = source.charAt(i);
        if (ch === "[") {
            depth++;
        } else if (ch === "]") {
            depth--;
            if (depth === 0) {
                break;
            }
        }
    }

    if (depth !== 0) {
        throw new Error("Unbalanced brackets while extracting array statement '" + name + "'.");
    }

    // Include the trailing ';' if present, right after the closing bracket.
    var end = i + 1;
    if (source.charAt(end) === ";") {
        end++;
    }

    return source.substring(start, end);
}

// Extracts a single-line "var <name> = ...;" statement -- used to pull
// configVariantSuffixRegex (getExtension's dependency) out of scripts.js.
function sbTestExtractSingleLineVarStatement(source, name) {
    var marker = "var " + name + " = ";
    var start = source.indexOf(marker);
    if (start === -1) {
        throw new Error("Could not find var statement '" + name + "' in scripts.js -- extraction marker out of date?");
    }

    var end = source.indexOf(";", start);
    if (end === -1) {
        throw new Error("Could not find terminating ';' for var statement '" + name + "'.");
    }

    return source.substring(start, end + 1);
}

function sbTestLoadPureFunctionsUnderTest() {
    var source = sbTestReadScriptsJs();
    var extracted = sbTestExtractFunction(source, "sbConfigFilterMatches")
        + "\n"
        + sbTestExtractFunction(source, "sbParseConfigList")
        + "\n"
        + sbTestExtractFunction(source, "sbDeriveSelectedConfigsFromAxisSelections")
        + "\n"
        + sbTestExtractFunction(source, "sbContains")
        + "\n"
        + sbTestExtractArrayStatement(source, "supportedFileExtensions")
        + "\n"
        + sbTestExtractFunction(source, "endsWithIgnoreCase")
        + "\n"
        + sbTestExtractSingleLineVarStatement(source, "configVariantSuffixRegex")
        + "\n"
        + sbTestExtractFunction(source, "getExtension")
        + "\n"
        + sbTestExtractFunction(source, "isSupportedExtension")
        + "\n"
        + sbTestExtractFunction(source, "isFile");

    // eval defines these names in this function's scope; return them so the
    // caller can invoke them directly.
    eval(extracted);
    return {
        sbConfigFilterMatches: sbConfigFilterMatches,
        sbParseConfigList: sbParseConfigList,
        sbDeriveSelectedConfigsFromAxisSelections: sbDeriveSelectedConfigsFromAxisSelections,
        sbContains: sbContains,
        getExtension: getExtension,
        isFile: isFile
    };
}

function sbTestRun() {
    var underTest = sbTestLoadPureFunctionsUnderTest();
    var sbConfigFilterMatches = underTest.sbConfigFilterMatches;
    var sbParseConfigList = underTest.sbParseConfigList;
    var sbDeriveSelectedConfigsFromAxisSelections = underTest.sbDeriveSelectedConfigsFromAxisSelections;
    var sbContains = underTest.sbContains;
    var getExtension = underTest.getExtension;
    var isFile = underTest.isFile;

    var passed = 0;
    var failed = 0;

    function assert(description, actual, expected) {
        if (actual === expected) {
            passed++;
        } else {
            failed++;
            WScript.Echo("FAIL: " + description + " -- expected " + expected + ", got " + actual);
        }
    }

    // Untagged elements (no data-configs at all) are always shown, regardless
    // of selection.
    assert("untagged, no selection -> shown", sbConfigFilterMatches([], null), true);
    assert("untagged, some selection -> shown", sbConfigFilterMatches(["windows"], null), true);
    assert("untagged empty-string attr -> shown", sbConfigFilterMatches(["windows"], ""), true);

    // No selection shows everything (the union), regardless of tag.
    assert("tagged, no selection (empty array) -> shown", sbConfigFilterMatches([], "windows"), true);
    assert("tagged, no selection (null) -> shown", sbConfigFilterMatches(null, "windows"), true);

    // Overlap / non-overlap between selection and tag.
    assert("selected=[windows], tag=windows -> shown", sbConfigFilterMatches(["windows"], "windows"), true);
    assert("selected=[linux], tag=windows -> hidden", sbConfigFilterMatches(["linux"], "windows"), false);
    assert("selected=[linux,mac], tag=windows,mac -> shown (overlap on mac)", sbConfigFilterMatches(["linux", "mac"], "windows,mac"), true);
    assert("selected=[linux], tag=windows,mac -> hidden (no overlap)", sbConfigFilterMatches(["linux"], "windows,mac"), false);

    // Case-insensitivity, matching the server's OrdinalIgnoreCase comparisons.
    // sbParseConfigList lower-cases; the selection is expected to already be
    // lower-cased by sbGetSelectedConfigs/sbOnConfigSelectionChanged, so we
    // simulate that contract here.
    assert("case-insensitive match", sbConfigFilterMatches(["windows"], "Windows"), true);

    // Whitespace and multi-value parsing.
    var parsed = sbParseConfigList(" windows , mac ");
    assert("parses 2 entries", parsed.length, 2);
    assert("trims/lower-cases first entry", parsed[0], "windows");
    assert("trims/lower-cases second entry", parsed[1], "mac");
    assert("empty attr parses to 0 entries", sbParseConfigList("").length, 0);
    assert("null attr parses to 0 entries", sbParseConfigList(null).length, 0);

    // ConfigFileDeduper (Pass2) disambiguates divergent config-variant pages
    // by inserting "~" + an 8-hex-digit content hash immediately before the
    // extension (e.g. on-disk "EnvHelper.cs~87f21542.html", linked from the
    // client hash as "Demo/EnvHelper.cs~87f21542"). getExtension()/isFile()
    // must see through that suffix, or the auto-navigate-to-variant feature
    // silently no-ops (processHash() falls through without redirecting the
    // "s" frame) -- this was a real bug caught via live-browser validation,
    // not just a hypothetical.
    assert("extension unaffected by ordinary filename", getExtension("Demo/EnvHelper.cs"), "cs");
    assert("extension survives config-variant hash suffix", getExtension("Demo/EnvHelper.cs~87f21542"), "cs");
    assert("extension survives suffix + .html", getExtension("Demo/EnvHelper.cs~87f21542.html"), "html");
    assert("short/non-hex tilde suffix is NOT stripped (not a real variant hash)", getExtension("Demo/Weird.cs~abc"), "cs~abc");
    assert("isFile true for ordinary source file", isFile("Demo/EnvHelper.cs"), true);
    assert("isFile true for config-variant suffixed file (the regression)", isFile("Demo/EnvHelper.cs~87f21542"), true);
    assert("isFile true for config-variant suffixed file with .html", isFile("Demo/EnvHelper.cs~87f21542.html"), true);

    // sbDeriveSelectedConfigsFromAxisSelections -- the multi-axis panel's pure
    // "which flat config names match this per-axis selection" derivation (#104
    // multi-axis scaling: os x arch and beyond, not just one flat dimension).
    var configAxisValues = {
        "windows-x64": { os: "windows", arch: "x64" },
        "windows-arm64": { os: "windows", arch: "arm64" },
        "linux-x64": { os: "linux", arch: "x64" },
        "linux-arm64": { os: "linux", arch: "arm64" }
    };
    var allConfigs = ["windows-x64", "windows-arm64", "linux-x64", "linux-arm64"];

    assert(
        "no restriction on any axis -> every config matches",
        sbDeriveSelectedConfigsFromAxisSelections({ os: [], arch: [] }, configAxisValues, allConfigs).length,
        4);

    var osWindowsOnly = sbDeriveSelectedConfigsFromAxisSelections({ os: ["windows"], arch: [] }, configAxisValues, allConfigs);
    assert("os=windows restriction -> 2 matches", osWindowsOnly.length, 2);
    assert("os=windows restriction includes windows-x64", sbContains(osWindowsOnly, "windows-x64"), true);
    assert("os=windows restriction excludes linux-x64", sbContains(osWindowsOnly, "linux-x64"), false);

    var windowsX64Only = sbDeriveSelectedConfigsFromAxisSelections({ os: ["windows"], arch: ["x64"] }, configAxisValues, allConfigs);
    assert("os=windows AND arch=x64 -> exactly 1 match (AND across axes)", windowsX64Only.length, 1);
    assert("os=windows AND arch=x64 -> matches windows-x64", windowsX64Only[0], "windows-x64");

    var x64Either = sbDeriveSelectedConfigsFromAxisSelections({ os: [], arch: ["x64"] }, configAxisValues, allConfigs);
    assert("arch=x64 only -> 2 matches (OR within an axis is moot with 1 value, but restriction still applies)", x64Either.length, 2);

    var multiOsAxis = sbDeriveSelectedConfigsFromAxisSelections({ os: ["windows", "linux"], arch: ["x64"] }, configAxisValues, allConfigs);
    assert("os=[windows,linux] (both values) AND arch=x64 -> OR within the os axis matches both", multiOsAxis.length, 2);

    // A config with no axis tags at all (absent from configAxisValues) can't
    // be excluded by an axis it doesn't participate in -- it always matches.
    var mixedConfigs = allConfigs.concat(["legacy-untagged"]);
    var untaggedAlwaysMatches = sbDeriveSelectedConfigsFromAxisSelections({ os: ["windows"], arch: [] }, configAxisValues, mixedConfigs);
    assert("untagged config always matches regardless of axis restriction", sbContains(untaggedAlwaysMatches, "legacy-untagged"), true);

    WScript.Echo(passed + " passed, " + failed + " failed");
    if (failed > 0) {
        WScript.Quit(1);
    }
}

sbTestRun();
