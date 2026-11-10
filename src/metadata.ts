// Database metadata model, loader and lookup index.

import { execFile } from 'child_process';

export type ObjectKind = 'table' | 'view' | 'procedure' | 'scalarFunction' | 'tableFunction' | 'synonym';

export interface DbColumn {
  name: string;
  type: string;
  ordinal: number;
  nullable: boolean;
  identity: boolean;
  primaryKey: boolean;
  computed: boolean;
}

export interface DbParam {
  name: string;
  type: string;
  ordinal: number;
  output: boolean;
}

export interface DbObject {
  schema: string;
  name: string;
  kind: ObjectKind;
  columns: DbColumn[];
  params: DbParam[];
  returnType?: string;
}

export interface ForeignKey {
  name: string;
  fromSchema: string;
  fromTable: string;
  toSchema: string;
  toTable: string;
  columns: Array<{ from: string; to: string }>;
}

export interface DbMetadata {
  database: string;
  schemas: string[];
  objects: DbObject[];
  foreignKeys: ForeignKey[];
  loadedAt: Date;
}

export interface ConnectionSettings {
  server: string;
  authentication: 'windows' | 'sql';
  user?: string;
  password?: string;
  trustServerCertificate: boolean;
}

const KIND_BY_TYPE: Record<string, ObjectKind> = {
  U: 'table', V: 'view', P: 'procedure', PC: 'procedure', FN: 'scalarFunction', FS: 'scalarFunction',
  IF: 'tableFunction', TF: 'tableFunction', FT: 'tableFunction', SN: 'synonym'
};

const key = (schema: string, name: string) => `${schema.toLowerCase()}.${name.toLowerCase()}`;

/** Parses the TAB-separated output of scripts/metadata.ps1. */
export function parseMetadataOutput(database: string, output: string): DbMetadata {
  const schemas: string[] = [];
  const objects = new Map<string, DbObject>();
  const fks = new Map<string, ForeignKey>();
  const pendingColumns: string[][] = [];
  const pendingParams: string[][] = [];

  for (const line of output.replace(/^﻿/, '').split(/\r?\n/)) {
    if (!line) continue;
    const f = line.split('\t');
    switch (f[0]) {
      case 'S':
        schemas.push(f[1]);
        break;
      case 'O': {
        const kind = KIND_BY_TYPE[f[3]];
        if (kind) objects.set(key(f[1], f[2]), { schema: f[1], name: f[2], kind, columns: [], params: [] });
        break;
      }
      case 'C':
        pendingColumns.push(f);
        break;
      case 'P':
        pendingParams.push(f);
        break;
      case 'F': {
        const id = `${f[2]}.${f[3]}.${f[1]}`;
        let fk = fks.get(id);
        if (!fk) {
          fk = { name: f[1], fromSchema: f[2], fromTable: f[3], toSchema: f[5], toTable: f[6], columns: [] };
          fks.set(id, fk);
        }
        fk.columns.push({ from: f[4], to: f[7] });
        break;
      }
    }
  }

  for (const f of pendingColumns) {
    const obj = objects.get(key(f[1], f[2]));
    if (!obj) continue;
    obj.columns.push({
      name: f[3], type: f[4], ordinal: Number(f[5]), nullable: f[6] === '1', identity: f[7] === '1',
      primaryKey: f[8] === '1', computed: f[9] === '1'
    });
  }
  for (const f of pendingParams) {
    const obj = objects.get(key(f[1], f[2]));
    if (!obj) continue;
    const ordinal = Number(f[5]);
    if (ordinal === 0) { obj.returnType = f[4]; continue; }
    obj.params.push({ name: f[3], type: f[4], ordinal, output: f[6] === '1' });
  }
  for (const obj of objects.values()) {
    obj.columns.sort((a, b) => a.ordinal - b.ordinal);
    obj.params.sort((a, b) => a.ordinal - b.ordinal);
  }

  return { database, schemas, objects: [...objects.values()], foreignKeys: [...fks.values()], loadedAt: new Date() };
}

