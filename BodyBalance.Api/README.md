# Calendar and booking request API

## Practitioner working hours and time off

Apply `database/configure_api_availability_access.sql` using the database administrator before deploying availability editing. It grants writes to existing availability tables; there are no new tables or schema versions for this feature. In `/admin`, select a practitioner and expand **Manage availability**. Single-practitioner accounts see their own editor automatically.

The authenticated endpoints are `GET /api/practitioner/{id}/availability`, `POST /api/practitioner/{id}/availability/hours`, `POST /api/practitioner/{id}/availability/time-off`, and `POST /api/practitioner/{id}/availability/time-off/{exceptionId}/remove`. All writes require `X-Booking-Review: 1` and the existing session/account authorization for that practitioner; private appointment review links do not grant schedule-editing access. All times use the practitioner's configured zone.

Weekly hours accept `{ version, hours: [{ dayOfWeek, start: "09:00", end: "17:00" }] }`, with Sunday = 0 and up to three non-overlapping periods per day. The version from GET detects concurrent edits. Saving replaces recurring rules from the practitioner's current local date onward, preserving past rules, existing appointments, time off, and `Added` exceptions. The editor warns when dated rules will be replaced. An empty hours array removes recurring availability from today onward. Existing confirmed appointments are not cancelled by working-hour changes.

Time off accepts `{ startDate: "2026-10-20", endDate: "2026-10-21", allDay: true }`, or `allDay: false` plus `startTime` and `endTime` in `HH:mm`. All-day ranges include both dates; timed ranges run continuously from the first date/time to the last date/time. Nonexistent and ambiguous DST boundaries are rejected. Saves use the same transaction-scoped practitioner lock as booking/approval and reject any confirmed appointment overlap, returning the conflicting local times. Identical time-off retries do not add duplicate blocks. Pending requests are retained, but approval rechecks the blocked schedule. Removing a block is idempotent and scoped to the authorized practitioner. Unknown save outcomes require refreshing the editor before further changes.

Before production deployment, verify against a non-production SQL database: the permission script, two-account isolation, competing approval/time-off saves, stale hours versions, rollback on insert failure, identical time-off retries, and practitioner-local midnight/DST behavior. Unit tests cover validation and slot calculation; they do not execute SQL or send live email.

Customer confirmation emails require `database/009_booking_confirmations.sql` before deploying this API. Approval through either the private link or practitioner inbox queues one email to the booking's customer address, containing practitioner, service, local date/time with UTC offset, approved location, and price. The existing Resend settings and notification test overrides apply. Replayed approvals reuse the existing outbox row. Delivery failures do not undo approval; the existing scheduled dispatch retries eligible pending confirmations while the appointment is confirmed and has not started. Declines and previously confirmed appointments do not generate new emails. SQL-script approvals queue the same email for scheduled dispatch.

The .NET 10 isolated Azure Functions project serves public schedule data and accepts appointment requests. Customer contact details and email history stay on the backend. Public responses include the practitioner's published location description, never internal exception reasons or lists of appointments. Practitioners approve through a private notification link or their authorized account at `/practitioner`.

| Endpoint | Result |
| --- | --- |
| `GET /api/practitioners` | Active practitioners, time zones, location descriptions, minimum notice and slot intervals |
| `GET /api/practitioners/1/services` | Active assigned services, descriptions, standard/promotional prices, and first-visit prices |
| `GET /api/practitioners/1/availability?month=2026-10&serviceId=1` | UTC open windows and service-specific slots after the notice cutoff; `bookingEnabled` requires an active service and a configured location |
| `POST /api/booking-requests` | Saves a pending request and durable email/SMS notifications, attempts delivery, and returns a receipt; does not reserve time |
| `POST /api/contact-messages` | Saves a general message and queues email/SMS to the shared practice contact; returns its reference |
| `POST /api/booking-review/details` | Private request details, requiring the notification's request ID and secret token |
| `POST /api/booking-review/decision` | Approve/decline using the private token; approval reserves the existing appointment interval |
| `GET /api/practitioner/requests` | Signed-in account information and only its assigned practitioners' pending/upcoming appointments |
| `POST /api/practitioner/requests/{id}/details` | Request details scoped to the signed-in practitioner's assignments |
| `POST /api/practitioner/requests/{id}/decision` | Account-authorized approval/decline, including when a notification link expires |
| `POST /api/booking-notifications/dispatch` | Bounded booking and contact outbox dispatch, requiring the private `X-Notification-Key` header |

