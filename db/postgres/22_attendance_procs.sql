-- Attendance module procs (unquoted identifiers so Postgres case-folding matches existing
-- call sites with zero C# changes, per convention established in 09_auth_procs.sql).

CREATE OR REPLACE FUNCTION dbo.attendancealertconfig_get(tenantid uuid)
RETURNS TABLE (
    "NoticeDays" int, "EmailDays" int, "AutoSend" boolean,
    "AutoTime" varchar(5), "AutoChannel" varchar(10), "LastAutoSentDate" date
)
LANGUAGE sql
AS $$
    SELECT c."NoticeDays", c."EmailDays", c."AutoSend", c."AutoTime", c."AutoChannel", c."LastAutoSentDate"
    FROM "dbo"."AttendanceAlertConfigs" c
    WHERE c."TenantId" = attendancealertconfig_get.tenantid;
$$;

CREATE OR REPLACE FUNCTION dbo.attendancealertconfig_upsert(
    tenantid uuid, noticedays int, emaildays int, autosend boolean, autotime varchar(5), autochannel varchar(10)
)
RETURNS TABLE (
    "NoticeDays" int, "EmailDays" int, "AutoSend" boolean,
    "AutoTime" varchar(5), "AutoChannel" varchar(10), "LastAutoSentDate" date
)
LANGUAGE plpgsql
AS $$
BEGIN
    UPDATE "dbo"."AttendanceAlertConfigs" SET
        "NoticeDays" = attendancealertconfig_upsert.noticedays,
        "EmailDays" = attendancealertconfig_upsert.emaildays,
        "AutoSend" = attendancealertconfig_upsert.autosend,
        "AutoTime" = attendancealertconfig_upsert.autotime,
        "AutoChannel" = attendancealertconfig_upsert.autochannel,
        "UpdatedAt" = now()
    WHERE "TenantId" = attendancealertconfig_upsert.tenantid;

    IF NOT FOUND THEN
        INSERT INTO "dbo"."AttendanceAlertConfigs"
            ("TenantId", "NoticeDays", "EmailDays", "AutoSend", "AutoTime", "AutoChannel")
        VALUES (
            attendancealertconfig_upsert.tenantid, attendancealertconfig_upsert.noticedays,
            attendancealertconfig_upsert.emaildays, attendancealertconfig_upsert.autosend,
            attendancealertconfig_upsert.autotime, attendancealertconfig_upsert.autochannel
        );
    END IF;

    RETURN QUERY
    SELECT c."NoticeDays", c."EmailDays", c."AutoSend", c."AutoTime", c."AutoChannel", c."LastAutoSentDate"
    FROM "dbo"."AttendanceAlertConfigs" c
    WHERE c."TenantId" = attendancealertconfig_upsert.tenantid;
END;
$$;

-- Called on a platform session (IsPlatform = true) so RLS returns every tenant's row.
CREATE OR REPLACE FUNCTION dbo.attendancealertconfig_listdue(today timestamptz, nowminutes int)
RETURNS TABLE ("TenantId" uuid, "NoticeDays" int, "EmailDays" int, "AutoChannel" varchar(10))
LANGUAGE sql
AS $$
    SELECT c."TenantId", c."NoticeDays", c."EmailDays", c."AutoChannel"
    FROM "dbo"."AttendanceAlertConfigs" c
    WHERE c."AutoSend" = true
      AND (c."LastAutoSentDate" IS NULL OR c."LastAutoSentDate" < attendancealertconfig_listdue.today::date)
      AND (EXTRACT(HOUR FROM c."AutoTime"::time) * 60 + EXTRACT(MINUTE FROM c."AutoTime"::time)) <= attendancealertconfig_listdue.nowminutes;
$$;

-- Called on that tenant's session context.
CREATE OR REPLACE FUNCTION dbo.attendancealertconfig_markautosent(tenantid uuid, date timestamptz)
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE
    affected int;
BEGIN
    UPDATE "dbo"."AttendanceAlertConfigs" SET
        "LastAutoSentDate" = attendancealertconfig_markautosent.date::date,
        "UpdatedAt" = now()
    WHERE "TenantId" = attendancealertconfig_markautosent.tenantid;

    GET DIAGNOSTICS affected = ROW_COUNT;
    RETURN affected;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.schoollocation_delete(tenantid uuid)
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE
    affected int;
BEGIN
    DELETE FROM "dbo"."SchoolLocations" WHERE "TenantId" = schoollocation_delete.tenantid;
    GET DIAGNOSTICS affected = ROW_COUNT;
    RETURN affected;
END;
$$;
