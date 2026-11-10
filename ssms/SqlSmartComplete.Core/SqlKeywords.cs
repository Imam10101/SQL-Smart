using System;
using System.Collections.Generic;
using System.Linq;

namespace SqlSmartComplete
{
    /// <summary>T-SQL reserved words, keywords and built-in functions.</summary>
    public static class SqlKeywords
    {
        private static HashSet<string> Set(string words) =>
            new HashSet<string>(words.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

        private static string[] List(string words) =>
            words.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);

        /// <summary>Official T-SQL reserved keywords.</summary>
        public static readonly HashSet<string> Reserved = Set(@"
ADD ALL ALTER AND ANY AS ASC AUTHORIZATION BACKUP BEGIN BETWEEN BREAK BROWSE BULK BY
CASCADE CASE CHECK CHECKPOINT CLOSE CLUSTERED COALESCE COLLATE COLUMN COMMIT COMPUTE
CONSTRAINT CONTAINS CONTAINSTABLE CONTINUE CONVERT CREATE CROSS CURRENT CURRENT_DATE
CURRENT_TIME CURRENT_TIMESTAMP CURRENT_USER CURSOR DATABASE DBCC DEALLOCATE DECLARE
DEFAULT DELETE DENY DESC DISK DISTINCT DISTRIBUTED DOUBLE DROP DUMP ELSE END ERRLVL
ESCAPE EXCEPT EXEC EXECUTE EXISTS EXIT EXTERNAL FETCH FILE FILLFACTOR FOR FOREIGN
FREETEXT FREETEXTTABLE FROM FULL FUNCTION GOTO GRANT GROUP HAVING HOLDLOCK IDENTITY
IDENTITY_INSERT IDENTITYCOL IF IN INDEX INNER INSERT INTERSECT INTO IS JOIN KEY KILL
LEFT LIKE LINENO LOAD MERGE NATIONAL NOCHECK NONCLUSTERED NOT NULL NULLIF OF OFF
OFFSETS ON OPEN OPENDATASOURCE OPENQUERY OPENROWSET OPENXML OPTION OR ORDER OUTER OVER
PERCENT PIVOT PLAN PRECISION PRIMARY PRINT PROC PROCEDURE PUBLIC RAISERROR READ
READTEXT RECONFIGURE REFERENCES REPLICATION RESTORE RESTRICT RETURN REVERT REVOKE
RIGHT ROLLBACK ROWCOUNT ROWGUIDCOL RULE SAVE SCHEMA SECURITYAUDIT SELECT
SEMANTICKEYPHRASETABLE SEMANTICSIMILARITYDETAILSTABLE SEMANTICSIMILARITYTABLE
SESSION_USER SET SETUSER SHUTDOWN SOME STATISTICS SYSTEM_USER TABLE TABLESAMPLE
TEXTSIZE THEN TO TOP TRAN TRANSACTION TRIGGER TRUNCATE TRY_CONVERT TSEQUAL UNION
UNIQUE UNPIVOT UPDATE UPDATETEXT USE USER VALUES VARYING VIEW WAITFOR WHEN WHERE
WHILE WITH WITHIN WRITETEXT");

        /// <summary>Words that can never be a table alias.</summary>
        public static readonly HashSet<string> AliasStop = new HashSet<string>(
            Reserved.Concat(List("GO OUTPUT OFFSET WINDOW APPLY USING MATCHED THROW RETURNS INCLUDE NOLOCK LOOP HASH REMOTE")),
            StringComparer.OrdinalIgnoreCase);

        public static readonly string[] Keywords = List(@"
SELECT FROM WHERE JOIN INNER LEFT RIGHT FULL OUTER CROSS APPLY ON AND OR NOT IN IS NULL
LIKE BETWEEN EXISTS AS DISTINCT TOP PERCENT WITH TIES GROUP BY ORDER ASC DESC HAVING
UNION ALL EXCEPT INTERSECT CASE WHEN THEN ELSE END INSERT INTO VALUES UPDATE SET DELETE
MERGE USING MATCHED OUTPUT TRUNCATE TABLE CREATE ALTER DROP VIEW PROCEDURE PROC FUNCTION
TRIGGER INDEX SCHEMA DATABASE DECLARE BEGIN COMMIT ROLLBACK TRANSACTION TRAN TRY CATCH
IF WHILE BREAK CONTINUE RETURN RETURNS EXEC EXECUTE PRINT RAISERROR THROW GO USE OVER
PARTITION ROWS RANGE UNBOUNDED PRECEDING FOLLOWING CURRENT ROW OFFSET FETCH NEXT ONLY
PIVOT UNPIVOT FOR PRIMARY KEY FOREIGN REFERENCES CONSTRAINT DEFAULT CHECK UNIQUE
IDENTITY CLUSTERED NONCLUSTERED NOLOCK READUNCOMMITTED OPTION RECOMPILE NOCOUNT
CURSOR OPEN CLOSE DEALLOCATE COLLATE ANY SOME ESCAPE WAITFOR");

        public static readonly string[] KeywordPhrases =
        {
            "GROUP BY", "ORDER BY", "PARTITION BY", "INNER JOIN", "LEFT JOIN", "LEFT OUTER JOIN",
            "RIGHT JOIN", "FULL OUTER JOIN", "CROSS JOIN", "CROSS APPLY", "OUTER APPLY",
            "INSERT INTO", "DELETE FROM", "UNION ALL", "IS NULL", "IS NOT NULL", "NOT IN",
            "NOT EXISTS", "BEGIN TRAN", "COMMIT TRAN", "ROLLBACK TRAN", "BEGIN TRY", "END TRY",
            "BEGIN CATCH", "END CATCH", "SET NOCOUNT ON", "WITH (NOLOCK)"
        };

        public static readonly string[] AfterTableKeywords =
        {
            "AS", "ON", "WHERE", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL OUTER JOIN",
            "CROSS JOIN", "CROSS APPLY", "OUTER APPLY", "JOIN", "GROUP BY", "ORDER BY", "HAVING",
            "UNION", "UNION ALL", "EXCEPT", "INTERSECT", "WITH (NOLOCK)", "SET", "OPTION"
        };

        public static readonly string[] DataTypes = List(@"
int bigint smallint tinyint bit decimal numeric money smallmoney float real
date datetime datetime2 smalldatetime time datetimeoffset
char varchar nchar nvarchar text ntext binary varbinary image
uniqueidentifier xml sql_variant hierarchyid geography geometry");

        public sealed class BuiltinFunction
        {
            public string Name;
            public string Signature;
            public string Description;
            public bool NoParens;
        }

        private static BuiltinFunction F(string name, string sig, string desc, bool noParens = false) =>
            new BuiltinFunction { Name = name, Signature = sig, Description = desc, NoParens = noParens };

        public static readonly BuiltinFunction[] BuiltinFunctions =
        {
            F("COUNT", "COUNT( { [ALL | DISTINCT] expression | * } ) : int", "Number of items in a group."),
            F("COUNT_BIG", "COUNT_BIG( expression | * ) : bigint", "Number of items in a group as bigint."),
            F("SUM", "SUM( [ALL | DISTINCT] expression )", "Sum of the values."),
            F("AVG", "AVG( [ALL | DISTINCT] expression )", "Average of the values."),
            F("MIN", "MIN( expression )", "Minimum value."),
            F("MAX", "MAX( expression )", "Maximum value."),
            F("STRING_AGG", "STRING_AGG( expression, separator ) [WITHIN GROUP (ORDER BY ...)]", "Concatenates values with a separator."),
            F("STDEV", "STDEV( expression )", "Statistical standard deviation."),
            F("VAR", "VAR( expression )", "Statistical variance."),
            F("GROUPING", "GROUPING( column )", "Indicates whether a column in GROUP BY is aggregated."),
            F("ROW_NUMBER", "ROW_NUMBER() OVER ( [PARTITION BY ...] ORDER BY ... )", "Sequential row number within a partition."),
            F("RANK", "RANK() OVER ( [PARTITION BY ...] ORDER BY ... )", "Rank with gaps."),
            F("DENSE_RANK", "DENSE_RANK() OVER ( [PARTITION BY ...] ORDER BY ... )", "Rank without gaps."),
            F("NTILE", "NTILE( n ) OVER ( ... )", "Distributes rows into n groups."),
            F("LAG", "LAG( expression [, offset [, default]] ) OVER ( ... )", "Value from a previous row."),
            F("LEAD", "LEAD( expression [, offset [, default]] ) OVER ( ... )", "Value from a following row."),
            F("FIRST_VALUE", "FIRST_VALUE( expression ) OVER ( ... )", "First value in an ordered set."),
            F("LAST_VALUE", "LAST_VALUE( expression ) OVER ( ... )", "Last value in an ordered set."),
            F("LEN", "LEN( string ) : int", "Number of characters, excluding trailing spaces."),
            F("DATALENGTH", "DATALENGTH( expression ) : int", "Number of bytes used."),
            F("LEFT", "LEFT( string, count )", "Left part of a string."),
            F("RIGHT", "RIGHT( string, count )", "Right part of a string."),
            F("SUBSTRING", "SUBSTRING( string, start, length )", "Part of a string."),
            F("LTRIM", "LTRIM( string )", "Removes leading spaces."),
            F("RTRIM", "RTRIM( string )", "Removes trailing spaces."),
            F("TRIM", "TRIM( [characters FROM] string )", "Removes leading and trailing spaces."),
            F("UPPER", "UPPER( string )", "Converts to upper case."),
            F("LOWER", "LOWER( string )", "Converts to lower case."),
            F("REPLACE", "REPLACE( string, find, replacement )", "Replaces all occurrences of a substring."),
            F("CHARINDEX", "CHARINDEX( find, string [, start] ) : int", "Position of a substring."),
            F("PATINDEX", "PATINDEX( '%pattern%', string ) : int", "Position of a pattern."),
            F("CONCAT", "CONCAT( value1, value2 [, ...] )", "Concatenates values, treating NULL as empty."),
            F("CONCAT_WS", "CONCAT_WS( separator, value1, value2 [, ...] )", "Concatenates values with a separator."),
            F("FORMAT", "FORMAT( value, format [, culture] )", "Formats a value with a .NET format string."),
            F("STUFF", "STUFF( string, start, length, replacement )", "Deletes part of a string and inserts another."),
            F("REPLICATE", "REPLICATE( string, count )", "Repeats a string."),
            F("REVERSE", "REVERSE( string )", "Reverses a string."),
            F("SPACE", "SPACE( count )", "String of spaces."),
            F("STR", "STR( float [, length [, decimals]] )", "Number as text."),
            F("CHAR", "CHAR( int )", "Character from an ASCII code."),
            F("NCHAR", "NCHAR( int )", "Unicode character from a code."),
            F("ASCII", "ASCII( string ) : int", "ASCII code of the first character."),
            F("UNICODE", "UNICODE( string ) : int", "Unicode code of the first character."),
            F("QUOTENAME", "QUOTENAME( string [, quote_char] )", "Adds delimiters to make a valid identifier."),
            F("STRING_SPLIT", "STRING_SPLIT( string, separator [, enable_ordinal] )", "Splits a string into rows (table function)."),
            F("TRANSLATE", "TRANSLATE( string, characters, translations )", "Replaces characters one for one."),
            F("GETDATE", "GETDATE() : datetime", "Current date and time."),
            F("GETUTCDATE", "GETUTCDATE() : datetime", "Current UTC date and time."),
            F("SYSDATETIME", "SYSDATETIME() : datetime2", "Current date and time with high precision."),
            F("SYSUTCDATETIME", "SYSUTCDATETIME() : datetime2", "Current UTC date and time with high precision."),
            F("SYSDATETIMEOFFSET", "SYSDATETIMEOFFSET() : datetimeoffset", "Current date and time with time zone offset."),
            F("CURRENT_TIMESTAMP", "CURRENT_TIMESTAMP : datetime", "Current date and time (ANSI).", true),
            F("DATEADD", "DATEADD( datepart, number, date )", "Adds an interval to a date."),
            F("DATEDIFF", "DATEDIFF( datepart, startdate, enddate ) : int", "Number of datepart boundaries between two dates."),
            F("DATEDIFF_BIG", "DATEDIFF_BIG( datepart, startdate, enddate ) : bigint", "DATEDIFF returning bigint."),
            F("DATEPART", "DATEPART( datepart, date ) : int", "Integer part of a date."),
            F("DATENAME", "DATENAME( datepart, date ) : nvarchar", "Name of a date part."),
            F("DATEFROMPARTS", "DATEFROMPARTS( year, month, day ) : date", "Builds a date."),
            F("DATETIMEFROMPARTS", "DATETIMEFROMPARTS( y, m, d, h, mi, s, ms ) : datetime", "Builds a datetime."),
            F("DATETRUNC", "DATETRUNC( datepart, date )", "Truncates a date to the given part."),
            F("EOMONTH", "EOMONTH( date [, months_to_add] ) : date", "Last day of the month."),
            F("YEAR", "YEAR( date ) : int", "Year of a date."),
            F("MONTH", "MONTH( date ) : int", "Month of a date."),
            F("DAY", "DAY( date ) : int", "Day of a date."),
            F("ISDATE", "ISDATE( expression ) : int", "Returns 1 if the value is a valid date."),
            F("CAST", "CAST( expression AS data_type )", "Converts a value to another type."),
            F("CONVERT", "CONVERT( data_type, expression [, style] )", "Converts a value to another type with a style."),
            F("TRY_CAST", "TRY_CAST( expression AS data_type )", "CAST that returns NULL on failure."),
            F("TRY_CONVERT", "TRY_CONVERT( data_type, expression [, style] )", "CONVERT that returns NULL on failure."),
            F("PARSE", "PARSE( string AS data_type [USING culture] )", "Parses text with a culture."),
            F("TRY_PARSE", "TRY_PARSE( string AS data_type [USING culture] )", "PARSE that returns NULL on failure."),
            F("ISNULL", "ISNULL( expression, replacement )", "Replaces NULL with a value."),
            F("COALESCE", "COALESCE( expression1, expression2 [, ...] )", "First non-NULL value."),
            F("NULLIF", "NULLIF( expression1, expression2 )", "NULL if both values are equal."),
            F("IIF", "IIF( condition, true_value, false_value )", "Inline IF."),
            F("CHOOSE", "CHOOSE( index, value1, value2 [, ...] )", "Value at the given position."),
            F("ISNUMERIC", "ISNUMERIC( expression ) : int", "Returns 1 if the value is numeric."),
            F("GREATEST", "GREATEST( value1, value2 [, ...] )", "Largest of the values."),
            F("LEAST", "LEAST( value1, value2 [, ...] )", "Smallest of the values."),
            F("ABS", "ABS( number )", "Absolute value."),
            F("ROUND", "ROUND( number, length [, function] )", "Rounds a number."),
            F("CEILING", "CEILING( number )", "Smallest integer greater than or equal."),
            F("FLOOR", "FLOOR( number )", "Largest integer less than or equal."),
            F("POWER", "POWER( number, power )", "Number raised to a power."),
            F("SQRT", "SQRT( float )", "Square root."),
            F("SQUARE", "SQUARE( float )", "Square."),
            F("SIGN", "SIGN( number )", "Sign of a number: -1, 0 or 1."),
            F("RAND", "RAND( [seed] ) : float", "Random number between 0 and 1."),
            F("LOG", "LOG( float [, base] )", "Logarithm."),
            F("LOG10", "LOG10( float )", "Base-10 logarithm."),
            F("EXP", "EXP( float )", "Exponential value."),
            F("PI", "PI() : float", "The constant pi."),
            F("NEWID", "NEWID() : uniqueidentifier", "New GUID."),
            F("SCOPE_IDENTITY", "SCOPE_IDENTITY() : numeric", "Last identity value inserted in the current scope."),
            F("IDENT_CURRENT", "IDENT_CURRENT( 'table' )", "Last identity value generated for a table."),
            F("OBJECT_ID", "OBJECT_ID( 'name' [, 'type'] ) : int", "Object id of a schema object."),
            F("OBJECT_NAME", "OBJECT_NAME( object_id [, database_id] )", "Name of an object."),
            F("DB_NAME", "DB_NAME( [database_id] )", "Database name."),
            F("DB_ID", "DB_ID( ['name'] ) : int", "Database id."),
            F("SCHEMA_NAME", "SCHEMA_NAME( [schema_id] )", "Schema name."),
            F("USER_NAME", "USER_NAME( [id] )", "Database user name."),
            F("SUSER_SNAME", "SUSER_SNAME( [sid] )", "Login name."),
            F("HOST_NAME", "HOST_NAME()", "Client workstation name."),
            F("APP_NAME", "APP_NAME()", "Application name of the session."),
            F("ERROR_MESSAGE", "ERROR_MESSAGE()", "Message of the error caught in CATCH."),
            F("ERROR_NUMBER", "ERROR_NUMBER()", "Number of the error caught in CATCH."),
            F("ERROR_LINE", "ERROR_LINE()", "Line number of the error caught in CATCH."),
            F("ERROR_PROCEDURE", "ERROR_PROCEDURE()", "Procedure where the error occurred."),
            F("ERROR_SEVERITY", "ERROR_SEVERITY()", "Severity of the error caught in CATCH."),
            F("ERROR_STATE", "ERROR_STATE()", "State of the error caught in CATCH."),
            F("XACT_STATE", "XACT_STATE() : smallint", "State of the current transaction."),
            F("CHECKSUM", "CHECKSUM( * | expression [, ...] ) : int", "Hash value."),
            F("HASHBYTES", "HASHBYTES( 'algorithm', input )", "Hash using MD5, SHA1, SHA2_256 or SHA2_512."),
            F("SERVERPROPERTY", "SERVERPROPERTY( property )", "Information about the server."),
            F("JSON_VALUE", "JSON_VALUE( expression, path )", "Scalar value from JSON."),
            F("JSON_QUERY", "JSON_QUERY( expression [, path] )", "Object or array from JSON."),
            F("JSON_MODIFY", "JSON_MODIFY( expression, path, new_value )", "Updates a JSON value."),
            F("ISJSON", "ISJSON( expression ) : int", "Returns 1 if the text is valid JSON."),
            F("OPENJSON", "OPENJSON( json [, path] ) [WITH (...)]", "Rows from JSON (table function)."),
            F("@@ROWCOUNT", "@@ROWCOUNT : int", "Rows affected by the last statement.", true),
            F("@@IDENTITY", "@@IDENTITY : numeric", "Last identity value inserted in the session.", true),
            F("@@ERROR", "@@ERROR : int", "Error number of the last statement.", true),
            F("@@TRANCOUNT", "@@TRANCOUNT : int", "Number of open transactions.", true),
            F("@@FETCH_STATUS", "@@FETCH_STATUS : int", "Status of the last cursor FETCH.", true),
            F("@@VERSION", "@@VERSION : nvarchar", "SQL Server version.", true),
            F("@@SERVERNAME", "@@SERVERNAME : nvarchar", "Server name.", true),
            F("@@SPID", "@@SPID : smallint", "Session id.", true)
        };
    }
}
