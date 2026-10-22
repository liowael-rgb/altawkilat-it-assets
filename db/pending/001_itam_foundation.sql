-- IT Assets foundation (build plan step 1, 22 October 2026): employees read from Active Directory, roles with regional
-- scope, backups during leave, audit; master data; the asset register; purchase orders and devices received on them.
-- SQL Server 2014 syntax (compatibility level 120). Own database ITAM_Production; nothing shared with other systems.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
EXEC(N'CREATE SCHEMA itam_access AUTHORIZATION dbo');
EXEC(N'CREATE SCHEMA itam_ref AUTHORIZATION dbo');
EXEC(N'CREATE SCHEMA itam_data AUTHORIZATION dbo');

-- Employees: a read-only copy of the Active Directory users OU, keyed by the stable objectGUID (kept after disabling).
CREATE TABLE itam_access.employee(
 employee_id uniqueidentifier NOT NULL PRIMARY KEY,
 account nvarchar(128) NOT NULL CONSTRAINT UX_itam_employee_account UNIQUE,
 display_name nvarchar(200) NOT NULL, email nvarchar(254) NULL, job_title nvarchar(200) NULL, department nvarchar(200) NULL,
 manager_id uniqueidentifier NULL, enabled bit NOT NULL, synced_at_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME());

-- IT roles. The IT Support Specialist holds one row per region; the other roles are national (region NULL).
CREATE TABLE itam_access.role_assignment(
 assignment_id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
 employee_id uniqueidentifier NOT NULL REFERENCES itam_access.employee(employee_id),
 role_code nvarchar(40) NOT NULL CHECK(role_code IN(N'SupportSpecialist',N'TeamLeader',N'SystemAdministrator',N'VoiceAdministrator',
   N'AssetsOfficer',N'ItManager',N'ItAdministrator',N'Auditor',N'InkOfficer')),
 region_code nvarchar(20) NULL,
 created_by uniqueidentifier NOT NULL, created_at_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
 CONSTRAINT CK_itam_role_region CHECK((role_code IN(N'SupportSpecialist',N'InkOfficer') AND region_code IS NOT NULL) OR (role_code NOT IN(N'SupportSpecialist',N'InkOfficer') AND region_code IS NULL)));
CREATE UNIQUE INDEX UX_itam_role ON itam_access.role_assignment(employee_id,role_code,region_code);

-- Backups during leave (My Backup).
CREATE TABLE itam_access.leave_backup(
 backup_id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
 person_id uniqueidentifier NOT NULL REFERENCES itam_access.employee(employee_id),
 backup_employee_id uniqueidentifier NOT NULL REFERENCES itam_access.employee(employee_id),
 leave_from date NOT NULL, leave_to date NOT NULL, note nvarchar(500) NULL,
 set_by uniqueidentifier NOT NULL, set_at_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME(), cancelled_at_utc datetime2 NULL,
 CONSTRAINT CK_itam_backup CHECK(leave_to>=leave_from AND person_id<>backup_employee_id));

-- Append-only audit of every change.
CREATE TABLE itam_access.audit(
 audit_id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY, at_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
 actor_id uniqueidentifier NOT NULL, entity nvarchar(40) NOT NULL, entity_key nvarchar(100) NOT NULL, action nvarchar(30) NOT NULL,
 before_json nvarchar(max) NULL, after_json nvarchar(max) NULL, reason nvarchar(1000) NULL);
CREATE INDEX IX_itam_audit ON itam_access.audit(entity,entity_key,at_utc);

-- Master data ---------------------------------------------------------------------------------------------------------
CREATE TABLE itam_ref.asset_type(
 type_code nvarchar(10) NOT NULL PRIMARY KEY CHECK(type_code COLLATE Latin1_General_100_BIN2 NOT LIKE N'%[^A-Z]%'),
 name_en nvarchar(60) NOT NULL CONSTRAINT UX_itam_type_name UNIQUE, sort_order int NOT NULL DEFAULT 0, active bit NOT NULL DEFAULT 1);
CREATE TABLE itam_ref.region(
 region_code nvarchar(20) NOT NULL PRIMARY KEY, name_en nvarchar(60) NOT NULL, sort_order int NOT NULL DEFAULT 0, active bit NOT NULL DEFAULT 1);
