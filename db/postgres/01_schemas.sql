-- Source SQL Server database "Sms" has 5 schemas total; only 2 are user schemas
-- (dbo = application data/procs, rls = the tenant-isolation predicate function).
-- guest / INFORMATION_SCHEMA / sys are SQL Server built-ins with no Postgres equivalent needed.

CREATE SCHEMA IF NOT EXISTS dbo;
CREATE SCHEMA IF NOT EXISTS rls;

-- Required extension for gen_random_uuid() used to replace SQL Server's
-- newid()/newsequentialid() column defaults (see 04_tables.sql).
CREATE EXTENSION IF NOT EXISTS pgcrypto;
