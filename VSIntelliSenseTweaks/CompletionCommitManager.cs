using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics.Metrics;
using Microsoft.VisualStudio.Package;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio;
using System.Threading;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft;
using System.Diagnostics;
using MSXML;
using VSIntelliSenseTweaks.Utilities;

namespace VSIntelliSenseTweaks
{
    //[Export(typeof(IAsyncCompletionCommitManagerProvider))]
    //[ContentType("CSharp")]
    //[Name("CustomCompletionCommitManagerProvider")]
    internal class CompletionCommitManagerProvider : IAsyncCompletionCommitManagerProvider
    {
        [Import]
        internal IVsEditorAdaptersFactoryService Adapter { get; set; }

        [Import]
        internal SVsServiceProvider ServiceProvider { get; set; }

        public IAsyncCompletionCommitManager GetOrCreate(ITextView textView)
        {
            var vsTextView = Adapter.GetViewAdapter(textView);
            var dte = (DTE)ServiceProvider.GetService(typeof(DTE));

            return new CompletionCommitManager(vsTextView, dte);
        }
    }

    internal class CompletionCommitManager : IAsyncCompletionCommitManager
    {
        internal IVsTextView vsTextView;
        internal IVsExpansion ex;
        internal IVsExpansionManager exManager;
        internal Guid languageGuid;
        internal DTE dte;

        public CompletionCommitManager(IVsTextView vsTextView, DTE dte)
        {
            this.vsTextView = vsTextView;
            vsTextView.GetBuffer(out var ppBuffer);
            var vsTextBuffer = (IVsTextBuffer)ppBuffer;
            //tm.GetExpansionManager(out exManager);
            this.ex = (IVsExpansion)ppBuffer;
            ppBuffer.GetLanguageServiceID(out this.languageGuid);
            this.dte = dte;
        }

        public IEnumerable<char> PotentialCommitCharacters => new Char[]
        {
            ' ', '.', ',',
            '!', '?',
            '\'', '"',
            '+', '-', '*', '/', '=',
            '&', '|', '^', '~',
            '<', '>', '(', ')', '[', ']'
        };

        public bool ShouldCommitCompletion(IAsyncCompletionSession session, SnapshotPoint location, char typedChar, CancellationToken token)
        {
            return true;
        }

        public CommitResult TryCommit(IAsyncCompletionSession session, ITextBuffer buffer, CompletionItem item, char typedChar, CancellationToken token)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (item.IsCommittedAsSnippet)
            {
                throw new Exception();
            }

            var trackingSpan = session.ApplicableToSpan;
            var snapshot = buffer.Replace(trackingSpan.GetSpan(buffer.CurrentSnapshot), item.InsertText);
            var currentSpan = trackingSpan.GetSpan(snapshot);
            TextSpan commitSpan = currentSpan.GetTextSpan();
            //dte.ExecuteCommand("Edit.InsertSnippet");
            IVsExpansionSession expSess;
            //IVsExpansionClient expClient = new ExClient();
            var insertSnippetResult = ex.InsertExpansion
            (
                commitSpan,
                commitSpan,
                default(IVsExpansionClient),
                languageGuid,
                out expSess
            );
            if (insertSnippetResult == VSConstants.S_OK)
            {
                return CommitResult.Handled;
            }
            return CommitResult.Unhandled;
        }

        //public class ExClient : IVsExpansionClient
        //{
        //    public int GetExpansionFunction(IXMLDOMNode xmlFunctionNode, string bstrFieldName, out IVsExpansionFunction pFunc)
        //    {
        //        return VSConstants.S_OK;
        //    }

        //    public int FormatSpan(IVsTextLines pBuffer, TextSpan[] ts)
        //    {
        //        throw new NotImplementedException();
        //    }

        //    public int EndExpansion()
        //    {
        //        throw new NotImplementedException();
        //    }

        //    public int IsValidType(IVsTextLines pBuffer, TextSpan[] ts, string[] rgTypes, int iCountTypes, out int pfIsValidType)
        //    {
        //        throw new NotImplementedException();
        //    }

        //    public int IsValidKind(IVsTextLines pBuffer, TextSpan[] ts, string bstrKind, out int pfIsValidKind)
        //    {
        //        throw new NotImplementedException();
        //    }

        //    public int OnBeforeInsertion(IVsExpansionSession pSession)
        //    {
        //        throw new NotImplementedException();
        //    }

        //    public int OnAfterInsertion(IVsExpansionSession pSession)
        //    {
        //        throw new NotImplementedException();
        //    }

        //    public int PositionCaretForEditing(IVsTextLines pBuffer, TextSpan[] ts)
        //    {
        //        throw new NotImplementedException();
        //    }

        //    public int OnItemChosen(string pszTitle, string pszPath)
        //    {
        //        throw new NotImplementedException();
        //    }
        //}
    }
}