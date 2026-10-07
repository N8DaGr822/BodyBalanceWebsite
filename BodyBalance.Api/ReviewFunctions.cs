using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace BodyBalance.Api;

public sealed partial class ScheduleFunctions
{
    [Function("BookingReviewDetails")]
    public Task<HttpResponseData> BookingReviewDetails(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "booking-review/details")] HttpRequestData request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async () =>
        {
            var access = await ReadReviewBodyAsync<ReviewAccess>(request, cancellationToken);
            return await ReviewResponseAsync(request, await data.GetBookingReviewAsync(access, cancellationToken));
        }, cancellationToken);

    [Function("BookingReviewDecision")]
    public Task<HttpResponseData> BookingReviewDecision(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "booking-review/decision")] HttpRequestData request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async () =>
        {
            var decision = await ReadReviewBodyAsync<BookingDecision>(request, cancellationToken);
            return await ReviewResponseAsync(request, await data.DecideBookingAsync(decision, cancellationToken));
        }, cancellationToken);

    [Function("PractitionerInbox")]
    public Task<HttpResponseData> PractitionerInbox(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "practitioner/requests")] HttpRequestData request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async () =>
        {
            var identity = Identity(request);
            if (identity is null) return await JsonAsync(request, HttpStatusCode.Unauthorized, new { error = "Sign in to review appointments." });
            var allowed = PractitionerAccess.AllowedPractitioners(identity, configuration);
            var practitioners = allowed.Length == 0 ? [] : (await data.GetPractitionersAsync(cancellationToken))
                .Where(p => allowed.Contains(p.PractitionerId)).ToList();
            var bookings = new List<BookingSummary>();
            foreach (var id in allowed)
                bookings.AddRange(await data.GetPractitionerRequestsAsync(id, cancellationToken));
            return await JsonAsync(request, HttpStatusCode.OK, new { identity.IdentityProvider, identity.UserId,
                identity.UserDetails, practitioners, bookings });
        }, cancellationToken);

    [Function("PractitionerReviewDetails")]
    public Task<HttpResponseData> PractitionerReviewDetails(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "practitioner/requests/{id:guid}/details")] HttpRequestData request,
        Guid id, CancellationToken cancellationToken) => ExecuteAsync(request, async () =>
        {
            var practitionerId = await AuthorizedPractitionerAsync(request, id, cancellationToken);
            if (practitionerId is null) return await ReviewResponseAsync(request, null);
            await ReadReviewBodyAsync<JsonElement>(request, cancellationToken);
            return await ReviewResponseAsync(request, await data.GetBookingReviewAsync(new(id, null), cancellationToken, practitionerId));
        }, cancellationToken);

    [Function("PractitionerReviewDecision")]
    public Task<HttpResponseData> PractitionerReviewDecision(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "practitioner/requests/{id:guid}/decision")] HttpRequestData request,
        Guid id, CancellationToken cancellationToken) => ExecuteAsync(request, async () =>
        {
            var practitionerId = await AuthorizedPractitionerAsync(request, id, cancellationToken);
            if (practitionerId is null) return await ReviewResponseAsync(request, null);
            var decision = await ReadReviewBodyAsync<BookingDecision>(request, cancellationToken);
            // Identity Scope: Route and verified account determine the request; caller-supplied tokens are ignored here.
            decision = decision with { RequestId = id, Token = null };
            return await ReviewResponseAsync(request, await data.DecideBookingAsync(decision, cancellationToken, practitionerId));
        }, cancellationToken);

    [Function("DispatchBookingNotifications")]
    public Task<HttpResponseData> DispatchBookingNotifications(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "booking-notifications/dispatch")] HttpRequestData request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async () =>
        {
            var expected = configuration["NotificationDispatchKey"];
            var supplied = Header(request, "X-Notification-Key");
            if (string.IsNullOrEmpty(expected) || expected.Length < 32 || string.IsNullOrEmpty(supplied) || supplied.Length > 256
                || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(supplied))))
                return await JsonAsync(request, HttpStatusCode.Unauthorized, new { error = "Not authorized." });
            var processed = await notifications.DispatchAsync(null, cancellationToken);
            var contactProcessed = await notifications.DispatchContactAsync(null, cancellationToken);
            return await JsonAsync(request, HttpStatusCode.OK, new { processed, contactProcessed });
        }, cancellationToken);

    private async Task<int?> AuthorizedPractitionerAsync(HttpRequestData request, Guid id, CancellationToken cancellationToken)
    {
        var identity = Identity(request);
        if (identity is null) return null;
        var allowed = PractitionerAccess.AllowedPractitioners(identity, configuration);
        if (allowed.Length == 0) return null;
        var practitionerId = await data.GetBookingPractitionerAsync(id, cancellationToken);
        return practitionerId is { } value && allowed.Contains(value) ? value : null;
    }

    private static PractitionerIdentity? Identity(HttpRequestData request) =>
        PractitionerAccess.ReadIdentity(Header(request, "x-ms-client-principal"));

    private static string? Header(HttpRequestData request, string name) =>
        request.Headers.TryGetValues(name, out var values) && values.Count() == 1 ? values.First() : null;

    private static async Task<T> ReadReviewBodyAsync<T>(HttpRequestData request, CancellationToken cancellationToken)
    {
        // CSRF: Cross-origin forms cannot supply this header; production API CORS stays same-origin.
        if (Header(request, "X-Booking-Review") != "1")
            throw new BookingException("Open the appointment review page to continue.");
        using var reader = new StreamReader(request.Body);
        var buffer = new char[4097];
        var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
        if (count > 4096) throw new BookingException("The review request is too large.");
        return JsonSerializer.Deserialize<T>(buffer.AsSpan(0, count), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new BookingException("Enter the review details.");
    }

    private static Task<HttpResponseData> ReviewResponseAsync(HttpRequestData request, BookingReviewDetails? details) =>
        details is null ? JsonAsync(request, HttpStatusCode.NotFound, new { error = "This review link is invalid, expired, or unavailable to your account. Sign in to review your appointments." })
            : JsonAsync(request, HttpStatusCode.OK, details);
}