/** Fast lookups over the metadata. */
export class MetadataIndex {
  private readonly byKey = new Map<string, DbObject>();
  private readonly byName = new Map<string, DbObject[]>();
  private readonly fks = new Map<string, ForeignKey[]>();
  private readonly schemaSet = new Set<string>();

  constructor(public readonly data: DbMetadata, private readonly defaultSchema = 'dbo') {
    for (const s of data.schemas) this.schemaSet.add(s.toLowerCase());
    for (const o of data.objects) {
      this.byKey.set(key(o.schema, o.name), o);
      const n = o.name.toLowerCase();
      const list = this.byName.get(n);
      if (list) list.push(o); else this.byName.set(n, [o]);
      this.schemaSet.add(o.schema.toLowerCase());
    }
    for (const fk of data.foreignKeys) {
      for (const k of new Set([key(fk.fromSchema, fk.fromTable), key(fk.toSchema, fk.toTable)])) {
        const list = this.fks.get(k);
        if (list) list.push(fk); else this.fks.set(k, [fk]);
      }
    }
  }

  get objects(): DbObject[] { return this.data.objects; }
  get schemas(): string[] { return this.data.schemas; }

  find(schema: string | undefined, name: string): DbObject | undefined {
    if (schema) return this.byKey.get(key(schema, name));
    const list = this.byName.get(name.toLowerCase());
    if (!list || list.length === 0) return undefined;
    return list.find(o => o.schema.toLowerCase() === this.defaultSchema.toLowerCase()) ?? list[0];
  }

  hasSchema(name: string): boolean {
    return this.schemaSet.has(name.toLowerCase());
  }

  inSchema(schema: string): DbObject[] {
    const s = schema.toLowerCase();
    return this.data.objects.filter(o => o.schema.toLowerCase() === s);
  }

  foreignKeysOf(obj: DbObject): ForeignKey[] {
    return this.fks.get(key(obj.schema, obj.name)) ?? [];
  }
}

export const sameObject = (a: DbObject, schema: string, name: string) =>
  a.schema.toLowerCase() === schema.toLowerCase() && a.name.toLowerCase() === name.toLowerCase();

// ---------------------------------------------------------------------------
// Loader

function runScript(scriptPath: string, env: Record<string, string>, timeoutMs = 120000): Promise<string> {
  return new Promise((resolve, reject) => {
    execFile(
      'powershell.exe',
      ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', scriptPath],
      { env: { ...process.env, ...env }, maxBuffer: 256 * 1024 * 1024, timeout: timeoutMs, windowsHide: true, encoding: 'utf8' },
      (err, stdout, stderr) => {
        if (err) {
          const msg = (stderr || '').trim() || err.message;
          reject(new Error(msg));
          return;
        }
        resolve(stdout);
      });
  });
}

function connectionEnv(c: ConnectionSettings): Record<string, string> {
  return {
    SQLSMART_SERVER: c.server,
    SQLSMART_AUTH: c.authentication,
    SQLSMART_USER: c.user ?? '',
    SQLSMART_PASSWORD: c.password ?? '',
    SQLSMART_TRUST: c.trustServerCertificate ? '1' : '0'
  };
}

export async function listDatabases(scriptPath: string, c: ConnectionSettings): Promise<string[]> {
  const out = await runScript(scriptPath, { ...connectionEnv(c), SQLSMART_MODE: 'databases' }, 30000);
  return out.replace(/^﻿/, '').split(/\r?\n/).filter(l => l.startsWith('D\t')).map(l => l.slice(2));
}

export async function loadMetadata(scriptPath: string, c: ConnectionSettings, database: string): Promise<DbMetadata> {
  const out = await runScript(scriptPath, { ...connectionEnv(c), SQLSMART_MODE: 'metadata', SQLSMART_DATABASE: database });
  return parseMetadataOutput(database, out);
}
