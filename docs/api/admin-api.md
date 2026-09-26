# School Admin CRM API (`sms-admin`) — Contract Reference

> **Audience:** the frontend engineer/agent wiring `sms-admin` to the live backend.
> **Status:** **Forward contract** (Phase-2 deliverable). Backend currently implements only Phase 0
> (auth + health). Source of truth: `sms-admin/src/types/index.ts` + `sms-admin/src/data/mockDb.ts` +
> master design `docs/2026-06-13-backend-api-design.md` §3A/§3B/§3C. Machine-readable twin:
> [`admin-api.openapi.yaml`](./admin-api.openapi.yaml).
>
> ⚠️ **Naming:** the `sms-admin` frontend uses **camelCase** with no adapter layer. The backend exposes
> the **canonical snake_case** names below (§3B). The frontend's field-alignment plan
> (`docs/plans/2026-06-13-catreadmin-field-alignment.md` and the admin equivalent) adds the mapper.
> This doc lists the **canonical snake_case** keys; the camelCase the UI uses today is noted where it differs.

---

## 1. Conventions

Same platform conventions as all SMS apps:

| Concern | Rule |
|---|---|
| Base URL | `{API_BASE_URL}/v1` |
| Casing | **snake_case** JSON both ways |
| Dates | ISO-8601 — `YYYY-MM-DD` (dates), `YYYY-MM-DDThh:mm:ssZ` (timestamps) |
| Money | `decimal(18,2)` INR |
| Success | single `{ "data": {…} }` · list `{ "data": [...], "next_cursor": "…"|null }` |
| Error | `{ "error": { "code", "message", "details" } }` |
| Auth | `Authorization: Bearer <access_token>` + `X-Tenant-Id: <school_id>` on every call except login/refresh |
| Roles | `admin` · `principal` · `vice_principal` · `teacher` (capability matrix §6) |
| Paging | cursor: `?limit=50&cursor=…` |

Standard error codes: `invalid_credentials` (401) · `unauthorized` (401) · `forbidden` (403) · `not_found` (404) · `validation_error` (422) · `conflict` (409) · `rate_limited` (429) · `internal_error` (500).

---

## 2. Auth

Unified surface (§3C). Admin logs in with email + password.

- **`POST /v1/auth/login`** — `{ "email", "password" }` → `{ "data": { "access_token", "refresh_token" } }`
- **`POST /v1/auth/refresh`** — `{ "refresh_token" }` → tokens (rotates)
- **`GET /v1/auth/me`** — `{ "data": { "id", "tenant_id", "roles": [...] } }`
- **`POST /v1/auth/logout`** — `{ "refresh_token" }` → `204`

Console kind: emails on the platform owner domain resolve to the `owner` console; others to the `school` console. The token's `tenant_id` scopes all data; send it as `X-Tenant-Id`.

---

## 3. Resources

> Canonical snake_case keys (§3B). "(UI: `x`)" notes the camelCase the current frontend uses.

### 3.1 School (tenant settings) — `GET /v1/schools`, `GET /v1/schools/{id}`
`id` · `name` · `city` · `country` · `tier` (`silver`·`gold`·`platinum`; UI: `plan`) · `status` (`active`·`trial`·`past_due`) · `students_count` (UI: `students`) · `staff_count` (UI: `staff`) · `mrr` · `attendance_pct` (UI: `attendance`) · `fees_pct` (UI: `fees`) · `payroll_pct` (UI: `payroll`) · `currency` · `timezone` (UI: `tz`) · `logo_url` (UI: `logo`) · `color`. Read-only here; `PATCH /v1/schools/{id}` updates settings/branding.

### 3.2 Student — `/v1/students`
Required: `id` · `admission_no` (UI: `adm`) · `name` · `gender` (`M`·`F`) · `grade` · `section` · `class_label` (UI: `cls`) · `roll` · `guardian_name` (UI: `guardian`) · `guardian_phone` (UI: `phone`) · `attendance_pct` (UI: `attendance`) · `fee_status` (`paid`·`partial`·`due`) · `fee_due` · `status` (`active`·`inactive`) · `house` · `avatar_hue`.
Optional enrolment detail: `academic_year`, `admission_date`, `dob`, `blood_group`, `religion`, `category`, `caste`, `mother_tongue`, `languages`, `last_school`, `address`, `email`, `aadhaar`, `father` & `mother` (ParentInfo: `name`,`email`,`phone`,`occupation`,`aadhaar`), `documents` (`birth_cert`,`transfer_cert`,`student_aadhaar`,`father_aadhaar`).

