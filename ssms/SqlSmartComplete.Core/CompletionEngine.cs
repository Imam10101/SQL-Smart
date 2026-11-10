using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SqlSmartComplete
{
    public enum SuggestionKind { Table, View, Column, Procedure, Function, TableFunction, Synonym, Schema, Alias, Join, Keyword, Variable, Cte, Temp, DataType, Snippet }

    public sealed class Suggestion
    {
        public string Label;
        public string Description;
        public SuggestionKind Kind;
        public string Detail;
        public string Documentation;
        public string InsertText;
        public string FilterText;
        public string SortText;
        /// <summary>Caret position inside InsertText after inserting, or null for the end.</summary>
        public int? CaretOffset;
        /// <summary>Open the suggestion list again after inserting (used for "dbo.").</summary>
        public bool Retrigger;
        public bool Preselect;
        public override string ToString() => $"{Label} => {InsertText}";
    }

    public sealed class EngineOptions
    {
        public bool AliasEnabled = true;
        public bool AliasUseAs = false;
        public bool AliasUpper = false;
        /// <summary>auto, always or never.</summary>
        public string QualifyColumns = "auto";
        /// <summary>whenNotDefault or always.</summary>
        public string SchemaPrefix = "whenNotDefault";
        public string DefaultSchema = "dbo";
        public bool KeywordsLower = false;
    }

    public static class CompletionEngine
    {
        private static readonly HashSet<string> AliasKeywords = new HashSet<string> { "FROM", "JOIN", "APPLY", "USING", "MERGE" };
        private static readonly HashSet<string> TvfKeywords = new HashSet<string> { "FROM", "JOIN", "APPLY", "USING" };
        private static readonly Regex IdentRe = new Regex(@"^(#{1,2})?[A-Za-z_][A-Za-z0-9_@$#]*$", RegexOptions.Compiled);

        public static string QuoteIdent(string name) =>
            IdentRe.IsMatch(name) && !SqlKeywords.Reserved.Contains(name) ? name : "[" + name.Replace("]", "]]") + "]";

        private static string Pad(int n) => n.ToString("D4");

        // ------------------------------------------------------------------ builder

        private sealed class Builder
        {
            public readonly List<Suggestion> Out = new List<Suggestion>();
            private readonly HashSet<string> seen = new HashSet<string>();
            public readonly Analysis A;
            public readonly MetadataIndex Idx;
            public readonly EngineOptions Opt;

            public Builder(Analysis a, MetadataIndex idx, EngineOptions opt) { A = a; Idx = idx; Opt = opt; }

            public string Kw(string s) => Opt.KeywordsLower ? s.ToLowerInvariant() : s;
            public string Filter(string name) => A.Bracketed ? "[" + name : name;

            public void Add(Suggestion s)
            {
                string key = s.Kind + "|" + s.Label + "|" + s.Description + "|" + s.InsertText;
                if (!seen.Add(key)) return;
                if (s.FilterText == null) s.FilterText = Filter(s.Label);
                Out.Add(s);
            }

            public string ObjectText(DbObject o, bool forceSchema = false)
            {
                if (A.Qualifier.Count > 0) return QuoteIdent(o.Name);
                bool needSchema = forceSchema || Opt.SchemaPrefix == "always" ||
                                  !string.Equals(o.Schema, Opt.DefaultSchema, StringComparison.OrdinalIgnoreCase);
                return needSchema ? QuoteIdent(o.Schema) + "." + QuoteIdent(o.Name) : QuoteIdent(o.Name);
            }

            public string RefQualifier(TableRef r)
            {
                if (r.Alias != null) return QuoteIdent(r.Alias);
                if (r.Kind == RefKind.Variable) return r.Name;
                return r.Schema != null ? QuoteIdent(r.Schema) + "." + QuoteIdent(r.Name) : QuoteIdent(r.Name);
            }

            public DbObject RefObject(TableRef r) =>
                Idx != null && (r.Kind == RefKind.Object || r.Kind == RefKind.Tvf) ? Idx.Find(r.Schema, r.Name) : null;

            public List<KeyValuePair<string, DbColumn>> RefColumns(TableRef r)
            {
                if (r.Columns != null) return r.Columns.Select(n => new KeyValuePair<string, DbColumn>(n, null)).ToList();
                var o = RefObject(r);
                return o == null ? new List<KeyValuePair<string, DbColumn>>() : o.Columns.Select(c => new KeyValuePair<string, DbColumn>(c.Name, c)).ToList();
            }

            public string AliasText(string alias) => Opt.AliasUseAs ? Kw("AS") + " " + alias : alias;
            public string NewAlias(string name) => AliasGenerator.Make(name, A.UsedAliases, Opt.AliasUpper);

            public static SuggestionKind KindOf(DbObject o)
            {
                switch (o.Kind)
                {
                    case ObjectKind.Table: return SuggestionKind.Table;
                    case ObjectKind.View: return SuggestionKind.View;
                    case ObjectKind.Procedure: return SuggestionKind.Procedure;
                    case ObjectKind.ScalarFunction: return SuggestionKind.Function;
                    case ObjectKind.TableFunction: return SuggestionKind.TableFunction;
                    default: return SuggestionKind.Synonym;
                }
            }

            public static string KindName(ObjectKind k)
            {
                switch (k)
                {
                    case ObjectKind.ScalarFunction: return "scalar function";
                    case ObjectKind.TableFunction: return "table function";
                    default: return k.ToString().ToLowerInvariant();
                }
            }

            public string ObjectDoc(DbObject o)
            {
                var lines = new List<string> { $"{o.Schema}.{o.Name} ({KindName(o.Kind)})" };
                if (o.Params.Count > 0)
                {
                    lines.Add("");
                    lines.Add("Parameters:");
                    lines.AddRange(o.Params.Select(p => $"  {p.Name} {p.Type}{(p.Output ? " OUTPUT" : "")}"));
                }
                if (o.ReturnType != null) { lines.Add(""); lines.Add("Returns: " + o.ReturnType); }
                if (o.Columns.Count > 0)
                {
                    lines.Add("");
                    lines.Add("Columns:");
                    lines.AddRange(o.Columns.Take(30).Select(c => $"  {c.Name} {c.Type}{(c.PrimaryKey ? " PK" : "")}{(c.Nullable ? "" : " NOT NULL")}"));
                    if (o.Columns.Count > 30) lines.Add($"  ... {o.Columns.Count - 30} more");
                }
                return string.Join("\n", lines);
            }

            public static string ColumnDetail(DbColumn c)
            {
                if (c == null) return "column";
                var flags = new[] { c.PrimaryKey ? "PK" : null, c.Identity ? "identity" : null, c.Computed ? "computed" : null, c.Nullable ? "null" : "not null" }
                    .Where(x => x != null);
                return $"{c.Type} ({string.Join(", ", flags)})";
            }

            // ---- item groups

            public void ColumnItems(TableRef r, bool qualify, string group)
            {
                string q = RefQualifier(r);
                int i = 0;
                foreach (var c in RefColumns(r))
                {
                    Add(new Suggestion
                    {
                        Label = c.Key,
                        Description = r.Alias ?? r.Name,
                        Kind = SuggestionKind.Column,
                        Detail = ColumnDetail(c.Value),
                        Documentation = $"Column of {(r.Schema != null ? r.Schema + "." : "")}{r.Name}{(r.Alias != null ? $" (alias {r.Alias})" : "")}\n{ColumnDetail(c.Value)}",
                        InsertText = qualify ? q + "." + QuoteIdent(c.Key) : QuoteIdent(c.Key),
                        SortText = group + (r.Alias ?? r.Name).ToLowerInvariant() + Pad(i++)
                    });
                }
            }

            public void StarItem(TableRef r, bool afterDot)
            {
                string q = RefQualifier(r);
                var cols = RefColumns(r).Select(c => QuoteIdent(c.Key)).ToList();
                if (cols.Count == 0) return;
                string text = string.Join(", ", cols.Select((c, i) => afterDot && i == 0 ? c : q + "." + c));
                Add(new Suggestion
                {
                    Label = "* (all columns)", Description = r.Alias ?? r.Name, Kind = SuggestionKind.Snippet,
                    Detail = $"Expand all {cols.Count} columns", InsertText = text, FilterText = "*", SortText = "0"
                });
            }

            public void TableItem(DbObject o, bool withAlias, string group = "1")
            {
                string text = ObjectText(o);
                string alias = withAlias ? NewAlias(o.Name) : null;
                string aliasPart = alias != null ? " " + AliasText(alias) : "";
                if (o.Kind == ObjectKind.TableFunction)
                {
                    Add(new Suggestion
                    {
                        Label = o.Name, Description = o.Schema, Kind = SuggestionKind.TableFunction,
                        Detail = "table function" + (alias != null ? $"  inserts {o.Name}(...) {alias}" : ""),
                        Documentation = ObjectDoc(o),
                        InsertText = text + "()" + aliasPart, CaretOffset = text.Length + 1,
                        SortText = group + o.Name.ToLowerInvariant()
                    });
                    return;
                }
                Add(new Suggestion
                {
                    Label = o.Name, Description = o.Schema, Kind = KindOf(o),
                    Detail = KindName(o.Kind) + (alias != null ? $"  inserts {text}{aliasPart}" : ""),
                    Documentation = ObjectDoc(o),
                    InsertText = text + aliasPart,
                    SortText = group + o.Name.ToLowerInvariant()
                });
            }

            public void SchemaItems(string group)
            {
                if (Idx == null) return;
                foreach (var s in Idx.Schemas)
                    Add(new Suggestion { Label = s, Kind = SuggestionKind.Schema, Detail = "schema", InsertText = QuoteIdent(s) + ".", SortText = group + s.ToLowerInvariant(), Retrigger = true });
            }

            public void KeywordItems(IEnumerable<string> words, string group, bool keepOrder = false)
            {
                int i = 0;
                foreach (var w in words)
                {
                    Add(new Suggestion { Label = Kw(w), Kind = SuggestionKind.Keyword, InsertText = Kw(w), SortText = keepOrder ? group + Pad(i) : group + w.ToLowerInvariant() });
                    i++;
                }
            }

            public void BuiltinFunctionItems(string group)
            {
                foreach (var f in SqlKeywords.BuiltinFunctions)
                {
                    string name = Kw(f.Name);
                    Add(new Suggestion
                    {
                        Label = name, Kind = SuggestionKind.Function, Detail = f.Signature, Documentation = f.Signature + "\n" + f.Description,
                        InsertText = f.NoParens ? name : name + "()", CaretOffset = f.NoParens ? (int?)null : name.Length + 1,
                        SortText = group + f.Name.ToLowerInvariant()
                    });
                }
            }

            public void UserFunctionItems(string group, IEnumerable<DbObject> objects)
            {
                foreach (var o in objects.Where(x => x.Kind == ObjectKind.ScalarFunction))
                {
                    string text = ObjectText(o, true);
                    Add(new Suggestion
                    {
                        Label = o.Name, Description = o.Schema, Kind = SuggestionKind.Function,
                        Detail = "scalar function" + (o.ReturnType != null ? " : " + o.ReturnType : ""),
                        Documentation = ObjectDoc(o), InsertText = text + "()", CaretOffset = text.Length + 1,
                        SortText = group + o.Name.ToLowerInvariant()
                    });
                }
            }

            public void ProcedureItems(IEnumerable<DbObject> objects, string group)
            {
                foreach (var o in objects.Where(x => x.Kind == ObjectKind.Procedure))
                {
                    string text = ObjectText(o);
                    string insert = text;
                    int? caret = null;
                    if (o.Params.Count > 0)
                    {
                        var parts = o.Params.Select(p => p.Name + " = " + (p.Output ? " " + Kw("OUTPUT") : "")).ToList();
                        insert = text + " " + string.Join(", ", parts);
                        caret = text.Length + 1 + o.Params[0].Name.Length + 3;
                    }
                    Add(new Suggestion
                    {
                        Label = o.Name, Description = o.Schema, Kind = SuggestionKind.Procedure,
                        Detail = "procedure" + (o.Params.Count > 0 ? $" ({o.Params.Count} parameters)" : ""),
                        Documentation = ObjectDoc(o), InsertText = insert, CaretOffset = caret,
                        SortText = group + o.Name.ToLowerInvariant()
                    });
                }
            }

            public void VariableItems(string group)
            {
                foreach (var v in A.Variables)
                    Add(new Suggestion { Label = v, Kind = SuggestionKind.Variable, Detail = "variable", InsertText = v, SortText = group + v.ToLowerInvariant() });
            }

            public void JoinItems()
            {
                if (Idx == null) return;
                string and = Kw("AND"), on = Kw("ON");
                foreach (var r in A.VisibleRefs.Where(x => x.Start < A.ReplaceStart))
                {
                    var ro = RefObject(r);
                    if (ro == null) continue;
                    foreach (var fk in Idx.ForeignKeysOf(ro))
                    {
                        var sides = new List<Tuple<string, string, List<KeyValuePair<string, string>>>>();
                        if (ro.Is(fk.FromSchema, fk.FromTable)) sides.Add(Tuple.Create(fk.ToSchema, fk.ToTable, fk.Columns));
                        if (ro.Is(fk.ToSchema, fk.ToTable)) sides.Add(Tuple.Create(fk.FromSchema, fk.FromTable, fk.Columns.Select(c => new KeyValuePair<string, string>(c.Value, c.Key)).ToList()));
                        foreach (var side in sides)
                        {
                            var cand = Idx.Find(side.Item1, side.Item2);
                            if (cand == null) continue;
                            string alias = Opt.AliasEnabled ? NewAlias(cand.Name) : null;
                            string candQ = alias ?? ObjectText(cand);
                            string cond = string.Join(" " + and + " ", side.Item3.Select(p => $"{RefQualifier(r)}.{QuoteIdent(p.Key)} = {candQ}.{QuoteIdent(p.Value)}"));
                            string head = ObjectText(cand) + (alias != null ? " " + AliasText(alias) : "");
                            Add(new Suggestion
                            {
                                Label = $"{head} {on} {cond}", Kind = SuggestionKind.Join, Detail = "join on foreign key " + fk.Name,
                                Documentation = ObjectDoc(cand), InsertText = $"{head} {on} {cond}",
                                FilterText = Filter(cand.Name), SortText = "0" + cand.Name.ToLowerInvariant()
                            });
                        }
                    }
                }
            }

            public void JoinConditionItems()
            {
                var t = A.JoinTarget;
                if (t == null || Idx == null) return;
                var to = RefObject(t);
                if (to == null) return;
                string and = Kw("AND");
                foreach (var r in A.VisibleRefs)
                {
                    if (ReferenceEquals(r, t)) continue;
                    var ro = RefObject(r);
                    if (ro == null) continue;
                    foreach (var fk in Idx.ForeignKeysOf(to))
                    {
                        var conds = new List<string>();
                        if (to.Is(fk.FromSchema, fk.FromTable) && ro.Is(fk.ToSchema, fk.ToTable))
                            conds.Add(string.Join(" " + and + " ", fk.Columns.Select(c => $"{RefQualifier(r)}.{QuoteIdent(c.Value)} = {RefQualifier(t)}.{QuoteIdent(c.Key)}")));
                        if (to.Is(fk.ToSchema, fk.ToTable) && ro.Is(fk.FromSchema, fk.FromTable))
                            conds.Add(string.Join(" " + and + " ", fk.Columns.Select(c => $"{RefQualifier(r)}.{QuoteIdent(c.Key)} = {RefQualifier(t)}.{QuoteIdent(c.Value)}")));
                        foreach (var cond in conds)
                            Add(new Suggestion { Label = cond, Kind = SuggestionKind.Join, Detail = "foreign key " + fk.Name, InsertText = cond, FilterText = cond, SortText = "0", Preselect = true });
                    }
                }
            }
        }

        // ------------------------------------------------------------------ entry point

        private static TableRef FindRef(IEnumerable<TableRef> refs, string q)
        {
            var list = refs.ToList();
            return list.FirstOrDefault(r => string.Equals(r.Alias, q, StringComparison.OrdinalIgnoreCase))
                   ?? list.FirstOrDefault(r => string.Equals(r.Name, q, StringComparison.OrdinalIgnoreCase));
        }

        private static readonly HashSet<SuggestionKind> ExactKinds = new HashSet<SuggestionKind>
        {
            SuggestionKind.Table, SuggestionKind.View, SuggestionKind.Synonym, SuggestionKind.Cte,
            SuggestionKind.Temp, SuggestionKind.Column
        };

        /// <summary>
        /// The single table or column whose name equals the typed word (ignoring case), or null.
        /// Used to complete a fully typed or pasted name as if it had been picked from the list.
        /// </summary>
        public static Suggestion ExactMatch(Analysis a, MetadataIndex idx, EngineOptions opt = null)
        {
            if (a.Kind == ContextKind.None || a.Prefix.Length == 0) return null;
            if (string.Equals(a.PrevWord, "AS", StringComparison.OrdinalIgnoreCase)) return null; // naming an alias
            var matches = GetSuggestions(a, idx, opt)
                .Where(s => ExactKinds.Contains(s.Kind) && s.CaretOffset == null && !s.Retrigger &&
                            string.Equals((s.FilterText ?? s.Label).Trim('[', ']'), a.Prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return matches.Select(s => s.InsertText).Distinct().Count() == 1 ? matches[0] : null;
        }

        public static List<Suggestion> GetSuggestions(Analysis a, MetadataIndex idx, EngineOptions opt = null)
        {
            opt = opt ?? new EngineOptions();
            if (a.Kind == ContextKind.None) return new List<Suggestion>();
            var b = new Builder(a, idx, opt);

            if (a.Qualifier.Count > 0) { Qualified(b); return b.Out; }

            switch (a.Kind)
            {
                case ContextKind.TableRef: TableRefItems(b); break;
                case ContextKind.Procedure:
                    if (idx != null) b.ProcedureItems(idx.Objects, "1");
                    b.SchemaItems("8");
                    b.VariableItems("5");
                    break;
                case ContextKind.InsertColumns: InsertColumns(b); break;
                case ContextKind.AfterTableRef: AfterTable(b); break;
                case ContextKind.JoinCondition: b.JoinConditionItems(); Expression(b); break;
                case ContextKind.General:
                    b.KeywordItems(SqlKeywords.KeywordPhrases, "6");
                    b.KeywordItems(SqlKeywords.Keywords, "6");
                    b.VariableItems("5");
                    break;
                default: Expression(b); break;
            }
            return b.Out;
        }

        private static void ColumnsOfObject(Builder b, DbObject o)
        {
            int i = 0;
            foreach (var c in o.Columns)
                b.Add(new Suggestion { Label = c.Name, Kind = SuggestionKind.Column, Detail = Builder.ColumnDetail(c), InsertText = QuoteIdent(c.Name), SortText = "1" + Pad(i++) });
        }

        private static void Qualified(Builder b)
        {
            var a = b.A;
            var idx = b.Idx;
            string q = a.Qualifier[a.Qualifier.Count - 1];

            if (a.Qualifier.Count == 1)
            {
                bool NotUnderCursor(TableRef r) => !(r.Start < a.ReplaceStart && r.End >= a.ReplaceStart - 1);
                var r0 = FindRef(a.VisibleRefs.Where(NotUnderCursor), q) ?? FindRef(a.Refs.Where(NotUnderCursor), q);
                if (r0 != null) { b.StarItem(r0, true); b.ColumnItems(r0, false, "1"); return; }
            }
            if (idx == null) return;

            if (a.Qualifier.Count >= 2)
            {
                var obj = idx.Find(a.Qualifier[a.Qualifier.Count - 2], q);
                if (obj != null && obj.Columns.Count > 0) { ColumnsOfObject(b, obj); return; }
            }

            if (idx.HasSchema(q))
            {
                var objs = idx.InSchema(q).ToList();
                switch (a.Kind)
                {
                    case ContextKind.TableRef:
                        bool withAlias = b.Opt.AliasEnabled && AliasKeywords.Contains(a.Keyword ?? "");
                        bool tvf = TvfKeywords.Contains(a.Keyword ?? "");
                        foreach (var o in objs)
                            if (o.Kind == ObjectKind.Table || o.Kind == ObjectKind.View || o.Kind == ObjectKind.Synonym || (tvf && o.Kind == ObjectKind.TableFunction))
                                b.TableItem(o, withAlias);
                        break;
                    case ContextKind.Procedure:
                        b.ProcedureItems(objs, "1");
                        break;
                    default:
                        b.UserFunctionItems("1", objs);
                        foreach (var o in objs) if (o.Kind == ObjectKind.Table || o.Kind == ObjectKind.View) b.TableItem(o, false, "3");
                        break;
                }
                return;
            }

            var single = idx.Find(null, q);
            if (single != null) ColumnsOfObject(b, single);
        }

        private static void TableRefItems(Builder b)
        {
            var a = b.A;
            string kw = a.Keyword ?? "FROM";
            bool withAlias = b.Opt.AliasEnabled && AliasKeywords.Contains(kw);
            bool tvf = TvfKeywords.Contains(kw);

            if (kw == "JOIN") b.JoinItems();
            if (b.Idx != null)
                foreach (var o in b.Idx.Objects)
                    if (o.Kind == ObjectKind.Table || o.Kind == ObjectKind.View || o.Kind == ObjectKind.Synonym || (tvf && o.Kind == ObjectKind.TableFunction))
                        b.TableItem(o, withAlias);

            foreach (var c in a.Ctes)
            {
                string alias = withAlias ? b.NewAlias(c.Name) : null;
                b.Add(new Suggestion
                {
                    Label = c.Name, Kind = SuggestionKind.Cte, Detail = "common table expression", Documentation = string.Join(", ", c.Columns),
                    InsertText = QuoteIdent(c.Name) + (alias != null ? " " + b.AliasText(alias) : ""), SortText = "0" + c.Name.ToLowerInvariant()
                });
            }
            foreach (var t in a.TempTables)
            {
                string alias = withAlias ? b.NewAlias(t) : null;
                b.Add(new Suggestion { Label = t, Kind = SuggestionKind.Temp, Detail = "temporary table", InsertText = t + (alias != null ? " " + b.AliasText(alias) : ""), SortText = "2" + t.ToLowerInvariant() });
            }
            if (kw != "TABLE") b.VariableItems("5");
            b.SchemaItems("8");
        }

        private static void InsertColumns(Builder b)
        {
            var a = b.A;
            if (a.InsertName == null || b.Idx == null) return;
            var o = b.Idx.Find(a.InsertSchema, a.InsertName);
            if (o == null) return;
            var writable = o.Columns.Where(c => !c.Identity && !c.Computed).ToList();
            if (writable.Count > 0)
                b.Add(new Suggestion
                {
                    Label = "(all columns)", Kind = SuggestionKind.Snippet, Detail = $"Insert all {writable.Count} writable columns",
                    InsertText = string.Join(", ", writable.Select(c => QuoteIdent(c.Name))), FilterText = "(", SortText = "0"
                });
            int i = 0;
            foreach (var c in o.Columns)
                b.Add(new Suggestion { Label = c.Name, Kind = SuggestionKind.Column, Detail = Builder.ColumnDetail(c), InsertText = QuoteIdent(c.Name), SortText = (c.Identity || c.Computed ? "2" : "1") + Pad(i++) });
        }

        private static void AfterTable(Builder b)
        {
            var a = b.A;
            var last = a.Refs.Where(r => r.End <= a.ReplaceStart).LastOrDefault();
            if (last != null && last.Alias == null && b.Opt.AliasEnabled && AliasKeywords.Contains(last.Keyword) && last.Kind != RefKind.Derived && last.Kind != RefKind.Variable)
            {
                string alias = b.NewAlias(last.Name);
                b.Add(new Suggestion { Label = alias, Kind = SuggestionKind.Alias, Detail = "alias for " + last.Name, InsertText = b.AliasText(alias), SortText = "00", Preselect = true });
            }
            b.KeywordItems(SqlKeywords.AfterTableKeywords, "1", true);
            b.KeywordItems(SqlKeywords.Keywords, "6");
        }

        private static void Expression(Builder b)
        {
            var a = b.A;
            var refs = a.VisibleRefs.Where(r => r.Kind != RefKind.Derived || r.Alias != null).ToList();
            foreach (var r in refs)
            {
                bool qualify = b.Opt.QualifyColumns == "always" || (b.Opt.QualifyColumns == "auto" && (r.Alias != null || refs.Count > 1));
                b.ColumnItems(r, qualify, "1");
            }
            foreach (var r in refs.Where(x => x.Alias != null))
                b.Add(new Suggestion { Label = r.Alias, Kind = SuggestionKind.Alias, Detail = $"alias of {(r.Schema != null ? r.Schema + "." : "")}{r.Name}", InsertText = QuoteIdent(r.Alias), SortText = "2" + r.Alias.ToLowerInvariant() });
            b.VariableItems("3");
            if (b.Idx != null) b.UserFunctionItems("4", b.Idx.Objects);
            b.BuiltinFunctionItems("4");
            b.KeywordItems(SqlKeywords.KeywordPhrases, "6");
            b.KeywordItems(SqlKeywords.Keywords, "6");
            b.KeywordItems(SqlKeywords.DataTypes.Select(t => t.ToUpperInvariant()), "7");
            b.SchemaItems("8");
        }
    }
}
