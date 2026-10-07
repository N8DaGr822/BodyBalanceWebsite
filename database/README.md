# BodyBalance database setup

Target: Azure SQL database `bodybalance` on `body-balance.database.windows.net`.

## Apply the initial schema

1. Open the database's Azure Query editor and connect with Microsoft Entra authentication.
2. Open `001_initial_schema.sql` locally and copy the entire file into a new query.
3. Run it against `bodybalance`. The first run returns Mary and Leslie with `Central Standard Time` and null locations.
4. Verify with the read-only query below.

The script uses a transaction and a migration lock. A successful rerun is a no-op, recorded in `dbo.SchemaVersions`. A failure rolls back this run's changes. Existing tables with conflicting names cause failure rather than being replaced. Apply future changes in new numbered scripts; do not edit an already-applied migration.

```sql
SELECT Version, AppliedAtUtc FROM dbo.SchemaVersions;
SELECT PractitionerId, DisplayName, TimeZoneId, Location FROM dbo.Practitioners;
SELECT name FROM sys.tables WHERE schema_id = SCHEMA_ID('dbo') ORDER BY name;
```

Expect version 1, two practitioners, and seven tables including `SchemaVersions`.

## Mary's services and booking requests (version 2)

Run `002_mary_booking_setup.sql` after version 1, then rerun `configure_api_read_access.sql` and run `configure_api_booking_access.sql` as the Entra database administrator. Deploy the updated API only after these scripts succeed. The migration is transactional and a successful rerun is a no-op. It stops for review if Mary already has active services or current/future weekly hours, rather than changing existing configuration silently.

- Mary works Tuesday through Saturday, 10:00-18:00 Central, effective on the local date the migration runs. No inter-session buffer is configured.
- `MinimumNoticeHours = 48` and `SlotIntervalMinutes = 30` are practitioner settings. Other practitioners retain zero minimum notice and their existing hours/services.
- 30-Minute Reiki: `Price = 44`, `RegularPrice = 55`, no first-visit restriction.
- 60-Minute Reiki: `Price = 111`, `FirstTimeClientPrice = 77`. `Price` remains the standard amount; the separate first-visit amount avoids applying the discount to returning clients.
- Both descriptions are stored in `Services`, and both services are assigned only to Mary.
- Mary's location is `Leavenworth - exact address to be confirmed` unless a location was already configured. Confirm the actual address before approving a request.
- `BookingRequestId` is a unique retry reference. `IsFirstVisitRequested` records the customer's claim for Mary's review. Existing appointments keep their snapshots and remain untouched.

The API validates notice, service assignment, complete session duration, clock alignment, blocks and reservations inside a serializable transaction using `BodyBalance.Practitioner.{PractitionerId}` as its application lock. It saves `Requested` appointments, not reservations. Separate people may request the same time; only one overlapping request can subsequently be confirmed.

First-visit eligibility means new to the selected practitioner. A self-declaration plus normalized email history determines the provisional stored price. Prior requested, confirmed, completed or no-show appointments with Mary prevent the automatic discount; cancelled appointments do not. This cannot identify offline visits, different email addresses, or prove identity. Mary must verify eligibility and agree the final price during review. The public response does not reveal email history or the history-derived price. No payment is collected.

### Review requests with Mary

Version 3 adds notification links and a practitioner sign-in screen. The administrator query and review script remain available as a fallback. Run this private query in Azure SQL Query editor; never expose its result publicly:

```sql
SELECT a.BookingRequestId, p.DisplayName, a.ServiceName, a.StartAtUtc, a.EndAtUtc,
    a.TimeZoneId, a.CustomerName, a.CustomerEmail, a.CustomerPhone,
    a.Price AS ProvisionalPrice, a.IsFirstVisitRequested, a.Location, a.CreatedAtUtc
FROM dbo.Appointments a
JOIN dbo.Practitioners p ON p.PractitionerId = a.PractitionerId
WHERE a.Status = 'Requested'
ORDER BY a.CreatedAtUtc;
```

For manual database review, use `review_booking_request.sql` with the request reference and `Confirmed` or `Cancelled`. Confirmation requires the agreed price and exact location. It takes the same practitioner lock, rejects past or occupied times, and rechecks active assignments, notice at submission, session duration, slot alignment, working hours and exceptions. Its conservative hours check requires one complete working interval to cover the session; it rejects sessions spanning multiple adjacent intervals until an administrator reviews the schedule. Approval may occur inside the notice period if the original request met it. Version 3 gives the backend limited UPDATE access for token/account-authorized approvals; anonymous schedule and submission endpoints cannot approve requests.

Do not confirm requests with a bare `UPDATE Status` statement. All future approval, rescheduling, cancellation and schedule-management tools must follow the same transaction/locking strategy. No review script sends messages; the administrator must communicate the outcome and final address to the client.

Verify version 2, five Mary availability rules, and the two service assignments before enabling public requests. Database-backed concurrent request/retry/approval tests should run against a non-production SQL database; unit tests alone cannot prove SQL locking behavior.

## Notifications and practitioner review (version 3)

Run `003_booking_notifications.sql`, then `configure_api_review_access.sql` as the database administrator before deploying the new API. This adds private recipient columns, hashed review credentials, link expiry, a review timestamp, and `BookingNotifications`. Existing appointments and practitioner routing remain unchanged; no contacts or messages are seeded.

