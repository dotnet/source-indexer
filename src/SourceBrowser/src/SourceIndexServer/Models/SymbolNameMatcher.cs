using System;

namespace Microsoft.SourceBrowser.SourceIndexServer.Models
{
    /// <summary>
    /// The guaranteed-top match tiers -- exact and prefix always outrank every fuzzy (subsequence)
    /// match, regardless of fuzzy score. See <see cref="SymbolNameMatcher.FuzzyMatchLevel(int)"/>
    /// for how fuzzy scores are encoded into the same <c>DeclaredSymbolInfo.MatchLevel</c> ushort
    /// space above this floor.
    /// </summary>
    public enum MatchTier : ushort
    {
        None = 0,
        ExactCaseSensitive = 1,
        ExactIgnoreCase = 2,
        PrefixCaseSensitive = 3,
        PrefixIgnoreCase = 4,
    }

    /// <summary>
    /// Implements the "intelligent search" matching rules from
    /// https://github.com/KirillOsenkov/SourceBrowser/issues/29. In addition to plain prefix
    /// matching, a candidate can match via a single unified, boundary-aware subsequence scorer
    /// (fzf/VS-Code-fuzzyScore style) that subsumes both camelCase-hump matching (e.g. "NeCl"
    /// matches "NewClass"/"MyNewClass") and substring-anywhere matching ("widget" matches
    /// "xxwidgetxx") -- the same scorer handles both, rewarding boundary-aligned and consecutive
    /// runs so a camel-hump-aligned match naturally outscores an arbitrary substring one. Fuzzy
    /// matches are only ever considered when a candidate doesn't qualify for exact/prefix, and are
    /// always ranked below every exact/prefix match (see <see cref="FuzzyMatchLevel(int)"/>).
    /// </summary>
    public static class SymbolNameMatcher
    {
        // Every MatchLevel >= FuzzyLevelFloor is a fuzzy (subsequence) match; every value below it
        // is a MatchTier (None=0 .. PrefixIgnoreCase=4). This guarantees exact/prefix always sorts
        // ahead of every fuzzy match, no matter how high the fuzzy score is.
        public const ushort FuzzyLevelFloor = 1000;

        // Scores are clamped into this range before being encoded as a MatchLevel, so a
        // pathologically long query/candidate can't overflow the ushort or collide with the
        // exact/prefix tiers. 2000 comfortably exceeds any realistic per-character bonus (at most
        // ~7 per matched character -- see Score below) for query lengths well beyond what anyone
        // would type into a symbol search box.
        private const int MaxFuzzyScore = 2000;

        /// <summary>
        /// Classifies <paramref name="candidate"/> against <paramref name="query"/> using only the
        /// cheap exact/prefix checks -- returns <see cref="MatchTier.None"/> if neither applies.
        /// Callers gathering candidates from outside the prefix range should fall back to
        /// <see cref="ComputeCharMask(string)"/> + <see cref="TryScoreFuzzy(string, string, out int)"/>
        /// instead of treating a None here as "no match".
        /// </summary>
        public static MatchTier ClassifyExactOrPrefix(string candidate, string query)
        {
            if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(query) || candidate.Length < query.Length)
            {
                return MatchTier.None;
            }

            // Most callers (e.g. Index.FindSymbols's prefix-range pass) only ever invoke this on
            // candidates already known to be at least a case-insensitive prefix match, so an exact
            // whole-string search is the rare case and a genuine mismatch essentially never
            // happens here -- checking case-SENSITIVE equality/prefix first (as a naive
            // exact-then-prefix, case-sensitive-then-insensitive ordering would) wastes a scan on
            // a comparison that's usually going to fail anyway. Check ignore-case once instead --
            // it subsumes both "is this an exact match" (equal length) and "is this a prefix
            // match" (candidate longer than query) -- and only pay for the case-SENSITIVE
            // comparison (to pick the CaseSensitive vs IgnoreCase sub-tier) once we already know
            // there's a match to classify.
            var candidatePrefix = candidate.AsSpan(0, query.Length);
            if (!candidatePrefix.Equals(query, StringComparison.OrdinalIgnoreCase))
            {
                return MatchTier.None;
            }