-- IT warehouses and branches: where assets are kept; each in one region.
CREATE TABLE itam_ref.location(
 location_id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
 name_en nvarchar(200) NOT NULL CONSTRAINT UX_itam_location_name UNIQUE,
 kind nvarchar(20) NOT NULL CHECK(kind IN(N'ITWarehouse',N'Branch')),
 region_code nvarchar(20) NOT NULL REFERENCES itam_ref.region(region_code), city nvarchar(100) NULL, active bit NOT NULL DEFAULT 1);
CREATE TABLE itam_ref.supplier(
 supplier_id int IDENTITY(1,1) NOT NULL PRIMARY KEY, name_en nvarchar(200) NOT NULL CONSTRAINT UX_itam_supplier UNIQUE,
 vat_no nvarchar(20) NULL, active bit NOT NULL DEFAULT 1,
 -- Supplier module (owner, 22 October 2026): contact details; added by the Ink Officer, IT Assets Officer or IT Administrator.
 name_ar nvarchar(200) NULL, cr_no nvarchar(20) NULL, contact_name nvarchar(200) NULL, phone nvarchar(30) NULL, email nvarchar(200) NULL, address nvarchar(300) NULL,
 notes nvarchar(500) NULL, created_by uniqueidentifier NULL, created_at_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME());
CREATE TABLE itam_ref.manufacturer(
 manufacturer_id int IDENTITY(1,1) NOT NULL PRIMARY KEY, name_en nvarchar(100) NOT NULL CONSTRAINT UX_itam_manufacturer UNIQUE, active bit NOT NULL DEFAULT 1);
CREATE TABLE itam_ref.model(
 model_id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
 manufacturer_id int NOT NULL REFERENCES itam_ref.manufacturer(manufacturer_id),
 type_code nvarchar(10) NOT NULL REFERENCES itam_ref.asset_type(type_code),
 name_en nvarchar(150) NOT NULL, active bit NOT NULL DEFAULT 1,
 CONSTRAINT UX_itam_model UNIQUE(manufacturer_id,type_code,name_en));
-- Short lists: ComputerType, OperatingSystem, PrinterType, TonerType, Software.
CREATE TABLE itam_ref.value_item(
 list_code nvarchar(30) NOT NULL, item_code nvarchar(60) NOT NULL, name_en nvarchar(100) NOT NULL,
 sort_order int NOT NULL DEFAULT 0, active bit NOT NULL DEFAULT 1, CONSTRAINT PK_itam_value PRIMARY KEY(list_code,item_code));

-- Numbers ---------------------------------------------------------------------------------------------------------------
-- One sequence for all assets (continues after the highest SharePoint number, 4808), and the other document sequences.
CREATE TABLE itam_data.counter(counter_code nvarchar(20) NOT NULL PRIMARY KEY, next_value int NOT NULL CHECK(next_value>0));

CREATE TABLE itam_data.stored_file(
 file_id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY, file_name nvarchar(260) NOT NULL, media_type nvarchar(100) NOT NULL,
 byte_length int NOT NULL CHECK(byte_length>0), sha256 binary(32) NOT NULL, content varbinary(max) NOT NULL,
 uploaded_by uniqueidentifier NOT NULL, uploaded_at_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME());

-- Purchase orders (sent by Procurement by email; there is no SAP) ------------------------------------------------------------
CREATE TABLE itam_data.po(
 po_id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
 po_number nvarchar(40) NOT NULL CONSTRAINT UX_itam_po_number UNIQUE,
 po_date date NOT NULL, supplier_id int NOT NULL REFERENCES itam_ref.supplier(supplier_id),
 raised_by nvarchar(200) NOT NULL, request_ref nvarchar(20) NULL, invoice_no nvarchar(60) NULL,
 document_file_id bigint NOT NULL REFERENCES itam_data.stored_file(file_id), note nvarchar(1000) NULL,
 created_by uniqueidentifier NOT NULL, created_at_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME(), version rowversion NOT NULL);
