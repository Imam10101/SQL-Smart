SQL Smart Complete for SSMS 1.0.0
=================================

dbForge-style IntelliSense inside SQL Server Management Studio 21 and 22.

  - Suggests schemas, tables, views, columns, functions, procedures and variables.
  - Adds an automatic alias when you pick a table:  FROM Cus  ->  Customers c
  - Suggests whole joins from foreign keys:  JOIN  ->  Orders o ON c.CustomerID = o.CustomerID
  - "c." lists the columns of the table behind alias c, plus "* (all columns)".
  - Follows the active query window: its server, login and the database selected
    in the toolbar. A USE statement in the script switches the database too.


INSTALL
-------
1. Close SSMS.
2. Double-click SqlSmartCompleteSetup.exe and accept the administrator prompt.
   If Windows SmartScreen appears, click "More info" and then "Run anyway".
   The file is not code-signed.
3. Start SSMS, open a New Query window on a database, and type.

The setup finds every SSMS 21 or later installation and installs the extension into
  <SSMS>\Common7\IDE\Extensions\SqlSmartComplete


USING IT
--------
  SELECT * FROM Cu        pick "Customers"      ->  SELECT * FROM Customers c
  ... JOIN                pick the join         ->  JOIN Orders o ON c.CustomerID = o.CustomerID
  SELECT c.               columns of Customers
  SELECT Na               c.Name, already qualified with the alias
  EXEC usp_               procedure with its parameters
  INSERT INTO T (         column names, or "(all columns)"

Press Tab or Enter to accept a suggestion. Ctrl+Space opens the list at any time.
If you ignore the list and keep typing, it closes at the next space or punctuation
and a new list starts with the next word.

Typed or pasted names are completed too, in any case:
  from customers<space>   ->  FROM Customers c        (also Tab, Enter, comma, ')')
  paste dbo.customers     ->  dbo.Customers c
  where name=             ->  WHERE c.Name=
This only happens when exactly one table or column has that name. If you already
wrote your own alias (FROM customers cu), only the case is fixed.
Turn it off with autoComplete = false in settings.ini.
SSMS's own IntelliSense items appear in the same list. To see only these suggestions,
turn off Tools > Options > Text Editor > Transact-SQL > IntelliSense > Enable IntelliSense.


SETTINGS
--------
%APPDATA%\SqlSmartComplete\settings.ini is created the first time you type in a query window.
Changes apply immediately. You can change the alias style (c, C, AS c), column
qualification, keyword case and the metadata refresh interval.
Metadata reloads every 5 minutes by default, so new tables appear after a short wait.

Microsoft Entra (Azure AD) logins cannot be read from SSMS. For those, fill in the
fallback.* lines in settings.ini with a Windows or SQL login.


TROUBLESHOOTING
---------------
The log is at %LOCALAPPDATA%\SqlSmartComplete\log.txt. It shows when the extension loads,
which connection it uses and how long metadata takes to load.


UNINSTALL
---------
Windows Settings > Apps > "SQL Smart Complete for SSMS" > Uninstall,
or run:  SqlSmartCompleteSetup.exe /uninstall

Silent install for IT:   SqlSmartCompleteSetup.exe /quiet
Custom SSMS location:    SqlSmartCompleteSetup.exe /ide:"D:\Tools\SSMS\Common7\IDE"
