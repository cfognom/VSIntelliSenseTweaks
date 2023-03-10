using Microsoft;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using VSIntelliSenseTweaks.Utilities;
using Microsoft.VisualStudio.Text;

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
        private struct CompletionItemKey : IComparable<CompletionItemKey>
        {
            public int score;
            public int index;
            //public Subwords subwords;

            public int CompareTo(CompletionItemKey other)
            {
                int comp = score - other.score;
                if (comp == 0) // If score is same, preserve initial ordering.
                {
                    comp = index - other.index;
                }
                return comp;
            }
        }

        private struct Subword
        {
            public int startIndex;
            public int length;

            public ReadOnlySpan<char> SliceOf(ReadOnlySpan<char> word)
            {
                return word.Slice(startIndex, length);
            }
        }

        private struct Subwords
        {
            public int startIndex; // Start index in subwordBeginnings array.
            public int length; // Length in subwordBeginnings array.

            public void GetSubwords(ReadOnlySpan<char> word, List<byte> subwordBeginnings, Span<Subword> subwords)
            {
                Debug.Assert(subwords.Length == length);
                for (int i = 0; i < length; i++)
                {
                    int subwordStart = subwordBeginnings[startIndex + i];
                    int subwordStop = i + 1 == length ? word.Length : subwordBeginnings[startIndex + i + 1];

                    subwords[i] = new Subword
                    {
                        startIndex = subwordStart,
                        length = subwordStop - subwordStart
                    };
                }
            }
        }

        const int textFilterMaxLength = 256;

        CompletionItemWithHighlight[] eligibleCompletions;
        CompletionItemKey[] eligibleKeys;
        WordScorer scorer;

        private struct Result
        {
            public FilteredCompletionModel completionModel;
            public ITextVersion textVersion;
        }
        Result result;

        CompletionFilterManager filterManager;
        bool hasFilterManager;

        Task<FilteredCompletionModel> updateTask;

        private void PerformInitialAllocations(int n_completions, int maxCharCount)
        {
            //Debug.Assert(filteredCompletions == null); // Check that we did not allocate already.
            this.eligibleCompletions = new CompletionItemWithHighlight[n_completions];
            this.eligibleKeys      = new CompletionItemKey[n_completions];
            this.scorer = new WordScorer(maxCharCount);
        }

        //private void DetermineSubwords(ImmutableArray<CompletionItem> completions)
        //{
        //    Span<CharKind> charKinds = stackalloc CharKind[patternMaxLength];
        //    for (int i = 0; i < completions.Length; i++)
        //    {
        //        var word = completions[i].FilterText.AsSpan(0, patternMaxLength);
        //        subwords[i] = DetermineSubwords(word, charKinds, subwordBeginnings);
        //    }
        //}

        public Task<ImmutableArray<CompletionItem>> SortCompletionListAsync(IAsyncCompletionSession session, AsyncCompletionSessionInitialDataSnapshot data, CancellationToken token)
        {
            var sortTask = Task.Factory.StartNew(
                () => SortCompletionList(session, data, token),
                token,
                TaskCreationOptions.None,
                TaskScheduler.Current
            );
            return sortTask;
        }

        public ImmutableArray<CompletionItem> SortCompletionList(IAsyncCompletionSession session, AsyncCompletionSessionInitialDataSnapshot data, CancellationToken token)
        {
            var completions = data.InitialItemList;
            int n_completions = completions.Count;

            var builder = ImmutableArray.CreateBuilder<CompletionItem>(n_completions);
            int maxCharCount = 0;
            for (int i = 0; i < n_completions; i++)
            {
                var completion = completions[i];
                builder.Add(completion);
                maxCharCount = Math.Max(maxCharCount, completion.FilterText.Length);
            }
            builder.Sort(new InitialComparer());
            var initialSortedItems = builder.MoveToImmutable();

            PerformInitialAllocations(n_completions, maxCharCount);

            this.hasFilterManager = false;

            //DetermineSubwords(initialSortedItems);

            //#if !DEBUG
            //            // Manual update to hard-select top entry.
            //            session.OpenOrUpdate(data.Trigger, session.ApplicableToSpan.GetStartPoint(data.Snapshot), token);
            //#endif

            return initialSortedItems;
        }

        public Task<FilteredCompletionModel> UpdateCompletionListAsync(IAsyncCompletionSession session, AsyncCompletionSessionDataSnapshot data, CancellationToken token)
        {
            Assumes.True(
                updateTask == null || updateTask.IsCompleted,
                "Tried to update completion list while previous update was not completed."
            );

            updateTask = Task.Factory.StartNew(
                () => UpdateCompletionList(session, data, token),
                token,
                TaskCreationOptions.None,
                TaskScheduler.Current
            );
            return updateTask;
        }

        public FilteredCompletionModel UpdateCompletionList(IAsyncCompletionSession session, AsyncCompletionSessionDataSnapshot data, CancellationToken token)
        {
            var completions = data.InitialSortedItemList;
            int n_completions = completions.Count;

            var filterStates = data.SelectedFilters; // The types of filters displayed in the IntelliSense widget.
            if (!hasFilterManager)
            {
                filterManager = new CompletionFilterManager(filterStates);
            }
            filterManager.UpdateActiveFilters(filterStates);

            int preselectionIndex = -1;
            var textFilter = session.ApplicableToSpan.GetText(data.Snapshot);
            bool hasTextFilter = textFilter.Length > 0;
            int n_eligibleCompletions = 0;
            if (hasTextFilter)
            {
                AddEligibleCompletionsWithTextFilter();
            }
            else
            {
                AddEligibleCompletionsWithoutTextFilter();
            }

            int selectionIndex = Math.Max(preselectionIndex, 0);

            var currentTextVersion = data.Snapshot.Version;
            // If we type in a string that do not produce any completions we keep old result.
            bool keepOldResult = n_eligibleCompletions == 0 && result.textVersion != currentTextVersion;
            if (keepOldResult) return result.completionModel;

            result = new Result
            {
                completionModel = new FilteredCompletionModel
                (
                    ImmutableArray.Create(eligibleCompletions, 0, n_eligibleCompletions),
                    selectionIndex,
                    filterStates,
                    hasTextFilter ? UpdateSelectionHint.Selected : UpdateSelectionHint.SoftSelected,
                    centerSelection: false,
                    null
                ),
                textVersion = currentTextVersion
            };

            Debug.Assert(!token.IsCancellationRequested);

            return result.completionModel;

            void AddEligibleCompletionsWithTextFilter()
            {
                var pattern = textFilter.AsSpan(0, Math.Min(textFilter.Length, textFilterMaxLength));
                BitField64 availableFilters = default;
                for (int i = 0; i < n_completions; i++)
                {
                    var completion = completions[i];

                    var word = completion.FilterText.AsSpan();
                    int wordScore = scorer.ScoreWord(pattern, word, out var matchedSpans);
                    if (wordScore == 0) continue;

                    var filterMask = filterManager.CreateFilterMask(completion.Filters);
                    availableFilters |= filterManager.blacklistFilters & filterMask; // Announce availability.
                    if (filterManager.HasBlacklistedFilter(filterMask)) continue;

                    availableFilters |= filterManager.whitelistFilters & filterMask; // Announce availability.
                    if (!filterManager.HasWhitelistedFilter(filterMask)) continue;

                    //completion = new CompletionItem(
                    //    completion.DisplayText,
                    //    completion.Source,
                    //    completion.Icon,
                    //    completion.Filters,
                    //    "test",
                    //    completion.InsertText,
                    //    completion.SortText,
                    //    completion.FilterText,
                    //    completion.AutomationText,
                    //    completion.AttributeIcons,
                    //    completion.CommitCharacters,
                    //    completion.ApplicableToSpan,
                    //    true,
                    //    completion.IsPreselected
                    //);
                    eligibleCompletions[n_eligibleCompletions] = new CompletionItemWithHighlight(completion, matchedSpans);
                    eligibleKeys[n_eligibleCompletions] = new CompletionItemKey { score = wordScore, index = i };
                    n_eligibleCompletions++;
                }

                Array.Sort(eligibleKeys, eligibleCompletions, 0, n_eligibleCompletions);

                filterStates = UpdateFilterStates(filterStates, availableFilters);
            }

            void AddEligibleCompletionsWithoutTextFilter()
            {
                for (int i = 0; i < n_completions; i++)
                {
                    var completion = completions[i];

                    var filterMask = filterManager.CreateFilterMask(completion.Filters);
                    if (!filterManager.PassesActiveFilters(filterMask)) continue;

                    if (preselectionIndex == -1 && completion.IsPreselected) preselectionIndex = i;
                    eligibleCompletions[n_eligibleCompletions] = new CompletionItemWithHighlight(completion);
                    n_eligibleCompletions++;
                }
            }
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

        struct InitialComparer : IComparer<CompletionItem>
        {
            public int Compare(CompletionItem x, CompletionItem y)
            {
                int comp = string.Compare(x.SortText, y.SortText, ignoreCase: false);
                //if (comp == 0)
                //{
                //    comp = string.Compare(x, y, ignoreCase: false);
                //}
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
    }
}