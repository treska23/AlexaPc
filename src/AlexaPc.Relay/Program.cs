using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using AlexaPc.Relay.Contracts;
using AlexaPc.Relay.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// Keep the local development endpoint deterministic. Visual Studio can inject
// ASPNETCORE_URLS with a random port for web projects, which would leave the
// desktop agent trying localhost:5184 while the relay listens elsewhere.
var relayUrls = builder.Configuration["ALEXAPC_RELAY_URLS"];
builder.WebHost.UseUrls(string.IsNullOrWhiteSpace(relayUrls)
    ? "http://0.0.0.0:5184"
    : relayUrls);

builder.Services.AddSingleton<AgentConnectionManager>();

var app = builder.Build();
var deviceToken = builder.Configuration["ALEXAPC_DEVICE_TOKEN"] ?? "dev-device-token";
var apiKey = builder.Configuration["ALEXAPC_API_KEY"] ?? "dev-api-key";

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(20)
});

app.MapGet("/", () => Results.Ok(new
{
    name = "AlexaPc.Relay",
    status = "running"
}));

app.MapGet("/health", (AgentConnectionManager connections) => Results.Ok(new
{
    status = "ok",
    connectedAgents = connections.ConnectedAgents
}));

app.Map("/ws/agent", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var requestedDeviceId = context.Request.Query["deviceId"].ToString().Trim();
    var suppliedToken = context.Request.Query["token"].ToString();

    if (string.IsNullOrWhiteSpace(requestedDeviceId) || !SecureEquals(suppliedToken, deviceToken))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var connections = context.RequestServices.GetRequiredService<AgentConnectionManager>();
    await connections.RegisterAndListenAsync(requestedDeviceId, socket, context.RequestAborted);
});

app.MapPost("/api/commands", async (
    CommandApiRequest request,
    HttpContext context,
    AgentConnectionManager connections) =>
{
    var suppliedKey = context.Request.Headers["X-AlexaPc-Api-Key"].ToString();
    if (!SecureEquals(suppliedKey, apiKey))
    {
        return Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(request.DeviceId) || string.IsNullOrWhiteSpace(request.Command))
    {
        return Results.BadRequest(new CommandApiResponse(false, "deviceId y command son obligatorios."));
    }

    var result = await connections.ExecuteAsync(
        request.DeviceId.Trim(),
        request.Command.Trim(),
        context.RequestAborted);

    if (result is null)
    {
        return Results.Json(
            new CommandApiResponse(false, "El PC no está conectado al relay."),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Ok(result);
});

app.Run();

static bool SecureEquals(string left, string right)
{
    var leftBytes = Encoding.UTF8.GetBytes(left);
    var rightBytes = Encoding.UTF8.GetBytes(right);

    return leftBytes.Length == rightBytes.Length &&
           CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
}

public partial class Program
{
}
