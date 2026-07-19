var currentSelection = null;
var currentResult = null;
var useSolutionExplorer = /*USE_SOLUTION_EXPLORER*/true/*USE_SOLUTION_EXPLORER*/;
// Off by default -- most sites generated with this tool aren't Microsoft's own code, so the
// .NET/Microsoft logo marks in the header are opt-in via the HtmlGenerator /showBranding flag
// rather than something every consumer has to remember to hide. The "Source Browser" home link
// itself is unaffected either way.
var showBranding = /*SHOW_BRANDING*/false/*SHOW_BRANDING*/;
var anchorSplitChar = ",";

var externalUrlMap = [
    /*EXTERNAL_URL_MAP*/"https://referencesource.microsoft.com/", "http://sourceroslyn.io/"/*EXTERNAL_URL_MAP*/
];

var supportedFileExtensions = [
    "cs",
    "vb",
    "ts",
    "csproj",
    "vbproj",
    "targets",
    "props",
    "xaml",
    "xml",
    "resx"
];

function redirectLocation(frame, newLocation) {
    if (frame.location.href == newLocation) {
        return;
    }

    frame.location.replace(newLocation);
}

function setHash(newHash) {
    if (newHash.charAt(0) == '#') {
        newHash = newHash.slice(1);
    }

    top.history.replaceState(null, top.document.title, '#' + newHash);
}

function onHashChanged(e) {
    processHash();
}

function populateSearchBox(text) {
    ensureSearchBox();
    searchBox.focus();
    searchBox.value = text;
    searchBox.onkeyup();
}

function processHash() {
    var anchor = document.location.hash;
    if (anchor) {
        anchor = anchor.slice(1);

        if (!anchor) {
            if (top.location.pathname != "/") {
                redirectLocation(top, "/");
            }

            return;
        }

        if (startsWithIgnoreCase(anchor, "MSBuildProperty=")) {
            redirectLocation(n, "/MSBuildProperties/R/" + anchor.slice("MSBuildProperty=".length) + ".html");
            return;
        }

        if (startsWithIgnoreCase(anchor, "MSBuildItem=")) {
            redirectLocation(n, "/MSBuildItems/R/" + anchor.slice("MSBuildItem=".length) + ".html");
            return;
        }

        if (startsWithIgnoreCase(anchor, "MSBuildTarget=")) {
            redirectLocation(n, "/MSBuildTargets/R/" + anchor.slice("MSBuildTarget=".length) + ".html");
            return;
        }

        if (startsWithIgnoreCase(anchor, "MSBuildTask=")) {
            redirectLocation(n, "/MSBuildTasks/R/" + anchor.slice("MSBuildTask=".length) + ".html");
            return;
        }

        if (startsWithIgnoreCase(anchor, "EmptyArrayAllocation")) {
            redirectLocation(n, "/mscorlib/R/EmptyArrayAllocation.html");
            return;
        }

        if (startsWith(anchor, "q=")) {
            anchor = anchor.slice(2);
            try {
                anchor = decodeURIComponent(anchor);
            } catch (e) {
                // Leave the raw value in place if the hash isn't valid percent-encoding.
            }
            populateSearchBox(anchor);
            return;
        }

        anchor = decodeComma(anchor);

        var hashParts = anchor.split(anchorSplitChar);
        if (anchor.indexOf(anchorSplitChar) == -1 && anchor.indexOf("#") > -1) {
            // keep old URLs working for compat
            hashParts = anchor.split("#");
        }

        var potentialFile = anchor;
        var entireAnchorIsFile = true;
        var specialAnchorType = "";
        var hashOrLine = "";
        if (hashParts.length > 1) {
            var lastPart = hashParts[hashParts.length - 1];
            if (lastPart == "references" || lastPart == "namespaces") {
                specialAnchorType = hashParts.pop();
                entireAnchorIsFile = false;
            }
            lastPart = hashParts[hashParts.length - 1];
            var lineNumberRegex = new RegExp("^\\d+$");
            var hashRegex = new RegExp("^[0-9a-f]{16}$")
            if (lineNumberRegex.test(lastPart) || hashRegex.test(lastPart)) {
                hashOrLine = hashParts.pop();
                entireAnchorIsFile = false;
            }
            potentialFile = hashParts.join(anchorSplitChar);
        }

        potentialFile = decodeURIComponent(potentialFile);

        if (potentialFile.charAt(0) == "/") {
            potentialFile = potentialFile.slice(1);
        }

        if (potentialFile.charAt(potentialFile.length - 1) == "/") {
            potentialFile = potentialFile.slice(0, potentialFile.length - 1);
        }

        if (isFile(potentialFile)) {
            var fileUrl = potentialFile;

            if (!endsWithIgnoreCase(fileUrl, ".html")) {
                fileUrl = fileUrl + ".html";
            }

            if (hashOrLine) {
                fileUrl = fileUrl + "#" + createSafeLineNumber(hashOrLine);
            }

            redirectLocation(s, "/" + fileUrl);

            var pathParts = potentialFile.split("/");
            if (pathParts.length > 1) {
                if (specialAnchorType == "references") {
                    redirectLocation(n, "/" + pathParts[0] + "/R/" + hashOrLine + ".html");
                }
                else {
                    if (pathParts[0] != "MSBuildFiles" && pathParts[0] != "TypeScriptFiles") {
                        redirectLocation(n, "/" + pathParts[0] + "/ProjectExplorer.html");
                    }
                }
            }
        } else if (entireAnchorIsFile && potentialFile.indexOf("/") == -1) {
            redirectLocation(n, "/" + potentialFile + "/ProjectExplorer.html");
        } else if (specialAnchorType == "namespaces" && potentialFile.indexOf("/") == -1) {
            redirectLocation(n, "/" + potentialFile + "/namespaces.html");
        }
    } else if (useSolutionExplorer) {
        redirectLocation(n, "SolutionExplorer.html");
    }
}

function onPageLoaded() {
    if (navigator.appVersion.indexOf("MSIE") == -1) {
        document.getElementById("s").frameBorder = "1";
    }

    window.onhashchange = onHashChanged;

    top.name = "topFrame";

    initPaneResizer();

    var query = document.location.search;
    if (query && query.slice(0, 3) == "?q=") {
        redirectLocation(top, "/#" + query.slice(1));
        return;
    }

    var pathname = document.location.pathname;
    if (pathname.toLowerCase().slice(0, 11) == "/index.html") {
        redirectLocation(top, "/");
        return;
    }

    if (pathname.length > 1) {
        setHash(pathname.slice(1) + location.hash);
        redirectLocation(top, "/");
        return;
    }

    processHash();
}

// Makes the divider between the side-by-side nav and content panes draggable (desktop layout
// only). The nav pane width is persisted on `top` and in localStorage so it survives frame
// reloads and return visits. The mobile single-pane layout has no splitter (it's display:none)
// and must not inherit a fixed pixel width, so the width is cleared whenever that layout is
// active and reapplied on return to the side-by-side layout.
function initPaneResizer() {
    var resizer = document.getElementById("paneResizer");
    var navFrame = document.getElementById("n");
    var container = document.getElementById("splitter");
    if (!resizer || !navFrame || !container) {
        return;
    }

    var MIN_WIDTH = 200;
    var mobile = window.matchMedia(
        "(max-width: 700px), (orientation: landscape) and (max-height: 500px) and (pointer: coarse)");

    function storedWidth() {
        var value = parseInt(localStorage.getItem("navPaneWidth"), 10);
        return isNaN(value) ? 0 : value;
    }

    function clamp(width) {
        var max = container.clientWidth - MIN_WIDTH;
        if (max < MIN_WIDTH) {
            max = MIN_WIDTH;
        }
        return Math.min(Math.max(width, MIN_WIDTH), max);
    }

    // In the mobile layout the panes stack full-width; a leftover pixel width would override
    // that, so drop it there and restore the saved width when back to side-by-side.
    function applyLayout() {
        if (mobile.matches) {
            navFrame.style.width = "";
        } else if (storedWidth()) {
            navFrame.style.width = clamp(storedWidth()) + "px";
        }
    }

    resizer.addEventListener("pointerdown", function (e) {
        if (mobile.matches) {
            return;
        }

        e.preventDefault();
        document.body.classList.add("resizingPanes");

        // Track the move/up on the document rather than the 6px resizer: once the drag starts the
        // cursor leaves the handle, and the .resizingPanes rule disables the iframes' pointer
        // events so these still reach us here.
        function onMove(ev) {
            var width = clamp(ev.clientX - container.getBoundingClientRect().left);
            navFrame.style.width = width + "px";
        }

        function onUp() {
            document.body.classList.remove("resizingPanes");
            document.removeEventListener("pointermove", onMove);
            document.removeEventListener("pointerup", onUp);
            localStorage.setItem("navPaneWidth", parseInt(navFrame.style.width, 10));
        }

        document.addEventListener("pointermove", onMove);
        document.addEventListener("pointerup", onUp);
    });

    if (mobile.addEventListener) {
        mobile.addEventListener("change", applyLayout);
    } else if (mobile.addListener) {
        mobile.addListener(applyLayout);
    }

    applyLayout();
}

