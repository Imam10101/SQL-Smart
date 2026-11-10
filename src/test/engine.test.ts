import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import { makeAlias } from '../alias';
import { DEFAULT_OPTIONS, getSuggestions, Suggestion, aliasForTypedTable } from '../completionEngine';
import { MetadataIndex, parseMetadataOutput } from '../metadata';
import { analyze } from '../sqlParser';

// A tiny database in the same format scripts/metadata.ps1 prints.
const rows = [
  ['S', 'dbo'], ['S', 'sales'],
  ['O', 'dbo', 'Customers', 'U'],
  ['O', 'dbo', 'Orders', 'U'],
  ['O', 'dbo', 'OrderDetails', 'U'],
  ['O', 'dbo', 'Employees', 'U'],
  ['O', 'sales', 'SalesOrderHeader', 'U'],
  ['O', 'dbo', 'vActiveCustomers', 'V'],
  ['O', 'dbo', 'usp_GetOrders', 'P'],
  ['O', 'dbo', 'fn_FullName', 'FN'],
  ['O', 'dbo', 'fn_OrdersOf', 'IF'],
  ['C', 'dbo', 'Customers', 'CustomerID', 'int', '1', '0', '1', '1', '0'],
  ['C', 'dbo', 'Customers', 'Name', 'nvarchar(100)', '2', '0', '0', '0', '0'],
  ['C', 'dbo', 'Customers', 'City', 'nvarchar(50)', '3', '1', '0', '0', '0'],
  ['C', 'dbo', 'Orders', 'OrderID', 'int', '1', '0', '1', '1', '0'],
  ['C', 'dbo', 'Orders', 'CustomerID', 'int', '2', '0', '0', '0', '0'],
  ['C', 'dbo', 'Orders', 'OrderDate', 'datetime', '3', '0', '0', '0', '0'],
  ['C', 'dbo', 'OrderDetails', 'OrderID', 'int', '1', '0', '0', '1', '0'],
  ['C', 'dbo', 'OrderDetails', 'LineNo', 'int', '2', '0', '0', '1', '0'],
  ['C', 'dbo', 'OrderDetails', 'Qty', 'int', '3', '0', '0', '0', '0'],
  ['C', 'dbo', 'Employees', 'EmployeeID', 'int', '1', '0', '1', '1', '0'],
  ['C', 'dbo', 'Employees', 'ManagerID', 'int', '2', '1', '0', '0', '0'],
  ['C', 'sales', 'SalesOrderHeader', 'SalesOrderID', 'int', '1', '0', '1', '1', '0'],
  ['C', 'dbo', 'vActiveCustomers', 'CustomerID', 'int', '1', '0', '0', '0', '0'],
  ['C', 'dbo', 'fn_OrdersOf', 'OrderID', 'int', '1', '0', '0', '0', '0'],
  ['P', 'dbo', 'usp_GetOrders', '@CustomerID', 'int', '1', '0'],
  ['P', 'dbo', 'usp_GetOrders', '@Total', 'money', '2', '1'],
  ['P', 'dbo', 'fn_FullName', '', 'nvarchar(200)', '0', '1'],
  ['P', 'dbo', 'fn_FullName', '@First', 'nvarchar(100)', '1', '0'],
  ['P', 'dbo', 'fn_OrdersOf', '@CustomerID', 'int', '1', '0'],
  ['F', 'FK_Orders_Customers', 'dbo', 'Orders', 'CustomerID', 'dbo', 'Customers', 'CustomerID', '1'],
  ['F', 'FK_OrderDetails_Orders', 'dbo', 'OrderDetails', 'OrderID', 'dbo', 'Orders', 'OrderID', '1'],
  ['F', 'FK_Employees_Manager', 'dbo', 'Employees', 'ManagerID', 'dbo', 'Employees', 'EmployeeID', '1']
];
const idx = new MetadataIndex(parseMetadataOutput('Test', rows.map(r => r.join('\t')).join('\n')));

/** Runs completion at the "|" marker. */
function suggest(sqlWithCursor: string, opt = DEFAULT_OPTIONS): { items: Suggestion[]; kind: string } {
  const offset = sqlWithCursor.indexOf('|');
  const sql = sqlWithCursor.replace('|', '');
  const a = analyze(sql, offset);
  return { items: getSuggestions(a, idx, opt), kind: a.kind };
}
const insertOf = (items: Suggestion[], label: string) => items.find(i => i.label === label)?.insertText;
const labels = (items: Suggestion[]) => items.map(i => i.label);

