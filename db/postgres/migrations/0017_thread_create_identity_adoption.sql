-- 0017: Thread_Create adopts a legacy thread by CONTACT IDENTITY, not display-name equality.
--
-- Bug: a parent↔teacher conversation fragmented into two parent-side threads. The parent's
-- legacy thread (ContactUserId NULL, Name "Neha Singh") was never adopted by the reply-delivery
-- mirror, because the mirror arrives named after the SENDER's current display name ("Principal",
-- the null-Users.Name role-label fallback) and dbo.thread_create only adopted a NULL-ContactUserId
-- thread when th."Name" = @Name. "Principal" <> "Neha Singh" -> a second, duplicate thread, and
-- once that duplicate carried ContactUserId it shadowed the original for every future send.
--
-- Fix: when a ContactUserId is supplied and no exact ContactUserId match exists, adopt a legacy
-- NULL-ContactUserId thread for the SAME contact IDENTITY — the stored thread name is resolved to
-- a user id (dbo.resolve_thread_contact, mirroring CommsModule.ResolveContactUserIdAsync incl. the
-- " (parent)" / Role='Student' -> parent rules) and compared to the supplied ContactUserId. Exact
-- display-name match is still honored first, so M0149 behaviour is preserved. Resolution is
-- UNAMBIGUOUS-ONLY (exactly one distinct candidate) so two different people who happen to share a
-- name are NEVER merged. A transaction-scoped advisory lock on (tenant, owner, contact) serialises
-- concurrent sends so a race cannot create two threads for one conversation (0018 adds a hard
-- partial-unique backstop once existing duplicates are reconciled).
--
-- Rollback:
--   DROP FUNCTION IF EXISTS dbo.resolve_thread_contact(uuid, varchar, varchar, uuid);
--   then restore dbo.thread_create from the pre-0017 body (the M0149 adoption that matched on Name)
--   and re-grant: GRANT EXECUTE ON FUNCTION dbo.thread_create(uuid,uuid,varchar,varchar,boolean,uuid,uuid) TO sms_app;

-- Unambiguous contact-identity for a stored thread (name + role), from the owner's perspective.
-- Returns the single resolved user id, or NULL when zero or more-than-one distinct candidates
-- exist (so adoption never merges the wrong people). Mirrors CommsModule.ResolveContactUserIdAsync.
CREATE OR REPLACE FUNCTION dbo.resolve_thread_contact(
    p_tenant uuid, p_name varchar, p_role varchar, p_sender uuid
) RETURNS uuid
LANGUAGE plpgsql
STABLE
AS $$
DECLARE
    v_student_name text;
    v_ids uuid[];
BEGIN
    IF p_name IS NULL THEN
        RETURN NULL;
    END IF;

    v_student_name :=
        CASE
            WHEN p_name ILIKE '% (parent)'
                THEN btrim(left(p_name, length(p_name) - length(' (parent)')))
            WHEN lower(coalesce(p_role, '')) = 'student'
                THEN btrim(p_name)
            ELSE NULL
        END;

    SELECT array_agg(DISTINCT q.id) INTO v_ids
    FROM (
        SELECT u."Id" AS id
        FROM "dbo"."Users" u
        WHERE u."TenantId" = p_tenant AND u."Name" = p_name AND u."Id" <> p_sender
        UNION ALL
        SELECT t."UserId"
        FROM "dbo"."Teachers" t
        WHERE t."TenantId" = p_tenant AND t."Name" = p_name AND t."UserId" IS NOT NULL AND t."UserId" <> p_sender
        UNION ALL
        SELECT s."UserId"
        FROM "dbo"."Staff" s
        WHERE s."TenantId" = p_tenant AND s."Name" = p_name AND s."UserId" IS NOT NULL AND s."UserId" <> p_sender
        UNION ALL
        SELECT pl."ParentUserId"
        FROM "dbo"."Students" st
        INNER JOIN "dbo"."ParentStudentLinks" pl
            ON pl."StudentId" = st."Id" AND pl."TenantId" = st."TenantId"
        WHERE v_student_name IS NOT NULL
          AND st."TenantId" = p_tenant
          AND st."Name" = v_student_name
          AND pl."ParentUserId" <> p_sender
          AND (SELECT count(1) FROM "dbo"."Students" st2
                WHERE st2."TenantId" = p_tenant AND st2."Name" = v_student_name) = 1
    ) q
    WHERE q.id IS NOT NULL;

    IF v_ids IS NULL OR array_length(v_ids, 1) <> 1 THEN
        RETURN NULL;   -- zero or ambiguous: never adopt
    END IF;
    RETURN v_ids[1];