function onHeaderLoad() {
    ensureSearchBox();
    searchTimerID = -1;
    lastQuery = null;
    lastSearchString = searchBox.value;

    // Place the cursor at the end of the search box by default
    searchBox.focus();

    if (!showBranding) {
        var dotnetLogo = document.getElementById("dotnetLogo");
        if (dotnetLogo) {
            dotnetLogo.classList.add("brandingHidden");
        }

        var logoText = document.getElementById("logoText");
        if (logoText) {
            logoText.classList.add("brandingHidden");
        }
    }

    // Reflect the current pane state on the narrow-screen toggle button.
    updatePaneToggleLabel(top.document.body
        && top.document.body.classList
        && top.document.body.classList.contains("showNav"));

    // Landscape phones use the single-pane layout but keep the desktop-width header,
    // so reveal the pane toggle in the header for them (CSS can't detect this here).
    updateLandscapePhonePane();

    searchBox.onkeyup = function () {
        if (this.value != lastSearchString || (event && event.keyCode == 13)) {
            lastSearchString = this.value;
            if (!top.n.document.getElementById("symbols")) {
                top.n.location = "results.html";
                setTimeout(onSearchChange, 50);
            }

            onSearchChange();
        }
    };

}

function onResultsLoad() {
    ensureSearchBox();
    initRepoFilter(function () { runSearch(); });

    if (searchBox && searchBox.value && searchBox.value.length > 2) {
        runSearch();
    }
}

// init document (called in body onload)
function i(lineNumberCount) {
    if (isTopFrame()) {
        redirectToIndex();
        return;
    }

    // Programmatic navigation into the content pane (e.g. a deep link / hash
    // load, where there's no tap to catch) should also switch to it on narrow
    // screens. Taps on content links are handled by switchToContentPaneOnTap,
    // which runs in the still-visible nav frame -- iOS Safari defers an
    // iframe's onload while it is display:none, so we can't rely on this alone.
    setMobilePane(false);

    var isLargeFile = lineNumberCount > 30000;

    setPageTitle(document.title);

    if (!isLargeFile) {
        generateLineNumbers("ln", lineNumberCount);
        initializeHighlightReferences();

        if (top.symbolIdToHighlight) {
            highlightOccurrence(top.lineNumberToHighlight, top.symbolIdToHighlight);
            top.symbolIdToHighlight = null;
            top.lineNumberToHighlight = null;
        }

        addToolbar();

        rewriteExternalLinks();
        trackActiveItemInSolutionExplorer();

        var projectPathLink = document.getElementById("projectPath");
        if (projectPathLink) {
            projectPathLink.onclick = function () {
                var assemblyName = getAssemblyName();
                top.n.location = "/" + assemblyName + "/ProjectExplorer.html";
                setHash(assemblyName);
                // Match showDocumentOutline/showNamespaceExplorer: on narrow
                // screens switch to the nav pane so the explorer is visible.
                setMobilePane(true);
                return false;
            }
        }
    }

    updateTopHashFromRightPane();

    var element = top.s.document.getElementById(top.s.location.hash.slice(1));
    if (element && !isLargeFile) { // for some reason focusing here for a large file hangs IE
        element.focus();
    }

    sbApplyConfigFilter(document);
    sbMountConfigSelectorIntoContentPage();
}

function updateTopHashFromRightPane() {
    var filePath = top.s.location.pathname.slice(1);
    filePath = getDisplayableFileName(filePath);

    var newHash = filePath;

    var oldRightPaneHash = top.s.location.hash;
    if (oldRightPaneHash) {
        var newRightPaneHash = createSafeLineNumber(oldRightPaneHash);
        if (newRightPaneHash != oldRightPaneHash) {
            top.s.location.hash = newRightPaneHash;
        }

        newHash = newHash + anchorSplitChar + getDisplayableLineNumber(newRightPaneHash).slice(1);
    }

    setHash(newHash);
}

// init xml document (called in body onload)
function ix(lineNumberCount) {
    if (isTopFrame()) {
        redirectToIndex();
        return;
    }

    // See `i`: switch to the content pane for programmatic loads of an XML
    // document; taps are handled by switchToContentPaneOnTap.
    setMobilePane(false);

    var isLargeFile = lineNumberCount > 30000;

    setPageTitle(document.title);

    if (!isLargeFile) {
        generateLineNumbers("ln", lineNumberCount);
        initializeHighlightReferences();

        if (top.symbolIdToHighlight) {
            highlightOccurrence(top.lineNumberToHighlight, top.symbolIdToHighlight);
            top.symbolIdToHighlight = null;
            top.lineNumberToHighlight = null;
        }

        trackActiveItemInSolutionExplorer();
    }

    updateTopHashFromRightPane();

    var element = top.s.document.getElementById(top.s.location.hash.slice(1));
    if (element && !isLargeFile) { // for some reason focusing here for a large file hangs IE
        element.focus();
    }

    sbApplyConfigFilter(document);
    sbMountConfigSelectorIntoContentPage();
}

function rewriteExternalLinks() {
    var links = document.links;
    var length = links.length;
    for (var i = 0; i < length; i++) {
        var link = links[i];
        rewriteExternalLink(link);
    }
}

function rewriteExternalLink(link) {
    var url = link.getAttribute("href");

    var firstIndex = url.indexOf("@");
    if (firstIndex > -1) {
        var indexLength = url.indexOf("@", firstIndex + 1);
        var externalIndexNumber = url.slice(firstIndex + 1, indexLength);
        var externalUrl = externalUrlMap[externalIndexNumber];
        url = externalUrl + url.slice(indexLength + 1);
        link.href = url;
        link.target = "_top";
    }

    if (link.hash && link.hash.length == 17) {
        link.onclick = function () {
            var filePath = top.s.location.pathname.slice(1);
            filePath = getDisplayableFileName(filePath);
            filePath = filePath + anchorSplitChar + this.hash.slice(1);
            setHash(filePath);
        };
        return;
    }

    if (endsWith(url, "/0000000000.html")) {
        var filePath = top.s.location.pathname.slice(1);
        filePath = getDisplayableFileName(filePath);
        filePath = "/#" + filePath + anchorSplitChar + link.id;
        link.href = filePath;
        link.onclick = function () {
            redirectLocation(top.n, "/0000000000.html");
            return false;
        };
        return;
    }
}

function rewriteSolutionExplorerLink(link) {
    var url = link.href;
    var fileName = trimFromEnd(url, ".html");
    var extension = getExtension(fileName);
    var pathname = link.pathname;

    var setClassName = null;
    if (isSupportedExtension(extension) && !link.className) {
        setClassName = extension;
    } else {
        rewriteExternalLink(link);
    }

    if (setClassName) {
        link.className = setClassName;
        link.target = "s";
        link.textContent = getFileName(url);
        var assembly = getAssemblyFromExplorerFile(link);
        if (assembly) {
            if (extension != "ts") {
                link.href = assembly + pathname;
            }
        }
    }
}

function getFileName(url) {
    var lastSlash = url.lastIndexOf('/');
    if (lastSlash != -1) {
        url = url.slice(lastSlash + 1);
    }

    url = url.slice(0, url.length - 5);
    url = unescape(url);
    return url;
}

function getAssemblyFromExplorerFile(a) {
    while (a) {
        a = a.parentElement;
        if (a && a.getAttribute("data-assembly")) {
            return a.getAttribute("data-assembly");
        }
    }

    return null;
}

// onload of references file
function ro() {
    if (isTopFrame()) {
        redirectToSymbolReferences();
        return;
    }

    setPageTitle(document.title);

    var path = document.location.pathname;
    var symbolId = path.substring(path.lastIndexOf("/") + 1, path.lastIndexOf("."))
    for (var i = 0; i < document.links.length; i++) {
        var link = document.links[i];
        link.target = "s";
        link.className = "rL";
        link.onclick = function () {
            var actual = top.s.document.location.pathname;
            var expected = this.pathname;
            if (actual == expected || actual.substring(1) == expected) {
                highlightOccurrence(this.hash.substring(1), symbolId);
            } else {
                top.symbolIdToHighlight = symbolId;
                top.lineNumberToHighlight = this.hash.substring(1);
            }
        };
    }

    var displayableFileName = getDisplayableFileName(top.s.location.pathname.slice(1));
    var newHash = displayableFileName + anchorSplitChar + symbolId + anchorSplitChar + "references";
    if (startsWithIgnoreCase(path, "/MSBuildProperties")) {
        newHash = "MSBuildProperty=" + symbolId;
    } else if (startsWithIgnoreCase(path, "/MSBuildItems")) {
        newHash = "MSBuildItem=" + symbolId;
    } else if (startsWithIgnoreCase(path, "/MSBuildTargets")) {
        newHash = "MSBuildTarget=" + symbolId;
    } else if (startsWithIgnoreCase(path, "/MSBuildTasks")) {
        newHash = "MSBuildTask=" + symbolId;
    } else if (startsWithIgnoreCase(path, "/mscorlib/R/EmptyArrayAllocation")) {
        newHash = "EmptyArrayAllocation";
    }

    setHash(newHash);

    var headers = document.querySelectorAll(".rA");
    for (var i = 0; i < headers.length; i++) {
        var header = headers[i];
        header.onclick = function () {
            var collapsible = this.nextSibling;
            if (collapsible.style.display == "none") {
                collapsible.style.display = "block";
                this.classList.remove('collapsed');
            } else {
                collapsible.style.display = "none";
                this.classList.add('collapsed');
            }
        };
    }

    var fileHeaders = document.querySelectorAll(".rN");
    for (var i = 0; i < fileHeaders.length; i++) {
        var fileHeader = fileHeaders[i];
        var fileName = getInnerText(fileHeader);
        var openParen = fileName.lastIndexOf(" (");
        if (openParen !== -1) {
            fileName = fileName.slice(0, openParen);
        }

        var extension = fileName.substring(fileName.length - 2);
        var imageUrl = null;
        if (extension == "cs") {
            imageUrl = "url(../../content/icons/196.png)";
        } else if (extension == "vb") {
            imageUrl = "url(../../content/icons/195.png)";
        }

        if (imageUrl) {
            fileHeader.style.backgroundImage = imageUrl;
        }
    }

    // References just populated the nav pane; on narrow screens switch to it
    // so the results are visible (matches the search-results flow).
    setMobilePane(true);

    sbApplyConfigFilter(document);
}

