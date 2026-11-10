# Reads SQL Server metadata for the SQL Smart Complete extension.
# Connection settings arrive in environment variables so the password never
# appears on a command line:
#   SQLSMART_MODE      databases | metadata
#   SQLSMART_SERVER    server or instance name
#   SQLSMART_DATABASE  database (metadata mode)
#   SQLSMART_AUTH      windows | sql
#   SQLSMART_USER      login (sql auth)
#   SQLSMART_PASSWORD  password (sql auth)
#   SQLSMART_TRUST     1 to trust the server certificate
# Output: one record per line, fields separated by TAB, first field is the record type.

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false

try {
    $b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $b['Data Source'] = $env:SQLSMART_SERVER
    if ($env:SQLSMART_MODE -eq 'metadata' -and $env:SQLSMART_DATABASE) {
        $b['Initial Catalog'] = $env:SQLSMART_DATABASE
    } else {
        $b['Initial Catalog'] = 'master'
    }
    if ($env:SQLSMART_AUTH -eq 'sql') {
        $b['Integrated Security'] = $false
        $b['User ID'] = $env:SQLSMART_USER
        $b['Password'] = $env:SQLSMART_PASSWORD
    } else {
        $b['Integrated Security'] = $true
    }
    $b['TrustServerCertificate'] = ($env:SQLSMART_TRUST -eq '1')
    $b['Connect Timeout'] = 15
    $b['Application Name'] = 'SQL Smart Complete'

    if ($env:SQLSMART_MODE -eq 'databases') {
        $sql = @"
SELECT 'D', name FROM sys.databases
WHERE state = 0 AND HAS_DBACCESS(name) = 1
ORDER BY CASE WHEN database_id <= 4 THEN 1 ELSE 0 END, name;
"@
    } else {
        $sql = @"
SET NOCOUNT ON;
SELECT 'S', s.name
FROM sys.schemas s
WHERE s.schema_id < 16384 AND s.name NOT IN ('sys', 'INFORMATION_SCHEMA', 'guest');

SELECT 'O', s.name, o.name, RTRIM(o.type)
FROM sys.objects o
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE o.type IN ('U','V','P','PC','FN','FS','IF','TF','FT','SN')
  AND o.is_ms_shipped = 0;

SELECT 'C', s.name, o.name, c.name,
    CASE
        WHEN t.name IN ('varchar','char','varbinary','binary')
            THEN t.name + '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length AS varchar(10)) END + ')'
        WHEN t.name IN ('nvarchar','nchar')
            THEN t.name + '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length / 2 AS varchar(10)) END + ')'
        WHEN t.name IN ('decimal','numeric')
            THEN t.name + '(' + CAST(c.precision AS varchar(10)) + ',' + CAST(c.scale AS varchar(10)) + ')'
        WHEN t.name IN ('datetime2','time','datetimeoffset')
            THEN t.name + '(' + CAST(c.scale AS varchar(10)) + ')'
        ELSE t.name
    END,
    c.column_id,
    CAST(c.is_nullable AS int),
    CAST(c.is_identity AS int),
    CASE WHEN EXISTS (
        SELECT 1 FROM sys.index_columns ic
        JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
        WHERE i.is_primary_key = 1 AND ic.object_id = c.object_id AND ic.column_id = c.column_id
    ) THEN 1 ELSE 0 END,
    CAST(c.is_computed AS int)
FROM sys.columns c
JOIN sys.objects o ON o.object_id = c.object_id
JOIN sys.schemas s ON s.schema_id = o.schema_id
JOIN sys.types t ON t.user_type_id = c.user_type_id
WHERE o.type IN ('U','V','IF','TF','FT') AND o.is_ms_shipped = 0;

SELECT 'P', s.name, o.name, p.name,
    CASE
        WHEN t.name IN ('varchar','char','varbinary','binary')
            THEN t.name + '(' + CASE WHEN p.max_length = -1 THEN 'max' ELSE CAST(p.max_length AS varchar(10)) END + ')'
        WHEN t.name IN ('nvarchar','nchar')
            THEN t.name + '(' + CASE WHEN p.max_length = -1 THEN 'max' ELSE CAST(p.max_length / 2 AS varchar(10)) END + ')'
        WHEN t.name IN ('decimal','numeric')
            THEN t.name + '(' + CAST(p.precision AS varchar(10)) + ',' + CAST(p.scale AS varchar(10)) + ')'
        ELSE t.name
    END,
    p.parameter_id,
    CAST(p.is_output AS int)
FROM sys.parameters p
JOIN sys.objects o ON o.object_id = p.object_id
JOIN sys.schemas s ON s.schema_id = o.schema_id
JOIN sys.types t ON t.user_type_id = p.user_type_id
WHERE o.type IN ('P','PC','FN','FS','IF','TF','FT') AND o.is_ms_shipped = 0;

SELECT 'F', fk.name, ps.name, pt.name, pc.name, rs.name, rt.name, rc.name, fkc.constraint_column_id
FROM sys.foreign_key_columns fkc
JOIN sys.foreign_keys fk ON fk.object_id = fkc.constraint_object_id
JOIN sys.objects pt ON pt.object_id = fkc.parent_object_id
JOIN sys.schemas ps ON ps.schema_id = pt.schema_id
JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
JOIN sys.objects rt ON rt.object_id = fkc.referenced_object_id
JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
ORDER BY fk.object_id, fkc.constraint_column_id;
"@
    }

    $cn = New-Object System.Data.SqlClient.SqlConnection $b.ConnectionString
    $cn.Open()
    $cmd = $cn.CreateCommand()
    $cmd.CommandText = $sql
    $cmd.CommandTimeout = 120
    $r = $cmd.ExecuteReader()
    $sb = New-Object System.Text.StringBuilder
    do {
        $vals = New-Object object[] $r.FieldCount
        while ($r.Read()) {
            [void]$r.GetValues($vals)
            [void]$sb.Append((($vals -join "`t") -replace "[`r`n]", ' '))
            [void]$sb.Append("`n")
        }
    } while ($r.NextResult())
    $r.Close()
    $cn.Close()
    [Console]::Out.Write($sb.ToString())
    exit 0
}
catch {
    $msg = $_.Exception.Message
    if ($_.Exception.InnerException) { $msg = $_.Exception.InnerException.Message }
    [Console]::Error.WriteLine($msg)
    exit 1
}
