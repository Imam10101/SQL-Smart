# SQL Smart Complete

dbForge SQL Complete-style IntelliSense for SQL Server inside VS Code. It reads your
database's metadata and suggests the right things while you type in `.sql` files.

## What it does

| You type | You get |
|---|---|
| `SELECT * FROM Cus` | `Customers c` with an automatic alias |
| `FROM sales.` | Objects in the `sales` schema |
| `FROM Customers c JOIN ` | `Orders o ON c.CustomerID = o.CustomerID`, built from foreign keys |
| `JOIN Orders o ON ` | `c.CustomerID = o.CustomerID` |
| `SELECT Na` | `c.Name`, qualified with the right alias, even when FROM comes later |
| `c.` | Columns of the table behind `c`, plus "* (all columns)" to expand them |
| `EXEC usp_` | `usp_GetOrders @CustomerID = , @Total = OUTPUT` |
| `INSERT INTO Orders (` | Column names, plus "(all columns)" without identity columns |
| `FROM fn_` | `fn_OrdersOf(@CustomerID) foo` for table functions |
| `WHERE x = @` | Variables declared in the script |
| anything else | Built-in functions with signatures, keywords and data types |

Aliases follow dbForge's style: the first letter of each word. `Customers` becomes `c`,
`SalesOrderHeader` becomes `soh`. A clash adds a number, so the second `Customers` becomes `c1`.
Reserved words are skipped, so `InvoiceNumbers` becomes `in1`.

It also understands subqueries, CTEs, derived tables, `[bracketed]` names, `USE` statements
and scripts without semicolons. It stays quiet inside strings and comments.

## Setup

1. Install the `.vsix` with **Extensions > ... > Install from VSIX**, or run
   `code --install-extension sql-smart-complete-0.1.0.vsix`.
2. Open a `.sql` file. Click **SQL Smart: not connected** in the status bar, or run
   **SQL Smart: Connect to Server** from the Command Palette.
3. Enter the server, choose Windows or SQL Server authentication, then pick a database.

The status bar shows the server and database. Click it to switch database.
Run **SQL Smart: Refresh Metadata** after you create or change tables.
A `USE OtherDb` line in a script makes suggestions come from that database.

SQL logins keep their password in VS Code's secret storage, not in settings.

## Settings

| Setting | Default | Meaning |
|---|---|---|
| `sqlSmart.server` | `localhost` | SQL Server instance |
| `sqlSmart.database` | | Database for suggestions |
| `sqlSmart.authentication` | `windows` | `windows` or `sql` |
| `sqlSmart.alias.enabled` | `true` | Add aliases after FROM, JOIN, APPLY |
| `sqlSmart.alias.useAsKeyword` | `false` | Insert `Customers AS c` |
| `sqlSmart.alias.case` | `lower` | `c` or `C` |
| `sqlSmart.alias.onSpace` | `false` | Also add an alias when you type a full table name and press space |
| `sqlSmart.columns.qualify` | `auto` | `auto`, `always` or `never` prefix columns with the alias |
| `sqlSmart.schemaPrefix` | `whenNotDefault` | Insert `dbo.` only when needed, or always |
| `sqlSmart.defaultSchema` | `dbo` | Schema that needs no prefix |
| `sqlSmart.keywordCase` | `upper` | Case of inserted keywords |

## How it works

- `scripts/metadata.ps1` connects with .NET's SqlClient through Windows PowerShell and reads
  `sys.objects`, `sys.columns`, `sys.parameters` and `sys.foreign_key_columns` in one round trip.
  Connection details pass through environment variables, so passwords never appear on a command line.
- `src/sqlParser.ts` is an error-tolerant T-SQL tokenizer. It finds the statement around the cursor,
  the tables and aliases in it, and what kind of token is expected at the cursor.
- `src/completionEngine.ts` turns that context and the metadata into suggestions.
- `src/alias.ts` generates aliases.
- `src/extension.ts` connects it all to VS Code: commands, status bar, cache and the completion provider.

Metadata is loaded once per database and cached until you refresh or change the connection.

## Development

```
npm install
npm test          # compile and run unit tests
npm run package   # build the .vsix
```

Press F5 in VS Code with this folder open to start an Extension Development Host.

## Notes

- Windows only, because it uses Windows PowerShell's built-in SqlClient.
- If the Microsoft `mssql` extension is also active, both lists merge. Disable its IntelliSense
  with `"mssql.intelliSense.enableIntelliSense": false` to see only these suggestions.
