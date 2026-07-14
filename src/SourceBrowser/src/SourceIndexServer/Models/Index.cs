using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.SourceBrowser.Common;

namespace Microsoft.SourceBrowser.SourceIndexServer.Models
{
    public class Index : IDisposable
    {
        // Final, post-ranking display cap -- also drives the "showing top 100 of N" UI message
        // (see Query.PotentialRawResults / ResultsHtmlGenerator.GenerateResultCount).
        public const int MaxRawResults = 100;

        // Pre-ranking safety valve applied to the prefix-range pass only. Deliberately much
        // higher than MaxRawResults: candidates from every pass/interpretation are merged and
        // ranked with SymbolSorter *before* MaxRawResults is applied, so this only needs to be
        // large enough that a genuinely good match is never truncated before it gets scored. This
        // is sound for the prefix-range pass because MatchTier there is a small fixed set of
        // discrete values (Exact/Prefix x CaseSensitive/IgnoreCase) -- collisions within a Name-
        // sorted prefix range are cheap to reason about. It is NOT applied to the fuzzy fallback
        // scan: that tier ranks by a continuous score, so an early stop after N matches would keep
        // whichever candidates happen to sort alphabetically first and silently drop higher-
        // scoring matches found later -- see the fuzzy loop in FindSymbols for the fix.
        public const int MaxCandidatesPerPass = 2000;

        public static string RootPath { get; private set; }

        // For testing
        public static void SetRootPath(string rootPath)
        {
            RootPath = rootPath;
        }

        public Index()
        {
        }

        public Index(string rootPath)
        {
            RootPath = rootPath;
            Task.Run(() => IndexLoader.ReadIndex(this, rootPath));
        }

        internal void ClearAll()
        {
            assemblies.Clear();
            projects.Clear();
            symbols.Clear();
            symbolsById.Clear();
            symbolCharMasks = [];
            guids.Clear();
            projectToAssemblyIndexMap.Clear();
            msbuildProperties.Clear();
            msbuildItems.Clear();
            msbuildTargets.Clear();
            indexFinishedPopulating = false;
            progress = 0.0;
            huffman = null;
        }

        public List<AssemblyInfo> assemblies = new List<AssemblyInfo>();
        public List<string> projects = new List<string>();
        public List<IndexEntry> symbols = new List<IndexEntry>();
        public Dictionary<ulong, int> symbolsById = new Dictionary<ulong, int>();

        // Per-symbol char-presence bitmap (bit i = lowercase letter 'a'+i appears in the name),
        // used to cheaply prune candidates before running the fuzzy subsequence scorer -- see
        // SymbolNameMatcher.ComputeCharMask. Built alongside symbolsById in PopulateSymbolsById so
        // every existing call site (IndexLoader and tests) gets it "for free".
        public uint[] symbolCharMasks = [];

        public List<string> guids = new List<string>();
        public Dictionary<string, int> projectToAssemblyIndexMap = new Dictionary<string, int>();
        public List<string> msbuildProperties = new List<string>();
        public List<string> msbuildItems = new List<string>();
        public List<string> msbuildTargets = new List<string>();
        public List<string> msbuildTasks = new List<string>();

        public Huffman huffman;
        public bool indexFinishedPopulating = false;
        public double progress = 0.0;
        public string loadErrorMessage = null;

        public void PopulateSymbolsById()
        {
            symbolCharMasks = new uint[symbols.Count];

            for (int i = 0; i < symbols.Count; i++)
            {
                var symbol = symbols[i];
                symbolsById[symbol.ID] = i;
                symbolCharMasks[i] = SymbolNameMatcher.ComputeCharMask(symbol.Name);
            }
        }