            bool isCaseSensitiveMatch = candidatePrefix.Equals(query, StringComparison.Ordinal);

            if (candidate.Length == query.Length)
            {
                return isCaseSensitiveMatch ? MatchTier.ExactCaseSensitive : MatchTier.ExactIgnoreCase;
            }

            return isCaseSensitiveMatch ? MatchTier.PrefixCaseSensitive : MatchTier.PrefixIgnoreCase;
        }

        /// <summary>
        /// Encodes a fuzzy match score (see <see cref="TryScoreFuzzy"/>) into the same MatchLevel
        /// space as <see cref="MatchTier"/>, always above <see cref="FuzzyLevelFloor"/> -- and
        /// therefore always ranked below every exact/prefix tier -- while higher scores still sort
        /// ahead of lower ones within the fuzzy band (MatchLevel sorts ascending = better first).
        /// </summary>
        public static ushort FuzzyMatchLevel(int score)
        {
            int clamped = Math.Min(Math.Max(score, 0), MaxFuzzyScore);
            return (ushort)(FuzzyLevelFloor + (MaxFuzzyScore - clamped));
        }

        /// <summary>
        /// A cheap per-symbol prune signal: bit <c>i</c> is set if the lowercase-invariant
        /// <paramref name="name"/> contains letter <c>'a' + i</c> (non-letters are ignored). A
        /// subsequence match requires every query character to be present in the candidate, so
        /// computing this once per symbol (at index-load time) lets a query's mask be AND-checked
        /// against it in O(1) to skip candidates that can't possibly match -- without ever touching
        /// the (much more expensive) scorer or the candidate's name string at query time.
        /// </summary>
        public static uint ComputeCharMask(string name)
        {
            uint mask = 0;

            for (int i = 0; i < name.Length; i++)
            {
                char c = char.ToLowerInvariant(name[i]);
                if (c >= 'a' && c <= 'z')
                {
                    mask |= 1u << (c - 'a');
                }
            }

            return mask;
        }

        /// <summary>
        /// Attempts a boundary-aware subsequence match of <paramref name="query"/> against
        /// <paramref name="candidate"/> -- every query character must appear in <paramref
        /// name="candidate"/>, in order (case-insensitively), though not necessarily consecutively.
        /// A single left-to-right greedy scan (not a backtracking search): for each query
        /// character, takes the *first* case-insensitive match at or after the current candidate
        /// position. This is intentionally cheap (O(candidate length), one pass, no memoization) --
        /// it fails (returns false) as soon as a query character can't be found, so there's no
        /// separate "does this even match" pre-check to add: scoring and rejection are the same
        /// pass.
        /// </summary>
        public static bool TryScoreFuzzy(string candidate, string query, out int score)
        {
            int ci = 0;
            int total = 0;
            int consecutive = 0;

            for (int qi = 0; qi < query.Length; qi++)
            {
                char qc = query[qi];
                char qcLower = char.ToLowerInvariant(qc);
                bool found = false;

                for (; ci < candidate.Length; ci++)
                {
                    char cc = candidate[ci];
                    if (char.ToLowerInvariant(cc) != qcLower)
                    {
                        consecutive = 0;
                        continue;
                    }

                    int bonus = 1;

                    if (cc == qc)
                    {
                        bonus += 1; // exact-case agreement
                    }

                    if (ci == 0)
                    {
                        bonus += 3; // start-of-string
                    }
                    else
                    {
                        char prev = candidate[ci - 1];
                        bool humpBoundary = char.IsUpper(cc) && char.IsLower(prev);
                        bool afterSeparator = !char.IsLetterOrDigit(prev);
                        if (humpBoundary || afterSeparator)
                        {
                            bonus += 3; // camelCase-hump / word-boundary start
                        }
                    }

                    if (consecutive > 0)
                    {
                        bonus += 2; // consecutive run
                    }

                    total += bonus;
                    consecutive++;
                    ci++;
                    found = true;
                    break;
                }

                if (!found)
                {
                    score = 0;
                    return false;
                }
            }

            score = total;
            return true;
        }
    }
}
