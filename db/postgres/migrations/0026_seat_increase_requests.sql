-- 0026: seat-increase requests. A school owner RAISES a request to increase their billed seats;
-- the CATRE team APPROVES it (which applies the increase + bills the top-up) or rejects it. Mirrors
-- the plan-upgrade-request flow. Not RLS-guarded (owner writes run under the elevated platform
-- context in MeSchoolsService; CATRE is platform).
-- Rollback: DROP TABLE IF EXISTS dbo."SeatIncreaseRequests";

CREATE TABLE IF NOT EXISTS "dbo"."SeatIncreaseRequests" (
    "Id"               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "TenantId"         uuid NOT NULL,
    "TenantName"       varchar(200),
    "CurrentSeats"     int NOT NULL,
    "RequestedSeats"   int NOT NULL,
    "Status"           varchar(16) NOT NULL DEFAULT 'pending',  -- pending | approved | rejected
    "Note"             text,
    "RequestedByUserId" uuid,
    "ReviewedByUserId"  uuid,
    "CreatedAt"        timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt"        timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS "IX_SeatIncreaseRequests_Tenant" ON "dbo"."SeatIncreaseRequests" ("TenantId");
CREATE INDEX IF NOT EXISTS "IX_SeatIncreaseRequests_Status" ON "dbo"."SeatIncreaseRequests" ("Status");

-- Grant to the app role: 99_app_role_grants.sql ran before this table existed.
GRANT SELECT, INSERT, UPDATE, DELETE ON "dbo"."SeatIncreaseRequests" TO sms_app;
