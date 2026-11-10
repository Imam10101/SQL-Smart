using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;

namespace SqlSmartComplete
{
    public enum ObjectKind { Table, View, Procedure, ScalarFunction, TableFunction, Synonym }

    public sealed class DbColumn
    {
        public string Name, Type;
        public int Ordinal;
        public bool Nullable, Identity, PrimaryKey, Computed;
    }

    public sealed class DbParam
    {
        public string Name, Type;
        public int Ordinal;
        public bool Output;
    }

    public sealed class DbObject
    {
        public string Schema, Name;
        public ObjectKind Kind;
        public List<DbColumn> Columns = new List<DbColumn>();
        public List<DbParam> Params = new List<DbParam>();
        public string ReturnType;

        public bool Is(string schema, string name) =>
            string.Equals(Schema, schema, StringComparison.OrdinalIgnoreCase) && string.Equals(Name, name, StringComparison.OrdinalIgnoreCase);
    }

    public sealed class ForeignKey
    {
        public string Name, FromSchema, FromTable, ToSchema, ToTable;
        public List<KeyValuePair<string, string>> Columns = new List<KeyValuePair<string, string>>(); // from -> to
    }

    public sealed class DbMetadata
    {
        public string Database;
        public List<string> Schemas = new List<string>();
        public List<DbObject> Objects = new List<DbObject>();
        public List<ForeignKey> ForeignKeys = new List<ForeignKey>();
        public DateTime LoadedAt = DateTime.Now;
    }

    /// <summary>Fast lookups over the metadata.</summary>
    public sealed class MetadataIndex
    {
        private readonly Dictionary<string, DbObject> byKey = new Dictionary<string, DbObject>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<DbObject>> byName = new Dictionary<string, List<DbObject>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<ForeignKey>> fks = new Dictionary<string, List<ForeignKey>>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> schemaSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly string defaultSchema;

        public DbMetadata Data { get; }
        public List<DbObject> Objects => Data.Objects;
        public List<string> Schemas => Data.Schemas;

        private static string Key(string s, string n) => s + "." + n;

        public MetadataIndex(DbMetadata data, string defaultSchema = "dbo")
        {
            Data = data;
            this.defaultSchema = defaultSchema;
            foreach (var s in data.Schemas) schemaSet.Add(s);
            foreach (var o in data.Objects)
            {
                byKey[Key(o.Schema, o.Name)] = o;
                if (!byName.TryGetValue(o.Name, out var list)) byName[o.Name] = list = new List<DbObject>();
                list.Add(o);
                schemaSet.Add(o.Schema);
            }
            foreach (var fk in data.ForeignKeys)
            {
                foreach (var k in new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Key(fk.FromSchema, fk.FromTable), Key(fk.ToSchema, fk.ToTable) })
                {
                    if (!fks.TryGetValue(k, out var list)) fks[k] = list = new List<ForeignKey>();
                    list.Add(fk);
                }
            }
        }

        public DbObject Find(string schema, string name)
        {
            if (schema != null) return byKey.TryGetValue(Key(schema, name), out var o) ? o : null;
            if (!byName.TryGetValue(name, out var list) || list.Count == 0) return null;
            return list.FirstOrDefault(x => string.Equals(x.Schema, defaultSchema, StringComparison.OrdinalIgnoreCase)) ?? list[0];
        }

        public bool HasSchema(string name) => schemaSet.Contains(name);

        public IEnumerable<DbObject> InSchema(string schema) =>
            Data.Objects.Where(o => string.Equals(o.Schema, schema, StringComparison.OrdinalIgnoreCase));

