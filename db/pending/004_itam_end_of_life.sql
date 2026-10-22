-- IT Assets build step 5: end of life and Active Directory (plan v1.9). Loss cases LC-n (report, investigation, decision,
-- with employee responsibility and financial write-off kept separate, and recovery), disposals DS-n (approval, then
-- completion with evidence), sales SL-n (proof of payment), employee clearance CL-n for leavers (Owner Type Resigned,
-- Assets to collect, then the task to disable and keep the AD user), and the AD computer comparison runs.

INSERT itam_data.counter(counter_code,next_value) VALUES(N'LossCase',1),(N'Disposal',1),(N'Sale',1),(N'Clearance',1);

-- Loss case. Reporting a loss never receives the asset back and never clears the employee: the asset is Lost and the
-- custody stays open until an authorized decision closes it (closure reason LossDecision).
CREATE TABLE itam_data.loss_case(
 case_id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
 case_no nvarchar(20) NOT NULL CONSTRAINT UX_itam_loss_case_no UNIQUE,
 asset_id int NOT NULL REFERENCES itam_data.asset(asset_id),
 delivery_id int NULL REFERENCES itam_data.delivery(delivery_id), custodian_id uniqueidentifier NULL, -- the custody open when reported
 movement_id int NULL REFERENCES itam_data.movement(movement_id), -- confirmed lost on a transfer
 previous_status nvarchar(20) NOT NULL, region_code nvarchar(10) NOT NULL,
 reported_by uniqueidentifier NOT NULL, reported_at_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
 discovered_on date NOT NULL, last_location nvarchar(300) NOT NULL, circumstances nvarchar(2000) NOT NULL,
 report_file_id bigint NULL REFERENCES itam_data.stored_file(file_id),
 status nvarchar(30) NOT NULL CHECK(status IN(N'Reported',N'UnderInvestigation',N'AwaitingDecision',N'AwaitingAction',N'ClosedByDecision',N'RecoveredReview',N'Recovered')),
 investigator_id uniqueidentifier NULL, assigned_by uniqueidentifier NULL, assigned_at_utc datetime2 NULL,
 security_actions nvarchar(2000) NULL, security_by uniqueidentifier NULL, security_at_utc datetime2 NULL,
 findings nvarchar(4000) NULL, recommendation nvarchar(2000) NULL, findings_file_id bigint NULL REFERENCES itam_data.stored_file(file_id),
 findings_by uniqueidentifier NULL, findings_at_utc datetime2 NULL,
 -- Employee responsibility (only an authorized decision clears it).
 responsibility nvarchar(30) NOT NULL DEFAULT N'PendingDecision' CHECK(responsibility IN(N'PendingDecision',N'PendingRequiredAction',N'Cleared')),
 decision nvarchar(20) NULL CHECK(decision IN(N'NotLiable',N'Settlement')), decision_note nvarchar(2000) NULL,
 decision_file_id bigint NULL REFERENCES itam_data.stored_file(file_id), decided_by uniqueidentifier NULL, decided_at_utc datetime2 NULL,
 settlement_amount decimal(12,2) NULL, settlement_file_id bigint NULL REFERENCES itam_data.stored_file(file_id),
 settlement_note nvarchar(1000) NULL, settled_by uniqueidentifier NULL, settled_at_utc datetime2 NULL,
 -- Financial write-off (independent: approving it does not clear the employee).
 writeoff nvarchar(20) NOT NULL DEFAULT N'NotRequested' CHECK(writeoff IN(N'NotRequested',N'Pending',N'Approved',N'Rejected')),
 writeoff_reason nvarchar(1000) NULL, writeoff_requested_by uniqueidentifier NULL, writeoff_requested_at_utc datetime2 NULL,
 writeoff_by uniqueidentifier NULL, writeoff_at_utc datetime2 NULL, writeoff_note nvarchar(1000) NULL,
 -- Recovery: found again; IT receives it only on a physical receipt, then reviews it before reuse.
 recovered_on date NULL, recovered_by uniqueidentifier NULL, recovered_at_utc datetime2 NULL, recovery_note nvarchar(1000) NULL,
 recovery_location_id int NULL REFERENCES itam_ref.location(location_id), reviewed_by uniqueidentifier NULL, reviewed_at_utc datetime2 NULL,
 version rowversion NOT NULL);
CREATE UNIQUE INDEX UX_itam_loss_case_open ON itam_data.loss_case(asset_id) WHERE status IN(N'Reported',N'UnderInvestigation',N'AwaitingDecision',N'AwaitingAction',N'RecoveredReview');

-- Disposal: approval alone never disposes; the asset is Disposed only when the completion and its evidence are recorded.
CREATE TABLE itam_data.disposal(
 disposal_id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
 disposal_no nvarchar(20) NOT NULL CONSTRAINT UX_itam_disposal_no UNIQUE,
 asset_id int NOT NULL REFERENCES itam_data.asset(asset_id),
 assessment_id int NULL REFERENCES itam_data.assessment(assessment_id),
 reason nvarchar(1000) NOT NULL, condition nvarchar(500) NOT NULL, previous_status nvarchar(20) NOT NULL,
 location_id int NOT NULL REFERENCES itam_ref.location(location_id), region_code nvarchar(10) NOT NULL,
 requested_by uniqueidentifier NOT NULL, requested_at_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
 status nvarchar(20) NOT NULL CHECK(status IN(N'WaitingApproval',N'Rejected',N'Approved',N'Completed',N'Cancelled')),
 decided_by uniqueidentifier NULL, decided_at_utc datetime2 NULL, reject_reason nvarchar(500) NULL, cancelled_reason nvarchar(500) NULL,
 completed_on date NULL, method nvarchar(20) NULL CHECK(method IN(N'Recycling',N'Destruction',N'ReturnToVendor',N'Donation',N'Other')),
 completed_by_name nvarchar(200) NULL, recipient nvarchar(300) NULL, evidence_file_id bigint NULL REFERENCES itam_data.stored_file(file_id),
 data_wiped bit NULL, licenses_removed bit NULL, completion_note nvarchar(1000) NULL,
 completed_by uniqueidentifier NULL, completed_at_utc datetime2 NULL, version rowversion NOT NULL);
