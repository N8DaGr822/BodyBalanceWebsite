using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace BodyBalance.Api;

public sealed partial class ScheduleFunctions
{
    [Function("SendContactMessage")]
    public Task<HttpResponseData> SendContactMessage(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "contact-messages")] HttpRequestData request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async () =>
        {
            // Message Size: Allow JSON escaping for the 4,000-character message while bounding request memory.
            using var reader = new StreamReader(request.Body);
            var buffer = new char[32769];
            var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
            if (count > 32768)
                return await JsonAsync(request, HttpStatusCode.RequestEntityTooLarge, new { error = "Your message is too large." });
            var message = JsonSerializer.Deserialize<ContactRequest>(buffer.AsSpan(0, count), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new BookingException("Enter your contact details and message.");
            var receipt = await data.SaveContactAsync(message, cancellationToken);
            try { await notifications.DispatchContactAsync(receipt.RequestId, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Contact notification dispatch failed for {RequestId}; inspect the outbox.", receipt.RequestId);
            }
            return await JsonAsync(request, HttpStatusCode.Created, receipt);
        }, cancellationToken, "Messages are temporarily unavailable. Please try again shortly.", "Enter valid contact details and a message.");
}
