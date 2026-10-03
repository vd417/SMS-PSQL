-- 0011: dbo.contact_claims_sync, maintains the dbo."ContactClaims" ledger for one owner with
-- PersonId-aware conflict detection. Additive only: CREATE OR REPLACE FUNCTION; no table,
-- index, RLS or grant changes. Nothing calls it yet.
-- For each kind (email, phone): drops this owner's stale claim, then claims the normalized value.
-- An existing claim is fine if it is this owner's own (PersonId adopted if it was NULL) or
-- belongs to the same person via another owner row; otherwise it raises SQLSTATE 'SMSDC'
-- ('contact_conflict:<kind>'). The INSERT tolerates unique_violation so it stays correct once
-- the (gated, later) unique index on (TenantId, Kind, NormalizedValue) lands.
-- Rollback: DROP FUNCTION dbo.contact_claims_sync(uuid,text,text,uuid,text,text);

CREATE OR REPLACE FUNCTION dbo.contact_claims_sync(
    p_tenant     uuid,
    p_owner_type text,
    p_owner_id   text,
    p_person_id  uuid,
    p_email      text,
    p_phone      text)
RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    v_kind        text;
    v_val         text;
    v_existing_id uuid;
    v_existing_ot text;
    v_existing_oi text;
    v_existing_pi uuid;
BEGIN
    FOREACH v_kind IN ARRAY ARRAY['email', 'phone'] LOOP
        -- reset per-iteration state
        v_existing_id := NULL;
        v_existing_ot := NULL;
        v_existing_oi := NULL;
        v_existing_pi := NULL;

        IF v_kind = 'email' THEN
            v_val := dbo.normalize_email(p_email);
        ELSE
            v_val := dbo.normalize_phone(p_phone);
        END IF;

        -- drop this owner's stale claim of this kind
        DELETE FROM dbo."ContactClaims"
         WHERE "TenantId" = p_tenant
           AND "OwnerType" = p_owner_type
           AND "OwnerId" = p_owner_id
           AND "Kind" = v_kind
           AND (v_val IS NULL OR "NormalizedValue" <> v_val);

        IF v_val IS NULL THEN
            CONTINUE;
        END IF;

        SELECT "Id", "OwnerType", "OwnerId", "PersonId"
          INTO v_existing_id, v_existing_ot, v_existing_oi, v_existing_pi
          FROM dbo."ContactClaims"
         WHERE "TenantId" = p_tenant AND "Kind" = v_kind AND "NormalizedValue" = v_val
         LIMIT 1;

        IF v_existing_id IS NULL THEN
            BEGIN
                INSERT INTO dbo."ContactClaims" ("TenantId", "Kind", "NormalizedValue", "OwnerType", "OwnerId", "PersonId")
                VALUES (p_tenant, v_kind, v_val, p_owner_type, p_owner_id, p_person_id);
                CONTINUE;
            EXCEPTION WHEN unique_violation THEN
                -- a concurrent writer claimed it first: evaluate their claim below
                SELECT "Id", "OwnerType", "OwnerId", "PersonId"
                  INTO v_existing_id, v_existing_ot, v_existing_oi, v_existing_pi
                  FROM dbo."ContactClaims"
                 WHERE "TenantId" = p_tenant AND "Kind" = v_kind AND "NormalizedValue" = v_val
                 LIMIT 1;
            END;
        END IF;

        IF v_existing_ot = p_owner_type AND v_existing_oi = p_owner_id THEN
            -- own claim: adopt the PersonId if it was missing
            IF v_existing_pi IS NULL AND p_person_id IS NOT NULL THEN
                UPDATE dbo."ContactClaims" SET "PersonId" = p_person_id WHERE "Id" = v_existing_id;
            END IF;
        ELSIF p_person_id IS NOT NULL AND v_existing_pi IS NOT NULL AND v_existing_pi = p_person_id THEN
            -- same real person through a different owner row: reuse
            NULL;
        ELSE
            RAISE EXCEPTION 'contact_conflict:%', v_kind USING ERRCODE = 'SMSDC';
        END IF;
    END LOOP;
END;
$$;
