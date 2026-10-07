# Calendar and booking request API

The .NET 10 isolated Azure Functions project serves public schedule data and accepts appointment requests. Customer contact details and email history stay on the backend. Public responses include the practitioner's published location description, never internal exception reasons or lists of appointments. Administrative approval is performed using the private database review script, not a public write endpoint.

| Endpoint | Result |
| --- | --- |
| `GET /api/practitioners` | Active practitioners, time zones, location descriptions, minimum notice and slot intervals |
| `GET /api/practitioners/1/services` | Active assigned services, descriptions, standard/promotional prices, and first-visit prices |
| `GET /api/practitioners/1/availability?month=2026-10&serviceId=1` | UTC open windows and service-specific slots after the notice cutoff; `bookingEnabled` requires an active service and a configured location |
| `POST /api/booking-requests` | Validates and saves a pending request, returning a receipt; does not reserve time or send email |

An empty schedule returns an empty `windows` array. Configuration, identity, network, or SQL failures return HTTP 503, not an empty schedule. Invalid months return 400 and missing/inactive practitioners return 404. No schedule data is fabricated.

Open windows combine weekly and added hours, then subtract blocks and confirmed/completed/no-show reservations. Slots fit the selected service duration inside those windows and start on the practitioner's local clock grid. Notice is measured in elapsed UTC hours, including weekends and daylight-saving changes. Omitting `serviceId` preserves the open-window response and returns no slots. No buffer is configured. The version 2 migration publishes Mary's hours and services; other practitioners remain unchanged.

Submission accepts `requestId` (a client-generated GUID reused for retries), `practitionerId`, `serviceId`, `startAtUtc` (an ISO timestamp with offset), `customerName`, `customerEmail`, optional `customerPhone`, and `isFirstVisit`. End time, price, currency, service name, and location are computed by the server. Responses use 201 for a saved/replayed receipt, 400 for invalid input, 409 for unavailable times or conflicting retry references, and 503 for infrastructure failures. The browser preserves the reference and details after an uncertain network result. A matching retry returns the original receipt even if the schedule later changes.

First-visit pricing is provisional: it requires a customer claim and no non-cancelled history with that practitioner under the normalized email. Mary verifies eligibility and the final price at approval. No public email-history lookup is exposed, and receipts omit the history-derived price. Requests use a serializable transaction and a per-practitioner application lock; approvals use the same lock in `database/review_booking_request.sql`. Pending requests deliberately do not block each other. There is no automatic email, payment, or practitioner admin UI.

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

As your existing Entra SQL administrator, apply `database/002_mary_booking_setup.sql`, rerun `database/configure_api_read_access.sql`, then run `database/configure_api_booking_access.sql` in the `bodybalance` Query editor. The latter grants the contact-field reads needed for history/retry checks and INSERT on appointments. It grants no UPDATE or DELETE; public callers cannot approve bookings. Keep these SQL permissions on the backend identity. If Entra reports an ambiguous name or cannot resolve the application, inspect that error rather than granting broad directory roles.

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

Commit and push the reviewed files when ready. Run **Deploy Azure Static Web App** manually from GitHub Actions. Its workflow builds/tests the API, publishes both projects, sets only the Azure site's public `ScheduleApiBaseUrl` to `/api`, then deploys with the token. It keeps `/api` paths out of the SPA fallback so failures cannot silently return HTML.

The existing GitHub Pages workflow is unchanged; that site retains the unconnected calendar while its `ScheduleApiBaseUrl` is empty. Use the new `azurestaticapps.net` URL for the connected site. A later decision can retire GitHub Pages or move a custom domain.

Verify on the Azure hostname:

1. `/api/practitioners` returns Mary and Leslie.
2. Mary's services endpoint returns the 30- and 60-minute services. Use the returned IDs when querying availability.
3. `/schedule` shows Mary's Tuesday-Saturday hours, service choices and request form. Dates within 48 hours have no requestable slots. Leslie remains unconfigured.
4. Switch practitioner/month rapidly and navigate away while loading. No obsolete calendar data should replace the latest selection.
5. In a non-production SQL environment, test a request and a retry with the same GUID (one row), simultaneous first-visit requests (only one provisional discount), rejection of a confirmed overlap, and concurrent approvals (only one confirmation). Review the receipt's pending language and confirm that no email or reservation is promised.

SQL may need to wake from auto-pause; if the first call returns 503, retry shortly. If repeated calls fail, inspect configuration, SQL grants, and firewall settings. HTTP 503 deliberately hides internal details from public clients. Keep paid telemetry disabled unless explicitly selected.

## References

- [Managed Functions capabilities and identity limitation](https://learn.microsoft.com/en-us/azure/static-web-apps/apis-functions)
- [.NET 10 API runtime configuration](https://learn.microsoft.com/en-us/azure/static-web-apps/configuration)
- [Static Web Apps hosting plans](https://learn.microsoft.com/en-us/azure/static-web-apps/plans)
- [Create Entra database users](https://learn.microsoft.com/en-us/sql/t-sql/statements/create-user-transact-sql)
