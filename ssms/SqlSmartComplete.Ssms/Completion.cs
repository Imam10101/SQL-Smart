using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.VisualStudio.Core.Imaging;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace SqlSmartComplete.Ssms
{
    internal static class SqlBuffers
    {
        private static readonly HashSet<string> Logged = new HashSet<string>();

        /// <summary>True for the SSMS T-SQL query editor buffer.</summary>
        public static bool IsSql(ITextBuffer buffer)
        {
            var ct = buffer?.ContentType;
            if (ct == null) return false;
            return Matches(ct, 0);
        }

        private static bool Matches(IContentType ct, int depth)
        {
            if (ct.TypeName.IndexOf("sql", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (depth > 4) return false;
            return ct.BaseTypes.Any(b => Matches(b, depth + 1));
        }

        public static void LogContentType(ITextBuffer buffer)
        {
            var ct = buffer?.ContentType;
            if (ct == null) return;
            lock (Logged)
            {
                if (!Logged.Add(ct.TypeName)) return;
            }
            Log.Write($"Editor opened with content type '{ct.TypeName}' (bases: {string.Join(", ", ct.BaseTypes.Select(b => b.TypeName))}); SQL = {IsSql(buffer)}");
        }
    }

    // ---------------------------------------------------------------------- source

    [Export(typeof(IAsyncCompletionSourceProvider))]
    [Name("SqlSmartComplete")]
    [ContentType("SQL")]
    internal sealed class SqlCompletionSourceProvider : IAsyncCompletionSourceProvider
    {
        public IAsyncCompletionSource GetOrCreate(ITextView textView) =>
            textView.Properties.GetOrCreateSingletonProperty(() => new SqlCompletionSource());
    }

    internal sealed class SqlCompletionSource : IAsyncCompletionSource
    {
        private static readonly HashSet<string> SpaceTriggers = new HashSet<string>
            { "FROM", "JOIN", "APPLY", "UPDATE", "INTO", "EXEC", "EXECUTE", "ON", "TABLE", "USING", "MERGE" };

        private Analysis pending;
        private ConnectionInfo pendingConnection;
        private int pendingOffset = -1;
        private ITextSnapshot pendingSnapshot;

        private static bool IdentChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == '#' || c == '$';

        public CompletionStartData InitializeCompletion(CompletionTrigger trigger, SnapshotPoint triggerLocation, CancellationToken token)
        {
            try
            {
                if (!Settings.Enabled || !SqlBuffers.IsSql(triggerLocation.Snapshot.TextBuffer))
                    return CompletionStartData.DoesNotParticipateInCompletion;

                char ch = trigger.Character;
                switch (trigger.Reason)
                {
                    case CompletionTriggerReason.Insertion:
                        if (!(IdentChar(ch) || ch == '.' || ch == ' ' || ch == '[')) return CompletionStartData.DoesNotParticipateInCompletion;
                        break;
                    case CompletionTriggerReason.Invoke:
                    case CompletionTriggerReason.InvokeAndCommitIfUnique:
                        break;
                    default:
                        return CompletionStartData.DoesNotParticipateInCompletion;
                }

                var snapshot = triggerLocation.Snapshot;
                int offset = triggerLocation.Position;
                var a = SqlParser.Analyze(snapshot.GetText(), offset);
                if (a.Kind == ContextKind.None) return CompletionStartData.DoesNotParticipateInCompletion;

                if (trigger.Reason == CompletionTriggerReason.Insertion && ch == ' ' &&
                    (a.Prefix.Length > 0 || a.Qualifier.Count > 0 || !SpaceTriggers.Contains(a.PrevWord ?? "")))
                    return CompletionStartData.DoesNotParticipateInCompletion;

                pending = a;
                pendingOffset = offset;
                pendingSnapshot = snapshot;
                pendingConnection = ThreadHelper.CheckAccess() ? SsmsConnection.TryGetActive() : Settings.Fallback;

                return new CompletionStartData(CompletionParticipation.ProvidesItems,
                    new SnapshotSpan(snapshot, a.ReplaceStart, offset - a.ReplaceStart));
            }
            catch (Exception ex)
            {
                Log.Write("InitializeCompletion failed: " + ex);
                return CompletionStartData.DoesNotParticipateInCompletion;
            }
        }

        public async Task<CompletionContext> GetCompletionContextAsync(IAsyncCompletionSession session, CompletionTrigger trigger,
            SnapshotPoint triggerLocation, SnapshotSpan applicableToSpan, CancellationToken token)
        {
            try
            {
                var a = pending;
                if (a == null || pendingSnapshot != triggerLocation.Snapshot || pendingOffset != triggerLocation.Position)
                    a = SqlParser.Analyze(triggerLocation.Snapshot.GetText(), triggerLocation.Position);
                var conn = pendingConnection;

                MetadataIndex idx = null;
                string db = a.Database ?? conn?.Database;
                if (conn != null && !string.IsNullOrEmpty(db))
                {
                    var load = MetadataStore.GetAsync(conn, db);
                    var done = await Task.WhenAny(load, Task.Delay(5000, token)).ConfigureAwait(false);
                    if (done == load) idx = load.Result;
                }

                var items = CompletionEngine.GetSuggestions(a, idx, Settings.Engine)
                    .Select(s => ToItem(s))
                    .ToImmutableArray();
                return new CompletionContext(items);
            }
            catch (OperationCanceledException)
            {
                return CompletionContext.Empty;
            }
            catch (Exception ex)
            {
                Log.Write("GetCompletionContextAsync failed: " + ex);
                return CompletionContext.Empty;
            }
        }

        public Task<object> GetDescriptionAsync(IAsyncCompletionSession session, CompletionItem item, CancellationToken token)
        {
            if (item.Properties.TryGetProperty(typeof(Suggestion), out Suggestion s))
            {
                string text = s.Documentation ?? s.Detail;
                if (s.Documentation != null && s.Detail != null && !s.Documentation.Contains(s.Detail)) text = s.Detail + "\n\n" + s.Documentation;
                if (s.Kind != SuggestionKind.Keyword && s.InsertText != s.Label) text = (text != null ? text + "\n\n" : "") + "Inserts: " + s.InsertText;
                return Task.FromResult<object>(text);
            }
            return Task.FromResult<object>(null);
        }

        private CompletionItem ToItem(Suggestion s)
        {
            var item = new CompletionItem(s.Label, this, Icon(s.Kind), ImmutableArray<CompletionFilter>.Empty,
                s.Description ?? "", s.InsertText, s.SortText, s.FilterText ?? s.Label, ImmutableArray<ImageElement>.Empty);
            item.Properties.AddProperty(typeof(Suggestion), s);
            return item;
        }

        private static readonly Dictionary<SuggestionKind, ImageElement> Icons = new Dictionary<SuggestionKind, ImageElement>
        {
            [SuggestionKind.Table] = Img(KnownImageIds.Table),
            [SuggestionKind.View] = Img(KnownImageIds.View),
            [SuggestionKind.Column] = Img(KnownImageIds.Column),
            [SuggestionKind.Procedure] = Img(KnownImageIds.StoredProcedure),
            [SuggestionKind.Function] = Img(KnownImageIds.ScalarFunction),
            [SuggestionKind.TableFunction] = Img(KnownImageIds.TableFunction),
            [SuggestionKind.Synonym] = Img(KnownImageIds.Synonym),
            [SuggestionKind.Schema] = Img(KnownImageIds.Schema),
            [SuggestionKind.Alias] = Img(KnownImageIds.LocalVariable),
            [SuggestionKind.Join] = Img(KnownImageIds.Join),
            [SuggestionKind.Keyword] = Img(KnownImageIds.IntellisenseKeyword),
            [SuggestionKind.Variable] = Img(KnownImageIds.LocalVariable),
            [SuggestionKind.Cte] = Img(KnownImageIds.TableGroup),
            [SuggestionKind.Temp] = Img(KnownImageIds.Table),
            [SuggestionKind.DataType] = Img(KnownImageIds.TypeDefinition),
            [SuggestionKind.Snippet] = Img(KnownImageIds.Snippet)
        };

        private static ImageElement Img(int id) => new ImageElement(new ImageId(KnownImageIds.ImageCatalogGuid, id));
        private static ImageElement Icon(SuggestionKind k) => Icons.TryGetValue(k, out var i) ? i : null;
    }

    // ---------------------------------------------------------------------- commit

    [Export(typeof(IAsyncCompletionCommitManagerProvider))]
    [Name("SqlSmartComplete commit")]
    [ContentType("SQL")]
    internal sealed class SqlCommitManagerProvider : IAsyncCompletionCommitManagerProvider
    {
        [Import] internal IAsyncCompletionBroker Broker = null;

        public IAsyncCompletionCommitManager GetOrCreate(ITextView textView) =>
            textView.Properties.GetOrCreateSingletonProperty(() => new SqlCommitManager(textView, Broker));
    }

    /// <summary>Places the caret inside "()" and re-opens the list after inserting "dbo.".</summary>
    internal sealed class SqlCommitManager : IAsyncCompletionCommitManager
    {
        private readonly ITextView view;
        private readonly IAsyncCompletionBroker broker;

        public SqlCommitManager(ITextView view, IAsyncCompletionBroker broker)
        {
            this.view = view;
            this.broker = broker;
        }

        public IEnumerable<char> PotentialCommitCharacters => Array.Empty<char>();

        public bool ShouldCommitCompletion(IAsyncCompletionSession session, SnapshotPoint location, char typedChar, CancellationToken token) => false;

        public CommitResult TryCommit(IAsyncCompletionSession session, ITextBuffer buffer, CompletionItem item, char typedChar, CancellationToken token)
        {
            if (!item.Properties.TryGetProperty(typeof(Suggestion), out Suggestion s)) return CommitResult.Unhandled;
            if (s.CaretOffset == null && !s.Retrigger) return CommitResult.Unhandled;
            try
            {
                var span = session.ApplicableToSpan.GetSpan(buffer.CurrentSnapshot);
                var after = buffer.Replace(span, s.InsertText);
                var point = new SnapshotPoint(after, span.Start.Position + (s.CaretOffset ?? s.InsertText.Length));
                var mapped = view.BufferGraph.MapUpToBuffer(point, PointTrackingMode.Positive, PositionAffinity.Successor, view.TextBuffer) ?? point;
                view.Caret.MoveTo(mapped);

                if (s.Retrigger)
                {
                    Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            var pt = view.Caret.Position.BufferPosition;
                            var trig = new CompletionTrigger(CompletionTriggerReason.Invoke, pt.Snapshot);
                            var next = broker.TriggerCompletion(view, trig, pt, CancellationToken.None);
                            next?.OpenOrUpdate(trig, pt, CancellationToken.None);
                        }
                        catch (Exception ex) { Log.Write("Re-trigger failed: " + ex.Message); }
                    }), DispatcherPriority.Background);
                }
                return new CommitResult(true, CommitBehavior.None);
            }
            catch (Exception ex)
            {
                Log.Write("Commit failed: " + ex);
                return CommitResult.Unhandled;
            }
        }
    }

    // ---------------------------------------------------------------------- warm-up

    /// <summary>Logs editor content types and preloads metadata when a query window gets focus.</summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("SQL")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class SqlViewListener : IWpfTextViewCreationListener
    {
        private static bool announced;

        public void TextViewCreated(IWpfTextView view)
        {
            try
            {
                if (!announced)
                {
                    announced = true;
                    Log.Write($"SQL Smart Complete loaded into {System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName}. Settings: {Settings.FilePath}");
                }
                SqlBuffers.LogContentType(view.TextBuffer);
                if (!SqlBuffers.IsSql(view.TextBuffer)) return;
                view.GotAggregateFocus += (s, e) => Preload();
            }
            catch (Exception ex)
            {
                Log.Write("TextViewCreated failed: " + ex.Message);
            }
        }

        private static void Preload()
        {
            try
            {
                if (!Settings.Enabled) return;
                var c = SsmsConnection.TryGetActive();
                if (c?.Database != null) _ = MetadataStore.GetAsync(c, c.Database);
            }
            catch (Exception ex)
            {
                Log.Write("Preload failed: " + ex.Message);
            }
        }
    }
}