CREATE TABLE itam_data.po_line(
 po_line_id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
 po_id int NOT NULL REFERENCES itam_data.po(po_id), line_no int NOT NULL CHECK(line_no>0),
 type_code nvarchar(10) NOT NULL REFERENCES itam_ref.asset_type(type_code),
 manufacturer_id int NULL REFERENCES itam_ref.manufacturer(manufacturer_id), model_id int NULL REFERENCES itam_ref.model(model_id),
 specification nvarchar(500) NULL, quantity int NOT NULL CHECK(quantity BETWEEN 1 AND 10000), unit_cost decimal(18,2) NOT NULL CHECK(unit_cost>=0),
 CONSTRAINT UX_itam_po_line UNIQUE(po_id,line_no));
CREATE TABLE itam_data.po_receipt(
 receipt_id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
 po_id int NOT NULL REFERENCES itam_data.po(po_id), location_id int NOT NULL REFERENCES itam_ref.location(location_id),
 received_at_utc datetime2 NOT NULL, received_by uniqueidentifier NOT NULL,
 invoice_ref nvarchar(60) NULL, evidence_file_id bigint NULL REFERENCES itam_data.stored_file(file_id), note nvarchar(500) NULL);
-- Each print of the PO pack, and when it went to Accounting.
CREATE TABLE itam_data.pack_print(
 print_id int IDENTITY(1,1) NOT NULL PRIMARY KEY, po_id int NOT NULL REFERENCES itam_data.po(po_id),
 printed_by uniqueidentifier NOT NULL, printed_at_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
 devices int NOT NULL, notes_signed int NOT NULL, notes_unsigned int NOT NULL,
 sent_on date NULL, sent_by uniqueidentifier NULL);

-- The asset register ------------------------------------------------------------------------------------------------------------
CREATE TABLE itam_data.asset(
 asset_id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
 asset_no nvarchar(20) NOT NULL CONSTRAINT UX_itam_asset_no UNIQUE,
 asset_seq int NOT NULL CONSTRAINT UX_itam_asset_seq UNIQUE,
 type_code nvarchar(10) NOT NULL REFERENCES itam_ref.asset_type(type_code),
 status nvarchar(20) NOT NULL CHECK(status IN(N'InStore',N'InUse',N'InTransit',N'InRepair',N'PendingDisposal',N'Disposed',N'Sold',N'Expired',N'Lost')),
 stock_location nvarchar(20) NOT NULL CHECK(stock_location IN(N'ITWarehouse',N'Branch',N'Employee')),
 location_id int NOT NULL REFERENCES itam_ref.location(location_id),
 manufacturer_id int NULL REFERENCES itam_ref.manufacturer(manufacturer_id), model_id int NULL REFERENCES itam_ref.model(model_id),
 serial_no nvarchar(100) NULL, serial_key nvarchar(100) NULL,
 service_tag nvarchar(100) NULL, service_tag_key nvarchar(100) NULL,
 mac_address char(17) NULL, ip_address varchar(15) NULL,
 device_condition nvarchar(10) NOT NULL CHECK(device_condition IN(N'New',N'Used')),
 purchase_date date NULL, supplier_id int NULL REFERENCES itam_ref.supplier(supplier_id),
 po_id int NULL REFERENCES itam_data.po(po_id), po_line_id int NULL REFERENCES itam_data.po_line(po_line_id),
 receipt_id int NULL REFERENCES itam_data.po_receipt(receipt_id), po_number_text nvarchar(40) NULL,
 unit_cost decimal(18,2) NULL CHECK(unit_cost>=0), invoice_no nvarchar(60) NULL, notes nvarchar(2000) NULL,
 created_by uniqueidentifier NOT NULL, created_at_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME(), version rowversion NOT NULL);
CREATE UNIQUE INDEX UX_itam_serial ON itam_data.asset(serial_key) WHERE serial_key IS NOT NULL;
CREATE UNIQUE INDEX UX_itam_service_tag ON itam_data.asset(service_tag_key) WHERE service_tag_key IS NOT NULL;
CREATE UNIQUE INDEX UX_itam_mac ON itam_data.asset(mac_address) WHERE mac_address IS NOT NULL;
CREATE INDEX IX_itam_asset_location ON itam_data.asset(location_id,status);
CREATE INDEX IX_itam_asset_po ON itam_data.asset(po_id,po_line_id);
-- Fields that depend on the asset type (processor, memory, operating system, extension ...).
CREATE TABLE itam_data.asset_attribute(
 asset_id int NOT NULL REFERENCES itam_data.asset(asset_id), attr_code nvarchar(30) NOT NULL, value nvarchar(400) NOT NULL,
 CONSTRAINT PK_itam_asset_attribute PRIMARY KEY(asset_id,attr_code));
