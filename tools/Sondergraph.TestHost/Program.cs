using System.Globalization;
using Sondergraph.Workspaces;

// Test-only helper: a real OS process that exercises the workspace lock, so that
// cross-process behaviour can be tested for real rather than simulated in-process.
//
// Usage:
//   Sondergraph.TestHost hold   <workspaceRoot> <holdMillis> [command]
//   Sondergraph.TestHost verify <workspaceRoot>
//   Sondergraph.TestHost write  <workspaceRoot> <relativePath> <content> <iterations>

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: TestHost <hold|verify|write> <workspaceRoot> [...]");
    return 64;
}

var mode = args[0];
var root = Path.GetFullPath(args[1]);

var located = Workspace.Create(root);
if (!located.IsOk)
{
    Console.Error.WriteLine($"host: could not use workspace: {located.Message}");
    return 1;
}

var workspace = located.ValueOrThrow();

switch (mode)
{
    case "hold":
    {
        var holdMillis = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 500;
        var command = args.Length > 3 ? args[3] : "sg test-hold";

        using var acquired = WorkspaceLock.TryAcquire(workspace, command);
        if (!acquired.IsOk)
        {
            // Distinct exit code so the test can tell contention from other failures.
            Console.Error.WriteLine($"host: lock refused: {acquired.Code}: {acquired.Message}");
            return 3;
        }

        // Emitted after acquisition, so the test can sequence on it rather than sleeping.
        Console.WriteLine("ACQUIRED");
        Console.Out.Flush();

        Thread.Sleep(holdMillis);

        Console.WriteLine("RELEASING");
        return 0;
    }

    case "verify":
    {
        // Assert the lock is currently unavailable, as a second process would see it.
        var attempt = WorkspaceLock.TryAcquire(workspace, "sg verify");
        if (attempt.IsOk)
        {
            attempt.ValueOrThrow().Dispose();
            Console.WriteLine("UNLOCKED");
            return 0;
        }

        Console.WriteLine($"LOCKED:{attempt.Code}");
        var holder = WorkspaceLock.ReadHolder(workspace.LockFilePath);
        if (holder is not null)
        {
            Console.WriteLine($"HOLDER:{holder.Command}:{holder.ProcessId}");
        }

        return 0;
    }

    case "write":
    {
        var relativePath = args[2];
        var content = args[3];
        var iterations = args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 50;

        var resolved = workspace.ResolvePath(relativePath);
        if (!resolved.IsOk)
        {
            Console.Error.WriteLine($"host: {resolved.Message}");
            return 1;
        }

        for (var i = 0; i < iterations; i++)
        {
            var write = AtomicFile.WriteAllText(resolved.ValueOrThrow(), $"{content}-{i}");
            if (!write.IsOk)
            {
                Console.Error.WriteLine($"host: {write.Message}");
                return 1;
            }
        }

        Console.WriteLine("WROTE");
        return 0;
    }

    case "read-loop":
    {
        // Reads continuously, reporting any content that is not a whole valid value.
        // Used to prove the atomic write protocol prevents torn reads.
        var relativePath = args[2];
        var expectedPrefix = args[3];
        var seconds = args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 2;

        var resolved = workspace.ResolvePath(relativePath);
        if (!resolved.IsOk)
        {
            Console.Error.WriteLine($"host: {resolved.Message}");
            return 1;
        }

        var path = resolved.ValueOrThrow();
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var reads = 0;

        while (DateTime.UtcNow < deadline)
        {
            var read = AtomicFile.ReadAllTextOrNull(path);
            reads++;

            if (!read.IsOk)
            {
                Console.WriteLine($"READERROR:{read.Message}");
                return 1;
            }

            var text = read.Value;
            if (text is null)
            {
                continue;
            }

            // A valid value is exactly "<prefix>-<digits>". Anything else is a torn read.
            if (!text.StartsWith(expectedPrefix, StringComparison.Ordinal)
                || !int.TryParse(text.AsSpan(expectedPrefix.Length + 1), out _))
            {
                Console.WriteLine($"TORN:{text}");
                return 2;
            }
        }

        Console.WriteLine($"CLEAN:{reads}");
        return 0;
    }

    default:
        Console.Error.WriteLine($"host: unknown mode '{mode}'");
        return 64;
}
