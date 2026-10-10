# Handoff: staff-app sends wrong email on driver create → OTP request 404s

**Date:** 2026-09-30
**For:** staff-app (frontend) agent
**From:** backend investigation (branch `feat/teacher-app-sms-api-wiring`)
**Verdict:** Backend is behaving correctly. The bug is in the **staff app**: it sends the
wrong email in the create-driver request body. The OTP 404 is a *downstream symptom*, not a
separate bug.

---

## Symptoms reported

1. Created a driver "Aditya Naidu" in the staff app. It saved under
   `vaibhavdubey417@gmail.com` instead of the email that was typed.
2. `POST http://localhost:5262/v1/auth/otp/request` for the typed email returns
   **404 Not Found**.

## Root cause (one cause, two symptoms)

The staff app sent `vaibhavdubey417@gmail.com` as the driver's email in the
`POST /v1/staff` request body. The backend persisted exactly what it received. When the
staff app later requested an OTP for the email the user *typed*, no `Users` row has that
email, so the OTP endpoint correctly returns `not_registered` (404).

`vaibhavdubey417@gmail.com` is the `Catre:AdminEmail` value in
`src/Sms.Api/appsettings.Development.json` — i.e. the currently-signed-in admin's address.
That strongly suggests the staff app is defaulting/falling back to the logged-in admin's
email (or a stale form field) instead of the driver's typed email.

## Backend evidence (all confirmed by reading the code)

- **OTP endpoint exists and works** — `src/Sms.Api/Controllers/LoginController.cs:23`
  `[HttpPost("otp/request")]`, `[AllowAnonymous]`. Route: `POST /v1/auth/otp/request`.
  (The sibling `POST /v1/auth/login` on the same controller reaches its handler fine.)
- **The 404 is a logical `not_registered`, not a routing miss** —
  `RequestOtpAsync` → `SendOtpToRegisteredAsync`
  (`src/Sms.Application/Services/Auth/AuthService.cs:125-126`, `:492`). If the identifier
  is not found in `Users`, it returns `Error("not_registered", …)` with HTTP **404**
  (`AuthService.cs:497-498`).
- **The create path saves the email verbatim — no substitution** —
  `POST /v1/staff` → `StaffController.Create` (`StaffController.cs:21-23`) →
  `StaffingService.CreateStaffAsync` (`StaffingService.cs:126-134`, a thin delegate) →
  `StaffRepository.CreateAsync` which inserts `r.Email` directly
  (`src/Sms.Modules.Staffing/Data/StaffingRepositories.cs:184-195`). There is no code that
  swaps in the Catre admin email.

## What the staff-app agent should check / fix

1. In the create-driver flow, log the exact JSON body sent to `POST /v1/staff`. Confirm the
   `email` field equals what the user typed — it almost certainly currently equals the
   logged-in admin's email.
2. Likely culprits on the frontend:
   - Form state initialised from / bleeding the logged-in admin profile.
   - A shared/singleton form model reused between "my profile" and "create driver".
   - Autofill or a default value not cleared before submit.
3. After fixing, the same typed email must be the one used for the subsequent
   `POST /v1/auth/otp/request` call.

## How to verify the fix end-to-end

1. Create a driver with a fresh, distinct email (e.g. `driver.test+1@example.com`).
2. Confirm the saved `Staff`/`Users` row carries that email (not the admin's).
3. `POST /v1/auth/otp/request` with `{ "identifier": "driver.test+1@example.com" }` →
   expect **200**, not 404. (In Development, the OTP code is logged server-side —
   see `2bcfe7b feat(auth): dev-only logging of invite/password-setup codes`.)

## Backend action items

None required for this issue. Optional follow-ups if the product wants softer failure modes:
- Consider whether `POST /v1/auth/otp/request` should return a generic 200 for unknown
  identifiers to avoid account-enumeration (currently returns a distinguishable 404). This
  is a deliberate-design question, not a bug.
