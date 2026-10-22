-- IT Assets: database rights of the site (run by the DBA on the SQL Server, once per database, after --install-database).
-- Replace <DOMAIN\ITAssetsPool$> with the IIS application pool identity (a group managed service account or the server's
-- computer account DOMAIN\SERVER$). The site only reads and writes data through the itam_runtime role; it cannot change the
-- schema. The database scripts are installed separately by an administrator (Itam.Host.exe --install-database).
-- Use ITAM_Test on the test site and ITAM_Production on the production site.
USE [ITAM_Test];
IF SUSER_ID(N'<DOMAIN\ITAssetsPool$>') IS NULL CREATE LOGIN [<DOMAIN\ITAssetsPool$>] FROM WINDOWS;
IF USER_ID(N'<DOMAIN\ITAssetsPool$>') IS NULL CREATE USER [<DOMAIN\ITAssetsPool$>] FOR LOGIN [<DOMAIN\ITAssetsPool$>];
ALTER ROLE itam_runtime ADD MEMBER [<DOMAIN\ITAssetsPool$>];
-- The site checks at start that the database scripts are installed (reads dbo.itam_schema_migration).
GRANT SELECT ON dbo.itam_schema_migration TO [<DOMAIN\ITAssetsPool$>];
