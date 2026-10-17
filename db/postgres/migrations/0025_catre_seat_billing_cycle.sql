-- 0025: seat-limit + usage-based billing foundations (additive columns only).
--
-- Billed-vs-enrolled is already modelled (Tenants.StudentsCount billed vs live
-- EnrolledStudents). The billed SEAT LIMIT reuses the existing Tenants."LimitsStudents"
-- column (copied from the plan at create/change-plan) — no new seat column is added.
--
-- This migration adds the billing-cycle, included-seats, alert-threshold and
-- seat-increase-mode fields, plus invoice/subscription period bookkeeping, so later
-- phases (enforcement, recurring billing, seat increase) can read/write them.
-- All columns are nullable or defaulted, so existing rows keep working unchanged.
--
-- Rollback:
--   ALTER TABLE dbo."Tenants"       DROP COLUMN IF EXISTS "BillingCycle", DROP COLUMN IF EXISTS "SeatAlertThreshold", DROP COLUMN IF EXISTS "SeatIncreaseMode";
--   ALTER TABLE dbo."Plans"         DROP COLUMN IF EXISTS "IncludedStudents", DROP COLUMN IF EXISTS "SeatAlertThreshold", DROP COLUMN IF EXISTS "AllowedCycles";
--   ALTER TABLE dbo."Subscriptions" DROP COLUMN IF EXISTS "BillingInterval", DROP COLUMN IF EXISTS "PeriodStart", DROP COLUMN IF EXISTS "PeriodEnd";
--   ALTER TABLE dbo."Invoices"      DROP COLUMN IF EXISTS "PeriodStart", DROP COLUMN IF EXISTS "PeriodEnd", DROP COLUMN IF EXISTS "Seats", DROP COLUMN IF EXISTS "Kind";

-- Tenant: cycle chosen at onboarding; per-school warning gap override; auto/manual mode.
ALTER TABLE dbo."Tenants"
    ADD COLUMN IF NOT EXISTS "BillingCycle"       varchar(12) NOT NULL DEFAULT 'month',
    ADD COLUMN IF NOT EXISTS "SeatAlertThreshold" int         NULL,
    ADD COLUMN IF NOT EXISTS "SeatIncreaseMode"   varchar(8)  NOT NULL DEFAULT 'manual';

-- Plan: flat plans' bundled seat count; default warning gap; which cycles are offered.
ALTER TABLE dbo."Plans"
    ADD COLUMN IF NOT EXISTS "IncludedStudents"   int         NULL,
    ADD COLUMN IF NOT EXISTS "SeatAlertThreshold" int         NULL,
    ADD COLUMN IF NOT EXISTS "AllowedCycles"      varchar(40) NOT NULL DEFAULT 'month,quarter,year';

-- Subscription: cycle snapshot + the window the current invoice covers.
ALTER TABLE dbo."Subscriptions"
    ADD COLUMN IF NOT EXISTS "BillingInterval" varchar(12)  NOT NULL DEFAULT 'month',
    ADD COLUMN IF NOT EXISTS "PeriodStart"     timestamptz  NULL,
    ADD COLUMN IF NOT EXISTS "PeriodEnd"       timestamptz  NULL;

-- Invoice: billed window, seat snapshot, and whether it is a recurring cycle bill
-- or a mid-cycle proration charge.
ALTER TABLE dbo."Invoices"
    ADD COLUMN IF NOT EXISTS "PeriodStart" timestamptz NULL,
    ADD COLUMN IF NOT EXISTS "PeriodEnd"   timestamptz NULL,
    ADD COLUMN IF NOT EXISTS "Seats"       int         NULL,
    ADD COLUMN IF NOT EXISTS "Kind"        varchar(16) NOT NULL DEFAULT 'cycle';