function onDocumentOutlineLoad() {
    var root = document.getElementById('root');
    root.style.cursor = "pointer";
    var doc = top.s.document;
    var links = doc.querySelectorAll('a');
    for (var i = 0; i < links.length; i++) {
        var link = links[i];
        var dataGlyphText = link.getAttribute('data-glyph');
        if (link && link.id && link.id.length == 16 && dataGlyphText) {
            var a = document.createElement('a');
            a.href = doc.location.pathname + "#" + link.id;
            a.target = "s";

            var dataGlyph = dataGlyphText.split(",");
            var glyph = dataGlyph[0];
            var indent = dataGlyph[1];

            var div = document.createElement('div');
            div.className = "documentOutlineDiv";
            div.style.marginLeft = (32 * indent) + 'px';
            div.style.paddingTop = "2px";
            div.style.paddingBottom = "2px";

            var img = document.createElement('img');
            img.src = '/content/icons/' + glyph + '.png';
            img.style.marginRight = '8px';
            img.style.verticalAlign = 'bottom';
            div.appendChild(img);

            var keywordText = getKeywordsFromGlyph(glyph);
            var keyword = document.createElement('span');
            keyword.className = "k";
            setInnerText(keyword, keywordText);
            keyword.style.marginRight = '6px';
            keyword.style.verticalAlign = 'center';
            //div.appendChild(keyword);

            var name = document.createElement('span');
            var text = link.textContent;

            // append ~ for destructors
            if (link.previousSibling && endsWith(link.previousSibling.textContent, "~")) {
                text = "~" + text;
            }

            setInnerText(name, text);
            name.style.verticalAlign = 'center';
            div.appendChild(name);

            a.appendChild(div);

            root.appendChild(a);
        }
    }
}

function resultClick(sender) {
    if (currentResult) {
        if (currentResult.classList) {
            currentResult.classList.remove("currentResult");
        } else {
            currentResult.className = "resultItem";
        }
    }

    currentResult = sender;
    if (currentResult.classList) {
        currentResult.classList.add("currentResult");
    } else {
        currentResult.className = "resultItem currentResult";
    }
}

// Narrow-screen single-pane support. On wide screens both panes are always
// visible, so the showNav class on the top document's body is inert there.
function setMobilePane(showNav) {
    var topBody = top.document.body;
    if (!topBody || !topBody.classList) {
        return;
    }

    if (showNav) {
        topBody.classList.add("showNav");
    } else {
        topBody.classList.remove("showNav");
    }

    updatePaneToggleLabel(showNav);
}

function updatePaneToggleLabel(showNav) {
    var header = top.h;
    if (!header || !header.document) {
        return;
    }

    var toggle = header.document.getElementById("paneToggle");
    if (toggle) {
        toggle.textContent = showNav ? "Code" : "Results";
    }
}

function toggleMobilePane() {
    var topBody = top.document.body;
    if (!topBody || !topBody.classList) {
        return;
    }

    setMobilePane(!topBody.classList.contains("showNav"));
}

// Landscape phones are wide enough to miss the max-width breakpoints but too short for a
// comfortable side-by-side split, so the top document switches to the single-pane layout
// for them purely in CSS (see the landscape term on the pane breakpoint in styles.css).
// The pane toggle, however, lives in the fixed 58px-tall header iframe whose own viewport
// is always short + landscape, so a CSS media query there can't tell a landscape phone
// from a portrait tablet. Mirror the same device-level query here in JS -- evaluated
// against `top` so it reflects the device viewport regardless of which frame calls in --
// and flag the header body so the .headerBody.landscapePhone rule reveals the toggle.
function updateLandscapePhonePane() {
    var header = top.h;
    if (!header || !header.document || !header.document.body || !top.matchMedia) {
        return;
    }

    var mq = top.landscapePhoneQuery;
    if (!mq) {
        mq = top.matchMedia("(orientation: landscape) and (max-height: 500px) and (pointer: coarse)");
        top.landscapePhoneQuery = mq;

        var relay = function () { updateLandscapePhonePane(); };
        if (mq.addEventListener) {
            mq.addEventListener("change", relay);
        } else if (mq.addListener) {
            // Safari < 14 only exposes the deprecated MediaQueryList.addListener.
            mq.addListener(relay);
        }
    }

    if (mq.matches) {
        header.document.body.classList.add("landscapePhone");
    } else {
        header.document.body.classList.remove("landscapePhone");
    }
}

function isFile(path) {
    if (endsWithIgnoreCase(path, ".html")) {
        path = path.slice(0, path.length - 5);
    }

    var extension = getExtension(path);
    return isSupportedExtension(extension);
}

function ensureSearchBox() {
    if (typeof searchBox === "object" && searchBox != null) {
        return;
    }

    if (typeof h === "object") {
        searchBox = h.document.getElementById("search-box");
    } else if (typeof top.h === "object") {
        searchBox = top.h.document.getElementById("search-box");
    } else {
        searchBox = document.getElementById("search-box");
    }
}

// The repo filter now lives in whichever page's content it actually scopes -- results.html's
// result list or SolutionExplorer.html's tree -- rather than the header, since each of those
// pages is reloaded independently by the nav frame. The selected value is persisted on `top`
// (the one frame that's never reloaded) so picking a repo in one view keeps the other in sync.
function getSelectedRepoFilter() {
    return (typeof top !== "undefined" && top.selectedRepoFilter) ? top.selectedRepoFilter : "";
}

function setSelectedRepoFilter(repo) {
    if (typeof top !== "undefined") {
        top.selectedRepoFilter = repo || "";
    }
}

// Populates the current page's repo filter dropdown (if it has one -- see
// results.html/SolutionExplorer.html) only when the site actually has more than one distinct
// tagged repo -- for a single-repo/untagged site it stays hidden and search/browsing behave
// exactly as they did before repo tagging existed. Once populated, it's embedded directly into
// the page's "note" strip (the "N results found:" bar on results.html, or the static intro note
// on SolutionExplorer.html) so it reads as a filter on that content rather than a separate
// control. onChanged(repo) is invoked whenever the user picks a different repo, so the caller can
// decide what "re-scope this page" means for it (re-run search, re-filter the tree, etc.).
function initRepoFilter(onChanged) {
    var repoFilter = document.getElementById("repo-filter");
    if (!repoFilter) {
        return;
    }

    fetch("api/repos", { method: "GET", headers: { "Accept": "application/json" } })
        .then(function (response) { return response.ok ? response.json() : []; })
        .then(function (repos) {
            if (!repos || repos.length <= 1) {
                return;
            }

            var allOption = document.createElement("option");
            allOption.value = "";
            allOption.textContent = "All repos";
            repoFilter.appendChild(allOption);

            repos.forEach(function (repo) {
                var option = document.createElement("option");
                option.value = repo;
                option.textContent = repo;
                repoFilter.appendChild(option);
            });

            repoFilter.value = getSelectedRepoFilter();
            repoFilter.style.display = "";
            repoFilter.onchange = function () {
                setSelectedRepoFilter(this.value);
                if (typeof onChanged === "function") {
                    onChanged(this.value);
                }
            };

            repoFilterElement = repoFilter;
            embedRepoFilterInNote(document);
        })
        .catch(function () { });
}

// The repo filter <select> is kept alive as a single persistent DOM node (rather than being
// re-created) so its options, wiring, and current value survive being moved. It starts out
// parked right after <body> (see Markup.cs), then gets appended into whichever "note" div is
// currently first in the page -- results.html regenerates that note's content (and the note
// element itself) on every keystroke via loadSearchResults(), so this needs to be re-run each
// time; SolutionExplorer.html's note is static, so this only ever runs once there.
var repoFilterElement = null;

function embedRepoFilterInNote(scope) {
    if (!repoFilterElement) {
        return;
    }

    var note = scope.querySelector(".note");
    if (!note) {
        return;
    }

    note.classList.add("hasFilter");
    note.appendChild(repoFilterElement);
}

// Hides/shows each project's subtree in the merged Solution Explorer (SolutionExplorer.html)
// based on its data-repo attribute (see SolutionFinalizer.GetProjectExplorerText), which is only
// emitted at all when the site has a repo tag. A "repoHidden" CSS class (rather than toggling
// inline display directly) is used so this doesn't fight with the folder expand/collapse logic,
// which also manipulates display on the same elements.
function filterSolutionExplorerByRepo(repo) {
    var nodes = document.querySelectorAll("[data-repo]");
    for (var i = 0; i < nodes.length; i++) {
        var node = nodes[i];
        var hide = !!repo && node.getAttribute("data-repo") !== repo;
        node.classList.toggle("repoHidden", hide);
    }

    // Once scoped to a single repo, that repo's own grouping header (see
    // Program.GetSolutionExplorerGroupingFolder -- only emitted when the site spans more than one
    // repo) is redundant: the user already picked it from the dropdown. Unwrap it -- hide the
    // header label and force its folder open -- so the tree reads the same as an ungrouped site
    // instead of showing an always-selected wrapper around everything. Other repos' headers are
    // left alone; they're already fully hidden by the data-repo pass above.
    var repoTitles = document.querySelectorAll(".repoTitle");
    for (var i = 0; i < repoTitles.length; i++) {
        var title = repoTitles[i];
        var isSelectedRepo = !!repo && title.getAttribute("data-repo") === repo;
        title.classList.toggle("repoUnwrapped", isSelectedRepo);

        if (isSelectedRepo) {
            expandFolderIfNeeded(title.nextElementSibling);
        }
    }
}

