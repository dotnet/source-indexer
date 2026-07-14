# Incremental indexing, multi-config sites, and repo tagging

This document covers three related HtmlGenerator features that build on top of each other:
incremental (re-)indexing, multi-config websites (e.g. Windows vs. Linux, x64 vs. arm64), and
tagging output by source repo. All three are opt-in via CLI flags -- omitting them reproduces
the original, single-pass, single-config behavior byte-for-byte.

## Incremental indexing (`/incremental`)

By default, HtmlGenerator is not incremental: `/force` deletes and fully regenerates `/out` from
scratch every run. `/incremental` instead computes a per-project staleness key (source content +
resolved references + relevant compile options) and skips Pass1 regeneration and the Pass2
copy-into-output step for any project whose key matches the last run's. Every run -- even one that
regenerates nothing -- still recomputes cross-project aggregates ("Used By" backlinks, reference
counts, root indexes) over the *full* current project set, so an unchanged project B correctly
picks up an updated "Used By" entry when only project A's reference to B changed.

```
:: first run: clear and fully generate /out
HtmlGenerator.exe MySolution.sln /out:C:\output /force

:: every run after that: only regenerate what changed
HtmlGenerator.exe MySolution.sln /out:C:\output /incremental
```

Rules of thumb:
* Use `/force` exactly once (or whenever you want a guaranteed clean rebuild); use `/incremental`
  for every run after that against the same `/out`.
* Without `/force` or `/incremental`, `/out` is left untouched -- one of the two is required.
* `/out` must be a dedicated, disposable folder. It must not be nested inside HtmlGenerator's own
  build output (`bin\...\Web`), because `WebsiteFinalizer` copies that template folder into `/out`
  on every pass -- nesting causes it to copy itself into itself.

## Multi-config sites (`/config:`, `/configAxes:`)

If you need to index the same sources built more than one way (e.g. a `#if WINDOWS` /
`#if LINUX` codebase, or windows-x64 vs. linux-arm64), run HtmlGenerator once per build
configuration against the *same* `/out`, tagging each run with `/config:<name>`:

```
HtmlGenerator.exe MySolution.sln /out:C:\output /force      /config:windows /p:DefineConstants=WINDOWS
HtmlGenerator.exe MySolution.sln /out:C:\output /incremental /config:linux   /p:DefineConstants=LINUX
```

Configs are **merged into a single served index**, not partitioned into separate sites: symbols,
references, and rendered files that are identical across configs collapse into one shared entry;
only genuinely divergent declarations/references/files get config-tagged variants. With 0 or 1
config registered, output is unaffected -- byte-identical to a run without `/config:` at all. A
selector fold into the code view shows up automatically on any page/element that actually has more
than one config's worth of content, letting readers filter to just the config(s) they care about.

If your build matrix has more than one dimension (OS x architecture, for example), tag each
config with structured axis values via `/configAxes:`:

```
/config:windows-x64 /configAxes:os=windows;arch=x64
/config:linux-arm64  /configAxes:os=linux;arch=arm64
```

This groups the selector UI by axis (one pill group for `os`, one for `arch`, ...) instead of one
flat checkbox per config name -- important once you have more than a couple of configs, since a
flat list doesn't scale (e.g. 2 OSes x 2 architectures is 4 flat names, but only 2+2 axis values).
`/configAxes:` is ignored (and harmless) without `/config:`.

For CI setups where each platform's job runs in an isolated environment and only the final
aggregation step has all of them together, `/mergeConfigsOnly` runs just the merge step (no
MSBuild/Pass1) over whatever configs are already staged in `/out`'s `obj/<config>` folders --
useful when each CI job uploads its own per-config Pass1 output as an artifact, and a separate job
downloads them all onto one `/out` and merges.

## Repo tagging (`/repoPath:`, `/serverPath:`, `/repo:`)

When indexing multiple repos into one site (as multi-config indexing above might, or just because
you want one combined index across several checkouts), tag each local source folder with a
display name so Solution Explorer groups projects under Repo/Solution nodes instead of a flat
list, and so search can optionally be scoped to just one repo:

```
/repoPath:"C:\src\llvmsharp"="llvmsharp"
/repoPath:"C:\src\clangsharp"="clangsharp"
```

If you also want source links to point at each repo's hosted URL, use `/serverPath:` the same way,
or use `/repo:` as sugar for specifying both at once:

```
/repo:"C:\src\llvmsharp"="llvmsharp"="https://github.com/dotnet/llvmsharp"
```

Quotes around each part are optional if the value has no spaces or `=` signs, but recommended.
Untagged folders are simply left out of the repo grouping/scoping -- this is purely additive.

## Putting it together

`GenerateTestSite.cmd` in the repo root demonstrates all three together: two repos
(`TestCode\TestSolution.sln` and `TestCode\RepoB\RepoB.slnx`), tagged via `/repoPath:`, indexed
across two configs (`windows` built with `/force`, then `linux` with `/incremental`), each tagged
with an `os` axis via `/configAxes:`. Run it, then `RunTestSite.cmd`, to see multi-repo grouping,
the multi-axis config selector, and incremental re-indexing all working together against one
generated site.