- **`GET /v1/students`** — filters `?q=` (name/admission_no/class) `?grade=` `?status=` `?fee=`; cursor-paged.
- **`GET /v1/students/{id}`** · **`POST /v1/students`** (create) · **`PATCH /v1/students/{id}`** (update).
- **Bulk import:** `POST /v1/students/import` (CSV/XLSX rows mapped to Student) — backed by a TVP bulk proc.

### 3.3 Teacher — `/v1/teachers`
Required: `id` · `name` · `gender` · `department` (UI: `dept`) · `designation` (UI: `desig`) · `subjects` (string[]) · `class_teacher` (class label or null; UI: `classTeacher`) · `phone` · `email` · `exp` · `rating` · `attendance_pct` · `result` · `load` · `status` · `avatar_hue` · `top` (bool).
Optional onboarding: `dob`, `blood_group`, `marital_status`, `alt_phone`, `father_name`, `mother_name`, `aadhaar`, `pan`, `nationality`, `religion`, `languages`, `permanent_address`, `current_address`, `qualification`, `specialization`, `prev_school`, `date_of_joining`, `date_of_leaving`, `employee_type`, `contract_type`, `work_shift`, `work_location`, `basic_salary`, `epf`, `uan`, `username`, plus nested `bank` (`holder`,`account`,`bank`,`ifsc`,`branch`), `emergency` (`person`,`relationship`,`phone`), `transport`, `hostel`, `social`, `leaves`, `documents`.
- **`GET /v1/teachers`** (`?q=` `?dept=` `?status=`) · **`GET /{id}`** · **`POST`** · **`PATCH /{id}`**.

### 3.4 Staff (non-teaching) — `/v1/staff`
Required: `id` · `name` · `gender` · `role` (e.g. "Bus Driver") · `category` (`transport`·`security`·`academic`·`admin`·`support`; UI: `cat`) · `department` (UI: `dept`) · `phone` · `shift` · `route` (or null) · `attendance_pct` · `status` · `avatar_hue`. Optional onboarding mirrors Teacher (bank/emergency/documents/etc.).
- **`GET /v1/staff`** (`?q=` `?cat=`) · **`GET /{id}`** · **`POST`** · **`PATCH /{id}`**.

### 3.5 Bus — `GET /v1/buses`
`id` · `bus_no` (UI: `no`) · `label` · `driver` · `conductor` · `route` · `capacity` · `students` · `stops` · `status` (`on_route`·`at_stop`·`delayed`·`idle`·`maintenance`) · `speed` · `eta` · `fuel` · `color`.

### 3.6 Exam (term) — `/v1/exams`
`id` · `name` · `type` · `grades` (range label) · `from_date` (UI: `from`) · `to_date` (UI: `to`) · `subject_count` (UI: `subjects`) · `status` (`scheduled`·`completed`·`marks_entry`·`draft`) · `marks_entered_pct` (UI: `marksEntered`) · `published` (bool).
- **`GET /v1/exams`** · **`POST /v1/exams`** · **`PATCH /v1/exams/{id}`** (status/published).
- **Datesheet (ExamPaper):** `PUT /v1/exams/{id}/datesheet` with `slots[]`: `subject`·`date`·`start_time` (UI: `start`)·`duration_min` (UI: `duration`)·`room`·`invigilator1` (UI: `inv1`)·`invigilator2` (UI: `inv2`).
- **Marks entry:** `PUT /v1/exams/{id}/marks` with `{ "entries": [ { "student_id", "subject_id", "marks" } ] }` (bulk upsert proc).
- **Exam attendance:** `PUT /v1/exams/{id}/attendance` with `{ "entries": [ { "student_id", "status": "present"|"absent" } ] }`.

