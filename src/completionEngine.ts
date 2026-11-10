// Builds the suggestion list from the cursor analysis and the database metadata.
// It has no VS Code dependency so it can be unit tested with plain Node.

import { makeAlias } from './alias';
import { DbColumn, DbObject, MetadataIndex, sameObject } from './metadata';
import { AFTER_TABLE_KEYWORDS, BUILTIN_FUNCTIONS, DATA_TYPES, KEYWORDS, KEYWORD_PHRASES, RESERVED } from './sqlKeywords';
import { Analysis, TableRef } from './sqlParser';

export type SuggestionKind =
  | 'table' | 'view' | 'column' | 'procedure' | 'function' | 'tableFunction' | 'synonym' | 'schema' | 'alias'
  | 'join' | 'keyword' | 'variable' | 'cte' | 'temp' | 'dataType' | 'snippet';

export interface Suggestion {
  label: string;
  /** Shown on the right of the label, such as the schema or alias. */
  description?: string;
  kind: SuggestionKind;
  detail?: string;
  documentation?: string;
  insertText: string;
  snippet?: boolean;
  filterText?: string;
  sortText: string;
  /** Open the suggestion list again after inserting (used for "dbo."). */
  retrigger?: boolean;
  preselect?: boolean;
}

export interface EngineOptions {
  aliasEnabled: boolean;
  aliasUseAs: boolean;
  aliasCase: 'lower' | 'upper';
  qualifyColumns: 'auto' | 'always' | 'never';
  schemaPrefix: 'whenNotDefault' | 'always';
  defaultSchema: string;
  keywordCase: 'upper' | 'lower';
}

export const DEFAULT_OPTIONS: EngineOptions = {
  aliasEnabled: true,
  aliasUseAs: false,
  aliasCase: 'lower',
  qualifyColumns: 'auto',
  schemaPrefix: 'whenNotDefault',
  defaultSchema: 'dbo',
  keywordCase: 'upper'
};

/** Keywords after which a table gets an automatic alias. */
const ALIAS_KEYWORDS = new Set(['FROM', 'JOIN', 'APPLY', 'USING', 'MERGE']);
const TVF_KEYWORDS = new Set(['FROM', 'JOIN', 'APPLY', 'USING']);

const IDENT_RE = /^(#{1,2})?[A-Za-z_][A-Za-z0-9_@$#]*$/;

export function quoteIdent(name: string): string {
  return IDENT_RE.test(name) && !RESERVED.has(name.toUpperCase()) ? name : `[${name.replace(/]/g, ']]')}]`;
}

const snippetEscape = (s: string) => s.replace(/[\\$}]/g, m => '\\' + m);
const pad = (n: number) => String(n).padStart(4, '0');

class Builder {
  readonly out: Suggestion[] = [];
  private readonly seen = new Set<string>();

  constructor(readonly a: Analysis, readonly idx: MetadataIndex | undefined, readonly opt: EngineOptions) {}

  kw(s: string): string { return this.opt.keywordCase === 'lower' ? s.toLowerCase() : s; }

  filter(name: string): string { return this.a.bracketed ? '[' + name : name; }

  add(s: Suggestion): void {
    const key = `${s.kind}|${s.label}|${s.description ?? ''}|${s.insertText}`;
    if (this.seen.has(key)) return;
    this.seen.add(key);
    if (!s.filterText) s.filterText = this.filter(s.label);
    this.out.push(s);
  }

  // ---- helpers -----------------------------------------------------------

  objectText(o: DbObject, forceSchema = false): string {
    if (this.a.qualifier.length) return quoteIdent(o.name);
    const needSchema = forceSchema || this.opt.schemaPrefix === 'always' ||
      o.schema.toLowerCase() !== this.opt.defaultSchema.toLowerCase();
    return needSchema ? `${quoteIdent(o.schema)}.${quoteIdent(o.name)}` : quoteIdent(o.name);
  }

