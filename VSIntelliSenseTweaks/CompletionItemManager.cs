#if DEBUG
#define INCLUDE_DEBUG_SUFFIX
#define DEBUG_TIME
#endif

using Microsoft;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using VSIntelliSenseTweaks.Utilities;

using RoslynCompletionItem = Microsoft.CodeAnalysis.Completion.CompletionItem;
using VSCompletionItem = Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data.CompletionItem;

namespace VSIntelliSenseTweaks
{
    [Export(typeof(IAsyncCompletionItemManagerProvider))]
    [ContentType("CSharp")]
    [Name("CustomCompletionItemManagerProvider")]
    internal class CompletionItemManagerProvider : IAsyncCompletionItemManagerProvider
    {
        public IAsyncCompletionItemManager GetOrCreate(ITextView textView)
        {
            return new CompletionItemManager();
        }
    }

    internal class CompletionItemManager : IAsyncCompletionItemManager
    {
        const int textFilterMaxLength = 256;

        CompletionItemWithHighlight[] eligibleCompletions;
        CompletionItemKey[] eligibleKeys;
        WordScorer scorer = new WordScorer(stackInitialCapacity: 4096);

        CompletionFilterManager filterManager;
        bool hasFilterManager;

#if DEBUG_TIME
        Stopwatch watch = new Stopwatch();
#endif

        private void EnsureCapacity(int n_completions)
        {
            if (eligibleCompletions == null || n_completions > eligibleCompletions.Length)
            {
                this.eligibleCompletions = new CompletionItemWithHighlight[n_completions];
                this.eligibleKeys        = new CompletionItemKey[n_completions];
            }
            this.hasFilterManager = false;
        }

        //public Task<CompletionList<VSCompletionItem>> SortCompletionItemListAsync(IAsyncCompletionSession session, AsyncCompletionSessionInitialDataSnapshot data, CancellationToken token)
        //{
        //    var completions = data.InitialItemList;
        //    int n_completions = completions.Count;
        //    EnsureCapacity(n_completions);
        //    var a = session.CreateCompletionList(data.InitialItemList);
        //    return Task.FromResult(a);
        //}

        public Task<ImmutableArray<VSCompletionItem>> SortCompletionListAsync(IAsyncCompletionSession session, AsyncCompletionSessionInitialDataSnapshot data, CancellationToken token)
        {
            var sortTask = Task.Factory.StartNew(TaskFunction, token, TaskCreationOptions.None, TaskScheduler.Current);

            return sortTask;

            ImmutableArray<VSCompletionItem> TaskFunction() => SortCompletionList(session, data, token);
        }

        public Task<FilteredCompletionModel> UpdateCompletionListAsync(IAsyncCompletionSession session, AsyncCompletionSessionDataSnapshot data, CancellationToken token)
        {
            var updateTask = Task.Factory.StartNew(TaskFunction, token, TaskCreationOptions.None, TaskScheduler.Current);

            return updateTask;

            FilteredCompletionModel TaskFunction() => UpdateCompletionList(session, data, token);
        }

        public ImmutableArray<VSCompletionItem> SortCompletionList(IAsyncCompletionSession session, AsyncCompletionSessionInitialDataSnapshot data, CancellationToken token)
        {
#if DEBUG_TIME
            watch.Restart();
#endif
            var completions = data.InitialItemList;
            int n_completions = completions.Count;

            EnsureCapacity(n_completions);

            var builder = ImmutableArray.CreateBuilder<VSCompletionItem>(n_completions);
            for (int i = 0; i < n_completions; i++)
            {
                var completion = completions[i];
                builder.Add(completion);
            }
            builder.Sort(new InitialComparer());
            var initialSortedItems = builder.MoveToImmutable();

#if DEBUG_TIME
            watch.Stop();
            Debug.WriteLine($"SortCompletionList ms: {watch.ElapsedMilliseconds}");
#endif
            return initialSortedItems;
        }

