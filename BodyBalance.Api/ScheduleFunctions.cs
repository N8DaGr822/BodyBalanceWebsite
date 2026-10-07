using System.Globalization;
using System.Net;
using System.Text.Json;
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

    [Function("Services")]
    public Task<HttpResponseData> Services(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "practitioners/{id:int}/services")] HttpRequestData request,
        int id, CancellationToken cancellationToken) => ExecuteAsync(request, async () =>
        {
            if (!(await data.GetPractitionersAsync(cancellationToken)).Any(p => p.PractitionerId == id))
                return await JsonAsync(request, HttpStatusCode.NotFound, new { error = "Practitioner not found." });
            return await JsonAsync(request, HttpStatusCode.OK, await data.GetServicesAsync(id, cancellationToken));
        }, cancellationToken);

    [Function("RequestBooking")]
    public Task<HttpResponseData> RequestBooking(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "booking-requests")] HttpRequestData request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async () =>
        {
            // Request Size: Contact details and slot selection fit comfortably within this limit.
            using var reader = new StreamReader(request.Body);
            var buffer = new char[8193];
            var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
            if (count > 8192)
                return await JsonAsync(request, HttpStatusCode.RequestEntityTooLarge, new { error = "The booking request is too large." });
            var booking = JsonSerializer.Deserialize<BookingRequest>(buffer.AsSpan(0, count), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new BookingException("Enter your booking details.");
            return await JsonAsync(request, HttpStatusCode.Created, await data.RequestBookingAsync(booking, cancellationToken));
        }, cancellationToken);

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
            var services = await data.GetServicesAsync(id, cancellationToken);
            var slots = new List<TimeWindow>();
            if (request.Query["serviceId"] is { } serviceValue)
            {
                if (!int.TryParse(serviceValue, out var serviceId) || services.Find(s => s.ServiceId == serviceId) is not { } service)
                    return await JsonAsync(request, HttpStatusCode.BadRequest, new { error = "Choose an active service for this practitioner." });
                slots = Availability.Slots(windows, TimeZoneInfo.FindSystemTimeZoneById(practitioner.TimeZoneId),
                    service.DurationMinutes, practitioner.SlotIntervalMinutes);
            }
            return await JsonAsync(request, HttpStatusCode.OK, new
            {
                practitioner.PractitionerId, practitioner.TimeZoneId,
                practitioner.MinimumNoticeHours, practitioner.SlotIntervalMinutes, practitioner.Location,
                bookingEnabled = services.Count > 0 && !string.IsNullOrWhiteSpace(practitioner.Location), windows, slots
            });
        }, cancellationToken);

    private async Task<HttpResponseData> ExecuteAsync(HttpRequestData request,
        Func<Task<HttpResponseData>> action, CancellationToken cancellationToken)
    {
        try { return await action(); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (BookingException exception)
        {
            return await JsonAsync(request, exception.Conflict ? HttpStatusCode.Conflict : HttpStatusCode.BadRequest,
                new { error = exception.Message });
        }
        catch (JsonException)
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, new { error = "Enter valid booking details." });
        }
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
