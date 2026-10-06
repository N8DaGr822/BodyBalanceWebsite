using System.Globalization;
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace BodyBalance.Api;

public sealed class ScheduleFunctions(ScheduleData data, ILogger<ScheduleFunctions> logger)
{
    [Function("Practitioners")]
    public Task<HttpResponseData> Practitioners(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "practitioners")] HttpRequestData request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async () =>
            await JsonAsync(request, HttpStatusCode.OK, await data.GetPractitionersAsync(cancellationToken)), cancellationToken);

    [Function("Availability")]
    public Task<HttpResponseData> GetAvailability(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "practitioners/{id:int}/availability")] HttpRequestData request,
        int id, CancellationToken cancellationToken) => ExecuteAsync(request, async () =>
        {
            if (!DateTime.TryParseExact(request.Query["month"], "yyyy-MM", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var month) || month.Year < 2000 || month.Year > 2100 || id <= 0)
                return await JsonAsync(request, HttpStatusCode.BadRequest, new { error = "Provide a valid practitioner and month (yyyy-MM, years 2000-2100)." });

            var practitioner = (await data.GetPractitionersAsync(cancellationToken)).Find(p => p.PractitionerId == id);
            if (practitioner is null)
                return await JsonAsync(request, HttpStatusCode.NotFound, new { error = "Practitioner not found." });

            var windows = await data.GetAvailabilityAsync(practitioner, month, cancellationToken);
            return await JsonAsync(request, HttpStatusCode.OK, new
            {
                practitioner.PractitionerId, practitioner.TimeZoneId,
                bookingEnabled = false, windows
            });
        }, cancellationToken);

    private async Task<HttpResponseData> ExecuteAsync(HttpRequestData request,
        Func<Task<HttpResponseData>> action, CancellationToken cancellationToken)
    {
        try { return await action(); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogError(exception, "Schedule data could not be loaded.");
            return await JsonAsync(request, HttpStatusCode.ServiceUnavailable,
                new { error = "The schedule is temporarily unavailable. Please try again shortly." });
        }
    }

    private static async Task<HttpResponseData> JsonAsync(HttpRequestData request, HttpStatusCode status, object value)
    {
        var response = request.CreateResponse(status);
        response.Headers.Add("Cache-Control", "no-store");
        await response.WriteAsJsonAsync(value);
        return response;
    }
}