-- OS and software keys: encrypted by the application; every view is logged.
CREATE TABLE itam_data.asset_secret(
 asset_id int NOT NULL REFERENCES itam_data.asset(asset_id), secret_code nvarchar(20) NOT NULL CHECK(secret_code IN(N'OsKey',N'SoftwareKey')),
 protected_value nvarchar(max) NOT NULL, CONSTRAINT PK_itam_asset_secret PRIMARY KEY(asset_id,secret_code));
CREATE TABLE itam_data.secret_view(
 view_id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY, asset_id int NOT NULL, secret_code nvarchar(20) NOT NULL,
 viewer_id uniqueidentifier NOT NULL, at_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME());
-- Full history of each asset.
CREATE TABLE itam_data.asset_event(
 event_id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY, asset_id int NOT NULL REFERENCES itam_data.asset(asset_id),
 at_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME(), actor_id uniqueidentifier NOT NULL, kind nvarchar(30) NOT NULL, detail nvarchar(2000) NULL);
CREATE INDEX IX_itam_asset_event ON itam_data.asset_event(asset_id,event_id);

-- Reference values (owner master data workbook ITAM_Master_Data_v1, 20 October 2026).
INSERT itam_ref.asset_type(type_code,name_en,sort_order) VALUES
 (N'PC',N'Computer',1),(N'PNT',N'Printer',2),(N'IP',N'IP Phone',3),(N'MON',N'Monitor',4),(N'SCA',N'Scanner',5),
 (N'SOF',N'Software',6),(N'TON',N'Printer Cartridge',7),(N'DEV',N'Other',8);
INSERT itam_ref.region(region_code,name_en,sort_order) VALUES
 (N'Central',N'Central',1),(N'East',N'East',2),(N'West',N'West',3),(N'South',N'South',4),(N'UAE',N'UAE',5);
INSERT itam_ref.value_item(list_code,item_code,name_en,sort_order) VALUES
 (N'ComputerType',N'Desktop',N'Desktop',1),(N'ComputerType',N'Notebook',N'Notebook',2),(N'ComputerType',N'AllInOne',N'All In One',3),(N'ComputerType',N'iPad',N'iPad',4),
 (N'OperatingSystem',N'Win11',N'Windows 11',1),(N'OperatingSystem',N'Win10',N'Windows 10',2),(N'OperatingSystem',N'Win81',N'Windows 8.1',3),(N'OperatingSystem',N'Win7',N'Windows 7',4),
 (N'PrinterType',N'Network',N'Network Printer',1),(N'PrinterType',N'Local',N'Local Printer',2),
 (N'TonerType',N'Black',N'Black',1),(N'TonerType',N'Color',N'Color',2);
INSERT itam_data.counter(counter_code,next_value) VALUES(N'Asset',4809),(N'Delivery',4133),(N'Request',1),(N'GatePass',1),(N'Transfer',1);

IF DATABASE_PRINCIPAL_ID(N'itam_runtime') IS NULL CREATE ROLE itam_runtime AUTHORIZATION dbo;
GRANT SELECT,INSERT,UPDATE ON SCHEMA::itam_access TO itam_runtime;
GRANT DELETE ON itam_access.role_assignment TO itam_runtime;
DENY UPDATE,DELETE ON itam_access.audit TO itam_runtime;
GRANT SELECT,INSERT,UPDATE,DELETE ON SCHEMA::itam_ref TO itam_runtime;
GRANT SELECT,INSERT,UPDATE ON SCHEMA::itam_data TO itam_runtime;
GRANT DELETE ON itam_data.asset_attribute TO itam_runtime;
GRANT DELETE ON itam_data.asset_secret TO itam_runtime;
DENY UPDATE,DELETE ON itam_data.asset_event TO itam_runtime;
COMMIT TRANSACTION;
