namespace DiskWeaver.Daemon;

/// <summary>Credentials for <c>POST /auth/login</c> -- checked against PAM, see <see cref="PamAuthenticator"/>.</summary>
public sealed record LoginRequest(string Username, string Password);
