-- Non-superuser application role. Postgres superusers always bypass RLS (even with FORCE ROW
-- LEVEL SECURITY), so both the local dev DB and every test-fixture database must connect as
-- this role -- not "postgres" -- for RLS to have any effect at all. Password is a fixed,
-- non-secret local/test value (never used outside a local dev machine or a disposable
-- per-test-run database).
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'sms_app') THEN
        CREATE ROLE sms_app LOGIN PASSWORD 'sms_app_dev_pw' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
    END IF;
END
$$;
