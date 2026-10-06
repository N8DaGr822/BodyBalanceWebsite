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

## Scheduling rules

- Each practitioner has separate availability. There is no shared-room constraint.
- `Central Standard Time` is the Windows time-zone identifier for Kansas City, including daylight saving time. Azure hosting region does not determine appointment time zone.
- Weekly hours use local wall-clock times and inclusive effective dates. Sunday is 0, matching C# `DayOfWeek`. Multiple intervals support breaks. Version 1 supports same-day working intervals, not overnight shifts.
- Exceptions and appointment start/end times use UTC in `datetime2` columns. The API must convert local times with the practitioner's time zone and handle ambiguous/invalid daylight-saving times explicitly. A full local day off is converted using both local midnights, not an assumed 24-hour UTC duration.
- Intervals are start-inclusive and end-exclusive; an appointment ending at 11:00 can be followed by one starting at 11:00.
- Added exceptions extend working hours; blocked exceptions take precedence. No availability is published until working hours or added exceptions exist.
- Appointments reference valid practitioner/service assignments and snapshot the service name, price, currency, location, time zone, and start/end times.
- Requested appointments do not reserve time. Confirmed appointments reserve the practitioner's interval; completed/no-show appointments retain occupied history. Cancelled appointments do not block availability. General enquiries without a requested slot are outside this initial schema.
- Deactivate practitioners, services, and assignments rather than deleting booking history. Foreign keys intentionally do not cascade deletes.

## Required before enabling bookings

This script creates storage only. It does not connect the website, publish slots, authenticate practitioners, send email, or prevent overlapping appointments by itself.

The C# API must validate service assignments, working hours, blocked times, contact details, and state transitions. All appointment creation, confirmation, rescheduling, and schedule changes must use a consistent per-practitioner transaction/locking strategy. Check overlaps and write in the same transaction; a separate check followed by an insert is unsafe. `RowVersion` supports stale-edit detection but does not prevent overlapping inserts. Test concurrent bookings before enabling public writes.

Public endpoints return available slots, never customer records or internal exception reasons. Keep SQL credentials/tokens on the backend and give its identity only the required database permissions. The browser must not connect directly to SQL.

Confirm actual service offerings, locations, weekly hours, and any buffer policy before seeding additional data. The website's existing service descriptions/prices have not been treated as confirmed booking configuration.