        public List<ForeignKey> ForeignKeysOf(DbObject o) =>
            fks.TryGetValue(Key(o.Schema, o.Name), out var list) ? list : new List<ForeignKey>();
    }

    public static class MetadataLoader
    {
        private static readonly Dictionary<string, ObjectKind> KindByType = new Dictionary<string, ObjectKind>
        {
            ["U"] = ObjectKind.Table, ["V"] = ObjectKind.View, ["P"] = ObjectKind.Procedure, ["PC"] = ObjectKind.Procedure,
            ["FN"] = ObjectKind.ScalarFunction, ["FS"] = ObjectKind.ScalarFunction, ["IF"] = ObjectKind.TableFunction,
            ["TF"] = ObjectKind.TableFunction, ["FT"] = ObjectKind.TableFunction, ["SN"] = ObjectKind.Synonym
        };

        private const string TypeExpr = @"
    CASE
        WHEN t.name IN ('varchar','char','varbinary','binary')
            THEN t.name + '(' + CASE WHEN {0}.max_length = -1 THEN 'max' ELSE CAST({0}.max_length AS varchar(10)) END + ')'
        WHEN t.name IN ('nvarchar','nchar')
            THEN t.name + '(' + CASE WHEN {0}.max_length = -1 THEN 'max' ELSE CAST({0}.max_length / 2 AS varchar(10)) END + ')'
        WHEN t.name IN ('decimal','numeric')
            THEN t.name + '(' + CAST({0}.precision AS varchar(10)) + ',' + CAST({0}.scale AS varchar(10)) + ')'
        ELSE t.name
    END";

        public static readonly string Query = @"
SET NOCOUNT ON;
SELECT 'S', s.name
FROM sys.schemas s
WHERE s.schema_id < 16384 AND s.name NOT IN ('sys', 'INFORMATION_SCHEMA', 'guest');

SELECT 'O', s.name, o.name, RTRIM(o.type)
FROM sys.objects o
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE o.type IN ('U','V','P','PC','FN','FS','IF','TF','FT','SN') AND o.is_ms_shipped = 0;

SELECT 'C', s.name, o.name, c.name," + string.Format(TypeExpr, "c") + @",
    CAST(c.column_id AS varchar(10)),
    CAST(c.is_nullable AS varchar(1)),
    CAST(c.is_identity AS varchar(1)),
    CASE WHEN EXISTS (
        SELECT 1 FROM sys.index_columns ic
        JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
        WHERE i.is_primary_key = 1 AND ic.object_id = c.object_id AND ic.column_id = c.column_id
    ) THEN '1' ELSE '0' END,
    CAST(c.is_computed AS varchar(1))
FROM sys.columns c
JOIN sys.objects o ON o.object_id = c.object_id
JOIN sys.schemas s ON s.schema_id = o.schema_id
JOIN sys.types t ON t.user_type_id = c.user_type_id
WHERE o.type IN ('U','V','IF','TF','FT') AND o.is_ms_shipped = 0;

SELECT 'P', s.name, o.name, p.name," + string.Format(TypeExpr, "p") + @",
    CAST(p.parameter_id AS varchar(10)),
    CAST(p.is_output AS varchar(1))
FROM sys.parameters p
JOIN sys.objects o ON o.object_id = p.object_id
JOIN sys.schemas s ON s.schema_id = o.schema_id
JOIN sys.types t ON t.user_type_id = p.user_type_id
WHERE o.type IN ('P','PC','FN','FS','IF','TF','FT') AND o.is_ms_shipped = 0;

SELECT 'F', fk.name, ps.name, pt.name, pc.name, rs.name, rt.name, rc.name, CAST(fkc.constraint_column_id AS varchar(10))
FROM sys.foreign_key_columns fkc
JOIN sys.foreign_keys fk ON fk.object_id = fkc.constraint_object_id
JOIN sys.objects pt ON pt.object_id = fkc.parent_object_id
JOIN sys.schemas ps ON ps.schema_id = pt.schema_id
JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
JOIN sys.objects rt ON rt.object_id = fkc.referenced_object_id
JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
ORDER BY fk.object_id, fkc.constraint_column_id;";

        /// <summary>Reads metadata from a live database.</summary>
        public static DbMetadata Load(string connectionString, string database)
        {
            var rows = new List<string[]>();
            using (var cn = new SqlConnection(connectionString))
            {
                cn.Open();
                if (!string.IsNullOrEmpty(database) && !string.Equals(cn.Database, database, StringComparison.OrdinalIgnoreCase))
                    cn.ChangeDatabase(database);
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = Query;
                    cmd.CommandTimeout = 120;
                    using (var r = cmd.ExecuteReader())
                    {
                        do
                        {
                            while (r.Read())
                            {
                                var row = new string[r.FieldCount];
                                for (int i = 0; i < row.Length; i++) row[i] = r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i));
                                rows.Add(row);
                            }
                        } while (r.NextResult());
                    }
                }
            }
            return Parse(database, rows);
        }

        /// <summary>Builds the model from tagged rows (same layout as the PowerShell script output).</summary>
        public static DbMetadata Parse(string database, IEnumerable<string[]> rows)
        {
            var md = new DbMetadata { Database = database };
            var objects = new Dictionary<string, DbObject>(StringComparer.OrdinalIgnoreCase);
            var fks = new Dictionary<string, ForeignKey>(StringComparer.OrdinalIgnoreCase);
            var cols = new List<string[]>();
            var pars = new List<string[]>();

            foreach (var f in rows)
            {
                if (f.Length == 0) continue;
                switch (f[0])
                {
                    case "S": md.Schemas.Add(f[1]); break;
                    case "O":
                        if (KindByType.TryGetValue(f[3], out var kind))
                            objects[f[1] + "." + f[2]] = new DbObject { Schema = f[1], Name = f[2], Kind = kind };
                        break;
                    case "C": cols.Add(f); break;
                    case "P": pars.Add(f); break;
                    case "F":
                        string id = f[2] + "." + f[3] + "." + f[1];
                        if (!fks.TryGetValue(id, out var fk))
                            fks[id] = fk = new ForeignKey { Name = f[1], FromSchema = f[2], FromTable = f[3], ToSchema = f[5], ToTable = f[6] };
                        fk.Columns.Add(new KeyValuePair<string, string>(f[4], f[7]));
                        break;
                }
            }
            foreach (var f in cols)
            {
                if (!objects.TryGetValue(f[1] + "." + f[2], out var o)) continue;
                o.Columns.Add(new DbColumn
                {
                    Name = f[3], Type = f[4], Ordinal = int.Parse(f[5]), Nullable = f[6] == "1", Identity = f[7] == "1",
                    PrimaryKey = f[8] == "1", Computed = f[9] == "1"
                });
            }
            foreach (var f in pars)
            {
                if (!objects.TryGetValue(f[1] + "." + f[2], out var o)) continue;
                int ord = int.Parse(f[5]);
                if (ord == 0) { o.ReturnType = f[4]; continue; }
                o.Params.Add(new DbParam { Name = f[3], Type = f[4], Ordinal = ord, Output = f[6] == "1" });
            }
            foreach (var o in objects.Values)
            {
                o.Columns.Sort((x, y) => x.Ordinal.CompareTo(y.Ordinal));
                o.Params.Sort((x, y) => x.Ordinal.CompareTo(y.Ordinal));
            }
            md.Objects = objects.Values.ToList();
            md.ForeignKeys = fks.Values.ToList();
            return md;
        }
    }
}
