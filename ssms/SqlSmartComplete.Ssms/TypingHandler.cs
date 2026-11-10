using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.Commanding.Commands;
using Microsoft.VisualStudio.Utilities;

namespace SqlSmartComplete.Ssms
{
    /// <summary>
    /// Keyboard handling that runs before the editor's completion handler:
    ///  - a space, punctuation, Enter or a paste closes a suggestion list that is still open,
    ///    so the next word starts a fresh list instead of filtering the old one;
    ///  - a fully typed or pasted name that exactly matches one table or column (any case)
    ///    is completed as if it had been picked with Tab: customers -> Customers c.
    /// </summary>
    [Export(typeof(ICommandHandler))]
    [Name("SqlSmartComplete typing")]
    [ContentType("SQL")]
    [TextViewRole(PredefinedTextViewRoles.Interactive)]
    [Order(Before = PredefinedCompletionNames.CompletionCommandHandler)]
    internal sealed class SqlTypingHandler :
        IChainedCommandHandler<TypeCharCommandArgs>,
        IChainedCommandHandler<ReturnKeyCommandArgs>,
        IChainedCommandHandler<TabKeyCommandArgs>,
        IChainedCommandHandler<PasteCommandArgs>
    {
        [Import] internal IAsyncCompletionBroker Broker = null;

        public string DisplayName => "SQL Smart Complete";

        // Characters that end a word and close an open list.
        private static bool BreaksWord(char c) =>
            char.IsWhiteSpace(c) || ",;()=<>+-*/%'!|&^~".IndexOf(c) >= 0;

        // Characters after which an exactly typed name is completed.
        private static bool CompletesWord(char c) =>
            c == ' ' || c == '\t' || c == ',' || c == ';' || c == ')' || c == '=';

        private static bool Active(ITextBuffer buffer) => Settings.Enabled && SqlBuffers.IsSql(buffer);

        public CommandState GetCommandState(TypeCharCommandArgs args, Func<CommandState> next) => next();
        public CommandState GetCommandState(ReturnKeyCommandArgs args, Func<CommandState> next) => next();
        public CommandState GetCommandState(TabKeyCommandArgs args, Func<CommandState> next) => next();
        public CommandState GetCommandState(PasteCommandArgs args, Func<CommandState> next) => next();

        public void ExecuteCommand(TypeCharCommandArgs args, Action next, CommandExecutionContext context)
        {
            try
            {
                char c = args.TypedChar;
                if (BreaksWord(c) && Active(args.SubjectBuffer))
                {
                    Dismiss(args.TextView);
                    if (CompletesWord(c)) AutoComplete.TryAtCaret(args.TextView, args.SubjectBuffer);
                }
            }
            catch (Exception ex) { Log.Write("TypeChar handler failed: " + ex.Message); }
            next();
        }

        public void ExecuteCommand(ReturnKeyCommandArgs args, Action next, CommandExecutionContext context)
        {
            try
            {
                if (Active(args.SubjectBuffer) && !HasHardSelection(args.TextView))
                {
                    Dismiss(args.TextView);
                    AutoComplete.TryAtCaret(args.TextView, args.SubjectBuffer);
                }
            }
            catch (Exception ex) { Log.Write("Return handler failed: " + ex.Message); }
            next();
        }

        public void ExecuteCommand(TabKeyCommandArgs args, Action next, CommandExecutionContext context)
        {
            try
            {
                // With a list open, Tab commits the selected item as usual.
                if (Active(args.SubjectBuffer) && !HasAnySelection(args.TextView))
                {
                    Dismiss(args.TextView);
                    if (AutoComplete.TryAtCaret(args.TextView, args.SubjectBuffer)) return; // Tab consumed
                }
            }
            catch (Exception ex) { Log.Write("Tab handler failed: " + ex.Message); }
            next();
        }

        public void ExecuteCommand(PasteCommandArgs args, Action next, CommandExecutionContext context)
        {
            ITrackingPoint start = null;
            try
            {
                if (Active(args.SubjectBuffer))
                {
                    Dismiss(args.TextView);
                    var sel = args.TextView.Selection.Start.Position;
                    var p = args.TextView.BufferGraph.MapDownToBuffer(sel, PointTrackingMode.Negative, args.SubjectBuffer, PositionAffinity.Predecessor);
                    if (p.HasValue) start = p.Value.Snapshot.CreateTrackingPoint(p.Value.Position, PointTrackingMode.Negative);
                }
            }
            catch (Exception ex) { Log.Write("Paste handler failed: " + ex.Message); }

            next();

            try
            {
                if (start == null) return;
                Dismiss(args.TextView);
                var caret = AutoComplete.Caret(args.TextView, args.SubjectBuffer);
                if (caret == null) return;
                var snap = caret.Value.Snapshot;
                int from = start.GetPosition(snap), to = caret.Value.Position;
                if (to <= from || to - from > 300) return;

                // Only a single name, possibly qualified or bracketed: dbo.Customers, [Sales].[Order Lines]
                string pasted = snap.GetText(from, to - from);
                string name = pasted.TrimEnd();
                if (!PastedName.IsMatch(name.TrimStart())) return;
                AutoComplete.TryAt(new SnapshotPoint(snap, from + name.Length), pasted.Length != name.Length);
            }
            catch (Exception ex) { Log.Write("Paste completion failed: " + ex.Message); }
        }

        private static readonly Regex PastedName = new Regex(@"^(?:(?:\[[^\]]+\]|[\p{L}_#][\p{L}\p{N}_@#$]*)\.){0,3}(?:\[[^\]]+\]|[\p{L}_#][\p{L}\p{N}_@#$]*)$");

        private void Dismiss(ITextView view)
        {
            var s = Broker?.GetSession(view);
            if (s != null && !s.IsDismissed) s.Dismiss();
        }

        private bool HasHardSelection(ITextView view)
        {
            var s = Broker?.GetSession(view);
            if (s == null || s.IsDismissed) return false;
            var items = s.GetComputedItems(CancellationToken.None);
            return items?.SelectedItem != null && !items.UsesSoftSelection && !items.SuggestionItemSelected;
        }

        private bool HasAnySelection(ITextView view)
        {
            var s = Broker?.GetSession(view);
            if (s == null || s.IsDismissed) return false;
            var items = s.GetComputedItems(CancellationToken.None);
            return items?.SelectedItem != null;
        }
    }

