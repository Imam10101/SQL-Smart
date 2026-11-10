using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using SqlSmartComplete;

internal static class Program
{
    private static int passed, failed;

    private static readonly MetadataIndex Idx = new MetadataIndex(MetadataLoader.Parse("Test", new[]
    {
        "S|dbo", "S|sales",
        "O|dbo|Customers|U", "O|dbo|Orders|U", "O|dbo|OrderDetails|U", "O|dbo|Employees|U",
        "O|sales|SalesOrderHeader|U", "O|dbo|vActiveCustomers|V", "O|dbo|usp_GetOrders|P",
        "O|dbo|fn_FullName|FN", "O|dbo|fn_OrdersOf|IF",
        "C|dbo|Customers|CustomerID|int|1|0|1|1|0", "C|dbo|Customers|Name|nvarchar(100)|2|0|0|0|0", "C|dbo|Customers|City|nvarchar(50)|3|1|0|0|0",
        "C|dbo|Orders|OrderID|int|1|0|1|1|0", "C|dbo|Orders|CustomerID|int|2|0|0|0|0", "C|dbo|Orders|OrderDate|datetime|3|0|0|0|0",
        "C|dbo|OrderDetails|OrderID|int|1|0|0|1|0", "C|dbo|OrderDetails|LineNo|int|2|0|0|1|0", "C|dbo|OrderDetails|Qty|int|3|0|0|0|0",
        "C|dbo|Employees|EmployeeID|int|1|0|1|1|0", "C|dbo|Employees|ManagerID|int|2|1|0|0|0",
        "C|sales|SalesOrderHeader|SalesOrderID|int|1|0|1|1|0",
        "C|dbo|vActiveCustomers|CustomerID|int|1|0|0|0|0",
        "C|dbo|fn_OrdersOf|OrderID|int|1|0|0|0|0",
        "P|dbo|usp_GetOrders|@CustomerID|int|1|0", "P|dbo|usp_GetOrders|@Total|money|2|1",
        "P|dbo|fn_FullName||nvarchar(200)|0|1", "P|dbo|fn_FullName|@First|nvarchar(100)|1|0",
        "P|dbo|fn_OrdersOf|@CustomerID|int|1|0",
        "F|FK_Orders_Customers|dbo|Orders|CustomerID|dbo|Customers|CustomerID|1",
        "F|FK_OrderDetails_Orders|dbo|OrderDetails|OrderID|dbo|Orders|OrderID|1",
        "F|FK_Employees_Manager|dbo|Employees|ManagerID|dbo|Employees|EmployeeID|1"
    }.Select(l => l.Split('|'))));

    private static (List<Suggestion> items, ContextKind kind) Suggest(string sqlWithCursor, EngineOptions opt = null)
    {
        int offset = sqlWithCursor.IndexOf('|');
        string sql = sqlWithCursor.Remove(offset, 1);
        var a = SqlParser.Analyze(sql, offset);
        return (CompletionEngine.GetSuggestions(a, Idx, opt), a.Kind);
    }

    private static string InsertOf(List<Suggestion> items, string label) => items.FirstOrDefault(i => i.Label == label)?.InsertText;
    private static List<string> Labels(List<Suggestion> items) => items.Select(i => i.Label).ToList();

    private static void Eq(object actual, object expected, string what)
    {
        if (Equals(actual, expected)) return;
        throw new Exception($"{what}: expected [{expected}] but got [{actual}]");
    }

    private static void True(bool cond, string what)
    {
        if (!cond) throw new Exception(what);
    }

    private static void Test(string name, Action body)
    {
        try { body(); passed++; Console.WriteLine("  ok   " + name); }
        catch (Exception e) { failed++; Console.WriteLine("  FAIL " + name + "\n       " + e.Message); }
    }

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("Unit tests");

        Test("alias generation", () =>
        {
            var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Eq(AliasGenerator.Make("Customers", empty), "c", "Customers");
            Eq(AliasGenerator.Make("SalesOrderHeader", empty), "soh", "SalesOrderHeader");
            Eq(AliasGenerator.Make("order_details", empty), "od", "order_details");
            Eq(AliasGenerator.Make("XMLDocument", empty), "xd", "XMLDocument");
            Eq(AliasGenerator.Make("Customers", new HashSet<string> { "c" }), "c1", "clash");
            Eq(AliasGenerator.Make("Customers", new HashSet<string> { "c", "c1" }), "c2", "clash 2");
            Eq(AliasGenerator.Make("InvoiceNumbers", empty), "in1", "reserved");
        });

        Test("FROM suggests tables with alias", () =>
        {
            var (items, kind) = Suggest("SELECT * FROM Cu|");
            Eq(kind, ContextKind.TableRef, "kind");
            Eq(InsertOf(items, "Customers"), "Customers c", "Customers");
            Eq(InsertOf(items, "SalesOrderHeader"), "sales.SalesOrderHeader soh", "schema prefix");
            True(!Labels(items).Contains("usp_GetOrders"), "procedures excluded");
        });

