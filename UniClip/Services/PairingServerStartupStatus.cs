namespace UniClip.Services;

public static class PairingServerStartupStatus
{
    public static string Message { get; private set; } = "PIN server is starting…";

    public static void Set(string message)
    {
        Message = message;
    }
}
