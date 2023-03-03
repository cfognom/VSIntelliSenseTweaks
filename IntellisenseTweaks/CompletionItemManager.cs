using Microsoft;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO.Pipelines;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace IntellisenseTweaks
{
    [Export(typeof(IAsyncCompletionItemManagerProvider))]
    [ContentType("CSharp")]
    [Name("CustomCompletionItemManagerProvider")]
    internal class CompletionItemManagerProvider : IAsyncCompletionItemManagerProvider
    {
        public IAsyncCompletionItemManager GetOrCreate(ITextView textView)
        {
            return new CompletionItemManager(textView);
        }
    }

    internal class CompletionItemManager : IAsyncCompletionItemManager
    {
        internal struct ScoreAndIndex : IComparable<ScoreAndIndex>
        {
            public int score;
            public int index;

            public int CompareTo(ScoreAndIndex other)
            {
                int comp = other.score - score;
                if (comp == 0) // If score is same, preserve initial ordering.
                {
                    comp = index - other.index;
                }
                return comp;
            }
        }

        ITextView textView;
        CompletionItemWithHighlight[] filteredCompletions;
        ScoreAndIndex[] keys;
        int matchCount;
        FilteredCompletionModel completionModel;
        Task<FilteredCompletionModel> updateTask;

        [Import]
        IAsyncCompletionBroker completionBroker;

        public CompletionItemManager(ITextView textView)
        {
            this.textView = textView;
        }

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
            var initialCompletions = data.InitialItemList;
            int n_completions = initialCompletions.Count;

            Debug.Assert(filteredCompletions == null);
            //builder = ImmutableArray.CreateBuilder<CompletionItemWithHighlight>(result.Length);
            filteredCompletions = new CompletionItemWithHighlight[n_completions];
            keys = new ScoreAndIndex[n_completions];

            var builder = ImmutableArray.CreateBuilder<CompletionItem>(n_completions);
            for (int i = 0; i < n_completions; i++)
            {
                builder.Add(initialCompletions[i]);
            }
            builder.Sort(new InitialComparer());
            var initialSortedItems = builder.MoveToImmutable();


            
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
            int preselectionIndex = -1;
            var pattern = session.ApplicableToSpan.GetText(data.Snapshot);
            bool hasPattern = pattern.Length > 0;
            if (hasPattern)
            {
                var scorer = new WordScorer(pattern);
                matchCount = 0;
                for (int i = 0; i < n_completions; i++)
                {
                    var completion = completions[i];
                    var word = completion.FilterText.AsSpan();
                    int wordScore = scorer.ScoreWord(word, out var matchedSpans);
                    if (wordScore > 0)
                    {
                        filteredCompletions[matchCount] = new CompletionItemWithHighlight(completion, matchedSpans);
                        keys[matchCount] = new ScoreAndIndex { score = wordScore, index = i };
                        matchCount++;
                    }
                }

                Array.Sort(keys, filteredCompletions, 0, matchCount);
            }
            else
            {
                for (int i = 0; i < n_completions; i++)
                {
                    var completion = completions[i];
                    if (preselectionIndex == -1 && completion.IsPreselected) preselectionIndex = i;
                    filteredCompletions[i] = new CompletionItemWithHighlight(completion);
                }
                matchCount = n_completions;
            }

            int selectionIndex = Math.Max(preselectionIndex, 0);

            if (matchCount > 0)
            {
                completionModel = new FilteredCompletionModel
                (
                    ImmutableArray.Create(filteredCompletions, 0, matchCount),
                    selectionIndex,
                    data.SelectedFilters,
                    hasPattern ? UpdateSelectionHint.Selected : UpdateSelectionHint.SoftSelected,
                    centerSelection: false,
                    null
                );
            }
            token.ThrowIfCancellationRequested();
            return completionModel;
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

        public ref struct WordScorer
        {
            ReadOnlySpan<char> pattern;
            ReadOnlySpan<char> word;
            ScoredSpan staged;
            int wordScore;
            int tempSpansCount;
            Span[] tempSpans;

            public WordScorer(string pattern)
            {
                Debug.Assert(pattern != null && pattern.Length > 0);
                this.pattern = pattern.AsSpan();
                this.word = default;
                this.staged = default;
                this.wordScore = default;
                this.tempSpansCount = default;
                this.tempSpans = new Span[pattern.Length];
            }

            public int ScoreWord(ReadOnlySpan<char> word, out ImmutableArray<Span> matchedSpans)
            {
                this.word = word;
                this.staged = default;
                this.wordScore = 0;
                this.tempSpansCount = 0;

                var current = new ScoredSpan { start = -1 };
                int previousSpanEnd = 0;
                for (int i = 0, j = 0; i < word.Length; i++)
                {
                    int charScore = GetCharScore(word[i], pattern[j]);
                    if (charScore > 0) // Char match.
                    {
                        //if (i == 0) wordScore += 1; // First char bonus.
                        if (current.start == -1) // Start span if there is none.
                        {
                            current.start = i;
                            current.frontGap = i - previousSpanEnd;
                        }

                        current.charScore += charScore;
                        j++;

                        if (j == pattern.Length)  // Matched all chars in pattern, end final span.
                        {
                            current.length = i + 1 - current.start;
                            PushSpan(current);
                            PushStagedSpan(); // Make sure staged span is included before we build array.
                            matchedSpans = ImmutableArray.Create(tempSpans, 0, tempSpansCount);
                            return wordScore;
                        }
                    }
                    else // No char match.
                    {
                        if (current.start != -1) // End span if its open.
                        {
                            current.length = i - current.start;
                            PushSpan(current);
                            current.charScore = 0;
                            current.start = -1;
                            previousSpanEnd = i;
                        }
                    }
                }

                // Not all chars in pattern were matched.
                matchedSpans = ImmutableArray<Span>.Empty;
                return 0;
            }

            void PushSpan(in ScoredSpan pushed)
            {
                int mergedStart = pushed.start - staged.length;
                bool canMergeStagedSpan = word.Slice(staged.start, staged.length)
                    .SequenceEqual(word.Slice(mergedStart, staged.length));
                if (canMergeStagedSpan)
                {
                    staged.start = mergedStart;
                    staged.length += pushed.length;
                    staged.charScore += pushed.charScore;
                    staged.frontGap += pushed.frontGap;
                }
                else
                {
                    PushStagedSpan();
                    staged = pushed;
                }
            }

            void PushStagedSpan()
            {
                wordScore += CalculateSpanScore(staged);
                tempSpans[tempSpansCount++] = new Span(staged.start, staged.length);
            }

            private static int GetCharScore(char completionChar, char patternChar)
            {
                if (completionChar == patternChar)
                    return 2;
                else if (char.ToLower(completionChar) == char.ToLower(patternChar))
                    return 1;
                else
                    return 0;
            }

            private bool IsSubwordStart(int index)
            {
                return index == 0
                    || (char.IsLower(word[index - 1]) && char.IsUpper(word[index]))
                    || (!char.IsLetter(word[index - 1]) && char.IsLetter(word[index]))
                    || (index + 1 < word.Length && char.IsUpper(word[index]) && char.IsLower(word[index + 1]));
            }

            private int CalculateSpanScore(in ScoredSpan span)
            {
                int gapPenalty = Math.Min(span.frontGap, 255);
                int subwordStartMultiplier = IsSubwordStart(span.start) ? 2 : 1;
                int lengthScore = 256 * subwordStartMultiplier * (2 * span.charScore - 1);
                // By making lengthScore a multiple of 256 and keeping gapPenalty under 256,
                // gap penalty will only be relevant when when lengthScore is same.
                return lengthScore - gapPenalty;
            }

            private struct ScoredSpan
            {
                public int start;
                public int length;
                public int charScore;
                public int frontGap;
            }
        }
    }
}