New appointment submissions and their two notification rows commit together. Only confirmed appointments reserve calendar availability. Both web approval paths use the existing practitioner lock and recheck availability within the same serializable transaction. Account-to-practitioner permissions and test recipient overrides belong in private backend settings; see `../BodyBalance.Api/README.md` for setup.

Monitor privately:

```sql
SELECT n.AppointmentId, a.BookingRequestId, p.DisplayName, n.Channel, n.State,
    n.Attempts, n.LastError, n.ProviderMessageId, n.ClaimedAtUtc, n.NextAttemptAtUtc,
    a.Status AS AppointmentStatus, a.ReviewExpiresAtUtc
FROM dbo.BookingNotifications n
JOIN dbo.Appointments a ON a.AppointmentId = n.AppointmentId
JOIN dbo.Practitioners p ON p.PractitionerId = a.PractitionerId
WHERE n.State <> 'Accepted'
ORDER BY n.NextAttemptAtUtc;
```

After inspecting provider logs and proving a notification was **not accepted**, an administrator can reset that exact `(AppointmentId, Channel)` row to `State = 'Pending', Attempts = 0, ClaimId = NULL, ClaimedAtUtc = NULL, LastError = NULL, NextAttemptAtUtc = SYSUTCDATETIME()`. Keep the operation scoped to the inspected row. Do not reset `Accepted`, uncertain outcomes, or recently claimed `Sending` rows without reconciliation; doing so can duplicate messages. Expired or already-reviewed requests are skipped by dispatch and remain available in the login inbox when pending.

To set production contacts, update only the intended practitioner's `NotificationEmail` and E.164 `NotificationPhone` after verifying their ID/slug. Clear both test override settings when switching to production routing. These columns are not returned by public practitioner endpoints.

The version 3 migration and SQL concurrency scenarios require validation against a non-production Azure SQL database before enabling production writes. Unit tests validate token/identity boundaries and mocked provider behavior; they do not establish SQL locking or real delivery.

## General contact messages (version 4)

Run `004_contact_messages.sql` after version 3, then `configure_api_contact_access.sql` as the database administrator. The migration is transactional and repeat-safe. It adds `ContactMessages` (name, email, optional phone, message, creation time and unique request reference) and `ContactNotifications` (one email and one SMS delivery record per message). No existing appointments or practitioner settings change.

The API saves the message and its two notification rows together. A matching GUID retry returns the saved receipt; reusing that GUID with different content fails. New submissions share an application lock for the duplicate check and the practice-wide hourly limit. Contact enquiries never reserve time, require approval, or appear in the appointment inbox. Shared practice recipients and test routing are backend settings documented in `../BodyBalance.Api/README.md`.

Monitor privately, using the same provider-reconciliation rules as booking notifications:

```sql
SELECT RequestId, Channel, State, Attempts, LastError, ProviderMessageId,
    ClaimedAtUtc, NextAttemptAtUtc
FROM dbo.ContactNotifications
WHERE State <> 'Accepted'
ORDER BY NextAttemptAtUtc;
```

Contact message contents and customer details are private and have no public read endpoint. If delivery fails, an administrator can retrieve the saved message by its `RequestId` in SQL. Only reset the specific `(RequestId, Channel)` notification after proving it was not accepted by the provider. Validate the migration, concurrent GUID replay and hourly limits against a non-production SQL database before enabling writes.

## Scheduling rules

- Each practitioner has separate availability. There is no shared-room constraint.
- `Central Standard Time` is the Windows time-zone identifier for Kansas City, including daylight saving time. Azure hosting region does not determine appointment time zone.
- Weekly hours use local wall-clock times and inclusive effective dates. Sunday is 0, matching C# `DayOfWeek`. Multiple intervals support breaks. Version 1 supports same-day working intervals, not overnight shifts.
- Exceptions and appointment start/end times use UTC in `datetime2` columns. The API must convert local times with the practitioner's time zone and handle ambiguous/invalid daylight-saving times explicitly. A full local day off is converted using both local midnights, not an assumed 24-hour UTC duration.
- Intervals are start-inclusive and end-exclusive; an appointment ending at 11:00 can be followed by one starting at 11:00.
- Added exceptions extend working hours; blocked exceptions take precedence. No availability is published until working hours or added exceptions exist.
- Appointments reference valid practitioner/service assignments and snapshot the service name, price, currency, location, time zone, and start/end times.
- Requested appointments do not reserve time. Confirmed appointments reserve the practitioner's interval; completed/no-show appointments retain occupied history. Cancelled appointments do not block availability. Version 4 stores general enquiries separately from appointments.
- Deactivate practitioners, services, and assignments rather than deleting booking history. Foreign keys intentionally do not cascade deletes.

## Database responsibilities

The initial schema creates storage only. Version 2 supports appointment requests. Version 3 supports private approval links, account-authorized practitioner review, and durable email/SMS notifications. Version 4 supports general contact messages and their email/SMS notifications.

The C# API must validate service assignments, working hours, blocked times, contact details, and state transitions. All appointment creation, confirmation, rescheduling, and schedule changes must use a consistent per-practitioner transaction/locking strategy. Check overlaps and write in the same transaction; a separate check followed by an insert is unsafe. `RowVersion` supports stale-edit detection but does not prevent overlapping inserts. Test concurrent bookings before enabling public writes.

Public endpoints return available slots, never customer records or internal exception reasons. Keep SQL credentials/tokens on the backend and give its identity only the required database permissions. The browser must not connect directly to SQL.

Mary's version 2 offerings and hours are confirmed by the owner. Confirm other practitioners' offerings, locations, hours and buffer policies before seeding additional data.