function onSearchChange() {
    ensureSearchBox();
    if (searchBox.value.length > 2) {
        if (searchTimerID == -1) {
            searchTimerID = setTimeout(runSearch, 200);
        }
    } else if (searchBox.value.length === 0 && useSolutionExplorer) {
        // Nothing to search for -- go back to the primary Solution Explorer view (the same one
        // shown on first load) instead of leaving an empty/prompt-only results pane behind.
        returnToSolutionExplorer();
    } else {
        loadSearchResults("<div class='note'>Enter at least 3 characters.</div>");
    }
}

// Restores the nav frame to the Solution Explorer and drops any stale "#q=" search hash, so
// clearing the search box gets the user back to where they started.
function returnToSolutionExplorer() {
    if (top.location.hash) {
        top.history.replaceState(null, top.document.title, top.location.pathname + top.location.search);
    }

    if (top.n) {
        redirectLocation(top.n, "SolutionExplorer.html");
    }
}

function runSearch() {
    ensureSearchBox();
    searchTimerID = -1;
    if (typeof lastQuery === "object" && lastQuery !== null) {
        lastQuery.abort();
        lastQuery = null;
    }

    setPageTitle(searchBox.value);

    var query = searchBox.value;
    var selectedRepo = getSelectedRepoFilter();
    if (selectedRepo) {
        query = "repo:" + selectedRepo + " " + query;
    }

    lastQuery = getUrl("api/symbols/?symbol=" + encodeURIComponent(query), loadSearchResults);
}

function getUrl(url, callback) {
    var controller = new AbortController();
    fetch(url, {
        method: "GET",
        headers: { "Accept": "text/html" },
        signal: controller.signal
    })
        .then(function (response) {
            return response.ok ? response.text() : "";
        })
        .then(function (data) {
            if (typeof data === "string" && data.length > 0) {
                callback(data);
            }
        })
        .catch(function () {
            // Ignore aborted or failed requests; the next keystroke reissues the search.
        });
    return controller;
}

function loadSearchResults(data) {
    if (top.n) {
        var container = top.n.document.getElementById("symbols");
        if (!container) {
            container = top.n.document.getElementById("note");
        }

        if (container) {
            container.innerHTML = data;

            // The repo filter select lives in the nav frame's own execution context (it's the
            // one that renders results.html), so re-embed it there, not in this (header) frame's
            // context, now that the note it was sitting in just got replaced wholesale above.
            if (typeof top.n.embedRepoFilterInNote === "function") {
                top.n.embedRepoFilterInNote(container);
            }

            if (searchBox && searchBox.value && searchBox.value.length > 2) {
                setHash("q=" + encodeURIComponent(searchBox.value));
            }

            // On narrow screens surface the results the user is searching for.
            setMobilePane(true);

            if (data && data.length > 40 && data.slice(0, 40) === '<div class="note">Index is being rebuilt') {
                searchTimerID = -1;
                onSearchChange();
            }
        }
    }
}

// this is usually called in the "s" frame
function redirect(map, prefixLength) {
    if (!prefixLength) {
        prefixLength = 16;
    }

    var anchor = document.location.hash;
    if (anchor) {
        anchor = anchor.slice(1);

        anchor = decodeComma(anchor);

        var hashParts = anchor.split(anchorSplitChar);
        var anchorHasReferencesSuffix = false;
        if (hashParts.length > 1 && hashParts[hashParts.length - 1] == "references") {
            anchorHasReferencesSuffix = true;
            hashParts.pop();
        }
        var id = hashParts.join(anchorSplitChar);
        var shortId = id;
        if (prefixLength < shortId.length) {
            shortId = shortId.slice(0, prefixLength);
        }

        // all the keys have their first character trimmed since it's a bucket file aX.html
        // and X is the same for all ids
        shortId = shortId.slice(1);

        var redirectTo = map[shortId];
        if (redirectTo) {
            var destination = redirectTo + ".html" + "#" + createSafeLineNumber(id);
            if (anchorHasReferencesSuffix) {
                destination = destination + anchorSplitChar + "references";
            }

            redirectLocation(document, destination);
        }
    }
}

// multi-staged redirect A.html -> A0.html -> filePath.html (to reduce size of a.html)
function redirectToNextLevelRedirectFile() {
    var anchor = document.location.hash;
    if (anchor) {
        anchor = anchor.slice(1);

        anchor = decodeComma(anchor);

        var hashParts = anchor.split(anchorSplitChar);
        var anchorHasReferencesSuffix = false;
        if (hashParts.length > 1 && hashParts[hashParts.length - 1] == "references") {
            anchorHasReferencesSuffix = true;
            hashParts.pop();
        }
        var id = hashParts.join(anchorSplitChar);

        var destination = "A" + id.slice(0, 1) + ".html" + "#" + createSafeLineNumber(id);
        if (anchorHasReferencesSuffix) {
            destination = destination + anchorSplitChar + "references";
        }

        redirectLocation(document, destination);
    }
}

// this is usually called in the "n" frame
function redirectToReferences() {
    var anchor = document.location.hash;
    if (anchor) {
        var destination = "R/" + anchor + ".html";
        redirectLocation(document, destination);
    }
}

function generateLineNumbers(id, count) {
    if (count == 0) {
        return;
    }

    var filePath = document.location.pathname.slice(1);
    filePath = getDisplayableFileName(filePath);

    var list = [];
    for (var i = 1; i <= count; i++) {
        var line =
            "<a id=\"l" +
            i +
            "\" href=\"" +
            "/#" +
            filePath + anchorSplitChar +
            i +
            "\" target=\"_self\" onclick=\"setHash('" +
            filePath.replace("'", "\\'") + anchorSplitChar + i + "');document.location.hash='l" +
            i +
            "';return false;\">" + i + "</a><br>";
        list.push(line);
    }

    var text = list.join("");

    document.getElementById(id).innerHTML = text;
}

function highlightOccurrence(lineNumber, symbolId) {
    var sourceDocument = top.s.document;
    if (sourceDocument.currentLine) {
        sourceDocument.currentLine.style.background = "transparent";
    }

    var lineNumberId = createSafeLineNumber(lineNumber);
    sourceDocument.location.hash = lineNumberId;

    var lineNumberSpan = sourceDocument.getElementById(lineNumberId);
    lineNumberSpan.style.background = "lime";
    sourceDocument.currentLine = lineNumberSpan;

    // there are two kinds of links in the document page:
    // 1. links to definitions
    // 2. links on line numbers
    // Clear the links which aren't references to the symbol currently referenced
    // and the line numbers which aren't the current line
    for (var i = 0; i < sourceDocument.links.length; i++) {
        var link = sourceDocument.links[i];
        var target = link.hash.substring(1);
        if (target == symbolId) {
            link.style.background = "yellow";
        }
        else if (link !== lineNumberSpan) {
            link.style.background = "transparent";
        }
    }
}

// highlight references
function t(sender) {
    var classname = sender.className;

    var elements;
    if (currentSelection) {
        elements = document.getElementsByClassName(currentSelection);
        for (var i = 0; i < elements.length; i++) {
            elements[i].style.background = "transparent";
        }

        var def = document.getElementById(currentSelection.replace(" r", " rd"));
        if (def) {
            def.style.borderColor = "transparent";
        }

        if (classname == currentSelection) {
            currentSelection = null;
            return;
        }
    }

    currentSelection = classname;

    elements = document.getElementsByClassName(currentSelection);
    for (var i = 0; i < elements.length; i++) {
        elements[i].style.background = "cyan";
    }

    var def = document.getElementById(currentSelection.replace(" r", " rd"));
    if (def) {
        def.style.borderColor = "black";
        def.style.borderStyle = "solid";
        def.style.borderWidth = "1px";
    }
}

function initializeHighlightReferences() {
    elements = document.querySelectorAll(".r");
    for (var i = 0; i < elements.length; i++) {
        elements[i].onclick = function () { t(this); };
    }
}

function addToolbar() {
    var documentOutlineButton = document.createElement('img');
    documentOutlineButton.setAttribute('src', '/content/icons/DocumentOutline.png');
    documentOutlineButton.title = "Document Outline";
    documentOutlineButton.className = 'documentOutlineButton';
    documentOutlineButton.onclick = showDocumentOutline;
    document.body.appendChild(documentOutlineButton);

    var projectExplorerButton = document.createElement('img');
    var projectExplorerIcon = '/content/icons/CSharpProjectExplorer.png';
    if (document.title.slice(document.title.length - 2) == "vb") {
        projectExplorerIcon = '/content/icons/VBProjectExplorer.png';
    }

    projectExplorerButton.setAttribute('src', projectExplorerIcon);
    projectExplorerButton.title = "Project Explorer";
    projectExplorerButton.className = 'projectExplorerButton';
    projectExplorerButton.onclick = function () { document.getElementById('projectPath').click(); };
    document.body.appendChild(projectExplorerButton);

    var namespaceExplorerButton = document.createElement('img');
    namespaceExplorerButton.setAttribute('src', '/content/icons/NamespaceExplorer.png');
    namespaceExplorerButton.title = "Namespace Explorer";
    namespaceExplorerButton.className = 'namespaceExplorerButton';
    namespaceExplorerButton.onclick = showNamespaceExplorer;
    document.body.appendChild(namespaceExplorerButton);
}

function showDocumentOutline() {
    top.n.location = "/documentoutline.html";
    setMobilePane(true);
}