        public Query Get(string queryString)
        {
            if (!indexFinishedPopulating)
            {
                string message = "Index is being rebuilt... " + string.Format("{0:0%}", progress);
                if (loadErrorMessage != null)
                {
                    message = message + "<br />" + loadErrorMessage;
                }

                return Query.Empty(message);
            }

            if (queryString.Length < 3)
            {
                return Query.Empty("Enter at least three characters for type or member name");
            }

            var query = new Query(queryString);
            if (query.IsAssemblySearch())
            {
                FindAssemblies(query, defaultToAll: true);
            }
            else if (query.SymbolKinds.Contains(SymbolKindText.MSBuildProperty))
            {
                FindMSBuildProperties(query, defaultToAll: true);
            }
            else if (query.SymbolKinds.Contains(SymbolKindText.MSBuildItem))
            {
                FindMSBuildItems(query, defaultToAll: true);
            }
            else if (query.SymbolKinds.Contains(SymbolKindText.MSBuildTarget))
            {
                FindMSBuildTargets(query, defaultToAll: true);
            }
            else if (query.SymbolKinds.Contains(SymbolKindText.MSBuildTask))
            {
                FindMSBuildTasks(query, defaultToAll: true);
            }
            else
            {
                FindSymbols(query);
                FindAssemblies(query);
                FindProjects(query);
                FindGuids(query);
                FindMSBuildProperties(query);
                FindMSBuildItems(query);
                FindMSBuildTargets(query);
                FindMSBuildTasks(query);
            }

            return query;
        }

        public AssemblyInfo FindAssembly(string assemblyName)
        {
            int i = SortedSearch.FindItem(assemblies, assemblyName, a => a.AssemblyName);
            if (i == -1)
            {
                return default(AssemblyInfo);
            }

            return assemblies[i];
        }

        public int GetReferencingAssembliesCount(string assemblyName)
        {
            var assemblyInfo = FindAssembly(assemblyName);
            return assemblyInfo.ReferencingAssembliesCount;
        }

        public void FindAssemblies(Query query, bool defaultToAll = false)
        {
            string assemblyName = query.GetSearchTermForAssemblySearch();
            if (assemblyName == null)
            {
                if (defaultToAll)
                {
                    query.AddResultAssemblies(GetAllListedAssemblies());
                }

                return;
            }

            bool isQuoted = false;
            assemblyName = Query.StripQuotes(assemblyName, out isQuoted);

            var search = new SortedSearch(i => this.assemblies[i].AssemblyName, this.assemblies.Count);
            int low, high;
            search.FindBounds(assemblyName, out low, out high);
            if (high >= low)
            {
                var result = Enumerable
                    .Range(low, high - low + 1)
                    .Where(i => !isQuoted || assemblies[i].AssemblyName.Length == assemblyName.Length)
                    .Select(i => assemblies[i])
                    .Where(a => a.ProjectKey != -1)
                    .Take(MaxRawResults)
                    .ToList();
                query.AddResultAssemblies(result);
            }
        }

        private IEnumerable<AssemblyInfo> GetAllListedAssemblies()
        {
            return this.assemblies.Where(a => a.ProjectKey != -1);
        }

        public void FindSymbols(Query query)
        {
            // Tracks symbol positions already emitted by an earlier interpretation/pass in this
            // call, so a symbol matched by one interpretation's prefix pass isn't independently
            // re-discovered (and duplicated) by another interpretation's camelCase/substring
            // fallback pass over the same query -- e.g. "Source Symbol" searching for two names,
            // where "SourceNamedTypeSymbol" is a prefix match for "Source" and would otherwise also
            // be a substring match for "Symbol". Pre-sized to a fixed, symbols.Count-independent
            // bound: each interpretation contributes at most MaxCandidatesPerPass (prefix pass) +
            // MaxRawResults (fuzzy pass) entries, so this avoids repeated doubling/copying as
            // interpretations are merged without ever over-allocating relative to the (typically
            // much larger) total symbol count.
            var seenSymbolIndices = new HashSet<int>((MaxCandidatesPerPass + MaxRawResults) * Math.Max(1, query.Interpretations.Count));

            // Count of prefix-or-better candidates already merged into this query, across every
            // interpretation processed so far. Since ranking strictly prefers exact/prefix over
            // camelCase/substring (see SymbolNameMatcher.MatchTier), once this reaches
            // MaxRawResults no camel/substring hit can ever appear in the final, capped, sorted
            // result -- so the (expensive) fallback scan becomes pure wasted work and is skipped.
            // This is what keeps a plain/common prefix search (e.g. "Widget") cheap: it no longer
            // pays for a full-table scan just to confirm there's nothing better to add.
            int prefixOrBetterCount = 0;

            foreach (var interpretation in query.Interpretations)
            {
                FindSymbols(query, interpretation, seenSymbolIndices, ref prefixOrBetterCount);
            }

            if (query.ResultSymbols.Any())
            {
                query.ResultSymbols.Sort((l, r) => SymbolSorter(l, r, query));

                // Apply the display cap only now, after all passes/interpretations have been merged
                // and ranked -- previously each pass capped at MaxRawResults independently, in
                // Name-sort order, before ranking ever ran, so a genuinely better match could be
                // dropped in favor of an arbitrary earlier (in name-sort order) same-tier one. See
                // https://github.com/KirillOsenkov/SourceBrowser/issues/29.
                if (query.ResultSymbols.Count > MaxRawResults)
                {
                    query.ResultSymbols.RemoveRange(MaxRawResults, query.ResultSymbols.Count - MaxRawResults);
                }
            }
        }