### 3.7 Report card — `GET /v1/reports/{student_id}?exam_id=`
`rows[]` (ReportRow: `subject`·`max_marks`·`marks`·`grade`·`gpa`·`pass`) · `total` · `max_total` · `pct` · `grade` (`A1`..`E`) · `gpa` · `result` (`PASS`·`COMPARTMENT`).
**Class rank:** `GET /v1/rank/{student_id}?exam_id=` → `{ "rank", "class_size" }`. Grade scale: ≥91 A1(10)·≥81 A2(9)·≥71 B1(8)·≥61 B2(7)·≥51 C1(6)·≥41 C2(5)·≥33 D(4)·<33 E(3); pass cutoff 33%.

### 3.8 Fee payment — `POST /v1/fees/payments`
`id` · `student_id` · `student_name` · `class_label` (UI: `cls`) · `fee_type` (`academic`·`transport`·`other`) · `amount` · `method` (UI: `mode`; e.g. UPI/Cash/Card) · `ref` · `date`. (Student fee status is derived from invoices.)

### 3.9 Approval — `GET /v1/approvals`
`id` · `type` · `module` (`exams`·`fees`·`hr`·`attendance`·`academics`) · `cap` (`V`·`E`·`A`) · `title` · `detail` · `requester_id` · `requester_name` (UI: `requester`) · `role` · `amount` (or null) · `priority` (`high`·`medium`·`low`) · `status` (`pending`·`approved`·`rejected`) · `for_roles` (string[]) · `applied_on` · `decided_by` · `decided_note`. List is filtered to the caller's role (`for_roles` contains it). **`PATCH /v1/approvals/{id}`** `{ "status", "decided_note" }`.

### 3.10 Communication
- **Threads (parent↔teacher), canonical `/v1/threads`** (§3C): `GET /v1/threads`, `GET /v1/threads/{id}/messages`, `POST /v1/threads/{id}/messages` `{ "text" }`. Thread: `id`·`name`·`role`·`last_message`·`last_at`·`unread`·`group`. Message: `id`·`thread_id`·`sender_id`·`text`·`sent_at`·`is_mine`.
- **Complaints — `GET /v1/complaints`:** `id`·`subject`·`from`·`category` (UI: `cat`)·`priority`·`status` (`open`·`in_progress`·`resolved`)·`age`·`assignee`·`body`.
- **Announcements — `GET/POST /v1/announcements`:** `id`·`title`·`body`·`date`·`from`·`role`·`type` (`info`·`warning`·`event`·`urgent`)·`pinned`·`audience`.
- **Notifications — `GET /v1/notifications`:** `id`·`icon`·`tone`·`title`·`body`·`time`·`unread`.

### 3.11 Attendance (roll-call) — `/v1/classes/{class_id}/attendance`
`GET …?date=YYYY-MM-DD` → `AttendanceRecord[]` (`student_id`·`status` `present`·`absent`·`late`·`leave`·`holiday`·`date`). `POST …` bulk upsert `{ "date", "records": [...] }` (shared with teacher app, §3C).