An empty schedule returns an empty `windows` array. Configuration, identity, network, or SQL failures return HTTP 503, not an empty schedule. Invalid months return 400 and missing/inactive practitioners return 404. No schedule data is fabricated.

Open windows combine weekly and added hours, then subtract blocks and confirmed/completed/no-show reservations. Slots fit the selected service duration inside those windows and start on the practitioner's local clock grid. Notice is measured in elapsed UTC hours, including weekends and daylight-saving changes. Omitting `serviceId` preserves the open-window response and returns no slots. No buffer is configured. The version 2 migration publishes Mary's hours and services; other practitioners remain unchanged.

Submission accepts `requestId` (a client-generated GUID reused for retries), `practitionerId`, `serviceId`, `startAtUtc` (an ISO timestamp with offset), `customerName`, `customerEmail`, optional `customerPhone`, and `isFirstVisit`. End time, price, currency, service name, and location are computed by the server. Responses use 201 for a saved/replayed receipt, 400 for invalid input, 409 for unavailable times or conflicting retry references, and 503 for infrastructure failures. The browser preserves the reference and details after an uncertain network result. A matching retry returns the original receipt even if the schedule later changes.

First-visit pricing is provisional: it requires a customer claim and no non-cancelled history with that practitioner under the normalized email. Mary verifies eligibility and the final price at approval. No public email-history lookup is exposed, and receipts omit the history-derived price. Requests and approvals use serializable transactions and the same per-practitioner application lock as `database/review_booking_request.sql`. Pending requests deliberately do not block each other. Email/SMS notify the practitioner only; the practitioner contacts the client with the outcome. No payment is collected.

## General contact messages

`/contact` collects name, email, optional phone and a message. Appointment requests stay on `/schedule`, where the **Request an appointment** link and calendar day/available-hours clicks lead to the date/time controls. A day with no requestable slots explains that another date is needed. With an empty `ScheduleApiBaseUrl`, both pages explicitly show that online submissions are unavailable; the contact form cannot report a successful send.

Apply `database/004_contact_messages.sql` after version 3, then `database/configure_api_contact_access.sql` and `database/005_contact_practitioner.sql`, before deploying this API. General enquiries use `ContactMessages` and `ContactNotifications` and never create appointments. Submission accepts `requestId` (a client GUID preserved on retries), an active `practitionerId`, `name` (150 characters), `email` (254), optional `phone` (30), and `message` (4,000). The server validates and normalizes values, saves the message and both notifications in one transaction, and returns a receipt containing only `requestId`. Matching GUID retries return the original receipt; different content or practitioner with that GUID returns 409. The form locks the original values after an uncertain outcome so its retry cannot silently become a second message. Older forms without a practitioner selection receive a validation error and must be refreshed.

New contact messages use the selected practitioner's private `NotificationEmail` and `NotificationPhone`, as appointment requests do. Missing practitioner destinations do not fall back to another inbox. `ContactRecipientEmail` and `ContactRecipientPhone` apply only to legacy messages with no practitioner selection. The same `NotificationTestEmail` / `NotificationTestPhone` overrides used for bookings apply here. If either override is set, channels without a test recipient stay pending rather than sending to a live recipient. The email contains the message and contact details, with the visitor's email as Reply-To and the configured verified sender as From. SMS is a short alert to check email, without the visitor's message or contact details. These messages require no approval link and do not appear in the practitioner's appointment inbox.

`ContactMessagesPerHour` defaults to `20` across the practice. A transaction lock makes the hourly limit and GUID check atomic; replaying a saved message does not consume another allowance. This bounds new messages and notification costs but is not bot detection. The existing notification sender, dispatch endpoint and retry workflow handle both outboxes with the same delivery/reconciliation policy below.