test('alias generation', () => {
  assert.equal(makeAlias('Customers', new Set()), 'c');
  assert.equal(makeAlias('SalesOrderHeader', new Set()), 'soh');
  assert.equal(makeAlias('order_details', new Set()), 'od');
  assert.equal(makeAlias('XMLDocument', new Set()), 'xd');
  assert.equal(makeAlias('Customers', new Set(['c'])), 'c1');
  assert.equal(makeAlias('Customers', new Set(['c', 'c1'])), 'c2');
  assert.equal(makeAlias('InvoiceNumbers', new Set()), 'in1', 'IN is reserved');
  assert.equal(makeAlias('Customers', new Set(), 'upper'), 'C');
});

test('FROM suggests tables with an automatic alias', () => {
  const { items, kind } = suggest('SELECT * FROM Cu|');
  assert.equal(kind, 'tableRef');
  assert.equal(insertOf(items, 'Customers'), 'Customers c');
  assert.equal(insertOf(items, 'SalesOrderHeader'), 'sales.SalesOrderHeader soh', 'non-default schema is prefixed');
  assert.equal(insertOf(items, 'vActiveCustomers'), 'vActiveCustomers vac');
  assert.ok(!labels(items).includes('usp_GetOrders'), 'procedures are not tables');
});

test('alias avoids aliases already used in the statement', () => {
  const { items } = suggest('SELECT * FROM Customers c JOIN Cu|');
  assert.equal(insertOf(items, 'Customers'), 'Customers c1');
});

test('alias with AS and upper case options', () => {
  const { items } = suggest('SELECT * FROM |', { ...DEFAULT_OPTIONS, aliasUseAs: true, aliasCase: 'upper' });
  assert.equal(insertOf(items, 'Orders'), 'Orders AS O');
});

test('JOIN suggests full foreign-key join conditions', () => {
  const { items } = suggest('SELECT * FROM Customers c JOIN |');
  assert.ok(labels(items).includes('Orders o ON c.CustomerID = o.CustomerID'), labels(items).join('\n'));
  const { items: i2 } = suggest('SELECT * FROM Customers c JOIN Orders o ON c.CustomerID = o.CustomerID JOIN |');
  assert.ok(labels(i2).includes('OrderDetails od ON o.OrderID = od.OrderID'));
});

test('self-referencing foreign key gets a distinct alias', () => {
  const { items } = suggest('SELECT * FROM Employees e LEFT JOIN |');
  assert.ok(labels(items).includes('Employees e1 ON e.ManagerID = e1.EmployeeID'), labels(items).filter(l => l.includes(' ON ')).join('\n'));
});

test('ON suggests the foreign-key condition', () => {
  const { items, kind } = suggest('SELECT * FROM Customers c INNER JOIN Orders o ON |');
  assert.equal(kind, 'joinCondition');
  assert.equal(items[0].insertText, 'c.CustomerID = o.CustomerID');
});

test('alias dot lists that table\'s columns', () => {
  const { items } = suggest('SELECT c.| FROM Customers c');
  assert.deepEqual(items.filter(i => i.kind === 'column').map(i => i.label), ['CustomerID', 'Name', 'City']);
  assert.equal(insertOf(items, 'Name'), 'Name');
  assert.equal(insertOf(items, '* (all columns)'), 'CustomerID, c.Name, c.City');
});

test('SELECT list suggests qualified columns from tables written later', () => {
  const { items, kind } = suggest('SELECT Na| FROM Customers c JOIN Orders o ON c.CustomerID = o.CustomerID');
  assert.equal(kind, 'expression');
  assert.equal(insertOf(items, 'Name'), 'c.Name');
  const orderDate = items.find(i => i.label === 'OrderDate');
  assert.equal(orderDate?.insertText, 'o.OrderDate');
  assert.ok(labels(items).includes('COUNT'), 'built-in functions');
  assert.ok(labels(items).includes('fn_FullName'), 'user functions');
  assert.equal(insertOf(items, 'fn_FullName'), 'dbo.fn_FullName(${1:@First})$0');
});