        public FilteredCompletionModel UpdateCompletionList(IAsyncCompletionSession session, AsyncCompletionSessionDataSnapshot data, CancellationToken token)
        {
#if DEBUG_TIME
            watch.Restart();
#endif
            var completions = data.InitialSortedItemList;
            int n_completions = completions.Count;

            var filterStates = data.SelectedFilters; // The types of filters displayed in the IntelliSense widget.
            if (!hasFilterManager)
            {
                filterManager = new CompletionFilterManager(filterStates);
            }
            filterManager.UpdateActiveFilters(filterStates);

            var textFilter = session.ApplicableToSpan.GetText(data.Snapshot);
            bool hasTextFilter = textFilter.Length > 0;
            int n_eligibleCompletions = 0;

#if DEBUG_TIME
            Debug.WriteLine($"UpdateCompletionList Before AddEligibleCompletions ms: {watch.ElapsedMilliseconds}");
#endif
            AddEligibleCompletions();
#if DEBUG_TIME
            Debug.WriteLine($"UpdateCompletionList After AddEligibleCompletions ms: {watch.ElapsedMilliseconds}");
#endif

            var selectionKind = GetSelectionKind();

            var result = new FilteredCompletionModel
            (
                items: ImmutableArray.Create(eligibleCompletions, 0, n_eligibleCompletions),
                selectedItemIndex: 0,
                filters: filterStates,
                selectionHint: selectionKind,
                centerSelection: false,
                uniqueItem: null
            );

            Debug.Assert(!token.IsCancellationRequested);

#if DEBUG_TIME
            Debug.WriteLine($"UpdateCompletionList ms: {watch.ElapsedMilliseconds}");
            watch.Stop();
#endif
            return result;

            void AddEligibleCompletions()
            {
                var pattern = textFilter.AsSpan(0, Math.Min(textFilter.Length, textFilterMaxLength));
                BitField64 availableFilters = default;
                for (int i = 0; i < n_completions; i++)
                {
                    var completion = completions[i];

                    int patternScore;
                    ImmutableArray<Microsoft.VisualStudio.Text.Span> matchedSpans;
                    if (hasTextFilter)
                    {
                        var word = completion.FilterText.AsSpan();
                        patternScore = scorer.ScoreWord(pattern, word, out matchedSpans);
                        if (patternScore <= 0) continue;
                    }
                    else
                    {
                        patternScore = 0;
                        matchedSpans = ImmutableArray<Microsoft.VisualStudio.Text.Span>.Empty;
                    }

                    var filterMask = filterManager.CreateFilterMask(completion.Filters);
                    availableFilters |= filterManager.blacklistFilters & filterMask; // Announce filter availability.
                    if (filterManager.HasBlacklistedFilter(filterMask)) continue;

                    availableFilters |= filterManager.whitelistFilters & filterMask; // Announce filter availability.
                    if (!filterManager.HasWhitelistedFilter(filterMask)) continue;

                    int roslynScore = GetRoslynScore(completion);

                    int defaultIndex = data.Defaults.IndexOf(completion.FilterText);
                    if (defaultIndex == -1) defaultIndex = int.MaxValue;

                    var key = new CompletionItemKey
                    {
                        patternScore = patternScore,
                        roslynScore = roslynScore,
                        defaultIndex = defaultIndex,
                        initialIndex = i
                    };
#if INCLUDE_DEBUG_SUFFIX
                    AddDebugSuffix(ref completion, in key);
#endif
                    eligibleCompletions[n_eligibleCompletions] = new CompletionItemWithHighlight(completion, matchedSpans);
                    eligibleKeys[n_eligibleCompletions] = key;
                    n_eligibleCompletions++;
                }

                Array.Sort(eligibleKeys, eligibleCompletions, 0, n_eligibleCompletions);

                filterStates = UpdateFilterStates(filterStates, availableFilters);
            }

            UpdateSelectionHint GetSelectionKind()
            {
                if (n_eligibleCompletions == 0)
                    return UpdateSelectionHint.NoChange;

                if (hasTextFilter && !data.DisplaySuggestionItem)
                    return UpdateSelectionHint.Selected;

                var bestKey = eligibleKeys[0];

                if (bestKey.roslynScore >= MatchPriority.Preselect)
                    return UpdateSelectionHint.Selected;

                //if (bestKey.defaultIndex < int.MaxValue)
                //    return UpdateSelectionHint.Selected;

                return UpdateSelectionHint.SoftSelected;
            }
        }

