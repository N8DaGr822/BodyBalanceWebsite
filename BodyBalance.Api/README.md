# Calendar and booking request API

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

Apply `database/004_contact_messages.sql` after version 3, then `database/configure_api_contact_access.sql`, before deploying this API. General enquiries use `ContactMessages` and `ContactNotifications` and never create appointments. Submission accepts `requestId` (a client GUID preserved on retries), `name` (150 characters), `email` (254), optional `phone` (30), and `message` (4,000). The server validates and normalizes values, saves the message and both notifications in one transaction, and returns a receipt containing only `requestId`. Matching GUID retries return the original receipt; different content with that GUID returns 409. The form locks the original values after an uncertain outcome so its retry cannot silently become a second message.

Configure `ContactRecipientEmail` and `ContactRecipientPhone` in private backend settings for the single shared practice destination. The same paired `NotificationTestEmail` / `NotificationTestPhone` overrides used for bookings apply here. The email contains the message and contact details, with the visitor's email as Reply-To and the configured verified sender as From. SMS is a short alert to check email, without the visitor's message or contact details. These messages require no approval link and do not appear in the practitioner's appointment inbox.

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
| `NotificationTestEmail` / `NotificationTestPhone` | Optional paired override: sends booking and general contact notifications to the tester |
| `ContactRecipientEmail` / `ContactRecipientPhone` | Shared practice contact for general messages; phone uses E.164 format |
| `ContactMessagesPerHour` | Default `20`; limits new general messages across the practice, excluding GUID replays |
| `BookingRequestsPerPractitionerPerHour` | Default `20`; limits new anonymous requests/notification costs, without counting GUID replays twice |

The requested test contacts and freshly generated signing/dispatch secrets are in the ignored `local.settings.json` on this workstation. These values are not deployed by GitHub. Copy the corresponding values privately into Azure's backend settings; keep API credentials out of chat, Git, and `wwwroot`. Use a non-production database for tests. A test recipient override redirects messages only; it does not prevent a test approval from reserving time in whichever database is configured.

Provision Twilio and Resend accounts and senders before testing delivery. They can incur subscription, phone-number, registration and message charges; this repository does not purchase resources or send test messages during builds. Both channels use the providers' REST APIs through one injected `HttpClient`, without adding SDK dependencies. Resend click and open tracking must remain disabled on the sending domain so private approval links are not rewritten or retained by tracking redirects. Booking notification text contains a private review link rather than customer contact information; general contact email contains the submitted message as described above.

For Resend, add `entwinedandunwind.com` under **Domains**, configure the sending DNS records in Cloudflare, and wait for **Verified**. Receiving is not required for these outbound notifications. In the domain's configuration, confirm click and open tracking are off. Create a domain-restricted key with **Sending access**, then set `ResendApiKey` and `NotificationFromEmail` in the Static Web App's production backend environment variables. The former `SendGridApiKey` setting is no longer used. Deploy the updated API before testing the website's booking or contact notifications; a Resend dashboard test alone does not exercise this code.

For production routing, save each practitioner's `NotificationEmail` and `NotificationPhone` in the private `Practitioners` columns, and clear **both** test override settings. Null/unconfigured recipients leave a pending notification rather than guessing a destination. Leslie still needs her services, hours, and location configured before accepting new requests.

### Backup practitioner login

The footer's **Practitioner sign-in** link opens `/practitioner`. Azure Static Web Apps provides Microsoft and GitHub sign-in. Sign in once, then read the account reference displayed on that page. An administrator must verify that it belongs to the intended person and configure `PractitionerAccess__<provider>__<userId>` in backend environment settings with a comma-separated list of allowed practitioner IDs (for example `1,2` for the owner's test account, after verifying the IDs in SQL). `__` maps to the .NET configuration separator; in local settings use `PractitionerAccess:<provider>:<userId>`.

Accounts have **no appointment access by default**, even if signed in. Authorization uses the SWA provider and user ID, not a claimed/display email or a caller-supplied practitioner ID. Map Mary's and Leslie's eventual accounts only to their own IDs. The inbox shows up to 100 pending requests and 100 upcoming confirmations per practitioner; requests from before version 3 and expired links are included.

The API trusts `x-ms-client-principal` only behind the **SWA managed API gateway**. Do not expose these functions as a separate public Function App trusting that header. SWA route rules require authentication for `/api/practitioner/*`, and the API independently checks identity and per-practitioner access. Local identity emulation is for local development only. Review POSTs require `X-Booking-Review: 1`; keep production CORS same-origin to protect cookie-authenticated decisions.

### Private links and delivery recovery

Review links expire after seven days or at session start, whichever is earlier. Their credential is HMAC-bound to the request and practitioner; SQL stores only its SHA-256 hash. The credential travels in a URL fragment, is removed from browser history after loading, and is posted in a body to read/review the request. A GET or email-link preview cannot approve it. Anyone holding the full link can review that request until expiry, so treat it as private. Rotating the signing key changes future links; to revoke existing links immediately, an administrator must also clear their stored `ReviewTokenHash` values. Account-based review remains available.

Each request transaction inserts one email and one SMS outbox row. Atomic claims prevent ordinary concurrent submissions, retries, or scheduled dispatches from resending the same row. `Accepted` means the provider accepted the message, **not** confirmed delivery. Provider logs are the source for bounces and handset delivery; delivery callbacks are not implemented. Missing configuration and HTTP 429 retry after 15 minutes, for at most five claims. Other rejections and uncertain outcomes enter `NeedsReview`. A process interruption can leave `Sending`; these are deliberately not automatically resent because the provider may have accepted them.

Enable `.github/workflows/booking-notifications.yml` by setting the repository variable `BODYBALANCE_SITE_URL` to the connected site's HTTPS origin, and secret `BOOKING_NOTIFICATION_DISPATCH_KEY` to the backend's `NotificationDispatchKey`. This HTTP-only retry job handles up to two booking and two contact rows per run, with a nominal five-minute schedule; GitHub may delay or disable scheduled workflows on inactive repositories, so monitor it. Initial submission also dispatches its own two rows. The backup appointment inbox works independently of notifications.

Use the private outbox queries in `database/README.md` to monitor failures. After correcting configuration, retry only notifications known not to have been accepted by the provider. Reconcile `Sending`/unknown outcomes with provider logs before resetting them; never blindly clear an `Accepted` row. Expired or reviewed requests are not dispatched. No background fire-and-forget work or paid timer/queue infrastructure is added.

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
