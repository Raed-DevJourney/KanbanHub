using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.Azure.Functions.Worker;

namespace KanbanHub.DevHost;

/// <summary>
/// Maps every HTTP-triggered function of the API to the same route on this host, so the endpoint classes
/// run unchanged: same routes, same JSON, same results.
/// </summary>
internal static class FunctionRoutes
{
    private const string RoutePrefix = "/api/";

    public static void MapFunctions(this WebApplication app, Assembly api)
    {
        IEnumerable<MethodInfo> functions = api.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Where(method => method.GetCustomAttribute<FunctionAttribute>() is not null);

        foreach (MethodInfo function in functions) Map(app, function);
    }

    private static void Map(WebApplication app, MethodInfo function)
    {
        HttpTriggerAttribute? trigger = function.GetParameters()
            .Select(parameter => parameter.GetCustomAttribute<HttpTriggerAttribute>())
            .FirstOrDefault(attribute => attribute is not null);
        if (trigger?.Route is null || trigger.Methods is null) return;

        app.MapMethods(RoutePrefix + trigger.Route, trigger.Methods, http => InvokeAsync(function, http));
    }

    private static async Task InvokeAsync(MethodInfo function, HttpContext http)
    {
        object? endpoints = function.IsStatic
            ? null
            : ActivatorUtilities.GetServiceOrCreateInstance(http.RequestServices, function.DeclaringType!);
        object?[] arguments = [.. function.GetParameters().Select(parameter => Bind(parameter, http))];

        object? returned = function.Invoke(endpoints, arguments);
        IActionResult result = returned is Task<IActionResult> pending ? await pending : (IActionResult)returned!;

        await result.ExecuteResultAsync(new ActionContext(http, http.GetRouteData(), new ActionDescriptor()));
    }

    /// <summary>The three kinds of parameter the endpoints take: the request, route values and the cancellation token.</summary>
    private static object? Bind(ParameterInfo parameter, HttpContext http)
    {
        if (parameter.ParameterType == typeof(HttpRequest)) return http.Request;
        if (parameter.ParameterType == typeof(CancellationToken)) return http.RequestAborted;

        string value = http.GetRouteValue(parameter.Name!)?.ToString() ?? "";
        return parameter.ParameterType == typeof(Guid)
            ? Guid.Parse(value)
            : Convert.ChangeType(value, parameter.ParameterType, CultureInfo.InvariantCulture);
    }
}
