using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace CustomExtension
{
    [Export(typeof(IAsyncCompletionItemManagerProvider))]
    [ContentType("CSharp")]
    [Name("CustomCompletionManagerProvider")]
    [Order(After = "default")]
    internal class CustomCompletionManagerProvider : IAsyncCompletionItemManagerProvider
    {
        public IAsyncCompletionItemManager GetOrCreate(ITextView textView)
        {
            return new VSCodeLikeAsyncCompletionManager
            {
                textView = textView
            };
        }
    }

    internal class VSCodeLikeAsyncCompletionManager : IAsyncCompletionItemManager
    {
        internal ITextView textView;

        public Task<ImmutableArray<CompletionItem>> SortCompletionListAsync(IAsyncCompletionSession session, AsyncCompletionSessionInitialDataSnapshot data, CancellationToken token)
        {
            var result = data.InitialItemList
                .OrderBy(x => x.SortText, new InitialComparer())
                .ToImmutableArray();
            return Task.FromResult(result);
        }

        public Task<FilteredCompletionModel> UpdateCompletionListAsync(IAsyncCompletionSession session, AsyncCompletionSessionDataSnapshot data, CancellationToken token)
        {
            var potentialSuggestions = data.InitialSortedItemList;
            int n_suggestion = potentialSuggestions.Count;
            var suggestions = new CompletionItemWithHighlight[n_suggestion];
            var scores = new int[n_suggestion];
            var currentText = session.ApplicableToSpan.GetText(data.Snapshot).AsSpan();
            int successCount = 0;
            for (int i = 0; i < n_suggestion; i++)
            {
                var potentialSuggestion = potentialSuggestions[i];
                var filterText = potentialSuggestion.FilterText.AsSpan();
                if (SuggestWord(filterText, currentText, out var matchedSpans, out int score))
                {
                    suggestions[i] = new CompletionItemWithHighlight(potentialSuggestion, matchedSpans);
                    scores[i] = score;
                    successCount++;
                }
                else
                {
                    //scores[i] = int.MinValue;
                }
            }

            int j = 0;
            var immutableSuggestions = suggestions
                .OrderByDescending(x => scores[j++])
                .Take(successCount)
                .ToImmutableArray();

            var result = new FilteredCompletionModel
            (
                immutableSuggestions,
                0
            );
            return Task.FromResult(result);
        }

        struct InitialComparer : IComparer<string>
        {
            public int Compare(string x, string y)
            {
                return string.Compare(x, y, ignoreCase: true);
            }
        }

        struct ScoreComparer : IComparer<int>
        {
            public int Compare(int x, int y)
            {
                return y - x;
            }
        }

        bool SuggestWord(ReadOnlySpan<char> suggestionWord, ReadOnlySpan<char> caretWord, out ImmutableArray<Span> matchedSpans, out int score)
        {
            score = 0;
            if (caretWord.Length == 0)
            {
                matchedSpans = ImmutableArray<Span>.Empty;
                return true;
            }

            var spans = new List<Span>(caretWord.Length);

            int matchSpanStart = -1;
            for (int i = 0, j = 0; i < suggestionWord.Length; i++)
            {
                if (IsMatchingChar(suggestionWord[i], caretWord[j]))
                {
                    if (i == 0) score += 1; // First char bonus.
                    if (matchSpanStart == -1) matchSpanStart = i;
                    j++;
                    if (j == caretWord.Length)
                    {
                        int matchSpanLength = i + 1 - matchSpanStart;
                        AddSpan(matchSpanStart, matchSpanLength, ref score);
                        matchedSpans = spans.ToImmutableArray();
                        return true;
                    }
                }
                else if (matchSpanStart != -1)
                {
                    int matchSpanLength = i - matchSpanStart;
                    AddSpan(matchSpanStart, matchSpanLength, ref score);
                    matchSpanStart = -1;
                }

                void AddSpan(int start, int length, ref int _score)
                {
                    spans.Add(new Span(start, length));
                    _score += length * length;
                }

                bool IsMatchingChar(char suggestionChar, char caretChar)
                {
                    return char.ToLower(suggestionChar) == char.ToLower(caretChar);
                }
            }

            matchedSpans = spans.ToImmutableArray();
            return true;
        }
    }
}