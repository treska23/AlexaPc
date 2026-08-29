namespace AlexaPc.Relay.Contracts;

public sealed record CommandApiRequest(string DeviceId, string Command);

public sealed record CommandApiResponse(bool Success, string Message);

public sealed record RelayCommandMessage(string Type, string RequestId, string Command);

public sealed record RelayResultMessage(string Type, string RequestId, bool Success, string Message);
