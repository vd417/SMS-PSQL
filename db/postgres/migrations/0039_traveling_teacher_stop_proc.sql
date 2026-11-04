-- 0007: let dbo.bustravelingteacher_add set/update a traveling teacher's optional StopId
-- (the admin "assign traveling teacher + stop" flow). Upsert on (TenantId, BusId, TeacherUserId):
-- an existing row has its StopId updated, otherwise a new row is inserted. Param refs are
-- function-qualified so the quoted "StopId" column is never confused with the stopid argument.

CREATE OR REPLACE FUNCTION dbo.bustravelingteacher_add(TenantId uuid, BusId uuid, TeacherUserId uuid, StopId uuid DEFAULT NULL)
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE v_count int;
BEGIN
    UPDATE "dbo"."BusTravelingTeachers" t
    SET "StopId" = bustravelingteacher_add.stopid
    WHERE t."TenantId" = bustravelingteacher_add.tenantid
      AND t."BusId" = bustravelingteacher_add.busid
      AND t."TeacherUserId" = bustravelingteacher_add.teacheruserid;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    IF v_count = 0 THEN
        INSERT INTO "dbo"."BusTravelingTeachers" ("TenantId", "BusId", "TeacherUserId", "StopId")
        VALUES (bustravelingteacher_add.tenantid, bustravelingteacher_add.busid,
                bustravelingteacher_add.teacheruserid, bustravelingteacher_add.stopid);
        v_count := 1;
    END IF;
    RETURN v_count;
END;
$$;
