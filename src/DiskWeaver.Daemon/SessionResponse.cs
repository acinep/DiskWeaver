namespace DiskWeaver.Daemon;

/// <summary>Body of <c>GET /auth/session</c> when a cookie session is currently valid.</summary>
public sealed record SessionResponse(string Username);