  refQualifier(r: TableRef): string {
    if (r.alias) return quoteIdent(r.alias);
    if (r.kind === 'variable') return r.name;
    return r.schema ? `${quoteIdent(r.schema)}.${quoteIdent(r.name)}` : quoteIdent(r.name);
  }

  refObject(r: TableRef): DbObject | undefined {
    if (!this.idx || (r.kind !== 'object' && r.kind !== 'tvf')) return undefined;
    return this.idx.find(r.schema, r.name);
  }

  refColumns(r: TableRef): Array<{ name: string; col?: DbColumn }> {
    if (r.columns) return r.columns.map(name => ({ name }));
    const o = this.refObject(r);
    return o ? o.columns.map(c => ({ name: c.name, col: c })) : [];
  }

  aliasText(alias: string): string {
    return this.opt.aliasUseAs ? `${this.kw('AS')} ${alias}` : alias;
  }

  newAlias(name: string): string {
    return makeAlias(name, this.a.usedAliases, this.opt.aliasCase);
  }

  kindOf(o: DbObject): SuggestionKind {
    switch (o.kind) {
      case 'table': return 'table';
      case 'view': return 'view';
      case 'procedure': return 'procedure';
      case 'scalarFunction': return 'function';
      case 'tableFunction': return 'tableFunction';
      default: return 'synonym';
    }
  }

  objectDoc(o: DbObject): string {
    const title = `**${o.schema}.${o.name}** (${o.kind.replace(/([A-Z])/g, ' $1').toLowerCase()})`;
    const lines: string[] = [title];
    if (o.params.length) {
      lines.push('', 'Parameters:', ...o.params.map(p => `- \`${p.name}\` ${p.type}${p.output ? ' OUTPUT' : ''}`));
    }
    if (o.returnType) lines.push('', `Returns: ${o.returnType}`);
    if (o.columns.length) {
      const cols = o.columns.slice(0, 40).map(c => `- \`${c.name}\` ${c.type}${c.primaryKey ? ' PK' : ''}${c.nullable ? '' : ' NOT NULL'}`);
      lines.push('', 'Columns:', ...cols);
      if (o.columns.length > 40) lines.push(`- ... ${o.columns.length - 40} more`);
    }
    return lines.join('\n');
  }

  columnDetail(c: DbColumn | undefined): string {
    if (!c) return 'column';
    const flags = [c.primaryKey ? 'PK' : '', c.identity ? 'identity' : '', c.computed ? 'computed' : '', c.nullable ? 'null' : 'not null']
      .filter(Boolean).join(', ');
    return `${c.type} (${flags})`;
  }

  // ---- item groups -------------------------------------------------------

