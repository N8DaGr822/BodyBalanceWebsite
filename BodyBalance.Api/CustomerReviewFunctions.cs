using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace BodyBalance.Api;

public sealed partial class ScheduleFunctions
{
    [Function("PractitionerCustomerReviews")]
    public Task<HttpResponseData> PractitionerCustomerReviews(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "practitioner/{practitionerId:int}/customer-reviews")] HttpRequestData request,
        int practitionerId, CancellationToken cancellationToken) => ExecuteAsync(request, async () =>
        {
            var (identity, allowed) = await AccessAsync(request, cancellationToken);
            if (identity is null || !allowed.Contains(practitionerId))
                return await JsonAsync(request, HttpStatusCode.Forbidden, new { error = "Sign in with access to this practitioner." });
            return await JsonAsync(request, HttpStatusCode.OK, await data.GetPractitionerCustomerReviewsAsync(practitionerId, cancellationToken));
        }, cancellationToken);

    [Function("RemoveCustomerReview")]
    public Task<HttpResponseData> RemoveCustomerReview(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "practitioner/{practitionerId:int}/customer-reviews/{id:guid}/remove")] HttpRequestData request,
        int practitionerId, Guid id, CancellationToken cancellationToken) => ExecuteAsync(request, async () =>
        {
            var (identity, allowed) = await AccessAsync(request, cancellationToken);
            if (identity is null || !allowed.Contains(practitionerId))
                return await JsonAsync(request, HttpStatusCode.Forbidden, new { error = "Sign in with access to this practitioner." });
            await ReadReviewBodyAsync<JsonElement>(request, cancellationToken);
            return await data.RemoveCustomerReviewAsync(practitionerId, id, cancellationToken)
                ? await JsonAsync(request, HttpStatusCode.OK, new { removed = true })
                : await JsonAsync(request, HttpStatusCode.NotFound, new { error = "This review is unavailable to your account." });
        }, cancellationToken);

    [Function("CustomerReviews")]
    public Task<HttpResponseData> CustomerReviews(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "customer-reviews")] HttpRequestData request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async () =>
            await JsonAsync(request, HttpStatusCode.OK, await data.GetCustomerReviewsAsync(cancellationToken)), cancellationToken);

    [Function("SubmitCustomerReview")]
    public Task<HttpResponseData> SubmitCustomerReview(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "customer-reviews")] HttpRequestData request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async () =>
        {
            // Review Size: Bound request memory while allowing JSON-escaped review text.
            using var reader = new StreamReader(request.Body);
            var buffer = new char[16385];
            var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
            if (count > 16384)
                return await JsonAsync(request, HttpStatusCode.RequestEntityTooLarge, new { error = "Your review is too large." });
            var review = JsonSerializer.Deserialize<CustomerReviewRequest>(buffer.AsSpan(0, count), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new BookingException("Enter your display name and review.");
            var id = await data.SaveCustomerReviewAsync(review, cancellationToken);
            return await JsonAsync(request, HttpStatusCode.Created, new { requestId = id });
        }, cancellationToken, "Reviews are temporarily unavailable. Please try again shortly.", "Enter valid review details.");
}