function showNamespaceExplorer() {
    var assemblyName = getAssemblyName();
    var namespacesUrl = "/" + assemblyName + "/namespaces.html";
    top.n.location = namespacesUrl;
    setHash(assemblyName + ",namespaces");
    setMobilePane(true);
}

// Firefox doesn't support innerText, but it supports textContent
// See http://blog.coderlab.us/2005/09/22/using-the-innertext-property-with-firefox/
function setInnerText(element, text) {
    if (typeof element.innerText !== "undefined") {
        element.innerText = text;
    } else {
        element.textContent = text;
    }
}

function getInnerText(element) {
    if (typeof element.innerText !== "undefined") {
        return element.innerText;
    } else {
        return element.textContent;
    }
}

function getKeywordsFromGlyph(glyph) {
    switch (glyph - (glyph % 6)) {
        case 0:
            return "class";
        case 6:
            return "constant";
        case 12:
            return "delegate";
        case 18:
            return "enum";
        case 24:
            return "enum member";
        case 30:
            return "event";
        case 36:
            return "exception";
        case 42:
            return "field";
        case 48:
            return "interface";
        case 54:
            return "macro";
        case 60:
            return "map";
        case 66:
            return "map item";
        case 72:
            return "method";
        case 78:
            return "overload";
        case 84:
            return "module";
        case 90:
            return "namespace";
        case 96:
            return "operator";
        case 102:
            return "property";
        case 108:
            return "struct";
        case 114:
            return "type parameter";
        case 150:
            return "module";
        case 220:
            return "extension method";
        default:
            return "symbol";
    }
}

function trackActiveItemInSolutionExplorer() {
    if (top.n) {
        var doc = top.n.document;
        if (doc) {
            var rootFolderDiv = doc.getElementById('rootFolder');
            if (rootFolderDiv && (rootFolderDiv.className == "projectCS" || rootFolderDiv.className == "projectVB")) {
                rootFolderDiv = rootFolderDiv.nextElementSibling;
                if (rootFolderDiv) {
                    var filePath = getFilePath();
                    if (filePath) {
                        selectItem(rootFolderDiv, filePath.split("\\"));
                    }
                }
            }
        }
    }
}

function selectItem(div, parts) {
    var text = parts[0];
    var found = null;
    for (var i = 0; i < div.children.length; i++) {
        var child = div.children[i];
        if (getInnerText(child) == text) {
            found = child;
            break;
        }
    }

    if (!found) {
        return;
    }

    if (parts.length == 1 && found.tagName == "A") {
        selectFile(found);
    }
    else if (parts.length > 1 && found.tagName == "DIV") {
        found = found.nextElementSibling;
        expandFolderIfNeeded(found);
        selectItem(found, parts.slice(1));
    }
}

function selectFile(a) {
    var selected = top.n.document.selectedFile;
    if (selected === a) {
        return;
    }

    if (selected && selected.classList) {
        selected.classList.remove("selectedFilename");
    }

    top.n.document.selectedFile = a;
    if (a) {
        if (a.classList) {
            a.classList.add("selectedFilename");
        }

        scrollIntoViewIfNeeded(a);
    }
}

function scrollIntoViewIfNeeded(element) {
    var topOfPage = window.pageYOffset || document.documentElement.scrollTop || document.body.scrollTop;
    var heightOfPage = window.innerHeight || document.documentElement.clientHeight || document.body.clientHeight;
    var elY = 0;
    var elH = 0;

    if (document.layers) {
        elY = element.y;
        elH = element.height;
    }
    else {
        for (var p = element; p && p.tagName != 'BODY'; p = p.offsetParent) {
            elY += p.offsetTop;
        }

        elH = element.offsetHeight;
    }

    if ((topOfPage + heightOfPage) < (elY + elH)) {
        element.scrollIntoView(false);
    }
    else if (elY < topOfPage) {
        element.scrollIntoView(true);
    }
}

function expandFolderIfNeeded(folder) {
    if (folder.style.display != "block" && folder && folder.previousSibling && folder.previousSibling.onclick) {
        folder.previousSibling.onclick();
    }
}

function getFilePath() {
    var a = top.s.document.getElementById("filePath");
    if (a) {
        return getInnerText(a);
    }

    return null;
}

function getAssemblyName() {
    var a = top.s.document.getElementById("projectPath");
    if (a) {
        var url = a.hash;
        return url.slice(1);
    }

    return null;
}

// this is called when clicking on the project link, redirecting from project\index.html to project\ProjectExplorer.html
function redirectToIndex() {
    var scriptPath = this.document.scripts[0].src;
    var rootPath = scriptPath.slice(0, scriptPath.length - 10);
    var sourcePath = this.document.location.href;
    var relativePath = sourcePath.slice(rootPath.length);
    var destination = rootPath + "#" + relativePath.replace("#", anchorSplitChar);
    redirectLocation(document, destination);
}

// this is called when the references file (/R/id.html) is loaded in the top frame
function redirectToSymbolReferences() {
    var referencesFilePath = this.document.location.href;
    var destination = referencesFilePath.replace("/R/", "/A.html" + "#");

    // strip off the ".html" suffix
    destination = destination.slice(0, destination.length - 5);
    destination = destination + anchorSplitChar + "references";
    redirectLocation(top, destination);
}

function toggle(header, id) {
    var element = document.getElementById(id);
    if (element.style.display == 'none') {
        header.classList.remove("collapsed");
        element.style.display = 'block';
    }
    else {
        header.classList.add("collapsed");
        element.style.display = 'none';
    }
}

function isTopFrame() {
    return top === self;
}

function initializeProjectIndex(url) {
    if (!isTopFrame()) {
        url = "ProjectExplorer.html";
        redirectLocation(document, url);
    } else {
        redirectLocation(top, url);
    }
}

function initializeProjectExplorer() {
    makeFoldersCollapsible(/* closed folder */"202.png", "201.png", "../content/icons/", initializeSolutionExplorerFolder);
    initializeProjectExplorerRootFolder();
    trackActiveItemInSolutionExplorer();
}

function initializeProjectExplorerRootFolder() {
    var rootFolder = document.getElementById("rootFolder");
    if (rootFolder) {
        rootFolder = rootFolder.nextElementSibling;
        if (rootFolder) {
            initializeSolutionExplorerFolder(rootFolder);
        }
    }
}

function onSolutionExplorerLoad() {
    loadSolutionExplorer();
}

function loadSolutionExplorer() {
    makeFoldersCollapsible(/* closed folder */"202.png", "201.png", "content/icons/", initializeSolutionExplorerFolder);
    document.getElementById("rootFolder").style.display = "block";

    // Apply whatever repo is currently selected (persisted on `top`) so navigating to the
    // Solution Explorer after already picking a repo in search stays scoped the same way, even
    // before the dropdown itself has finished being populated below.
    filterSolutionExplorerByRepo(getSelectedRepoFilter());
    initRepoFilter(function (repo) { filterSolutionExplorerByRepo(repo); });
}

function initializeNamespaceExplorer() {
    // The namespace tree ships as a compact JSON payload (namespaceExplorerData) instead of a
    // multi-MB nested-<div> document. Build DOM for the top level now and defer each subtree until
    // its folder is first expanded, so only the branches the user opens are ever materialized.
    var data = window.namespaceExplorerData;
    if (!data) {
        return;
    }

    var ctx = {
        asm: data.assemblyName,
        icons: data.pathPrefix + "content/icons/"
    };

    var children = data.children || [];
    for (var i = 0; i < children.length; i++) {
        renderNamespaceNode(document.body, children[i], ctx);
    }
}

// A node is a fixed-shape array: [name, [children]] for a namespace, [name, glyph, id] for a leaf
// type, or [name, glyph, id, [children]] for a type with nested types. Element 1 being a number means
// it is a type (that number is the glyph); the last element being an array means it has children.
function renderNamespaceNode(container, node, ctx) {
    var isType = typeof node[1] === "number";
    var children = Array.isArray(node[node.length - 1]) ? node[node.length - 1] : null;

    var titleDiv = document.createElement("div");
    titleDiv.className = children ? "folderTitle" : "typeTitle";

    if (isType) {
        var link = document.createElement("a");
        link.className = "tDN";
        link.href = "/" + ctx.asm + "/A.html#" + node[2];
        link.target = "s";

        var typeIcon = document.createElement("img");
        typeIcon.className = "tDNI";
        typeIcon.src = ctx.icons + node[1] + ".png";

        link.appendChild(typeIcon);
        link.appendChild(document.createTextNode(node[0]));
        titleDiv.appendChild(link);
    } else {
        titleDiv.appendChild(document.createTextNode(node[0]));
    }

    container.appendChild(titleDiv);

    if (children) {
        var folderDiv = document.createElement("div");
        folderDiv.className = "folder";
        folderDiv.style.display = "none";
        container.appendChild(folderDiv);
        addNamespaceFolderToggle(titleDiv, folderDiv, children, ctx);
    }
}

