import * as vscode from 'vscode';
import { aliasForTypedTable, EngineOptions, getSuggestions, Suggestion, SuggestionKind } from './completionEngine';
import { ConnectionSettings, listDatabases, loadMetadata, MetadataIndex } from './metadata';
import { analyze } from './sqlParser';

const SECTION = 'sqlSmart';
/** Typing a space after these words opens the suggestion list, like dbForge. */
const SPACE_TRIGGERS = new Set(['FROM', 'JOIN', 'APPLY', 'UPDATE', 'INTO', 'EXEC', 'EXECUTE', 'ON', 'TABLE', 'USING', 'MERGE']);

const KIND_MAP: Record<SuggestionKind, vscode.CompletionItemKind> = {
  table: vscode.CompletionItemKind.Struct,
  view: vscode.CompletionItemKind.Interface,
  column: vscode.CompletionItemKind.Field,
  procedure: vscode.CompletionItemKind.Method,
  function: vscode.CompletionItemKind.Function,
  tableFunction: vscode.CompletionItemKind.Function,
  synonym: vscode.CompletionItemKind.Reference,
  schema: vscode.CompletionItemKind.Module,
  alias: vscode.CompletionItemKind.Variable,
  join: vscode.CompletionItemKind.Snippet,
  keyword: vscode.CompletionItemKind.Keyword,
  variable: vscode.CompletionItemKind.Variable,
  cte: vscode.CompletionItemKind.Struct,
  temp: vscode.CompletionItemKind.Struct,
  dataType: vscode.CompletionItemKind.TypeParameter,
  snippet: vscode.CompletionItemKind.Snippet
};

let log: vscode.OutputChannel;

function cfg(): vscode.WorkspaceConfiguration {
  return vscode.workspace.getConfiguration(SECTION);
}

function engineOptions(): EngineOptions {
  const c = cfg();
  return {
    aliasEnabled: c.get('alias.enabled', true),
    aliasUseAs: c.get('alias.useAsKeyword', false),
    aliasCase: c.get('alias.case', 'lower'),
    qualifyColumns: c.get('columns.qualify', 'auto'),
    schemaPrefix: c.get('schemaPrefix', 'whenNotDefault'),
    defaultSchema: c.get('defaultSchema', 'dbo'),
    keywordCase: c.get('keywordCase', 'upper')
  };
}

const secretKey = (server: string, user: string) => `sqlSmart:${server.toLowerCase()}:${user.toLowerCase()}`;

// ---------------------------------------------------------------------------
// Metadata cache, one entry per database

class MetadataStore {
  private readonly pending = new Map<string, Promise<MetadataIndex | undefined>>();
  private readonly loaded = new Map<string, MetadataIndex>();
  private readonly loading = new Set<string>();
  private generation = 0;
  private lastErrorShown = 0;
  readonly scriptPath: string;

  constructor(private readonly ctx: vscode.ExtensionContext, private readonly onChange: () => void) {
    this.scriptPath = ctx.asAbsolutePath('scripts/metadata.ps1');
  }

  async connection(): Promise<ConnectionSettings> {
    const c = cfg();
    const server = c.get<string>('server', 'localhost');
    const authentication = c.get<'windows' | 'sql'>('authentication', 'windows');
    const user = c.get<string>('user', '');
    const password = authentication === 'sql' ? await this.ctx.secrets.get(secretKey(server, user)) : undefined;
    return { server, authentication, user, password, trustServerCertificate: c.get('trustServerCertificate', true) };
  }

  isLoading(db: string): boolean { return this.loading.has(db.toLowerCase()); }
  peek(db: string): MetadataIndex | undefined { return this.loaded.get(db.toLowerCase()); }

  get(db: string): Promise<MetadataIndex | undefined> {
    const key = db.toLowerCase();
    let p = this.pending.get(key);
    if (!p) {
      p = this.load(db, this.generation);
      this.pending.set(key, p);
    }
    return p;
  }

  clear(): void {
    this.generation++;
    this.pending.clear();
    this.loaded.clear();
    this.loading.clear();
    this.onChange();
  }

