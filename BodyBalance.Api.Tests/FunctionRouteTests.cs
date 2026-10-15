using System.Reflection;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Xunit;

namespace BodyBalance.Api.Tests;

public class FunctionRouteTests
{
    [Fact]
    public void HttpRoutesAvoidAzureHostReservedPrefixes()
    {
        foreach (var method in typeof(ScheduleFunctions).GetMethods())
        {
            var function = method.GetCustomAttribute<FunctionAttribute>();
            if (function is null) continue;
            foreach (var parameter in method.GetParameters())
            {
                var trigger = parameter.GetCustomAttribute<HttpTriggerAttribute>();
                if (trigger is null) continue;
                var prefix = (trigger.Route ?? function.Name).TrimStart('/').Split('/')[0];
                Assert.False(prefix.Equals("admin", StringComparison.OrdinalIgnoreCase)
                    || prefix.Equals("runtime", StringComparison.OrdinalIgnoreCase), $"{method.Name} uses an Azure-reserved route prefix.");
            }
        }
    }
}