        private int GetRoslynScore(VSCompletionItem completion)
        {
            var roslynObject = completion.Properties.GetProperty("RoslynCompletionItemData");

            if (roslynObject == null) return 0;

            var roslynCompletion = GetRoslynItemProperty(roslynObject);
            int roslynScore = roslynCompletion.Rules.MatchPriority;

            return roslynScore;
        }

        // Since we do not have compile time access the class type:
        // "Microsoft.CodeAnalysis.Editor.Implementation.IntelliSense.AsyncCompletion.CompletionItemData",
        // we have to use reflection or expressions to access it.
        private static Func<object, RoslynCompletionItem> RoslynCompletionItemGetter = null;
        private RoslynCompletionItem GetRoslynItemProperty(object roslynObject)
        {
            if (RoslynCompletionItemGetter == null)
            {
                var input    = Expression.Parameter(typeof(object));
                var casted   = Expression.Convert(input, roslynObject.GetType());
                var property = Expression.PropertyOrField(casted, "RoslynItem");
                var lambda   = Expression.Lambda(property, input);
                RoslynCompletionItemGetter = (Func<object, RoslynCompletionItem>)lambda.Compile();
            }

            return RoslynCompletionItemGetter.Invoke(roslynObject);
        }

        private ImmutableArray<CompletionFilterWithState> UpdateFilterStates(ImmutableArray<CompletionFilterWithState> filterStates, BitField64 availableFilters)
        {
            var builder = ImmutableArray.CreateBuilder<CompletionFilterWithState>(filterStates.Length);
            for (int i = 0; i < filterStates.Length; i++)
            {
                var filterState = filterStates[i];
                var filter = filterState.Filter;
                builder.Add(new CompletionFilterWithState(filter, availableFilters.GetBit(i), filterState.IsSelected));
            }
            return builder.MoveToImmutable();
        }

        struct InitialComparer : IComparer<VSCompletionItem>
        {
            public int Compare(VSCompletionItem x, VSCompletionItem y)
            {
                var a = x.SortText; var b = y.SortText;
                int comp = 0;
                if (a.Length > 0 && b.Length > 0)
                {
                    comp = GetUnderscoreCount(a) - GetUnderscoreCount(b);
                }
                if (comp == 0)
                {
                    comp = string.Compare(a, b, ignoreCase: false);
                }
                return comp;
            }

            private int GetUnderscoreCount(string str)
            {
                int i = 0;
                while (i < str.Length && str[i] == '_')
                {
                    i++;
                }
                return i;
            }
        }

        struct CompletionItemKey : IComparable<CompletionItemKey>
        {
            public int patternScore;
            public int roslynScore;
            public int defaultIndex;
            public int initialIndex;

            public int CompareTo(CompletionItemKey other)
            {
                int comp = patternScore - other.patternScore;
                if (comp == 0)
                {
                    comp = other.roslynScore - roslynScore;
                }
                if (comp == 0)
                {
                    comp = defaultIndex - other.defaultIndex;
                }
                if (comp == 0) // If score is same, preserve initial ordering.
                {
                    comp = initialIndex - other.initialIndex;
                }
                return comp;
            }
        }

        struct CompletionFilterManager
        {
            CompletionFilter[] filters;
            public readonly BitField64 blacklistFilters;
            public readonly BitField64 whitelistFilters;
            BitField64 activeBlacklistFilters;
            BitField64 activeWhitelistFilters;

            enum CompletionFilterKind
            {
                Null, Blacklist, Whitelist
            }

