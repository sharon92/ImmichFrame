namespace ImmichFrame.WebApi.Services;

public sealed class CloudflareFrame(IConfiguration configuration, TimeProvider time)
{
    public const string Prefix = "/api/cloudflare/frame";
    public const string Target = "cloudflare";
    public const string KeyHeader = "X-ImmichFrame-Device-Key";
    private static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CommandLifetime = TimeSpan.FromSeconds(20);
    private readonly object gate = new();
    private readonly Queue<(CloudCommand Command, DateTimeOffset Expires)> commands = new();
    private Guid frame;
    private string? current;
    private DateTimeOffset lastSeen;

    public record CloudCommand(string Command, decimal? Value, string? Key);
    public record Playback(Guid Frame, string? Current);

    private string? Key => configuration["CLOUDFLARE_FRAME_KEY"];
    public bool Enabled => !string.IsNullOrEmpty(Key);

    public bool Accepts(string key) => Key is { Length: >= 32 and <= 256 } expected && key.Length <= 256 &&
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(key));

    private bool IsOnline(DateTimeOffset now) => frame != Guid.Empty && now - lastSeen < OnlineWindow;

    private void RemoveExpired(DateTimeOffset now)
    {
        while (commands.TryPeek(out var pending) && pending.Expires <= now)
            commands.Dequeue();
    }

    public Playback? Status()
    {
        lock (gate)
            return IsOnline(time.GetUtcNow()) ? new(frame, current) : null;
    }

    public int Enqueue(CloudCommand command)
    {
        lock (gate)
        {
            var now = time.GetUtcNow();
            if (!IsOnline(now)) return StatusCodes.Status503ServiceUnavailable;
            RemoveExpired(now);
            if (commands.Count >= 12) return StatusCodes.Status429TooManyRequests;
            commands.Enqueue((command, now + CommandLifetime));
            return StatusCodes.Status202Accepted;
        }
    }

    public bool CheckIn(Guid id, string? photo, out CloudCommand? command)
    {
        lock (gate)
        {
            var now = time.GetUtcNow();
            command = null;
            if (IsOnline(now) && frame != id) return false;
            if (!IsOnline(now)) commands.Clear();
            RemoveExpired(now);
            frame = id;
            current = photo;
            lastSeen = now;
            if (commands.TryDequeue(out var pending)) command = pending.Command;
            return true;
        }
    }

    public static bool IsPhotoKey(string? key) =>
        key is { Length: 110 } && key.Split('/') is { Length: 3 } parts &&
        parts.All(part => Guid.TryParseExact(part, "D", out _));
}
