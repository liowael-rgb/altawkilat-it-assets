# IT Assets – installing the production site

IT Assets is its own site with its own database (owner decision, 22 October 2026). This guide is for IT infrastructure and
the DBA. **Owner decision, 22 October 2026: go to production directly** (database `ITAM_Production`), with no separate
test site. Every production change is approved by the owner first. Until the owner declares it live, production runs in a
**test phase**:

- **Email**: `Mail:Enabled` false, or `Mail:TestRecipient` set to one mailbox (every email goes there, naming the real
  recipients), until the owner approves real sending.
- **Access**: only the IT team at first: in IIS, *Authorization Rules* on the site allow an AD group (for example
  `ALTAWKILATIT Assets Pilot`) and deny all other users; the owner widens it at go-live.
- **Backups**: `ITAM_Production` and the keys folder before every update.

`ITAM_Test` remains possible later for a test or training site; the steps are the same with that name.

**Servers (owner, 22 October 2026): the same servers as the shared platform.** IIS: **ATWK-WEB-1**. SQL Server:
**ATWK-SQL-1** (SQL Server 2025). IT Assets has its own site, application pool, folders and database (`ITAM_Production`) on
them and never uses the shared platform's databases, site or settings. Already in place there for the shared platform, so
usually only to confirm: the ASP.NET Core Hosting Bundle (.NET 10), LDAPS trust to the domain controllers (VTMS reads AD
the same way), and the server's permission to send through the mail relay (CMS sends from the same server). The SQL
connection trusts the server certificate as the shared platform does (`SqlTrustServerCertificate`: true); it stays
encrypted. Nothing here uses or stores a password: SQL Server, Active Directory and the mail relay
accept the site's own Windows identity (the IIS application pool identity).

## What IT prepares (once per site)

1. **Server**: Windows Server with IIS and the **ASP.NET Core 10 Hosting Bundle** (the same as the hub). IIS features:
   *Windows Authentication* on.
2. **Application pool**: *No managed code*, integrated pipeline, identity a **group managed service account** (recommended,
   for example `ALTAWKILAT\svc-itassets$`) or *ApplicationPoolIdentity* (then SQL and AD see the server's computer account).
3. **Site**: its own site (for example `it.altawkilat.com`), **HTTPS only** with the company certificate, physical
   path `E:\ITAssets\Site`. In *Authentication*, set for the site in IIS (not in web.config: IIS locks these sections):
   Anonymous **on**, Windows Authentication **off** (people sign in on the site's own sign-in page).
4. **Folders** (outside the site folder), with *Modify* for the application pool identity:
   - `E:\ITAssets\Keys` – the keys that encrypt the OS and software keys (DPAPI, this server). **Back it up with the
     database.** Without it, the stored keys cannot be read. A move to another server needs these keys moved by IT.
   - `E:\ITAssets\Backups` – the install script's backups of the site folder.
5. **SQL Server** (DBA):
   - Create the database `ITAM_Production` on ATWK-SQL-1 (SQL Server 2025; keep the default compatibility level).
   - The person installing (step 7) needs *db_owner* on it for the install only.
   - After the install, run `deploy\sql\Grant-ItamRuntime.sql` with the pool identity: the site gets the `itam_runtime`
     role only (read and write data; it cannot change the schema).
6. **Active Directory**: the pool identity reads AD over **LDAPS (636)** as VTMS does (domain users can read by default;
   the server must trust the domain controller's certificate). IT Assets never writes to AD.
7. **Mail relay**: allow the server to send through the company relay (anonymous from the server, or the pool identity).
   Keep `Mail:Enabled` false until the owner approves email; then use `Mail:TestRecipient` during the test phase.

## Installing a package

1. On the developer PC: `deploy\Build-ItamPackage.ps1` (runs the checks, then builds
   `outputs\IT Assets site package (<date> <commit>).zip`).
2. On the server, first time only: copy `appsettings.Production.template.json` from the package to
   `E:\ITAssets\Site\appsettings.Production.json` and fill it in (SQL server name, site address, keys folder, domain
   controller, OUs, mail relay). It holds no password.
3. On the server, as an administrator with db_owner on the database:

   ```powershell
   .\Install-ItamSite.ps1 -Package "<the zip>" -SitePath E:\ITAssets\Site -BackupRoot E:\ITAssets\Backups -HealthUrl https://it.altawkilat.com/health -InstallDatabase
   ```

   It puts the site offline, backs up the current files, copies the new ones (your appsettings.Production.json and logs
   stay), installs the database scripts (`Itam.Host.exe --install-database`), brings the site back and checks `/health`.
   If anything fails the site stays offline and the backup path is printed.
4. First time only: the DBA runs `deploy\sql\Grant-ItamRuntime.sql`; then recycle the application pool.

## How the site behaves

- **Sign-in**: the site's sign-in page (owner, 22 October 2026): AD username and password, checked with the domain
  controller over LDAPS (636); the password is never stored or logged. Signed in for 8 hours of work (renewed while working),
  until *Sign out* or the browser is closed. A person is let in when their AD account is enabled and copied into IT Assets.
  Five wrong passwords for one account (or 20 from one PC) within 15 minutes wait 15 minutes before AD is asked again, so
  guessing cannot lock the AD account. Sign-ins and failures are in the event log.
- **Employees**: copied from the users OU every 15 minutes (`Directory:SyncEveryMinutes`, 15 to 1440), and at once with
  *Read AD now* (Administration Center > Employees, IT Manager or System Administrator; kept in the audit log). A read that would disable more than
  half of the enabled employees is refused and logged (wrong OU or partial read): nothing changes.
- **AD computer comparison**: every day at `Directory:CompareAtHour` (Riyadh time), and with *Compare now*.
- **Emails**: written to the outbox in every case; sent only when `Mail:Enabled` is true. With `Mail:TestRecipient` every
  email goes to that one mailbox with a notice naming the real recipients. The Email outbox page shows them all.
- **Database scripts**: the site refuses to start while a script is not installed (the message says to run
  `--install-database`). A changed script that was already installed is refused.
- **Health**: `https://<site>/health` answers `OK` when the site and its database answer (for monitoring).
- **Logs**: the Windows event log (*Application*, source *IIS AspNetCore Module V2* / *IT Assets*). For a start-up problem,
  set `stdoutLogEnabled="true"` in web.config for a few minutes (`logs\stdout`).

## Before production

The owner approves each release. Back up `ITAM_Production` and the keys folder before every update. Production uses its own
database, keys folder and settings; never point the test site at `ITAM_Production`.