            public CompletionFilterManager(ImmutableArray<CompletionFilterWithState> filterStates)
            {
                Assumes.True(filterStates.Length < 64);

                filters = new CompletionFilter[filterStates.Length];
                blacklistFilters = default;
                whitelistFilters = default;
                activeBlacklistFilters = default;
                activeWhitelistFilters = default;

                for (int i = 0; i < filterStates.Length; i++)
                {
                    var filterState = filterStates[i];
                    var filter = filterState.Filter;
                    this.filters[i] = filter;
                    var filterKind = GetFilterKind(i, filter);
                    switch (filterKind)
                    {
                        case CompletionFilterKind.Blacklist:
                            blacklistFilters.SetBit(i);
                            break;

                        case CompletionFilterKind.Whitelist:
                            whitelistFilters.SetBit(i);
                            break;

                        default: throw new Exception();
                    }
                }

                CompletionFilterKind GetFilterKind(int index, CompletionFilter filter)
                {
                    // Is there a safer rule to determine what kind of filter it is?
                    return index == 0 ? CompletionFilterKind.Blacklist : CompletionFilterKind.Whitelist;
                }
            }

            public void UpdateActiveFilters(ImmutableArray<CompletionFilterWithState> filterStates)
            {
                Debug.Assert(filterStates.Length == filters.Length);

                BitField64 selectedFilters = default;
                for (int i = 0; i < filterStates.Length; i++)
                {
                    if (filterStates[i].IsSelected)
                    {
                        selectedFilters.SetBit(i);
                    }
                }

                activeBlacklistFilters = ~selectedFilters & blacklistFilters;
                activeWhitelistFilters = selectedFilters & whitelistFilters;
                if (activeWhitelistFilters == default)
                {
                    // No active whitelist = everything on whitelist.
                    activeWhitelistFilters = whitelistFilters;
                }
            }

            public BitField64 CreateFilterMask(ImmutableArray<CompletionFilter> completionFilters)
            {
                BitField64 mask = default;
                for (int i = 0; i < completionFilters.Length; i++)
                {
                    int index = Array.IndexOf(filters, completionFilters[i]);
                    Debug.Assert(index >= 0);
                    mask.SetBit(index);
                }
                return mask;
            }

            public bool HasBlacklistedFilter(BitField64 completionFilters)
            {
                bool isOnBlacklist = (activeBlacklistFilters & completionFilters) != default;
                return isOnBlacklist;
            }

            public bool HasWhitelistedFilter(BitField64 completionFilters)
            {
                bool isOnWhitelist = (activeWhitelistFilters & completionFilters) != default;
                return isOnWhitelist;
            }

            public bool PassesActiveFilters(BitField64 completionFilters)
            {
                return !HasBlacklistedFilter(completionFilters) && HasWhitelistedFilter(completionFilters);
            }
        }

        [Conditional("INCLUDE_DEBUG_SUFFIX")]
        private void AddDebugSuffix(ref VSCompletionItem completion, in CompletionItemKey key)
        {
            var patternScoreString = key.patternScore == 0 ? "-" : key.patternScore.ToString();
            var roslynScoreString = key.roslynScore == 0 ? "-" : key.roslynScore.ToString();
            var defaultIndexString = key.defaultIndex == int.MaxValue ? "-" : key.defaultIndex.ToString();
            var debugSuffix = $@" (ptrnScr: {patternScoreString}, rslnScr: {roslynScoreString}, dfltIdx: {defaultIndexString}, initIdx: {key.initialIndex})";
            completion = new VSCompletionItem
            (
                completion.DisplayText,
                completion.Source,
                completion.Icon,
                completion.Filters,
                completion.Suffix + debugSuffix,
                completion.InsertText,
                completion.SortText,
                completion.FilterText,
                completion.AutomationText,
                completion.AttributeIcons,
                completion.CommitCharacters,
                completion.ApplicableToSpan,
                completion.IsCommittedAsSnippet,
                completion.IsPreselected
            );
        }
    }
}