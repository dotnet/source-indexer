# MiniRuntime

A tiny stand-in for a "real" upstream repo (think `dotnet/runtime`) used as a
fixture by the local Aspire inner loop in `src/source-indexer.AppHost`.

It exists purely so the inner loop has something small to build, capture a
binlog from, push to Azurite, and index with `HtmlGenerator`. It is not
published anywhere and is not part of the source-indexer product.