END;
$$;

GRANT EXECUTE ON FUNCTION dbo.resolve_thread_contact(uuid, varchar, varchar, uuid) TO sms_app;

CREATE OR REPLACE FUNCTION dbo.thread_create(
    TenantId uuid, OwnerUserId uuid, Name varchar(120), "role" varchar(40),
    IsGroup boolean, ChildId uuid, ContactUserId uuid DEFAULT NULL
)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "Name" varchar(120), "Role" varchar(40), "LastMessage" varchar(400),
    "LastAt" timestamptz, "Unread" integer, "Group" boolean, "ChildId" uuid
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE
    v_existing uuid;
    v_id uuid;
BEGIN
    IF ContactUserId IS NOT NULL THEN
        -- Serialise concurrent sends to the same (tenant, owner, contact) so a race cannot create
        -- two threads for one conversation. Released at transaction end.
        PERFORM pg_advisory_xact_lock(
            hashtextextended(TenantId::text || ':' || OwnerUserId::text || ':' || ContactUserId::text, 0));

        SELECT th."Id" INTO v_existing
        FROM "dbo"."ChatThreads" th
        WHERE th."TenantId" = TenantId AND th."OwnerUserId" = OwnerUserId AND th."ContactUserId" = ContactUserId
        LIMIT 1;

        IF v_existing IS NULL THEN
            -- Adopt a legacy NULL-ContactUserId thread for the SAME contact identity rather than
            -- creating a second conversation. Exact display-name match wins first (M0149); then a
            -- thread whose stored name/role resolves to this same ContactUserId (unambiguous only),
            -- so "Neha Singh" is adopted even when the sender now presents as "Principal".
            SELECT th."Id" INTO v_existing
            FROM "dbo"."ChatThreads" th
            WHERE th."TenantId" = TenantId AND th."OwnerUserId" = OwnerUserId
              AND th."ContactUserId" IS NULL
              AND th."IsGroup" = COALESCE(IsGroup, false)
              AND ( th."Name" = thread_create.Name
                 OR dbo.resolve_thread_contact(TenantId, th."Name", th."Role", OwnerUserId) = ContactUserId )
            ORDER BY (th."Name" = thread_create.Name) DESC, th."LastAt" DESC NULLS LAST
            LIMIT 1;

            IF v_existing IS NOT NULL THEN
                UPDATE "dbo"."ChatThreads" th
                SET "ContactUserId" = thread_create.ContactUserId, "Role" = thread_create."role"
                WHERE th."Id" = v_existing;
            END IF;
        END IF;
    ELSE
        SELECT th."Id" INTO v_existing
        FROM "dbo"."ChatThreads" th
        WHERE th."TenantId" = TenantId AND th."OwnerUserId" = OwnerUserId AND th."ContactUserId" IS NULL
          AND th."Name" = thread_create.Name
        LIMIT 1;
    END IF;

    IF v_existing IS NOT NULL THEN
        RETURN QUERY
        SELECT "Id", "TenantId", "Name", "Role", "LastMessage", "LastAt", "Unread", "IsGroup" AS "Group", "ChildId"
        FROM "dbo"."ChatThreads" WHERE "Id" = v_existing;
        RETURN;
    END IF;

    v_id := gen_random_uuid();
    INSERT INTO "dbo"."ChatThreads" ("Id", "TenantId", "OwnerUserId", "Name", "Role", "IsGroup", "ChildId", "ContactUserId")
    VALUES (v_id, TenantId, OwnerUserId, Name, thread_create."role", COALESCE(IsGroup, false), ChildId, ContactUserId);

    RETURN QUERY
    SELECT "Id", "TenantId", "Name", "Role", "LastMessage", "LastAt", "Unread", "IsGroup" AS "Group", "ChildId"
    FROM "dbo"."ChatThreads" WHERE "Id" = v_id;
END;
$$;

GRANT EXECUTE ON FUNCTION dbo.thread_create(uuid, uuid, varchar, varchar, boolean, uuid, uuid) TO sms_app;
