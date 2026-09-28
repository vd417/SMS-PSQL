# Staff App ↔ sms-api End-to-End Wiring — Design (pointer)

The canonical spec lives in the staff app repo:
`sms-staff/docs/superpowers/specs/2026-09-26-sms-api-end-to-end-wiring-design.md`

Backend changes it requires in this repo (see §3 of that spec):

1. Trip start attribution — driver/conductor always taken from the bus assignment; caller must be the assigned driver or conductor (403 `not_assigned`); no assigned driver → 422 `no_driver_assigned`. Done in C# before the unchanged `dbo.trip_start` call — no migration.
2. `GET /v1/staff/trip/assignment` resolves for conductors too; adds `driver_name`.
3. New participant-only `GET /v1/staff/trips/{tripId}/stops` returning authoritative `TripStopProgress` state.
4. Staff trip JSON gains `current_stop_id`.
5. Idempotent dev seed `db/dev-seed/staff_e2e.sql` behind a `seed` compose profile.
6. Integration tests for attribution, authorization, stop progress read-back and tenant isolation.