  columnItems(r: TableRef, qualify: boolean, group: string): void {
    const q = this.refQualifier(r);
    this.refColumns(r).forEach((c, i) => {
      this.add({
        label: c.name,
        description: r.alias ?? r.name,
        kind: 'column',
        detail: this.columnDetail(c.col),
        documentation: `Column of **${r.schema ? r.schema + '.' : ''}${r.name}**${r.alias ? ` (alias \`${r.alias}\`)` : ''}`,
        insertText: qualify ? `${q}.${quoteIdent(c.name)}` : quoteIdent(c.name),
        sortText: `${group}${(r.alias ?? r.name).toLowerCase()}${pad(i)}`
      });
    });
  }

  starItem(r: TableRef, afterDot: boolean): void {
    const q = this.refQualifier(r);
    const cols = this.refColumns(r).map(c => quoteIdent(c.name));
    if (cols.length === 0) return;
    const text = cols.map((c, i) => (afterDot && i === 0 ? c : `${q}.${c}`)).join(', ');
    this.add({
      label: '* (all columns)',
      description: r.alias ?? r.name,
      kind: 'snippet',
      detail: `Expand all ${cols.length} columns`,
      insertText: text,
      filterText: '*',
      sortText: '0'
    });
  }

  tableItem(o: DbObject, withAlias: boolean, group = '1'): void {
    const text = this.objectText(o);
    const alias = withAlias ? this.newAlias(o.name) : undefined;
    const aliasPart = alias ? ' ' + this.aliasText(alias) : '';
    if (o.kind === 'tableFunction') {
      const params = o.params.map((p, i) => `\${${i + 1}:${snippetEscape(p.name)}}`).join(', ');
      this.add({
        label: o.name, description: o.schema, kind: 'tableFunction',
        detail: `table function${alias ? `  inserts ${o.name}(...) ${alias}` : ''}`,
        documentation: this.objectDoc(o),
        insertText: `${snippetEscape(text)}(${params})${snippetEscape(aliasPart)}$0`, snippet: true,
        sortText: `${group}${o.name.toLowerCase()}`
      });
      return;
    }
    this.add({
      label: o.name, description: o.schema, kind: this.kindOf(o),
      detail: `${o.kind}${alias ? `  inserts ${text}${aliasPart}` : ''}`,
      documentation: this.objectDoc(o),
      insertText: text + aliasPart,
      sortText: `${group}${o.name.toLowerCase()}`
    });
  }

  schemaItems(group: string): void {
    for (const s of this.idx?.schemas ?? []) {
      this.add({
        label: s, kind: 'schema', detail: 'schema', insertText: `${quoteIdent(s)}.`,
        sortText: `${group}${s.toLowerCase()}`, retrigger: true
      });
    }
  }

  keywordItems(words: string[], group: string, keepOrder = false): void {
    words.forEach((w, i) => {
      this.add({
        label: this.kw(w), kind: 'keyword', insertText: this.kw(w),
        sortText: keepOrder ? `${group}${pad(i)}` : `${group}${w.toLowerCase()}`
      });
    });
  }

  builtinFunctionItems(group: string): void {
    for (const f of BUILTIN_FUNCTIONS) {
      const name = this.kw(f.name);
      this.add({
        label: name, kind: 'function', detail: f.signature, documentation: f.description,
        insertText: f.noParens ? name : `${name}($0)`, snippet: !f.noParens,
        sortText: `${group}${f.name.toLowerCase()}`
      });
    }
  }

  userFunctionItems(group: string, objects: DbObject[]): void {
    for (const o of objects) {
      if (o.kind !== 'scalarFunction') continue;
      const params = o.params.map((p, i) => `\${${i + 1}:${snippetEscape(p.name)}}`).join(', ');
      this.add({
        label: o.name, description: o.schema, kind: 'function',
        detail: `scalar function${o.returnType ? ' : ' + o.returnType : ''}`,
        documentation: this.objectDoc(o),
        insertText: `${snippetEscape(this.objectText(o, true))}(${params})$0`, snippet: true,
        sortText: `${group}${o.name.toLowerCase()}`
      });
    }
  }

  procedureItems(objects: DbObject[], group: string): void {
    for (const o of objects) {
      if (o.kind !== 'procedure') continue;
      const params = o.params
        .map((p, i) => `${snippetEscape(p.name)} = \${${i + 1}}${p.output ? ' ' + this.kw('OUTPUT') : ''}`)
        .join(', ');
      this.add({
        label: o.name, description: o.schema, kind: 'procedure',
        detail: `procedure${o.params.length ? ` (${o.params.length} parameters)` : ''}`,
        documentation: this.objectDoc(o),
        insertText: `${snippetEscape(this.objectText(o))}${params ? ' ' + params : ''}$0`, snippet: true,
        sortText: `${group}${o.name.toLowerCase()}`
      });
    }
  }

  variableItems(group: string): void {
    for (const v of this.a.variables) {
      this.add({ label: v, kind: 'variable', detail: 'variable', insertText: v, sortText: `${group}${v.toLowerCase()}` });
    }
  }

  /** "JOIN Orders o ON c.CustomerID = o.CustomerID", built from foreign keys. */
  joinItems(): void {
    if (!this.idx) return;
    const existing = this.a.visibleRefs
      .filter(r => r.start < this.a.replaceStart)
      .map(r => ({ r, o: this.refObject(r) }))
      .filter((x): x is { r: TableRef; o: DbObject } => !!x.o);
    const AND = this.kw('AND');
    const ON = this.kw('ON');
    for (const { r, o: ro } of existing) {
      for (const fk of this.idx.foreignKeysOf(ro)) {
        const sides: Array<{ schema: string; name: string; pairs: Array<[string, string]> }> = [];
        if (sameObject(ro, fk.fromSchema, fk.fromTable)) {
          sides.push({ schema: fk.toSchema, name: fk.toTable, pairs: fk.columns.map(c => [c.from, c.to]) });
        }
        if (sameObject(ro, fk.toSchema, fk.toTable)) {
          sides.push({ schema: fk.fromSchema, name: fk.fromTable, pairs: fk.columns.map(c => [c.to, c.from]) });
        }
        for (const side of sides) {
          const cand = this.idx.find(side.schema, side.name);
          if (!cand) continue;
          const alias = this.opt.aliasEnabled ? this.newAlias(cand.name) : undefined;
          const candQ = alias ?? this.objectText(cand);
          const cond = side.pairs
            .map(([refCol, candCol]) => `${this.refQualifier(r)}.${quoteIdent(refCol)} = ${candQ}.${quoteIdent(candCol)}`)
            .join(` ${AND} `);
          const head = `${this.objectText(cand)}${alias ? ' ' + this.aliasText(alias) : ''}`;
          this.add({
            label: `${head} ${ON} ${cond}`,
            kind: 'join',
            detail: `join on foreign key ${fk.name}`,
            documentation: this.objectDoc(cand),
            insertText: `${head} ${ON} ${cond}`,
            filterText: this.filter(cand.name),
            sortText: `0${cand.name.toLowerCase()}`
          });
        }
      }
    }
  }

  /** Conditions for "JOIN Orders o ON |", built from foreign keys. */
  joinConditionItems(): void {
    const t = this.a.joinTarget;
    if (!t || !this.idx) return;
    const to = this.refObject(t);
    if (!to) return;
    const AND = this.kw('AND');
    for (const r of this.a.visibleRefs) {
      if (r === t) continue;
      const ro = this.refObject(r);
      if (!ro) continue;
      for (const fk of this.idx.foreignKeysOf(to)) {
        const conds: string[] = [];
        if (sameObject(to, fk.fromSchema, fk.fromTable) && sameObject(ro, fk.toSchema, fk.toTable)) {
          conds.push(fk.columns.map(c => `${this.refQualifier(r)}.${quoteIdent(c.to)} = ${this.refQualifier(t)}.${quoteIdent(c.from)}`).join(` ${AND} `));
        }
        if (sameObject(to, fk.toSchema, fk.toTable) && sameObject(ro, fk.fromSchema, fk.fromTable)) {
          conds.push(fk.columns.map(c => `${this.refQualifier(r)}.${quoteIdent(c.from)} = ${this.refQualifier(t)}.${quoteIdent(c.to)}`).join(` ${AND} `));
        }
        for (const cond of conds) {
          this.add({
            label: cond, kind: 'join', detail: `foreign key ${fk.name}`, insertText: cond,
            filterText: cond, sortText: '0', preselect: true
          });
        }
      }
    }
  }
}

// ---------------------------------------------------------------------------

function findRef(refs: TableRef[], q: string): TableRef | undefined {
  const l = q.toLowerCase();
  return refs.find(r => r.alias?.toLowerCase() === l) ?? refs.find(r => r.name.toLowerCase() === l);
}

export function getSuggestions(a: Analysis, idx: MetadataIndex | undefined, opt: EngineOptions = DEFAULT_OPTIONS): Suggestion[] {
  if (a.kind === 'none') return [];
  const b = new Builder(a, idx, opt);

  if (a.qualifier.length > 0) {
    qualified(b);
    return b.out;
  }

  switch (a.kind) {
    case 'tableRef': tableRef(b); break;
    case 'procedure':
      b.procedureItems(idx?.objects ?? [], '1');
      b.schemaItems('8');
      b.variableItems('5');
      break;
    case 'insertColumns': insertColumns(b); break;
    case 'afterTableRef': afterTable(b); break;
    case 'joinCondition':
      b.joinConditionItems();
      expression(b);
      break;
    case 'general':
      b.keywordItems(KEYWORD_PHRASES, '6');
      b.keywordItems(KEYWORDS, '6');
      b.variableItems('5');
      break;
    default:
      expression(b);
  }
  return b.out;
}

function qualified(b: Builder): void {
  const { a, idx } = b;
  const q = a.qualifier[a.qualifier.length - 1];

  if (a.qualifier.length === 1) {
    // Ignore the half-typed reference under the cursor, such as "sales" in "FROM sales.|"
    const notUnderCursor = (r: TableRef) => !(r.start < a.replaceStart && r.end >= a.replaceStart - 1);
    const ref = findRef(a.visibleRefs.filter(notUnderCursor), q) ?? findRef(a.refs.filter(notUnderCursor), q);
    if (ref) {
      b.starItem(ref, true);
      b.columnItems(ref, false, '1');
      return;
    }
  }
  if (!idx) return;

  if (a.qualifier.length >= 2) {
    const obj = idx.find(a.qualifier[a.qualifier.length - 2], q);
    if (obj && obj.columns.length) {
      obj.columns.forEach((c, i) => b.add({
        label: c.name, kind: 'column', detail: b.columnDetail(c), insertText: quoteIdent(c.name), sortText: `1${pad(i)}`
      }));
      return;
    }
  }

  if (idx.hasSchema(q)) {
    const objs = idx.inSchema(q);
    switch (a.kind) {
      case 'tableRef': {
        const withAlias = b.opt.aliasEnabled && ALIAS_KEYWORDS.has(a.keyword ?? '');
        const tvf = TVF_KEYWORDS.has(a.keyword ?? '');
        for (const o of objs) {
          if (o.kind === 'table' || o.kind === 'view' || o.kind === 'synonym' || (tvf && o.kind === 'tableFunction')) b.tableItem(o, withAlias);
        }
        break;
      }
      case 'procedure':
        b.procedureItems(objs, '1');
        break;
      default:
        b.userFunctionItems('1', objs);
        for (const o of objs) if (o.kind === 'table' || o.kind === 'view') b.tableItem(o, false, '3');
    }
    return;
  }

  // "Customers." with no alias declared yet: show that table's columns.
  const obj = idx.find(undefined, q);
  if (obj) {
    obj.columns.forEach((c, i) => b.add({
      label: c.name, kind: 'column', detail: b.columnDetail(c), insertText: quoteIdent(c.name), sortText: `1${pad(i)}`
    }));
  }
}

function tableRef(b: Builder): void {
  const { a, idx, opt } = b;
  const kw = a.keyword ?? 'FROM';
  const withAlias = opt.aliasEnabled && ALIAS_KEYWORDS.has(kw);
  const tvf = TVF_KEYWORDS.has(kw);

  if (kw === 'JOIN') b.joinItems();

  for (const o of idx?.objects ?? []) {
    if (o.kind === 'table' || o.kind === 'view' || o.kind === 'synonym' || (tvf && o.kind === 'tableFunction')) {
      b.tableItem(o, withAlias);
    }
  }
  for (const c of a.ctes) {
    const alias = withAlias ? b.newAlias(c.name) : undefined;
    b.add({
      label: c.name, kind: 'cte', detail: 'common table expression', documentation: c.columns.join(', '),
      insertText: quoteIdent(c.name) + (alias ? ' ' + b.aliasText(alias) : ''), sortText: `0${c.name.toLowerCase()}`
    });
  }
  for (const t of a.tempTables) {
    const alias = withAlias ? b.newAlias(t) : undefined;
    b.add({
      label: t, kind: 'temp', detail: 'temporary table',
      insertText: t + (alias ? ' ' + b.aliasText(alias) : ''), sortText: `2${t.toLowerCase()}`
    });
  }
  if (kw !== 'TABLE') b.variableItems('5');
  b.schemaItems('8');
}

function insertColumns(b: Builder): void {
  const { a, idx } = b;
  const t = a.insertTarget;
  if (!t || !idx) return;
  const o = idx.find(t.schema, t.name);
  if (!o) return;
  const writable = o.columns.filter(c => !c.identity && !c.computed);
  if (writable.length) {
    b.add({
      label: '(all columns)', kind: 'snippet', detail: `Insert all ${writable.length} writable columns`,
      insertText: writable.map(c => quoteIdent(c.name)).join(', '), filterText: '(', sortText: '0'
    });
  }
  o.columns.forEach((c, i) => b.add({
    label: c.name, kind: 'column', detail: b.columnDetail(c), insertText: quoteIdent(c.name),
    sortText: `${c.identity || c.computed ? '2' : '1'}${pad(i)}`
  }));
}

function afterTable(b: Builder): void {
  const { a } = b;
  // "FROM Customers |" without an alias yet: offer the generated alias first.
  const last = a.refs.filter(r => r.end <= a.replaceStart).pop();
  if (last && !last.alias && b.opt.aliasEnabled && ALIAS_KEYWORDS.has(last.keyword) && last.kind !== 'derived' && last.kind !== 'variable') {
    const alias = b.newAlias(last.name);
    b.add({
      label: alias, kind: 'alias', detail: `alias for ${last.name}`, insertText: b.aliasText(alias),
      sortText: '00', preselect: true
    });
  }
  b.keywordItems(AFTER_TABLE_KEYWORDS, '1', true);
  b.keywordItems(KEYWORDS, '6');
}

function expression(b: Builder): void {
  const { a, idx, opt } = b;
  const refs = a.visibleRefs.filter(r => r.kind !== 'derived' || r.alias);
  for (const r of refs) {
    const qualify = opt.qualifyColumns === 'always' || (opt.qualifyColumns === 'auto' && (!!r.alias || refs.length > 1));
    b.columnItems(r, qualify, '1');
  }
  for (const r of refs) {
    if (!r.alias) continue;
    b.add({
      label: r.alias, kind: 'alias', detail: `alias of ${r.schema ? r.schema + '.' : ''}${r.name}`,
      insertText: quoteIdent(r.alias), sortText: `2${r.alias.toLowerCase()}`
    });
  }
  b.variableItems('3');
  if (idx) b.userFunctionItems('4', idx.objects);
  b.builtinFunctionItems('4');
  b.keywordItems(KEYWORD_PHRASES, '6');
  b.keywordItems(KEYWORDS, '6');
  b.keywordItems(DATA_TYPES.map(t => t.toUpperCase()), '7');
  b.schemaItems('8');
}

/**
 * For the optional "alias on space" feature: if the user just typed a full table
 * name after FROM/JOIN, return the alias text to insert.
 */
export function aliasForTypedTable(a: Analysis, idx: MetadataIndex | undefined, opt: EngineOptions): string | undefined {
  if (!idx || !opt.aliasEnabled || a.kind !== 'tableRef' || !ALIAS_KEYWORDS.has(a.keyword ?? '') || !a.prefix) return undefined;
  const schema = a.qualifier.length ? a.qualifier[a.qualifier.length - 1] : undefined;
  const o = idx.find(schema, a.prefix);
  if (!o || o.name.toLowerCase() !== a.prefix.toLowerCase()) return undefined;
  if (o.kind !== 'table' && o.kind !== 'view' && o.kind !== 'synonym') return undefined;
  const alias = makeAlias(o.name, a.usedAliases, opt.aliasCase);
  return opt.aliasUseAs ? `${opt.keywordCase === 'lower' ? 'as' : 'AS'} ${alias}` : alias;
}
