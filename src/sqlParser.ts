// A small, error-tolerant T-SQL parser. It does not validate SQL. It only finds
// what the completion engine needs: the statement around the cursor, the tables
// and aliases in it, and the kind of thing expected at the cursor.

import { ALIAS_STOP, RESERVED } from './sqlKeywords';

export type TokenType = 'word' | 'qident' | 'string' | 'number' | 'variable' | 'punct' | 'operator';

export interface Token {
  type: TokenType;
  /** Raw source text. */
  text: string;
  /** Identifier value without brackets or quotes, string content, or raw text. */
  value: string;
  /** Upper-case value, used for keyword checks. */
  upper: string;
  start: number;
  end: number;
  /** False for an unterminated string or quoted identifier. */
  closed: boolean;
}

export interface TokenizeResult {
  tokens: Token[];
  comments: Array<[number, number]>;
}

const IDENT_START = /[\p{L}_#]/u;
const IDENT_PART = /[\p{L}\p{N}_@#$]/u;
const TWO_CHAR_OPS = new Set(['<=', '>=', '<>', '!=', '!<', '!>', '+=', '-=', '*=', '/=', '%=', '&=', '|=', '^=', '::']);

export function tokenize(text: string): TokenizeResult {
  const tokens: Token[] = [];
  const comments: Array<[number, number]> = [];
  const n = text.length;
  let i = 0;

  const push = (type: TokenType, start: number, end: number, value: string, closed = true) => {
    tokens.push({ type, text: text.slice(start, end), value, upper: value.toUpperCase(), start, end, closed });
  };

  while (i < n) {
    const c = text[i];
    const next = text[i + 1];

    if (c === ' ' || c === '\t' || c === '\r' || c === '\n' || c === '\f' || c === '\v' || c === ' ') {
      i++;
      continue;
    }
    if (c === '-' && next === '-') {
      const s = i;
      while (i < n && text[i] !== '\n') i++;
      comments.push([s, i]);
      continue;
    }
    if (c === '/' && next === '*') {
      const s = i;
      let depth = 0;
      while (i < n) {
        if (text[i] === '/' && text[i + 1] === '*') { depth++; i += 2; continue; }
        if (text[i] === '*' && text[i + 1] === '/') { depth--; i += 2; if (depth === 0) break; continue; }
        i++;
      }
      comments.push([s, i]);
      continue;
    }
    if (c === "'" || ((c === 'N' || c === 'n') && next === "'")) {
      const s = i;
      i += c === "'" ? 1 : 2;
      let val = '';
      let closed = false;
      while (i < n) {
        if (text[i] === "'") {
          if (text[i + 1] === "'") { val += "'"; i += 2; continue; }
          i++;
          closed = true;
          break;
        }
        val += text[i++];
      }
      push('string', s, i, val, closed);
      continue;
    }
    if (c === '[' || c === '"') {
      const close = c === '[' ? ']' : '"';
      const s = i;
      i++;
      let val = '';
      let closed = false;
      while (i < n) {
        if (text[i] === close) {
          if (text[i + 1] === close) { val += close; i += 2; continue; }
          i++;
          closed = true;
          break;
        }
        if (text[i] === '\n' && c === '[') break; // unterminated bracket while typing
        val += text[i++];
      }
      push('qident', s, i, val, closed);
      continue;
    }
    if (c === '@') {
      const s = i;
      i++;
      if (text[i] === '@') i++;
      while (i < n && IDENT_PART.test(text[i])) i++;
      push('variable', s, i, text.slice(s, i));
      continue;
    }
    if (IDENT_START.test(c)) {
      const s = i;
      i++;
      while (i < n && IDENT_PART.test(text[i])) i++;
      push('word', s, i, text.slice(s, i));
      continue;
    }
    if (c >= '0' && c <= '9') {
      const s = i;
      while (i < n && /[0-9A-Za-z_.]/.test(text[i])) {
        if ((text[i] === 'e' || text[i] === 'E') && (text[i + 1] === '-' || text[i + 1] === '+')) i++;
        i++;
      }
      push('number', s, i, text.slice(s, i));
      continue;
    }
    if (c === '(' || c === ')' || c === ',' || c === ';' || c === '.') {
      push('punct', i, i + 1, c);
      i++;
      continue;
    }
    const two = text.slice(i, i + 2);
    if (TWO_CHAR_OPS.has(two)) {
      push('operator', i, i + 2, two);
      i += 2;
      continue;
    }
    push('operator', i, i + 1, c);
    i++;
  }
  return { tokens, comments };
}

// ---------------------------------------------------------------------------
// Token helpers

export const isPunct = (t: Token | undefined, p: string): boolean => !!t && t.type === 'punct' && t.value === p;
export const isWord = (t: Token | undefined, ...words: string[]): boolean =>
  !!t && t.type === 'word' && (words.length === 0 || words.includes(t.upper));

/** A token that can be part of an object name: a bare word, [quoted] or "quoted" identifier. */
export function isNameToken(t: Token | undefined): boolean {
  return !!t && (t.type === 'qident' || (t.type === 'word' && !RESERVED.has(t.upper)));
}

/** A token that can be an alias after a table name. */
function isAliasToken(t: Token | undefined): boolean {
  return !!t && (t.type === 'qident' || (t.type === 'word' && !ALIAS_STOP.has(t.upper) && !t.value.startsWith('#')));
}

function matchingParen(ts: Token[], open: number): number {
  let depth = 0;
  for (let k = open; k < ts.length; k++) {
    if (isPunct(ts[k], '(')) depth++;
    else if (isPunct(ts[k], ')')) {
      depth--;
      if (depth === 0) return k;
    }
  }
  return ts.length; // unclosed
}

// ---------------------------------------------------------------------------
// Statement splitting

const OBJECT_TYPE_WORDS = new Set(['TABLE', 'VIEW', 'PROC', 'PROCEDURE', 'FUNCTION', 'INDEX', 'SCHEMA', 'TRIGGER',
  'DATABASE', 'SEQUENCE', 'TYPE', 'SYNONYM', 'USER', 'LOGIN', 'ROLE', 'STATISTICS', 'DEFAULT', 'RULE', 'ASSEMBLY']);
const ALWAYS_STARTERS = new Set(['CREATE', 'ALTER', 'DROP', 'DECLARE', 'PRINT', 'RETURN', 'IF', 'WHILE', 'BEGIN',
  'USE', 'TRUNCATE', 'RAISERROR', 'THROW', 'WAITFOR', 'OPEN', 'CLOSE', 'FETCH', 'DEALLOCATE', 'GRANT', 'REVOKE',
  'DENY', 'BACKUP', 'RESTORE', 'DBCC', 'COMMIT', 'ROLLBACK', 'BREAK', 'CONTINUE', 'GOTO']);
const DML_WORDS = new Set(['SELECT', 'INSERT', 'UPDATE', 'DELETE', 'MERGE']);
const NOT_CTE_WITH = new Set(['TIES', 'CHECK', 'NOCHECK', 'RECOMPILE', 'ENCRYPTION', 'SCHEMABINDING', 'EXECUTE',
  'ROLLUP', 'CUBE', 'NOWAIT', 'LOG', 'OVERRIDE', 'VIEW_METADATA', 'NATIVE_COMPILATION', 'XMLNAMESPACES', 'GRANT',
  'MARK', 'RESULT', 'ROLLBACK']);

/** True if the GO token stands alone on its line, which makes it a batch separator. */
function isBatchSeparator(t: Token, text: string): boolean {
  if (t.type !== 'word' || t.upper !== 'GO') return false;
  const lineStart = text.lastIndexOf('\n', t.start - 1) + 1;
  if (text.slice(lineStart, t.start).trim() !== '') return false;
  const lineEnd = text.indexOf('\n', t.end);
  const rest = text.slice(t.end, lineEnd === -1 ? text.length : lineEnd);
  return /^\s*(\d+\s*)?(--.*)?$/.test(rest);
}

export interface StatementRange {
  start: number;
  end: number;
}

/**
 * Splits a script into statements. Boundaries are semicolons, GO lines and
 * keywords that start a new statement at the top level (scripts often omit ';').
 */
export function splitStatements(tokens: Token[], text: string): StatementRange[] {
  const bounds: number[] = [0];
  let depth = 0;
  let first: Token | null = null;
  let withPrelude = false;
  let insertPending = false;

  const reset = (t: Token | null) => {
    first = t;
    withPrelude = !!t && t.upper === 'WITH';
    insertPending = !!t && t.upper === 'INSERT';
  };

  for (let i = 0; i < tokens.length; i++) {
    const t = tokens[i];
    if (isPunct(t, '(')) { depth++; if (!first) reset(t); continue; }
    if (isPunct(t, ')')) { depth = Math.max(0, depth - 1); continue; }
    if (isPunct(t, ';')) { bounds.push(t.end); depth = 0; reset(null); continue; }
    if (isBatchSeparator(t, text)) { bounds.push(t.end); depth = 0; reset(null); continue; }

    if (!first) { reset(t); continue; }
    if (depth !== 0 || t.type !== 'word') continue;

    const u = t.upper;
    const prev = tokens[i - 1];
    const pu = prev && prev.type === 'word' ? prev.upper : '';
    const prevComma = isPunct(prev, ',');
    let starter = false;

    if (DML_WORDS.has(u)) {
      if (u === 'SELECT') {
        starter = !['UNION', 'ALL', 'EXCEPT', 'INTERSECT', 'AS', 'FOR', 'GRANT', 'DENY', 'REVOKE'].includes(pu) && !prevComma;
      } else {
        starter = !['ON', 'THEN', 'INSTEAD', 'AFTER', 'FOR', 'OF', 'GRANT', 'DENY', 'REVOKE', 'TO', 'NOT'].includes(pu) && !prevComma;
      }
      if (starter && withPrelude) { starter = false; withPrelude = false; insertPending = u === 'INSERT'; }
      else if (starter && insertPending && u === 'SELECT') { starter = false; insertPending = false; }
    } else if (u === 'EXEC' || u === 'EXECUTE') {
      starter = !insertPending && !['AS', 'WITH', 'GRANT', 'DENY', 'REVOKE'].includes(pu) && !prevComma;
      if (insertPending) insertPending = false;
    } else if (u === 'WITH') {
      const n1 = tokens[i + 1];
      const n2 = tokens[i + 2];
      starter = isNameToken(n1) && !NOT_CTE_WITH.has(n1.upper) && (isWord(n2, 'AS') || isPunct(n2, '('));
    } else if (ALWAYS_STARTERS.has(u)) {
      starter = true;
      if (u === 'IF' && (OBJECT_TYPE_WORDS.has(pu) || pu === 'COLUMN' || pu === 'CONSTRAINT')) starter = false;
      if (u === 'FETCH' && (pu === 'ROWS' || pu === 'ROW')) starter = false;
      if ((u === 'DROP' || u === 'ALTER') && first && (first as Token).upper === 'ALTER') starter = false;
      if (['GRANT', 'DENY', 'REVOKE'].includes(u) && pu === 'WITH') starter = false;
      if (u === 'CLOSE' && pu === 'SET') starter = false;
      if (u === 'OPEN' && pu === 'SET') starter = false;
    }

    if (starter) {
      bounds.push(t.start);
      reset(t);
    }
  }

  const ranges: StatementRange[] = [];
  for (let k = 0; k < bounds.length; k++) {
    const start = bounds[k];
    const end = k + 1 < bounds.length ? bounds[k + 1] : text.length;
    ranges.push({ start, end });
  }
  return ranges;
}

// ---------------------------------------------------------------------------
// Table references

export type RefKind = 'object' | 'tvf' | 'derived' | 'cte' | 'temp' | 'variable';

export interface TableRef {
  kind: RefKind;
  schema?: string;
  name: string;
  alias?: string;
  /** Output columns for derived tables and CTEs. */
  columns?: string[];
  /** Start offset of the enclosing '(' or -1 for the top level. */
  scope: number;
  start: number;
  end: number;
  /** The keyword that introduced it: FROM, JOIN, UPDATE, INTO... */
  keyword: string;
}

export interface CteDef {
  name: string;
  columns: string[];
}

const REF_KEYWORDS = new Set(['FROM', 'JOIN', 'APPLY', 'USING', 'UPDATE', 'INTO', 'MERGE', 'TABLE', 'DELETE']);
const SELECT_LIST_END = new Set(['FROM', 'INTO', 'WHERE', 'UNION', 'EXCEPT', 'INTERSECT', 'ORDER', 'GROUP', 'HAVING', 'OPTION', 'FOR', 'WINDOW']);

/** Output column names of a SELECT (used for CTEs and derived tables). */
export function selectOutputNames(ts: Token[]): string[] {
  let depth = 0;
  let i = -1;
  for (let k = 0; k < ts.length; k++) {
    if (isPunct(ts[k], '(')) depth++;
    else if (isPunct(ts[k], ')')) depth--;
    else if (depth === 0 && isWord(ts[k], 'SELECT')) { i = k; break; }
  }
  if (i < 0) return [];
  let j = i + 1;
  if (isWord(ts[j], 'DISTINCT', 'ALL')) j++;
  if (isWord(ts[j], 'TOP')) {
    j++;
    if (isPunct(ts[j], '(')) j = matchingParen(ts, j) + 1; else j++;
    if (isWord(ts[j], 'PERCENT')) j++;
    if (isWord(ts[j], 'WITH') && isWord(ts[j + 1], 'TIES')) j += 2;
  }
  const items: Token[][] = [];
  let cur: Token[] = [];
  depth = 0;
  for (let k = j; k < ts.length; k++) {
    const t = ts[k];
    if (depth === 0 && t.type === 'word' && SELECT_LIST_END.has(t.upper)) break;
    if (isPunct(t, '(')) depth++;
    if (isPunct(t, ')')) depth--;
    if (depth === 0 && isPunct(t, ',')) { items.push(cur); cur = []; continue; }
    cur.push(t);
  }
  items.push(cur);

  const names: string[] = [];
  for (const it of items) {
    if (it.length === 0) continue;
    if (it.length >= 3 && isNameToken(it[0]) && it[1].type === 'operator' && it[1].value === '=') {
      names.push(it[0].value);
      continue;
    }
    const last = it[it.length - 1];
    if (last.type !== 'word' && last.type !== 'qident') continue;
    if (it.length === 1) { names.push(last.value); continue; }
    const prev = it[it.length - 2];
    if (prev.type === 'operator') continue;
    names.push(last.value);
  }
  return names;
}

/** Parses "WITH a AS (...), b (x, y) AS (...)" at the start of a statement. */
export function parseCtes(ts: Token[]): CteDef[] {
  const ctes: CteDef[] = [];
  if (!isWord(ts[0], 'WITH')) return ctes;
  let i = 1;
  while (i < ts.length && isNameToken(ts[i])) {
    const name = ts[i].value;
    i++;
    let cols: string[] | undefined;
    if (isPunct(ts[i], '(')) {
      const close = matchingParen(ts, i);
      cols = ts.slice(i + 1, close).filter(isNameToken).map(t => t.value);
      i = close + 1;
    }
    if (!isWord(ts[i], 'AS')) { ctes.push({ name, columns: cols ?? [] }); break; }
    i++;
    if (!isPunct(ts[i], '(')) { ctes.push({ name, columns: cols ?? [] }); break; }
    const close = matchingParen(ts, i);
    ctes.push({ name, columns: cols ?? selectOutputNames(ts.slice(i + 1, close)) });
    i = close + 1;
    if (!isPunct(ts[i], ',')) break;
    i++;
  }
  return ctes;
}

/** Finds every table reference in a statement's tokens. */
export function parseTableRefs(ts: Token[], ctes: CteDef[] = []): TableRef[] {
  const refs: TableRef[] = [];
  const scopes: number[] = [];
  const scopeAt: number[] = new Array(ts.length);
  for (let k = 0; k < ts.length; k++) {
    scopeAt[k] = scopes.length ? scopes[scopes.length - 1] : -1;
    if (isPunct(ts[k], '(')) scopes.push(ts[k].start);
    else if (isPunct(ts[k], ')')) scopes.pop();
  }

  for (let i = 0; i < ts.length; i++) {
    const t = ts[i];
    if (t.type !== 'word' || !REF_KEYWORDS.has(t.upper)) continue;
    const u = t.upper;
    const prev = ts[i - 1];
    if (u === 'UPDATE' && isWord(prev, 'ON', 'FOR', 'AFTER', 'OF')) continue;
    if (u === 'DELETE' && (isWord(prev, 'ON', 'FOR', 'AFTER', 'OF') || isWord(ts[i + 1], 'FROM'))) continue;
    if (u === 'MERGE' && isWord(ts[i + 1], 'INTO')) continue;
    if (u === 'TABLE' && !isWord(prev, 'TRUNCATE', 'DROP', 'ALTER', 'CREATE', 'EXISTS')) continue;
    if (u === 'FROM' && isWord(prev, 'DISTINCT', 'IS')) continue; // IS [NOT] DISTINCT FROM

    let j = i + 1;
    for (;;) {
      const r = parseOneRef(ts, j, u, scopeAt, ctes);
      if (!r) break;
      refs.push(r.ref);
      j = r.next;
      if (u === 'FROM' && isPunct(ts[j], ',')) { j++; continue; }
      break;
    }
  }
  return refs;
}

function parseOneRef(ts: Token[], j: number, kw: string, scopeAt: number[], ctes: CteDef[]):
  { ref: TableRef; next: number } | null {
  const t = ts[j];
  if (!t) return null;
  if (IF_EXISTS(ts, j)) j += 2;
  const first = ts[j];
  if (!first) return null;

  // Derived table: FROM (SELECT ...) alias
  if (isPunct(first, '(') && kw !== 'INTO' && kw !== 'TABLE') {
    const close = matchingParen(ts, j);
    const inner = ts.slice(j + 1, close);
    if (!inner.some(x => isWord(x, 'SELECT'))) return null;
    let k = close + 1;
    let alias: string | undefined;
    if (isWord(ts[k], 'AS')) k++;
    if (isAliasToken(ts[k])) { alias = ts[k].value; k++; }
    let columns = selectOutputNames(inner);
    if (isPunct(ts[k], '(') && alias) {
      const c2 = matchingParen(ts, k);
      columns = ts.slice(k + 1, c2).filter(isNameToken).map(x => x.value);
      k = c2 + 1;
    }
    return {
      ref: { kind: 'derived', name: alias ?? '(subquery)', alias, columns, scope: scopeAt[j] ?? -1, start: first.start, end: ts[Math.min(k, ts.length) - 1].end, keyword: kw },
      next: k
    };
  }

  let kind: RefKind;
  if (first.type === 'variable' && !first.value.startsWith('@@')) kind = 'variable';
  else if (first.type === 'word' && first.value.startsWith('#')) kind = 'temp';
  else if (isNameToken(first)) kind = 'object';
  else return null;

  const parts: string[] = [first.value];
  let k = j + 1;
  while (isPunct(ts[k], '.')) {
    if (isPunct(ts[k + 1], '.')) { parts.push(''); k++; continue; } // db..table
    if (isNameToken(ts[k + 1])) { parts.push(ts[k + 1].value); k += 2; continue; }
    break;
  }
  const name = parts[parts.length - 1];
  const schema = parts.length >= 2 ? parts[parts.length - 2] || undefined : undefined;
  if (!name) return null;

  if (isPunct(ts[k], '(') && kw !== 'INTO' && kw !== 'TABLE' && kind === 'object') {
    kind = 'tvf';
    k = matchingParen(ts, k) + 1;
  }

  let alias: string | undefined;
  if (isWord(ts[k], 'AS') && isAliasToken(ts[k + 1])) { alias = ts[k + 1].value; k += 2; }
  else if (isAliasToken(ts[k])) { alias = ts[k].value; k++; }

  let columns: string[] | undefined;
  if (kind === 'object' && !schema) {
    const cte = ctes.find(c => c.name.toLowerCase() === name.toLowerCase());
    if (cte) { kind = 'cte'; columns = cte.columns; }
  }

  return {
    ref: { kind, schema, name, alias, columns, scope: scopeAt[j] ?? -1, start: first.start, end: ts[k - 1].end, keyword: kw },
    next: k
  };
}

function IF_EXISTS(ts: Token[], j: number): boolean {
  return isWord(ts[j], 'IF') && isWord(ts[j + 1], 'EXISTS');
}

// ---------------------------------------------------------------------------
// Cursor analysis

export type ContextKind =
  | 'none'            // inside a string or comment
  | 'general'         // start of a statement
  | 'tableRef'        // a table name is expected (after FROM, JOIN, ...)
  | 'procedure'       // after EXEC
  | 'expression'      // SELECT list, WHERE, SET, ...
  | 'joinCondition'   // after ON
  | 'afterTableRef'   // right after "FROM Customers c"
  | 'insertColumns';  // INSERT INTO t ( | )

export interface Analysis {
  kind: ContextKind;
  /** The partial word under the cursor, without a leading '[' */
  prefix: string;
  /** Offset where the inserted text starts (replaces the prefix). */
  replaceStart: number;
  /** True if the prefix started with '[' */
  bracketed: boolean;
  /** Names before the prefix, separated by dots, such as ['c'] for "c.Na" */
  qualifier: string[];
  /** Keyword that introduced a table reference (FROM, JOIN, ...) */
  keyword?: string;
  /** Upper-case previous word, if the token before the cursor is a word. */
  prevWord?: string;
  refs: TableRef[];
  visibleRefs: TableRef[];
  ctes: CteDef[];
  usedAliases: Set<string>;
  insertTarget?: { schema?: string; name: string };
  joinTarget?: TableRef;
  /** Database from the last USE statement before the cursor. */
  database?: string;
  variables: string[];
  tempTables: string[];
}

const CLAUSE_WORDS = new Set(['SELECT', 'FROM', 'JOIN', 'APPLY', 'USING', 'WHERE', 'ON', 'BY', 'HAVING', 'SET',
  'VALUES', 'UPDATE', 'INTO', 'MERGE', 'EXEC', 'EXECUTE', 'WHEN', 'THEN', 'ELSE', 'RETURN', 'OUTPUT', 'IF', 'WHILE',
  'DECLARE', 'PRINT', 'DELETE', 'INSERT', 'TABLE', 'CASE', 'AND', 'OR', 'TOP']);
const TABLE_POSITION_WORDS = new Set(['FROM', 'JOIN', 'APPLY', 'USING', 'UPDATE', 'INTO', 'MERGE', 'TABLE', 'DELETE']);
const TABLE_CLAUSES = new Set(['FROM', 'JOIN', 'APPLY', 'USING', 'MERGE', 'UPDATE', 'INTO', 'TABLE', 'DELETE']);

function emptyAnalysis(offset: number): Analysis {
  return {
    kind: 'none', prefix: '', replaceStart: offset, bracketed: false, qualifier: [], refs: [], visibleRefs: [],
    ctes: [], usedAliases: new Set(), variables: [], tempTables: []
  };
}

export function analyze(text: string, offset: number): Analysis {
  const { tokens, comments } = tokenize(text);

  for (const [s, e] of comments) {
    const isLine = text.startsWith('--', s);
    if (s < offset && (offset < e || (isLine && offset === e) || (e === text.length && offset === e))) {
      return emptyAnalysis(offset);
    }
  }
  for (const t of tokens) {
    if (t.type === 'string' && t.start < offset && (offset < t.end || !t.closed)) return emptyAnalysis(offset);
    if (t.start >= offset) break;
  }

  // Partial word under the cursor.
  const head = text.slice(Math.max(0, offset - 300), offset);
  const m = /(\[(?:[^\]\n]|\]\])*|[\p{L}\p{N}_@#$]+)$/u.exec(head);
  let prefix = m ? m[1] : '';
  let replaceStart = offset - prefix.length;
  let bracketed = false;
  if (prefix.startsWith('[')) { bracketed = true; prefix = prefix.slice(1).replace(/\]\]/g, ']'); }
  if (/^[0-9]/.test(prefix)) return emptyAnalysis(offset);

  // The statement around the cursor.
  const ranges = splitStatements(tokens, text);
  let range = ranges[0];
  for (const r of ranges) {
    if (r.start <= replaceStart) range = r; else break;
  }
  const stmt = tokens.filter(t =>
    t.start >= range.start && t.start < range.end && !isPunct(t, ';') && !isBatchSeparator(t, text) &&
    !(prefix !== '' && t.start === replaceStart));

  const ctes = parseCtes(stmt);
  const refs = parseTableRefs(stmt, ctes);
  const usedAliases = new Set(refs.filter(r => r.alias).map(r => r.alias!.toLowerCase()));

  // Tokens before the cursor, and the qualifier ("c." or "dbo.").
  const before = stmt.filter(t => t.end <= replaceStart);
  const qualifier: string[] = [];
  let k = before.length - 1;
  while (k >= 1 && isPunct(before[k], '.') && (before[k - 1].type === 'word' || before[k - 1].type === 'qident')) {
    qualifier.unshift(before[k - 1].value);
    k -= 2;
  }
  const clauseTokens = before.slice(0, k + 1);

  // Visible scopes at the cursor.
  const chain = new Set<number>([-1]);
  {
    const stack: number[] = [];
    for (const t of before) {
      if (isPunct(t, '(')) stack.push(t.start);
      else if (isPunct(t, ')')) stack.pop();
    }
    stack.forEach(s => chain.add(s));
  }
  const visibleRefs = refs.filter(r => chain.has(r.scope));

  // Walk back at the cursor's nesting level.
  let prev: Token | undefined;
  let clause: string | undefined;
  let groupOpen = -1;
  let level = 0;
  for (let i = clauseTokens.length - 1; i >= 0; i--) {
    const t = clauseTokens[i];
    if (isPunct(t, ')')) { if (!prev) prev = t; level++; continue; }
    if (isPunct(t, '(')) {
      if (level > 0) { level--; continue; }
      if (!prev) prev = t;
      groupOpen = i;
      break;
    }
    if (level > 0) continue;
    if (!prev) prev = t;
    if (!clause && t.type === 'word' && CLAUSE_WORDS.has(t.upper)) {
      if (!['AND', 'OR', 'CASE', 'WHEN', 'THEN', 'ELSE', 'TOP'].includes(t.upper)) { clause = t.upper; break; }
    }
  }

  const result: Analysis = {
    kind: 'expression', prefix, replaceStart, bracketed, qualifier, refs, visibleRefs, ctes, usedAliases,
    variables: [], tempTables: [], prevWord: prev && prev.type === 'word' ? prev.upper : undefined
  };

  // Document-wide facts.
  const vars = new Set<string>();
  const temps = new Set<string>();
  for (let i = 0; i < tokens.length; i++) {
    const t = tokens[i];
    if (t.type === 'variable' && !t.value.startsWith('@@') && t.start !== replaceStart && t.value.length > 1) vars.add(t.value);
    if (t.type === 'word' && t.value.startsWith('#') && t.value.length > 1 && t.start !== replaceStart) temps.add(t.value);
    if (t.start < offset && isWord(t, 'USE') && tokens[i + 1] && (tokens[i + 1].type === 'word' || tokens[i + 1].type === 'qident') && tokens[i + 1].end <= replaceStart) {
      result.database = tokens[i + 1].value;
    }
  }
  result.variables = [...vars];
  result.tempTables = [...temps];

  const pu = result.prevWord;

  if (!prev) {
    result.kind = 'general';
  } else if (pu && TABLE_POSITION_WORDS.has(pu) && isTablePosition(clauseTokens, pu)) {
    result.kind = 'tableRef';
    result.keyword = pu;
  } else if (isPunct(prev, ',') && clause === 'FROM') {
    result.kind = 'tableRef';
    result.keyword = 'FROM';
  } else if (pu === 'EXEC' || pu === 'EXECUTE') {
    result.kind = 'procedure';
  } else if (groupOpen > 0 && insertTargetBefore(clauseTokens, groupOpen)) {
    result.kind = 'insertColumns';
    result.insertTarget = insertTargetBefore(clauseTokens, groupOpen);
  } else if (clause && TABLE_CLAUSES.has(clause) && (isNameToken(prev) || isPunct(prev, ')') || prev.type === 'variable' || (prev.type === 'word' && prev.value.startsWith('#')))) {
    result.kind = 'afterTableRef';
  } else if (clause === 'ON') {
    result.kind = 'joinCondition';
    const before2 = visibleRefs.filter(r => r.start < replaceStart);
    result.joinTarget = before2[before2.length - 1];
  } else {
    result.kind = 'expression';
  }

  if (qualifier.length > 0 && result.kind === 'afterTableRef') {
    // "FROM dbo.|" walks back over "dbo ." and lands on FROM via clauseTokens,
    // so this only triggers for odd input; treat it as a table position.
    result.kind = 'tableRef';
  }
  return result;
}

