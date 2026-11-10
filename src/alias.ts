// dbForge-style alias generation: the first letter of each word in the name.
//   Customers         -> c
//   SalesOrderHeader  -> soh
//   order_details     -> od
//   XMLDocument       -> xd
// Clashes with existing aliases or reserved words get a number: c1, c2...

import { ALIAS_STOP } from './sqlKeywords';

export function splitWords(name: string): string[] {
  const cleaned = name.replace(/^#+/, '');
  const parts = cleaned.split(/[^\p{L}\p{N}]+/u).filter(Boolean);
  const words: string[] = [];
  for (const p of parts) {
    const m = p.match(/\p{Lu}+(?=\p{Lu}\p{Ll})|\p{Lu}?\p{Ll}+|\p{Lu}+|\p{N}+|\p{L}+/gu);
    words.push(...(m ?? [p]));
  }
  return words;
}

export function baseAlias(name: string): string {
  const letters = splitWords(name)
    .filter(w => /^\p{L}/u.test(w))
    .map(w => w[0])
    .join('');
  return (letters || 't').toLowerCase();
}

export function makeAlias(name: string, used: Set<string>, casing: 'lower' | 'upper' = 'lower'): string {
  const base = baseAlias(name);
  const taken = (a: string) => used.has(a.toLowerCase()) || ALIAS_STOP.has(a.toUpperCase());
  let alias = base;
  for (let n = 1; taken(alias); n++) alias = base + n;
  return casing === 'upper' ? alias.toUpperCase() : alias;
}
