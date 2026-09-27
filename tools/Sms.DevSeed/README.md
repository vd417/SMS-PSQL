# Sms.DevSeed

Seeds an isolated demo school into a **`*_dev`** PostgreSQL database for teacher-app end-to-end testing.

```bash
# In your own shell: owner role of the _dev db; omit Password to use pgpass. Never commit this.
export SMS_MIGRATOR_CONNECTION="Host=localhost;Port=5432;Database=sms_dev;Username=<owner>"
dotnet run --project tools/Sms.DevSeed -- --i-know-this-is-dev
```

- It refuses any database whose name does not end in `_dev`, and it refuses to run without `--i-know-this-is-dev`.
- It is idempotent: it uses deterministic ids and `INSERT … ON CONFLICT DO NOTHING`. A second run inserts 0 rows.
- It never runs UPDATE, DELETE or TRUNCATE.
- It fails, and commits nothing, if a non-seed row already holds one of its unique keys.

## Seeded logins (dev fixtures, not secrets)

| Email | Password | Tenant | Role | Notes |
|---|---|---|---|---|
| principal@seed.schooldesk.test | DevSeed-Principal-2026! | SchoolDesk Dev Seed | school.principal | |
| teacher.a@seed.schooldesk.test | DevSeed-Teacher-2026! | SchoolDesk Dev Seed | school.teacher | Class teacher of IX-A, IX-A period 1, bus DS-01 duty |
| teacher.b@seed.schooldesk.test | DevSeed-Teacher-2026! | SchoolDesk Dev Seed | school.teacher | Class teacher of IX-B; IX-A period 2 only, so IX-A roll-call returns 403 |
| multi@seed.schooldesk.test | DevSeed-Teacher-2026! | both schools | school.teacher | Exercises /me/schools and switch-school |
| other.teacher@seed.schooldesk.test | DevSeed-Teacher-2026! | Dev Seed Other School | school.teacher | Cross-tenant negative checks |

Data: classes IX-A and IX-B with 10 students each; a Mon–Fri timetable with periods 1–3; exam "Dev Seed Unit Test 1" with 3 papers; one pending leave from teacher B; one announcement; a geofence at 18.5204, 73.8567 (200 m); bus DS-01 on "Dev Seed Route 1" (3 stops, 5 riders, each rider's stop pointing at a `RouteStops` id, matching what a routed bus's `/bus/assigned` returns).

In "Dev Seed Other School", `multi@` also has a Teachers row (`OS-T002`, English Teacher) and a Mon–Fri period-2 English slot on the other school's IX-A, so a teacher-scoped `/classes` returns that class after switch-school.