  private async load(db: string, gen: number): Promise<MetadataIndex | undefined> {
    const key = db.toLowerCase();
    this.loading.add(key);
    this.onChange();
    const started = Date.now();
    try {
      const conn = await this.connection();
      const md = await loadMetadata(this.scriptPath, conn, db);
      if (gen !== this.generation) return undefined;
      const idx = new MetadataIndex(md, cfg().get('defaultSchema', 'dbo'));
      this.loaded.set(key, idx);
      const count = (k: string) => md.objects.filter(o => o.kind === k).length;
      log.appendLine(`[${new Date().toLocaleTimeString()}] Loaded ${conn.server}/${db} in ${Date.now() - started} ms: ` +
        `${count('table')} tables, ${count('view')} views, ${count('procedure')} procedures, ` +
        `${count('scalarFunction') + count('tableFunction')} functions, ${md.foreignKeys.length} foreign keys.`);
      return idx;
    } catch (err) {
      const msg = err instanceof Error ? err.message : String(err);
      log.appendLine(`[${new Date().toLocaleTimeString()}] Failed to load metadata for ${db}: ${msg}`);
      if (Date.now() - this.lastErrorShown > 60000) {
        this.lastErrorShown = Date.now();
        void vscode.window.showWarningMessage(`SQL Smart: could not read metadata for "${db}". ${msg}`, 'Connect', 'Show Log')
          .then(choice => {
            if (choice === 'Connect') void vscode.commands.executeCommand('sqlSmart.connect');
            if (choice === 'Show Log') log.show();
          });
      }
      // Allow a retry after a short pause instead of hammering the server on every keystroke.
      setTimeout(() => { if (this.pending.get(key)) this.pending.delete(key); }, 15000);
      return undefined;
    } finally {
      this.loading.delete(key);
      this.onChange();
    }
  }
}

// ---------------------------------------------------------------------------

