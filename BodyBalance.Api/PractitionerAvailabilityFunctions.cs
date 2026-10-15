using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace BodyBalance.Api;

public sealed partial class ScheduleFunctions
{
    [Function("PractitionerAvailability")]
    public Task<HttpResponseData> PractitionerAvailabilitySettings(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "practitioner/{id:int}/availability")] HttpRequestData request,
        int id, CancellationToken cancellationToken) => ExecuteAsync(request, async () =>
        {
            var (identity, allowed) = await AccessAsync(request, cancellationToken);
            if (identity is null || !allowed.Contains(id))
                return await JsonAsync(request, HttpStatusCode.Forbidden, new { error = "Sign in with access to this practitioner." });
            return await JsonAsync(request, HttpStatusCode.OK, await data.GetAvailabilitySettingsAsync(id, cancellationToken));
        }, cancellationToken);

    [Function("SavePractitionerHours")]
    public Task<HttpResponseData> SavePractitionerHours(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "practitioner/{id:int}/availability/hours")] HttpRequestData request,
        int id, CancellationToken cancellationToken) => ChangePractitionerAvailabilityAsync(request, id, "hours", null, cancellationToken);

    [Function("AddPractitionerTimeOff")]
    public Task<HttpResponseData> AddPractitionerTimeOff(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "practitioner/{id:int}/availability/time-off")] HttpRequestData request,
        int id, CancellationToken cancellationToken) => ChangePractitionerAvailabilityAsync(request, id, "time-off", null, cancellationToken);

    [Function("RemovePractitionerTimeOff")]
    public Task<HttpResponseData> RemovePractitionerTimeOff(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "practitioner/{id:int}/availability/time-off/{exceptionId:int}/remove")] HttpRequestData request,
        int id, int exceptionId, CancellationToken cancellationToken) =>
        ChangePractitionerAvailabilityAsync(request, id, "remove", exceptionId, cancellationToken);

    private Task<HttpResponseData> ChangePractitionerAvailabilityAsync(HttpRequestData request, int id, string action,
        int? exceptionId, CancellationToken cancellationToken) => ExecuteAsync(request, async () =>
        {
            // Practitioner Scope: Verify identity before reading or changing any schedule; private review tokens grant no access.
            var (identity, allowed) = await AccessAsync(request, cancellationToken);
            if (identity is null || !allowed.Contains(id))
                return await JsonAsync(request, HttpStatusCode.Forbidden, new { error = "Sign in with access to this practitioner." });
            WeeklyHoursChange? hours = null;
            TimeOffChange? timeOff = null;
            if (action == "hours") hours = await ReadReviewBodyAsync<WeeklyHoursChange>(request, cancellationToken);
            else if (action == "time-off") timeOff = await ReadReviewBodyAsync<TimeOffChange>(request, cancellationToken);
            else await ReadReviewBodyAsync<JsonElement>(request, cancellationToken);
            await data.ChangeAvailabilityAsync(id, hours, timeOff, exceptionId, cancellationToken);
            return await JsonAsync(request, HttpStatusCode.OK, new { saved = true });
        }, cancellationToken);
}