test('single table without alias inserts bare column names', () => {
  const { items } = suggest('SELECT * FROM Customers WHERE |');
  assert.equal(insertOf(items, 'City'), 'City');
});

test('schema dot lists the schema objects', () => {
  const { items } = suggest('SELECT * FROM sales.|');
  assert.equal(insertOf(items, 'SalesOrderHeader'), 'SalesOrderHeader soh');
});

test('EXEC suggests procedures with parameters', () => {
  const { items, kind } = suggest('EXEC |');
  assert.equal(kind, 'procedure');
  assert.equal(insertOf(items, 'usp_GetOrders'), 'usp_GetOrders @CustomerID = ${1}, @Total = ${2} OUTPUT$0');
});

test('INSERT column list', () => {
  const { items, kind } = suggest('INSERT INTO dbo.Orders (CustomerID, |');
  assert.equal(kind, 'insertColumns');
  assert.equal(insertOf(items, '(all columns)'), 'CustomerID, OrderDate', 'identity column is skipped');
});

test('table function in FROM gets parameters and alias', () => {
  const { items } = suggest('SELECT * FROM fn_|');
  assert.equal(insertOf(items, 'fn_OrdersOf'), 'fn_OrdersOf(${1:@CustomerID}) foo$0');
});

test('after a table, the alias and keywords are suggested', () => {
  const { items, kind } = suggest('SELECT * FROM Customers |');
  assert.equal(kind, 'afterTableRef');
  assert.equal(items[0].label, 'c');
  assert.ok(labels(items).includes('WHERE'));
});

test('subquery scopes are respected', () => {
  const { items } = suggest('SELECT | FROM Customers c WHERE EXISTS (SELECT 1 FROM Orders o WHERE o.CustomerID = c.CustomerID)');
  assert.ok(labels(items).includes('Name'));
  assert.ok(!labels(items).includes('OrderDate'), 'columns of the inner query are hidden from the outer SELECT');
  const { items: inner } = suggest('SELECT * FROM Customers c WHERE EXISTS (SELECT 1 FROM Orders o WHERE |)');
  assert.ok(labels(inner).includes('OrderDate'));
  assert.ok(labels(inner).includes('City'), 'correlated subquery sees the outer table');
});

test('CTE and derived table columns', () => {
  const { items } = suggest('WITH big AS (SELECT CustomerID, COUNT(*) AS Cnt FROM Orders GROUP BY CustomerID) SELECT b.| FROM big b');
  assert.deepEqual(items.filter(i => i.kind === 'column').map(i => i.label), ['CustomerID', 'Cnt']);
  const { items: d } = suggest('SELECT x.| FROM (SELECT Name, City AS Town FROM Customers) x');
  assert.deepEqual(d.filter(i => i.kind === 'column').map(i => i.label), ['Name', 'Town']);
});

test('statements without semicolons are kept apart', () => {
  const sql = 'SELECT * FROM Customers c\nSELECT * FROM |';
  const { items } = suggest(sql);
  assert.equal(insertOf(items, 'Customers'), 'Customers c', 'alias c is free again in the new statement');
});

test('no suggestions inside strings and comments', () => {
  assert.equal(suggest("SELECT 'FROM |'").kind, 'none');
  assert.equal(suggest('-- FROM |').kind, 'none');
  assert.equal(suggest('/* FROM | */').kind, 'none');
});

test('bracketed names', () => {
  const { items } = suggest('SELECT * FROM [Cus|');
  const c = items.find(i => i.label === 'Customers');
  assert.equal(c?.filterText, '[Customers');
  assert.equal(c?.insertText, 'Customers c');
});

test('variables and USE', () => {
  const a = analyze('USE Shop\nDECLARE @Id int\nSELECT * FROM Orders WHERE OrderID = @', 'USE Shop\nDECLARE @Id int\nSELECT * FROM Orders WHERE OrderID = @'.length);
  assert.equal(a.database, 'Shop');
  assert.ok(a.variables.includes('@Id'));
});

test('alias on space for a fully typed table name', () => {
  const sql = 'SELECT * FROM Customers';
  assert.equal(aliasForTypedTable(analyze(sql, sql.length), idx, DEFAULT_OPTIONS), 'c');
  const partial = 'SELECT * FROM Custom';
  assert.equal(aliasForTypedTable(analyze(partial, partial.length), idx, DEFAULT_OPTIONS), undefined);
});
