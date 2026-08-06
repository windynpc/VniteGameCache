using System.Text.Json;

var sleepMs = 700;
var message = "phase-2.5-test";
var spawnChild = false;

for (var index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--sleep-ms" when index + 1 < args.Length && int.TryParse(args[index + 1], out var parsed):
            sleepMs = parsed;
            index++;
            break;
        case "--message" when index + 1 < args.Length:
            message = args[index + 1];
            index++;
            break;
        case "--spawn-child":
            spawnChild = true;
            break;
    }
}

var outputDirectory = Environment.GetEnvironmentVariable("VGC_TESTGAME_LOG_DIR")
                      ?? Path.Combine(
                          Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                          "VniteGameCache",
                          "testgame");
Directory.CreateDirectory(outputDirectory);

var entry = new
{
    timestamp_utc = DateTimeOffset.UtcNow,
    process_id = Environment.ProcessId,
    working_directory = Environment.CurrentDirectory,
    args,
    message,
    spawn_child = spawnChild
};

var file = Path.Combine(outputDirectory, $"testgame-{DateTimeOffset.UtcNow:yyyyMMdd}.jsonl");
await File.AppendAllTextAsync(file, JsonSerializer.Serialize(entry) + Environment.NewLine);
await Task.Delay(sleepMs);