export function activate(ctx: vscode.ExtensionContext): void {
  log = vscode.window.createOutputChannel('SQL Smart Complete');
  const status = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 50);
  ctx.subscriptions.push(log, status);

  const updateStatus = () => {
    const editor = vscode.window.activeTextEditor;
    if (!editor || editor.document.languageId !== 'sql') { status.hide(); return; }
    const c = cfg();
    const db = c.get<string>('database', '');
    const server = c.get<string>('server', 'localhost');
    if (!db) {
      status.text = '$(database) SQL Smart: not connected';
      status.tooltip = 'Click to connect to SQL Server';
      status.command = 'sqlSmart.connect';
    } else {
      const idx = store.peek(db);
      const busy = store.isLoading(db);
      status.text = `${busy ? '$(sync~spin)' : '$(database)'} ${server} / ${db}`;
      status.tooltip = idx
        ? `SQL Smart: ${idx.objects.length} objects loaded at ${idx.data.loadedAt.toLocaleTimeString()}. Click to switch database.`
        : busy ? 'SQL Smart: loading metadata...' : 'SQL Smart: click to switch database';
      status.command = 'sqlSmart.selectDatabase';
    }
    status.show();
  };

  const store = new MetadataStore(ctx, updateStatus);

  const preload = () => {
    const db = cfg().get<string>('database', '');
    if (db) void store.get(db);
  };

  // ---- completion -------------------------------------------------------

  const provider: vscode.CompletionItemProvider = {
    async provideCompletionItems(doc, pos, token, context) {
      const text = doc.getText();
      const offset = doc.offsetAt(pos);
      const a = analyze(text, offset);
      if (a.kind === 'none') return undefined;

      if (context.triggerKind === vscode.CompletionTriggerKind.TriggerCharacter && context.triggerCharacter === ' ') {
        if (a.prefix !== '' || a.qualifier.length > 0 || !SPACE_TRIGGERS.has(a.prevWord ?? '')) return undefined;
      }

      const db = a.database ?? cfg().get<string>('database', '');
      const idx = db ? await store.get(db) : undefined;
      if (token.isCancellationRequested) return undefined;

      const range = new vscode.Range(doc.positionAt(a.replaceStart), pos);
      const items = getSuggestions(a, idx, engineOptions()).map(s => toItem(s, range));
      return new vscode.CompletionList(items, false);
    }
  };

  ctx.subscriptions.push(
    vscode.languages.registerCompletionItemProvider([{ language: 'sql' }], provider, '.', ' ')
  );

  // ---- optional: alias when a table name is typed and followed by a space

  ctx.subscriptions.push(vscode.workspace.onDidChangeTextDocument(async e => {
    if (e.document.languageId !== 'sql' || !cfg().get('alias.onSpace', false)) return;
    if (e.reason !== undefined || e.contentChanges.length !== 1) return;
    const ch = e.contentChanges[0];
    if (ch.text !== ' ' || ch.rangeLength !== 0) return;
    const editor = vscode.window.activeTextEditor;
    if (!editor || editor.document !== e.document) return;

    const doc = e.document;
    const offset = ch.rangeOffset;
    const after = doc.getText().slice(offset + 1, offset + 200).split('\n')[0];
    if (/^\s*[\p{L}_\[#]/u.test(after) && !/^\s*(WHERE|JOIN|INNER|LEFT|RIGHT|FULL|CROSS|OUTER|ON|GROUP|ORDER|WITH)\b/i.test(after)) return;

    const a = analyze(doc.getText(), offset);
    const db = a.database ?? cfg().get<string>('database', '');
    if (!db) return;
    const alias = aliasForTypedTable(a, await store.get(db), engineOptions());
    if (!alias || doc.version !== e.document.version) return;

    const insertAt = doc.positionAt(offset + 1);
    await editor.edit(eb => eb.insert(insertAt, alias + ' '), { undoStopBefore: false, undoStopAfter: true });
  }));

  // ---- commands ---------------------------------------------------------

  const pickDatabase = async (): Promise<boolean> => {
    let names: string[];
    try {
      const conn = await store.connection();
      names = await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Notification, title: `SQL Smart: reading databases on ${conn.server}...` },
        () => listDatabases(store.scriptPath, conn));
    } catch (err) {
      const msg = err instanceof Error ? err.message : String(err);
      log.appendLine(`Failed to list databases: ${msg}`);
      void vscode.window.showErrorMessage(`SQL Smart: could not connect. ${msg}`);
      return false;
    }
    const current = cfg().get<string>('database', '');
    const pick = await vscode.window.showQuickPick(
      names.map(n => ({ label: n, description: n === current ? 'current' : undefined })),
      { placeHolder: 'Select the database to use for suggestions' });
    if (!pick) return false;
    await cfg().update('database', pick.label, vscode.ConfigurationTarget.Global);
    return true;
  };

  ctx.subscriptions.push(
    vscode.commands.registerCommand('sqlSmart.connect', async () => {
      const c = cfg();
      const server = await vscode.window.showInputBox({
        title: 'SQL Smart: Connect', prompt: 'SQL Server instance', value: c.get('server', 'localhost'),
        placeHolder: 'localhost, .\\SQLEXPRESS, 192.168.1.10,1433'
      });
      if (!server) return;
      const auth = await vscode.window.showQuickPick(
        [{ label: 'Windows Authentication', value: 'windows' }, { label: 'SQL Server Authentication', value: 'sql' }],
        { title: 'SQL Smart: Connect', placeHolder: 'Authentication' });
      if (!auth) return;
      let user = '';
      if (auth.value === 'sql') {
        user = await vscode.window.showInputBox({ title: 'SQL Smart: Connect', prompt: 'Login', value: c.get('user', '') }) ?? '';
        if (!user) return;
        const password = await vscode.window.showInputBox({ title: 'SQL Smart: Connect', prompt: `Password for ${user}`, password: true });
        if (password === undefined) return;
        await ctx.secrets.store(secretKey(server, user), password);
      }
      await c.update('server', server, vscode.ConfigurationTarget.Global);
      await c.update('authentication', auth.value, vscode.ConfigurationTarget.Global);
      await c.update('user', user, vscode.ConfigurationTarget.Global);
      store.clear();
      if (await pickDatabase()) preload();
    }),

    vscode.commands.registerCommand('sqlSmart.selectDatabase', async () => {
      if (await pickDatabase()) preload();
    }),

    vscode.commands.registerCommand('sqlSmart.refresh', async () => {
      const db = cfg().get<string>('database', '');
      store.clear();
      if (!db) { void vscode.commands.executeCommand('sqlSmart.connect'); return; }
      const idx = await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Window, title: 'SQL Smart: refreshing metadata' },
        () => store.get(db));
      if (idx) void vscode.window.setStatusBarMessage(`SQL Smart: ${idx.objects.length} objects loaded from ${db}`, 4000);
    }),

    vscode.commands.registerCommand('sqlSmart.showLog', () => log.show()),

    vscode.workspace.onDidChangeConfiguration(e => {
      if (['server', 'database', 'authentication', 'user', 'trustServerCertificate', 'defaultSchema']
        .some(k => e.affectsConfiguration(`${SECTION}.${k}`))) {
        store.clear();
        preload();
      }
    }),

    vscode.window.onDidChangeActiveTextEditor(updateStatus)
  );

  updateStatus();
  preload();
}

function toItem(s: Suggestion, range: vscode.Range): vscode.CompletionItem {
  const item = new vscode.CompletionItem({ label: s.label, description: s.description }, KIND_MAP[s.kind]);
  item.range = range;
  item.insertText = s.snippet ? new vscode.SnippetString(s.insertText) : s.insertText;
  item.filterText = s.filterText;
  item.sortText = s.sortText;
  item.detail = s.detail;
  if (s.documentation) item.documentation = new vscode.MarkdownString(s.documentation);
  if (s.preselect) item.preselect = true;
  if (s.retrigger) item.command = { command: 'editor.action.triggerSuggest', title: 'Suggest' };
  return item;
}

export function deactivate(): void { /* nothing to clean up */ }
