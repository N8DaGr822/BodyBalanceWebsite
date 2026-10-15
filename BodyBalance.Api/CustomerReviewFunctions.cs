using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace BodyBalance.Api;

public sealed partial class ScheduleFunctions
{
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