        private void FindSymbols(Query query, Interpretation interpretation, HashSet<int> seenSymbolIndices, ref int prefixOrBetterCount)
        {
            string searchTerm = interpretation.CoreSearchTerm;

            var search = new SortedSearch(i => symbols[i].Name, symbols.Count);

            int low, high;
            search.FindBounds(searchTerm, out low, out high);

            // Bounded by a fixed, symbols.Count-independent constant regardless of index size:
            // at most MaxCandidatesPerPass from the prefix-range pass below plus MaxRawResults
            // from the fuzzy fallback pass, so pre-sizing here avoids repeated doubling/copying
            // without ever over-allocating relative to the (typically much larger) symbol table.
            var candidates = new List<DeclaredSymbolInfo>(MaxCandidatesPerPass + MaxRawResults);

            if (high >= low)
            {
                query.PotentialRawResults += high - low + 1;

                for (int i = low; i <= high && candidates.Count < MaxCandidatesPerPass; i++)
                {
                    // A symbol already promoted to a result by another interpretation/pass for this
                    // query is skipped here, rather than being independently re-discovered (and
                    // duplicated) -- e.g. searching "Source Symbol" over a type also matched by the
                    // "Symbol" interpretation's substring-anywhere fallback pass below.
                    if (seenSymbolIndices.Contains(i))
                    {
                        continue;
                    }

                    if (interpretation.IsVerbatim && symbols[i].Name.Length != searchTerm.Length)
                    {
                        continue;
                    }

                    var entry = symbols[i].GetDeclaredSymbolInfo(huffman, assemblies, projects);
                    if (!query.Filter(entry) || !interpretation.Filter(entry))
                    {
                        continue;
                    }

                    entry.MatchLevel = (ushort)SymbolNameMatcher.ClassifyExactOrPrefix(entry.Name, searchTerm);
                    candidates.Add(entry);
                    seenSymbolIndices.Add(i);

                    // Everything in [low, high] is at least a case-insensitive prefix match by
                    // construction of the SortedSearch bounds, so ClassifyExactOrPrefix can never
                    // return None here.
                    prefixOrBetterCount++;
                }
            }

            // Unified fuzzy (boundary-aware subsequence) candidate gathering (issue #29). This
            // single scorer subsumes both camelCase-hump matching (e.g. "NeCl" -> "NewClass") and
            // substring-anywhere matching ("widget" -> "xxwidgetxx") -- see
            // SymbolNameMatcher.TryScoreFuzzy. It isn't restricted to the SortedSearch prefix range
            // at all, so this is a genuinely separate scan, not just a ranking change. Skipped for
            // verbatim (quoted) terms, which are meant to be literal, and skipped once prefix-or-
            // better candidates have already filled the display cap for this query (see the
            // prefixOrBetterCount comment above) -- at that point no fuzzy hit could ever make it
            // into the displayed results anyway.
            if (!interpretation.IsVerbatim && searchTerm.Length > 0 && prefixOrBetterCount < MaxRawResults)
            {
                // Every query character must be present in a candidate for a subsequence match to
                // be possible, so this mask lets the loop below skip the vast majority of symbols
                // with a single O(1) bitwise check, before ever touching the (much more expensive)
                // scorer or the candidate's name string.
                uint queryMask = SymbolNameMatcher.ComputeCharMask(searchTerm);

                // Pass 1: score every prune-surviving candidate using only the cheap, already-
                // decompressed IndexEntry.Name -- no MaxCandidatesPerPass cap here, unlike the
                // prefix-range pass above. The fuzzy tier ranks by a *continuous* score, not by
                // scan/Name-sort order, so stopping the scan itself after N matches would keep
                // whichever candidates happen to sort alphabetically first and silently discard
                // higher-scoring matches found later in the array -- the same truncate-before-rank
                // bug fixed for MaxRawResults in the outer FindSymbols, just relocated into this
                // pass. Every surviving candidate is scored here; nothing is discarded by scan
                // order.
                //
                // Deliberately left unsized (not pre-allocated to symbols.Count or another
                // fixed guess): survivor count is entirely query-dependent -- from a handful
                // (selective queries like "WiFa") up to hundreds of thousands (common-letter
                // queries like "NeCl" at large index sizes, measured ~365k of 1M symbols). List<T>
                // doubling growth is already amortized O(n) total copying, so there's no runaway
                // (quadratic) growth risk here to fix; pre-sizing to a worst-case bound would
                // instead cost real transient memory on every low-selectivity query just to save a
                // few reallocations on the rare high-selectivity one.
                var scored = new List<(int index, int score)>();

                for (int i = 0; i < symbols.Count; i++)
                {
                    if (i >= low && i <= high)
                    {
                        // Already covered (and counted) by the prefix pass above.
                        continue;
                    }

                    if (seenSymbolIndices.Contains(i))
                    {
                        continue;
                    }

                    if ((symbolCharMasks[i] & queryMask) != queryMask)
                    {
                        continue;
                    }

                    if (!SymbolNameMatcher.TryScoreFuzzy(symbols[i].Name, searchTerm, out int score))
                    {
                        continue;
                    }

                    scored.Add((i, score));
                }

                query.PotentialRawResults += scored.Count;

                // Pass 2: decompress (GetDeclaredSymbolInfo) and filter only in strictly
                // descending score order, stopping once MaxRawResults passing candidates have
                // been collected for this pass. This is correctness-preserving, not a repeat of
                // the scan-order bug: because candidates are visited best-score-first, we can
                // never skip a higher-scoring passing match in favor of a lower-scoring one, and
                // this pass's own top MaxRawResults is provably sufficient for the query's global
                // top MaxRawResults once merged with every other pass/interpretation (a classic
                // top-K-of-the-union property) -- see the MaxRawResults truncation in the outer
                // FindSymbols. This keeps the (expensive) decompression + query/interpretation
                // filtering off the vast majority of survivors that the final display will never
                // show, without ever guessing at rank before scoring.
                scored.Sort((a, b) => b.score.CompareTo(a.score));

                int fuzzyMatchCount = 0;
                for (int s = 0; s < scored.Count && fuzzyMatchCount < MaxRawResults; s++)
                {
                    int i = scored[s].index;
                    var entry = symbols[i].GetDeclaredSymbolInfo(huffman, assemblies, projects);
                    if (!query.Filter(entry) || !interpretation.Filter(entry))
                    {
                        continue;
                    }

                    entry.MatchLevel = SymbolNameMatcher.FuzzyMatchLevel(scored[s].score);
                    candidates.Add(entry);
                    seenSymbolIndices.Add(i);
                    fuzzyMatchCount++;
                }
            }

            query.AddResultSymbols(candidates);
        }

