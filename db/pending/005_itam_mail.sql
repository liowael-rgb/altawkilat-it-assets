-- IT Assets emails (IT-EM templates, owner manifest): every email is written to the outbox in the same transaction as the
-- action that causes it, then sent after commit by the mail sender through the configured mail service. The event key
-- makes each email once only (an event, a reminder day, a weekly summary). The body is kept as sent, for the outbox page.
CREATE TABLE itam_data.mail_outbox(
 mail_id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
 template nvarchar(10) NOT NULL, event_key nvarchar(300) NOT NULL CONSTRAINT UX_itam_mail_event UNIQUE,
 record_ref nvarchar(60) NULL, to_list nvarchar(2000) NOT NULL, cc_list nvarchar(2000) NULL, recipients nvarchar(2000) NULL,
 reply_to nvarchar(254) NULL, -- the person who acted: replies reach them, never the system address
 subject nvarchar(300) NOT NULL, body_html nvarchar(max) NOT NULL,
 status nvarchar(20) NOT NULL DEFAULT N'Queued' CHECK(status IN(N'Queued',N'Sent',N'Failed',N'NoRecipient')),
 attempts int NOT NULL DEFAULT 0, last_error nvarchar(1000) NULL, sent_to nvarchar(2000) NULL,
 created_at_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME(), sent_at_utc datetime2 NULL);
CREATE INDEX IX_itam_mail_status ON itam_data.mail_outbox(status,mail_id);