// Mirrors addImagesToFolder for the namespace tree: a plain namespace gets a folder icon plus a +/-
// toggle and the whole row toggles; a type-with-children keeps its own icon/link and only the +/-
// toggles so the link stays clickable. Children are rendered on first expand.
function addNamespaceFolderToggle(titleDiv, folderDiv, children, ctx) {
    var firstChild = titleDiv.firstChild;
    var isTypeFolder = isLink(firstChild);

    var plusMinus = document.createElement("img");
    plusMinus.src = ctx.icons + "plus.png";
    plusMinus.className = "imagePlusMinus";

    if (isTypeFolder) {
        titleDiv.insertBefore(plusMinus, firstChild);
    } else {
        var folderImage = document.createElement("img");
        folderImage.src = ctx.icons + "90.png";
        folderImage.className = "imageFolder";
        titleDiv.insertBefore(folderImage, firstChild);
        titleDiv.insertBefore(plusMinus, folderImage);
    }

    var built = false;
    var handler = function () {
        if (folderDiv.style.display === "none") {
            plusMinus.src = ctx.icons + "minus.png";
            if (!built) {
                for (var i = 0; i < children.length; i++) {
                    renderNamespaceNode(folderDiv, children[i], ctx);
                }
                built = true;
            }
            folderDiv.style.display = "block";
        } else {
            plusMinus.src = ctx.icons + "plus.png";
            folderDiv.style.display = "none";
        }
    };

    if (isTypeFolder) {
        plusMinus.onclick = handler;
    } else {
        titleDiv.onclick = handler;
    }
}

function initializeSolutionExplorerFolder(folder) {
    for (var i = 0; i < folder.children.length; i++) {
        var child = folder.children[i];
        if (isLink(child)) {
            rewriteSolutionExplorerLink(child);
        }
    }
}

function makeFoldersCollapsible(folderIcon, openFolderIcon, pathToIcons, initializeHandler) {
    var elements = document.querySelectorAll(".folder");
    var length = elements.length;
    for (var i = 0; i < length; i++) {
        var folder = elements[i];
        folder.style.display = 'none';
        folder.initialize = initializeHandler;
        if (folder.parentNode.id === 'rootFolder'
         || folder.parentNode.previousSibling.id === 'rootFolder'
         || folder.parentNode.className === 'namespaceExplorerBody') {
            addImagesToFolder(folder, folderIcon, openFolderIcon, pathToIcons);
        }
    }
}

function addImagesToFolder(folder, folderIcon, openFolderIcon, pathToIcons) {
    var div = folder.previousSibling;
    var firstChild = div.firstChild;

    var imagePlusMinus = document.createElement("img");
    imagePlusMinus.src = pathToIcons + "plus.png";
    imagePlusMinus.className = "imagePlusMinus";

    var imageFolder = document.createElement("img");
    imageFolder.src = pathToIcons + folderIcon;
    imageFolder.className = "imageFolder";
    setFolderImage(imageFolder, div, firstChild, pathToIcons, folderIcon);

    var handler = expandCollapseFolder(folder, imagePlusMinus, imageFolder, div, firstChild, pathToIcons, folderIcon, openFolderIcon);

    var skipImage = isLink(firstChild);
    if (skipImage) {
        div.insertBefore(imagePlusMinus, firstChild);
        imagePlusMinus.onclick = handler;
    } else {
        div.insertBefore(imageFolder, firstChild);
        div.insertBefore(imagePlusMinus, imageFolder);
        div.onclick = handler;
    }
}

function isLink(element) {
    return element && element.tagName && element.tagName == "A";
}

function expandCollapseFolder(capturedFolder, capturedPlusMinus, capturedFolderImage, capturedDiv, capturedFirstChild, pathToIcons, folderIcon, openFolderIcon) {
    return function () {
        if (capturedFolder.style.display == 'none') {
            capturedPlusMinus.src = pathToIcons + "minus.png";
            if (capturedDiv.className != "projectCSInSolution" && capturedDiv.className != "projectVBInSolution") {
                capturedFolderImage.src = pathToIcons + openFolderIcon;
            }

            if (capturedFolder.initialize) {
                capturedFolder.initialize(capturedFolder);
                capturedFolder.initialize = null;
            }

            if (!capturedFolder.everExpanded) {
                for (var i = 0; i < capturedFolder.children.length; i++) {
                    if (capturedFolder.children[i].className === 'folder') {
                        addImagesToFolder(capturedFolder.children[i], folderIcon, openFolderIcon, pathToIcons);
                    }
                }
            }

            capturedFolder.everExpanded = true;
            capturedFolder.style.display = 'block';
        }
        else {
            capturedPlusMinus.src = pathToIcons + "plus.png";
            setFolderImage(capturedFolderImage, capturedDiv, capturedFirstChild, pathToIcons, folderIcon);
            capturedFolder.style.display = 'none';
        }
    }
}

function setFolderImage(folder, div, firstChild, pathToIcons, folderIcon) {
    var text = firstChild.textContent;
    if (text === 'References' || text === "Used By") {
        folder.src = pathToIcons + "192.png";
    } else if (text === 'Properties') {
        folder.src = pathToIcons + "102.png";
    } else if (text === 'Generated') {
        folder.src = pathToIcons + "generated.png";
    } else if (div.className == "projectCSInSolution") {
        folder.src = pathToIcons + "196.png";
    } else if (div.className == "projectVBInSolution") {
        folder.src = pathToIcons + "195.png";
    }
    else {
        folder.src = pathToIcons + folderIcon;
    }
}

function setPageTitle(title) {
    if (!title) {
        title = "Source Browser";
    }

    if (top && top.document) {
        top.document.title = title;
    }
}

function decodeComma(text) {
    text = text.replace("%2c", ",");
    text = text.replace("%2C", ",");
    return text;
}

function getDisplayableLineNumber(text) {
    if (text == "#") {
        return "";
    }

    if (text.slice(0, 2) == "#l") {
        text = anchorSplitChar + text.slice(2);
    }

    return text;
}

function getDisplayableFileName(text) {
    if (endsWithIgnoreCase(text, ".html")) {
        text = text.slice(0, text.length - 5);
    }

    text = encodeURIComponent(text);
    while (text.indexOf("%2F") > -1) {
        // don't escape slashes since they actually look nice in the URL unescaped
        text = text.replace("%2F", "/");
    }

    return text;
}

function createSafeLineNumber(text) {
    if (isNumber(text) && text.length != 16) {
        text = "l" + text;
    }

    return text;
}

function isNumber(n) {
    return !isNaN(parseFloat(n)) && isFinite(n);
}

function startsWith(text, prefix) {
    if (!text || !prefix) {
        return false;
    }

    if (prefix.length > text.length) {
        return false;
    }

    var slice = text.slice(0, prefix.length);
    return slice == prefix;
}

function startsWithIgnoreCase(text, prefix) {
    if (!text || !prefix) {
        return false;
    }

    if (prefix.length > text.length) {
        return false;
    }

    var slice = text.slice(0, prefix.length);
    return slice.toLowerCase() == prefix.toLowerCase();
}

function endsWith(text, suffix) {
    if (!text || !suffix) {
        return false;
    }

    if (suffix.length > text.length) {
        return false;
    }

    var slice = text.slice(text.length - suffix.length, text.length);
    return slice == suffix;
}

function endsWithIgnoreCase(text, suffix) {
    if (!text || !suffix) {
        return false;
    }

    if (suffix.length > text.length) {
        return false;
    }

    var slice = text.slice(text.length - suffix.length, text.length);
    return slice && (slice.toLowerCase() == suffix.toLowerCase());
}

function trimFromEnd(text, suffixToTrim) {
    if (!text || !suffixToTrim) {
        return text;
    }

    if (endsWithIgnoreCase(text, suffixToTrim)) {
        text = text.slice(0, text.length - suffixToTrim.length);
    }

    return text;
}

// ConfigFileDeduper (Pass2) disambiguates divergently-rendered config variants by inserting
// "~" + an 8-hex-digit content hash immediately before the file's extension (e.g.
// "EnvHelper.cs~87f21542.html" on disk, linked from the client as ".../EnvHelper.cs~87f21542").
// Strip that suffix before computing the extension so variant URLs are still recognized as
// files by isFile()/processHash() -- otherwise the trailing hash gets swallowed into a bogus
// "cs~87f21542" pseudo-extension and the file-redirect branch below is skipped entirely.
var configVariantSuffixRegex = /~[0-9a-f]{8}$/i;

function getExtension(filePath) {
    if (!filePath) {
        return "";
    }

    filePath = filePath.replace(configVariantSuffixRegex, "");

    var dot = filePath.lastIndexOf(".");
    if (dot == filePath.length - 1) {
        return "";
    }

    return filePath.slice(dot + 1).toLowerCase();
}

function isSupportedExtension(extension) {
    return supportedFileExtensions.indexOf(extension) != -1;
}

// The File/Project footer (`.dH`) is `position: fixed` and grows as the paths
// wrap, so a hard-coded bottom reserve on the code container (`.cz`) either
// wastes space or lets the footer cover the code area's horizontal scrollbar.
// Reserve exactly the footer's rendered height instead, and re-sync on resize
// (e.g. device rotation) since wrapping - and therefore the height - changes.
function syncFooterReserve() {
    var footer = document.querySelector(".dH");
    var container = document.querySelector(".cz");
    if (!footer || !container) {
        return;
    }

    container.style.marginBottom = (footer.offsetHeight + 8) + "px";
}

window.addEventListener("load", syncFooterReserve);
window.addEventListener("resize", syncFooterReserve);

// Switch to the content pane on narrow screens when the user taps a link that
// loads it (`target="s"`) -- a search result, a solution/project explorer file,
// or a reference. We handle this from the tap rather than the content frame's
// onload because the tap happens in the nav frame, which is still visible;
// iOS Safari defers an iframe's onload while it is `display: none`, so the
// content frame can't reliably un-hide itself from its own load handler.
function switchToContentPaneOnTap(event) {
    var node = event.target;
    while (node && node.nodeType === 1) {
        if (node.tagName === "A") {
            if (node.target === "s") {
                setMobilePane(false);
            }
            return;
        }
        node = node.parentNode;
    }
}

document.addEventListener("click", switchToContentPaneOnTap, true);

