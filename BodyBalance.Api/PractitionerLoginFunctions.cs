using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace BodyBalance.Api;

public sealed partial class ScheduleFunctions
{
    [Function("RequestPractitionerCode")]
    public Task<HttpResponseData> RequestPractitionerCode(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "practitioner-login/code")] HttpRequestData request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async () =>
        {
            var input = await ReadReviewBodyAsync<PractitionerLoginRequest>(request, cancellationToken);
            var email = PractitionerLogin.NormalizeEmail(input.Email);
            var code = PractitionerLogin.CreateCode();
            var hash = PractitionerLogin.CodeHash(email, code, configuration["BookingReviewSigningKey"]);
            if (await data.CreateSignInCodeAsync(email, hash, cancellationToken))
            {
                var delivery = await notifications.SendSignInCodeAsync(email, code, cancellationToken);
                if (delivery.State != "Accepted")
                    logger.LogWarning("Practitioner sign-in email was not accepted: {State}, {Reason}", delivery.State, delivery.Error);
            }
            // Account Privacy: Unknown, disabled and throttled accounts receive the same response.
            return await JsonAsync(request, HttpStatusCode.OK, new { message = "If this email has practitioner access, a code will arrive shortly. Check spam too. Wait a minute before requesting another code." });
        }, cancellationToken, "Sign-in is temporarily unavailable. Please try again shortly.");

    [Function("VerifyPractitionerCode")]
    public Task<HttpResponseData> VerifyPractitionerCode(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "practitioner-login/verify")] HttpRequestData request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async () =>
        {
            var input = await ReadReviewBodyAsync<PractitionerLoginRequest>(request, cancellationToken);
            var email = PractitionerLogin.NormalizeEmail(input.Email);
            if (!PractitionerLogin.ValidCode(input.Code))
                return await JsonAsync(request, HttpStatusCode.Unauthorized, new { error = "Enter a valid six-digit code." });
            var token = PractitionerLogin.CreateSession();
            var hash = PractitionerLogin.CodeHash(email, input.Code!, configuration["BookingReviewSigningKey"]);
            if (!await data.VerifySignInCodeAsync(email, hash, PractitionerLogin.SessionHash(token), cancellationToken))
                return await JsonAsync(request, HttpStatusCode.Unauthorized, new { error = "This code is invalid or expired. Request a new code and try again." });
            var response = await JsonAsync(request, HttpStatusCode.OK, new { signedIn = true });
            response.Headers.Add("Set-Cookie", PractitionerLogin.SessionCookie(token));
            return response;
        }, cancellationToken, "Sign-in is temporarily unavailable. Please try again shortly.");

    [Function("SignOutPractitioner")]
    public Task<HttpResponseData> SignOutPractitioner(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "practitioner-login/logout")] HttpRequestData request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async () =>
        {
            await ReadReviewBodyAsync<System.Text.Json.JsonElement>(request, cancellationToken);
            var token = PractitionerLogin.ReadSession(Header(request, "Cookie"));
            if (token is not null) await data.RevokeLoginSessionAsync(PractitionerLogin.SessionHash(token), cancellationToken);
            var response = await JsonAsync(request, HttpStatusCode.OK, new { signedOut = true });
            response.Headers.Add("Set-Cookie", PractitionerLogin.SessionCookie("", clear: true));
            return response;
        }, cancellationToken);

    private async Task<(PractitionerIdentity? Identity, int[] Allowed)> AccessAsync(HttpRequestData request, CancellationToken cancellationToken)
    {
        var identity = Identity(request);
        var allowed = identity is null ? [] : PractitionerAccess.AllowedPractitioners(identity, configuration);
        // Developer Access: Explicit Azure/GitHub assignments retain precedence and are never changed by email login.
        if (allowed.Length > 0) return (identity, allowed);
        var token = PractitionerLogin.ReadSession(Header(request, "Cookie"));
        if (token is not null && await data.GetLoginSessionAsync(PractitionerLogin.SessionHash(token), cancellationToken) is { } session)
            return (new("email", session.PractitionerId.ToString(System.Globalization.CultureInfo.InvariantCulture), session.Email, ["authenticated"]), [session.PractitionerId]);
        return (identity, allowed);
    }
}
