using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    const int HEARTBEAT_INTERVAL_SECONDS = 30;
    static async Task Main(string[] args)
    {
        Console.WriteLine("=== Usage Tracker Test - Offline Mode ===\n");

        //UsageTracker.StorageDirectory = Path.Combine(
        //    AppDomain.CurrentDomain.BaseDirectory,
        //    "TestData"
        //);

        Console.Write("Username: ");
        string username = Console.ReadLine() ?? "";
        Console.Write("Password: ");
        string password = Console.ReadLine() ?? "";

        var session = new UsageSession(username, password);

        Console.Write("Start with online login? (Y/N): ");
        var loginChoice = Console.ReadKey();
        Console.WriteLine("\n");

        LoginResult result;
        if (loginChoice.Key == ConsoleKey.Y)
        {
            Console.WriteLine("Processing online login...");
            result = session.ProcessOnlineLogin();
            Console.WriteLine($"✓ {result.Message}\n");
        }

        Console.WriteLine("Processing offline login...");
        result = session.ProcessOfflineLogin();

        if (!result.Success)
        {
            Console.WriteLine($"ERROR: {result.Message}");
            Console.WriteLine("Initial online login is required before offline use is available!");
            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
            return;
        }

        Console.WriteLine($"✓ {result.Message}\n");

        Console.WriteLine("Initial Offline Stats:");
        DisplayStats(result.Stats);

        Console.WriteLine($"\n=== Starting Heartbeat (every {HEARTBEAT_INTERVAL_SECONDS} seconds) ===");
        Console.WriteLine("Watch the offline time accumulate - press 'Q' to quit");
        
        using (var cts = new CancellationTokenSource())
        {
            var heartbeatTask = RunHeartbeat(session, cts.Token);

            while (true)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.Q)
                    {
                        Console.WriteLine("\nStopping heartbeat...");
                        cts.Cancel();
                        break;
                    }
                }
                await Task.Delay(100);
            }

            await heartbeatTask;
        }

        var finalResult = session.UpdateHeartbeat();
        Console.WriteLine("\nFinal Usage Stats:");
        DisplayStats(finalResult.Stats);

        Console.WriteLine("\nPress any key to exit...");
        Console.ReadKey();
    }

    static async Task RunHeartbeat(UsageSession session, CancellationToken cancellationToken)
    {
        int heartbeatCount = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(HEARTBEAT_INTERVAL_SECONDS * 1000, cancellationToken);

                heartbeatCount++;
                Console.WriteLine($"\n--- Heartbeat #{heartbeatCount} at {DateTime.Now:HH:mm:ss} ---");

                var result = session.UpdateHeartbeat();

                if (!result.Success)
                {
                    Console.WriteLine($"❌ {result.Message}");

                    Console.WriteLine("\nPress any key to exit...");
                    Console.ReadKey();
                    Environment.Exit(result.ClockManipulationDetected ? 1 : 0);
                }
                else
                {
                    Console.WriteLine("✓ Heartbeat successful");
                    DisplayStats(result.Stats);
                }
            }
        }
        catch (TaskCanceledException)
        {
            Console.WriteLine("Heartbeat stopped gracefully.");
        }
    }

    static void DisplayStats(UsageStats? stats)
    {
        if (stats == null)
        {
            Console.WriteLine("❌ Unable to retrieve statistics");
            return;
        }

        if (stats.TimeManipulationDetected)
        {
            Console.WriteLine("⚠️  WARNING: Time manipulation detected!");
        }

        if (stats.IsCurrentlyOnline)
        {
            Console.WriteLine("📶 Status: Online (No time limit)");
        }
        else
        {
            Console.WriteLine($"📊 Offline Mode:");
            Console.WriteLine($"   • Remaining: {stats.RemainingOfflineHours:F4} hours");
            Console.WriteLine($"   • Used: {stats.TotalOfflineHours:F4} hours");
            Console.WriteLine($"   • Session: {stats.CurrentSessionDuration.TotalHours:F4} hours");
            if (stats.RemainingOfflineHours < UsageTracker.MAX_OFFLINE_HOURS * 0.10)
                Console.WriteLine($"   🚨 CRITICAL: Less than {UsageTracker.MAX_OFFLINE_HOURS * 0.10:F1} hours left!");
            else if (stats.RemainingOfflineHours < UsageTracker.MAX_OFFLINE_HOURS * 0.50)
                Console.WriteLine($"   ⚠️  Low: Less than {UsageTracker.MAX_OFFLINE_HOURS * 0.50:F1} hours remaining");
        }
    }


}