// ----------------------------------------------------------------------------
// Config selector (#104).
//
// The selector panel is mounted INSIDE the content page itself -- directly
// under the file's "dH" header block, as part of the code view -- rather than
// as a persistent row spanning the whole index.html page above both panes.
// See sbMountConfigSelectorIntoContentPage below, called from i()/ix() on
// every content-page load. This has been validated live in a real browser
// (pill toggling, #if-region grey/highlight switching, and the multi-config
// repo demo all confirmed working end-to-end), not just via the standalone
// pure-function test script (src/HtmlGenerator.Tests/ClientScriptTests/configSelectorFilter.tests.js
// -- see the header comment there for how to run it, and for exactly which
// functions it covers).
//
// Selection is a FILTER, not a subtree switch: elements tagged data-configs
// that don't overlap the current selection are greyed (not removed), and an
// empty/no selection shows everything (the union) -- byte-identical in
// behavior to a site built without configs when only one config exists,
// since such a site never has configs.json and sbMountConfigSelectorIntoContentPage no-ops.
// ----------------------------------------------------------------------------

var sbConfigSelectorStorageKey = "sourceBrowserSelectedConfigs";

// Pure decision function: does an element tagged with data-configs="..." (or
// untagged) count as "shown" for the given selection? Kept dependency-free
// (no DOM access) so it can be executed and asserted against outside a
// browser -- see configSelectorFilter.tests.js.
function sbConfigFilterMatches(selectedConfigs, dataConfigsAttr) {
    // Untagged elements are shared/inert across every config -- always shown.
    if (!dataConfigsAttr) {
        return true;
    }

    // No selection (including a selector that hasn't loaded/initialized yet)
    // shows everything -- the union, matching a no-config build.
    if (!selectedConfigs || selectedConfigs.length === 0) {
        return true;
    }

    var elementConfigs = sbParseConfigList(dataConfigsAttr);
    for (var i = 0; i < elementConfigs.length; i++) {
        for (var j = 0; j < selectedConfigs.length; j++) {
            if (elementConfigs[i] === selectedConfigs[j]) {
                return true;
            }
        }
    }

    return false;
}

// Splits a "a, b" data-configs attribute value into a trimmed, lower-cased
// array (["a","b"]). Comparisons throughout this feature are case-insensitive
// to match the server side's StringComparer.OrdinalIgnoreCase.
function sbParseConfigList(raw) {
    if (!raw) {
        return [];
    }

    var parts = raw.split(",");
    var result = [];
    for (var i = 0; i < parts.length; i++) {
        var trimmed = parts[i].replace(/^\s+|\s+$/g, "").toLowerCase();
        if (trimmed.length > 0) {
            result.push(trimmed);
        }
    }

    return result;
}

// Pure decision function for the multi-axis panel: given a per-axis selection
// map (axisName -> array of selected values; a missing/empty array means "no
// restriction on this axis"), the full configName -> {axisName: value} map
// (configs.json's "configAxisValues"), and the full list of registered config
// names, returns the flat list of config names that match EVERY axis
// restriction (AND across axes; an axis with several selected values is an OR
// within that axis -- standard faceted-filter semantics). A config with no
// axis tags of its own (absent from configAxisValues, e.g. a mixed site with
// some untagged configs) always matches -- it can't be excluded by an axis it
// doesn't participate in. Kept dependency-free (no DOM access) so it can be
// executed and asserted against outside a browser -- see
// configSelectorFilter.tests.js.
function sbDeriveSelectedConfigsFromAxisSelections(axisSelections, configAxisValues, allConfigNames) {
    var result = [];
    for (var i = 0; i < allConfigNames.length; i++) {
        var configName = allConfigNames[i];
        var tags = configAxisValues ? configAxisValues[configName] : null;
        if (!tags) {
            result.push(configName);
            continue;
        }

        var matchesAllAxes = true;
        for (var axisName in axisSelections) {
            if (!Object.prototype.hasOwnProperty.call(axisSelections, axisName)) {
                continue;
            }

            var selectedValues = axisSelections[axisName];
            if (!selectedValues || selectedValues.length === 0) {
                continue; // No restriction on this axis.
            }

            var tagValue = tags[axisName];
            if (!tagValue || !sbContains(selectedValues, tagValue)) {
                matchesAllAxes = false;
                break;
            }
        }

        if (matchesAllAxes) {
            result.push(configName);
        }
    }

    return result;
}

function sbContains(array, value) {
    for (var i = 0; i < array.length; i++) {
        if (array[i] === value) {
            return true;
        }
    }

    return false;
}

function sbGetSelectedConfigs() {
    try {
        var raw = window.sessionStorage.getItem(sbConfigSelectorStorageKey);
        return raw ? sbParseConfigList(raw) : [];
    } catch (e) {
        // sessionStorage can throw in some sandboxed/embedded contexts; treat
        // as "no selection" (show everything) rather than fail the page.
        return [];
    }
}

function sbSetSelectedConfigs(selectedConfigs) {
    try {
        window.sessionStorage.setItem(sbConfigSelectorStorageKey, selectedConfigs.join(","));
    } catch (e) {
        // Best-effort persistence only; filtering still works for the
        // lifetime of the current page even if this throws.
    }
}

// DOM-application half: greys/ungreys every data-configs-tagged element under
// `root` (a Document or element) according to the current selection. Safe to
// call on any page, tagged or not -- pages with no [data-configs] elements at
// all (the overwhelming majority when 0/1 configs are registered) simply
// find nothing to iterate.
function sbApplyConfigFilter(root) {
    if (!root || !root.querySelectorAll) {
        return;
    }

    var selectedConfigs = sbGetSelectedConfigs();
    var elements = root.querySelectorAll("[data-configs]");
    for (var i = 0; i < elements.length; i++) {
        var element = elements[i];
        var matches = sbConfigFilterMatches(selectedConfigs, element.getAttribute("data-configs"));
        if (matches) {
            element.classList.remove("configFilteredOut");
        } else {
            element.classList.add("configFilteredOut");
        }
    }
}

// Re-applies the current selection to every frame that might already have
// content loaded (called after the user changes the selection in the header).
// Each frame also re-applies on its own subsequent loads via sbApplyConfigFilter
// calls already wired into i()/ix()/ro(), so a freshly-navigated frame is
// correct even without this broadcast.
function sbReapplyConfigFilterToAllFrames() {
    try {
        if (top.s && top.s.document) {
            sbApplyConfigFilter(top.s.document);
        }
    } catch (e) { }
    try {
        if (top.n && top.n.document) {
            sbApplyConfigFilter(top.n.document);
        }
    } catch (e) { }
}

// Mounts the config panel INSIDE the content page itself, directly under its
// "dH" file-header block, so the selector reads as part of the code view
// instead of a separate row/banner. Called from i()/ix() on every
// content-page load (a fresh mount each time, since each navigation is a full
// frame reload -- there's no cross-navigation DOM state to carry beyond
// what's already in sessionStorage). No-ops entirely when: the page has no
// "dH" header (not a real source/content page), the page has nothing tagged
// data-configs at all (nothing to select between for THIS page), or
// configs.json is missing/has fewer than 2 configs -- i.e. every 0/1-config
// site, which is the overwhelming majority of real usage -- so those sites
// see zero visual or behavioral change from this feature existing.
var sbConfigSelectorData = null; // { configs, axes, configAxisValues } from configs.json.

function sbMountConfigSelectorIntoContentPage() {
    var anchor = document.querySelector(".dH");
    if (!anchor || !document.querySelector("[data-configs]")) {
        return;
    }

    var request = new XMLHttpRequest();
    request.open("GET", "/configs.json", true);
    request.onload = function () {
        if (request.status !== 200 || !request.responseText) {
            return;
        }

        var data;
        try {
            data = JSON.parse(request.responseText);
        } catch (e) {
            return;
        }

        if (!data || !data.configs || data.configs.length < 2) {
            // Fewer than 2 registered configs -- nothing to select between.
            return;
        }

        sbConfigSelectorData = data;

        var container = document.getElementById("configSelectorContainer");
        if (!container) {
            container = document.createElement("div");
            container.id = "configSelectorContainer";
            anchor.parentNode.insertBefore(container, anchor.nextSibling);
        }

        sbRenderConfigSelectorUI(container, data);
    };
    // A missing configs.json (single/no-config site) is the common case and
    // simply leaves nothing mounted -- no error handling needed beyond
    // onload checking request.status.
    request.send();
}

var sbConfigPanelCollapsedStorageKey = "sourceBrowserConfigPanelCollapsed";

function sbIsConfigPanelCollapsed() {
    try {
        return window.sessionStorage.getItem(sbConfigPanelCollapsedStorageKey) === "1";
    } catch (e) {
        return false;
    }
}

function sbSetConfigPanelCollapsed(collapsed) {
    try {
        window.sessionStorage.setItem(sbConfigPanelCollapsedStorageKey, collapsed ? "1" : "0");
    } catch (e) {
        // Best-effort persistence only; the panel still works for the
        // lifetime of the current page even if this throws.
    }
}