## Approval and notification setup

Apply `database/003_booking_notifications.sql` and `database/configure_api_review_access.sql` before deploying this version. Earlier pending requests remain reviewable through practitioner sign-in; no notifications or tokens are retroactively generated for them. Approval rechecks active assignments, duration, hours, exceptions, reservations and notice at submission. It requires the final price and confirmed address. A duplicate identical decision returns the existing result; a used link cannot change the decision. General cancellation/rescheduling of confirmed appointments remains an administrator operation.

Configure these **backend** settings, never the public WASM settings:

| Setting | Value |
| --- | --- |
| `BookingSiteUrl` | The connected Azure site's HTTPS origin, such as `https://your-site.azurestaticapps.net` |
| `BookingReviewSigningKey` | A cryptographically random secret, at least 32 bytes; required for new requests |
| `NotificationDispatchKey` | A separate random secret of at least 32 characters |
| `ResendApiKey` | Resend API key with Sending access, restricted to the verified sending domain |
| `NotificationFromEmail` | An email address on the verified Resend domain, such as `notifications@entwinedandunwind.com` (address only) |
| `TwilioAccountSid` | Twilio account SID (`AC...`) |
| `TwilioApiKeySid` / `TwilioApiKeySecret` | Twilio API key (`SK...`) and secret with permission to create messages |
| `NotificationFromPhone` | Your SMS-enabled Twilio number in E.164 format, with the applicable sender registration completed |
| `NotificationTestEmail` / `NotificationTestPhone` | Optional test overrides: configured channels send to the tester; channels without a test recipient stay pending |
| `ContactRecipientEmail` / `ContactRecipientPhone` | Fallback only for legacy contact messages without a practitioner; phone uses E.164 format |
| `ContactMessagesPerHour` | Default `20`; limits new general messages across the practice, excluding GUID replays |
| `BookingRequestsPerPractitionerPerHour` | Default `20`; limits new anonymous requests/notification costs, without counting GUID replays twice |

The requested test contacts and freshly generated signing/dispatch secrets are in the ignored `local.settings.json` on this workstation. These values are not deployed by GitHub. Copy the corresponding values privately into Azure's backend settings; keep API credentials out of chat, Git, and `wwwroot`. Use a non-production database for tests. A test recipient override redirects messages only; it does not prevent a test approval from reserving time in whichever database is configured.

Provision Twilio and Resend accounts and senders before testing delivery. They can incur subscription, phone-number, registration and message charges; this repository does not purchase resources or send test messages during builds. Both channels use the providers' REST APIs through one injected `HttpClient`, without adding SDK dependencies. Resend click and open tracking must remain disabled on the sending domain so private approval links are not rewritten or retained by tracking redirects. Booking notification text contains a private review link rather than customer contact information; general contact email contains the submitted message as described above.

For Resend, add `entwinedandunwind.com` under **Domains**, configure the sending DNS records in Cloudflare, and wait for **Verified**. Receiving is not required for these outbound notifications. In the domain's configuration, confirm click and open tracking are off. Create a domain-restricted key with **Sending access**, then set `ResendApiKey` and `NotificationFromEmail` in the Static Web App's production backend environment variables. The former `SendGridApiKey` setting is no longer used. Deploy the updated API before testing the website's booking or contact notifications; a Resend dashboard test alone does not exercise this code.

For production routing, save each practitioner's `NotificationEmail` and `NotificationPhone` in the private `Practitioners` columns, and clear **both** test override settings. For email-only testing, set only `NotificationTestEmail`; SMS stays pending. Null/unconfigured recipients leave a pending notification rather than guessing a destination. Leslie still needs her services, hours, and location configured before accepting new requests.

### Appointment admin and practitioner login