        Test("alias avoids used aliases", () => Eq(InsertOf(Suggest("SELECT * FROM Customers c JOIN Cu|").items, "Customers"), "Customers c1", "c1"));

        Test("alias with AS and upper case", () =>
            Eq(InsertOf(Suggest("SELECT * FROM |", new EngineOptions { AliasUseAs = true, AliasUpper = true }).items, "Orders"), "Orders AS O", "AS O"));

        Test("JOIN suggests foreign-key joins", () =>
        {
            True(Labels(Suggest("SELECT * FROM Customers c JOIN |").items).Contains("Orders o ON c.CustomerID = o.CustomerID"), "Orders join");
            True(Labels(Suggest("SELECT * FROM Customers c JOIN Orders o ON c.CustomerID = o.CustomerID JOIN |").items).Contains("OrderDetails od ON o.OrderID = od.OrderID"), "OrderDetails join");
            True(Labels(Suggest("SELECT * FROM Employees e LEFT JOIN |").items).Contains("Employees e1 ON e.ManagerID = e1.EmployeeID"), "self join");
        });

        Test("ON suggests the condition", () =>
        {
            var (items, kind) = Suggest("SELECT * FROM Customers c INNER JOIN Orders o ON |");
            Eq(kind, ContextKind.JoinCondition, "kind");
            Eq(items[0].InsertText, "c.CustomerID = o.CustomerID", "condition");
        });

        Test("alias dot lists columns", () =>
        {
            var items = Suggest("SELECT c.| FROM Customers c").items;
            Eq(string.Join(",", items.Where(i => i.Kind == SuggestionKind.Column).Select(i => i.Label)), "CustomerID,Name,City", "columns");
            Eq(InsertOf(items, "* (all columns)"), "CustomerID, c.Name, c.City", "star");
        });

        Test("SELECT list qualifies columns", () =>
        {
            var (items, kind) = Suggest("SELECT Na| FROM Customers c JOIN Orders o ON c.CustomerID = o.CustomerID");
            Eq(kind, ContextKind.Expression, "kind");
            Eq(InsertOf(items, "Name"), "c.Name", "Name");
            Eq(InsertOf(items, "OrderDate"), "o.OrderDate", "OrderDate");
            var fn = items.First(i => i.Label == "fn_FullName");
            Eq(fn.InsertText, "dbo.fn_FullName()", "user function");
            Eq(fn.CaretOffset, 16, "caret inside parens");
        });

        Test("single table without alias", () => Eq(InsertOf(Suggest("SELECT * FROM Customers WHERE |").items, "City"), "City", "bare"));
        Test("schema dot", () => Eq(InsertOf(Suggest("SELECT * FROM sales.|").items, "SalesOrderHeader"), "SalesOrderHeader soh", "schema objects"));

        Test("EXEC procedures", () =>
        {
            var (items, kind) = Suggest("EXEC |");
            Eq(kind, ContextKind.Procedure, "kind");
            var p = items.First(i => i.Label == "usp_GetOrders");
            Eq(p.InsertText, "usp_GetOrders @CustomerID = , @Total =  OUTPUT", "params");
            Eq(p.InsertText.Substring(0, p.CaretOffset.Value), "usp_GetOrders @CustomerID = ", "caret");
        });

        Test("INSERT column list", () =>
        {
            var (items, kind) = Suggest("INSERT INTO dbo.Orders (CustomerID, |");
            Eq(kind, ContextKind.InsertColumns, "kind");
            Eq(InsertOf(items, "(all columns)"), "CustomerID, OrderDate", "writable columns");
        });

        Test("table function in FROM", () => Eq(InsertOf(Suggest("SELECT * FROM fn_|").items, "fn_OrdersOf"), "fn_OrdersOf() foo", "tvf"));

        Test("after table suggests alias", () =>
        {
            var (items, kind) = Suggest("SELECT * FROM Customers |");
            Eq(kind, ContextKind.AfterTableRef, "kind");
            Eq(items[0].Label, "c", "alias first");
        });

        Test("subquery scopes", () =>
        {
            var outer = Labels(Suggest("SELECT | FROM Customers c WHERE EXISTS (SELECT 1 FROM Orders o WHERE o.CustomerID = c.CustomerID)").items);
            True(outer.Contains("Name") && !outer.Contains("OrderDate"), "outer scope");
            var inner = Labels(Suggest("SELECT * FROM Customers c WHERE EXISTS (SELECT 1 FROM Orders o WHERE |)").items);
            True(inner.Contains("OrderDate") && inner.Contains("City"), "inner scope");
        });