// Renders the full panel: an axis-grouped pill toggle per registered axis
// value (e.g. an "os" row with "windows"/"linux" pills, an "arch" row with
// "x64"/"arm64" pills) when expanded, or a compact chevron + per-axis summary
// ("os: windows", "arch: all") when collapsed. Configs with no axis tags at
// all (data.axes is empty, e.g. plain /config:<name> runs with no
// /configAxes:) fall back to a single flat "Configs" group listing every
// config name directly, matching the original flat-checkbox behavior.
function sbRenderConfigSelectorUI(container, data) {
    var collapsed = sbIsConfigPanelCollapsed();
    container.innerHTML = "";
    container.className = "configSelectorPanel" + (collapsed ? " configSelectorPanelCollapsed" : "");

    var toggle = document.createElement("button");
    toggle.type = "button";
    toggle.className = "configSelectorToggle";
    toggle.setAttribute("aria-label", collapsed ? "Expand config selector" : "Collapse config selector");
    toggle.onclick = function () {
        sbSetConfigPanelCollapsed(!sbIsConfigPanelCollapsed());
        sbRenderConfigSelectorUI(container, data);
    };
    container.appendChild(toggle);

    var body = document.createElement("span");
    body.className = "configSelectorBody";
    container.appendChild(body);

    var axisNames = data.axes ? sbObjectKeys(data.axes) : [];
    var hasAxes = axisNames.length > 0;

    var selectedConfigs = sbGetSelectedConfigs();
    var axisSelections = sbComputeAxisSelectionsFromFlatSelection(data, axisNames, selectedConfigs);

    if (collapsed) {
        if (hasAxes) {
            for (var a = 0; a < axisNames.length; a++) {
                body.appendChild(sbCreateConfigAxisSummaryPill(axisNames[a], data.axes[axisNames[a]], axisSelections[axisNames[a]]));
            }
        } else {
            body.appendChild(sbCreateConfigAxisSummaryPill("Configs", data.configs, selectedConfigs));
        }
        return;
    }

    if (hasAxes) {
        for (var i = 0; i < axisNames.length; i++) {
            body.appendChild(sbCreateConfigAxisGroup(container, data, axisNames[i], data.axes[axisNames[i]], axisSelections));
        }
    } else {
        body.appendChild(sbCreateConfigAxisGroup(container, data, "Configs", data.configs, { "Configs": selectedConfigs }));
    }
}

function sbObjectKeys(obj) {
    var keys = [];
    for (var key in obj) {
        if (Object.prototype.hasOwnProperty.call(obj, key)) {
            keys.push(key);
        }
    }

    return keys;
}

// Given the currently-persisted flat selected-config-name list, derives a
// per-axis "which values are currently restricted" view purely for rendering
// pill active-state -- the source of truth remains the flat selection in
// sessionStorage (sbConfigSelectorStorageKey), matching every other page's
// (and the sbApplyConfigFilter pipeline's) view of it.
function sbComputeAxisSelectionsFromFlatSelection(data, axisNames, selectedConfigs) {
    var axisSelections = {};
    for (var a = 0; a < axisNames.length; a++) {
        axisSelections[axisNames[a]] = [];
    }

    if (!selectedConfigs || selectedConfigs.length === 0) {
        return axisSelections; // No restriction on any axis.
    }

    for (var a2 = 0; a2 < axisNames.length; a2++) {
        var axisName = axisNames[a2];
        var valuesSelectedOnThisAxis = [];
        for (var c = 0; c < selectedConfigs.length; c++) {
            var tags = data.configAxisValues ? data.configAxisValues[selectedConfigs[c]] : null;
            var value = tags ? tags[axisName] : null;
            if (value && !sbContains(valuesSelectedOnThisAxis, value)) {
                valuesSelectedOnThisAxis.push(value);
            }
        }

        // If every known value for this axis is represented, treat it as "no
        // restriction" (matches how a fully-checked set collapses to []).
        var allValues = data.axes[axisName];
        var coversEveryValue = allValues && valuesSelectedOnThisAxis.length === allValues.length;
        axisSelections[axisName] = coversEveryValue ? [] : valuesSelectedOnThisAxis;
    }

    return axisSelections;
}

function sbCreateConfigAxisSummaryPill(axisLabel, allValues, selectedValues) {
    var pill = document.createElement("span");
    pill.className = "configAxisSummaryPill";
    var text = axisLabel + ": " + (selectedValues && selectedValues.length > 0 ? selectedValues.join(", ") : "all");
    pill.appendChild(document.createTextNode(text));
    return pill;
}

function sbCreateConfigAxisGroup(container, data, axisLabel, values, axisSelections) {
    var group = document.createElement("span");
    group.className = "configAxisGroup";

    var label = document.createElement("span");
    label.className = "configSelectorLabel";
    label.appendChild(document.createTextNode(axisLabel + ":"));
    group.appendChild(label);

    var selectedValues = axisSelections[axisLabel] || [];

    for (var i = 0; i < values.length; i++) {
        (function (value) {
            var pill = document.createElement("button");
            pill.type = "button";
            var isActive = selectedValues.length === 0 || sbContains(selectedValues, value);
            pill.className = "configSelectorPill" + (isActive ? " configSelectorPillActive" : "");
            pill.appendChild(document.createTextNode(value));
            pill.onclick = function () {
                sbOnConfigAxisPillToggled(container, data, axisLabel, value);
            };
            group.appendChild(pill);
        })(values[i]);
    }

    return group;
}

// A pill click toggles ONE value within ONE axis group. Recomputes every
// axis's selection from the (now-updated) pill DOM state, derives the flat
// selected-config-names list via the pure sbDeriveSelectedConfigsFromAxisSelections,
// persists it, and re-renders -- same flow as the original flat checkboxes,
// just with an extra per-axis grouping step in front of the existing,
// unchanged sbSetSelectedConfigs/sbApplyConfigFilter pipeline.
function sbOnConfigAxisPillToggled(container, data, toggledAxisLabel, toggledValue) {
    var axisNames = data.axes ? sbObjectKeys(data.axes) : [];
    var hasAxes = axisNames.length > 0;
    var selectedConfigs = sbGetSelectedConfigs();
    var axisSelections = sbComputeAxisSelectionsFromFlatSelection(data, axisNames, selectedConfigs);

    var currentValues = hasAxes ? (axisSelections[toggledAxisLabel] || []) : selectedConfigs;
    var allValuesForAxis = hasAxes ? data.axes[toggledAxisLabel] : data.configs;

    // An empty/no-restriction selection is treated as "everything selected"
    // for toggle purposes, so the first click on any pill narrows down to
    // just that value rather than appearing to add to a full set.
    var effectiveCurrentValues = currentValues.length === 0 ? allValuesForAxis.slice() : currentValues.slice();
    var index = -1;
    for (var i = 0; i < effectiveCurrentValues.length; i++) {
        if (effectiveCurrentValues[i] === toggledValue) {
            index = i;
            break;
        }
    }

    if (index >= 0) {
        effectiveCurrentValues.splice(index, 1);
    } else {
        effectiveCurrentValues.push(toggledValue);
    }

    var newValues = effectiveCurrentValues.length === allValuesForAxis.length ? [] : effectiveCurrentValues;

    var derivedSelectedConfigs;
    if (hasAxes) {
        axisSelections[toggledAxisLabel] = newValues;
        derivedSelectedConfigs = sbDeriveSelectedConfigsFromAxisSelections(axisSelections, data.configAxisValues, data.configs);
    } else {
        derivedSelectedConfigs = newValues;
    }

    var isFullSelection = derivedSelectedConfigs.length === data.configs.length;
    var toPersist = isFullSelection ? [] : derivedSelectedConfigs.map(function (c) { return c.toLowerCase(); });

    sbSetSelectedConfigs(toPersist);
    sbRenderConfigSelectorUI(container, data);
    sbReapplyConfigFilterToAllFrames();
    sbTryAutoNavigateToVariant(toPersist);
}

// When the selection narrows to EXACTLY one config, and the source frame is
// currently showing a file with a config-file-variant banner (StageDivergent
// lyRenderedFiles' output -- a file whose #if-guarded content differs per
// config, rendered as separate physical pages), jump straight to that
// config's variant instead of leaving the user to click the banner link
// manually. This is the practical way to get the effect of "the #if region
// toggles with the selector": each config's branch is a real, independently-
// compiled page (Roslyn's inactive-region classification isn't something a
// single page can re-derive live), so "toggling" means navigating to the
// already-rendered page for that branch, not rewriting DOM in place.
//
// No-ops (leaves today's manual-click behavior) when: more/fewer than one
// config is selected, the current file has no variant banner at all (the
// overwhelming majority of files), or the frame/document isn't reachable for
// any reason -- this is a convenience on top of the filter, never a
// requirement for the filter to still work correctly.
function sbTryAutoNavigateToVariant(selectedConfigs) {
    if (!selectedConfigs || selectedConfigs.length !== 1) {
        return;
    }

    try {
        var sourceDocument = top.s && top.s.document;
        if (!sourceDocument) {
            return;
        }

        var links = sourceDocument.querySelectorAll(".configFileVariantLink[data-configs]");
        if (!links || links.length === 0) {
            return;
        }

        var target = selectedConfigs[0];
        for (var i = 0; i < links.length; i++) {
            var elementConfigs = sbParseConfigList(links[i].getAttribute("data-configs"));
            if (!sbContains(elementConfigs, target)) {
                continue;
            }

            var anchor = links[i].getElementsByTagName("a")[0];
            var href = anchor && anchor.getAttribute("href");
            var hashIndex = href ? href.indexOf("#") : -1;
            if (hashIndex < 0) {
                return;
            }

            // Route through the same hash the banner's target="_top" link would
            // navigate to, so this reuses processHash()'s existing frame-
            // navigation (source AND nav-pane sync) instead of duplicating it.
            var newHash = href.slice(hashIndex + 1);
            if (top.location.hash.slice(1) !== newHash) {
                top.location.hash = newHash;
            }

            return;
        }
    } catch (e) {
        // Best-effort only -- cross-frame access can throw during unusual
        // timing (e.g. a frame mid-navigation); fall back to the manual
        // banner-click path rather than failing the selection change.
    }
}