CREATE UNIQUE INDEX UX_itam_disposal_open ON itam_data.disposal(asset_id) WHERE status IN(N'WaitingApproval',N'Approved');

-- Sale to an employee: any IT role records it with the proof of payment; no approval (decided 20 October 2026).
CREATE TABLE itam_data.sale(
 sale_id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
 sale_no nvarchar(20) NOT NULL CONSTRAINT UX_itam_sale_no UNIQUE,
 asset_id int NOT NULL CONSTRAINT UX_itam_sale_asset UNIQUE REFERENCES itam_data.asset(asset_id),
 buyer_id uniqueidentifier NOT NULL REFERENCES itam_access.employee(employee_id),
 price decimal(12,2) NOT NULL CHECK(price>=0), sold_on date NOT NULL,
 proof_file_id bigint NOT NULL REFERENCES itam_data.stored_file(file_id), notes nvarchar(1000) NULL,
 delivery_id int NULL REFERENCES itam_data.delivery(delivery_id), previous_status nvarchar(20) NOT NULL, location_id int NOT NULL,
 recorded_by uniqueidentifier NOT NULL, recorded_at_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME());

-- Employee clearance (leaver). The account stays active in AD while assets are collected; after the clearance is completed
-- the System Administrator disables the user and keeps it in AD (disable task Waiting, then Done).
CREATE TABLE itam_data.clearance(
 clearance_id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
 clearance_no nvarchar(20) NOT NULL CONSTRAINT UX_itam_clearance_no UNIQUE,
 employee_id uniqueidentifier NOT NULL REFERENCES itam_access.employee(employee_id),
 last_working_day date NULL, note nvarchar(1000) NULL,
 marked_by uniqueidentifier NOT NULL, marked_at_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
 status nvarchar(20) NOT NULL CHECK(status IN(N'InProgress',N'Completed',N'Cancelled')),
 completed_by uniqueidentifier NULL, completed_at_utc datetime2 NULL, completion_note nvarchar(1000) NULL,
 cancelled_by uniqueidentifier NULL, cancelled_at_utc datetime2 NULL, cancelled_reason nvarchar(500) NULL,
 disable_status nvarchar(20) NOT NULL DEFAULT N'NotDue' CHECK(disable_status IN(N'NotDue',N'Waiting',N'Done')),
 disabled_by uniqueidentifier NULL, disabled_at_utc datetime2 NULL, disable_note nvarchar(500) NULL);
CREATE UNIQUE INDEX UX_itam_clearance_open ON itam_data.clearance(employee_id) WHERE status=N'InProgress';

-- AD computer comparison: each run with its counts, and its result lines; a reviewed line stays hidden until the computer changes.
CREATE TABLE itam_data.ad_run(
 run_id int IDENTITY(1,1) NOT NULL PRIMARY KEY, run_at_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME(), run_by uniqueidentifier NULL,
 ous nvarchar(1000) NOT NULL, inactive_days int NOT NULL, computers int NOT NULL,
 matched int NOT NULL, no_delivery int NOT NULL, removal_waiting int NOT NULL, not_in_ad int NOT NULL, needs_check int NOT NULL, reviewed int NOT NULL);
CREATE TABLE itam_data.ad_run_line(
 line_id int IDENTITY(1,1) NOT NULL PRIMARY KEY, run_id int NOT NULL REFERENCES itam_data.ad_run(run_id),
 result nvarchar(20) NOT NULL CHECK(result IN(N'Matched',N'NoDelivery',N'RemovalWaiting',N'NotInAd',N'NeedsCheck')),
 computer_name nvarchar(64) NOT NULL, ad_ou nvarchar(400) NULL, ad_enabled bit NULL, ad_last_logon_utc datetime2 NULL, ad_created_utc datetime2 NULL,
 ad_os nvarchar(100) NULL, fingerprint nvarchar(300) NOT NULL, delivery_id int NULL REFERENCES itam_data.delivery(delivery_id),
 asset_id int NULL REFERENCES itam_data.asset(asset_id), region_code nvarchar(10) NULL, reviewed bit NOT NULL DEFAULT 0);
CREATE INDEX IX_itam_ad_run_line_run ON itam_data.ad_run_line(run_id);
CREATE TABLE itam_data.ad_review(
 review_id int IDENTITY(1,1) NOT NULL PRIMARY KEY, computer_name nvarchar(64) NOT NULL, result nvarchar(20) NOT NULL, fingerprint nvarchar(300) NOT NULL,
 reason nvarchar(500) NOT NULL, reviewed_by uniqueidentifier NOT NULL, reviewed_at_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME());
CREATE UNIQUE INDEX UX_itam_ad_review ON itam_data.ad_review(computer_name,result);
GRANT DELETE ON itam_data.ad_review TO itam_runtime;
