# Dev seed (staff app end-to-end check)

`docker compose --profile seed up --build` — applies `staff_e2e.sql` after migrations. Safe to re-run.

Logins (tenant "Greenfield E2E School"): driver@e2e.test, conductor@e2e.test, sweeper@e2e.test,
gardener@e2e.test, guard@e2e.test, peon@e2e.test. None has a password yet: sign in with any
password → "You haven't set a password yet" → request a code (printed in the `api` container log
by the Development OTP sender) → set a password.

Bus `E2E-BUS-01` (driver + conductor assigned) runs "E2E Route 1": 4 stops, students at stops
1, 2 and 4 (stop 3 has none).