        private void FindProjects(Query query)
        {
            string searchTerm = query.GetSearchTermForProjectSearch();
            if (searchTerm == null)
            {
                return;
            }

            var search = new SortedSearch(i => projects[i], projects.Count);

            int low, high;
            search.FindBounds(searchTerm, out low, out high);
            if (high >= low)
            {
                var result = Enumerable
                    .Range(low, high - low + 1)
                    .Select(i => assemblies[projectToAssemblyIndexMap[projects[i]]])
                    .Take(MaxRawResults)
                    .ToList();
                query.AddResultProjects(result);
            }
        }

        private void FindGuids(Query query)
        {
            string searchTerm = query.OriginalString;
            searchTerm = searchTerm.TrimStart('{', '(');
            searchTerm = searchTerm.TrimEnd('}', ')');

            var result = FindInList(searchTerm, guids, defaultToAll: false);
            if (result != null && result.Any())
            {
                query.AddResultGuids(result.ToList());
            }
        }

        private void FindMSBuildProperties(Query query, bool defaultToAll = false)
        {
            var result = FindInList(query.GetSearchTermForMSBuildSearch(), msbuildProperties, defaultToAll);
            if (result != null && result.Any())
            {
                query.AddResultMSBuildProperties(result.ToList());
            }
        }