/** UPDATE/DELETE/TABLE only count as table positions in the right syntax. */
function isTablePosition(ts: Token[], word: string): boolean {
  const last = ts[ts.length - 1];
  const before = ts[ts.length - 2];
  if (!last) return false;
  if (word === 'UPDATE' || word === 'DELETE') return !isWord(before, 'ON', 'FOR', 'AFTER', 'OF', 'THEN');
  if (word === 'TABLE') return isWord(before, 'TRUNCATE', 'DROP', 'ALTER', 'CREATE', 'EXISTS');
  return true;
}

function insertTargetBefore(ts: Token[], groupOpen: number): { schema?: string; name: string } | undefined {
  let k = groupOpen - 1;
  if (!isNameToken(ts[k]) && !(ts[k] && ts[k].type === 'word' && ts[k].value.startsWith('#'))) return undefined;
  const name = ts[k].value;
  let schema: string | undefined;
  k--;
  if (isPunct(ts[k], '.') && isNameToken(ts[k - 1])) {
    schema = ts[k - 1].value;
    k -= 2;
    while (isPunct(ts[k], '.') && isNameToken(ts[k - 1])) k -= 2; // db.schema.table
  }
  if (isWord(ts[k], 'INTO') || isWord(ts[k], 'INSERT')) return { schema, name };
  return undefined;
}