        Test("CTE and derived columns", () =>
        {
            var cte = Suggest("WITH big AS (SELECT CustomerID, COUNT(*) AS Cnt FROM Orders GROUP BY CustomerID) SELECT b.| FROM big b").items;
            Eq(string.Join(",", cte.Where(i => i.Kind == SuggestionKind.Column).Select(i => i.Label)), "CustomerID,Cnt", "cte");
            var d = Suggest("SELECT x.| FROM (SELECT Name, City AS Town FROM Customers) x").items;
            Eq(string.Join(",", d.Where(i => i.Kind == SuggestionKind.Column).Select(i => i.Label)), "Name,Town", "derived");
        });

        Test("statements without semicolons", () => Eq(InsertOf(Suggest("SELECT * FROM Customers c\nSELECT * FROM |").items, "Customers"), "Customers c", "new statement"));
        Test("GO separates batches", () => Eq(InsertOf(Suggest("SELECT * FROM Customers c\r\nGO\r\nSELECT * FROM Customers c1 JOIN |").items, "Customers"), "Customers c", "after GO c is free"));

        Test("strings and comments", () =>
        {
            Eq(Suggest("SELECT 'FROM |'").kind, ContextKind.None, "string");
            Eq(Suggest("-- FROM |").kind, ContextKind.None, "line comment");
            Eq(Suggest("/* FROM | */").kind, ContextKind.None, "block comment");
        });

        Test("bracketed names", () =>
        {
            var c = Suggest("SELECT * FROM [Cus|").items.First(i => i.Label == "Customers");
            Eq(c.FilterText, "[Customers", "filter");
            Eq(c.InsertText, "Customers c", "insert");
        });

        Test("USE and variables", () =>
        {
            string sql = "USE Shop\nDECLARE @Id int\nSELECT * FROM Orders WHERE OrderID = @";
            var a = SqlParser.Analyze(sql, sql.Length);
            Eq(a.Database, "Shop", "USE");
            True(a.Variables.Contains("@Id"), "variable");
        });

        Test("exact typed name completes like Tab, ignoring case", () =>
        {
            Suggestion Exact(string s)
            {
                int o = s.IndexOf('|');
                return CompletionEngine.ExactMatch(SqlParser.Analyze(s.Remove(o, 1), o), Idx);
            }
            Eq(Exact("SELECT * FROM customers|")?.InsertText, "Customers c", "lower-case table");
            Eq(Exact("select * from CUSTOMERS c join orders|")?.InsertText, "Orders o", "join");
            Eq(Exact("SELECT * FROM dbo.customers|")?.InsertText, "Customers c", "pasted dbo.customers");
            Eq(Exact("SELECT * FROM salesorderheader|")?.InsertText, "sales.SalesOrderHeader soh", "schema added");
            Eq(Exact("SELECT * FROM Cust|"), null, "partial word is left alone");
            Eq(Exact("SELECT name| FROM Customers c JOIN Orders o ON c.CustomerID = o.CustomerID")?.InsertText, "c.Name", "column");
            Eq(Exact("SELECT customerid| FROM Customers c JOIN Orders o ON c.CustomerID = o.CustomerID"), null, "ambiguous column");
            Eq(Exact("SELECT c.Name AS name|  FROM Customers c"), null, "alias after AS");
            Eq(Exact("SELECT * FROM fn_ordersof|"), null, "functions need the list");
        });

        // Optional live test: dotnet run -- <server> <database>
        if (args.Length >= 2)
        {
            Console.WriteLine($"\nLive test against {args[0]} / {args[1]}");
            var cs = new SqlConnectionStringBuilder { DataSource = args[0], InitialCatalog = args[1], IntegratedSecurity = true, TrustServerCertificate = true, ConnectTimeout = 15 }.ConnectionString;
            var sw = Stopwatch.StartNew();
            var md = MetadataLoader.Load(cs, args[1]);
            var idx = new MetadataIndex(md);
            Console.WriteLine($"  loaded {md.Objects.Count} objects and {md.ForeignKeys.Count} foreign keys in {sw.ElapsedMilliseconds} ms");
            var fk = md.ForeignKeys.FirstOrDefault();
            var table = fk != null ? idx.Find(fk.ToSchema, fk.ToTable) : md.Objects.First(o => o.Kind == ObjectKind.Table);
            string tableText = string.Equals(table.Schema, "dbo", StringComparison.OrdinalIgnoreCase) ? table.Name : table.Schema + "." + table.Name;
            foreach (var sql in new[] { $"SELECT * FROM {table.Name.Substring(0, 3)}|", $"SELECT * FROM {tableText} x JOIN |", $"SELECT x.| FROM {tableText} x" })
            {
                int off = sql.IndexOf('|');
                var a = SqlParser.Analyze(sql.Remove(off, 1), off);
                var items = CompletionEngine.GetSuggestions(a, idx);
                Console.WriteLine($"  {sql}  ->  {a.Kind}, {items.Count} items");
                foreach (var s in items.OrderBy(s => s.SortText).Take(4)) Console.WriteLine("      " + s);
            }
        }

        Console.WriteLine($"\n{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }
}
