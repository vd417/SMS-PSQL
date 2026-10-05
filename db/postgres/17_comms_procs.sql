-- Comms module: PL/pgSQL conversions of the 5 stored procedures in the Complaint/Thread/
-- Message/Announcement family. dbo.Notification_Create is already converted in
-- 14_transport_procs.sql and is not touched here.
-- Source: full OBJECT_DEFINITION() extracted read-only from the live SQL Server Sms database,
-- cross-checked against sqlserver-object-inventory.csv.
--
-- All RETURNS TABLE shapes match the column order/names of their C# response records exactly
-- (ComplaintResponse, ChatThreadResponse's 9-column primary shape, CommsRepository's private
-- MessageRow, AnnouncementResponse) per the established "Dapper constructor arity/order must
-- match columns exactly" bug class.
--
-- IN parameters that collide with a reserved word in this Postgres version's grammar
-- ("from"/"role"/"status"/"type"/"text") are declared quoted-lowercase, matching what
-- BaseRepository.FunctionCallSql's named-argument call-site label always folds to
-- (p.Name.ToLowerInvariant()) -- distinct identifiers from the RETURNS TABLE's quoted
-- mixed-case OUT columns of the same logical name, so no #variable_conflict ambiguity, but the
-- pragma is kept anyway for consistency with every other converted proc this session.

CREATE OR REPLACE FUNCTION dbo.complaint_create(
    TenantId uuid, Subject varchar(200), "from" varchar(120), Category varchar(40),
    Priority varchar(10), Body varchar(2000)
)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "Subject" varchar(200), "From" varchar(120), "Category" varchar(40),
    "Priority" varchar(10), "Status" varchar(20), "Age" varchar(20), "Assignee" varchar(120), "Body" varchar(2000)
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE v_id uuid := gen_random_uuid();
BEGIN
    INSERT INTO "dbo"."Complaints" ("Id", "TenantId", "Subject", "From", "Category", "Priority", "Body")
    VALUES (v_id, TenantId, Subject, complaint_create."from", Category, COALESCE(Priority, 'medium'), Body);

    RETURN QUERY
    SELECT "Id", "TenantId", "Subject", "From", "Category", "Priority", "Status", "Age", "Assignee", "Body"
    FROM "dbo"."Complaints" WHERE "Id" = v_id;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.complaint_update(
    Id uuid, TenantId uuid, "status" varchar(20), Assignee varchar(120)
)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "Subject" varchar(200), "From" varchar(120), "Category" varchar(40),
    "Priority" varchar(10), "Status" varchar(20), "Age" varchar(20), "Assignee" varchar(120), "Body" varchar(2000)
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
BEGIN
    UPDATE "dbo"."Complaints"
    SET "Status" = COALESCE(complaint_update."status", "Status"),
        "Assignee" = COALESCE(complaint_update.Assignee, "Assignee")
    WHERE "Id" = complaint_update.Id AND "TenantId" = complaint_update.TenantId;

    RETURN QUERY
    SELECT "Id", "TenantId", "Subject", "From", "Category", "Priority", "Status", "Age", "Assignee", "Body"
    FROM "dbo"."Complaints" WHERE "Id" = complaint_update.Id AND "TenantId" = complaint_update.TenantId;
END;
$$;

-- Unambiguous contact-identity for a stored thread (name + role), from the owner's perspective.
-- Returns the single resolved user id, or NULL when zero or more-than-one distinct candidates
-- exist, so thread adoption never merges two different people who share a name. Mirrors
-- CommsModule.ResolveContactUserIdAsync (incl. the " (parent)" / Role='Student' -> parent rules).
-- See migrations/0017_thread_create_identity_adoption.sql.
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
            -- creating a second conversation. Exact display-name match wins first (preserves the
            -- original same-name adoption); then a thread whose stored name/role resolves to this
            -- same ContactUserId (unambiguous only), so "Neha Singh" is adopted even when the
            -- sender now presents under a different display name such as "Principal".
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

CREATE OR REPLACE FUNCTION dbo.message_add(
    TenantId uuid, ThreadId uuid, SenderId uuid,
    "text" varchar(2000) DEFAULT NULL, ImageUrl text DEFAULT NULL, CorrelationId uuid DEFAULT NULL
)
RETURNS TABLE (
    "Id" uuid, "ThreadId" uuid, "SenderId" uuid, "Text" varchar(2000), "ImageUrl" text,
    "SentAt" timestamptz, "DeliveredAt" timestamptz, "ReadAt" timestamptz
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE
    v_id uuid := gen_random_uuid();
    v_now timestamptz := now() AT TIME ZONE 'UTC';
    v_text varchar(2000) := COALESCE(message_add."text", '');
    v_correlation_id uuid := COALESCE(CorrelationId, gen_random_uuid());
    v_preview varchar(400);
BEGIN
    v_preview := CASE
        WHEN length(btrim(v_text)) > 0 THEN left(v_text, 400)
        WHEN ImageUrl IS NOT NULL THEN '[Image]'
        ELSE ''
    END;

    INSERT INTO "dbo"."ChatMessages"
        ("Id", "TenantId", "ThreadId", "SenderId", "Text", "ImageUrl", "SentAt", "CorrelationId", "DeliveredAt", "ReadAt")
    VALUES (v_id, TenantId, ThreadId, SenderId, v_text, ImageUrl, v_now, v_correlation_id, NULL, NULL);

    UPDATE "dbo"."ChatThreads" SET "LastMessage" = v_preview, "LastAt" = v_now WHERE "Id" = message_add.ThreadId;

    RETURN QUERY
    SELECT "Id", "ThreadId", "SenderId", "Text", "ImageUrl", "SentAt", "DeliveredAt", "ReadAt"
    FROM "dbo"."ChatMessages" WHERE "Id" = v_id;
END;
$$;

CREATE OR REPLACE FUNCTION dbo.announcement_create(
    TenantId uuid, Title varchar(200), Body varchar(2000), "from" varchar(120),
    "role" varchar(40), "type" varchar(20), Audience varchar(20), CreatorUserId uuid DEFAULT NULL
)
RETURNS TABLE (
    "Id" uuid, "TenantId" uuid, "Title" varchar(200), "Body" varchar(2000), "Date" timestamptz,
    "From" varchar(120), "Role" varchar(40), "Type" varchar(20), "Pinned" boolean, "Audience" varchar(20)
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE v_id uuid := gen_random_uuid();
BEGIN
    INSERT INTO "dbo"."Announcements"
        ("Id", "TenantId", "Title", "Body", "From", "Role", "Type", "Audience", "CreatorUserId")
    VALUES (v_id, TenantId, Title, Body, announcement_create."from", announcement_create."role",
            COALESCE(announcement_create."type", 'info'), Audience, CreatorUserId);

    RETURN QUERY
    SELECT "Id", "TenantId", "Title", "Body", "Date", "From", "Role", "Type", "Pinned", "Audience"
    FROM "dbo"."Announcements" WHERE "Id" = v_id;
END;
$$;