The footer's **Admin sign-in** link opens `/admin`; existing `/practitioner` links still work. Azure Static Web Apps provides Microsoft and GitHub sign-in. Sign in once, then read the account reference displayed on that page. An administrator must verify that it belongs to the intended person and configure `PractitionerAccess__<provider>__<userId>` in backend environment settings with a comma-separated list of allowed practitioner IDs (for example `1,2` for the owner's account, after verifying the IDs in SQL). `__` maps to the .NET configuration separator; in local settings use `PractitionerAccess:<provider>:<userId>`.

Accounts have **no appointment access by default**, even if signed in. Authorization uses the SWA provider and user ID, not a claimed/display email or a caller-supplied practitioner ID. Map Mary's and Leslie's eventual accounts only to their own IDs. The inbox shows up to 100 pending requests and 100 upcoming confirmations per practitioner; requests from before version 3 and expired links are included.

The admin dashboard displays pending requests separately from a weekly calendar, with an optional practitioner filter. It uses the existing inbox endpoint with `?week=yyyy-MM-dd` to load appointments overlapping seven Central Time calendar days. The weekly results include confirmed, completed, and no-show appointments, including manually entered appointments without a request reference; they are not limited by the upcoming-list cap. Pending and cancelled requests do not occupy the calendar. Week navigation follows the browser culture's first day of the week. Calendar entries are read-only; request details reuse the existing approval and decline form. Approval reserves the time but does not send a decision email to the client.

The API trusts `x-ms-client-principal` only behind the **SWA managed API gateway**. Do not expose these functions as a separate public Function App trusting that header. The gateway allows `/api/practitioner/*` through because email sessions are verified by this API, not Azure's identity provider. Every inbox/details/decision endpoint independently checks the Azure identity or database session and the allowed practitioner IDs. Local identity emulation is for local development only. Review and login POSTs require `X-Booking-Review: 1`; keep production CORS same-origin to protect cookie-authenticated decisions.

### Email-code practitioner sign-in (Free plan)

Apply `database/006_practitioner_login.sql` and `database/configure_api_login_access.sql` before deploying. Provision explicitly approved email addresses in `PractitionerLoginAccounts`, with one account per practitioner. There is no public registration. Login ownership is separate from notification routing; changing `Practitioners.NotificationEmail` does not grant access. The API cannot insert accounts or modify their email, practitioner ID, or enabled state. The developer's existing GitHub configuration retains access to both practitioners and takes precedence over an email session.

`POST /api/practitioner-login/code` sends a six-digit code only to an enabled account whose practitioner is active. Unknown, disabled and rate-limited addresses receive the same response. Codes expire after ten minutes, allow five verification attempts and are replaced by a new code. Sends are limited to one per minute and five per hour per account. Authentication emails bypass notification test overrides, so a test inbox can never receive someone else's sign-in code. Delivery failures are logged without the code or recipient; the user can request another code after the cooldown.

`POST /api/practitioner-login/verify` consumes the code and creates the session in one transaction. SQL stores HMAC hashes of codes, using a purpose-separated input with the existing `BookingReviewSigningKey`, and SHA-256 hashes of 256-bit random session tokens. The opaque session cookie is host-only, Secure, HttpOnly and SameSite=Strict, expires after eight hours (or a fixed 30 days when the practitioner selects **Remember me on this device**) and is never stored in browser local storage. SQL and cookie expiration use the same duration; ordinary requests do not extend it. Existing sessions keep their original expiration. Clearing browser cookies or using another browser requires another sign-in. Every API request rechecks session expiration, revocation, account enablement and practitioner activity. Sign-out revokes the database session before clearing the cookie. Disabling the account immediately blocks existing sessions; before changing its email, also revoke its sessions and clear its pending code. Administrators may delete expired sessions during normal database maintenance; the API has no DELETE permission.

Before rollout, verify valid/invalid/expired/reused codes, lockout and resend limits, concurrent verification, disabled-account rejection, logout/replay rejection, Mary/Leslie isolation, anonymous denial and unchanged GitHub access. Unit tests do not prove SQL transaction behavior or live email/cookie delivery; verify those against the deployed environment with the account owner's participation. Email login inherits the security of the practitioner's email account and does not provide phishing-resistant MFA.

### Optional Google sign-in setup

