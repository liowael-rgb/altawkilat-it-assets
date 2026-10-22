-- IT Assets build step 2: handover. Delivery to an employee (one open custody per asset), the delivery note signed on
-- paper by the employee and uploaded by IT, receipt back (-R), technical assessment (TA-), and the infrastructure tasks
-- that follow a receipt (computer removal from AD and Endpoint Central; IP phone removal from CUCM).

-- Active Directory details printed on the delivery note (read-only copy; no national ID).
ALTER TABLE itam_access.employee ADD mobile nvarchar(50) NULL, branch nvarchar(200) NULL, company nvarchar(200) NULL, hr_code nvarchar(50) NULL,
 region nvarchar(100) NULL, -- region as written in Active Directory (matched to a region code or name)
 sap_id nvarchar(50) NULL, -- SAP ID (Active Directory streetAddress, as VTMS)
 ip_phone nvarchar(50) NULL; -- desk extension (Active Directory ipPhone, as VTMS)

-- One row per delivery note. Open custody = received_at_utc IS NULL. The employee and product details are kept as issued
-- (snapshot_json) so a reprint never changes.
CREATE TABLE itam_data.delivery(
 delivery_id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
 delivery_no nvarchar(40) NOT NULL CONSTRAINT UX_itam_delivery_no UNIQUE,
 asset_id int NOT NULL REFERENCES itam_data.asset(asset_id),
 employee_id uniqueidentifier NOT NULL REFERENCES itam_access.employee(employee_id),
 delivered_on date NOT NULL,
 owner_type nvarchar(20) NOT NULL CHECK(owner_type IN(N'Personal',N'Department',N'Temporary',N'Other')),
 computer_name nvarchar(64) NULL, notes nvarchar(1000) NULL,
 from_location_id int NOT NULL REFERENCES itam_ref.location(location_id),
 snapshot_json nvarchar(max) NOT NULL,
 issued_by uniqueidentifier NOT NULL, issued_at_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
 -- Signed on paper by the employee himself; IT uploads the scan. Kept unchanged once uploaded.
 signed_file_id bigint NULL REFERENCES itam_data.stored_file(file_id), signed_by uniqueidentifier NULL, signed_at_utc datetime2 NULL,
 -- Physical return to IT (receipt note = delivery number + -R).
 received_at_utc datetime2 NULL, received_by uniqueidentifier NULL,
 receipt_condition nvarchar(20) NULL CHECK(receipt_condition IN(N'Working',N'Damaged',N'Faulty')),
 receipt_location_id int NULL REFERENCES itam_ref.location(location_id), receipt_notes nvarchar(1000) NULL,
 version rowversion NOT NULL);
CREATE UNIQUE INDEX UX_itam_delivery_open_asset ON itam_data.delivery(asset_id) WHERE received_at_utc IS NULL;
CREATE UNIQUE INDEX UX_itam_delivery_open_computer ON itam_data.delivery(computer_name) WHERE received_at_utc IS NULL AND computer_name IS NOT NULL;
CREATE INDEX IX_itam_delivery_employee ON itam_data.delivery(employee_id);

-- Technical assessment of a returned device (TA- + delivery number); one per delivery.
CREATE TABLE itam_data.assessment(
 assessment_id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
 assessment_no nvarchar(44) NOT NULL CONSTRAINT UX_itam_assessment_no UNIQUE,
 delivery_id int NOT NULL CONSTRAINT UX_itam_assessment_delivery UNIQUE REFERENCES itam_data.delivery(delivery_id),
 review nvarchar(4000) NOT NULL,
 recommendation nvarchar(20) NOT NULL CHECK(recommendation IN(N'Reuse',N'RepairByIt',N'RepairOutside',N'Dispose')),
 evidence_file_id bigint NULL REFERENCES itam_data.stored_file(file_id),
 assessed_by uniqueidentifier NOT NULL, assessed_at_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME());

-- Infrastructure tasks created by a receipt: the System Administrator removes the computer from AD and Endpoint Central;
-- the Voice Administrator removes the IP phone extension from CUCM. Done is recorded with who and when.
CREATE TABLE itam_data.task(
 task_id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
 kind nvarchar(30) NOT NULL CHECK(kind IN(N'RemoveFromAd',N'RemoveFromCucm')),
 asset_id int NOT NULL REFERENCES itam_data.asset(asset_id), delivery_id int NOT NULL REFERENCES itam_data.delivery(delivery_id),
 subject nvarchar(200) NOT NULL, created_at_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
 done_by uniqueidentifier NULL, done_at_utc datetime2 NULL, done_note nvarchar(500) NULL,
 CONSTRAINT UX_itam_task UNIQUE(delivery_id,kind));
