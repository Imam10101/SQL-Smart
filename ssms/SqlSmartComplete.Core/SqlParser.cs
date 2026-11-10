using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SqlSmartComplete
{
    // A small, error-tolerant T-SQL parser. It does not validate SQL. It finds what
    // the completion engine needs: the statement around the cursor, the tables and
    // aliases in it, and the kind of thing expected at the cursor.

    public enum TokenType { Word, QIdent, String, Number, Variable, Punct, Operator }

    public sealed class Token
    {
        public TokenType Type;
        public string Text;
        public string Value;
        public string Upper;
        public int Start;
        public int End;
        public bool Closed = true;
        public override string ToString() => $"{Type}:{Text}";
    }

    public enum RefKind { Object, Tvf, Derived, Cte, Temp, Variable }

    public sealed class TableRef
    {
        public RefKind Kind;
        public string Schema;
        public string Name;
        public string Alias;
        public List<string> Columns;
        public int Scope;
        public int Start;
        public int End;
        public string Keyword;
    }

    public sealed class CteDef
    {
        public string Name;
        public List<string> Columns;
    }

    public enum ContextKind { None, General, TableRef, Procedure, Expression, JoinCondition, AfterTableRef, InsertColumns }

    public sealed class Analysis
    {
        public ContextKind Kind;
        public string Prefix = "";
        public int ReplaceStart;
        public bool Bracketed;
        public List<string> Qualifier = new List<string>();
        public string Keyword;
        public string PrevWord;
        public List<TableRef> Refs = new List<TableRef>();
        public List<TableRef> VisibleRefs = new List<TableRef>();
        public List<CteDef> Ctes = new List<CteDef>();
        public HashSet<string> UsedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public string InsertSchema;
        public string InsertName;
        public TableRef JoinTarget;
        public string Database;
        public List<string> Variables = new List<string>();
        public List<string> TempTables = new List<string>();
    }

    public static class SqlParser
    {
        // ------------------------------------------------------------------ tokenizer

        private static bool IdentStart(char c) => char.IsLetter(c) || c == '_' || c == '#';
        private static bool IdentPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == '#' || c == '$';

        private static readonly HashSet<string> TwoCharOps = new HashSet<string>
            { "<=", ">=", "<>", "!=", "!<", "!>", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "::" };

        public static List<Token> Tokenize(string text, List<KeyValuePair<int, int>> comments = null)
        {
            var tokens = new List<Token>();
            int n = text.Length, i = 0;

            void Push(TokenType type, int start, int end, string value, bool closed = true) =>
                tokens.Add(new Token { Type = type, Text = text.Substring(start, end - start), Value = value, Upper = value.ToUpperInvariant(), Start = start, End = end, Closed = closed });

            while (i < n)
            {
                char c = text[i];
                char next = i + 1 < n ? text[i + 1] : '\0';

                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '-' && next == '-')
                {
                    int s = i;
                    while (i < n && text[i] != '\n') i++;
                    comments?.Add(new KeyValuePair<int, int>(s, i));
                    continue;
                }
                if (c == '/' && next == '*')
                {
                    int s = i, depth = 0;
                    while (i < n)
                    {
                        if (text[i] == '/' && i + 1 < n && text[i + 1] == '*') { depth++; i += 2; continue; }
                        if (text[i] == '*' && i + 1 < n && text[i + 1] == '/') { depth--; i += 2; if (depth == 0) break; continue; }
                        i++;
                    }
                    comments?.Add(new KeyValuePair<int, int>(s, i));
                    continue;
                }
                if (c == '\'' || ((c == 'N' || c == 'n') && next == '\''))
                {
                    int s = i;
                    i += c == '\'' ? 1 : 2;
                    var val = new StringBuilder();
                    bool closed = false;
                    while (i < n)
                    {
                        if (text[i] == '\'')
                        {
                            if (i + 1 < n && text[i + 1] == '\'') { val.Append('\''); i += 2; continue; }
                            i++; closed = true; break;
                        }
                        val.Append(text[i++]);
                    }
                    Push(TokenType.String, s, i, val.ToString(), closed);
                    continue;
                }
                if (c == '[' || c == '"')
                {
                    char close = c == '[' ? ']' : '"';
                    int s = i;
                    i++;
                    var val = new StringBuilder();
                    bool closed = false;
                    while (i < n)
                    {
                        if (text[i] == close)
                        {
                            if (i + 1 < n && text[i + 1] == close) { val.Append(close); i += 2; continue; }
                            i++; closed = true; break;
                        }
                        if (text[i] == '\n' && c == '[') break;
                        val.Append(text[i++]);
                    }
                    Push(TokenType.QIdent, s, i, val.ToString(), closed);
                    continue;
                }
                if (c == '@')
                {
                    int s = i;
                    i++;
                    if (i < n && text[i] == '@') i++;
                    while (i < n && IdentPart(text[i])) i++;
                    Push(TokenType.Variable, s, i, text.Substring(s, i - s));
                    continue;
                }
                if (IdentStart(c))
                {
                    int s = i;
                    i++;
                    while (i < n && IdentPart(text[i])) i++;
                    Push(TokenType.Word, s, i, text.Substring(s, i - s));
                    continue;
                }
                if (c >= '0' && c <= '9')
                {
                    int s = i;
                    while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '.'))
                    {
                        if ((text[i] == 'e' || text[i] == 'E') && i + 1 < n && (text[i + 1] == '-' || text[i + 1] == '+')) i++;
                        i++;
                    }
                    Push(TokenType.Number, s, i, text.Substring(s, i - s));
                    continue;
                }
                if (c == '(' || c == ')' || c == ',' || c == ';' || c == '.')
                {
                    Push(TokenType.Punct, i, i + 1, c.ToString());
                    i++;
                    continue;
                }
                if (i + 1 < n && TwoCharOps.Contains(text.Substring(i, 2)))
                {
                    Push(TokenType.Operator, i, i + 2, text.Substring(i, 2));
                    i += 2;
                    continue;
                }
                Push(TokenType.Operator, i, i + 1, c.ToString());
                i++;
            }
            return tokens;
        }

        // ------------------------------------------------------------------ helpers

        private static Token At(IList<Token> ts, int i) => i >= 0 && i < ts.Count ? ts[i] : null;
        public static bool IsPunct(Token t, string p) => t != null && t.Type == TokenType.Punct && t.Value == p;
        public static bool IsWord(Token t, params string[] words) =>
            t != null && t.Type == TokenType.Word && (words.Length == 0 || Array.IndexOf(words, t.Upper) >= 0);

        public static bool IsNameToken(Token t) =>
            t != null && (t.Type == TokenType.QIdent || (t.Type == TokenType.Word && !SqlKeywords.Reserved.Contains(t.Upper)));

        private static bool IsAliasToken(Token t) =>
            t != null && (t.Type == TokenType.QIdent || (t.Type == TokenType.Word && !SqlKeywords.AliasStop.Contains(t.Upper) && !t.Value.StartsWith("#")));

        private static int MatchingParen(IList<Token> ts, int open)
        {
            int depth = 0;
            for (int k = open; k < ts.Count; k++)
            {
                if (IsPunct(ts[k], "(")) depth++;
                else if (IsPunct(ts[k], ")")) { depth--; if (depth == 0) return k; }
            }
            return ts.Count;
        }

        // ------------------------------------------------------------------ statements

        private static readonly HashSet<string> ObjectTypeWords = new HashSet<string> { "TABLE", "VIEW", "PROC", "PROCEDURE", "FUNCTION", "INDEX", "SCHEMA", "TRIGGER",
            "DATABASE", "SEQUENCE", "TYPE", "SYNONYM", "USER", "LOGIN", "ROLE", "STATISTICS", "DEFAULT", "RULE", "ASSEMBLY" };
        private static readonly HashSet<string> AlwaysStarters = new HashSet<string> { "CREATE", "ALTER", "DROP", "DECLARE", "PRINT", "RETURN", "IF", "WHILE", "BEGIN",
            "USE", "TRUNCATE", "RAISERROR", "THROW", "WAITFOR", "OPEN", "CLOSE", "FETCH", "DEALLOCATE", "GRANT", "REVOKE",
            "DENY", "BACKUP", "RESTORE", "DBCC", "COMMIT", "ROLLBACK", "BREAK", "CONTINUE", "GOTO" };
        private static readonly HashSet<string> DmlWords = new HashSet<string> { "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE" };
        private static readonly HashSet<string> NotCteWith = new HashSet<string> { "TIES", "CHECK", "NOCHECK", "RECOMPILE", "ENCRYPTION", "SCHEMABINDING", "EXECUTE",
            "ROLLUP", "CUBE", "NOWAIT", "LOG", "OVERRIDE", "VIEW_METADATA", "NATIVE_COMPILATION", "XMLNAMESPACES", "GRANT", "MARK", "RESULT", "ROLLBACK" };
        private static readonly string[] SelectNotAfter = { "UNION", "ALL", "EXCEPT", "INTERSECT", "AS", "FOR", "GRANT", "DENY", "REVOKE" };
        private static readonly string[] DmlNotAfter = { "ON", "THEN", "INSTEAD", "AFTER", "FOR", "OF", "GRANT", "DENY", "REVOKE", "TO", "NOT" };
        private static readonly Regex GoRest = new Regex(@"^\s*(\d+\s*)?(--.*)?$", RegexOptions.Compiled);

        public static bool IsBatchSeparator(Token t, string text)
        {
            if (t.Type != TokenType.Word || t.Upper != "GO") return false;
            int lineStart = t.Start == 0 ? 0 : text.LastIndexOf('\n', t.Start - 1) + 1;
            if (text.Substring(lineStart, t.Start - lineStart).Trim().Length != 0) return false;
            int lineEnd = text.IndexOf('\n', t.End);
            string rest = text.Substring(t.End, (lineEnd == -1 ? text.Length : lineEnd) - t.End).TrimEnd('\r');
            return GoRest.IsMatch(rest);
        }

        /// <summary>Returns statement start offsets (the first is always 0).</summary>
        public static List<int> SplitStatements(List<Token> tokens, string text)
        {
            var bounds = new List<int> { 0 };
            int depth = 0;
            Token first = null;
            bool withPrelude = false, insertPending = false;

            void Reset(Token t)
            {
                first = t;
                withPrelude = t != null && t.Upper == "WITH";
                insertPending = t != null && t.Upper == "INSERT";
            }

            for (int i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (IsPunct(t, "(")) { depth++; if (first == null) Reset(t); continue; }
                if (IsPunct(t, ")")) { depth = Math.Max(0, depth - 1); continue; }
                if (IsPunct(t, ";")) { bounds.Add(t.End); depth = 0; Reset(null); continue; }
                if (IsBatchSeparator(t, text)) { bounds.Add(t.End); depth = 0; Reset(null); continue; }
                if (first == null) { Reset(t); continue; }
                if (depth != 0 || t.Type != TokenType.Word) continue;

                string u = t.Upper;
                var prev = At(tokens, i - 1);
                string pu = prev != null && prev.Type == TokenType.Word ? prev.Upper : "";
                bool prevComma = IsPunct(prev, ",");
                bool starter = false;

                if (DmlWords.Contains(u))
                {
                    starter = u == "SELECT"
                        ? Array.IndexOf(SelectNotAfter, pu) < 0 && !prevComma
                        : Array.IndexOf(DmlNotAfter, pu) < 0 && !prevComma;
                    if (starter && withPrelude) { starter = false; withPrelude = false; insertPending = u == "INSERT"; }
                    else if (starter && insertPending && u == "SELECT") { starter = false; insertPending = false; }
                }
                else if (u == "EXEC" || u == "EXECUTE")
                {
                    starter = !insertPending && Array.IndexOf(new[] { "AS", "WITH", "GRANT", "DENY", "REVOKE" }, pu) < 0 && !prevComma;
                    if (insertPending) insertPending = false;
                }
                else if (u == "WITH")
                {
                    var n1 = At(tokens, i + 1);
                    var n2 = At(tokens, i + 2);
                    starter = IsNameToken(n1) && !NotCteWith.Contains(n1.Upper) && (IsWord(n2, "AS") || IsPunct(n2, "("));
                }
                else if (AlwaysStarters.Contains(u))
                {
                    starter = true;
                    if (u == "IF" && (ObjectTypeWords.Contains(pu) || pu == "COLUMN" || pu == "CONSTRAINT")) starter = false;
                    if (u == "FETCH" && (pu == "ROWS" || pu == "ROW")) starter = false;
                    if ((u == "DROP" || u == "ALTER") && first != null && first.Upper == "ALTER") starter = false;
                    if ((u == "GRANT" || u == "DENY" || u == "REVOKE") && pu == "WITH") starter = false;
                    if ((u == "CLOSE" || u == "OPEN") && pu == "SET") starter = false;
                }

                if (starter) { bounds.Add(t.Start); Reset(t); }
            }
            return bounds;
        }

        // ------------------------------------------------------------------ table references

        private static readonly HashSet<string> RefKeywords = new HashSet<string> { "FROM", "JOIN", "APPLY", "USING", "UPDATE", "INTO", "MERGE", "TABLE", "DELETE" };
        private static readonly HashSet<string> SelectListEnd = new HashSet<string> { "FROM", "INTO", "WHERE", "UNION", "EXCEPT", "INTERSECT", "ORDER", "GROUP", "HAVING", "OPTION", "FOR", "WINDOW" };

        public static List<string> SelectOutputNames(IList<Token> ts)
        {
            int depth = 0, i = -1;
            for (int k = 0; k < ts.Count; k++)
            {
                if (IsPunct(ts[k], "(")) depth++;
                else if (IsPunct(ts[k], ")")) depth--;
                else if (depth == 0 && IsWord(ts[k], "SELECT")) { i = k; break; }
            }
            var names = new List<string>();
            if (i < 0) return names;
            int j = i + 1;
            if (IsWord(At(ts, j), "DISTINCT", "ALL")) j++;
            if (IsWord(At(ts, j), "TOP"))
            {
                j++;
                if (IsPunct(At(ts, j), "(")) j = MatchingParen(ts, j) + 1; else j++;
                if (IsWord(At(ts, j), "PERCENT")) j++;
                if (IsWord(At(ts, j), "WITH") && IsWord(At(ts, j + 1), "TIES")) j += 2;
            }
            var items = new List<List<Token>>();
            var cur = new List<Token>();
            depth = 0;
            for (int k = j; k < ts.Count; k++)
            {
                var t = ts[k];
                if (depth == 0 && t.Type == TokenType.Word && SelectListEnd.Contains(t.Upper)) break;
                if (IsPunct(t, "(")) depth++;
                if (IsPunct(t, ")")) depth--;
                if (depth == 0 && IsPunct(t, ",")) { items.Add(cur); cur = new List<Token>(); continue; }
                cur.Add(t);
            }
            items.Add(cur);

            foreach (var it in items)
            {
                if (it.Count == 0) continue;
                if (it.Count >= 3 && IsNameToken(it[0]) && it[1].Type == TokenType.Operator && it[1].Value == "=") { names.Add(it[0].Value); continue; }
                var last = it[it.Count - 1];
                if (last.Type != TokenType.Word && last.Type != TokenType.QIdent) continue;
                if (it.Count == 1) { names.Add(last.Value); continue; }
                var prev = it[it.Count - 2];
                if (prev.Type == TokenType.Operator) continue;
                names.Add(last.Value);
            }
            return names;
        }

        public static List<CteDef> ParseCtes(IList<Token> ts)
        {
            var ctes = new List<CteDef>();
            if (!IsWord(At(ts, 0), "WITH")) return ctes;
            int i = 1;
            while (i < ts.Count && IsNameToken(ts[i]))
            {
                string name = ts[i].Value;
                i++;
                List<string> cols = null;
                if (IsPunct(At(ts, i), "("))
                {
                    int close = MatchingParen(ts, i);
                    cols = ts.Skip(i + 1).Take(close - i - 1).Where(IsNameToken).Select(t => t.Value).ToList();
                    i = close + 1;
                }
                if (!IsWord(At(ts, i), "AS")) { ctes.Add(new CteDef { Name = name, Columns = cols ?? new List<string>() }); break; }
                i++;
                if (!IsPunct(At(ts, i), "(")) { ctes.Add(new CteDef { Name = name, Columns = cols ?? new List<string>() }); break; }
                int c2 = MatchingParen(ts, i);
                ctes.Add(new CteDef { Name = name, Columns = cols ?? SelectOutputNames(ts.Skip(i + 1).Take(c2 - i - 1).ToList()) });
                i = c2 + 1;
                if (!IsPunct(At(ts, i), ",")) break;
                i++;
            }
            return ctes;
        }

        public static List<TableRef> ParseTableRefs(IList<Token> ts, List<CteDef> ctes)
        {
            var refs = new List<TableRef>();
            var scopes = new Stack<int>();
            var scopeAt = new int[ts.Count];
            for (int k = 0; k < ts.Count; k++)
            {
                scopeAt[k] = scopes.Count > 0 ? scopes.Peek() : -1;
                if (IsPunct(ts[k], "(")) scopes.Push(ts[k].Start);
                else if (IsPunct(ts[k], ")") && scopes.Count > 0) scopes.Pop();
            }

            for (int i = 0; i < ts.Count; i++)
            {
                var t = ts[i];
                if (t.Type != TokenType.Word || !RefKeywords.Contains(t.Upper)) continue;
                string u = t.Upper;
                var prev = At(ts, i - 1);
                if (u == "UPDATE" && IsWord(prev, "ON", "FOR", "AFTER", "OF")) continue;
                if (u == "DELETE" && (IsWord(prev, "ON", "FOR", "AFTER", "OF") || IsWord(At(ts, i + 1), "FROM"))) continue;
                if (u == "MERGE" && IsWord(At(ts, i + 1), "INTO")) continue;
                if (u == "TABLE" && !IsWord(prev, "TRUNCATE", "DROP", "ALTER", "CREATE", "EXISTS")) continue;
                if (u == "FROM" && IsWord(prev, "DISTINCT", "IS")) continue;

                int j = i + 1;
                while (true)
                {
                    var r = ParseOneRef(ts, j, u, scopeAt, ctes, out int next);
                    if (r == null) break;
                    refs.Add(r);
                    j = next;
                    if (u == "FROM" && IsPunct(At(ts, j), ",")) { j++; continue; }
                    break;
                }
            }
            return refs;
        }

        private static TableRef ParseOneRef(IList<Token> ts, int j, string kw, int[] scopeAt, List<CteDef> ctes, out int next)
        {
            next = j;
            if (At(ts, j) == null) return null;
            if (IsWord(At(ts, j), "IF") && IsWord(At(ts, j + 1), "EXISTS")) j += 2;
            var first = At(ts, j);
            if (first == null) return null;
            int scope = j < scopeAt.Length ? scopeAt[j] : -1;

            if (IsPunct(first, "(") && kw != "INTO" && kw != "TABLE")
            {
                int close = MatchingParen(ts, j);
                var inner = ts.Skip(j + 1).Take(close - j - 1).ToList();
                if (!inner.Any(x => IsWord(x, "SELECT"))) return null;
                int k = close + 1;
                string alias = null;
                if (IsWord(At(ts, k), "AS")) k++;
                if (IsAliasToken(At(ts, k))) { alias = ts[k].Value; k++; }
                var columns = SelectOutputNames(inner);
                if (IsPunct(At(ts, k), "(") && alias != null)
                {
                    int c2 = MatchingParen(ts, k);
                    columns = ts.Skip(k + 1).Take(c2 - k - 1).Where(IsNameToken).Select(x => x.Value).ToList();
                    k = c2 + 1;
                }
                next = k;
                int endIdx = Math.Min(k, ts.Count) - 1;
                return new TableRef { Kind = RefKind.Derived, Name = alias ?? "(subquery)", Alias = alias, Columns = columns, Scope = scope, Start = first.Start, End = ts[endIdx].End, Keyword = kw };
            }

            RefKind kind;
            if (first.Type == TokenType.Variable && !first.Value.StartsWith("@@")) kind = RefKind.Variable;
            else if (first.Type == TokenType.Word && first.Value.StartsWith("#")) kind = RefKind.Temp;
            else if (IsNameToken(first)) kind = RefKind.Object;
            else return null;

            var parts = new List<string> { first.Value };
            int m = j + 1;
            while (IsPunct(At(ts, m), "."))
            {
                if (IsPunct(At(ts, m + 1), ".")) { parts.Add(""); m++; continue; }
                if (IsNameToken(At(ts, m + 1))) { parts.Add(ts[m + 1].Value); m += 2; continue; }
                break;
            }
            string name = parts[parts.Count - 1];
            string schema = parts.Count >= 2 && parts[parts.Count - 2].Length > 0 ? parts[parts.Count - 2] : null;
            if (string.IsNullOrEmpty(name)) return null;

            if (IsPunct(At(ts, m), "(") && kw != "INTO" && kw != "TABLE" && kind == RefKind.Object)
            {
                kind = RefKind.Tvf;
                m = MatchingParen(ts, m) + 1;
            }

            string al = null;
            if (IsWord(At(ts, m), "AS") && IsAliasToken(At(ts, m + 1))) { al = ts[m + 1].Value; m += 2; }
            else if (IsAliasToken(At(ts, m))) { al = ts[m].Value; m++; }

            List<string> cols = null;
            if (kind == RefKind.Object && schema == null)
            {
                var cte = ctes.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                if (cte != null) { kind = RefKind.Cte; cols = cte.Columns; }
            }

            next = m;
            int last = Math.Min(m, ts.Count) - 1;
            return new TableRef { Kind = kind, Schema = schema, Name = name, Alias = al, Columns = cols, Scope = scope, Start = first.Start, End = ts[last].End, Keyword = kw };
        }

        // ------------------------------------------------------------------ cursor analysis

        private static readonly HashSet<string> ClauseWords = new HashSet<string> { "SELECT", "FROM", "JOIN", "APPLY", "USING", "WHERE", "ON", "BY", "HAVING", "SET",
            "VALUES", "UPDATE", "INTO", "MERGE", "EXEC", "EXECUTE", "WHEN", "THEN", "ELSE", "RETURN", "OUTPUT", "IF", "WHILE",
            "DECLARE", "PRINT", "DELETE", "INSERT", "TABLE", "CASE", "AND", "OR", "TOP" };
        private static readonly HashSet<string> SkipClauseWords = new HashSet<string> { "AND", "OR", "CASE", "WHEN", "THEN", "ELSE", "TOP" };
        private static readonly HashSet<string> TablePositionWords = new HashSet<string> { "FROM", "JOIN", "APPLY", "USING", "UPDATE", "INTO", "MERGE", "TABLE", "DELETE" };
        private static readonly HashSet<string> TableClauses = TablePositionWords;
        private static readonly Regex PrefixRegex = new Regex(@"(\[(?:[^\]\n]|\]\])*|[\p{L}\p{N}_@#$]+)$", RegexOptions.Compiled);

        public static Analysis Analyze(string text, int offset)
        {
            var comments = new List<KeyValuePair<int, int>>();
            var tokens = Tokenize(text, comments);
            var none = new Analysis { Kind = ContextKind.None, ReplaceStart = offset };

            foreach (var c in comments)
            {
                bool isLine = string.CompareOrdinal(text, c.Key, "--", 0, 2) == 0;
                if (c.Key < offset && (offset < c.Value || (isLine && offset == c.Value) || (c.Value == text.Length && offset == c.Value))) return none;
            }
            foreach (var t in tokens)
            {
                if (t.Start >= offset) break;
                if (t.Type == TokenType.String && t.Start < offset && (offset < t.End || !t.Closed)) return none;
            }

            int headStart = Math.Max(0, offset - 300);
            var m = PrefixRegex.Match(text.Substring(headStart, offset - headStart));
            string prefix = m.Success ? m.Groups[1].Value : "";
            int replaceStart = offset - prefix.Length;
            bool bracketed = false;
            if (prefix.StartsWith("[")) { bracketed = true; prefix = prefix.Substring(1).Replace("]]", "]"); }
            if (prefix.Length > 0 && char.IsDigit(prefix[0])) return none;

            var bounds = SplitStatements(tokens, text);
            int rangeStart = 0, rangeEnd = text.Length;
            for (int b = 0; b < bounds.Count; b++)
            {
                if (bounds[b] <= replaceStart) { rangeStart = bounds[b]; rangeEnd = b + 1 < bounds.Count ? bounds[b + 1] : text.Length; }
                else break;
            }
            var stmt = tokens.Where(t => t.Start >= rangeStart && t.Start < rangeEnd && !IsPunct(t, ";") && !IsBatchSeparator(t, text)
                                         && !(prefix.Length > 0 && t.Start == replaceStart)).ToList();

            var ctes = ParseCtes(stmt);
            var refs = ParseTableRefs(stmt, ctes);
            var a = new Analysis { Prefix = prefix, ReplaceStart = replaceStart, Bracketed = bracketed, Refs = refs, Ctes = ctes };
            foreach (var r in refs) if (r.Alias != null) a.UsedAliases.Add(r.Alias);

            var before = stmt.Where(t => t.End <= replaceStart).ToList();
            int k = before.Count - 1;
            while (k >= 1 && IsPunct(before[k], ".") && (before[k - 1].Type == TokenType.Word || before[k - 1].Type == TokenType.QIdent))
            {
                a.Qualifier.Insert(0, before[k - 1].Value);
                k -= 2;
            }
            var clauseTokens = before.Take(k + 1).ToList();

            var chain = new HashSet<int> { -1 };
            var stack = new Stack<int>();
            foreach (var t in before)
            {
                if (IsPunct(t, "(")) stack.Push(t.Start);
                else if (IsPunct(t, ")") && stack.Count > 0) stack.Pop();
            }
            foreach (var s in stack) chain.Add(s);
            a.VisibleRefs = refs.Where(r => chain.Contains(r.Scope)).ToList();

            Token prev = null;
            string clause = null;
            int groupOpen = -1, level = 0;
            for (int i = clauseTokens.Count - 1; i >= 0; i--)
            {
                var t = clauseTokens[i];
                if (IsPunct(t, ")")) { if (prev == null) prev = t; level++; continue; }
                if (IsPunct(t, "("))
                {
                    if (level > 0) { level--; continue; }
                    if (prev == null) prev = t;
                    groupOpen = i;
                    break;
                }
                if (level > 0) continue;
                if (prev == null) prev = t;
                if (clause == null && t.Type == TokenType.Word && ClauseWords.Contains(t.Upper) && !SkipClauseWords.Contains(t.Upper))
                {
                    clause = t.Upper;
                    break;
                }
            }
            a.PrevWord = prev != null && prev.Type == TokenType.Word ? prev.Upper : null;

            var vars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var temps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t.Type == TokenType.Variable && !t.Value.StartsWith("@@") && t.Start != replaceStart && t.Value.Length > 1 && vars.Add(t.Value)) a.Variables.Add(t.Value);
                if (t.Type == TokenType.Word && t.Value.StartsWith("#") && t.Value.Length > 1 && t.Start != replaceStart && temps.Add(t.Value)) a.TempTables.Add(t.Value);
                var n1 = At(tokens, i + 1);
                if (t.Start < offset && IsWord(t, "USE") && n1 != null && (n1.Type == TokenType.Word || n1.Type == TokenType.QIdent) && n1.End <= replaceStart)
                    a.Database = n1.Value;
            }

            string pu = a.PrevWord;
            if (prev == null) a.Kind = ContextKind.General;
            else if (pu != null && TablePositionWords.Contains(pu) && IsTablePosition(clauseTokens, pu)) { a.Kind = ContextKind.TableRef; a.Keyword = pu; }
            else if (IsPunct(prev, ",") && clause == "FROM") { a.Kind = ContextKind.TableRef; a.Keyword = "FROM"; }
            else if (pu == "EXEC" || pu == "EXECUTE") a.Kind = ContextKind.Procedure;
            else if (groupOpen > 0 && InsertTargetBefore(clauseTokens, groupOpen, out var ins, out var insSchema))
            {
                a.Kind = ContextKind.InsertColumns;
                a.InsertName = ins;
                a.InsertSchema = insSchema;
            }
            else if (clause != null && TableClauses.Contains(clause) &&
                     (IsNameToken(prev) || IsPunct(prev, ")") || prev.Type == TokenType.Variable || (prev.Type == TokenType.Word && prev.Value.StartsWith("#"))))
                a.Kind = ContextKind.AfterTableRef;
            else if (clause == "ON")
            {
                a.Kind = ContextKind.JoinCondition;
                a.JoinTarget = a.VisibleRefs.Where(r => r.Start < replaceStart).LastOrDefault();
            }
            else a.Kind = ContextKind.Expression;

            if (a.Qualifier.Count > 0 && a.Kind == ContextKind.AfterTableRef) a.Kind = ContextKind.TableRef;
            return a;
        }

        private static bool IsTablePosition(List<Token> ts, string word)
        {
            if (ts.Count == 0) return false;
            var before = At(ts, ts.Count - 2);
            if (word == "UPDATE" || word == "DELETE") return !IsWord(before, "ON", "FOR", "AFTER", "OF", "THEN");
            if (word == "TABLE") return IsWord(before, "TRUNCATE", "DROP", "ALTER", "CREATE", "EXISTS");
            return true;
        }

        private static bool InsertTargetBefore(List<Token> ts, int groupOpen, out string name, out string schema)
        {
            name = null; schema = null;
            int k = groupOpen - 1;
            var t = At(ts, k);
            if (!IsNameToken(t) && !(t != null && t.Type == TokenType.Word && t.Value.StartsWith("#"))) return false;
            string n = t.Value;
            string s = null;
            k--;
            if (IsPunct(At(ts, k), ".") && IsNameToken(At(ts, k - 1)))
            {
                s = ts[k - 1].Value;
                k -= 2;
                while (IsPunct(At(ts, k), ".") && IsNameToken(At(ts, k - 1))) k -= 2;
            }
            if (IsWord(At(ts, k), "INTO") || IsWord(At(ts, k), "INSERT")) { name = n; schema = s; return true; }
            return false;
        }
    }
}