The Google integration is prepared but disabled until hosting and OAuth credentials are ready. Azure custom authentication requires the paid Static Web Apps Standard plan and disables the preconfigured providers. Obtain approval for the plan change first. Configure both Google and GitHub registrations before switching, to retain the developer's GitHub sign-in. The database application's `AZURE_CLIENT_*` credentials must not be reused for website login.

1. Create a Google OAuth web application for Entwined & Unwind with callback `https://entwinedandunwind.com/.auth/login/google/callback`. Configure its consent screen; while in testing, add the intended practitioner accounts as test users. Store its client ID and secret directly in Azure production settings as `AUTH_GOOGLE_CLIENT_ID` and `AUTH_GOOGLE_CLIENT_SECRET`.
2. Create a GitHub OAuth application with homepage `https://entwinedandunwind.com` and callback `https://entwinedandunwind.com/.auth/login/github/callback`. Store its ID and secret as `AUTH_GITHUB_CLIENT_ID` and `AUTH_GITHUB_CLIENT_SECRET`. Keep all secrets out of source and chat.
3. Once Standard and all four settings are ready, set the GitHub repository variable `BODYBALANCE_GOOGLE_AUTH_ENABLED=true` and run the Azure deployment workflow. It adds `deployment/google-github-auth.json` to the deployed configuration and shows Google/GitHub login. Microsoft login remains available until this switch, then is replaced by Google.
4. Verify the developer's GitHub login and existing assignment. Have Mary and Leslie sign in with their own Google accounts and supply the displayed account references. After verifying ownership, assign `PractitionerAccess__google__<MaryUserId>=1` and `PractitionerAccess__google__<LeslieUserId>=2`. Do not grant access by matching a display email. New Google accounts have no appointment access until assigned.