        private void FindMSBuildItems(Query query, bool defaultToAll = false)
        {
            var result = FindInList(query.GetSearchTermForMSBuildSearch(), msbuildItems, defaultToAll);
            if (result != null && result.Any())
            {
                query.AddResultMSBuildItems(result.ToList());
            }
        }

        private void FindMSBuildTargets(Query query, bool defaultToAll = false)
        {
            var result = FindInList(query.GetSearchTermForMSBuildSearch(), msbuildTargets, defaultToAll);
            if (result != null && result.Any())
            {
                query.AddResultMSBuildTargets(result.ToList());
            }
        }

        private void FindMSBuildTasks(Query query, bool defaultToAll = false)
        {
            var result = FindInList(query.GetSearchTermForMSBuildSearch(), msbuildTasks, defaultToAll);
            if (result != null && result.Any())
            {
                query.AddResultMSBuildTasks(result.ToList());
            }
        }

        private IEnumerable<string> FindInList(string searchTerm, List<string> list, bool defaultToAll)
        {
            if (string.IsNullOrEmpty(searchTerm))
            {
                if (defaultToAll)
                {
                    return list;
                }
                else
                {
                    return null;
                }
            }

            var search = new SortedSearch(i => list[i], list.Count);

            int low, high;
            search.FindBounds(searchTerm, out low, out high);
            if (high >= low)
            {
                var result = Enumerable
                    .Range(low, high - low + 1)
                    .Select(i => list[i])
                    .Take(MaxRawResults);
                return result;
            }

            return null;
        }

        public List<DeclaredSymbolInfo> FindSymbols(string queryString)
        {
            var query = new Query(queryString);
            FindSymbols(query);
            return query.ResultSymbols;
        }

        /// <summary>
        /// This defines the ordering of results based on the kind of symbol and other heuristics
        /// </summary>
        private int SymbolSorter(DeclaredSymbolInfo left, DeclaredSymbolInfo right, Query query)
        {
            if (left == right)
            {
                return 0;
            }

            if (left == null || right == null)
            {
                return 1;
            }

            var comparison = left.MatchLevel.CompareTo(right.MatchLevel);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.KindRank.CompareTo(right.KindRank);
            if (comparison != 0)
            {
                return comparison;
            }

            // Among equally-good matches, prefer the canonical framework assemblies so BCL types
            // surface above incidental same-named types from niche assemblies.
            comparison = DeclaredSymbolInfo.GetAssemblyRank(left.AssemblyName).CompareTo(DeclaredSymbolInfo.GetAssemblyRank(right.AssemblyName));
            if (comparison != 0)
            {
                return comparison;
            }

            if (left.Name != null && right.Name != null)
            {
                comparison = string.Compare(left.Name, right.Name, StringComparison.Ordinal);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            comparison = left.AssemblyNumber.CompareTo(right.AssemblyNumber);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = StringComparer.Ordinal.Compare(left.Description, right.Description);
            return comparison;
        }

        public unsafe void Dispose()
        {
            if (huffman == null || symbols == null || assemblies == null || projects == null)
            {
                return;
            }

            for (int i = 0; i < this.symbols.Count; i++)
            {
                if (this.symbols[i].Description != IntPtr.Zero)
                {
                    NativeMemory.Free((void*)this.symbols[i].Description);
                }
            }

            this.huffman = null;
            this.symbols = null;
            this.symbolsById = null;
            this.assemblies = null;
            this.projects = null;
        }

        ~Index()
        {
            Dispose();
        }
    }
}