    /// <summary>Completes an exactly typed name the same way picking it from the list would.</summary>
    internal static class AutoComplete
    {
        private static readonly Regex NextWord = new Regex(@"\G[ \t]*([\p{L}_][\p{L}\p{N}_]*)");

        private static bool IdentChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == '#' || c == '$';

        public static SnapshotPoint? Caret(ITextView view, ITextBuffer buffer) =>
            view.Caret.Position.Point.GetPoint(buffer, PositionAffinity.Predecessor);

        public static bool TryAtCaret(ITextView view, ITextBuffer buffer)
        {
            var p = Caret(view, buffer);
            return p != null && TryAt(p.Value, false);
        }

        /// <param name="followedBySpace">The pasted text ended in whitespace, so the name is already complete.</param>
        public static bool TryAt(SnapshotPoint point, bool followedBySpace)
        {
            if (!Settings.AutoComplete) return false;
            var snap = point.Snapshot;
            int off = point.Position;
            if (off < snap.Length && IdentChar(snap[off])) return false; // in the middle of a word

            string text = snap.GetText();
            var a = SqlParser.Analyze(text, off);
            if (a.Kind == ContextKind.None || a.Prefix.Length == 0) return false;

            var conn = SsmsConnection.TryGetActive();
            string db = a.Database ?? conn?.Database;
            MetadataIndex idx = conn != null && !string.IsNullOrEmpty(db) ? MetadataStore.TryGetLoaded(conn, db) : null;

            var match = CompletionEngine.ExactMatch(a, idx, Settings.Engine);
            if (match == null) return false;

            string insert = match.InsertText;
            bool tableLike = match.Kind != SuggestionKind.Column;
            var span = new Span(a.ReplaceStart, off - a.ReplaceStart);
            if (snap.GetText(span) == insert) return false;

            // "FROM customers cu": the user already wrote an alias, so only fix the name's case.
            var nw = NextWord.Match(text, followedBySpace ? SkipSpace(text, off) : off);
            if (tableLike && nw.Success && !SqlKeywords.Reserved.Contains(nw.Groups[1].Value.ToUpperInvariant()))
            {
                string name = Strip(match.FilterText ?? match.Label);
                int at = insert.IndexOf(name, StringComparison.OrdinalIgnoreCase);
                if (at < 0) return false;
                int end = at + name.Length;
                if (end < insert.Length && insert[end] == ']') end++;
                insert = insert.Substring(0, end);
                if (snap.GetText(span) == insert) return false;
            }

            snap.TextBuffer.Replace(span, insert);
            Log.Write($"Auto-completed '{snap.GetText(span)}' -> '{insert}'");
            return true;
        }

        private static int SkipSpace(string text, int i)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            return i;
        }

        private static string Strip(string s) => s.Trim('[', ']');
    }
}