If activation fails, clear the repository variable and redeploy to restore preconfigured login. Verify the developer account reference and assignment after either provider change. Reference: [Azure custom authentication](https://learn.microsoft.com/azure/static-web-apps/authentication-custom).

### Private links and delivery recovery

Review links expire after seven days or at session start, whichever is earlier. Their credential is HMAC-bound to the request and practitioner; SQL stores only its SHA-256 hash. The credential travels in a URL fragment, is removed from browser history after loading, and is posted in a body to read/review the request. A GET or email-link preview cannot approve it. Anyone holding the full link can review that request until expiry, so treat it as private. Rotating the signing key changes future links; to revoke existing links immediately, an administrator must also clear their stored `ReviewTokenHash` values. Account-based review remains available.

Each request transaction inserts one email and one SMS outbox row. Atomic claims prevent ordinary concurrent submissions, retries, or scheduled dispatches from resending the same row. `Accepted` means the provider accepted the message, **not** confirmed delivery. Provider logs are the source for bounces and handset delivery; delivery callbacks are not implemented. Missing configuration and HTTP 429 retry after 15 minutes, for at most five claims. Other rejections and uncertain outcomes enter `NeedsReview`. A process interruption can leave `Sending`; these are deliberately not automatically resent because the provider may have accepted them.

Enable `.github/workflows/booking-notifications.yml` by setting the repository variable `BODYBALANCE_SITE_URL` to the connected site's HTTPS origin, and secret `BOOKING_NOTIFICATION_DISPATCH_KEY` to the backend's `NotificationDispatchKey`. This HTTP-only retry job handles up to two booking and two contact rows per run, with a nominal five-minute schedule; GitHub may delay or disable scheduled workflows on inactive repositories, so monitor it. Initial submission also dispatches its own two rows. The backup appointment inbox works independently of notifications.

Use the private outbox queries in `database/README.md` to monitor failures. After correcting configuration, retry only notifications known not to have been accepted by the provider. Reconcile `Sending`/unknown outcomes with provider logs before resetting them; never blindly clear an `Accepted` row. Expired or reviewed request notifications are not dispatched; customer confirmations are dispatched only while confirmed and before the appointment starts. No background fire-and-forget work or paid timer/queue infrastructure is added.

## Local development

Prerequisites: .NET 10 SDK, Azure CLI, and Azure Functions Core Tools v4 with .NET 10 support.

1. Copy `local.settings.example.json` to `local.settings.json` in this folder. The latter is gitignored and excluded from publishing.
2. Sign in yourself with `az login` and select the BodyBalance subscription. The SDK uses your local Azure credentials. Your SQL client IP firewall rule must permit this machine.
3. From this folder run `func start`. For these HTTP-only functions, no storage emulator is needed.
4. Test `http://localhost:7071/api/practitioners` and the availability endpoint above.
5. For local frontend integration only, temporarily set `ScheduleApiBaseUrl` in `BodyBalanceWebsite/wwwroot/appsettings.json` to `http://localhost:7071/api`, then run the website's HTTP launch profile on port 5171. Restore the empty value before committing. No secret belongs in frontend configuration.

Tests: `dotnet test BodyBalance.Api.Tests/BodyBalance.Api.Tests.csproj -c Release` from the repository root.

## Azure setup (not performed automatically)

Use **Azure Static Web Apps, Free**, with its **managed API**. Do not create an App Service, a separately billed Function App, a storage account, or paid monitoring for this setup. Keep the SQL free-offer cutoff set to pause rather than charge for overages. The Free hosting plan has quotas and no SLA.

### 1. Create the static web app

In Azure, search for **Static Web Apps** and create `bodybalance-web` under subscription/resource group `BodyBalance`. Select **Free**, and choose West US 2 if available for the managed API, otherwise a nearby supported region. Choose deployment source **Other** to use the prepared workflow instead of having Azure generate a second workflow. Creating this resource alone does not deploy the local code.

Copy its deployment token into the GitHub repository's Actions secret named `AZURE_STATIC_WEB_APPS_API_TOKEN`. Keep the token out of chat and source control.

### 2. Give the managed API its own Entra identity

Managed Static Web Apps functions do not support managed identity. This project uses `DefaultAzureCredential`, with a dedicated application identity in Azure and Azure CLI credentials locally.

In **Microsoft Entra ID > App registrations > New registration**:

- Name: `bodybalance-api` (use a unique display name in the tenant).
- Supported account types: this organizational directory only.
- No redirect URI is needed for this backend identity.

Record its **Application (client) ID** and **Directory (tenant) ID**. Under **Certificates & secrets**, create a client secret and keep its **Value** privately. Record its expiration and rotate it before that date. No Microsoft Graph application permissions or subscription Contributor role are needed by the API.

As your existing Entra SQL administrator, apply `database/002_mary_booking_setup.sql`, rerun `database/configure_api_read_access.sql`, then run `database/configure_api_booking_access.sql` in the `bodybalance` Query editor. Apply version 3 and `configure_api_review_access.sql` next, followed by version 4 and `configure_api_contact_access.sql`. These grant the backend the limited reads/writes needed for submission, notifications and authorized review, with no DELETE permission. Keep these SQL permissions on the backend identity. If Entra reports an ambiguous name or cannot resolve the application, inspect that error rather than granting broad directory roles.

### 3. Configure backend settings

In the static web app's **Environment variables / Configuration**, add these values for the production API:

| Name | Value |
| --- | --- |
| `AZURE_TENANT_ID` | Your app registration's directory ID |
| `AZURE_CLIENT_ID` | Its application ID |
| `AZURE_CLIENT_SECRET` | The private secret value, not the secret ID |
| `SqlConnectionString` | `Server=tcp:body-balance.database.windows.net,1433;Database=bodybalance;Encrypt=True;TrustServerCertificate=False;Connection Timeout=20;` |

Do not add a SQL password or an `Authentication` keyword: the API supplies an Entra access token. These settings belong on the API, not in `wwwroot/appsettings.json`.

### 4. Decide the SQL network access

Your current client-IP rule allows your computer, not the cloud API. The managed API also needs a SQL firewall path. With this managed hosting model, do not assume a single observed outbound IP is permanent.

The low-cost Azure option is the SQL server setting **Allow Azure services and resources to access this server**. This is broader than this app or subscription: Azure-hosted resources can reach the SQL authentication boundary, but still require a valid database identity and permissions. Review this tradeoff before enabling it; no firewall change is made by this repository. If this network boundary is unacceptable, choose hosting with controlled egress/private networking instead and re-evaluate its costs. Do not add an all-internet firewall rule.

### 5. Publish and verify

Commit and push the reviewed files when ready. Run **Deploy Azure Static Web App** manually from GitHub Actions. Its workflow builds/tests the API, sets only the Azure site's public `ScheduleApiBaseUrl` to `/api` before publishing both projects, then deploys with the token. Setting the address before publishing also updates Blazor's precompressed configuration files. It keeps `/api` paths out of the SPA fallback so failures cannot silently return HTML.

Both sites remain available. The GitHub Pages workflow sets the public `BookingSiteUrl` to `https://brave-flower-0c681771e.1.azurestaticapps.net` and leaves `ScheduleApiBaseUrl` empty. Opening `/schedule` on GitHub Pages redirects to the Azure calendar, preserving the fragment (including `#request-session`) and replacing the history entry so Back returns to the referring page. Azure continues to use its same-origin `/api` and does not redirect. Local development is unchanged unless `BookingSiteUrl` is explicitly configured. Deploy and verify the Azure calendar before publishing the GitHub Pages redirect; changing the destination hostname requires updating the Pages workflow.

Verify on the Azure hostname:

1. `/api/practitioners` returns Mary and Leslie.
2. Mary's services endpoint returns the 30- and 60-minute services. Use the returned IDs when querying availability.
3. `/schedule` shows Mary's Tuesday-Saturday hours, service choices and request form. Dates within 48 hours have no requestable slots. Leslie remains unconfigured.
4. Switch practitioner/month rapidly and navigate away while loading. No obsolete calendar data should replace the latest selection.
5. In a non-production SQL environment, test a request and a retry with the same GUID (one appointment and two outbox rows), simultaneous first-visit requests (only one provisional discount), rejection of a confirmed overlap, and concurrent approvals (only one confirmation).
6. With the recipient override and provider accounts configured, verify both test notifications, link expiry, repeated decisions, unassigned account denial, and that a Mary's-only account cannot access Leslie's requests. Verify confirmed time disappears from public availability on refresh and no customer details appear there.
7. Simulate provider 429, rejection and unknown outcomes; verify one channel's failure does not lose the booking or resend an already-accepted channel. Confirm the retry workflow succeeds without exposing its secret.
8. Submit a general contact message and replay the same GUID. Verify one `ContactMessages` row, exactly two `ContactNotifications` rows, no appointment, and shared test routing for both channels. Test concurrent replay and hourly limits against non-production SQL; mocked browser/provider checks do not establish SQL behavior or delivery.

SQL may need to wake from auto-pause; if the first call returns 503, retry shortly. If repeated calls fail, inspect configuration, SQL grants, and firewall settings. HTTP 503 deliberately hides internal details from public clients. Keep paid telemetry disabled unless explicitly selected.

## References

- [Managed Functions capabilities and identity limitation](https://learn.microsoft.com/en-us/azure/static-web-apps/apis-functions)
- [.NET 10 API runtime configuration](https://learn.microsoft.com/en-us/azure/static-web-apps/configuration)
- [Static Web Apps hosting plans](https://learn.microsoft.com/en-us/azure/static-web-apps/plans)
- [Create Entra database users](https://learn.microsoft.com/en-us/sql/t-sql/statements/create-user-transact-sql)
- [SWA authentication](https://learn.microsoft.com/en-us/azure/static-web-apps/authentication-authorization) and [trusted user information](https://learn.microsoft.com/en-us/azure/static-web-apps/user-information)
- [Twilio message API](https://www.twilio.com/docs/messaging/api/message-resource) and [Resend Send Email API](https://resend.com/docs/api-reference/emails/send-email)
- [Azure Communication Services retirement](https://learn.microsoft.com/en-us/azure/communication-services/acs-retirement-and-breaking-changes-guide): why this implementation does not use ACS email/SMS