### 3.12 Operations (KPI dashboards)
All summaries are per-tenant aggregates for the **Operations** screen. Access: principal/admin/owner.
- **Library — `GET /v1/library/summary`:** `catalogue` (total books) · `members` (distinct active borrowers) · `issued` (currently out) · `fines_due` (overdue days × ₹5/day). Derived from `LibraryBooks`.
- **Transport — `GET /v1/transport/summary`:** `vehicles` · `routes` (distinct named routes) · `students` (distinct pupils boarded) · `stops`. Derived from `Buses`/`BusStops`/`Boardings`.
- **Transport fleet — `GET /v1/transport/fleet`:** live board rows (bus + current trip + latest GPS ping + boarded count + derived `status`). Trip→bus linkage is by `BusId` (never bus number).
- **Student→bus roster — `/v1/transport/buses/{busId}/students`:** `GET` lists assigned students (`student_id`·`admission_no`·`bus_no`·`stop_name?`). `PUT /{studentId}` assigns (optional body `{ "stop_id" }`; one active bus per student, upsert). `DELETE /{studentId}` unassigns. Bus/student existence is RLS-scoped, so cross-tenant ids 404.
- **Notify parents — `POST /v1/transport/buses/{busId}/notify`:** body `{ "event_type": "departed"|"approaching"|"arrived", "stop_id"?, "channels": ["push","sms"] }` → `{ "reach" }` (in-app notices + SMS sent). Targets every assigned rider's parent(s) (`ParentStudentLinks` ∪ parent login by admission no); `stop_id` narrows to riders at that stop. SMS goes to the student's `guardian_phone`. Manual send, not deduped. 422 `invalid_event_type`/`invalid_channels`, 404 unknown bus; requires `operations`.
- **Campus geofence — `/v1/me/attendance/school-location`:** `GET` (any role) · `PUT { "lat", "lng", "radius_meters" (30–5000), "name" }` · `DELETE` clears it (subsequent `GET` → 404 `school_location_not_configured`). `PUT`/`DELETE` principal/admin/owner only; requires `attendance.geofence` (platinum).
- **Driver trip mutations (Staff app, `/v1/staff/trips/{tripId}/…`):** ping ingest, `end`, and boarding upsert now verify the trip is owned by the calling driver (`Trips.DriverId = caller`) → 403 otherwise, preventing a same-school driver from acting on a peer's trip.
- **Hostel — `/v1/hostel`:** `GET /summary` → `blocks`·`rooms`·`residents`·`occupancy_pct`. Masters: `GET/POST /blocks` (`name`·`warden`), `GET/POST /rooms` (`block_id`·`room_no`·`capacity`; response adds `block_name`·`residents`), `GET/POST /residents` (`room_id`·`student_name`·`student_id?`; response adds `room_no`).
- **Sports — `/v1/sports`:** `GET /summary` → `teams`·`events`·`athletes` (Σ roster)·`medals` (current year). Masters: `GET/POST /teams` (`name`·`sport`·`coach`·`athletes`), `GET/POST /events` (`name`·`event_date`·`venue`), `GET/POST /medals` (`kind` `gold`·`silver`·`bronze` · `title` · `year?` defaults to current year).

---

## 4. Lists
All list endpoints are cursor-paged (`limit`/`cursor`) and accept the per-resource filters noted above (`q`, `grade`, `status`, `fee`, `dept`, `cat`). Sorting via `?sort=field` / `-field`.

## 5. Tier gating
School tier unlocks modules (`RequireFeature`): **silver** = sis/academics/attendance/exams/fees/communication/operations/library/transport/hostel/sports; **gold** adds hr_payroll/analytics/reports.advanced; **platinum** adds attendance.geofence/transport.gps/support.dedicated.

## 6. RBAC capability matrix (V=view, E=edit, A=approve)
| Module | admin | principal | vice_principal | teacher |
|---|:-:|:-:|:-:|:-:|
| setup | E | V,E | V | — |
| dashboard | V | V | V | V |
| identity | E | V,E | — | — |
| sis (students) | E | V,E | V,E | V |
| academics | E | V,A | E,A | V,E |
| attendance | E | V,A | V,E,A | V,E |
| exams | E | A | A | V,E |
| fees | E | A | V | — |
| hr | E | A | A | — |
| communication | E | E,A | E | E |
| operations | E | V | V | — |
| settings | E | V | V | — |

## 7. Frontend reconciliation notes
- **camelCase → snake_case:** the whole admin UI is camelCase today; the canonical API is snake_case. Add the mapper layer (per the admin field-alignment plan); examples: `adm→admission_no`, `cls→class_label`, `feeStatus→fee_status`, `feeDue→fee_due`, `avatarHue→avatar_hue`, `classTeacher→class_teacher`, `dateOfJoining→date_of_joining`.
- **Validation:** Aadhaar 12 digits, PAN `ABCDE1234F`, IFSC `SBIN0001234`, file ≤4 MB (PDF/JPG/PNG).
- **Envelope/prefix:** current `api.ts` is mock; target the `/v1` base and `{data}`/`{error}` envelopes above.
- Money is `decimal(18,2)`; treat as decimal, not integer paisa.
